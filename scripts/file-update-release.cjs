#!/usr/bin/env node
'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const {
  MAX_MANIFEST_BYTES,
  normalizedPathKey,
  scanRoot,
  strictJsonParse,
  validateManifest,
  verifyManifestSignature
} = require('./file-update-planner.cjs');

const SIGNATURE_DOMAIN = Buffer.from('VeyonCampus.FileManifest.v1\0', 'utf8');
const MAX_KEY_BYTES = 64 * 1024;
const MAX_SIGNATURE_BYTES = 64 * 1024;
const HASH_PATTERN = /^[a-f0-9]{64}$/;
const BUFFER_SIZE = 1024 * 1024;

function fail(message) {
  throw new Error(message);
}

function parseArguments(argv) {
  if (argv.length === 0 || argv.includes('--help') || argv.includes('-h')) return { command: 'help' };
  const command = argv[0];
  if (command !== 'publish' && command !== 'verify') fail("Use 'publish' or 'verify'.");
  const allowed = command === 'publish'
    ? new Set(['manifest', 'private-key', 'public-key', 'object-store', 'release-directory', 'root'])
    : new Set(['release-directory', 'public-key', 'object-store']);
  const values = Object.create(null);
  for (let index = 1; index < argv.length; index++) {
    const token = argv[index];
    if (!token.startsWith('--')) fail(`Unexpected argument '${token}'.`);
    const key = token.slice(2);
    if (!allowed.has(key)) fail(`Unknown argument '--${key}'.`);
    const value = argv[++index];
    if (value === undefined || value.startsWith('--')) fail(`Missing value for '--${key}'.`);
    if (key === 'root') {
      (values.root ??= []).push(value);
      continue;
    }
    if (Object.hasOwn(values, key)) fail(`Duplicate argument '--${key}'.`);
    values[key] = value;
  }
  const required = command === 'publish'
    ? ['manifest', 'private-key', 'public-key', 'object-store', 'release-directory']
    : ['release-directory', 'public-key', 'object-store'];
  for (const key of required) if (!Object.hasOwn(values, key)) fail(`Missing required '--${key}'.`);
  if (command === 'publish' && (!Array.isArray(values.root) || values.root.length === 0))
    fail("'publish' requires one '--root ROOT_ID=PATH' for every manifest root.");
  if (command === 'verify' && Object.hasOwn(values, 'root')) fail("'verify' does not accept '--root'.");
  return { command, values };
}

function readRegularFile(filePath, label, maximumBytes) {
  const resolved = path.resolve(filePath);
  let before;
  try { before = fs.lstatSync(resolved); }
  catch { fail(`${label} is missing or unreadable.`); }
  if (!before.isFile() || before.isSymbolicLink() || before.nlink > 1 || before.size < 1 || before.size > maximumBytes)
    fail(`${label} must be a regular, unlinked file no larger than ${maximumBytes} bytes.`);
  let bytes;
  try { bytes = fs.readFileSync(resolved); }
  catch { fail(`${label} could not be read.`); }
  const after = fs.lstatSync(resolved);
  if (!sameFileIdentity(before, after) || bytes.length !== before.size)
    fail(`${label} changed while it was being read.`);
  return bytes;
}

function sameFileIdentity(left, right) {
  return left.isFile() && right.isFile() && left.size === right.size && left.dev === right.dev &&
    left.ino === right.ino && left.mtimeMs === right.mtimeMs && left.nlink === right.nlink;
}

function parseRoots(values) {
  const roots = new Map();
  for (const value of values) {
    const separator = value.indexOf('=');
    if (separator <= 0 || separator === value.length - 1) fail("Invalid '--root'; expected ROOT_ID=PATH.");
    const id = value.slice(0, separator);
    if (roots.has(id)) fail(`Duplicate root mapping '${id}'.`);
    roots.set(id, path.resolve(value.slice(separator + 1)));
  }
  return roots;
}

function parseCanonicalManifest(bytes, requireCurrent = true) {
  const manifest = strictJsonParse(bytes, 'file update manifest', MAX_MANIFEST_BYTES);
  validateManifest(manifest, 'file update manifest');
  const canonicalBytes = Buffer.from(JSON.stringify(manifest, null, 2) + '\n', 'utf8');
  if (!Buffer.from(bytes).equals(canonicalBytes))
    fail('Manifest must use deterministic UTF-8 JSON with no BOM and one final LF.');
  const now = Date.now();
  if (Date.parse(manifest.createdUtc) > now + 5 * 60 * 1000 ||
      (requireCurrent && Date.parse(manifest.expiresUtc) <= now))
    fail('Manifest is expired or not yet valid.');
  return manifest;
}

function createSignature(manifestBytes, privateKeyBytes, publicKeyBytes) {
  if (Buffer.from(publicKeyBytes).toString('utf8').match(/PRIVATE KEY/i))
    fail('A public key file must not contain private key material.');
  let privateKey;
  let publicKey;
  try {
    privateKey = crypto.createPrivateKey(privateKeyBytes);
    publicKey = crypto.createPublicKey(publicKeyBytes);
  } catch (error) {
    fail(`Could not load the manifest signing key pair: ${error.message}`);
  }
  const details = privateKey.asymmetricKeyDetails;
  if (privateKey.asymmetricKeyType !== 'rsa' || publicKey.asymmetricKeyType !== 'rsa' ||
      (details?.modulusLength ?? 0) < 3072 || (details?.modulusLength ?? 0) > 4096 ||
      (publicKey.asymmetricKeyDetails?.modulusLength ?? 0) !== details.modulusLength)
    fail('Manifest signing requires a matching RSA key pair from 3072 to 4096 bits.');
  const privatePublicDer = crypto.createPublicKey(privateKey).export({ type: 'spki', format: 'der' });
  const providedPublicDer = publicKey.export({ type: 'spki', format: 'der' });
  if (!Buffer.from(privatePublicDer).equals(Buffer.from(providedPublicDer)))
    fail('The private key does not match the supplied public key.');
  const keyId = crypto.createHash('sha256').update(providedPublicDer).digest('hex');
  const signature = crypto.sign('sha256', Buffer.concat([SIGNATURE_DOMAIN, manifestBytes]), {
    key: privateKey,
    padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
    saltLength: 32
  });
  const envelope = {
    schemaVersion: 1,
    algorithm: 'RSA-PSS-SHA256',
    keyId,
    signature: signature.toString('base64')
  };
  const signatureBytes = Buffer.from(JSON.stringify(envelope, null, 2) + '\n', 'utf8');
  verifyManifestSignature(manifestBytes, signatureBytes, publicKeyBytes);
  return { bytes: signatureBytes, keyId };
}

function ensureDirectory(directory, label) {
  const resolved = path.resolve(directory);
  fs.mkdirSync(resolved, { recursive: true });
  const stat = fs.lstatSync(resolved);
  if (!stat.isDirectory() || stat.isSymbolicLink()) fail(`${label} must be a real directory, not a link.`);
  return fs.realpathSync(resolved);
}

function requireDirectory(directory, label) {
  const resolved = path.resolve(directory);
  let stat;
  try { stat = fs.lstatSync(resolved); }
  catch { fail(`${label} is missing.`); }
  if (!stat.isDirectory() || stat.isSymbolicLink()) fail(`${label} must be a real directory, not a link.`);
  return fs.realpathSync(resolved);
}

function assertReleaseDirectoryContents(directory) {
  const entries = fs.readdirSync(directory).sort();
  if (entries.length !== 2 || entries[0] !== 'manifest.json' || entries[1] !== 'manifest.sig')
    fail('Release directory must contain only manifest.json and manifest.sig.');
  for (const name of entries) {
    const stat = fs.lstatSync(path.join(directory, name));
    if (!stat.isFile() || stat.isSymbolicLink() || stat.nlink > 1)
      fail(`Release file '${name}' must be a regular unlinked file.`);
  }
}

function verifyExistingReleaseDirectory(releaseDirectory, manifestBytes, publicKeyBytes) {
  let stat;
  try { stat = fs.lstatSync(releaseDirectory); }
  catch (error) { if (error.code === 'ENOENT') return false; throw error; }
  if (!stat.isDirectory() || stat.isSymbolicLink()) fail('Release directory must be a real directory.');
  assertReleaseDirectoryContents(releaseDirectory);
  const existingManifest = readRegularFile(path.join(releaseDirectory, 'manifest.json'), 'Existing release manifest', MAX_MANIFEST_BYTES);
  const existingSignature = readRegularFile(path.join(releaseDirectory, 'manifest.sig'), 'Existing release signature', MAX_SIGNATURE_BYTES);
  if (!existingManifest.equals(manifestBytes)) fail('Release directory already contains different manifest bytes.');
  verifyManifestSignature(existingManifest, existingSignature, publicKeyBytes);
  return true;
}

function isWithinOrSame(parent, candidate) {
  const relative = path.relative(parent, candidate);
  return relative === '' || (relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative));
}

function canonicalizePotentialPath(value) {
  let current = path.resolve(value);
  const missing = [];
  while (true) {
    try {
      fs.lstatSync(current);
      break;
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
      const parent = path.dirname(current);
      if (parent === current) throw error;
      missing.unshift(path.basename(current));
      current = parent;
    }
  }
  return path.join(fs.realpathSync(current), ...missing);
}

function assertNoOverlap(leftPath, rightPath, label) {
  const left = canonicalizePotentialPath(leftPath);
  const right = canonicalizePotentialPath(rightPath);
  if (isWithinOrSame(left, right) || isWithinOrSame(right, left)) fail(`${label} paths must not overlap.`);
}

function expectedFiles(manifest) {
  return manifest.components.flatMap((component) => component.files.map((file) => ({
    rootId: component.rootId,
    path: file.path,
    sizeBytes: file.sizeBytes,
    sha256: file.sha256
  })));
}

function validateUniqueObjectSizes(files) {
  const sizes = new Map();
  for (const file of files) {
    const existing = sizes.get(file.sha256);
    if (existing !== undefined && existing !== file.sizeBytes)
      fail(`Manifest uses one object digest with conflicting sizes (${file.sha256}).`);
    sizes.set(file.sha256, file.sizeBytes);
  }
}

function rejectSensitivePackagePaths(files) {
  for (const file of files) {
    const normalized = file.path.replaceAll('\\', '/').toLowerCase();
    const segments = normalized.split('/');
    const fileName = segments.at(-1) ?? '';
    if (/\.(?:pfx|p12|key|secret|credentials?)$/.test(fileName) ||
        segments.some((segment) => /(?:private[-_]?key|password|credentials?|secrets?)/.test(segment)))
      fail(`Manifest contains a path that must not be published: '${file.rootId}/${file.path}'.`);
  }
}

function validatePayloadRoots(manifest, rootMappings) {
  const expectedRootIds = manifest.roots.map((root) => root.id).sort();
  if (rootMappings.size !== expectedRootIds.length || expectedRootIds.some((id) => !rootMappings.has(id)))
    fail(`Payload roots must exactly match the manifest roots: ${expectedRootIds.join(', ')}.`);
  const scans = new Map();
  for (const rootId of expectedRootIds) {
    const scan = scanRoot(rootId, rootMappings.get(rootId));
    const expected = expectedFiles(manifest).filter((file) => file.rootId === rootId);
    const expectedByKey = new Map(expected.map((file) => [normalizedPathKey(rootId, file.path), file]));
    const actualByKey = new Map(scan.files.map((file) => [normalizedPathKey(rootId, file.path), file]));
    if (expectedByKey.size !== actualByKey.size) fail(`Payload root '${rootId}' does not match its complete manifest file set.`);
    for (const [key, file] of expectedByKey) {
      const actual = actualByKey.get(key);
      if (!actual || actual.path !== file.path || actual.sizeBytes !== file.sizeBytes || actual.sha256 !== file.sha256)
        fail(`Payload file '${rootId}/${file.path}' does not match the manifest size or SHA-256.`);
    }
    scans.set(rootId, scan);
  }
  return scans;
}

function ensureObjectDirectory(objectStore, digest) {
  const shaDirectory = path.join(objectStore, 'sha256');
  const shardPath = path.join(shaDirectory, digest.slice(0, 2));
  for (const directory of [shaDirectory, shardPath]) {
    fs.mkdirSync(directory, { recursive: true });
    const stat = fs.lstatSync(directory);
    if (!stat.isDirectory() || stat.isSymbolicLink()) fail('Content-addressed object directories must not contain links.');
  }
  return path.join(shardPath, digest);
}

function hashRegularFile(filePath, expectedSize, expectedHash, label) {
  let before;
  try { before = fs.lstatSync(filePath); }
  catch { fail(`${label} is missing.`); }
  if (!before.isFile() || before.isSymbolicLink() || before.nlink > 1 || before.size !== expectedSize)
    fail(`${label} is not a regular object with the expected size.`);
  const fd = fs.openSync(filePath, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
  try {
    const opened = fs.fstatSync(fd);
    if (!sameFileIdentity(before, opened)) fail(`${label} changed while it was opened.`);
    const hash = crypto.createHash('sha256');
    const buffer = Buffer.allocUnsafe(BUFFER_SIZE);
    let size = 0;
    while (true) {
      const read = fs.readSync(fd, buffer, 0, buffer.length, null);
      if (read === 0) break;
      size += read;
      hash.update(buffer.subarray(0, read));
    }
    const digest = hash.digest('hex');
    if (size !== expectedSize || digest !== expectedHash || !sameFileIdentity(opened, fs.fstatSync(fd)))
      fail(`${label} failed its size or SHA-256 check.`);
    return { sizeBytes: size, sha256: digest };
  } finally {
    fs.closeSync(fd);
  }
}

function verifyStoredObject(objectStore, file) {
  for (const directory of [
    path.join(objectStore, 'sha256'),
    path.join(objectStore, 'sha256', file.sha256.slice(0, 2))
  ]) {
    let stat;
    try { stat = fs.lstatSync(directory); }
    catch { fail(`Object shard for ${file.sha256} is missing.`); }
    if (!stat.isDirectory() || stat.isSymbolicLink()) fail(`Object shard for ${file.sha256} is not a real directory.`);
  }
  const objectPath = path.join(objectStore, 'sha256', file.sha256.slice(0, 2), file.sha256);
  hashRegularFile(objectPath, file.sizeBytes, file.sha256, `Object ${file.sha256}`);
  return objectPath;
}

function storeObject(objectStore, rootPath, file) {
  const objectPath = ensureObjectDirectory(objectStore, file.sha256);
  if (fs.existsSync(objectPath)) {
    verifyStoredObject(objectStore, file);
    return false;
  }
  const sourcePath = path.resolve(rootPath, ...file.path.split('/'));
  const relative = path.relative(rootPath, sourcePath);
  if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative))
    fail(`Payload path '${file.path}' escapes its fixed root.`);
  const sourceStat = fs.lstatSync(sourcePath);
  if (!sourceStat.isFile() || sourceStat.isSymbolicLink() || sourceStat.nlink > 1 || sourceStat.size !== file.sizeBytes)
    fail(`Payload file '${file.rootId}/${file.path}' changed after inventory.`);
  const inputFd = fs.openSync(sourcePath, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
  const opened = fs.fstatSync(inputFd);
  if (!sameFileIdentity(sourceStat, opened)) {
    fs.closeSync(inputFd);
    fail(`Payload file '${file.rootId}/${file.path}' changed while opening.`);
  }
  const temporaryPath = `${objectPath}.tmp-${crypto.randomUUID()}`;
  let outputFd;
  try {
    outputFd = fs.openSync(temporaryPath, fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL, 0o600);
    const hash = crypto.createHash('sha256');
    const buffer = Buffer.allocUnsafe(BUFFER_SIZE);
    let size = 0;
    while (true) {
      const read = fs.readSync(inputFd, buffer, 0, buffer.length, null);
      if (read === 0) break;
      let written = 0;
      while (written < read) {
        const count = fs.writeSync(outputFd, buffer, written, read - written);
        if (count <= 0) fail('Could not write the complete content-addressed object.');
        written += count;
      }
      size += read;
      hash.update(buffer.subarray(0, read));
    }
    const digest = hash.digest('hex');
    if (size !== file.sizeBytes || digest !== file.sha256 || !sameFileIdentity(opened, fs.fstatSync(inputFd)))
      fail(`Payload file '${file.rootId}/${file.path}' changed or failed SHA-256 verification while publishing.`);
    fs.fsyncSync(outputFd);
    fs.closeSync(outputFd);
    outputFd = undefined;
    fs.closeSync(inputFd);
    try {
      fs.linkSync(temporaryPath, objectPath);
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      verifyStoredObject(objectStore, file);
      fs.unlinkSync(temporaryPath);
      return false;
    }
    fs.unlinkSync(temporaryPath);
    verifyStoredObject(objectStore, file);
    return true;
  } catch (error) {
    if (outputFd !== undefined) fs.closeSync(outputFd);
    try { fs.closeSync(inputFd); } catch { /* already closed */ }
    try { fs.unlinkSync(temporaryPath); } catch { /* preserve the original failure */ }
    throw error;
  }
}

function writeReleaseDirectory(releaseDirectory, manifestBytes, signatureBytes, publicKeyBytes) {
  const resolved = path.resolve(releaseDirectory);
  if (verifyExistingReleaseDirectory(resolved, manifestBytes, publicKeyBytes)) return false;
  const parent = path.dirname(resolved);
  fs.mkdirSync(parent, { recursive: true });
  const parentStat = fs.lstatSync(parent);
  if (!parentStat.isDirectory() || parentStat.isSymbolicLink()) fail('Release directory parent must be a real directory.');
  const staging = path.join(parent, `.${path.basename(resolved)}.tmp-${crypto.randomUUID()}`);
  fs.mkdirSync(staging, { mode: 0o700 });
  try {
    fs.writeFileSync(path.join(staging, 'manifest.json'), manifestBytes, { flag: 'wx', mode: 0o600 });
    fs.writeFileSync(path.join(staging, 'manifest.sig'), signatureBytes, { flag: 'wx', mode: 0o600 });
    for (const fileName of ['manifest.json', 'manifest.sig']) {
      const fd = fs.openSync(path.join(staging, fileName), fs.constants.O_RDWR);
      try { fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
    }
    fs.renameSync(staging, resolved);
    return true;
  } catch (error) {
    try { fs.rmSync(staging, { recursive: true, force: true }); } catch { /* preserve original failure */ }
    throw error;
  }
}

function publicationSummary(manifest, manifestBytes, keyId, files, createdObjects, releaseCreated) {
  const unique = new Map(files.map((file) => [file.sha256, file.sizeBytes]));
  return {
    role: manifest.role,
    version: manifest.version,
    channel: manifest.channel,
    releaseSequence: manifest.releaseSequence,
    manifestSha256: crypto.createHash('sha256').update(manifestBytes).digest('hex'),
    keyId,
    fileCount: files.length,
    totalFileBytes: files.reduce((total, file) => total + file.sizeBytes, 0),
    uniqueObjectCount: unique.size,
    uniqueObjectBytes: [...unique.values()].reduce((total, size) => total + size, 0),
    objectsCreated: createdObjects,
    releaseDirectoryCreated: releaseCreated,
    localArtifactsWritten: true,
    installedMachineModified: false,
    productionPublished: false,
    deployable: false
  };
}

function publishRelease(values) {
  const manifestBytes = readRegularFile(values.manifest, 'Manifest', MAX_MANIFEST_BYTES);
  const manifest = parseCanonicalManifest(manifestBytes);
  const roots = parseRoots(values.root);
  const releaseDirectory = path.resolve(values['release-directory']);
  const objectStorePath = path.resolve(values['object-store']);
  for (const rootPath of roots.values()) {
    assertNoOverlap(rootPath, releaseDirectory, 'Payload and release directory');
    assertNoOverlap(rootPath, objectStorePath, 'Payload and object store');
  }
  assertNoOverlap(releaseDirectory, objectStorePath, 'Release directory and object store');
  validatePayloadRoots(manifest, roots);
  const publicKeyBytes = readRegularFile(values['public-key'], 'Developer public key', MAX_KEY_BYTES);
  const privateKeyBytes = readRegularFile(values['private-key'], 'Developer private key', MAX_KEY_BYTES);
  const signature = createSignature(manifestBytes, privateKeyBytes, publicKeyBytes);
  const files = expectedFiles(manifest);
  validateUniqueObjectSizes(files);
  rejectSensitivePackagePaths(files);
  verifyExistingReleaseDirectory(releaseDirectory, manifestBytes, publicKeyBytes);
  const objectStore = ensureDirectory(objectStorePath, 'Object store');
  const createdHashes = new Set();
  let objectsCreated = 0;
  for (const file of files) {
    if (createdHashes.has(file.sha256)) continue;
    const rootPath = roots.get(file.rootId);
    if (storeObject(objectStore, rootPath, file)) objectsCreated++;
    createdHashes.add(file.sha256);
    verifyStoredObject(objectStore, file);
  }
  const releaseCreated = writeReleaseDirectory(releaseDirectory, manifestBytes, signature.bytes, publicKeyBytes);
  return publicationSummary(manifest, manifestBytes, signature.keyId, files, objectsCreated, releaseCreated);
}

function verifyRelease(values) {
  const releaseDirectory = path.resolve(values['release-directory']);
  const stat = fs.lstatSync(releaseDirectory);
  if (!stat.isDirectory() || stat.isSymbolicLink()) fail('Release directory must be a real directory.');
  assertReleaseDirectoryContents(releaseDirectory);
  const manifestBytes = readRegularFile(path.join(releaseDirectory, 'manifest.json'), 'Manifest', MAX_MANIFEST_BYTES);
  const signatureBytes = readRegularFile(path.join(releaseDirectory, 'manifest.sig'), 'Detached signature', MAX_SIGNATURE_BYTES);
  const manifest = parseCanonicalManifest(manifestBytes, false);
  const publicKeyBytes = readRegularFile(values['public-key'], 'Developer public key', MAX_KEY_BYTES);
  const trust = verifyManifestSignature(manifestBytes, signatureBytes, publicKeyBytes);
  const objectStore = requireDirectory(values['object-store'], 'Object store');
  const files = expectedFiles(manifest);
  validateUniqueObjectSizes(files);
  rejectSensitivePackagePaths(files);
  const checked = new Set();
  let totalObjectBytes = 0;
  for (const file of files) {
    if (checked.has(file.sha256)) continue;
    verifyStoredObject(objectStore, file);
    checked.add(file.sha256);
    totalObjectBytes += file.sizeBytes;
  }
  return {
    role: manifest.role,
    version: manifest.version,
    channel: manifest.channel,
    releaseSequence: manifest.releaseSequence,
    manifestSha256: crypto.createHash('sha256').update(manifestBytes).digest('hex'),
    keyId: trust.keyId,
    fileCount: files.length,
    totalFileBytes: files.reduce((total, file) => total + file.sizeBytes, 0),
    verifiedObjectCount: checked.size,
    verifiedObjectBytes: totalObjectBytes,
    expired: Date.parse(manifest.expiresUtc) <= Date.now(),
    signatureVerified: true,
    objectsVerified: true,
    readOnly: true,
    installedMachineModified: false,
    productionPublished: false,
    deployable: false
  };
}

function formatHelp() {
  return [
    'Veyon Campus file update release packager (local artifact only)',
    '',
    'Publish a signed manifest and verified SHA-256 objects:',
    '  node scripts/file-update-release.cjs publish --manifest FILE --private-key PEM --public-key PEM',
    '    --object-store DIR --release-directory DIR --root ROOT_ID=DIR [--root ROOT_ID=DIR ...]',
    '',
    'Verify the detached signature and every content object:',
    '  node scripts/file-update-release.cjs verify --release-directory DIR --public-key PEM --object-store DIR',
    '',
    'This tool does not upload a release index or modify an installed machine.'
  ].join('\n');
}

function main(argv) {
  const parsed = parseArguments(argv);
  if (parsed.command === 'help') {
    process.stdout.write(formatHelp() + '\n');
    return 0;
  }
  const summary = parsed.command === 'publish' ? publishRelease(parsed.values) : verifyRelease(parsed.values);
  process.stdout.write(JSON.stringify(summary, null, 2) + '\n');
  return 0;
}

if (require.main === module) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    process.stderr.write(`file-update-release: ${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = { createSignature, parseCanonicalManifest, publishRelease, verifyRelease };

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
const MAX_COMPONENT_PACK_BYTES = 1_900_000_000;
const MAX_ZIP_ENTRIES = 65534;

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

function componentPackFileName(role, version, componentId) {
  return `VeyonCampus-${role}-${version}-component-${componentId}.zip`;
}

function expectedComponentPacks(manifest) {
  return manifest.components.map((component) => {
    const objects = new Map();
    for (const file of component.files) {
      const existingSize = objects.get(file.sha256);
      if (existingSize !== undefined && existingSize !== file.sizeBytes)
        fail(`Component '${component.id}' uses one object digest with conflicting sizes.`);
      objects.set(file.sha256, file.sizeBytes);
    }
    return {
      componentId: component.id,
      fileName: componentPackFileName(manifest.role, manifest.version, component.id),
      objects: [...objects].map(([sha256, sizeBytes]) => ({ sha256, sizeBytes }))
        .sort((left, right) => left.sha256 < right.sha256 ? -1 : left.sha256 > right.sha256 ? 1 : 0)
    };
  }).filter((pack) => pack.objects.length > 0);
}

function assertReleaseDirectoryContents(directory, manifest) {
  const entries = fs.readdirSync(directory).sort();
  if (entries.length !== 3 || entries[0] !== 'component-packs' ||
      entries[1] !== 'manifest.json' || entries[2] !== 'manifest.sig')
    fail('Release directory must contain only component-packs, manifest.json, and manifest.sig.');
  for (const name of ['manifest.json', 'manifest.sig']) {
    const stat = fs.lstatSync(path.join(directory, name));
    if (!stat.isFile() || stat.isSymbolicLink() || stat.nlink > 1)
      fail(`Release file '${name}' must be a regular unlinked file.`);
  }
  const packDirectory = path.join(directory, 'component-packs');
  const packDirectoryStat = fs.lstatSync(packDirectory);
  if (!packDirectoryStat.isDirectory() || packDirectoryStat.isSymbolicLink())
    fail('Component pack path must be a real directory.');
  const actualPacks = fs.readdirSync(packDirectory).sort();
  const expectedPacks = expectedComponentPacks(manifest).map((pack) => pack.fileName).sort();
  if (actualPacks.length !== expectedPacks.length || actualPacks.some((name, index) => name !== expectedPacks[index]))
    fail('Component pack files do not match the signed manifest components.');
  for (const name of actualPacks) {
    const stat = fs.lstatSync(path.join(packDirectory, name));
    if (!stat.isFile() || stat.isSymbolicLink() || stat.nlink > 1)
      fail(`Component pack '${name}' must be a regular unlinked file.`);
  }
}

function verifyExistingReleaseDirectory(releaseDirectory, manifestBytes, publicKeyBytes, manifest) {
  let stat;
  try { stat = fs.lstatSync(releaseDirectory); }
  catch (error) { if (error.code === 'ENOENT') return false; throw error; }
  if (!stat.isDirectory() || stat.isSymbolicLink()) fail('Release directory must be a real directory.');
  assertReleaseDirectoryContents(releaseDirectory, manifest);
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

const CRC32_TABLE = Uint32Array.from({ length: 256 }, (_, value) => {
  let remainder = value;
  for (let bit = 0; bit < 8; bit++)
    remainder = (remainder & 1) ? (0xedb88320 ^ (remainder >>> 1)) : (remainder >>> 1);
  return remainder >>> 0;
});

function updateCrc32(crc, bytes) {
  let current = crc;
  for (const byte of bytes) current = CRC32_TABLE[(current ^ byte) & 0xff] ^ (current >>> 8);
  return current >>> 0;
}

function expectedFiles(manifest) {
  return manifest.components.flatMap((component) => component.files.map((file) => ({
    componentId: component.id,
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
    let crc32 = 0xffffffff;
    let size = 0;
    while (true) {
      const read = fs.readSync(fd, buffer, 0, buffer.length, null);
      if (read === 0) break;
      size += read;
      hash.update(buffer.subarray(0, read));
      crc32 = updateCrc32(crc32, buffer.subarray(0, read));
    }
    const digest = hash.digest('hex');
    if (size !== expectedSize || digest !== expectedHash || !sameFileIdentity(opened, fs.fstatSync(fd)))
      fail(`${label} failed its size or SHA-256 check.`);
    return { sizeBytes: size, sha256: digest, crc32: (crc32 ^ 0xffffffff) >>> 0 };
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
  const metadata = hashRegularFile(objectPath, file.sizeBytes, file.sha256, `Object ${file.sha256}`);
  return { path: objectPath, ...metadata };
}

function readExactAt(fd, length, position, label) {
  const buffer = Buffer.alloc(length);
  let offset = 0;
  while (offset < length) {
    const read = fs.readSync(fd, buffer, offset, length - offset, position + offset);
    if (read <= 0) fail(`${label} is truncated.`);
    offset += read;
  }
  return buffer;
}

function hashFileRange(fd, start, length, label) {
  const hash = crypto.createHash('sha256');
  const buffer = Buffer.allocUnsafe(BUFFER_SIZE);
  let crc32 = 0xffffffff;
  let sizeBytes = 0;
  while (sizeBytes < length) {
    const read = fs.readSync(fd, buffer, 0, Math.min(buffer.length, length - sizeBytes), start + sizeBytes);
    if (read <= 0) fail(`${label} is truncated.`);
    const bytes = buffer.subarray(0, read);
    hash.update(bytes);
    crc32 = updateCrc32(crc32, bytes);
    sizeBytes += read;
  }
  return { sizeBytes, sha256: hash.digest('hex'), crc32: (crc32 ^ 0xffffffff) >>> 0 };
}

function writeAll(fd, bytes, label) {
  let offset = 0;
  while (offset < bytes.length) {
    const written = fs.writeSync(fd, bytes, offset, bytes.length - offset);
    if (written <= 0) fail(`${label} could not be written completely.`);
    offset += written;
  }
}

function makeZipLocalHeader(entry) {
  const name = Buffer.from(entry.name, 'ascii');
  const header = Buffer.alloc(30 + name.length);
  header.writeUInt32LE(0x04034b50, 0);
  header.writeUInt16LE(20, 4);
  header.writeUInt16LE(0, 6);
  header.writeUInt16LE(0, 8);
  header.writeUInt16LE(0, 10);
  header.writeUInt16LE(0x21, 12);
  header.writeUInt32LE(entry.crc32, 14);
  header.writeUInt32LE(entry.sizeBytes, 18);
  header.writeUInt32LE(entry.sizeBytes, 22);
  header.writeUInt16LE(name.length, 26);
  header.writeUInt16LE(0, 28);
  name.copy(header, 30);
  return header;
}

function makeZipCentralHeader(entry) {
  const name = Buffer.from(entry.name, 'ascii');
  const header = Buffer.alloc(46 + name.length);
  header.writeUInt32LE(0x02014b50, 0);
  header.writeUInt16LE(20, 4);
  header.writeUInt16LE(20, 6);
  header.writeUInt16LE(0, 8);
  header.writeUInt16LE(0, 10);
  header.writeUInt16LE(0, 12);
  header.writeUInt16LE(0x21, 14);
  header.writeUInt32LE(entry.crc32, 16);
  header.writeUInt32LE(entry.sizeBytes, 20);
  header.writeUInt32LE(entry.sizeBytes, 24);
  header.writeUInt16LE(name.length, 28);
  header.writeUInt16LE(0, 30);
  header.writeUInt16LE(0, 32);
  header.writeUInt16LE(0, 34);
  header.writeUInt16LE(0, 36);
  header.writeUInt32LE(0, 38);
  header.writeUInt32LE(entry.localOffset, 42);
  name.copy(header, 46);
  return header;
}

function copyVerifiedObjectToZip(outputFd, object, expected) {
  let before;
  try { before = fs.lstatSync(object.path); }
  catch { fail(`Component object ${expected.sha256} is missing.`); }
  if (!before.isFile() || before.isSymbolicLink() || before.nlink > 1 || before.size !== expected.sizeBytes)
    fail(`Component object ${expected.sha256} has an invalid file shape.`);
  const inputFd = fs.openSync(object.path, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
  try {
    const opened = fs.fstatSync(inputFd);
    if (!sameFileIdentity(before, opened)) fail(`Component object ${expected.sha256} changed while opening.`);
    const hash = crypto.createHash('sha256');
    const buffer = Buffer.allocUnsafe(BUFFER_SIZE);
    let crc32 = 0xffffffff;
    let sizeBytes = 0;
    while (true) {
      const read = fs.readSync(inputFd, buffer, 0, buffer.length, null);
      if (read === 0) break;
      const bytes = buffer.subarray(0, read);
      writeAll(outputFd, bytes, `Component object ${expected.sha256}`);
      hash.update(bytes);
      crc32 = updateCrc32(crc32, bytes);
      sizeBytes += read;
    }
    const metadata = { sizeBytes, sha256: hash.digest('hex'), crc32: (crc32 ^ 0xffffffff) >>> 0 };
    if (sizeBytes !== expected.sizeBytes || metadata.sha256 !== expected.sha256 ||
        metadata.crc32 !== expected.crc32 || !sameFileIdentity(opened, fs.fstatSync(inputFd)))
      fail(`Component object ${expected.sha256} changed or failed integrity validation while packaging.`);
  } finally {
    fs.closeSync(inputFd);
  }
}

function writeComponentPack(packPath, expectedObjects, objectStore) {
  if (expectedObjects.length < 1 || expectedObjects.length > MAX_ZIP_ENTRIES)
    fail('Component pack object count is outside the ZIP v1 limit.');
  const objects = expectedObjects.map((expected) => ({
    ...expected,
    ...verifyStoredObject(objectStore, expected),
    name: `sha256-${expected.sha256}.blob`
  }));
  let estimatedBytes = 22;
  for (const object of objects) estimatedBytes += 30 + object.name.length + object.sizeBytes + 46 + object.name.length;
  if (!Number.isSafeInteger(estimatedBytes) || estimatedBytes > MAX_COMPONENT_PACK_BYTES)
    fail('Component pack exceeds the safe external release asset size; split packs are not enabled yet.');

  const outputFd = fs.openSync(packPath, fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL, 0o600);
  const centralEntries = [];
  let offset = 0;
  try {
    for (const object of objects) {
      const entry = { name: object.name, sizeBytes: object.sizeBytes, crc32: object.crc32, localOffset: offset };
      const header = makeZipLocalHeader(entry);
      writeAll(outputFd, header, 'Component ZIP header');
      offset += header.length;
      copyVerifiedObjectToZip(outputFd, object, object);
      offset += object.sizeBytes;
      centralEntries.push(entry);
    }
    const centralOffset = offset;
    let centralSize = 0;
    for (const entry of centralEntries) {
      const header = makeZipCentralHeader(entry);
      writeAll(outputFd, header, 'Component ZIP directory');
      offset += header.length;
      centralSize += header.length;
    }
    if (centralEntries.length > 0xffff || centralOffset > 0xffffffff || centralSize > 0xffffffff)
      fail('Component ZIP needs unsupported ZIP64 fields.');
    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0);
    end.writeUInt16LE(0, 4);
    end.writeUInt16LE(0, 6);
    end.writeUInt16LE(centralEntries.length, 8);
    end.writeUInt16LE(centralEntries.length, 10);
    end.writeUInt32LE(centralSize, 12);
    end.writeUInt32LE(centralOffset, 16);
    end.writeUInt16LE(0, 20);
    writeAll(outputFd, end, 'Component ZIP end record');
    offset += end.length;
    if (offset > MAX_COMPONENT_PACK_BYTES) fail('Component pack exceeds the safe external release asset size.');
    fs.fsyncSync(outputFd);
  } catch (error) {
    fs.closeSync(outputFd);
    try { fs.unlinkSync(packPath); } catch { /* preserve original failure */ }
    throw error;
  }
  fs.closeSync(outputFd);
  return offset;
}

function verifyComponentPack(packPath, expectedObjects, label) {
  let before;
  try { before = fs.lstatSync(packPath); }
  catch { fail(`${label} is missing.`); }
  if (!before.isFile() || before.isSymbolicLink() || before.nlink > 1 ||
      before.size < 22 || before.size > MAX_COMPONENT_PACK_BYTES)
    fail(`${label} is not a regular ZIP within the configured asset size limit.`);
  const fd = fs.openSync(packPath, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
  try {
    const opened = fs.fstatSync(fd);
    if (!sameFileIdentity(before, opened)) fail(`${label} changed while opening.`);
    const end = readExactAt(fd, 22, opened.size - 22, label);
    if (end.readUInt32LE(0) !== 0x06054b50 || end.readUInt16LE(4) !== 0 || end.readUInt16LE(6) !== 0 ||
        end.readUInt16LE(20) !== 0)
      fail(`${label} has an unsupported ZIP end record.`);
    const entryCount = end.readUInt16LE(10);
    const entriesOnDisk = end.readUInt16LE(8);
    const centralSize = end.readUInt32LE(12);
    const centralOffset = end.readUInt32LE(16);
    if (entryCount !== expectedObjects.length || entriesOnDisk !== entryCount ||
        centralOffset + centralSize !== opened.size - 22)
      fail(`${label} has an invalid ZIP directory or object count.`);

    let centralPosition = centralOffset;
    let localOffset = 0;
    const expectedByName = new Map(expectedObjects.map((object) => [
      `sha256-${object.sha256}.blob`, object
    ]));
    const seen = new Set();
    for (let index = 0; index < entryCount; index++) {
      const central = readExactAt(fd, 46, centralPosition, label);
      if (central.readUInt32LE(0) !== 0x02014b50 || central.readUInt16LE(4) !== 20 ||
          central.readUInt16LE(6) !== 20 || central.readUInt16LE(8) !== 0 ||
          central.readUInt16LE(10) !== 0 || central.readUInt16LE(12) !== 0 ||
          central.readUInt16LE(14) !== 0x21 || central.readUInt16LE(30) !== 0 ||
          central.readUInt16LE(32) !== 0 || central.readUInt16LE(34) !== 0 ||
          central.readUInt16LE(36) !== 0 || central.readUInt32LE(38) !== 0)
        fail(`${label} contains an unsupported ZIP entry.`);
      const nameLength = central.readUInt16LE(28);
      const extraLength = central.readUInt16LE(30);
      const commentLength = central.readUInt16LE(32);
      if (nameLength < 1 || extraLength !== 0 || commentLength !== 0)
        fail(`${label} contains a ZIP entry with an unexpected name or extension field.`);
      const nameBytes = readExactAt(fd, nameLength, centralPosition + 46, label);
      if ([...nameBytes].some((byte) => byte > 0x7f)) fail(`${label} contains a non-ASCII object name.`);
      const name = nameBytes.toString('ascii');
      const expected = expectedByName.get(name);
      if (!expected || seen.has(name)) fail(`${label} contains an unknown or duplicate object.`);
      seen.add(name);
      const crc32 = central.readUInt32LE(16);
      const compressedSize = central.readUInt32LE(20);
      const uncompressedSize = central.readUInt32LE(24);
      const entryOffset = central.readUInt32LE(42);
      if (compressedSize !== expected.sizeBytes || uncompressedSize !== expected.sizeBytes ||
          crc32 !== expected.crc32 || entryOffset !== localOffset)
        fail(`${label} object metadata does not match its signed file hashes.`);

      const local = readExactAt(fd, 30, entryOffset, label);
      if (local.readUInt32LE(0) !== 0x04034b50 || local.readUInt16LE(4) !== 20 ||
          local.readUInt16LE(6) !== 0 || local.readUInt16LE(8) !== 0 ||
          local.readUInt16LE(10) !== 0 || local.readUInt16LE(12) !== 0x21 ||
          local.readUInt32LE(14) !== crc32 || local.readUInt32LE(18) !== compressedSize ||
          local.readUInt32LE(22) !== uncompressedSize || local.readUInt16LE(26) !== nameLength ||
          local.readUInt16LE(28) !== 0)
        fail(`${label} local ZIP header does not match its directory.`);
      const localName = readExactAt(fd, nameLength, entryOffset + 30, label);
      if (!localName.equals(nameBytes)) fail(`${label} local and central ZIP names differ.`);
      const dataOffset = entryOffset + 30 + nameLength;
      if (dataOffset + compressedSize > centralOffset) fail(`${label} object extends into the ZIP directory.`);
      const objectHash = hashFileRange(fd, dataOffset, uncompressedSize, `${label} object ${name}`);
      if (objectHash.sha256 !== expected.sha256 || objectHash.crc32 !== crc32)
        fail(`${label} object ${name} failed its SHA-256 or CRC check.`);
      localOffset = dataOffset + compressedSize;
      centralPosition += 46 + nameLength;
    }
    if (centralPosition !== centralOffset + centralSize || localOffset !== centralOffset ||
        seen.size !== expectedObjects.length || !sameFileIdentity(opened, fs.fstatSync(fd)))
      fail(`${label} contains gaps, trailing data, or changed while being read.`);
    return { sizeBytes: opened.size, objectCount: seen.size };
  } finally {
    fs.closeSync(fd);
  }
}

function verifyComponentPacks(releaseDirectory, manifest, objectStore) {
  const packDirectory = path.join(releaseDirectory, 'component-packs');
  let totalPackBytes = 0;
  const packs = expectedComponentPacks(manifest);
  for (const pack of packs) {
    const objects = pack.objects.map((object) => {
      const stored = verifyStoredObject(objectStore, object);
      return { ...object, crc32: stored.crc32 };
    });
    const verified = verifyComponentPack(path.join(packDirectory, pack.fileName), objects, pack.fileName);
    if (verified.objectCount !== objects.length) fail(`Component pack '${pack.fileName}' is incomplete.`);
    totalPackBytes += verified.sizeBytes;
  }
  return { componentPackCount: packs.length, componentPackBytes: totalPackBytes };
}

function writeComponentPacks(packDirectory, manifest, objectStore) {
  fs.mkdirSync(packDirectory);
  const stat = fs.lstatSync(packDirectory);
  if (!stat.isDirectory() || stat.isSymbolicLink()) fail('Component pack staging path must be a real directory.');
  const packs = expectedComponentPacks(manifest);
  let totalPackBytes = 0;
  for (const pack of packs) {
    const objects = pack.objects.map((object) => {
      const stored = verifyStoredObject(objectStore, object);
      return { ...object, crc32: stored.crc32 };
    });
    totalPackBytes += writeComponentPack(path.join(packDirectory, pack.fileName), objects, objectStore);
  }
  return { componentPackCount: packs.length, componentPackBytes: totalPackBytes };
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

function writeReleaseDirectory(releaseDirectory, manifestBytes, signatureBytes, publicKeyBytes, manifest, objectStore) {
  const resolved = path.resolve(releaseDirectory);
  if (verifyExistingReleaseDirectory(resolved, manifestBytes, publicKeyBytes, manifest)) {
    return { created: false, ...verifyComponentPacks(resolved, manifest, objectStore) };
  }
  const parent = path.dirname(resolved);
  fs.mkdirSync(parent, { recursive: true });
  const parentStat = fs.lstatSync(parent);
  if (!parentStat.isDirectory() || parentStat.isSymbolicLink()) fail('Release directory parent must be a real directory.');
  const staging = path.join(parent, `.${path.basename(resolved)}.tmp-${crypto.randomUUID()}`);
  fs.mkdirSync(staging, { mode: 0o700 });
  try {
    fs.writeFileSync(path.join(staging, 'manifest.json'), manifestBytes, { flag: 'wx', mode: 0o600 });
    fs.writeFileSync(path.join(staging, 'manifest.sig'), signatureBytes, { flag: 'wx', mode: 0o600 });
    const componentPacks = writeComponentPacks(path.join(staging, 'component-packs'), manifest, objectStore);
    for (const fileName of ['manifest.json', 'manifest.sig']) {
      const fd = fs.openSync(path.join(staging, fileName), fs.constants.O_RDWR);
      try { fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
    }
    assertReleaseDirectoryContents(staging, manifest);
    const verifiedPacks = verifyComponentPacks(staging, manifest, objectStore);
    if (verifiedPacks.componentPackBytes !== componentPacks.componentPackBytes ||
        verifiedPacks.componentPackCount !== componentPacks.componentPackCount)
      fail('Generated component pack verification did not match the package summary.');
    fs.renameSync(staging, resolved);
    return { created: true, ...verifiedPacks };
  } catch (error) {
    try { fs.rmSync(staging, { recursive: true, force: true }); } catch { /* preserve original failure */ }
    throw error;
  }
}

function publicationSummary(manifest, manifestBytes, keyId, files, createdObjects, releaseWrite) {
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
    componentPackCount: releaseWrite.componentPackCount,
    componentPackBytes: releaseWrite.componentPackBytes,
    componentPacksVerified: true,
    releaseDirectoryCreated: releaseWrite.created,
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
  verifyExistingReleaseDirectory(releaseDirectory, manifestBytes, publicKeyBytes, manifest);
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
  const releaseWrite = writeReleaseDirectory(
    releaseDirectory, manifestBytes, signature.bytes, publicKeyBytes, manifest, objectStore
  );
  return publicationSummary(manifest, manifestBytes, signature.keyId, files, objectsCreated, releaseWrite);
}

function verifyRelease(values) {
  const releaseDirectory = path.resolve(values['release-directory']);
  const stat = fs.lstatSync(releaseDirectory);
  if (!stat.isDirectory() || stat.isSymbolicLink()) fail('Release directory must be a real directory.');
  const manifestBytes = readRegularFile(path.join(releaseDirectory, 'manifest.json'), 'Manifest', MAX_MANIFEST_BYTES);
  const manifest = parseCanonicalManifest(manifestBytes, false);
  assertReleaseDirectoryContents(releaseDirectory, manifest);
  const signatureBytes = readRegularFile(path.join(releaseDirectory, 'manifest.sig'), 'Detached signature', MAX_SIGNATURE_BYTES);
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
  const componentPacks = verifyComponentPacks(releaseDirectory, manifest, objectStore);
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
    verifiedComponentPackCount: componentPacks.componentPackCount,
    verifiedComponentPackBytes: componentPacks.componentPackBytes,
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

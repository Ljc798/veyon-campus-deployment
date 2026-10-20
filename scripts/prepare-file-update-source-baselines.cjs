#!/usr/bin/env node
'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const {
  compareSemver,
  MAX_MANIFEST_BYTES,
  normalizedPathKey,
  strictJsonParse,
  validateManifest,
  validateRelativePath,
  verifyManifestSignature
} = require('./file-update-planner.cjs');

const MAX_POLICY_BYTES = 16 * 1024 * 1024;
const MAX_SIGNATURE_BYTES = 64 * 1024;
const MAX_PUBLIC_KEY_BYTES = 64 * 1024;
const HASH_PATTERN = /^[a-f0-9]{64}$/;
const MIGRATION_ID_PATTERN = /^[a-z0-9][a-z0-9._-]{0,95}$/;

function fail(message, cause) {
  throw new Error(message, cause ? { cause } : undefined);
}

function exactKeys(value, expected, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail(`${label} must be a JSON object.`);
  const actual = Object.keys(value).sort();
  const wanted = [...expected].sort();
  if (actual.length !== wanted.length || actual.some((key, index) => key !== wanted[index]))
    fail(`${label} contains missing or unknown fields.`);
}

function sameFileIdentity(left, right) {
  return left.isFile() && right.isFile() && left.size === right.size && left.dev === right.dev &&
    left.ino === right.ino && left.mtimeMs === right.mtimeMs && left.nlink === right.nlink;
}

function safeRelativePath(value, label) {
  if (typeof value !== 'string' || value.length < 1 || value.length > 512 || path.isAbsolute(value) ||
      /^[A-Za-z]:/.test(value) || value.split(/[\\/]/).some((part) => !part || part.length > 255 ||
        part === '.' || part === '..' || /[:<>"|?*\u0000-\u001f\u007f]/.test(part) || /[ .]$/.test(part) ||
        /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$/i.test(part)))
    fail(`${label} must be a safe relative path.`);
  return value.split(/[\\/]/);
}

function readSafeRelativeFile(root, relativePath, maximumBytes, label) {
  const rootPath = fs.realpathSync(root);
  const parts = safeRelativePath(relativePath, label);
  let current = rootPath;
  let before;
  for (let index = 0; index < parts.length; index++) {
    current = path.join(current, parts[index]);
    try { before = fs.lstatSync(current); }
    catch { fail(`${label} is missing or unreadable.`); }
    if (before.isSymbolicLink() || (index < parts.length - 1 && !before.isDirectory()))
      fail(`${label} path contains a link or non-directory parent.`);
    if (index === parts.length - 1 && (!before.isFile() || before.nlink > 1 || before.size < 1 || before.size > maximumBytes))
      fail(`${label} must be a regular unlinked file no larger than ${maximumBytes} bytes.`);
  }
  let bytes;
  try { bytes = fs.readFileSync(current); }
  catch { fail(`${label} could not be read.`); }
  let after;
  try { after = fs.lstatSync(current); }
  catch { fail(`${label} changed while being read.`); }
  if (!sameFileIdentity(before, after) || bytes.length !== before.size)
    fail(`${label} changed while being read.`);
  return { path: current, bytes, sha256: crypto.createHash('sha256').update(bytes).digest('hex') };
}

function readInputFile(filePath, maximumBytes, label) {
  const absolutePath = path.resolve(filePath);
  let before;
  try { before = fs.lstatSync(absolutePath); }
  catch { fail(`${label} is missing or unreadable.`); }
  if (!before.isFile() || before.isSymbolicLink() || before.nlink > 1 || before.size < 1 || before.size > maximumBytes)
    fail(`${label} must be a regular unlinked file no larger than ${maximumBytes} bytes.`);
  const root = fs.realpathSync(path.dirname(absolutePath));
  const fileName = path.basename(absolutePath);
  return readSafeRelativeFile(root, fileName, maximumBytes, label);
}

function readCanonicalManifest(file, label) {
  const manifest = strictJsonParse(file.bytes, label, MAX_MANIFEST_BYTES);
  const validated = validateManifest(manifest, label);
  const canonical = Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`, 'utf8');
  if (!canonical.equals(file.bytes)) fail(`${label} must use deterministic UTF-8 JSON with no BOM and one final LF.`);
  return { manifest, validated, sha256: file.sha256 };
}

function validateBaselinePolicy(policy) {
  exactKeys(policy, ['schemaVersion', 'kind', 'role', 'targetManifestSha256', 'sources'],
    'File update source-baseline policy');
  if (policy.schemaVersion !== 1 || policy.kind !== 'VeyonCampus.FileUpdateSourceBaselines' ||
      !['TeacherConsole', 'StudentSetup'].includes(policy.role) ||
      typeof policy.targetManifestSha256 !== 'string' || !HASH_PATTERN.test(policy.targetManifestSha256) ||
      !Array.isArray(policy.sources) || policy.sources.length < 1 || policy.sources.length > 256)
    fail('File update source-baseline policy is invalid.');
  for (const [index, source] of policy.sources.entries()) {
    exactKeys(source, ['manifestPath', 'signaturePath', 'publicKeyPath', 'requiredMigrations', 'managedDeletes'],
      `Source baseline ${index + 1}`);
    for (const key of ['manifestPath', 'signaturePath', 'publicKeyPath']) safeRelativePath(source[key], `Source baseline ${index + 1} ${key}`);
    if (!Array.isArray(source.requiredMigrations) || source.requiredMigrations.length > 128 ||
        source.requiredMigrations.some((id) => typeof id !== 'string' || !MIGRATION_ID_PATTERN.test(id)) ||
        new Set(source.requiredMigrations).size !== source.requiredMigrations.length)
      fail(`Source baseline ${index + 1} has invalid required migration IDs.`);
    if (!Array.isArray(source.managedDeletes) || source.managedDeletes.length > 50000)
      fail(`Source baseline ${index + 1} managedDeletes must be an array of at most 50,000 entries.`);
    for (const [deleteIndex, deletion] of source.managedDeletes.entries()) {
      exactKeys(deletion, ['rootId', 'path', 'previousSha256'], `Source baseline ${index + 1} delete ${deleteIndex + 1}`);
      if (typeof deletion.rootId !== 'string' || typeof deletion.previousSha256 !== 'string' ||
          !HASH_PATTERN.test(deletion.previousSha256))
        fail(`Source baseline ${index + 1} delete ${deleteIndex + 1} is invalid.`);
      try { validateRelativePath(deletion.path, `Source baseline ${index + 1} delete path`); }
      catch (error) { fail(`Source baseline ${index + 1} delete ${deleteIndex + 1} path is invalid.`, error); }
    }
  }
  return policy;
}

function compareDeleteRules(left, right) {
  const leftKey = `${left.rootId}\0${left.path}`;
  const rightKey = `${right.rootId}\0${right.path}`;
  return leftKey < rightKey ? -1 : leftKey > rightKey ? 1 : 0;
}

function buildDirectSources({ target, targetValidated, targetSha256, policy, policyDirectory }) {
  validateBaselinePolicy(policy);
  if (policy.role !== target.role) fail('Source-baseline policy role does not match the target manifest.');
  if (policy.targetManifestSha256 !== targetSha256)
    fail('Source-baseline policy is bound to different target manifest bytes.');
  if (!Array.isArray(target.directSources) || target.directSources.length !== 0)
    fail('Target manifest must not already contain direct source baselines.');

  const sourceVersions = new Set();
  const sourceDigests = new Set();
  const sourceVersionPrecedences = [];
  const sourceSequences = new Set();
  const migrationCatalog = new Map(target.migrationCatalog.map((migration) => [migration.id, migration]));
  const directSources = [];
  for (const [index, definition] of policy.sources.entries()) {
    const label = `Source baseline ${index + 1}`;
    const sourceManifestFile = readSafeRelativeFile(policyDirectory, definition.manifestPath,
      MAX_MANIFEST_BYTES, `${label} manifest`);
    const sourceSignatureFile = readSafeRelativeFile(policyDirectory, definition.signaturePath,
      MAX_SIGNATURE_BYTES, `${label} signature`);
    const sourcePublicKeyFile = readSafeRelativeFile(policyDirectory, definition.publicKeyPath,
      MAX_PUBLIC_KEY_BYTES, `${label} public key`);
    const source = readCanonicalManifest(sourceManifestFile, `${label} manifest`);
    const trust = verifyManifestSignature(sourceManifestFile.bytes, sourceSignatureFile.bytes, sourcePublicKeyFile.bytes);
    if (source.manifest.role !== target.role || source.manifest.product !== target.product ||
        source.manifest.architecture !== target.architecture || source.manifest.channel !== target.channel ||
        compareSemver(target.version, source.manifest.version) <= 0 ||
        target.releaseSequence <= source.manifest.releaseSequence || !Number.isSafeInteger(source.manifest.releaseSequence) ||
        source.manifest.releaseSequence < 1 || sourceSequences.has(source.manifest.releaseSequence) ||
        sourceVersionPrecedences.some((version) => compareSemver(version, source.manifest.version) === 0))
      fail(`${label} is not an older signed source for this exact target role, product, architecture and channel.`);
    if (sourceVersions.has(source.manifest.version) || sourceDigests.has(source.sha256))
      fail(`${label} duplicates another source version or manifest digest.`);
    sourceVersions.add(source.manifest.version);
    sourceDigests.add(source.sha256);
    sourceSequences.add(source.manifest.releaseSequence);
    sourceVersionPrecedences.push(source.manifest.version);

    const requiredMigrations = [...definition.requiredMigrations].sort();
    for (const migrationId of requiredMigrations) {
      const migration = migrationCatalog.get(migrationId);
      if (!migration || !migration.reversible)
        fail(`${label} requires an unknown or non-reversible migration '${migrationId}'.`);
    }

    const managedDeletes = definition.managedDeletes.map((entry) => ({ ...entry }))
      .sort(compareDeleteRules);
    const deleteKeys = new Set();
    for (const deletion of managedDeletes) {
      const key = normalizedPathKey(deletion.rootId, deletion.path);
      const sourceFile = source.validated.fileMap.get(key);
      if (!sourceFile || sourceFile.sha256 !== deletion.previousSha256)
        fail(`${label} delete authorization does not match the exact signed source file hash: ${deletion.rootId}/${deletion.path}.`);
      if (targetValidated.fileMap.has(key))
        fail(`${label} authorizes deletion of a file that still exists in the target: ${deletion.rootId}/${deletion.path}.`);
      if (deleteKeys.has(key)) fail(`${label} contains a duplicate managed delete authorization.`);
      deleteKeys.add(key);
    }
    directSources.push({
      version: source.manifest.version,
      manifestSha256: source.sha256,
      requiredMigrations,
      managedDeletes
    });
  }
  return directSources.sort((left, right) =>
    compareSemver(left.version, right.version) || (left.version < right.version ? -1 : left.version > right.version ? 1 : 0));
}

function writeNewFile(filePath, bytes) {
  const absolutePath = path.resolve(filePath);
  const parent = fs.realpathSync(path.dirname(absolutePath));
  const target = path.join(parent, path.basename(absolutePath));
  let fd;
  let created = false;
  try {
    fd = fs.openSync(target, fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL, 0o600);
    created = true;
    let offset = 0;
    while (offset < bytes.length) {
      const written = fs.writeSync(fd, bytes, offset, bytes.length - offset);
      if (written < 1) fail('Could not write the complete target manifest.');
      offset += written;
    }
    fs.fsyncSync(fd);
  } catch (error) {
    if (fd !== undefined) {
      try { fs.closeSync(fd); } catch { /* preserve original failure */ }
      fd = undefined;
    }
    if (created) {
      try { fs.unlinkSync(target); } catch { /* preserve original failure */ }
    }
    if (error?.code === 'EEXIST') fail('Output manifest already exists; it will not be overwritten.');
    throw error;
  } finally {
    if (fd !== undefined) fs.closeSync(fd);
  }
  return target;
}

function prepareFileUpdateSourceBaselines({ targetManifestPath, sourceBaselinePath, outputManifestPath }) {
  const targetFile = readInputFile(targetManifestPath, MAX_MANIFEST_BYTES, 'Target manifest');
  const targetRecord = readCanonicalManifest(targetFile, 'Target manifest');
  const policyFile = readInputFile(sourceBaselinePath, MAX_POLICY_BYTES, 'Source-baseline policy');
  const policy = strictJsonParse(policyFile.bytes, 'source-baseline policy', MAX_POLICY_BYTES);
  const outputPath = path.resolve(outputManifestPath);
  if (outputPath === targetFile.path || outputPath === policyFile.path)
    fail('Output manifest must be a new path distinct from all inputs.');

  const directSources = buildDirectSources({
    target: targetRecord.manifest,
    targetValidated: targetRecord.validated,
    targetSha256: targetRecord.sha256,
    policy,
    policyDirectory: path.dirname(policyFile.path)
  });
  const resultManifest = { ...targetRecord.manifest, directSources };
  validateManifest(resultManifest, 'target manifest with source baselines');
  const outputBytes = Buffer.from(`${JSON.stringify(resultManifest, null, 2)}\n`, 'utf8');
  if (outputBytes.length > MAX_MANIFEST_BYTES) fail('Target manifest with source baselines exceeds 16 MiB.');
  const writtenPath = writeNewFile(outputPath, outputBytes);
  return {
    role: resultManifest.role,
    version: resultManifest.version,
    sourceCount: directSources.length,
    sourceVersions: directSources.map((source) => source.version),
    targetManifestSha256: crypto.createHash('sha256').update(outputBytes).digest('hex'),
    outputManifestPath: writtenPath,
    readOnlyInputs: true,
    installedMachineModified: false,
    signedTarget: false,
    deployable: false
  };
}

function parseArguments(argv) {
  const values = Object.create(null);
  for (let index = 0; index < argv.length; index++) {
    const token = argv[index];
    if (!token.startsWith('--')) fail(`Unexpected argument '${token}'.`);
    const key = token.slice(2);
    if (!['target-manifest', 'source-baselines', 'output-manifest'].includes(key))
      fail(`Unknown argument '--${key}'.`);
    if (Object.hasOwn(values, key)) fail(`Duplicate argument '--${key}'.`);
    const value = argv[++index];
    if (value === undefined || value.startsWith('--')) fail(`Missing value for '--${key}'.`);
    values[key] = value;
  }
  for (const key of ['target-manifest', 'source-baselines', 'output-manifest'])
    if (!Object.hasOwn(values, key)) fail(`Missing required '--${key}'.`);
  return values;
}

function main(argv) {
  if (!argv.length || argv.includes('--help') || argv.includes('-h')) {
    process.stdout.write([
      'Attach explicitly selected, signed direct-source baselines to a canonical unsigned target manifest.',
      'The tool never writes an installed root, signs a release, uploads assets, or publishes an index.',
      '',
      'Usage:',
      '  node scripts/prepare-file-update-source-baselines.cjs --target-manifest FILE',
      '    --source-baselines POLICY.json --output-manifest NEW_FILE'
    ].join('\n') + '\n');
    return 0;
  }
  const values = parseArguments(argv);
  process.stdout.write(JSON.stringify(prepareFileUpdateSourceBaselines({
    targetManifestPath: values['target-manifest'],
    sourceBaselinePath: values['source-baselines'],
    outputManifestPath: values['output-manifest']
  }), null, 2) + '\n');
  return 0;
}

if (require.main === module) {
  try { process.exitCode = main(process.argv.slice(2)); }
  catch (error) {
    process.stderr.write(`prepare-file-update-source-baselines: ${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = {
  buildDirectSources,
  parseArguments,
  prepareFileUpdateSourceBaselines,
  validateBaselinePolicy
};

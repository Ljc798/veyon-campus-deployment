#!/usr/bin/env node
'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { publishRelease, verifyRelease } = require('./file-update-release.cjs');
const { strictJsonParse } = require('./file-update-planner.cjs');

const ROLE_INPUTS = Object.freeze([
  Object.freeze({
    role: 'StudentSetup',
    manifest: 'StudentSetup.file-manifest.json',
    rootArgs: (inputs) => [
      `StudentApp=${inputs.studentApp}`,
      `StudentAgentVersioned=${path.join(inputs.studentApp, 'WebsitePolicyAgent')}`,
      `StudentUpdateHelperVersioned=${inputs.studentHelper}`
    ]
  }),
  Object.freeze({
    role: 'TeacherConsole',
    manifest: 'TeacherConsole.file-manifest.json',
    rootArgs: (inputs) => [
      `TeacherApp=${inputs.teacherApp}`,
      `TeacherUpdateHelperVersioned=${inputs.teacherHelper}`
    ]
  })
]);

function fail(message) {
  throw new Error(message);
}

function parseArgs(argv) {
  const allowed = new Set([
    'manifest-directory', 'student-app', 'student-helper', 'teacher-app', 'teacher-helper'
  ]);
  const values = Object.create(null);
  for (let index = 0; index < argv.length; index++) {
    const token = argv[index];
    if (!token.startsWith('--')) fail(`Unexpected argument '${token}'.`);
    const name = token.slice(2);
    if (!allowed.has(name)) fail(`Unknown argument '--${name}'.`);
    if (Object.hasOwn(values, name)) fail(`Duplicate argument '--${name}'.`);
    const value = argv[++index];
    if (value === undefined || value.startsWith('--')) fail(`Missing value for '--${name}'.`);
    values[name] = value;
  }
  for (const name of allowed) {
    if (!Object.hasOwn(values, name)) fail(`Missing required argument '--${name}'.`);
  }
  return Object.fromEntries(Object.entries(values).map(([name, value]) => [name, path.resolve(value)]));
}

function requireDirectory(directory, label) {
  const stat = fs.lstatSync(directory);
  if (!stat.isDirectory() || stat.isSymbolicLink()) fail(`${label} must be a real directory.`);
  return fs.realpathSync(directory);
}

function readManifest(directory, fileName) {
  const manifestPath = path.join(directory, fileName);
  const stat = fs.lstatSync(manifestPath);
  if (!stat.isFile() || stat.isSymbolicLink()) fail(`Manifest '${fileName}' must be a regular file.`);
  const bytes = fs.readFileSync(manifestPath);
  return { path: manifestPath, manifest: strictJsonParse(bytes, fileName) };
}

function createEphemeralKeyPair(directory) {
  const pair = crypto.generateKeyPairSync('rsa', { modulusLength: 3072 });
  const privateKeyPath = path.join(directory, 'ephemeral-test-private.pem');
  const publicKeyPath = path.join(directory, 'ephemeral-test-public.pem');
  fs.writeFileSync(privateKeyPath, pair.privateKey.export({ type: 'pkcs8', format: 'pem' }),
    { flag: 'wx', mode: 0o600 });
  fs.writeFileSync(publicKeyPath, pair.publicKey.export({ type: 'spki', format: 'pem' }),
    { flag: 'wx', mode: 0o600 });
  return { privateKeyPath, publicKeyPath };
}

function verifyInventories(rawInputs) {
  const inputs = {
    'manifest-directory': requireDirectory(path.resolve(rawInputs['manifest-directory']), 'Manifest directory'),
    studentApp: requireDirectory(path.resolve(rawInputs['student-app']), 'StudentApp root'),
    studentHelper: requireDirectory(path.resolve(rawInputs['student-helper']), 'StudentUpdateHelperVersioned root'),
    teacherApp: requireDirectory(path.resolve(rawInputs['teacher-app']), 'TeacherApp root'),
    teacherHelper: requireDirectory(path.resolve(rawInputs['teacher-helper']), 'TeacherUpdateHelperVersioned root')
  };
  const workParent = process.env.RUNNER_TEMP || os.tmpdir();
  const workDirectory = fs.mkdtempSync(path.join(workParent, 'veyon-file-update-inventory-check-'));
  const results = [];
  try {
    const keys = createEphemeralKeyPair(workDirectory);
    const objectStore = path.join(workDirectory, 'objects');
    for (const definition of ROLE_INPUTS) {
      const { path: manifestPath, manifest } = readManifest(inputs['manifest-directory'], definition.manifest);
      if (manifest.role !== definition.role) fail(`${definition.manifest} role does not match '${definition.role}'.`);
      const releaseDirectory = path.join(workDirectory, 'releases', definition.role, manifest.version);
      const published = publishRelease({
        manifest: manifestPath,
        'private-key': keys.privateKeyPath,
        'public-key': keys.publicKeyPath,
        'object-store': objectStore,
        'release-directory': releaseDirectory,
        root: definition.rootArgs(inputs)
      });
      const verified = verifyRelease({
        'release-directory': releaseDirectory,
        'public-key': keys.publicKeyPath,
        'object-store': objectStore
      });
      if (!verified.signatureVerified || !verified.objectsVerified || verified.deployable ||
          published.productionPublished || published.deployable) {
        fail(`${definition.role} local release verification returned an unexpected publication state.`);
      }
      results.push({
        role: definition.role,
        version: manifest.version,
        fileCount: verified.fileCount,
        verifiedObjectCount: verified.verifiedObjectCount,
        manifestSha256: verified.manifestSha256,
        signatureVerified: verified.signatureVerified,
        objectsVerified: verified.objectsVerified,
        deployable: false
      });
    }
    return {
      roleCount: results.length,
      results,
      ephemeralSigningKey: true,
      temporaryArtifactsRemoved: true,
      installedMachineModified: false,
      productionPublished: false,
      deployable: false
    };
  } finally {
    fs.rmSync(workDirectory, { recursive: true, force: true });
  }
}

function formatHelp() {
  return [
    'Verify Windows file inventories by packaging and verifying local CAS objects with an ephemeral test key.',
    'No production signing key, hosted index, uploaded artifact or installed machine is used.',
    '',
    'Usage:',
    '  node scripts/verify-file-update-inventories.cjs --manifest-directory DIR',
    '    --student-app DIR --student-helper DIR --teacher-app DIR --teacher-helper DIR'
  ].join('\n');
}

if (require.main === module) {
  try {
    if (process.argv.slice(2).length === 1 && process.argv[2] === '--help') {
      process.stdout.write(formatHelp() + '\n');
    } else {
      process.stdout.write(JSON.stringify(verifyInventories(parseArgs(process.argv.slice(2))), null, 2) + '\n');
    }
  } catch (error) {
    process.stderr.write(`verify-file-update-inventories: ${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = { verifyInventories };

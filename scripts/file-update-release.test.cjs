'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { inventoryPayload, ROLE_LAYOUTS } = require('./file-update-planner.cjs');
const { publishRelease, verifyRelease } = require('./file-update-release.cjs');

function writeTree(root, files) {
  fs.mkdirSync(root, { recursive: true });
  for (const [relativePath, contents] of Object.entries(files)) {
    const filePath = path.join(root, ...relativePath.split('/'));
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, contents);
  }
}

function createFixture(t) {
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-file-update-release-'));
  t.after(() => fs.rmSync(temporaryRoot, { recursive: true, force: true }));
  const keyPair = crypto.generateKeyPairSync('rsa', { modulusLength: 3072 });
  const privateKeyPath = path.join(temporaryRoot, 'developer-private.pem');
  const publicKeyPath = path.join(temporaryRoot, 'developer-public.pem');
  fs.writeFileSync(privateKeyPath, keyPair.privateKey.export({ type: 'pkcs8', format: 'pem' }));
  fs.writeFileSync(publicKeyPath, keyPair.publicKey.export({ type: 'spki', format: 'pem' }));

  const appRoot = path.join(temporaryRoot, 'payload', 'TeacherApp');
  const helperRoot = path.join(temporaryRoot, 'payload', 'TeacherHelper');
  writeTree(appRoot, {
    'VeyonCampus.Teacher.exe': 'teacher-app-bytes',
    'Worker/VeyonCampus.Worker.exe': 'shared-worker-bytes',
    'Worker/copy-of-worker.dat': 'shared-worker-bytes'
  });
  writeTree(helperRoot, { 'VeyonCampus.UpdateHelper.exe': 'helper-bytes' });
  const now = new Date();
  const expires = new Date(now.getTime() + 30 * 24 * 60 * 60 * 1000);
  const layout = ROLE_LAYOUTS.TeacherConsole;
  const manifest = inventoryPayload({
    templateVersion: 1,
    product: layout.product,
    role: 'TeacherConsole',
    architecture: 'win-x64',
    version: '1.2.3',
    channel: 'stable',
    releaseSequence: 7,
    createdUtc: now.toISOString(),
    expiresUtc: expires.toISOString(),
    minimumUpdaterVersion: '1.2.0',
    requiredFileUpdateProtocolVersion: 1,
    components: [
      { id: 'TeacherConsole', version: '1.2.3', dependencies: [] },
      { id: 'TeacherWorker', version: '1.2.3', dependencies: ['TeacherConsole'] },
      { id: 'TeacherUpdateHelper', version: '0.4.41', dependencies: [] }
    ],
    requiredRuntimes: [],
    migrationCatalog: [],
    directSources: []
  }, new Map([
    ['TeacherApp', appRoot],
    ['TeacherUpdateHelperVersioned', helperRoot]
  ]));
  const manifestPath = path.join(temporaryRoot, 'manifest.json');
  fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
  const values = {
    manifest: manifestPath,
    'private-key': privateKeyPath,
    'public-key': publicKeyPath,
    'object-store': path.join(temporaryRoot, 'cas'),
    'release-directory': path.join(temporaryRoot, 'release', 'TeacherConsole', '1.2.3'),
    root: [
      `TeacherApp=${appRoot}`,
      `TeacherUpdateHelperVersioned=${helperRoot}`
    ]
  };
  return { temporaryRoot, appRoot, helperRoot, manifest, manifestPath, publicKeyPath, values };
}

test('publish signs the canonical manifest, deduplicates objects and verifies the release', (t) => {
  const fixture = createFixture(t);
  const published = publishRelease(fixture.values);
  assert.equal(published.role, 'TeacherConsole');
  assert.equal(published.version, '1.2.3');
  assert.equal(published.fileCount, 4);
  assert.equal(published.uniqueObjectCount, 3);
  assert.equal(published.objectsCreated, 3);
  assert.equal(published.releaseDirectoryCreated, true);
  assert.equal(published.localArtifactsWritten, true);
  assert.equal(published.installedMachineModified, false);
  assert.equal(published.deployable, false);

  const verified = verifyRelease({
    'release-directory': fixture.values['release-directory'],
    'public-key': fixture.publicKeyPath,
    'object-store': fixture.values['object-store']
  });
  assert.equal(verified.signatureVerified, true);
  assert.equal(verified.objectsVerified, true);
  assert.equal(verified.verifiedObjectCount, 3);
  assert.equal(verified.verifiedObjectBytes, published.uniqueObjectBytes);
  assert.equal(verified.deployable, false);

  const repeated = publishRelease(fixture.values);
  assert.equal(repeated.objectsCreated, 0);
  assert.equal(repeated.releaseDirectoryCreated, false);
});

test('publish fails closed when a payload file no longer matches the complete manifest', (t) => {
  const fixture = createFixture(t);
  fs.writeFileSync(path.join(fixture.appRoot, 'VeyonCampus.Teacher.exe'), 'changed-after-inventory');
  assert.throws(() => publishRelease(fixture.values), /does not match the manifest/);
  assert.equal(fs.existsSync(fixture.values['release-directory']), false);
  const objectRoot = path.join(fixture.values['object-store'], 'sha256');
  assert.equal(fs.existsSync(objectRoot), false);
});

test('verification rejects a tampered content object and an unexpected release file', (t) => {
  const fixture = createFixture(t);
  publishRelease(fixture.values);
  const worker = fixture.manifest.components.find((component) => component.id === 'TeacherWorker').files[0];
  const objectPath = path.join(fixture.values['object-store'], 'sha256', worker.sha256.slice(0, 2), worker.sha256);
  fs.writeFileSync(objectPath, 'tampered-object');
  assert.throws(() => verifyRelease({
    'release-directory': fixture.values['release-directory'],
    'public-key': fixture.publicKeyPath,
    'object-store': fixture.values['object-store']
  }), /expected size|SHA-256/);

  fs.writeFileSync(objectPath, 'shared-worker-bytes');
  fs.writeFileSync(path.join(fixture.values['release-directory'], 'extra.json'), '{}');
  assert.throws(() => verifyRelease({
    'release-directory': fixture.values['release-directory'],
    'public-key': fixture.publicKeyPath,
    'object-store': fixture.values['object-store']
  }), /only manifest\.json and manifest\.sig/);
});

test('publish refuses a public key that does not pair with its private key', (t) => {
  const fixture = createFixture(t);
  const otherPair = crypto.generateKeyPairSync('rsa', { modulusLength: 3072 });
  const otherPublicPath = path.join(fixture.temporaryRoot, 'other-public.pem');
  fs.writeFileSync(otherPublicPath, otherPair.publicKey.export({ type: 'spki', format: 'pem' }));
  fixture.values['public-key'] = otherPublicPath;
  assert.throws(() => publishRelease(fixture.values), /does not match/);
  assert.equal(fs.existsSync(fixture.values['release-directory']), false);
});

test('publish rejects a conflicting existing release before writing new objects', (t) => {
  const fixture = createFixture(t);
  publishRelease(fixture.values);
  const manifest = { ...fixture.manifest, releaseSequence: fixture.manifest.releaseSequence + 1 };
  fs.writeFileSync(fixture.manifestPath, JSON.stringify(manifest, null, 2) + '\n');
  assert.throws(() => publishRelease(fixture.values), /different manifest bytes/);
  const objectFiles = [];
  for (const shard of fs.readdirSync(path.join(fixture.values['object-store'], 'sha256'))) {
    objectFiles.push(...fs.readdirSync(path.join(fixture.values['object-store'], 'sha256', shard)));
  }
  assert.equal(objectFiles.length, 3);
});

test('publish refuses credential and private-key paths in package roots', (t) => {
  const fixture = createFixture(t);
  const relativePath = 'secrets/developer-private.pem';
  const contents = 'never publish private key material';
  writeTree(fixture.appRoot, { [relativePath]: contents });
  const component = fixture.manifest.components.find((item) => item.id === 'TeacherConsole');
  component.files.push({
    path: relativePath,
    sizeBytes: Buffer.byteLength(contents),
    sha256: crypto.createHash('sha256').update(contents).digest('hex')
  });
  component.files.sort((left, right) => left.path.localeCompare(right.path));
  fs.writeFileSync(fixture.manifestPath, JSON.stringify(fixture.manifest, null, 2) + '\n');
  assert.throws(() => publishRelease(fixture.values), /must not be published/);
  assert.equal(fs.existsSync(fixture.values['release-directory']), false);
  assert.equal(fs.existsSync(fixture.values['object-store']), false);
});

test('publish detects output paths that overlap payload roots through a directory alias', (t) => {
  const fixture = createFixture(t);
  const alias = path.join(fixture.temporaryRoot, 'payload-alias');
  try { fs.symlinkSync(fixture.temporaryRoot, alias, 'dir'); }
  catch (error) {
    if (['EPERM', 'EACCES', 'ENOTSUP'].includes(error.code)) {
      t.skip('Directory symlink creation is unavailable in this environment.');
      return;
    }
    throw error;
  }
  fixture.values['object-store'] = path.join(alias, 'payload', 'TeacherApp', 'objects');
  assert.throws(() => publishRelease(fixture.values), /paths must not overlap/);
  assert.equal(fs.existsSync(fixture.values['object-store']), false);
});

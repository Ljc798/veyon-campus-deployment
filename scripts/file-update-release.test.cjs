'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { inventoryPayload, ROLE_LAYOUTS } = require('./file-update-planner.cjs');
const { publishRelease, verifyRelease } = require('./file-update-release.cjs');
const { verifyInventories } = require('./verify-file-update-inventories.cjs');

function writeTree(root, files) {
  fs.mkdirSync(root, { recursive: true });
  for (const [relativePath, contents] of Object.entries(files)) {
    const filePath = path.join(root, ...relativePath.split('/'));
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, contents);
  }
}

function assertReadableByStandardZipLibrary(zipPath) {
  if (process.platform === 'win32') {
    const script = [
      'Add-Type -AssemblyName System.IO.Compression.FileSystem',
      '$archive = [System.IO.Compression.ZipFile]::OpenRead($env:VEYON_FILE_UPDATE_PACK)',
      'try { foreach ($entry in $archive.Entries) { $stream = $entry.Open(); try { $buffer = New-Object byte[] 4096; while ($stream.Read($buffer, 0, $buffer.Length) -gt 0) {} } finally { $stream.Dispose() } } } finally { $archive.Dispose() }'
    ].join('; ');
    execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
      env: { ...process.env, VEYON_FILE_UPDATE_PACK: zipPath },
      stdio: 'ignore'
    });
    return;
  }
  try {
    execFileSync('unzip', ['-t', zipPath], { stdio: 'ignore' });
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
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

function createWindowsInventoryFixture(t) {
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-file-update-inventory-'));
  t.after(() => fs.rmSync(temporaryRoot, { recursive: true, force: true }));
  const manifestDirectory = path.join(temporaryRoot, 'manifests');
  const studentApp = path.join(temporaryRoot, 'student-app');
  const studentHelper = path.join(temporaryRoot, 'student-helper');
  const teacherApp = path.join(temporaryRoot, 'teacher-app');
  const teacherHelper = path.join(temporaryRoot, 'teacher-helper');
  fs.mkdirSync(manifestDirectory, { recursive: true });

  writeTree(studentApp, {
    'VeyonCampus.StudentSetup.exe': 'student-setup-bytes',
    'Worker/VeyonCampus.Worker.exe': 'shared-worker-bytes',
    'WebsitePolicyAgent/VeyonCampus.Agent.exe': 'agent-bytes',
    'StudentCompanion/VeyonCampus.StudentCompanion.exe': 'companion-bytes',
    'EdgePolicyExecutor/VeyonCampus.EdgePolicyExecutor.exe': 'executor-bytes',
    'EdgeNativeHost/VeyonCampus.EdgeNativeHost.exe': 'native-host-bytes',
    'EdgeExtension/manifest.json': '{"name":"fixture"}'
  });
  writeTree(studentHelper, { 'VeyonCampus.UpdateHelper.exe': 'shared-helper-bytes' });
  writeTree(teacherApp, {
    'VeyonCampus.Teacher.exe': 'teacher-bytes',
    'Worker/VeyonCampus.Worker.exe': 'shared-worker-bytes'
  });
  writeTree(teacherHelper, { 'VeyonCampus.UpdateHelper.exe': 'shared-helper-bytes' });

  const roleDefinitions = [
    {
      role: 'StudentSetup',
      version: '1.2.3',
      roots: new Map([
        ['StudentApp', studentApp],
        ['StudentAgentVersioned', path.join(studentApp, 'WebsitePolicyAgent')],
        ['StudentUpdateHelperVersioned', studentHelper]
      ]),
      dependencies: new Map([
        ['StudentSetup', []],
        ['StudentWorker', ['StudentSetup']],
        ['StudentAgentPayload', []],
        ['StudentAgentRuntime', []],
        ['StudentCompanion', ['StudentSetup']],
        ['EdgePolicyExecutor', []],
        ['EdgeNativeHost', []],
        ['EdgeExtension', ['EdgeNativeHost']],
        ['StudentUpdateHelper', []]
      ])
    },
    {
      role: 'TeacherConsole',
      version: '1.2.3',
      roots: new Map([
        ['TeacherApp', teacherApp],
        ['TeacherUpdateHelperVersioned', teacherHelper]
      ]),
      dependencies: new Map([
        ['TeacherConsole', []],
        ['TeacherWorker', ['TeacherConsole']],
        ['TeacherUpdateHelper', []]
      ])
    }
  ];
  const now = new Date();
  for (const definition of roleDefinitions) {
    const layout = ROLE_LAYOUTS[definition.role];
    const manifest = inventoryPayload({
      templateVersion: 1,
      product: layout.product,
      role: definition.role,
      architecture: 'win-x64',
      version: definition.version,
      channel: 'stable',
      releaseSequence: 7,
      createdUtc: now.toISOString(),
      expiresUtc: new Date(now.getTime() + 30 * 24 * 60 * 60 * 1000).toISOString(),
      minimumUpdaterVersion: '0.4.41',
      requiredFileUpdateProtocolVersion: 1,
      components: layout.requiredComponents.map((id) => ({
        id,
        version: id.endsWith('UpdateHelper') ? '0.4.41' : definition.version,
        dependencies: definition.dependencies.get(id)
      })),
      requiredRuntimes: [],
      migrationCatalog: [],
      directSources: []
    }, definition.roots);
    fs.writeFileSync(path.join(manifestDirectory,
      `${definition.role}.file-manifest.json`), `${JSON.stringify(manifest, null, 2)}\n`);
  }

  return { manifestDirectory, studentApp, studentHelper, teacherApp, teacherHelper };
}

test('Windows role inventories package and verify locally with an ephemeral key only', (t) => {
  const fixture = createWindowsInventoryFixture(t);
  const result = verifyInventories({
    'manifest-directory': fixture.manifestDirectory,
    'student-app': fixture.studentApp,
    'student-helper': fixture.studentHelper,
    'teacher-app': fixture.teacherApp,
    'teacher-helper': fixture.teacherHelper
  });
  assert.equal(result.roleCount, 2);
  assert.deepEqual(result.results.map((item) => item.role), ['StudentSetup', 'TeacherConsole']);
  assert.ok(result.results.every((item) => item.signatureVerified && item.objectsVerified && !item.deployable));
  assert.equal(result.ephemeralSigningKey, true);
  assert.equal(result.temporaryArtifactsRemoved, true);
  assert.equal(result.installedMachineModified, false);
  assert.equal(result.productionPublished, false);
  assert.equal(result.deployable, false);
});

test('publish signs the canonical manifest, deduplicates objects and verifies the release', (t) => {
  const fixture = createFixture(t);
  const published = publishRelease(fixture.values);
  assert.equal(published.role, 'TeacherConsole');
  assert.equal(published.version, '1.2.3');
  assert.equal(published.fileCount, 4);
  assert.equal(published.uniqueObjectCount, 3);
  assert.equal(published.objectsCreated, 3);
  assert.equal(published.componentPackCount, 3);
  assert.ok(published.componentPackBytes > 0);
  assert.equal(published.componentPacksVerified, true);
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
  assert.equal(verified.verifiedComponentPackCount, 3);
  assert.equal(verified.verifiedComponentPackBytes, published.componentPackBytes);
  assert.equal(verified.deployable, false);

  const repeated = publishRelease(fixture.values);
  assert.equal(repeated.objectsCreated, 0);
  assert.equal(repeated.componentPackCount, 3);
  assert.equal(repeated.componentPackBytes, published.componentPackBytes);
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
  assertReadableByStandardZipLibrary(path.join(
    fixture.values['release-directory'], 'component-packs',
    'VeyonCampus-TeacherConsole-1.2.3-component-TeacherWorker.zip'
  ));
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
  }), /only component-packs, manifest\.json, and manifest\.sig/);
});

test('component pack verification rejects modified archives and unexpected entries', (t) => {
  const fixture = createFixture(t);
  const published = publishRelease(fixture.values);
  const packName = `VeyonCampus-TeacherConsole-1.2.3-component-TeacherWorker.zip`;
  const packPath = path.join(fixture.values['release-directory'], 'component-packs', packName);
  const validPack = fs.readFileSync(packPath);
  const unknownPack = Buffer.from(validPack);
  const workerDigest = fixture.manifest.components.find((component) => component.id === 'TeacherWorker').files[0].sha256;
  const expectedName = Buffer.from(`sha256-${workerDigest}.blob`, 'ascii');
  let searchFrom = 0;
  let replacedNames = 0;
  while (true) {
    const nameOffset = unknownPack.indexOf(expectedName, searchFrom);
    if (nameOffset < 0) break;
    unknownPack.write('unknown', nameOffset, 'ascii');
    replacedNames++;
    searchFrom = nameOffset + expectedName.length;
  }
  assert.equal(replacedNames, 2);
  fs.writeFileSync(packPath, unknownPack);
  assert.throws(() => verifyRelease({
    'release-directory': fixture.values['release-directory'],
    'public-key': fixture.publicKeyPath,
    'object-store': fixture.values['object-store']
  }), /unknown or duplicate object/);

  fs.writeFileSync(packPath, validPack);
  fs.appendFileSync(packPath, Buffer.from('unindexed trailing bytes'));
  assert.throws(() => verifyRelease({
    'release-directory': fixture.values['release-directory'],
    'public-key': fixture.publicKeyPath,
    'object-store': fixture.values['object-store']
  }), /unsupported ZIP end record/);
  assert.ok(published.componentPackBytes > 0);
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

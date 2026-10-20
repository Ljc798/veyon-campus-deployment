'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const {
  compareSemver,
  createPlan,
  inventoryPayload,
  ROLE_LAYOUTS,
  scanRoot,
  strictJsonParse,
  validateManifest,
  validateRelativePath,
  verifyManifestSignature
} = require('./file-update-planner.cjs');

function sha256(data) {
  return crypto.createHash('sha256').update(data).digest('hex');
}

function writeTree(root, files) {
  fs.mkdirSync(root, { recursive: true });
  for (const [relativePath, contents] of Object.entries(files)) {
    const filePath = path.join(root, ...relativePath.split('/'));
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, contents);
  }
}

function teacherTemplate({ version, releaseSequence, createdUtc, expiresUtc, directSources = [] }) {
  const layout = ROLE_LAYOUTS.TeacherConsole;
  return {
    templateVersion: 1,
    product: layout.product,
    role: 'TeacherConsole',
    architecture: 'win-x64',
    version,
    channel: 'stable',
    releaseSequence,
    createdUtc,
    expiresUtc,
    minimumUpdaterVersion: '1.0.0',
    requiredFileUpdateProtocolVersion: 1,
    components: [
      { id: 'TeacherConsole', version, dependencies: [] },
      { id: 'TeacherWorker', version, dependencies: ['TeacherConsole'] },
      { id: 'TeacherUpdateHelper', version, dependencies: [] }
    ],
    requiredRuntimes: [],
    migrationCatalog: [],
    directSources
  };
}

function serializeManifest(manifest) {
  return Buffer.from(JSON.stringify(manifest, null, 2) + '\n', 'utf8');
}

function makeTeacherFixture(t, options = {}) {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-file-plan-'));
  t.after(() => fs.rmSync(tempRoot, { recursive: true, force: true }));
  const now = Date.now();
  const created = (delta) => new Date(now + delta).toISOString();
  const sourceRoot = path.join(tempRoot, 'source');
  const sourceApp = path.join(sourceRoot, 'TeacherApp');
  const sourceHelper = path.join(sourceRoot, 'TeacherHelper');
  writeTree(sourceApp, {
    'VeyonCampus.Teacher.exe': 'teacher-v1',
    'old-library.dll': 'old-file',
    'Worker/VeyonCampus.Worker.exe': 'worker-same'
  });
  writeTree(sourceHelper, { 'VeyonCampus.UpdateHelper.exe': 'helper-same' });

  const sourceTemplate = teacherTemplate({
    version: '1.0.0', releaseSequence: 1, createdUtc: created(-48 * 60 * 60 * 1000),
    expiresUtc: created(-24 * 60 * 60 * 1000)
  });
  const sourceManifest = inventoryPayload(sourceTemplate, new Map([
    ['TeacherApp', sourceApp], ['TeacherUpdateHelperVersioned', sourceHelper]
  ]));
  const sourceBytes = serializeManifest(sourceManifest);
  const sourceDigest = sha256(sourceBytes);
  const priorLibraryHash = sha256('old-file');

  const targetRoot = path.join(tempRoot, 'target');
  const targetApp = path.join(targetRoot, 'TeacherApp');
  const targetHelper = path.join(targetRoot, 'TeacherHelper');
  writeTree(targetApp, {
    'VeyonCampus.Teacher.exe': 'teacher-v2',
    'new-library.dll': 'new-file',
    'Worker/VeyonCampus.Worker.exe': 'worker-same'
  });
  writeTree(targetHelper, { 'VeyonCampus.UpdateHelper.exe': 'helper-same' });
  const targetTemplate = teacherTemplate({
    version: '1.0.1', releaseSequence: 2, createdUtc: created(-60 * 1000), expiresUtc: created(24 * 60 * 60 * 1000),
    directSources: [{
      version: '1.0.0', manifestSha256: sourceDigest, requiredMigrations: [],
      managedDeletes: options.deleteRule === false ? [] : [{ rootId: 'TeacherApp', path: 'old-library.dll', previousSha256: priorLibraryHash }]
    }]
  });
  const targetManifest = inventoryPayload(targetTemplate, new Map([
    ['TeacherApp', targetApp], ['TeacherUpdateHelperVersioned', targetHelper]
  ]));
  const targetBytes = serializeManifest(targetManifest);
  const sourceRecord = { manifest: sourceManifest, bytes: sourceBytes, sha256: sourceDigest };
  const targetRecord = { manifest: targetManifest, bytes: targetBytes, sha256: sha256(targetBytes) };
  const rootMappings = new Map([
    ['TeacherApp', sourceApp], ['TeacherUpdateHelperVersioned', sourceHelper]
  ]);
  return {
    tempRoot, sourceApp, sourceHelper, targetApp, targetHelper, sourceManifest, targetManifest,
    sourceBytes, targetBytes, sourceRecord, targetRecord, rootMappings,
    options: {
      updaterVersion: '1.0.0', fileUpdateProtocol: 1, migrationRunnerCapability: 0, now: new Date(now)
    }
  };
}

function developmentTrust() {
  return { source: 'unverified-development', target: 'unverified-development', keyId: null };
}

test('strict JSON parser rejects duplicate keys, BOM and invalid UTF-8', () => {
  assert.throws(() => strictJsonParse(Buffer.from('{"role":"StudentSetup","role":"TeacherConsole"}')), /duplicate property/);
  assert.throws(() => strictJsonParse(Buffer.from([0xef, 0xbb, 0xbf, 0x7b, 0x7d])), /BOM/);
  assert.throws(() => strictJsonParse(Buffer.from([0xff, 0x7b, 0x7d])), /UTF-8/);
});

test('path validation rejects traversal, alternate data streams and reserved names', () => {
  assert.equal(validateRelativePath('StudentCompanion/app.dll', 'path'), 'StudentCompanion/app.dll');
  for (const invalid of ['../escape.dll', 'folder/../../escape.dll', 'C:/Windows/x.dll', 'file.dll:stream', 'NUL.txt', 'trailing.']) {
    assert.throws(() => validateRelativePath(invalid, 'path'), /canonical|absolute-path|traversal|reserved/);
  }
});

test('semantic versions use SemVer precedence and ignore build metadata', () => {
  assert.equal(compareSemver('1.0.0-alpha.2', '1.0.0-alpha.10'), -1);
  assert.equal(compareSemver('1.0.0', '1.0.0+build.5'), 0);
  assert.equal(compareSemver('1.1.0', '1.0.99'), 1);
});

test('inventory deterministically captures fixed Teacher roots and component ownership without changing inputs', (t) => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-inventory-'));
  t.after(() => fs.rmSync(tempRoot, { recursive: true, force: true }));
  const app = path.join(tempRoot, 'app');
  const helper = path.join(tempRoot, 'helper');
  writeTree(app, { 'VeyonCampus.Teacher.exe': 'app', 'Worker/VeyonCampus.Worker.exe': 'worker' });
  writeTree(helper, { 'VeyonCampus.UpdateHelper.exe': 'helper' });
  const before = fs.readdirSync(app).sort();
  const template = teacherTemplate({ version: '1.0.1', releaseSequence: 2,
    createdUtc: '2026-10-20T00:00:00Z', expiresUtc: '2026-11-20T00:00:00Z' });
  const first = inventoryPayload(template, new Map([['TeacherApp', app], ['TeacherUpdateHelperVersioned', helper]]));
  const second = inventoryPayload(template, new Map([['TeacherApp', app], ['TeacherUpdateHelperVersioned', helper]]));
  assert.deepEqual(first, second);
  assert.deepEqual(fs.readdirSync(app).sort(), before);
  assert.equal(first.components.find((component) => component.id === 'TeacherWorker').files[0].path, 'Worker/VeyonCampus.Worker.exe');
  assert.equal(first.roots.find((root) => root.id === 'TeacherApp').location, 'ProgramFiles/Veyon Campus/Teacher');
});

test('planner reports unchanged, download, add and explicitly authorized delete actions', (t) => {
  const fixture = makeTeacherFixture(t);
  const plan = createPlan(fixture.sourceRecord, fixture.targetRecord, fixture.rootMappings, fixture.options, developmentTrust());
  assert.equal(plan.status, 'planned');
  assert.equal(plan.readOnly, true);
  assert.equal(plan.deployable, false);
  assert.equal(plan.trust.source, 'unverified-development');
  const actionByPath = new Map(plan.actions.filter((action) => action.path).map((action) => [action.path, action.action]));
  assert.equal(actionByPath.get('VeyonCampus.Teacher.exe'), 'download');
  assert.equal(actionByPath.get('Worker/VeyonCampus.Worker.exe'), 'unchanged');
  assert.equal(actionByPath.get('new-library.dll'), 'add');
  assert.equal(actionByPath.get('old-library.dll'), 'managed-delete');
  assert.equal(actionByPath.get('VeyonCampus.UpdateHelper.exe'), 'unchanged');
});

test('planner sends a source without an exact direct-source baseline to the full installer', (t) => {
  const fixture = makeTeacherFixture(t);
  fixture.targetRecord.manifest.directSources = [];
  fixture.targetRecord.bytes = serializeManifest(fixture.targetRecord.manifest);
  fixture.targetRecord.sha256 = sha256(fixture.targetRecord.bytes);
  const plan = createPlan(fixture.sourceRecord, fixture.targetRecord, fixture.rootMappings, fixture.options, developmentTrust());
  assert.equal(plan.status, 'unsupported-source');
  assert.equal(plan.fallback, 'complete-signed-installer');
  assert.equal(plan.actions.length, 0);
  assert.ok(plan.problems.some((problem) => problem.code === 'source-not-supported'));
});

test('planner fails closed when a source file changes or an unknown file appears', (t) => {
  const altered = makeTeacherFixture(t);
  fs.writeFileSync(path.join(altered.sourceApp, 'VeyonCampus.Teacher.exe'), 'locally-modified');
  const alteredPlan = createPlan(altered.sourceRecord, altered.targetRecord, altered.rootMappings, altered.options, developmentTrust());
  assert.equal(alteredPlan.status, 'unsupported-source');
  assert.equal(alteredPlan.actions.length, 0);
  assert.ok(alteredPlan.problems.some((problem) => problem.code === 'source-file-mismatch'));

  const unknown = makeTeacherFixture(t);
  fs.writeFileSync(path.join(unknown.sourceApp, 'unknown.dll'), 'unowned');
  const unknownPlan = createPlan(unknown.sourceRecord, unknown.targetRecord, unknown.rootMappings, unknown.options, developmentTrust());
  assert.equal(unknownPlan.status, 'unsupported-source');
  assert.ok(unknownPlan.problems.some((problem) => problem.code === 'unknown-source-file'));
});

test('planner reports an unowned empty directory instead of silently ignoring it', (t) => {
  const fixture = makeTeacherFixture(t);
  fs.mkdirSync(path.join(fixture.sourceApp, 'unexpected-empty-directory'));
  const plan = createPlan(fixture.sourceRecord, fixture.targetRecord, fixture.rootMappings, fixture.options, developmentTrust());
  assert.equal(plan.status, 'unsupported-source');
  assert.ok(plan.problems.some((problem) => problem.code === 'unknown-source-directory'));
});

test('scanner rejects hard links instead of treating aliases as separate owned files', (t) => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-hardlink-'));
  t.after(() => fs.rmSync(tempRoot, { recursive: true, force: true }));
  const root = path.join(tempRoot, 'root');
  fs.mkdirSync(root);
  const original = path.join(root, 'owned.dll');
  fs.writeFileSync(original, 'owned');
  try { fs.linkSync(original, path.join(root, 'alias.dll')); }
  catch (error) {
    if (['EPERM', 'EACCES', 'ENOTSUP', 'UNKNOWN'].includes(error.code)) return t.skip('Hard links are unavailable on this filesystem.');
    throw error;
  }
  assert.throws(() => scanRoot('TeacherApp', root), /hard-linked file/);
});

test('planner rejects an unauthorized deletion and an expired target', (t) => {
  const noDelete = makeTeacherFixture(t, { deleteRule: false });
  const deletionPlan = createPlan(noDelete.sourceRecord, noDelete.targetRecord, noDelete.rootMappings, noDelete.options, developmentTrust());
  assert.equal(deletionPlan.status, 'unsupported-source');
  assert.ok(deletionPlan.problems.some((problem) => problem.code === 'delete-not-authorized'));

  const expired = makeTeacherFixture(t);
  expired.targetRecord.manifest.expiresUtc = new Date(Date.now() - 1000).toISOString();
  expired.targetRecord.bytes = serializeManifest(expired.targetRecord.manifest);
  expired.targetRecord.sha256 = sha256(expired.targetRecord.bytes);
  const expiredPlan = createPlan(expired.sourceRecord, expired.targetRecord, expired.rootMappings, expired.options, developmentTrust());
  assert.equal(expiredPlan.status, 'invalid-target');
  assert.equal(expiredPlan.actions.length, 0);
});

test('planner rejects role mismatch and file-update capability gaps', (t) => {
  const fixture = makeTeacherFixture(t);
  fixture.targetRecord.manifest.role = 'StudentSetup';
  assert.throws(() => validateManifest(fixture.targetRecord.manifest, 'target'), /product or architecture/);

  const capabilityFixture = makeTeacherFixture(t);
  capabilityFixture.options.fileUpdateProtocol = 0;
  const plan = createPlan(capabilityFixture.sourceRecord, capabilityFixture.targetRecord,
    capabilityFixture.rootMappings, capabilityFixture.options, developmentTrust());
  assert.equal(plan.status, 'unsupported-source');
  assert.ok(plan.problems.some((problem) => problem.code === 'file-update-capability-missing'));
});

test('planner rejects case-only renames because Windows path identity is case-insensitive', (t) => {
  const fixture = makeTeacherFixture(t);
  const component = fixture.targetRecord.manifest.components.find((value) => value.id === 'TeacherConsole');
  const appFile = component.files.find((file) => file.path === 'VeyonCampus.Teacher.exe');
  appFile.path = 'veyoncAMPUS.Teacher.exe';
  validateManifest(fixture.targetRecord.manifest, 'target');
  const plan = createPlan(fixture.sourceRecord, fixture.targetRecord, fixture.rootMappings, fixture.options, developmentTrust());
  assert.equal(plan.status, 'unsupported-source');
  assert.equal(plan.actions.length, 0);
  assert.ok(plan.problems.some((problem) => problem.code === 'path-case-change'));
});

test('detached manifest signatures bind the exact bytes, domain and pinned RSA key', () => {
  const { publicKey, privateKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 3072 });
  const publicPem = publicKey.export({ type: 'spki', format: 'pem' });
  const keyId = sha256(publicKey.export({ type: 'spki', format: 'der' }));
  const raw = Buffer.from('{"schemaVersion":1}\n');
  const domain = Buffer.from('VeyonCampus.FileManifest.v1\0');
  const signature = crypto.sign('sha256', Buffer.concat([domain, raw]), {
    key: privateKey,
    padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
    saltLength: 32
  }).toString('base64');
  const envelope = Buffer.from(JSON.stringify({ schemaVersion: 1, algorithm: 'RSA-PSS-SHA256', keyId, signature }));
  assert.equal(verifyManifestSignature(raw, envelope, publicPem).verified, true);
  assert.throws(() => verifyManifestSignature(Buffer.from('{"schemaVersion":1}'), envelope, publicPem), /verification failed/);
  assert.throws(() => verifyManifestSignature(raw, envelope, privateKey.export({ type: 'pkcs8', format: 'pem' })), /public key/);
});

test('inventory CLI emits JSON to stdout and leaves package roots unchanged', (t) => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-inventory-cli-'));
  t.after(() => fs.rmSync(tempRoot, { recursive: true, force: true }));
  const app = path.join(tempRoot, 'app');
  const helper = path.join(tempRoot, 'helper');
  const metadataPath = path.join(tempRoot, 'metadata.json');
  writeTree(app, { 'VeyonCampus.Teacher.exe': 'app', 'Worker/VeyonCampus.Worker.exe': 'worker' });
  writeTree(helper, { 'VeyonCampus.UpdateHelper.exe': 'helper' });
  fs.writeFileSync(metadataPath, JSON.stringify(teacherTemplate({ version: '1.0.1', releaseSequence: 2,
    createdUtc: '2026-10-20T00:00:00Z', expiresUtc: '2026-11-20T00:00:00Z' })));
  const beforeApp = fs.readdirSync(app).sort();
  const result = spawnSync(process.execPath, [
    path.join(__dirname, 'file-update-planner.cjs'), 'inventory', '--metadata', metadataPath,
    '--root', `TeacherApp=${app}`, '--root', `TeacherUpdateHelperVersioned=${helper}`
  ], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stderr, '');
  const manifest = JSON.parse(result.stdout);
  assert.equal(manifest.role, 'TeacherConsole');
  assert.equal(manifest.components.length, 3);
  assert.deepEqual(fs.readdirSync(app).sort(), beforeApp);
});

test('Student inventory keeps Agent payload and versioned runtime as separately owned roots', (t) => {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-student-inventory-'));
  t.after(() => fs.rmSync(tempRoot, { recursive: true, force: true }));
  const app = path.join(tempRoot, 'student-app');
  const agent = path.join(tempRoot, 'agent-runtime');
  const helper = path.join(tempRoot, 'student-helper');
  writeTree(app, {
    'VeyonCampus.StudentSetup.exe': 'setup',
    'Worker/VeyonCampus.Worker.exe': 'worker',
    'WebsitePolicyAgent/VeyonCampus.Agent.exe': 'same-agent-bytes',
    'StudentCompanion/VeyonCampus.StudentCompanion.exe': 'companion',
    'EdgePolicyExecutor/VeyonCampus.EdgePolicyExecutor.exe': 'executor',
    'EdgeNativeHost/VeyonCampus.EdgeNativeHost.exe': 'host',
    'EdgeExtension/manifest.json': 'extension'
  });
  writeTree(agent, { 'VeyonCampus.Agent.exe': 'same-agent-bytes' });
  writeTree(helper, { 'VeyonCampus.UpdateHelper.exe': 'helper' });
  const layout = ROLE_LAYOUTS.StudentSetup;
  const metadata = {
    templateVersion: 1, product: layout.product, role: 'StudentSetup', architecture: 'win-x64',
    version: '1.2.3', channel: 'stable', releaseSequence: 3,
    createdUtc: '2026-10-20T00:00:00Z', expiresUtc: '2026-11-20T00:00:00Z',
    minimumUpdaterVersion: '1.2.0', requiredFileUpdateProtocolVersion: 1,
    components: layout.requiredComponents.map((id) => ({ id, version: '1.2.3', dependencies: [] })),
    requiredRuntimes: [{ framework: 'Microsoft.NETCore.App', version: '10.0.13', architecture: 'x64' }],
    migrationCatalog: [], directSources: []
  };
  const manifest = inventoryPayload(metadata, new Map([
    ['StudentApp', app], ['StudentAgentVersioned', agent], ['StudentUpdateHelperVersioned', helper]
  ]));
  const agentPayload = manifest.components.find((component) => component.id === 'StudentAgentPayload').files[0];
  const agentRuntime = manifest.components.find((component) => component.id === 'StudentAgentRuntime').files[0];
  assert.equal(agentPayload.sha256, agentRuntime.sha256);
  assert.equal(agentPayload.path, 'WebsitePolicyAgent/VeyonCampus.Agent.exe');
  assert.equal(agentRuntime.path, 'VeyonCampus.Agent.exe');
  assert.equal(manifest.roots.find((root) => root.id === 'StudentAgentVersioned').activation, 'versioned-root');
});

test('planner reports a reversible compatible migration but never executes it', (t) => {
  const fixture = makeTeacherFixture(t);
  fixture.targetRecord.manifest.migrationCatalog = [{
    id: 'student-agent-layout-v2', runnerCapability: 1, reversible: true,
    requiresReboot: false, roots: ['TeacherApp']
  }];
  fixture.targetRecord.manifest.directSources[0].requiredMigrations = ['student-agent-layout-v2'];
  fixture.targetRecord.bytes = serializeManifest(fixture.targetRecord.manifest);
  fixture.targetRecord.sha256 = sha256(fixture.targetRecord.bytes);
  fixture.options.migrationRunnerCapability = 1;
  const plan = createPlan(fixture.sourceRecord, fixture.targetRecord, fixture.rootMappings, fixture.options, developmentTrust());
  assert.equal(plan.status, 'planned');
  assert.equal(plan.actions.some((action) => action.action === 'migration-required' && action.migrationId === 'student-agent-layout-v2'), true);
  assert.equal(plan.deployable, false);

  fixture.options.migrationRunnerCapability = 0;
  const unsupported = createPlan(fixture.sourceRecord, fixture.targetRecord, fixture.rootMappings, fixture.options, developmentTrust());
  assert.equal(unsupported.status, 'unsupported-source');
  assert.equal(unsupported.actions.some((action) => action.action === 'migration-required'), true);
});

test('signed plan CLI verifies a trusted key rotation and remains non-deployable', (t) => {
  const fixture = makeTeacherFixture(t);
  const sourceKeyPair = crypto.generateKeyPairSync('rsa', { modulusLength: 3072 });
  const targetKeyPair = crypto.generateKeyPairSync('rsa', { modulusLength: 3072 });
  const sourcePublicPem = sourceKeyPair.publicKey.export({ type: 'spki', format: 'pem' });
  const targetPublicPem = targetKeyPair.publicKey.export({ type: 'spki', format: 'pem' });
  const sourceKeyId = sha256(sourceKeyPair.publicKey.export({ type: 'spki', format: 'der' }));
  const targetKeyId = sha256(targetKeyPair.publicKey.export({ type: 'spki', format: 'der' }));
  const sign = (bytes, privateKey, keyId) => JSON.stringify({
    schemaVersion: 1,
    algorithm: 'RSA-PSS-SHA256',
    keyId,
    signature: crypto.sign('sha256', Buffer.concat([Buffer.from('VeyonCampus.FileManifest.v1\0'), bytes]), {
      key: privateKey,
      padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
      saltLength: 32
    }).toString('base64')
  });
  const sourcePath = path.join(fixture.tempRoot, 'source-manifest.json');
  const targetPath = path.join(fixture.tempRoot, 'target-manifest.json');
  const sourceSigPath = path.join(fixture.tempRoot, 'source-manifest.sig');
  const targetSigPath = path.join(fixture.tempRoot, 'target-manifest.sig');
  const sourcePublicKeyPath = path.join(fixture.tempRoot, 'source-public.pem');
  const targetPublicKeyPath = path.join(fixture.tempRoot, 'target-public.pem');
  fs.writeFileSync(sourcePath, fixture.sourceBytes);
  fs.writeFileSync(targetPath, fixture.targetBytes);
  fs.writeFileSync(sourceSigPath, sign(fixture.sourceBytes, sourceKeyPair.privateKey, sourceKeyId));
  fs.writeFileSync(targetSigPath, sign(fixture.targetBytes, targetKeyPair.privateKey, targetKeyId));
  fs.writeFileSync(sourcePublicKeyPath, sourcePublicPem);
  fs.writeFileSync(targetPublicKeyPath, targetPublicPem);
  const result = spawnSync(process.execPath, [
    path.join(__dirname, 'file-update-planner.cjs'), 'plan',
    '--source-manifest', sourcePath, '--target-manifest', targetPath,
    '--source-signature', sourceSigPath, '--target-signature', targetSigPath,
    '--source-public-key', sourcePublicKeyPath, '--target-public-key', targetPublicKeyPath,
    '--root', `TeacherApp=${fixture.sourceApp}`,
    '--root', `TeacherUpdateHelperVersioned=${fixture.sourceHelper}`,
    '--updater-version', '1.0.0', '--file-update-protocol', '1'
  ], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  const plan = JSON.parse(result.stdout);
  assert.equal(plan.status, 'planned');
  assert.equal(plan.trust.source, 'verified');
  assert.equal(plan.trust.target, 'verified');
  assert.equal(plan.trust.sourceKeyId, sourceKeyId);
  assert.equal(plan.trust.targetKeyId, targetKeyId);
  assert.equal(plan.deployable, false);
});

'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { inventoryPayload, ROLE_LAYOUTS } = require('./file-update-planner.cjs');
const { publishRelease } = require('./file-update-release.cjs');
const { publishFileUpdateRelease } = require('./publish-file-update-release.cjs');
const projectConfig = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'cloudbaserc.json'), 'utf8'));

const sourceCommit = '1234567890abcdef1234567890abcdef12345678';
const environment = {
  CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW: 'yes',
  CloudBase__EnvId: 'veyon-control-d3gs8hmuyd09c00a7',
  CloudBase__ApiKey: 'local-contract-double-key'
};

function writeTree(root, files) {
  fs.mkdirSync(root, { recursive: true });
  for (const [relativePath, content] of Object.entries(files)) {
    const filePath = path.join(root, ...relativePath.split('/'));
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, content);
  }
}

function createSignedRelease(t) {
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-file-update-publish-test-'));
  t.after(() => fs.rmSync(temporaryRoot, { recursive: true, force: true }));
  const keyPair = crypto.generateKeyPairSync('rsa', { modulusLength: 3072 });
  const privateKeyPath = path.join(temporaryRoot, 'developer-private.pem');
  const publicKeyPath = path.join(temporaryRoot, 'developer-public.pem');
  fs.writeFileSync(privateKeyPath, keyPair.privateKey.export({ type: 'pkcs8', format: 'pem' }));
  fs.writeFileSync(publicKeyPath, keyPair.publicKey.export({ type: 'spki', format: 'pem' }));
  const appRoot = path.join(temporaryRoot, 'payload', 'TeacherApp');
  const helperRoot = path.join(temporaryRoot, 'payload', 'TeacherUpdateHelper');
  writeTree(appRoot, {
    'VeyonCampus.Teacher.exe': 'teacher-app-bytes',
    'Worker/VeyonCampus.Worker.exe': 'teacher-worker-bytes'
  });
  writeTree(helperRoot, { 'VeyonCampus.UpdateHelper.exe': 'teacher-helper-bytes' });
  const now = new Date();
  const layout = ROLE_LAYOUTS.TeacherConsole;
  const manifest = inventoryPayload({
    templateVersion: 1,
    product: layout.product,
    role: 'TeacherConsole',
    architecture: 'win-x64',
    version: '1.2.3',
    channel: 'stable',
    releaseSequence: 1,
    createdUtc: now.toISOString(),
    expiresUtc: new Date(now.getTime() + 30 * 24 * 60 * 60 * 1000).toISOString(),
    minimumUpdaterVersion: '0.4.41',
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
  fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
  const releaseDirectory = path.join(temporaryRoot, 'release', 'TeacherConsole', '1.2.3');
  const objectStore = path.join(temporaryRoot, 'cas');
  const published = publishRelease({
    manifest: manifestPath,
    'private-key': privateKeyPath,
    'public-key': publicKeyPath,
    'object-store': objectStore,
    'release-directory': releaseDirectory,
    root: [`TeacherApp=${appRoot}`, `TeacherUpdateHelperVersioned=${helperRoot}`]
  });
  const notesPath = path.join(temporaryRoot, 'release-notes.md');
  fs.writeFileSync(notesPath, 'Signed file update publisher fixture.\n');
  const evidenceDirectory = path.join(temporaryRoot, 'docs', 'records', 'update-gates');
  fs.mkdirSync(evidenceDirectory, { recursive: true });
  const sourcePlans = [];
  for (const [index, sourceVersion] of ['1.0.0', '1.1.0', '1.2.0'].entries()) {
    const sourceManifestSha256 = String(index + 1).repeat(64);
    const plan = {
      schemaVersion: 1,
      kind: 'VeyonCampus.FileUpdatePlan',
      readOnly: true,
      deployable: false,
      status: 'planned',
      trust: { source: 'verified', target: 'verified', sourceKeyId: 'a'.repeat(64), targetKeyId: published.keyId },
      source: { role: 'TeacherConsole', version: sourceVersion, releaseSequence: index + 1,
        manifestSha256: sourceManifestSha256, fileCount: 3, bytes: 48 },
      target: { role: 'TeacherConsole', version: '1.2.3', releaseSequence: 1,
        manifestSha256: published.manifestSha256, fileCount: 3, bytes: 48,
        requiredFileUpdateProtocolVersion: 1 },
      actions: [],
      problems: [],
      requiredMigrations: []
    };
    const reportPath = path.join(evidenceDirectory, `source-plan-${index + 1}.json`);
    const reportBytes = Buffer.from(`${JSON.stringify(plan, null, 2)}\n`);
    fs.writeFileSync(reportPath, reportBytes);
    sourcePlans.push({ sourceVersion, sourceManifestSha256,
      planReportPath: path.relative(temporaryRoot, reportPath),
      planReportSha256: crypto.createHash('sha256').update(reportBytes).digest('hex') });
  }
  const planReview = {
    schemaVersion: 1,
    kind: 'VeyonCampus.FileUpdatePlanReview',
    status: 'passed',
    role: 'TeacherConsole',
    targetVersion: '1.2.3',
    targetManifestSha256: published.manifestSha256,
    reviewer: 'Local fixture reviewer',
    reviewedAtUtc: new Date().toISOString(),
    sourcePlans
  };
  const planReviewPath = path.join(evidenceDirectory, 'three-source-plan-review.json');
  fs.writeFileSync(planReviewPath, `${JSON.stringify(planReview, null, 2)}\n`);
  const writeEvidence = (name, text) => {
    const filePath = path.join(evidenceDirectory, name);
    const bytes = Buffer.from(`${text}\n`);
    fs.writeFileSync(filePath, bytes);
    return { evidencePath: path.relative(temporaryRoot, filePath),
      evidenceSha256: crypto.createHash('sha256').update(bytes).digest('hex') };
  };
  const g0Record = {
    schemaVersion: 1,
    kind: 'VeyonCampus.AtomicUpdateG0Acceptance',
    status: 'passed',
    approvedBy: 'Local fixture reviewer',
    approvedAtUtc: new Date().toISOString(),
    atomicUpdateCommit: sourceCommit,
    windows10: { status: 'passed', successfulUpgrade: true, rollbackVerified: true,
      recoveryRequiredVerified: true, ...writeEvidence('windows-10.md', 'Windows 10 acceptance fixture.') },
    windows11: { status: 'passed', successfulUpgrade: true, rollbackVerified: true,
      recoveryRequiredVerified: true, ...writeEvidence('windows-11.md', 'Windows 11 acceptance fixture.') },
    twoDeviceRollout: { status: 'passed', signedCommandVerified: true, lanDeliveryVerified: true,
      firewallVerified: true, concurrentUpdateVerified: true, perDeviceReadbackVerified: true,
      ...writeEvidence('two-device.md', 'Two-device acceptance fixture.') },
    failureInjection: {
      status: 'passed',
      ...writeEvidence('failure-injection.md', 'Atomic update failure injection fixture.'),
      scenarios: Object.fromEntries([
        'before-installer-start', 'after-student-setup-move', 'installer-running', 'agent-staged',
        'task-switched', 'helper-waiting-old-process', 'candidate-agent-started', 'before-health-readback',
        'after-health-readback', 'transaction-commit', 'disk-full', 'target-file-in-use',
        'system-task-creation-failure', 'system-task-acl-failure', 'helper-killed', 'forced-reboot', 'power-loss'
      ].map((scenario) => [scenario, true]))
    }
  };
  const g0AcceptancePath = path.join(evidenceDirectory, 'g0-acceptance.json');
  fs.writeFileSync(g0AcceptancePath, `${JSON.stringify(g0Record, null, 2)}\n`);
  return {
    temporaryRoot,
    releaseDirectory,
    objectStore,
    publicKeyPath,
    notesPath,
    values: {
      'release-directory': releaseDirectory,
      'public-key': publicKeyPath,
      'object-store': objectStore,
      'source-commit': sourceCommit,
      'release-notes': notesPath,
      'source-plan-review': path.relative(temporaryRoot, planReviewPath),
      'g0-acceptance-record': path.relative(temporaryRoot, g0AcceptancePath)
    }
  };
}

function assetInventory(assetPaths) {
  return assetPaths.map((filePath) => {
    const bytes = fs.readFileSync(filePath);
    return {
      name: path.basename(filePath),
      size: bytes.length,
      sha256: crypto.createHash('sha256').update(bytes).digest('hex')
    };
  });
}

function makeRequestDouble(events, catalogRows = []) {
  const rows = [...catalogRows];
  return {
    rows,
    request: async (_base, _key, requestPath, options = {}) => {
      if (requestPath.includes('/rpc/publish_file_update_release_v1')) {
        events.push('cloudbase-write');
        const body = options.body;
        const row = {
          release_id: body.p_release_id,
          role: body.p_role,
          product: body.p_role === 'TeacherConsole' ? 'VeyonCampus.TeacherConsole' : 'VeyonCampus.StudentSetup',
          version: body.p_version,
          architecture: 'win-x64',
          channel: body.p_channel,
          release_sequence: body.p_release_sequence,
          manifest_size_bytes: body.p_manifest_size_bytes,
          manifest_sha256: body.p_manifest_sha256,
          manifest_key_id: body.p_manifest_key_id,
          status: 'published'
        };
        rows.push(row);
        return row.release_id;
      }
      events.push('cloudbase-read');
      const query = new URLSearchParams(requestPath.split('?')[1]);
      const role = query.get('role')?.slice(3);
      const channel = query.get('channel')?.slice(3);
      const versionFilter = query.get('version')?.slice(3);
      const matches = rows.filter((row) => row.role === role && row.channel === channel &&
        (!versionFilter || row.version === versionFilter))
        .sort((left, right) => right.release_sequence - left.release_sequence);
      return matches.slice(0, 1);
    }
  };
}

function validPublisher(options, events, overrides = {}) {
  const publishGitCodeImpl = async ({ args }) => {
    events.push('gitcode');
    return { assets: assetInventory(args.slice(4)) };
  };
  const publishGitHubImpl = async ({ assetPaths }) => {
    events.push('github');
    return { assets: assetInventory(assetPaths) };
  };
  return {
    values: options.values,
    environment,
    repositoryRoot: options.temporaryRoot,
    projectConfig,
    lookupTagCommit: async () => sourceCommit,
    verifyG0CommitAncestry: () => {},
    publishGitCodeImpl,
    publishGitHubImpl,
    ...overrides
  };
}

test('release publisher verifies both mirrors and writes the CloudBase catalog last', async (t) => {
  const fixture = createSignedRelease(t);
  const events = [];
  const cloudbase = makeRequestDouble(events);
  const result = await publishFileUpdateRelease(validPublisher(fixture, events, {
    requestCloudBaseImpl: cloudbase.request
  }));
  assert.equal(result.role, 'TeacherConsole');
  assert.equal(result.tag, 'file-teacher-v1.2.3');
  assert.equal(result.catalogAlreadyPublished, false);
  assert.deepEqual(result.assetsVerifiedOn, ['gitcode', 'github']);
  assert.ok(events.indexOf('gitcode') < events.indexOf('github'));
  assert.ok(events.indexOf('github') < events.indexOf('cloudbase-write'));
  assert.equal(cloudbase.rows.length, 1);
});

test('release publisher does not write the catalog when GitHub mirror verification fails', async (t) => {
  const fixture = createSignedRelease(t);
  const events = [];
  const cloudbase = makeRequestDouble(events);
  await assert.rejects(publishFileUpdateRelease(validPublisher(fixture, events, {
    requestCloudBaseImpl: cloudbase.request,
    publishGitHubImpl: async () => { events.push('github'); throw new Error('mirror verification failed'); }
  })), /mirror verification failed/);
  assert.equal(events.includes('cloudbase-write'), false);
  assert.equal(cloudbase.rows.length, 0);
});

test('release publisher checks signed sequence and version against the withdrawn-inclusive catalog high water mark', async (t) => {
  const fixture = createSignedRelease(t);
  const events = [];
  const existing = {
    release_id: '11111111-1111-4111-8111-111111111111',
    role: 'TeacherConsole', product: 'VeyonCampus.TeacherConsole', version: '1.2.2',
    architecture: 'win-x64', channel: 'stable', release_sequence: 4,
    manifest_size_bytes: 2048, manifest_sha256: 'a'.repeat(64), manifest_key_id: 'b'.repeat(64), status: 'withdrawn'
  };
  const cloudbase = makeRequestDouble(events, [existing]);
  await assert.rejects(publishFileUpdateRelease(validPublisher(fixture, events, {
    requestCloudBaseImpl: cloudbase.request
  })), /both exceed the existing catalog high water mark/);
  assert.equal(events.includes('gitcode'), false);
  assert.equal(events.includes('github'), false);
  assert.equal(events.includes('cloudbase-write'), false);
});

test('release publisher blocks publication until three verified source plan reports are reviewed', async (t) => {
  const fixture = createSignedRelease(t);
  const reviewPath = path.join(fixture.temporaryRoot, fixture.values['source-plan-review']);
  const review = JSON.parse(fs.readFileSync(reviewPath, 'utf8'));
  review.sourcePlans.pop();
  fs.writeFileSync(reviewPath, `${JSON.stringify(review, null, 2)}\n`);
  const events = [];
  await assert.rejects(publishFileUpdateRelease(validPublisher(fixture, events)), /Three-source plan review/);
  assert.deepEqual(events, []);
});

test('release publisher blocks publication when the target commit predates the G0-tested updater', async (t) => {
  const fixture = createSignedRelease(t);
  const events = [];
  await assert.rejects(publishFileUpdateRelease(validPublisher(fixture, events, {
    verifyG0CommitAncestry: () => {
      const error = new Error('commit is not an ancestor');
      error.status = 1;
      throw error;
    }
  })), /predates the Windows-tested G0 atomic updater/);
  assert.deepEqual(events, []);
});

test('release publisher treats identical published metadata as an idempotent no-op after mirror readback', async (t) => {
  const fixture = createSignedRelease(t);
  const events = [];
  const measured = publishRelease({
    manifest: path.join(fixture.temporaryRoot, 'manifest.json'),
    'private-key': path.join(fixture.temporaryRoot, 'developer-private.pem'),
    'public-key': fixture.publicKeyPath,
    'object-store': fixture.objectStore,
    'release-directory': fixture.releaseDirectory,
    root: [
      `TeacherApp=${path.join(fixture.temporaryRoot, 'payload', 'TeacherApp')}`,
      `TeacherUpdateHelperVersioned=${path.join(fixture.temporaryRoot, 'payload', 'TeacherUpdateHelper')}`
    ]
  });
  const manifestBytes = fs.statSync(path.join(fixture.releaseDirectory, 'manifest.json')).size;
  const cloudbase = makeRequestDouble(events, [{
    release_id: '22222222-2222-4222-8222-222222222222',
    role: 'TeacherConsole', product: 'VeyonCampus.TeacherConsole', version: '1.2.3',
    architecture: 'win-x64', channel: 'stable', release_sequence: 1,
    manifest_size_bytes: manifestBytes, manifest_sha256: measured.manifestSha256,
    manifest_key_id: measured.keyId, status: 'published'
  }]);
  const result = await publishFileUpdateRelease(validPublisher(fixture, events, {
    requestCloudBaseImpl: cloudbase.request
  }));
  assert.equal(result.catalogReleaseId, '22222222-2222-4222-8222-222222222222');
  assert.equal(result.catalogAlreadyPublished, true);
  assert.equal(events.includes('cloudbase-write'), false);
  assert.ok(events.indexOf('github') > events.indexOf('gitcode'));
});

test('release publisher recovers an ambiguous RPC response only after exact catalog readback', async (t) => {
  const fixture = createSignedRelease(t);
  const events = [];
  const cloudbase = makeRequestDouble(events);
  const requestCloudBaseImpl = async (...args) => {
    try { return await cloudbase.request(...args); }
    catch (error) { throw error; }
  };
  const wrappedRequest = async (base, key, requestPath, requestOptions = {}) => {
    const result = await requestCloudBaseImpl(base, key, requestPath, requestOptions);
    if (requestPath.includes('/rpc/publish_file_update_release_v1'))
      throw new Error('simulated lost RPC response');
    return result;
  };
  const result = await publishFileUpdateRelease(validPublisher(fixture, events, {
    requestCloudBaseImpl: wrappedRequest
  }));
  assert.equal(result.catalogAlreadyPublished, false);
  assert.equal(cloudbase.rows.length, 1);
  assert.equal(events.filter((event) => event === 'cloudbase-write').length, 1);
});

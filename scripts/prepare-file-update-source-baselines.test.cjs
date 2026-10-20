'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { inventoryPayload, ROLE_LAYOUTS } = require('./file-update-planner.cjs');
const { prepareFileUpdateSourceBaselines } = require('./prepare-file-update-source-baselines.cjs');

const SIGNATURE_DOMAIN = Buffer.from('VeyonCampus.FileManifest.v1\0', 'utf8');

function writeTree(root, files) {
  fs.mkdirSync(root, { recursive: true });
  for (const [relativePath, contents] of Object.entries(files)) {
    const filePath = path.join(root, ...relativePath.split('/'));
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, contents);
  }
}

function teacherTemplate({ version, releaseSequence, directSources = [] }) {
  const layout = ROLE_LAYOUTS.TeacherConsole;
  return {
    templateVersion: 1,
    product: layout.product,
    role: 'TeacherConsole',
    architecture: 'win-x64',
    version,
    channel: 'stable',
    releaseSequence,
    createdUtc: '2026-10-20T00:00:00Z',
    expiresUtc: '2026-12-01T00:00:00Z',
    minimumUpdaterVersion: '1.0.0',
    requiredFileUpdateProtocolVersion: 1,
    components: [
      { id: 'TeacherConsole', version, dependencies: [] },
      { id: 'TeacherWorker', version, dependencies: ['TeacherConsole'] },
      { id: 'TeacherUpdateHelper', version: '1.0.0', dependencies: [] }
    ],
    requiredRuntimes: [],
    migrationCatalog: [],
    directSources
  };
}

function serialize(value) {
  return Buffer.from(`${JSON.stringify(value, null, 2)}\n`, 'utf8');
}

function signManifest(manifestBytes, keyPair) {
  const publicKey = keyPair.publicKey;
  const keyId = crypto.createHash('sha256')
    .update(publicKey.export({ type: 'spki', format: 'der' })).digest('hex');
  const signature = crypto.sign('sha256', Buffer.concat([SIGNATURE_DOMAIN, manifestBytes]), {
    key: keyPair.privateKey,
    padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
    saltLength: 32
  });
  return serialize({ schemaVersion: 1, algorithm: 'RSA-PSS-SHA256', keyId, signature: signature.toString('base64') });
}

function fixture(t, overrides = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-file-source-baseline-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const sourceApp = path.join(root, 'source-payload', 'TeacherApp');
  const sourceHelper = path.join(root, 'source-payload', 'TeacherHelper');
  writeTree(sourceApp, {
    'VeyonCampus.Teacher.exe': 'teacher-source',
    'obsolete.dll': 'old-library',
    'Worker/VeyonCampus.Worker.exe': 'worker-stable'
  });
  writeTree(sourceHelper, { 'VeyonCampus.UpdateHelper.exe': 'helper-stable' });
  const sourceManifest = inventoryPayload(teacherTemplate({ version: '1.0.0', releaseSequence: 1 }), new Map([
    ['TeacherApp', sourceApp], ['TeacherUpdateHelperVersioned', sourceHelper]
  ]));
  const sourceManifestBytes = serialize(sourceManifest);
  const keyPair = crypto.generateKeyPairSync('rsa', { modulusLength: 3072 });
  const baselineDirectory = path.join(root, 'baselines');
  fs.mkdirSync(baselineDirectory);
  fs.writeFileSync(path.join(baselineDirectory, 'source.json'), sourceManifestBytes);
  fs.writeFileSync(path.join(baselineDirectory, 'source.sig'), signManifest(sourceManifestBytes, keyPair));
  fs.writeFileSync(path.join(baselineDirectory, 'source-public.pem'), keyPair.publicKey.export({ type: 'spki', format: 'pem' }));

  const targetApp = path.join(root, 'target-payload', 'TeacherApp');
  const targetHelper = path.join(root, 'target-payload', 'TeacherHelper');
  writeTree(targetApp, {
    'VeyonCampus.Teacher.exe': 'teacher-target',
    'new-library.dll': 'new-library',
    'Worker/VeyonCampus.Worker.exe': 'worker-stable'
  });
  writeTree(targetHelper, { 'VeyonCampus.UpdateHelper.exe': 'helper-stable' });
  const targetManifest = inventoryPayload(teacherTemplate({ version: '1.1.0', releaseSequence: 2 }), new Map([
    ['TeacherApp', targetApp], ['TeacherUpdateHelperVersioned', targetHelper]
  ]));
  const targetManifestPath = path.join(root, 'target-manifest.json');
  const targetBytes = serialize(targetManifest);
  fs.writeFileSync(targetManifestPath, targetBytes);
  const sourceManifestSha256 = crypto.createHash('sha256').update(sourceManifestBytes).digest('hex');
  const policy = {
    schemaVersion: 1,
    kind: 'VeyonCampus.FileUpdateSourceBaselines',
    role: 'TeacherConsole',
    targetManifestSha256: crypto.createHash('sha256').update(targetBytes).digest('hex'),
    sources: [{
      manifestPath: 'source.json',
      signaturePath: 'source.sig',
      publicKeyPath: 'source-public.pem',
      requiredMigrations: [],
      managedDeletes: [{
        rootId: 'TeacherApp', path: 'obsolete.dll',
        previousSha256: crypto.createHash('sha256').update('old-library').digest('hex')
      }]
    }]
  };
  if (overrides.policy) Object.assign(policy, overrides.policy);
  const policyPath = path.join(baselineDirectory, 'policy.json');
  fs.writeFileSync(policyPath, serialize(policy));
  return { root, baselineDirectory, policy, policyPath, targetManifest, targetManifestPath, sourceManifestSha256 };
}

test('source-baseline preparation verifies signed source bytes and exact delete authorization', (t) => {
  const value = fixture(t);
  const outputManifestPath = path.join(value.root, 'target-with-sources.json');
  const result = prepareFileUpdateSourceBaselines({
    targetManifestPath: value.targetManifestPath,
    sourceBaselinePath: value.policyPath,
    outputManifestPath
  });
  const output = JSON.parse(fs.readFileSync(outputManifestPath, 'utf8'));
  assert.equal(result.sourceCount, 1);
  assert.equal(result.sourceVersions[0], '1.0.0');
  assert.equal(result.signedTarget, false);
  assert.equal(result.deployable, false);
  assert.deepEqual(output.directSources, [{
    version: '1.0.0',
    manifestSha256: value.sourceManifestSha256,
    requiredMigrations: [],
    managedDeletes: [{
      rootId: 'TeacherApp', path: 'obsolete.dll',
      previousSha256: crypto.createHash('sha256').update('old-library').digest('hex')
    }]
  }]);
});

test('source-baseline preparation rejects a different target digest and does not create output', (t) => {
  const value = fixture(t, { policy: { targetManifestSha256: 'a'.repeat(64) } });
  const outputManifestPath = path.join(value.root, 'must-not-exist.json');
  assert.throws(() => prepareFileUpdateSourceBaselines({
    targetManifestPath: value.targetManifestPath,
    sourceBaselinePath: value.policyPath,
    outputManifestPath
  }), /different target manifest bytes/);
  assert.equal(fs.existsSync(outputManifestPath), false);
});

test('source-baseline preparation rejects bad source signatures', (t) => {
  const value = fixture(t);
  fs.writeFileSync(path.join(value.baselineDirectory, 'source.sig'), serialize({
    schemaVersion: 1, algorithm: 'RSA-PSS-SHA256', keyId: 'b'.repeat(64), signature: Buffer.from('invalid').toString('base64')
  }));
  assert.throws(() => prepareFileUpdateSourceBaselines({
    targetManifestPath: value.targetManifestPath,
    sourceBaselinePath: value.policyPath,
    outputManifestPath: path.join(value.root, 'bad-signature.json')
  }), /signature|keyId/i);
});

test('source-baseline preparation never overwrites an existing output', (t) => {
  const value = fixture(t);
  const outputManifestPath = path.join(value.root, 'existing-output.json');
  fs.writeFileSync(outputManifestPath, 'keep-existing-bytes');
  assert.throws(() => prepareFileUpdateSourceBaselines({
    targetManifestPath: value.targetManifestPath,
    sourceBaselinePath: value.policyPath,
    outputManifestPath
  }), /already exists/);
  assert.equal(fs.readFileSync(outputManifestPath, 'utf8'), 'keep-existing-bytes');
});

test('source-baseline preparation rejects guessed hashes and deletions still present in the target', (t) => {
  const wrongHash = fixture(t);
  wrongHash.policy.sources[0].managedDeletes[0].previousSha256 = 'f'.repeat(64);
  fs.writeFileSync(wrongHash.policyPath, serialize(wrongHash.policy));
  assert.throws(() => prepareFileUpdateSourceBaselines({
    targetManifestPath: wrongHash.targetManifestPath,
    sourceBaselinePath: wrongHash.policyPath,
    outputManifestPath: path.join(wrongHash.root, 'wrong-hash.json')
  }), /exact signed source file hash/);

  const stillPresent = fixture(t);
  const originalTarget = stillPresent.targetManifest.components.find((item) => item.id === 'TeacherConsole');
  originalTarget.files.push({ path: 'obsolete.dll', sizeBytes: 11,
    sha256: crypto.createHash('sha256').update('old-library').digest('hex') });
  const targetBytes = serialize(stillPresent.targetManifest);
  fs.writeFileSync(stillPresent.targetManifestPath, targetBytes);
  stillPresent.policy.targetManifestSha256 = crypto.createHash('sha256').update(targetBytes).digest('hex');
  fs.writeFileSync(stillPresent.policyPath, serialize(stillPresent.policy));
  assert.throws(() => prepareFileUpdateSourceBaselines({
    targetManifestPath: stillPresent.targetManifestPath,
    sourceBaselinePath: stillPresent.policyPath,
    outputManifestPath: path.join(stillPresent.root, 'still-present.json')
  }), /still exists in the target/);
});

test('source-baseline preparation requires an older matching role and refuses symbolic-link inputs', (t) => {
  const wrongRole = fixture(t, { policy: { role: 'StudentSetup' } });
  assert.throws(() => prepareFileUpdateSourceBaselines({
    targetManifestPath: wrongRole.targetManifestPath,
    sourceBaselinePath: wrongRole.policyPath,
    outputManifestPath: path.join(wrongRole.root, 'wrong-role.json')
  }), /role does not match/);

  const linked = fixture(t);
  const linkedName = path.join(linked.baselineDirectory, 'linked-source.json');
  fs.symlinkSync(path.join(linked.baselineDirectory, 'source.json'), linkedName);
  linked.policy.sources[0].manifestPath = 'linked-source.json';
  fs.writeFileSync(linked.policyPath, serialize(linked.policy));
  assert.throws(() => prepareFileUpdateSourceBaselines({
    targetManifestPath: linked.targetManifestPath,
    sourceBaselinePath: linked.policyPath,
    outputManifestPath: path.join(linked.root, 'linked-input.json')
  }), /link or non-directory parent/);
});

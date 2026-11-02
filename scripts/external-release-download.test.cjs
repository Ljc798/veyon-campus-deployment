'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { test } = require('node:test');
const { downloadConfiguration, installerDownloadUrl } = require('../cloudfunctions/veyon-api/release-download');
const { canonicalStudentAgentTargetPayload, publish, verifyExternalArtifact } =
  require('./publish-application-release.cjs');

test('installer destinations are GitCode releases with matching role and filename', () => {
  const release = { role: 'TeacherConsole', version: '0.4.56', fileName: 'VeyonCampus-Teacher-Setup-0.4.56-win-x64.exe' };
  assert.equal(installerDownloadUrl(downloadConfiguration(), release),
    `https://gitcode.com/ljc_1126/veyon-campus-deployment/releases/download/v0.4.56/${release.fileName}`);
  assert.deepEqual(downloadConfiguration({ CloudBase__ApplicationReleaseDownloadSource: 'gitcode' }), { source: 'gitcode' });
  assert.deepEqual(downloadConfiguration({ CloudBase__ApplicationReleaseDownloadSource: 'github' }), { source: 'gitcode' });
  assert.throws(() => downloadConfiguration({ CloudBase__ApplicationReleaseDownloadSource: 'cloudbase' }));
  assert.throws(() => downloadConfiguration({ CloudBase__ApplicationReleaseDownloadSource: 'mirror' }));
  assert.throws(() => installerDownloadUrl({ source: 'mirror' }, release));
  assert.throws(() => installerDownloadUrl({ source: 'github' }, { ...release, version: '0.4.56-beta' }));
  assert.throws(() => installerDownloadUrl({ source: 'github' }, { ...release, role: 'StudentSetup' }));
});

test('publisher verifies external bytes before sending JSON metadata, with zero storage calls', async () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-external-release-'));
  const originalFetch = global.fetch;
  try {
    const bytes = Buffer.from('synthetic installer fixture');
    const fileName = 'VeyonCampus-Teacher-Setup-0.4.56-win-x64.exe';
    fs.writeFileSync(path.join(temp, fileName), bytes);
    const keys = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
    fs.writeFileSync(path.join(temp, 'private.pem'), keys.privateKey.export({ type: 'pkcs8', format: 'pem' }));
    fs.writeFileSync(path.join(temp, 'public.pem'), keys.publicKey.export({ type: 'spki', format: 'pem' }));
    const environment = {
      CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW: 'yes',
      CloudBase__EnvId: JSON.parse(fs.readFileSync(path.join(__dirname, '../cloudbaserc.json'), 'utf8')).envId,
      CloudBase__ApiKey: 'fixture-not-a-credential',
      VEYONCAMPUS_RELEASE_PRIVATE_KEY_PATH: path.join(temp, 'private.pem'),
      VEYONCAMPUS_RELEASE_PUBLIC_KEY_PATH: path.join(temp, 'public.pem')
    };
    const args = ['--role', 'TeacherConsole', '--version', '0.4.56', '--installer', path.join(temp, fileName), '--confirm-publication'];
    let downloaded = false;
    let published = false;
    let corrupt = false;
    global.fetch = async (url, options = {}) => {
      url = new URL(url);
      assert.ok(!url.pathname.includes('/storages/'), 'CloudBase installer storage must never be accessed');
      if (url.pathname === '/v3/releases/latest') return Response.json({ release: null });
      if (url.hostname === 'gitcode.com') {
        assert.equal(options.headers?.Authorization, undefined, 'CloudBase credentials must not reach GitCode');
        downloaded = true;
        return new Response(corrupt ? Buffer.from('changed bytes') : bytes);
      }
      if (url.pathname === '/v1/rdb/rest/rpc/publish_application_release_v4') {
        assert.ok(downloaded);
        assert.equal(options.method, 'POST');
        assert.equal(options.headers['Content-Type'], 'application/json');
        const body = JSON.parse(options.body);
        assert.equal(body.p_size_bytes, bytes.length);
        assert.equal(body.p_sha256, crypto.createHash('sha256').update(bytes).digest('hex').toUpperCase());
        assert.equal(body.p_student_agent_version, null);
        assert.equal(body.p_student_agent_target_signature, null);
        assert.ok(!('duplex' in options));
        published = true;
        return Response.json(body.p_release_id);
      }
      throw Error(`Unexpected request ${url}`);
    };
    await publish({ environment, args });
    assert.ok(published);
    published = false;
    corrupt = true;
    await assert.rejects(publish({ environment, args }), /differs/);
    assert.equal(published, false, 'bad external bytes must not be advertised');
    global.fetch = async () => new Response('missing', { status: 404 });
    await assert.rejects(verifyExternalArtifact('https://gitcode.com/fixture', {}), /unavailable/);
  } finally {
    global.fetch = originalFetch;
    fs.rmSync(temp, { recursive: true, force: true });
  }
});

test('StudentSetup publication binds its separately signed Agent target to the exact installer digest', async () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-student-agent-target-'));
  const originalFetch = global.fetch;
  try {
    const bytes = Buffer.from('synthetic student setup installer fixture');
    const fileName = 'VeyonCampus-Student-Setup-1.1.57-win-x64.exe';
    const installerPath = path.join(temp, fileName);
    const installerSha256 = crypto.createHash('sha256').update(bytes).digest('hex').toUpperCase();
    fs.writeFileSync(installerPath, bytes);
    fs.writeFileSync(installerPath + '.agent-target.json', JSON.stringify({
      schemaVersion: 1,
      product: 'VeyonCampus.StudentSetup',
      setupVersion: '1.1.57',
      installerSha256,
      studentAgentVersion: '0.4.57'
    }));
    const keys = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
    const privateKeyPath = path.join(temp, 'private.pem');
    fs.writeFileSync(privateKeyPath, keys.privateKey.export({ type: 'pkcs8', format: 'pem' }));
    fs.writeFileSync(path.join(temp, 'public.pem'), keys.publicKey.export({ type: 'spki', format: 'pem' }));
    const environment = {
      CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW: 'yes',
      CloudBase__EnvId: JSON.parse(fs.readFileSync(path.join(__dirname, '../cloudbaserc.json'), 'utf8')).envId,
      CloudBase__ApiKey: 'fixture-not-a-credential',
      VEYONCAMPUS_RELEASE_PRIVATE_KEY_PATH: privateKeyPath,
      VEYONCAMPUS_RELEASE_PUBLIC_KEY_PATH: path.join(temp, 'public.pem')
    };
    let published = false;
    let bindingCompleted = false;
    let returnExistingRelease = false;
    const verifyTargetSignature = (signature) => {
      const descriptor = {
        schemaVersion: 1,
        purpose: 'VeyonCampus.StudentSetup.AgentTarget.v1',
        product: 'VeyonCampus.StudentSetup',
        setupVersion: '1.1.57',
        installerSha256,
        studentAgentVersion: '0.4.57'
      };
      assert.ok(crypto.verify('sha256', canonicalStudentAgentTargetPayload(descriptor), {
        key: keys.publicKey,
        padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
        saltLength: 32
      }, Buffer.from(signature, 'base64')));
    };
    global.fetch = async (url, options = {}) => {
      url = new URL(url);
      if (url.pathname === '/v3/releases/latest') return Response.json({ release: returnExistingRelease ? {
        manifest: {
          schemaVersion: 3,
          version: '1.1.57',
          fileName,
          sizeBytes: bytes.length,
          sha256: installerSha256,
          policyCapabilities: { applicationPolicy: 1, studentSystemPolicy: 1 }
        }
      } : null });
      if (url.hostname === 'gitcode.com') return new Response(bytes);
      if (url.pathname === '/v1/rdb/rest/rpc/publish_application_release_v4') {
        const body = JSON.parse(options.body);
        assert.equal(body.p_sha256, installerSha256);
        assert.equal(body.p_student_agent_version, '0.4.57');
        verifyTargetSignature(body.p_student_agent_target_signature);
        published = true;
        return Response.json(body.p_release_id);
      }
      if (url.pathname === '/v1/rdb/rest/rpc/bind_application_release_agent_target_v1') {
        const body = JSON.parse(options.body);
        assert.equal(body.p_role, 'StudentSetup');
        assert.equal(body.p_version, '1.1.57');
        assert.equal(body.p_sha256, installerSha256);
        assert.equal(body.p_student_agent_version, '0.4.57');
        verifyTargetSignature(body.p_student_agent_target_signature);
        bindingCompleted = true;
        return Response.json('00112233-4455-4677-8899-aabbccddeeff');
      }
      throw Error(`Unexpected request ${url}`);
    };
    await publish({ environment, args: ['--role', 'StudentSetup', '--version', '1.1.57',
      '--installer', installerPath, '--confirm-publication'] });
    assert.ok(published);
    returnExistingRelease = true;
    await publish({ environment, args: ['--role', 'StudentSetup', '--version', '1.1.57',
      '--installer', installerPath, '--confirm-publication'] });
    assert.ok(bindingCompleted, 'an existing exact Setup artifact must receive its signed Agent target');
  } finally {
    global.fetch = originalFetch;
    fs.rmSync(temp, { recursive: true, force: true });
  }
});

test('release workflow leaves GitCode uploads to the local workstation', () => {
  const workflow = fs.readFileSync(path.join(__dirname, '../.github/workflows/windows-installers.yml'), 'utf8');
  const github = workflow.indexOf('- name: Publish or verify GitHub Release assets');
  const metadata = workflow.indexOf('- name: Publish signed version metadata to CloudBase');
  assert.ok(github >= 0 && metadata > github);
  assert.match(workflow, /GitCode\/AtomGit 附件由维护者在本机上传/);
  assert.doesNotMatch(workflow, /GITCODE_TOKEN|publish-gitcode-release\.cjs|mirror-gitcode-develop\.sh|Sync beta source commit to GitCode/);
  assert.match(fs.readFileSync(path.join(__dirname, 'upload-gitcode-release.ps1'), 'utf8'), /publish-gitcode-release\.cjs/);
  assert.equal(fs.existsSync(path.join(__dirname, '../.github/workflows/mirror-develop-to-gitcode.yml')), false);
  assert.equal(fs.existsSync(path.join(__dirname, 'mirror-gitcode-develop.sh')), false);
  assert.match(workflow, /Create or verify the release tag/);
  assert.match(workflow, /VeyonCampus-Student-Setup-\*-win-x64\.exe\.agent-target\.json/);
  assert.match(workflow, /github\.event\.head_commit\.message == 'Release v1\.0\.10'/);
  assert.doesNotMatch(workflow, /win-x64\.zip/);
});

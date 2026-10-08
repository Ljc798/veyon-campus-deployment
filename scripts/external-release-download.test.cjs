'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { test } = require('node:test');
const { downloadConfiguration, installerDownloadUrl } = require('../cloudfunctions/veyon-api/release-download');
const { publish, verifyExternalArtifact } = require('./publish-application-release.cjs');

test('installer destinations are stable GitHub/Gitee releases with matching role and filename', () => {
  const release = { role: 'TeacherConsole', version: '0.4.56', fileName: 'VeyonCampus-Teacher-Setup-0.4.56-win-x64.exe' };
  assert.equal(installerDownloadUrl(downloadConfiguration(), release),
    `https://github.com/Ljc798/veyon-campus-deployment/releases/download/v0.4.56/${release.fileName}`);
  const gitee = downloadConfiguration({ CloudBase__ApplicationReleaseDownloadSource: 'gitee',
    CloudBase__ApplicationReleaseGiteeRepository: 'school/veyon-campus' });
  assert.equal(installerDownloadUrl(gitee, release),
    `https://gitee.com/school/veyon-campus/releases/download/v0.4.56/${release.fileName}`);
  assert.throws(() => downloadConfiguration({ CloudBase__ApplicationReleaseDownloadSource: 'cloudbase' }));
  assert.throws(() => downloadConfiguration({ CloudBase__ApplicationReleaseDownloadSource: 'gitee' }));
  assert.throws(() => downloadConfiguration({ CloudBase__ApplicationReleaseDownloadSource: 'gitee',
    CloudBase__ApplicationReleaseGiteeRepository: '../evil?token=x' }));
  assert.throws(() => downloadConfiguration({ CloudBase__ApplicationReleaseDownloadSource: 'gitee',
    CloudBase__ApplicationReleaseGiteeRepository: '../evil' }));
  assert.throws(() => installerDownloadUrl(gitee, { ...release, version: '0.4.56-beta' }));
  assert.throws(() => installerDownloadUrl(gitee, { ...release, role: 'StudentSetup' }));
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
      if (url.hostname === 'github.com') {
        assert.equal(options.headers?.Authorization, undefined, 'CloudBase credentials must not reach GitHub');
        downloaded = true;
        return new Response(corrupt ? Buffer.from('changed bytes') : bytes);
      }
      if (url.pathname === '/v1/rdb/rest/rpc/publish_application_release_v3') {
        assert.ok(downloaded);
        assert.equal(options.method, 'POST');
        assert.equal(options.headers['Content-Type'], 'application/json');
        const body = JSON.parse(options.body);
        assert.equal(body.p_size_bytes, bytes.length);
        assert.equal(body.p_sha256, crypto.createHash('sha256').update(bytes).digest('hex').toUpperCase());
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
    await assert.rejects(verifyExternalArtifact('https://github.com/fixture', {}), /unavailable/);
  } finally {
    global.fetch = originalFetch;
    fs.rmSync(temp, { recursive: true, force: true });
  }
});

test('release workflow advertises metadata only after both external hosts are published', () => {
  const workflow = fs.readFileSync(path.join(__dirname, '../.github/workflows/windows-installers.yml'), 'utf8');
  const github = workflow.indexOf('- name: Publish or verify GitHub Release assets');
  const gitee = workflow.indexOf('- name: Publish matching Gitee Release');
  const metadata = workflow.indexOf('- name: Publish signed version metadata to CloudBase');
  assert.ok(github >= 0 && gitee > github && metadata > gitee);
});

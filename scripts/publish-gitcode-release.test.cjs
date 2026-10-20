'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { PassThrough } = require('node:stream');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const {
  inspectAssets,
  parseReleaseTag,
  publishGitCodeRelease,
  uploadWithHttps
} = require('./publish-gitcode-release.cjs');

const owner = 'ljc_1126';
const repo = 'veyon-campus-deployment';
const commit = '1234567890abcdef1234567890abcdef12345678';
const version = '1.2.3';
const tag = `file-student-v${version}`;

function digest(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

function fileUpdateAssets(root) {
  const manifestName = `VeyonCampus-StudentSetup-${version}-win-x64.file-manifest.json`;
  const contents = new Map([
    [manifestName, Buffer.from('{"schemaVersion":1}\n')],
    [manifestName.replace(/\.json$/, '.sig'), Buffer.from('{"algorithm":"RSA-PSS-SHA256"}\n')],
    [`VeyonCampus-StudentSetup-${version}-component-StudentWorker.zip`, Buffer.from('component-zip')]
  ]);
  const paths = [];
  for (const [name, bytes] of contents) {
    const filePath = path.join(root, name);
    fs.writeFileSync(filePath, bytes);
    paths.push(filePath);
  }
  return { paths, contents };
}

function mockGitCode({ initialRelease = null } = {}) {
  const state = { release: initialRelease, assets: new Map(initialRelease?.assets || []), calls: [] };
  const fetchImpl = async (input, options = {}) => {
    const url = new URL(input);
    const pathname = decodeURIComponent(url.pathname);
    state.calls.push({ method: options.method || 'GET', pathname, body: options.body || null });
    if (pathname.endsWith(`/releases/tags/${tag}`)) {
      if (!state.release) return new Response('', { status: 404 });
      return Response.json({
        tag_name: tag,
        target_commitish: commit,
        release_status: 'latest',
        name: state.release.name || `Veyon Campus Student File Update ${version}`,
        body: state.release.body || 'signed file update test\n',
        html_url: `https://gitcode.com/${owner}/${repo}/releases/${tag}`,
        assets: [...state.assets].map(([name, bytes]) => ({ name, size: bytes.length }))
      });
    }
    if (pathname.endsWith('/releases')) {
      const body = JSON.parse(options.body);
      state.release = { name: body.name, body: body.body };
      return Response.json({
        tag_name: body.tag_name,
        target_commitish: body.target_commitish,
        release_status: body.release_status,
        name: body.name,
        body: body.body,
        html_url: `https://gitcode.com/${owner}/${repo}/releases/${tag}`,
        assets: []
      }, { status: 201 });
    }
    if (pathname.endsWith('/upload_url'))
      return Response.json({ url: 'https://signed-upload.example/upload', headers: { 'x-test-upload': 'yes' } });
    const downloadMatch = /\/attach_files\/([^/]+)\/download$/.exec(pathname);
    if (downloadMatch) {
      const bytes = state.assets.get(downloadMatch[1]);
      return bytes ? new Response(bytes) : new Response('', { status: 404 });
    }
    throw new Error(`unexpected mocked GitCode API request ${options.method || 'GET'} ${pathname}`);
  };
  return { state, fetchImpl };
}

async function withTemp(callback) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-gitcode-file-update-test-'));
  try { await callback(root); }
  finally { fs.rmSync(root, { recursive: true, force: true }); }
}

function publisherOptions(root, assets, mocked) {
  const notesPath = path.join(root, 'notes.md');
  fs.writeFileSync(notesPath, 'signed file update test\n');
  const remoteCommit = async (actualOwner, actualRepo, actualTag) => {
    assert.equal(actualOwner, owner);
    assert.equal(actualRepo, repo);
    assert.equal(actualTag, tag);
    return commit;
  };
  const uploadWithHttpsImpl = async (url, headers, assetPath, size, sha256) => {
    assert.equal(url, 'https://signed-upload.example/upload');
    assert.equal(headers['x-test-upload'], 'yes');
    const bytes = fs.readFileSync(assetPath);
    assert.equal(bytes.length, size);
    assert.equal(digest(bytes), sha256);
    mocked.state.assets.set(path.basename(assetPath), bytes);
    return 200;
  };
  return {
    args: [tag, version, commit, notesPath, ...assets.paths],
    environment: { GITCODE_OWNER: owner, GITCODE_REPO: repo, GITCODE_TOKEN: 'test-token' },
    fetchImpl: mocked.fetchImpl,
    lookupTagCommit: remoteCommit,
    uploadWithHttpsImpl,
    writeOutput: () => {}
  };
}

test('file update GitCode tags strictly bind role, channel and SemVer', () => {
  assert.deepEqual(parseReleaseTag(tag), {
    version,
    prerelease: false,
    kind: 'file-update',
    role: 'StudentSetup'
  });
  assert.equal(parseReleaseTag('file-beta-teacher-v1.2.3-rc.1').role, 'TeacherConsole');
  assert.equal(parseReleaseTag('file-beta-teacher-v1.2.3-rc.1').prerelease, true);
  assert.throws(() => parseReleaseTag('file-student-v1.2.3-rc.1'), /release tag must use/);
});

test('GitCode file update publication streams and reads back the exact immutable role assets', async () => {
  await withTemp(async (root) => {
    const assets = fileUpdateAssets(root);
    const mocked = mockGitCode();
    const result = await publishGitCodeRelease(publisherOptions(root, assets, mocked));
    assert.equal(result.tag, tag);
    assert.deepEqual(result.assets.map((asset) => asset.name).sort(), [...assets.contents.keys()].sort());
    assert.equal(mocked.state.assets.size, 3);
    assert.equal(mocked.state.release.name, `Veyon Campus Student File Update ${version}`);
    assert.equal(mocked.state.calls.filter((call) => call.method === 'POST').length, 1);

    const retry = await publishGitCodeRelease(publisherOptions(root, assets, mocked));
    assert.equal(retry.assets.length, 3);
    assert.equal(mocked.state.calls.filter((call) => call.method === 'POST').length, 1);
  });
});

test('GitCode file update publication refuses unexpected existing attachments before uploading', async () => {
  await withTemp(async (root) => {
    const assets = fileUpdateAssets(root);
    const mocked = mockGitCode({ initialRelease: { assets: [['unexpected.exe', Buffer.from('x')]] } });
    await assert.rejects(
      publishGitCodeRelease(publisherOptions(root, assets, mocked)),
      /contains unexpected assets/
    );
    assert.equal(mocked.state.calls.some((call) => call.pathname.endsWith('/upload_url')), false);
  });
});

test('GitCode asset inspection rejects oversized files before reading them into memory', async () => {
  await withTemp(async (root) => {
    const sparsePath = path.join(root, 'oversized.zip');
    const descriptor = fs.openSync(sparsePath, 'wx', 0o600);
    fs.ftruncateSync(descriptor, 1_900_000_001);
    fs.closeSync(descriptor);
    await assert.rejects(inspectAssets([sparsePath]), /larger than 1\.9 GB/);
  });
});

test('GitCode HTTPS upload streams a file and verifies transmitted bytes', async () => {
  await withTemp(async (root) => {
    const bytes = Buffer.from('streamed component pack bytes');
    const filePath = path.join(root, 'pack.zip');
    fs.writeFileSync(filePath, bytes);
    const transmitted = [];
    const requestFactory = (_target, options, onResponse) => {
      assert.equal(options.method, 'PUT');
      assert.equal(options.headers['Content-Length'], String(bytes.length));
      const request = new PassThrough();
      request.on('data', (chunk) => transmitted.push(Buffer.from(chunk)));
      request.on('finish', () => {
        const response = new PassThrough();
        response.statusCode = 200;
        onResponse(response);
        response.end();
      });
      return request;
    };
    const status = await uploadWithHttps('https://upload.example/put', {}, filePath,
      bytes.length, digest(bytes), 1000, requestFactory);
    assert.equal(status, 200);
    assert.deepEqual(Buffer.concat(transmitted), bytes);
    await assert.rejects(uploadWithHttps('https://upload.example/put', {}, filePath,
      bytes.length, '0'.repeat(64), 1000, requestFactory), /source changed or did not match/);
  });
});

'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const {
  isLegacyDownloadAsset,
  parseReleaseTag,
  publishGiteeRelease,
  readRemoteTagCommit
} = require('./publish-gitee-release.cjs');

const commit = 'a'.repeat(40);

function makeGiteeApi(initial = {}) {
  const releaseTag = initial.tag ?? 'beta-v1.0.3';
  const prerelease = releaseTag.startsWith('beta-v');
  const state = {
    release: initial.release === false ? null : {
      id: 42,
      tag_name: releaseTag,
      target_commitish: commit,
      name: initial.name ?? `Veyon Campus ${releaseTag.replace(/^beta-v|^v/, '')}${prerelease ? ' Beta' : ''}`,
      prerelease,
      body: initial.body ?? 'Previous notes.\n'
    },
    attachments: new Map(),
    calls: [],
    deleted: []
  };
  let nextAttachmentId = 1;
  for (const name of initial.attachments || []) {
    const id = nextAttachmentId++;
    state.attachments.set(name, { id, name, size: 10 });
  }

  const fetchImpl = async (input, init = {}) => {
    const url = new URL(input);
    const method = init.method || 'GET';
    state.calls.push({ path: url.pathname, method, authorization: new Headers(init.headers).get('authorization') });
    const releaseByTag = `/api/v5/repos/owner/repo/releases/tags/${encodeURIComponent(releaseTag)}`;
    const releaseCollection = '/api/v5/repos/owner/repo/releases';
    const attachmentCollection = '/api/v5/repos/owner/repo/releases/42/attach_files';

    if (url.pathname === releaseByTag && method === 'GET') {
      if (!state.release) return new Response('not found', { status: 404 });
      return jsonResponse(state.release);
    }
    if (url.pathname === releaseCollection && method === 'POST') {
      if (state.release) return new Response('release already exists', { status: 409 });
      const body = JSON.parse(init.body);
      state.release = { id: 42, tag_name: body.tag_name, target_commitish: body.target_commitish,
        name: body.name, prerelease: body.prerelease, body: body.body };
      return jsonResponse(state.release, 201);
    }
    if (url.pathname === '/api/v5/repos/owner/repo/releases/42' && method === 'PATCH') {
      const body = JSON.parse(init.body);
      if (!body.tag_name || !body.name) return new Response('tag_name and name are required', { status: 400 });
      Object.assign(state.release, body);
      return jsonResponse(state.release);
    }
    if (url.pathname === attachmentCollection && method === 'GET') {
      return jsonResponse([...state.attachments.values()]);
    }
    const deletion = new RegExp(`^${attachmentCollection}/(\\d+)$`).exec(url.pathname);
    if (deletion && method === 'DELETE') {
      const entry = [...state.attachments.entries()].find(([, item]) => String(item.id) === deletion[1]);
      if (!entry) return new Response('not found', { status: 404 });
      state.deleted.push(entry[1].name);
      state.attachments.delete(entry[0]);
      return new Response(null, { status: 204 });
    }
    throw new Error(`Unexpected fake Gitee request: ${method} ${url.pathname}`);
  };
  return { fetchImpl, state };
}

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'content-type': 'application/json' }
  });
}

async function withNotes(callback, contents = '## What changed\n\nRelease test notes.\n') {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-gitee-release-test-'));
  const notesPath = path.join(directory, 'release-notes.md');
  fs.writeFileSync(notesPath, contents);
  try { await callback(notesPath); }
  finally { fs.rmSync(directory, { recursive: true, force: true }); }
}

function publishOptions(tag, notesPath, fetchImpl, options = {}) {
  let output = '';
  return {
    args: [tag, notesPath],
    environment: {
      GITHUB_SHA: commit,
      GITEE_OWNER: 'owner',
      GITEE_REPO: 'repo',
      GITEE_TOKEN: 'fixture-token',
      ...(options.environment ?? {})
    },
    fetchImpl,
    lookupTagCommit: options.lookupTagCommit ?? (async () => commit),
    writeOutput: value => { output += value; },
    getOutput: () => output
  };
}

test('Beta release links directly to complete role installers and cleans old split attachments', async () => {
  await withNotes(async notesPath => {
    const tag = 'beta-v1.0.3';
    const oldAssets = [
      'VeyonCampus-Student-Setup-1.0.3-win-x64.exe',
      'VeyonCampus-Student-Setup-1.0.3-win-x64.exe.part001',
      'VeyonCampus-Student-Setup-1.0.3-win-x64.exe.part002',
      'VeyonCampus-Teacher-Setup-1.0.3-win-x64.exe',
      'VeyonCampus-Teacher-Setup-1.0.3-win-x64.exe.part001',
      'VeyonCampus-Teacher-Setup-1.0.3-win-x64.exe.part002',
      'SHA256SUMS',
      'join-gitee-beta-installers.ps1'
    ];
    const api = makeGiteeApi({ tag, attachments: oldAssets });
    const options = publishOptions(tag, notesPath, api.fetchImpl);
    await publishGiteeRelease(options);

    assert.equal(api.state.release.prerelease, true);
    assert.equal(api.state.release.body.includes('https://github.com/Ljc798/veyon-campus-deployment/releases/download/beta-v1.0.3/VeyonCampus-Teacher-Setup-1.0.3-win-x64.exe'), true);
    assert.equal(api.state.release.body.includes('https://github.com/Ljc798/veyon-campus-deployment/releases/download/beta-v1.0.3/VeyonCampus-Student-Setup-1.0.3-win-x64.exe'), true);
    assert.equal(api.state.release.body.includes('https://github.com/Ljc798/veyon-campus-deployment/releases/download/beta-v1.0.3/SHA256SUMS'), true);
    assert.equal(api.state.release.body.includes('Release test notes.'), true);
    assert.doesNotMatch(api.state.release.body, /\.partNNN|join-gitee-beta-installers\.ps1/);
    assert.deepEqual(api.state.deleted.sort(), oldAssets.sort());
    assert.equal(api.state.attachments.size, 0);
    assert.deepEqual(JSON.parse(options.getOutput()).removedLegacyAttachments.sort(), oldAssets.sort());
    assert.ok(api.state.calls.every(call => call.authorization === 'Bearer fixture-token'));
  }, '## What changed\n\nRelease test notes.\n\nGitee 为适配单附件大小限制提供分卷；请下载 `.partNNN` 文件和 `join-gitee-beta-installers.ps1`。\n');
});

test('stable Gitee release uses complete GitHub links without uploading files', async () => {
  await withNotes(async notesPath => {
    const tag = 'v1.2.3';
    const api = makeGiteeApi({ tag, release: false });
    const options = publishOptions(tag, notesPath, api.fetchImpl);
    await publishGiteeRelease(options);

    assert.equal(api.state.release.name, 'Veyon Campus 1.2.3');
    assert.equal(api.state.release.prerelease, false);
    assert.ok(api.state.release.body.includes('/releases/download/v1.2.3/VeyonCampus-Student-Setup-1.2.3-win-x64.exe'));
    assert.equal(api.state.attachments.size, 0);
    assert.equal(JSON.parse(options.getOutput()).tag, tag);
  });
});

test('re-running the same publication is idempotent', async () => {
  await withNotes(async notesPath => {
    const tag = 'beta-v1.0.3';
    const api = makeGiteeApi({ tag, body: '' });
    const first = publishOptions(tag, notesPath, api.fetchImpl);
    await publishGiteeRelease(first);
    const callsAfterFirst = api.state.calls.length;
    const second = publishOptions(tag, notesPath, api.fetchImpl);
    await publishGiteeRelease(second);

    assert.equal(api.state.calls.filter(call => call.method === 'PATCH').length, 1);
    assert.equal(api.state.calls.length - callsAfterFirst, 3);
    assert.deepEqual(api.state.deleted, []);
  });
});

test('unrelated Gitee release attachments are preserved', async () => {
  await withNotes(async notesPath => {
    const tag = 'beta-v1.0.3';
    const api = makeGiteeApi({ tag, attachments: ['notes.txt', 'VeyonCampus-Student-Setup-1.0.3-win-x64.exe.part001'] });
    await publishGiteeRelease(publishOptions(tag, notesPath, api.fetchImpl));
    assert.deepEqual([...api.state.attachments.keys()], ['notes.txt']);
  });
});

test('mismatched Gitee tag is rejected before editing the release', async () => {
  await withNotes(async notesPath => {
    const tag = 'beta-v1.0.3';
    const api = makeGiteeApi({ tag, attachments: ['VeyonCampus-Student-Setup-1.0.3-win-x64.exe.part001'] });
    const options = publishOptions(tag, notesPath, api.fetchImpl, {
      lookupTagCommit: async () => 'b'.repeat(40)
    });
    await assert.rejects(publishGiteeRelease(options), /does not match the expected commit/);
    assert.equal(api.state.calls.length, 0);
    assert.equal(api.state.attachments.size, 1);
  });
});

test('only supported stable and beta release tags are accepted', () => {
  assert.deepEqual(parseReleaseTag('beta-v1.0.3'), { version: '1.0.3', prerelease: true });
  assert.deepEqual(parseReleaseTag('v1.2.3'), { version: '1.2.3', prerelease: false });
  assert.throws(() => parseReleaseTag('beta-v01.0.3'), /must use vX.Y.Z/);
  assert.equal(isLegacyDownloadAsset('VeyonCampus-Student-Setup-1.0.3-win-x64.exe.part001', '1.0.3'), true);
  assert.equal(isLegacyDownloadAsset('notes.txt', '1.0.3'), false);
});

test('remote tag lookup handles annotated and lightweight tags', () => {
  const output = `${'c'.repeat(40)}\trefs/tags/v1.2.3\n${'a'.repeat(40)}\trefs/tags/v1.2.3^{}\n`;
  const exec = (_command, args) => {
    assert.deepEqual(args.slice(-2), ['refs/tags/v1.2.3', 'refs/tags/v1.2.3^{}']);
    return output;
  };
  assert.equal(readRemoteTagCommit('owner', 'repo', 'v1.2.3', exec), 'a'.repeat(40));
});

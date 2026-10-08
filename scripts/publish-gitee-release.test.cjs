'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { publishGiteeRelease } = require('./publish-gitee-release.cjs');

const version = '0.4.52';
const tag = `v${version}`;
const commit = 'a'.repeat(40);

function sha256(buffer) {
  return crypto.createHash('sha256').update(buffer).digest('hex');
}

function fixtureAssets(root, options = {}) {
  const studentName = `VeyonCampus-Student-Setup-${version}-win-x64.exe`;
  const teacherName = `VeyonCampus-Teacher-Setup-${version}-win-x64.exe`;
  const contents = new Map([
    [studentName, Buffer.from('student installer fixture')],
    [teacherName, Buffer.from('teacher installer fixture')]
  ]);
  const paths = [];
  for (const [name, buffer] of contents) {
    const filePath = path.join(root, name);
    fs.writeFileSync(filePath, buffer);
    paths.push(filePath);
  }
  let checksumContent = Buffer.from([...contents]
    .map(([name, buffer]) => `${sha256(buffer)}  ${name}\n`).join(''));
  if (options.corruptChecksums) checksumContent = Buffer.concat([
    checksumContent, Buffer.from(`${'0'.repeat(64)}  bogus.exe\n`)
  ]);
  const checksumPath = path.join(root, 'SHA256SUMS');
  fs.writeFileSync(checksumPath, checksumContent);
  contents.set('SHA256SUMS', checksumContent);
  paths.push(checksumPath);
  return { paths, contents };
}

function makeGiteeApi(initial = {}) {
  const state = {
    release: initial.release === false ? null : {
      id: 42,
      tag_name: tag,
      target_commitish: initial.targetCommitish ?? commit
    },
    attachments: new Map(),
    calls: [],
    uploads: []
  };
  let nextAttachmentId = 1;
  for (const [name, bytes] of initial.attachments || []) {
    const id = nextAttachmentId++;
    state.attachments.set(name, { id, name, size: bytes.length, bytes: Buffer.from(bytes) });
  }

  const fetchImpl = async (input, init = {}) => {
    const url = new URL(input);
    const method = init.method || 'GET';
    state.calls.push({ url: url.pathname, method, authorization: new Headers(init.headers).get('authorization') });
    const releaseByTag = `/api/v5/repos/owner/repo/releases/tags/${tag}`;
    const releaseCollection = '/api/v5/repos/owner/repo/releases';
    const attachmentCollection = '/api/v5/repos/owner/repo/releases/42/attach_files';

    if (url.pathname === releaseByTag && method === 'GET') {
      if (!state.release) return new Response('not found', { status: 404 });
      return jsonResponse(state.release);
    }
    if (url.pathname === releaseCollection && method === 'POST') {
      if (state.release) return new Response('release already exists', { status: 409 });
      const body = JSON.parse(init.body);
      state.release = { id: 42, tag_name: body.tag_name, target_commitish: body.target_commitish };
      return jsonResponse(state.release, 201);
    }
    if (url.pathname === attachmentCollection && method === 'GET') {
      return jsonResponse([...state.attachments.values()].map(({ id, name, size }) => ({ id, name, size })));
    }
    if (url.pathname === attachmentCollection && method === 'POST') {
      const file = init.body.get('file');
      const name = file.name;
      if (state.attachments.has(name)) return new Response('attachment already exists', { status: 409 });
      const bytes = Buffer.from(await file.arrayBuffer());
      const id = nextAttachmentId++;
      state.attachments.set(name, { id, name, size: bytes.length, bytes });
      state.uploads.push(name);
      return jsonResponse({ id, name, size: bytes.length }, 201);
    }
    const download = new RegExp(`^${attachmentCollection}/(\\d+)/download$`).exec(url.pathname);
    if (download && method === 'GET') {
      const attachment = [...state.attachments.values()].find(item => String(item.id) === download[1]);
      return attachment ? new Response(attachment.bytes) : new Response('not found', { status: 404 });
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

async function inTempDirectory(callback) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-gitee-release-test-'));
  try { await callback(root); }
  finally { fs.rmSync(root, { recursive: true, force: true }); }
}

function publishOptions(root, assets, fetchImpl, options = {}) {
  const notesPath = path.join(root, 'release-notes.md');
  fs.writeFileSync(notesPath, 'Test release notes.\n');
  let output = '';
  return {
    args: [tag, notesPath, ...assets.paths],
    environment: { GITHUB_SHA: commit, GITEE_OWNER: 'owner', GITEE_REPO: 'repo', GITEE_TOKEN: 'fixture-token' },
    fetchImpl,
    writeOutput: value => { output += value; },
    getOutput: () => output,
    ...options
  };
}

test('new Gitee release is created with all three verified assets', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const { fetchImpl, state } = makeGiteeApi({ release: false });
    const options = publishOptions(root, assets, fetchImpl);
    await publishGiteeRelease(options);
    assert.deepEqual([...state.attachments.keys()].sort(), [...assets.contents.keys()].sort());
    assert.deepEqual(state.uploads.sort(), [...assets.contents.keys()].sort());
    assert.equal(JSON.parse(options.getOutput()).alreadyPublished, false);
    assert.ok(state.calls.every(call => call.authorization === 'Bearer fixture-token'));
  });
});

test('identical published Gitee release is an idempotent no-op', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const { fetchImpl, state } = makeGiteeApi({ attachments: [...assets.contents] });
    const options = publishOptions(root, assets, fetchImpl);
    await publishGiteeRelease(options);
    assert.equal(JSON.parse(options.getOutput()).alreadyPublished, true);
    assert.deepEqual(state.uploads, []);
    assert.ok(state.calls.every(call => call.method === 'GET'));
  });
});

test('partial release uploads only missing attachment and preserves existing bytes', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const studentName = `VeyonCampus-Student-Setup-${version}-win-x64.exe`;
    const teacherName = `VeyonCampus-Teacher-Setup-${version}-win-x64.exe`;
    const priorStudentBytes = Buffer.from(assets.contents.get(studentName));
    const { fetchImpl, state } = makeGiteeApi({ attachments: [[studentName, priorStudentBytes]] });
    await publishGiteeRelease(publishOptions(root, assets, fetchImpl));
    assert.deepEqual(state.uploads.sort(), [teacherName, 'SHA256SUMS'].sort());
    assert.deepEqual(state.attachments.get(studentName).bytes, priorStudentBytes);
    assert.equal(state.attachments.size, 3);
  });
});

test('different existing attachment fails before upload and remains unchanged', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const studentName = `VeyonCampus-Student-Setup-${version}-win-x64.exe`;
    const previousBytes = Buffer.from('older installer');
    const { fetchImpl, state } = makeGiteeApi({ attachments: [[studentName, previousBytes]] });
    await assert.rejects(
      publishGiteeRelease(publishOptions(root, assets, fetchImpl)),
      /differs from this build; release assets are immutable/
    );
    assert.deepEqual(state.attachments.get(studentName).bytes, previousBytes);
    assert.deepEqual(state.uploads, []);
  });
});

test('unexpected attachment fails closed without uploading', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const { fetchImpl, state } = makeGiteeApi({ attachments: [['unrelated.txt', Buffer.from('keep me')]] });
    await assert.rejects(
      publishGiteeRelease(publishOptions(root, assets, fetchImpl)),
      /unexpected attachment: unrelated.txt/
    );
    assert.deepEqual(state.uploads, []);
    assert.ok(state.attachments.has('unrelated.txt'));
  });
});

test('attachment without a filename fails closed without uploading', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const { fetchImpl, state } = makeGiteeApi({ attachments: [[null, Buffer.from('unidentified attachment')]] });
    await assert.rejects(
      publishGiteeRelease(publishOptions(root, assets, fetchImpl)),
      /attachment without a filename/
    );
    assert.deepEqual(state.uploads, []);
  });
});

test('release targeting a different commit fails before attachment changes', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const { fetchImpl, state } = makeGiteeApi({ targetCommitish: 'b'.repeat(40) });
    await assert.rejects(
      publishGiteeRelease(publishOptions(root, assets, fetchImpl)),
      /points to a different commit/
    );
    assert.deepEqual(state.uploads, []);
  });
});

test('invalid SHA256SUMS is rejected before contacting Gitee', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root, { corruptChecksums: true });
    const { fetchImpl, state } = makeGiteeApi({ release: false });
    await assert.rejects(
      publishGiteeRelease(publishOptions(root, assets, fetchImpl)),
      /SHA256SUMS must contain exactly one record/
    );
    assert.equal(state.calls.length, 0);
  });
});

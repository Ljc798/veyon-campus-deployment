'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { publishGiteeRelease, readRemoteTagCommit } = require('./publish-gitee-release.cjs');

const version = '0.4.52';
const tag = `v${version}`;
const commit = 'a'.repeat(40);

function sha256(buffer) {
  return crypto.createHash('sha256').update(buffer).digest('hex');
}

function fixtureAssets(root, options = {}) {
  const assetVersion = options.version ?? version;
  const studentName = `VeyonCampus-Student-Setup-${assetVersion}-win-x64.exe`;
  const teacherName = `VeyonCampus-Teacher-Setup-${assetVersion}-win-x64.exe`;
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
  if (options.includeHelper) {
    const helperPath = path.join(root, 'join-gitee-beta-installers.ps1');
    const helperContents = Buffer.from('Write-Host "Join verified Beta installer parts."\n');
    fs.writeFileSync(helperPath, helperContents);
    contents.set(path.basename(helperPath), helperContents);
    paths.push(helperPath);
  }
  return { paths, contents };
}

function makeGiteeApi(initial = {}) {
  const releaseTag = initial.tag ?? tag;
  const state = {
    release: initial.release === false ? null : {
      id: 42,
      tag_name: releaseTag,
      target_commitish: initial.targetCommitish ?? commit,
      prerelease: initial.prerelease ?? false,
      body: initial.body ?? 'Test release notes.\n'
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
    const releaseByTag = `/api/v5/repos/owner/repo/releases/tags/${releaseTag}`;
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
      state.release.tag_name = body.tag_name;
      state.release.name = body.name;
      state.release.body = body.body;
      return jsonResponse(state.release);
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
  const targetTag = options.tag ?? tag;
  const notesPath = path.join(root, 'release-notes.md');
  fs.writeFileSync(notesPath, 'Test release notes.\n');
  let output = '';
  return {
    args: [targetTag, notesPath, ...assets.paths],
    fetchImpl,
    writeOutput: value => { output += value; },
    getOutput: () => output,
    ...options,
    environment: {
      GITHUB_SHA: commit,
      GITEE_OWNER: 'owner',
      GITEE_REPO: 'repo',
      GITEE_TOKEN: 'fixture-token',
      ...(options.environment ?? {})
    },
    lookupTagCommit: options.lookupTagCommit ?? (async () => commit)
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

test('Gitee beta release uses the matching tag and is marked prerelease', async () => {
  await inTempDirectory(async root => {
    const betaVersion = '1.0.4';
    const betaTag = `beta-v${betaVersion}`;
    const assets = fixtureAssets(root, { version: betaVersion, includeHelper: true });
    const { fetchImpl, state } = makeGiteeApi({ release: false, tag: betaTag });
    await publishGiteeRelease(publishOptions(root, assets, fetchImpl, {
      tag: betaTag,
      maxAttachmentBytes: 1024,
      chunkSizeBytes: 8,
      writeProgress: () => {}
    }));
    assert.equal(state.release.tag_name, betaTag);
    assert.equal(state.release.name, `Veyon Campus ${betaVersion} Beta`);
    assert.equal(state.release.prerelease, true);
    assert.equal(state.release.body, 'Test release notes.\n');
    assert.ok(state.attachments.has('join-gitee-beta-installers.ps1'));
    assert.ok(state.attachments.has('SHA256SUMS'));
    for (const name of [...assets.contents.keys()].filter(name => name.endsWith('.exe'))) {
      assert.equal(state.attachments.has(name), false);
      const parts = [...state.attachments.values()]
        .filter(item => item.name.startsWith(`${name}.part`))
        .sort((left, right) => left.name.localeCompare(right.name));
      assert.ok(parts.length > 1);
      assert.deepEqual(Buffer.concat(parts.map(part => part.bytes)), assets.contents.get(name));
    }
  });
});

test('Gitee Beta publication resumes matching split assets after a network interruption', async () => {
  await inTempDirectory(async root => {
    const betaVersion = '1.0.4';
    const betaTag = `beta-v${betaVersion}`;
    const assets = fixtureAssets(root, { version: betaVersion, includeHelper: true });
    const api = makeGiteeApi({ release: false, tag: betaTag });
    let failOnce = true;
    const transientFetch = async (input, init = {}) => {
      const url = new URL(input);
      const uploadedFile = init.method === 'POST' && init.body instanceof FormData
        ? init.body.get('file')
        : null;
      if (failOnce && uploadedFile?.name.endsWith('.part002')) {
        failOnce = false;
        const cause = Object.assign(new Error('connection reset'), { code: 'ECONNRESET' });
        throw new TypeError('fetch failed', { cause });
      }
      return api.fetchImpl(input, init);
    };
    const firstOptions = publishOptions(root, assets, transientFetch, {
      tag: betaTag,
      maxAttachmentBytes: 1024,
      chunkSizeBytes: 8,
      writeProgress: () => {}
    });
    await assert.rejects(publishGiteeRelease(firstOptions), /part002.*ECONNRESET/);
    assert.ok([...api.state.attachments.keys()].some(name => name.endsWith('.part001')));
    const retryOptions = publishOptions(root, assets, api.fetchImpl, {
      tag: betaTag,
      maxAttachmentBytes: 1024,
      chunkSizeBytes: 8,
      writeProgress: () => {}
    });
    await publishGiteeRelease(retryOptions);
    assert.equal(api.state.attachments.size, 10);
    assert.equal(JSON.parse(retryOptions.getOutput()).alreadyPublished, false);
    const finalOptions = publishOptions(root, assets, api.fetchImpl, {
      tag: betaTag,
      maxAttachmentBytes: 1024,
      chunkSizeBytes: 8,
      writeProgress: () => {}
    });
    await publishGiteeRelease(finalOptions);
    assert.equal(JSON.parse(finalOptions.getOutput()).alreadyPublished, true);
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

test('existing Gitee Beta release notes are updated before adding recovery assets', async () => {
  await inTempDirectory(async root => {
    const betaVersion = '1.0.3';
    const betaTag = `beta-v${betaVersion}`;
    const assets = fixtureAssets(root, { version: betaVersion, includeHelper: true });
    const { fetchImpl, state } = makeGiteeApi({
      tag: betaTag,
      prerelease: true,
      body: 'The earlier notes did not include split-download instructions.'
    });
    await publishGiteeRelease(publishOptions(root, assets, fetchImpl, {
      tag: betaTag,
      maxAttachmentBytes: 1024,
      chunkSizeBytes: 8,
      writeProgress: () => {}
    }));
    assert.equal(state.release.body, 'Test release notes.\n');
    assert.ok(state.calls.some(call => call.url.endsWith('/releases/42') && call.method === 'PATCH'));
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

test('Gitee tag lookup resolves lightweight and annotated tags', () => {
  const lightweight = readRemoteTagCommit('owner', 'repo', tag, (_command, args) => {
    assert.equal(args[0], 'ls-remote');
    return `${commit}\trefs/tags/${tag}\n`;
  });
  const annotatedCommit = 'c'.repeat(40);
  const annotated = readRemoteTagCommit('owner', 'repo', tag, () =>
    `${'d'.repeat(40)}\trefs/tags/${tag}\n${annotatedCommit}\trefs/tags/${tag}^{}\n`);
  assert.equal(lightweight, commit);
  assert.equal(annotated, annotatedCommit);
  assert.equal(readRemoteTagCommit('owner', 'repo', tag, () => ''), null);
});

test('a release metadata target does not override the exact Gitee tag', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const { fetchImpl, state } = makeGiteeApi({ targetCommitish: 'b'.repeat(40) });
    await publishGiteeRelease(publishOptions(root, assets, fetchImpl));
    assert.equal(state.uploads.length, assets.paths.length);
  });
});

test('publisher accepts an explicit source commit without overriding GitHub defaults', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const { fetchImpl, state } = makeGiteeApi({ release: false });
    await publishGiteeRelease(publishOptions(root, assets, fetchImpl, {
      environment: {
        GITHUB_SHA: 'b'.repeat(40),
        GITEE_RELEASE_TARGET_COMMITISH: commit
      }
    }));
    assert.ok(state.release);
    assert.equal(state.release.target_commitish, commit);
  });
});

test('a different remote Gitee tag fails before release API changes', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const { fetchImpl, state } = makeGiteeApi();
    await assert.rejects(
      publishGiteeRelease(publishOptions(root, assets, fetchImpl, { lookupTagCommit: async () => 'b'.repeat(40) })),
      /does not match the expected commit/
    );
    assert.equal(state.calls.length, 0);
    assert.equal(state.uploads.length, 0);
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

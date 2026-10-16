'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { publishGitHubRelease } = require('./publish-github-release.cjs');

const version = '0.4.52';
const tag = `v${version}`;

function sha256(buffer) {
  return crypto.createHash('sha256').update(buffer).digest('hex');
}

function fixtureAssets(root, options = {}) {
  const assetVersion = options.version ?? version;
  const studentName = `VeyonCampus-Student-Setup-${assetVersion}-win-x64.exe`;
  const teacherName = `VeyonCampus-Teacher-Setup-${assetVersion}-win-x64.exe`;
  const studentZipName = `VeyonCampus-Student-Setup-${assetVersion}-win-x64.zip`;
  const teacherZipName = `VeyonCampus-Teacher-Setup-${assetVersion}-win-x64.zip`;
  const contents = new Map([
    [studentName, Buffer.from('student installer fixture')],
    [teacherName, Buffer.from('teacher installer fixture')],
    [studentZipName, Buffer.from('student bundle fixture')],
    [teacherZipName, Buffer.from('teacher bundle fixture')]
  ]);
  const paths = [];
  for (const [name, buffer] of contents) {
    const filePath = path.join(root, name);
    fs.writeFileSync(filePath, buffer);
    paths.push(filePath);
  }
  const checksumPath = path.join(root, 'SHA256SUMS');
  let checksumContent = Buffer.from([...contents]
    .map(([name, buffer]) => `${sha256(buffer)}  ${name}\n`).join(''));
  if (options.corruptChecksums) checksumContent = Buffer.concat([
    checksumContent, Buffer.from(`${'0'.repeat(64)}  bogus.exe\n`)
  ]);
  fs.writeFileSync(checksumPath, checksumContent);
  contents.set('SHA256SUMS', checksumContent);
  paths.push(checksumPath);
  return { paths, contents };
}

function makeGitHubCli(initial, releaseTag = tag) {
  const state = {
    release: initial ? {
      tagName: releaseTag,
      isDraft: Boolean(initial.isDraft),
      isImmutable: Boolean(initial.isImmutable),
      isPrerelease: Boolean(initial.isPrerelease),
      assets: new Map(initial.assets || [])
    } : null,
    calls: []
  };

  function cliError(message) {
    const error = new Error(message);
    error.stderr = message;
    error.status = 1;
    return error;
  }

  function addAsset(filePath) {
    const name = path.basename(filePath);
    if (state.release.assets.has(name)) throw cliError(`asset ${name} already exists`);
    state.release.assets.set(name, fs.readFileSync(filePath));
  }

  const githubCli = args => {
    state.calls.push([...args]);
    assert.equal(args[0], 'release');
    if (args[1] === 'view') {
      if (!state.release) throw cliError('HTTP 404: Not Found');
      return JSON.stringify({
        tagName: state.release.tagName,
        isDraft: state.release.isDraft,
        isImmutable: state.release.isImmutable,
        isPrerelease: state.release.isPrerelease,
        assets: [...state.release.assets].map(([name, buffer]) => ({ name, size: buffer.length }))
      });
    }
    if (args[1] === 'download') {
      const name = args[args.indexOf('--pattern') + 1];
      const directory = args[args.indexOf('--dir') + 1];
      const buffer = state.release?.assets.get(name);
      if (!buffer) throw cliError(`asset ${name} not found`);
      fs.writeFileSync(path.join(directory, name), buffer);
      return '';
    }
    if (args[1] === 'create') {
      if (state.release) throw cliError('release already exists');
      const titleIndex = args.indexOf('--title');
      const assetPaths = args.slice(3, titleIndex);
      state.release = {
        tagName: args[2],
        isDraft: false,
        isImmutable: false,
        isPrerelease: args.includes('--prerelease'),
        assets: new Map()
      };
      for (const filePath of assetPaths) addAsset(filePath);
      return '';
    }
    if (args[1] === 'upload') {
      for (const filePath of args.slice(3)) addAsset(filePath);
      return '';
    }
    if (args[1] === 'edit') {
      if (args.includes('--draft=false')) state.release.isDraft = false;
      return '';
    }
    throw cliError(`unexpected gh command: ${args.join(' ')}`);
  };
  return { githubCli, state };
}

async function inTempDirectory(callback) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-github-release-test-'));
  try { await callback(root); }
  finally { fs.rmSync(root, { recursive: true, force: true }); }
}

test('new GitHub release is created with the four verified downloads and checksums', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const { githubCli, state } = makeGitHubCli(null);
    const result = await publishGitHubRelease({ tag, version, assetPaths: assets.paths, githubCli });
    assert.equal(result.alreadyPublished, false);
    assert.deepEqual([...state.release.assets.keys()].sort(), [
      'SHA256SUMS',
      `VeyonCampus-Student-Setup-${version}-win-x64.exe`,
      `VeyonCampus-Teacher-Setup-${version}-win-x64.exe`,
      `VeyonCampus-Student-Setup-${version}-win-x64.zip`,
      `VeyonCampus-Teacher-Setup-${version}-win-x64.zip`
    ].sort());
    assert.ok(state.calls.some(call => call[1] === 'create'));
    assert.ok(state.calls.every(call => !call.includes('--clobber')));
  });
});

test('new Beta release is marked as a prerelease and uses the supplied notes', async () => {
  await inTempDirectory(async root => {
    const betaVersion = '1.0.3';
    const betaTag = `beta-v${betaVersion}`;
    const assets = fixtureAssets(root, { version: betaVersion });
    const notesPath = path.join(root, 'beta-notes.md');
    fs.writeFileSync(notesPath, 'Beta test notes.\n');
    const { githubCli, state } = makeGitHubCli(null, betaTag);
    await publishGitHubRelease({
      tag: betaTag,
      version: betaVersion,
      assetPaths: assets.paths,
      notesPath,
      githubCli
    });
    assert.equal(state.release.tagName, betaTag);
    assert.equal(state.release.isPrerelease, true);
    const create = state.calls.find(call => call[1] === 'create');
    assert.ok(create.includes('--prerelease'));
    assert.deepEqual(create.slice(create.indexOf('--notes-file')), ['--notes-file', notesPath]);
    assert.ok(!create.includes('--generate-notes'));
  });
});

test('Beta publication refuses an existing stable release with the same tag', async () => {
  await inTempDirectory(async root => {
    const betaVersion = '1.0.3';
    const betaTag = `beta-v${betaVersion}`;
    const assets = fixtureAssets(root, { version: betaVersion });
    const { githubCli, state } = makeGitHubCli({ isDraft: true, isPrerelease: false }, betaTag);
    await assert.rejects(
      publishGitHubRelease({ tag: betaTag, version: betaVersion, assetPaths: assets.paths, githubCli }),
      /wrong prerelease status/
    );
    assert.equal(state.calls.some(call => ['create', 'upload', 'edit'].includes(call[1])), false);
  });
});

test('matching draft release uploads only missing assets then publishes it', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const teacherName = `VeyonCampus-Teacher-Setup-${version}-win-x64.exe`;
    const checksumName = 'SHA256SUMS';
    const initial = {
      isDraft: true,
      assets: [teacherName, checksumName].map(name => [name, assets.contents.get(name) || fs.readFileSync(path.join(root, name))])
    };
    const { githubCli, state } = makeGitHubCli(initial);
    await publishGitHubRelease({ tag, version, assetPaths: assets.paths, githubCli });
    const upload = state.calls.find(call => call[1] === 'upload');
    assert.ok(upload);
    assert.deepEqual(upload.slice(3).map(filePath => path.basename(filePath)), [
      `VeyonCampus-Student-Setup-${version}-win-x64.exe`,
      `VeyonCampus-Student-Setup-${version}-win-x64.zip`,
      `VeyonCampus-Teacher-Setup-${version}-win-x64.zip`
    ]);
    assert.ok(state.calls.some(call => call[1] === 'edit' && call.includes('--draft=false')));
    assert.equal(state.release.isDraft, false);
    assert.ok(state.calls.every(call => !call.includes('--clobber')));
  });
});

test('identical published release is an idempotent no-op', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const initial = {
      isDraft: false,
      assets: [...assets.contents].map(([name, buffer]) => [name, buffer])
    };
    const { githubCli, state } = makeGitHubCli(initial);
    const result = await publishGitHubRelease({ tag, version, assetPaths: assets.paths, githubCli });
    assert.equal(result.alreadyPublished, true);
    assert.ok(state.calls.every(call => !['create', 'upload', 'edit'].includes(call[1])));
  });
});

test('different existing asset fails closed without replacing it', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const studentName = `VeyonCampus-Student-Setup-${version}-win-x64.exe`;
    const initial = {
      isDraft: true,
      assets: [[studentName, Buffer.from('different prior installer')]]
    };
    const { githubCli, state } = makeGitHubCli(initial);
    const before = Buffer.from(state.release.assets.get(studentName));
    await assert.rejects(
      publishGitHubRelease({ tag, version, assetPaths: assets.paths, githubCli }),
      /differs from this build; release assets are immutable/
    );
    assert.deepEqual(state.release.assets.get(studentName), before);
    assert.ok(state.calls.every(call => !['create', 'upload', 'edit'].includes(call[1])));
  });
});

test('unexpected release asset fails closed without changing the release', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const initial = { isDraft: true, assets: [['unrelated.txt', Buffer.from('keep me')]] };
    const { githubCli, state } = makeGitHubCli(initial);
    await assert.rejects(
      publishGitHubRelease({ tag, version, assetPaths: assets.paths, githubCli }),
      /unexpected asset: unrelated.txt/
    );
    assert.equal(state.calls.some(call => ['create', 'upload', 'edit'].includes(call[1])), false);
  });
});

test('published incomplete release is never modified', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root);
    const studentName = `VeyonCampus-Student-Setup-${version}-win-x64.exe`;
    const initial = { isDraft: false, assets: [[studentName, assets.contents.get(studentName)]] };
    const { githubCli, state } = makeGitHubCli(initial);
    await assert.rejects(
      publishGitHubRelease({ tag, version, assetPaths: assets.paths, githubCli }),
      /is missing assets; it will not be modified/
    );
    assert.equal(state.calls.some(call => ['create', 'upload', 'edit'].includes(call[1])), false);
  });
});

test('invalid local SHA256SUMS is rejected before contacting GitHub', async () => {
  await inTempDirectory(async root => {
    const assets = fixtureAssets(root, { corruptChecksums: true });
    const { githubCli, state } = makeGitHubCli(null);
    await assert.rejects(
      publishGitHubRelease({ tag, version, assetPaths: assets.paths, githubCli }),
      /SHA256SUMS must contain exactly one record/
    );
    assert.equal(state.calls.length, 0);
  });
});

test('Windows release workflow uses the no-overwrite publisher', () => {
  const workflow = fs.readFileSync(path.join(__dirname, '../.github/workflows/windows-installers.yml'), 'utf8');
  assert.match(workflow, /node scripts\/publish-github-release\.cjs/);
  assert.match(workflow, /node --test scripts\/publish-github-release\.test\.cjs scripts\/external-release-download\.test\.cjs/);
  assert.doesNotMatch(workflow, /gh release upload[\s\S]{0,240}--clobber/);
  const betaJob = workflow.slice(workflow.indexOf('  publish-beta-release:'));
  const betaRelease = betaJob.indexOf('Publish or verify GitHub Beta Release first');
  const betaSourceSync = betaJob.indexOf('Sync beta source commit to GitCode');
  assert.ok(betaRelease >= 0);
  assert.equal(betaSourceSync, -1);
  assert.doesNotMatch(betaJob, /Publish GitCode Beta ZIP mirror|publish-gitcode-release\.cjs/);
  assert.match(workflow, /VeyonCampus-Student-Setup-\$\{version\}-win-x64\.exe/);
  assert.doesNotMatch(workflow, /Create beginner-friendly role ZIPs|VeyonCampus-(?:Student|Teacher)-Setup-\$\{version\}-win-x64\.zip/);
  assert.match(workflow, /GitCode\/AtomGit 附件由维护者在本机上传/);
  assert.match(workflow, /gitcode\.com\/ljc_1126\/veyon-campus-deployment\/releases/);
  const existingMirror = fs.readFileSync(path.join(__dirname, '../.github/workflows/mirror-existing-beta-downloads.yml'), 'utf8');
  assert.match(existingMirror, /default: beta-v1\.0\.3/);
  assert.match(existingMirror, /SHA256SUMS-BUNDLES/);
  assert.doesNotMatch(workflow, /recover-beta-v1-0-3-release/);
});

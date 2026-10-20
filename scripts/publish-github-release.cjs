'use strict';

const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createReadStream } = require('node:fs');

const MAX_RELEASE_ASSET_BYTES = 512 * 1024 * 1024;
const MAX_FILE_UPDATE_ASSET_BYTES = 1_900_000_000;
const FILE_UPDATE_SEMVER_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

function defaultGitHubCli(args) {
  return execFileSync('gh', args, {
    encoding: 'utf8',
    maxBuffer: 16 * 1024 * 1024,
    stdio: ['ignore', 'pipe', 'pipe']
  });
}

function expectedAssetNames(version, includeLegacyBundles = false) {
  const names = [
    `VeyonCampus-Student-Setup-${version}-win-x64.exe`,
    `VeyonCampus-Teacher-Setup-${version}-win-x64.exe`,
    'SHA256SUMS'
  ];
  if (includeLegacyBundles) names.splice(2, 0,
    `VeyonCampus-Student-Setup-${version}-win-x64.zip`,
    `VeyonCampus-Teacher-Setup-${version}-win-x64.zip`);
  return names;
}

function validateArguments(tag, version, assetPaths) {
  const prerelease = tag.startsWith('beta-v');
  const expectedTag = `${prerelease ? 'beta-' : ''}v${version}`;
  if (!/^(?:beta-)?v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(tag) ||
      tag !== expectedTag || (assetPaths.length !== 3 && assetPaths.length !== 5)) {
    throw new Error('Expected a vX.Y.Z or beta-vX.Y.Z tag and its Student/Teacher EXE files, SHA256SUMS, and optional legacy ZIP bundles.');
  }
  const expected = expectedAssetNames(version, assetPaths.length === 5).sort();
  const actual = assetPaths.map(filePath => path.basename(filePath)).sort();
  if (actual.length !== expected.length || actual.some((name, index) => name !== expected[index])) {
    throw new Error('Pass the version-matched Student/Teacher EXE files, SHA256SUMS, and optional legacy ZIP bundles exactly once.');
  }
}

async function sha256File(filePath) {
  const hash = crypto.createHash('sha256');
  for await (const chunk of createReadStream(filePath)) hash.update(chunk);
  return hash.digest('hex');
}

async function inspectLocalAssets(assetPaths, maximumBytes = MAX_RELEASE_ASSET_BYTES, requireChecksums = true) {
  const assets = await Promise.all(assetPaths.map(async filePath => {
    const absolutePath = path.resolve(filePath);
    const info = await fs.promises.lstat(absolutePath);
    if (!info.isFile() || info.isSymbolicLink() || info.nlink > 1 || info.size < 1 || info.size > maximumBytes) {
      throw new Error(`Release asset is missing, linked, empty, or exceeds ${maximumBytes} bytes: ${path.basename(filePath)}.`);
    }
    return {
      path: absolutePath,
      name: path.basename(absolutePath),
      size: info.size,
      sha256: await sha256File(absolutePath)
    };
  }));
  if (requireChecksums) {
    const checksumAsset = assets.find(asset => asset.name === 'SHA256SUMS');
    if (checksumAsset.size > 8192) throw new Error('SHA256SUMS is unexpectedly large.');
    validateChecksums(await fs.promises.readFile(checksumAsset.path, 'utf8'), assets);
  }
  return assets;
}

function parseFileUpdateTag(tag, version) {
  const match = /^file-(beta-)?(student|teacher)-v(.+)$/.exec(tag || '');
  const parsedVersion = match && FILE_UPDATE_SEMVER_PATTERN.exec(match[3]);
  if (!match || !parsedVersion || match[3] !== version || (!match[1] && parsedVersion[4]))
    throw new Error('File update GitHub tag must match file-(beta-)?(student|teacher)-v<SemVer>.');
  return {
    version: match[3],
    prerelease: Boolean(match[1]),
    role: match[2] === 'student' ? 'StudentSetup' : 'TeacherConsole',
    displayRole: match[2] === 'student' ? 'Student' : 'Teacher'
  };
}

function validateFileUpdateAssetPaths(parsedTag, assetPaths) {
  if (assetPaths.length < 3 || assetPaths.length > 34)
    throw new Error('File update GitHub Release must contain its manifest, signature, and 1 to 32 component packs.');
  const manifestName = `VeyonCampus-${parsedTag.role}-${parsedTag.version}-win-x64.file-manifest.json`;
  const expected = new Set([manifestName, manifestName.replace(/\.json$/, '.sig')]);
  const packPrefix = `VeyonCampus-${parsedTag.role}-${parsedTag.version}-component-`;
  for (const assetPath of assetPaths) {
    const name = path.basename(assetPath);
    if (name.startsWith(packPrefix) &&
        /^VeyonCampus-(?:TeacherConsole|StudentSetup)-.+-component-[A-Za-z][A-Za-z0-9._-]{0,63}\.zip$/.test(name)) {
      expected.add(name);
    }
  }
  const actual = assetPaths.map((assetPath) => path.basename(assetPath));
  if (expected.size !== actual.length || new Set(actual).size !== actual.length ||
      actual.some((name) => !expected.has(name))) {
    throw new Error('File update GitHub Release assets must contain only the role manifest, signature, and component packs.');
  }
}

function validateChecksums(checksumText, assets) {
  const expected = new Map(assets.filter(asset => asset.name !== 'SHA256SUMS')
    .map(asset => [asset.name, asset.sha256]));
  const lines = checksumText.trimEnd().split(/\r?\n/);
  if (lines.length !== expected.size) throw new Error('SHA256SUMS must contain exactly one record for each installer asset.');
  const actual = new Map();
  for (const line of lines) {
    const match = /^([0-9a-f]{64})  ([^\r\n]+)$/.exec(line);
    if (!match || actual.has(match[2])) throw new Error('SHA256SUMS has an invalid or duplicate record.');
    actual.set(match[2], match[1]);
  }
  if (actual.size !== expected.size || [...expected].some(([name, hash]) => actual.get(name) !== hash)) {
    throw new Error('SHA256SUMS does not match the installer files in this build.');
  }
}

function readRelease(tag, githubCli) {
  let output;
  try {
    output = githubCli(['release', 'view', tag, '--json', 'tagName,assets,isDraft,isImmutable,isPrerelease']);
  } catch (error) {
    const detail = `${error?.stderr || ''} ${error?.message || ''}`;
    if (/release not found|HTTP 404|404 Not Found/i.test(detail)) return null;
    throw new Error(`Could not inspect GitHub Release ${tag}; no assets were changed.`);
  }
  let release;
  try { release = JSON.parse(output); }
  catch { throw new Error(`GitHub returned invalid Release metadata for ${tag}.`); }
  if (release?.tagName !== tag || !Array.isArray(release.assets) || typeof release.isDraft !== 'boolean' ||
      typeof release.isPrerelease !== 'boolean') {
    throw new Error(`GitHub returned incomplete or mismatched Release metadata for ${tag}.`);
  }
  const names = release.assets.map(asset => asset?.name);
  if (names.some(name => typeof name !== 'string' || name.length === 0) || new Set(names).size !== names.length) {
    throw new Error(`GitHub Release ${tag} has invalid or duplicate asset names.`);
  }
  return release;
}

function indexReleaseAssets(release, expected) {
  const result = new Map(release.assets.map(asset => [asset.name, asset]));
  for (const name of result.keys()) {
    if (!expected.has(name)) throw new Error(`GitHub Release contains an unexpected asset: ${name}.`);
  }
  return result;
}

function requireExactAssetSet(release, assets, expectedNames) {
  const indexed = indexReleaseAssets(release, expectedNames);
  if (indexed.size !== assets.length || assets.some(asset => !indexed.has(asset.name))) {
    throw new Error(`GitHub Release ${release.tagName} does not contain exactly the expected assets.`);
  }
  return indexed;
}

async function verifyRemoteAsset(tag, asset, expected, githubCli, tempRoot) {
  const downloadDirectory = fs.mkdtempSync(path.join(tempRoot, 'asset-'));
  try {
    githubCli(['release', 'download', tag, '--pattern', asset.name, '--dir', downloadDirectory]);
  } catch {
    throw new Error(`Could not download existing GitHub asset ${asset.name}; it will not be replaced.`);
  }
  const remotePath = path.join(downloadDirectory, asset.name);
  let info;
  try { info = await fs.promises.lstat(remotePath); }
  catch { throw new Error(`GitHub did not provide the existing asset ${asset.name}; it will not be replaced.`); }
  if (!info.isFile() || info.isSymbolicLink() || info.size !== asset.size ||
      await sha256File(remotePath) !== asset.sha256) {
    throw new Error(`Existing GitHub asset ${asset.name} differs from this build; release assets are immutable.`);
  }
}

async function publishGitHubRelease({ tag, version, assetPaths, notesPath = null, githubCli = defaultGitHubCli }) {
  validateArguments(tag, version, assetPaths);
  const prerelease = tag.startsWith('beta-v');
  if (notesPath) {
    const notesInfo = await fs.promises.lstat(path.resolve(notesPath));
    if (!notesInfo.isFile() || notesInfo.isSymbolicLink() || notesInfo.size < 1) {
      throw new Error('Release notes are missing, empty, or not a regular file.');
    }
  }
  const assets = await inspectLocalAssets(assetPaths);
  const expectedNames = new Set(assets.map(asset => asset.name));
  let release = readRelease(tag, githubCli);
  let alreadyPublished = Boolean(release && !release.isDraft);

  if (release && release.isPrerelease !== prerelease) {
    throw new Error(`GitHub Release ${tag} has the wrong prerelease status; no assets were changed.`);
  }

  if (!release) {
    const createArgs = [
      'release', 'create', tag,
      ...assets.map(asset => asset.path),
      '--title', `Veyon Campus ${version}`,
      '--verify-tag'
    ];
    if (prerelease) createArgs.push('--prerelease');
    if (notesPath) createArgs.push('--notes-file', path.resolve(notesPath));
    else createArgs.push('--generate-notes');
    githubCli(createArgs);
    release = readRelease(tag, githubCli);
    if (!release) throw new Error(`GitHub Release ${tag} was not visible after creation.`);
    const indexed = requireExactAssetSet(release, assets, expectedNames);
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-release-verify-'));
    try {
      for (const asset of assets) await verifyRemoteAsset(tag, asset, indexed.get(asset.name), githubCli, tempRoot);
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  } else {
    let indexed = indexReleaseAssets(release, expectedNames);
    const existingTempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-release-check-'));
    try {
      for (const asset of assets) {
        const existing = indexed.get(asset.name);
        if (existing) await verifyRemoteAsset(tag, asset, existing, githubCli, existingTempRoot);
      }
    } finally {
      fs.rmSync(existingTempRoot, { recursive: true, force: true });
    }
    const missing = assets.filter(asset => !indexed.has(asset.name));
    if (missing.length > 0) {
      if (!release.isDraft || release.isImmutable === true) {
        throw new Error(`Published GitHub Release ${tag} is missing assets; it will not be modified.`);
      }
      githubCli(['release', 'upload', tag, ...missing.map(asset => asset.path)]);
    }
    if (release.isDraft) githubCli(['release', 'edit', tag, '--draft=false']);
    release = readRelease(tag, githubCli);
    if (!release) throw new Error(`GitHub Release ${tag} disappeared during publication.`);
    indexed = requireExactAssetSet(release, assets, expectedNames);
    if (missing.length > 0) {
      const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-release-verify-'));
      try {
        for (const asset of missing) await verifyRemoteAsset(tag, asset, indexed.get(asset.name), githubCli, tempRoot);
      } finally {
        fs.rmSync(tempRoot, { recursive: true, force: true });
      }
    }
  }
  if (release.isDraft) {
    throw new Error(`GitHub Release ${tag} remains a draft after publication.`);
  }
  return { tag, assets: assets.map(asset => asset.name), alreadyPublished };
}

async function publishGitHubFileUpdateRelease({ tag, version, assetPaths, notesPath = null, githubCli = defaultGitHubCli }) {
  const parsedTag = parseFileUpdateTag(tag, version);
  validateFileUpdateAssetPaths(parsedTag, assetPaths);
  if (notesPath) {
    const notesInfo = await fs.promises.lstat(path.resolve(notesPath));
    if (!notesInfo.isFile() || notesInfo.isSymbolicLink() || notesInfo.size < 1) {
      throw new Error('File update Release notes are missing, empty, or not a regular file.');
    }
  }
  const assets = await inspectLocalAssets(assetPaths, MAX_FILE_UPDATE_ASSET_BYTES, false);
  const expectedNames = new Set(assets.map((asset) => asset.name));
  let release = readRelease(tag, githubCli);
  let alreadyPublished = Boolean(release && !release.isDraft);

  if (release && release.isPrerelease !== parsedTag.prerelease) {
    throw new Error(`GitHub file update Release ${tag} has the wrong prerelease status; no assets were changed.`);
  }

  if (!release) {
    const createArgs = [
      'release', 'create', tag,
      ...assets.map((asset) => asset.path),
      '--title', `Veyon Campus ${parsedTag.displayRole} File Update ${version}${parsedTag.prerelease ? ' Beta' : ''}`,
      '--verify-tag'
    ];
    if (parsedTag.prerelease) createArgs.push('--prerelease');
    if (notesPath) createArgs.push('--notes-file', path.resolve(notesPath));
    else createArgs.push('--notes', 'Signed Veyon Campus file update manifest and component packs.');
    githubCli(createArgs);
    release = readRelease(tag, githubCli);
    if (!release) throw new Error(`GitHub file update Release ${tag} was not visible after creation.`);
    const indexed = requireExactAssetSet(release, assets, expectedNames);
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-file-update-github-verify-'));
    try {
      for (const asset of assets) await verifyRemoteAsset(tag, asset, indexed.get(asset.name), githubCli, tempRoot);
    } finally {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    }
  } else {
    let indexed = indexReleaseAssets(release, expectedNames);
    const existingTempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-file-update-github-check-'));
    try {
      for (const asset of assets) {
        const existing = indexed.get(asset.name);
        if (existing) await verifyRemoteAsset(tag, asset, existing, githubCli, existingTempRoot);
      }
    } finally {
      fs.rmSync(existingTempRoot, { recursive: true, force: true });
    }
    const missing = assets.filter((asset) => !indexed.has(asset.name));
    if (missing.length > 0) {
      if (!release.isDraft || release.isImmutable === true) {
        throw new Error(`Published GitHub file update Release ${tag} is missing assets; it will not be modified.`);
      }
      githubCli(['release', 'upload', tag, ...missing.map((asset) => asset.path)]);
    }
    if (release.isDraft) githubCli(['release', 'edit', tag, '--draft=false']);
    release = readRelease(tag, githubCli);
    if (!release) throw new Error(`GitHub file update Release ${tag} disappeared during publication.`);
    indexed = requireExactAssetSet(release, assets, expectedNames);
    if (missing.length > 0) {
      const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-file-update-github-verify-'));
      try {
        for (const asset of missing) await verifyRemoteAsset(tag, asset, indexed.get(asset.name), githubCli, tempRoot);
      } finally {
        fs.rmSync(tempRoot, { recursive: true, force: true });
      }
    }
  }
  if (release.isDraft) throw new Error(`GitHub file update Release ${tag} remains a draft after publication.`);
  return {
    tag,
    assets: assets.map((asset) => ({ name: asset.name, size: asset.size, sha256: asset.sha256 })),
    alreadyPublished
  };
}

async function main() {
  const [tag, version, ...argumentsAfterVersion] = process.argv.slice(2);
  if (!tag || !version || ![3, 4, 5, 6].includes(argumentsAfterVersion.length)) {
    throw new Error('Usage: node scripts/publish-github-release.cjs <tag> <version> <student.exe> <teacher.exe> <SHA256SUMS> [release-notes.md] [legacy-student.zip legacy-teacher.zip].');
  }
  const hasLegacyBundles = argumentsAfterVersion.length >= 5;
  const result = await publishGitHubRelease({
    tag,
    version,
    assetPaths: argumentsAfterVersion.slice(0, hasLegacyBundles ? 5 : 3),
    notesPath: hasLegacyBundles ? argumentsAfterVersion[5] || null : argumentsAfterVersion[3] || null
  });
  process.stdout.write(JSON.stringify(result) + '\n');
}

if (require.main === module) {
  main().catch(error => {
    process.stderr.write(`GitHub Release publication failed: ${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = {
  publishGitHubRelease,
  publishGitHubFileUpdateRelease,
  validateArguments,
  parseFileUpdateTag,
  validateFileUpdateAssetPaths
};

'use strict';

const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createReadStream } = require('node:fs');

const MAX_RELEASE_ASSET_BYTES = 512 * 1024 * 1024;

function defaultGitHubCli(args) {
  return execFileSync('gh', args, {
    encoding: 'utf8',
    maxBuffer: 16 * 1024 * 1024,
    stdio: ['ignore', 'pipe', 'pipe']
  });
}

function expectedAssetNames(version) {
  return [
    `VeyonCampus-Student-Setup-${version}-win-x64.exe`,
    `VeyonCampus-Teacher-Setup-${version}-win-x64.exe`,
    'SHA256SUMS'
  ];
}

function validateArguments(tag, version, assetPaths) {
  if (!/^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(tag) ||
      tag !== `v${version}` || assetPaths.length !== 3) {
    throw new Error('Expected a stable vX.Y.Z tag and its Student, Teacher, and SHA256SUMS files.');
  }
  const expected = expectedAssetNames(version).sort();
  const actual = assetPaths.map(filePath => path.basename(filePath)).sort();
  if (actual.length !== expected.length || actual.some((name, index) => name !== expected[index])) {
    throw new Error('Pass the version-matched Student installer, Teacher installer, and SHA256SUMS exactly once.');
  }
}

async function sha256File(filePath) {
  const hash = crypto.createHash('sha256');
  for await (const chunk of createReadStream(filePath)) hash.update(chunk);
  return hash.digest('hex');
}

async function inspectLocalAssets(assetPaths) {
  const assets = await Promise.all(assetPaths.map(async filePath => {
    const absolutePath = path.resolve(filePath);
    const info = await fs.promises.lstat(absolutePath);
    if (!info.isFile() || info.isSymbolicLink() || info.size < 1 || info.size > MAX_RELEASE_ASSET_BYTES) {
      throw new Error(`Release asset is missing, empty, or larger than 512 MiB: ${path.basename(filePath)}.`);
    }
    return {
      path: absolutePath,
      name: path.basename(absolutePath),
      size: info.size,
      sha256: await sha256File(absolutePath)
    };
  }));
  const checksumAsset = assets.find(asset => asset.name === 'SHA256SUMS');
  if (checksumAsset.size > 8192) throw new Error('SHA256SUMS is unexpectedly large.');
  validateChecksums(await fs.promises.readFile(checksumAsset.path, 'utf8'), assets);
  return assets;
}

function validateChecksums(checksumText, assets) {
  const expected = new Map(assets.filter(asset => asset.name !== 'SHA256SUMS')
    .map(asset => [asset.name, asset.sha256]));
  const lines = checksumText.trimEnd().split(/\r?\n/);
  if (lines.length !== expected.size) throw new Error('SHA256SUMS must contain exactly one record for each installer.');
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
    output = githubCli(['release', 'view', tag, '--json', 'tagName,assets,isDraft,isImmutable']);
  } catch (error) {
    const detail = `${error?.stderr || ''} ${error?.message || ''}`;
    if (/release not found|HTTP 404|404 Not Found/i.test(detail)) return null;
    throw new Error(`Could not inspect GitHub Release ${tag}; no assets were changed.`);
  }
  let release;
  try { release = JSON.parse(output); }
  catch { throw new Error(`GitHub returned invalid Release metadata for ${tag}.`); }
  if (release?.tagName !== tag || !Array.isArray(release.assets) || typeof release.isDraft !== 'boolean') {
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

async function publishGitHubRelease({ tag, version, assetPaths, githubCli = defaultGitHubCli }) {
  validateArguments(tag, version, assetPaths);
  const assets = await inspectLocalAssets(assetPaths);
  const expectedNames = new Set(assets.map(asset => asset.name));
  let release = readRelease(tag, githubCli);
  let alreadyPublished = Boolean(release && !release.isDraft);

  if (!release) {
    githubCli([
      'release', 'create', tag,
      ...assets.map(asset => asset.path),
      '--title', `Veyon Campus ${version}`,
      '--generate-notes',
      '--verify-tag'
    ]);
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

async function main() {
  const [tag, version, ...assetPaths] = process.argv.slice(2);
  if (!tag || !version || assetPaths.length !== 3) {
    throw new Error('Usage: node scripts/publish-github-release.cjs <tag> <version> <student.exe> <teacher.exe> <SHA256SUMS>.');
  }
  const result = await publishGitHubRelease({ tag, version, assetPaths });
  process.stdout.write(JSON.stringify(result) + '\n');
}

if (require.main === module) {
  main().catch(error => {
    process.stderr.write(`GitHub Release publication failed: ${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = { publishGitHubRelease, validateArguments };

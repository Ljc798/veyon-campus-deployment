'use strict';

const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const https = require('node:https');
const path = require('node:path');
const { createReadStream } = require('node:fs');

const API_BASE = 'https://api.gitcode.com/api/v5';
const MAX_RELEASE_ASSET_BYTES = 1_900_000_000;
const RELEASE_TRANSFER_TIMEOUT_MS = 60 * 60 * 1000;
const SEMVER_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

function required(name, environment = process.env) {
  const value = environment[name]?.trim();
  if (!value) throw new Error(`Missing required environment variable ${name}.`);
  return value;
}

function pathSegment(value, name) {
  if (!/^[A-Za-z0-9_.-]+$/.test(value)) throw new Error(`${name} contains unsupported characters.`);
  return encodeURIComponent(value);
}

function parseReleaseTag(tag) {
  const installer = /^(beta-)?v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.exec(tag || '');
  if (installer) return {
    version: `${installer[2]}.${installer[3]}.${installer[4]}`,
    prerelease: Boolean(installer[1]),
    kind: 'installer'
  };
  const fileUpdate = /^file-(beta-)?(student|teacher)-v(.+)$/.exec(tag || '');
  const fileVersion = fileUpdate && SEMVER_PATTERN.exec(fileUpdate[3]);
  if (fileUpdate && fileVersion && (fileUpdate[1] || !fileVersion[4])) return {
    version: fileUpdate[3],
    prerelease: Boolean(fileUpdate[1]),
    kind: 'file-update',
    role: fileUpdate[2] === 'student' ? 'StudentSetup' : 'TeacherConsole'
  };
  throw new Error('GitCode release tag must use vX.Y.Z, beta-vX.Y.Z, file-student-vSEMVER, file-teacher-vSEMVER, or their file-beta variants.');
}

function readRemoteTagCommit(owner, repo, tag, exec = execFileSync) {
  const remote = `https://gitcode.com/${decodeURIComponent(owner)}/${decodeURIComponent(repo)}.git`;
  let output;
  try {
    output = exec('git', ['ls-remote', remote, `refs/tags/${tag}`, `refs/tags/${tag}^{}`], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe']
    });
  } catch (error) {
    const detail = error?.stderr?.toString?.().trim() || error?.message || 'unknown Git error';
    throw new Error(`Could not verify GitCode source tag ${tag}: ${detail}`, { cause: error });
  }
  const refs = new Map(output.split(/\r?\n/).filter(Boolean).map(line => {
    const [sha, ref] = line.trim().split(/\s+/);
    return [ref, sha];
  }));
  return refs.get(`refs/tags/${tag}^{}`) || refs.get(`refs/tags/${tag}`) || null;
}

function sameFileIdentity(left, right) {
  return left.isFile() && right.isFile() && left.size === right.size && left.dev === right.dev &&
    left.ino === right.ino && left.mtimeMs === right.mtimeMs && left.nlink === right.nlink;
}

async function hashFile(filePath) {
  const hash = crypto.createHash('sha256');
  for await (const chunk of createReadStream(filePath)) hash.update(chunk);
  return hash.digest('hex');
}

async function request(endpoint, token, options = {}, fetchImpl = global.fetch) {
  const method = options.method || 'GET';
  let response;
  try {
    response = await fetchImpl(`${API_BASE}${endpoint}`, {
      method,
      headers: {
        Accept: 'application/json',
        Authorization: `Bearer ${token}`,
        ...(options.headers || {})
      },
      body: options.body,
      signal: AbortSignal.timeout(options.timeoutMs || 30000),
      redirect: 'error'
    });
  } catch (error) {
    const detail = error?.cause?.code || error?.cause?.message || error?.message || 'unknown network error';
    throw new Error(`GitCode API request failed (${method} ${endpoint}): ${detail}`, { cause: error });
  }
  if (options.allowNotFound && response.status === 404) return null;
  if (!response.ok) {
    const detail = await response.text().catch(() => '');
    throw new Error(`GitCode API request failed with HTTP ${response.status} (${method} ${endpoint})${detail ? `: ${detail.slice(0, 384)}` : ''}.`);
  }
  if (response.status === 204) return null;
  try {
    return await response.json();
  } catch {
    throw new Error(`GitCode API returned invalid JSON (${method} ${endpoint}).`);
  }
}

async function inspectAssets(assetPaths) {
  if (!assetPaths.length) throw new Error('At least one release asset is required.');
  const assets = [];
  const names = new Set();
  for (const assetPath of assetPaths) {
    const absolutePath = path.resolve(assetPath);
    const info = await fs.promises.lstat(absolutePath);
    if (!info.isFile() || info.isSymbolicLink() || info.nlink > 1 || info.size < 1 || info.size > MAX_RELEASE_ASSET_BYTES) {
      throw new Error(`Release asset is missing, linked, empty, or larger than 1.9 GB: ${path.basename(assetPath)}.`);
    }
    const name = path.basename(absolutePath);
    if (names.has(name)) throw new Error(`Duplicate GitCode Release asset name: ${name}.`);
    names.add(name);
    const digest = await hashFile(absolutePath);
    const after = await fs.promises.lstat(absolutePath);
    if (!sameFileIdentity(info, after)) throw new Error(`Release asset changed while it was being hashed: ${name}.`);
    assets.push({ path: absolutePath, name, size: info.size, sha256: digest });
  }
  return assets;
}

function validateFileUpdateAssets(parsedTag, version, assets) {
  if (parsedTag.kind !== 'file-update') return;
  if (parsedTag.version !== version || assets.length < 3 || assets.length > 34)
    throw new Error('File update Release must match its role/version tag and contain 3 to 34 assets.');
  const roleName = parsedTag.role;
  const manifestName = `VeyonCampus-${roleName}-${version}-win-x64.file-manifest.json`;
  const signatureName = manifestName.replace(/\.json$/, '.sig');
  const expected = new Set([manifestName, signatureName]);
  let packCount = 0;
  for (const asset of assets) {
    if (asset.name.startsWith(`VeyonCampus-${roleName}-${version}-component-`) &&
        /^VeyonCampus-(?:TeacherConsole|StudentSetup)-.+-component-[A-Za-z][A-Za-z0-9._-]{0,63}\.zip$/.test(asset.name)) {
      expected.add(asset.name);
      packCount++;
    }
  }
  if (packCount < 1 || expected.size !== assets.length || assets.some((asset) => !expected.has(asset.name)))
    throw new Error('File update Release assets must contain only the role manifest, signature, and component packs.');
}

function releaseAssets(release) {
  if (!Array.isArray(release?.assets)) throw new Error('GitCode did not return the Release asset list.');
  const result = new Map();
  for (const asset of release.assets) {
    if (typeof asset?.name !== 'string' || !asset.name)
      throw new Error('GitCode returned a Release asset without a valid name.');
    if (result.has(asset.name)) throw new Error(`GitCode returned duplicate Release assets named ${asset.name}.`);
    result.set(asset.name, asset);
  }
  return result;
}

function isPrerelease(release) {
  return release.prerelease === true || release.release_status === 'pre';
}

async function getRelease(repoPath, tag, token, fetchImpl) {
  return request(`${repoPath}/releases/tags/${encodeURIComponent(tag)}`, token,
    { allowNotFound: true }, fetchImpl);
}

async function waitForRelease({ repoPath, tag, token, fetchImpl }) {
  for (let attempt = 0; attempt < 8; attempt++) {
    const release = await getRelease(repoPath, tag, token, fetchImpl);
    if (release) return release;
    if (attempt === 7) break;
    const delayMs = Math.min(1000 * (2 ** attempt), 15000);
    process.stdout.write(`GitCode Release ${tag} is not visible yet; checking again in ${Math.ceil(delayMs / 1000)} seconds...\n`);
    await new Promise(resolve => setTimeout(resolve, delayMs));
  }
  throw new Error(`GitCode Release ${tag} was not visible through the Release API; refusing to upload assets.`);
}

async function createOrVerifyRelease({
  repoPath, owner, repo, tag, version, targetCommitish, notes, prerelease, token, fetchImpl
}) {
  const fileTag = /^file-(beta-)?(student|teacher)-/.exec(tag);
  const roleName = fileTag?.[2] === 'student' ? 'Student' : 'Teacher';
  const releaseName = fileTag
    ? `Veyon Campus ${roleName} File Update ${version}${fileTag[1] ? ' Beta' : ''}`
    : `Veyon Campus ${version}${prerelease ? ' Beta' : ''}`;
  let release = await getRelease(repoPath, tag, token, fetchImpl);
  if (!release) {
    release = await request(repoPath + '/releases', token, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        tag_name: tag,
        name: releaseName,
        body: notes,
        target_commitish: targetCommitish,
        release_status: prerelease ? 'pre' : 'latest'
      })
    }, fetchImpl);
  }
  if (release.tag_name !== tag || release.target_commitish?.toLowerCase() !== targetCommitish.toLowerCase() ||
      isPrerelease(release) !== prerelease) {
    throw new Error(`Existing GitCode Release ${tag} does not match the expected source commit or release status.`);
  }
  if (release.name !== releaseName || release.body !== notes) {
    if (fileTag) throw new Error(`Existing GitCode file update Release ${tag} has different metadata and is immutable.`);
    release = await request(`${repoPath}/releases/${encodeURIComponent(tag)}`, token, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ tag_name: tag, name: releaseName, body: notes,
        release_status: prerelease ? 'pre' : 'latest' })
    }, fetchImpl);
    if (release.tag_name !== tag || release.name !== releaseName || release.body !== notes ||
        release.target_commitish?.toLowerCase() !== targetCommitish.toLowerCase() || isPrerelease(release) !== prerelease) {
      throw new Error(`GitCode did not save the expected metadata for Release ${tag}.`);
    }
  }
  if (release.html_url && new URL(release.html_url).hostname !== 'gitcode.com') {
    throw new Error('GitCode returned an unexpected Release page URL host.');
  }
  return {
    release,
    pageUrl: release.html_url || `https://gitcode.com/${owner}/${repo}/releases/${encodeURIComponent(tag)}`
  };
}

async function downloadAndVerify({ owner, repo, tag, asset, expected, token, fetchImpl }) {
  const endpoint = `/repos/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/releases/${encodeURIComponent(tag)}/attach_files/${encodeURIComponent(asset.name)}/download`;
  let response;
  try {
    response = await fetchImpl(`${API_BASE}${endpoint}`, {
      headers: { Authorization: `Bearer ${token}` },
      signal: AbortSignal.timeout(RELEASE_TRANSFER_TIMEOUT_MS),
      redirect: 'follow'
    });
  } catch (error) {
    const detail = error?.cause?.code || error?.cause?.message || error?.message || 'unknown network error';
    throw new Error(`Could not read back GitCode Release asset ${asset.name}: ${detail}.`, { cause: error });
  }
  if (!response.ok || !response.body) {
    throw new Error(`Could not read back GitCode Release asset ${asset.name} (HTTP ${response.status}).`);
  }
  const hash = crypto.createHash('sha256');
  let size = 0;
  for await (const chunk of response.body) {
    size += chunk.length;
    if (size > expected.size) throw new Error(`GitCode Release asset ${asset.name} is larger than the local build.`);
    hash.update(chunk);
  }
  if (size !== expected.size || hash.digest('hex') !== expected.sha256) {
    throw new Error(`GitCode Release asset ${asset.name} differs from the local build.`);
  }
}

function uploadWithHttps(url, headers, filePath, expectedSize, expectedSha256,
  timeoutMs = RELEASE_TRANSFER_TIMEOUT_MS, requestFactory = https.request) {
  const target = new URL(url);
  if (target.protocol !== 'https:') throw new Error('GitCode returned a non-HTTPS upload target.');

  const requestHeaders = { ...headers };
  for (const name of Object.keys(requestHeaders)) {
    if (name.toLowerCase() === 'content-length') delete requestHeaders[name];
  }
  requestHeaders['Content-Length'] = String(expectedSize);

  return new Promise((resolve, reject) => {
    let settled = false;
    let timeout;
    let uploadedBytes = 0;
    const hash = crypto.createHash('sha256');
    const source = createReadStream(filePath);
    const failUpload = (error) => {
      finish(error);
      source.destroy();
    };
    const finish = (error, statusCode) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      if (error) reject(error);
      else resolve(statusCode);
    };

    source.on('data', (chunk) => {
      uploadedBytes += chunk.length;
      hash.update(chunk);
    });
    source.on('error', failUpload);

    const uploadRequest = requestFactory(target, {
      method: 'PUT',
      headers: requestHeaders
    }, response => {
      response.on('error', failUpload);
      response.on('aborted', () => failUpload(new Error('GitCode upload response ended unexpectedly.')));
      response.on('end', () => {
        if (typeof response.statusCode !== 'number' || response.statusCode < 200 || response.statusCode >= 300) {
          failUpload(new Error('GitCode upload returned HTTP ' + (response.statusCode || 'unknown') + '.'));
          return;
        }
        if (uploadedBytes !== expectedSize || hash.digest('hex') !== expectedSha256) {
          failUpload(new Error('GitCode upload source changed or did not match its verified size and SHA-256.'));
          return;
        }
        finish(null, response.statusCode);
      });
      response.resume();
    });

    timeout = setTimeout(() => {
      const error = new Error('GitCode upload exceeded the 60-minute timeout.');
      error.code = 'ETIMEDOUT';
      uploadRequest.destroy(error);
    }, timeoutMs);
    uploadRequest.on('error', failUpload);
    source.pipe(uploadRequest);
  });
}

async function uploadAsset({ repoPath, tag, asset, token, fetchImpl, uploadWithHttpsImpl = uploadWithHttps }) {
  const endpoint = repoPath + '/releases/' + encodeURIComponent(tag) + '/upload_url?file_name=' + encodeURIComponent(asset.name);
  const upload = await request(endpoint, token, {}, fetchImpl);
  if (typeof upload?.url !== 'string' || !upload.url.startsWith('https://') ||
      !upload.headers || typeof upload.headers !== 'object') {
    throw new Error('GitCode did not return a valid upload target for ' + asset.name + '.');
  }
  process.stdout.write('Uploading ' + asset.name + ' (' + asset.size + ' bytes) to GitCode...\n');
  let statusCode;
  try {
    statusCode = await uploadWithHttpsImpl(upload.url, upload.headers, asset.path,
      asset.size, asset.sha256, RELEASE_TRANSFER_TIMEOUT_MS);
  } catch (error) {
    const detail = error?.cause?.code || error?.cause?.message || error?.message || 'unknown upload error';
    throw new Error('GitCode upload failed for ' + asset.name + ': ' + detail + '.', { cause: error });
  }
  process.stdout.write('GitCode accepted ' + asset.name + ' (HTTP ' + statusCode + ').\n');
}

async function publishGitCodeRelease(options = {}) {
  const args = options.args ?? process.argv.slice(2);
  const environment = options.environment ?? process.env;
  const fetchImpl = options.fetchImpl ?? global.fetch;
  const uploadWithHttpsImpl = options.uploadWithHttpsImpl ?? uploadWithHttps;
  const writeOutput = options.writeOutput ?? (value => process.stdout.write(value));
  const [tag, version, targetCommitish, notesPath, ...assetPaths] = args;
  if (!tag || !version || !targetCommitish || !notesPath || assetPaths.length === 0) {
    throw new Error('Usage: node scripts/publish-gitcode-release.cjs <tag> <version> <commit-sha> <release-notes-file> <asset>...');
  }
  const parsed = parseReleaseTag(tag);
  if (parsed.version !== version || !/^[0-9a-f]{40}$/i.test(targetCommitish)) {
    throw new Error('GitCode release version or source commit does not match the tag.');
  }
  const owner = pathSegment(required('GITCODE_OWNER', environment), 'GITCODE_OWNER');
  const repo = pathSegment(required('GITCODE_REPO', environment), 'GITCODE_REPO');
  const token = required('GITCODE_TOKEN', environment);
  const lookupTagCommit = options.lookupTagCommit ?? readRemoteTagCommit;
  const existingTagCommit = await lookupTagCommit(owner, repo, tag);
  if (existingTagCommit && existingTagCommit.toLowerCase() !== targetCommitish.toLowerCase()) {
    throw new Error(`GitCode source tag ${tag} does not match the expected commit ${targetCommitish}.`);
  }

  const notesInfo = await fs.promises.lstat(path.resolve(notesPath));
  if (!notesInfo.isFile() || notesInfo.isSymbolicLink() || notesInfo.size < 1) {
    throw new Error('GitCode Release notes are missing, empty, or not a regular file.');
  }
  const notes = await fs.promises.readFile(notesPath, 'utf8');
  const assets = await inspectAssets(assetPaths);
  validateFileUpdateAssets(parsed, version, assets);
  const expectedAssetNames = new Set(assets.map((asset) => asset.name));
  const repoPath = `/repos/${owner}/${repo}`;
  const releaseInfo = await createOrVerifyRelease({
    repoPath, owner: decodeURIComponent(owner), repo: decodeURIComponent(repo), tag, version,
    targetCommitish, notes, prerelease: parsed.prerelease, token, fetchImpl
  });
  const publishedTagCommit = await lookupTagCommit(owner, repo, tag);
  if (!publishedTagCommit || publishedTagCommit.toLowerCase() !== targetCommitish.toLowerCase()) {
    throw new Error(`GitCode source tag ${tag} was not created at the expected commit ${targetCommitish}.`);
  }
  const confirmedRelease = await waitForRelease({ repoPath, tag, token, fetchImpl });
  if (confirmedRelease.tag_name !== tag || confirmedRelease.target_commitish?.toLowerCase() !== targetCommitish.toLowerCase() ||
      isPrerelease(confirmedRelease) !== parsed.prerelease) {
    throw new Error(`GitCode Release ${tag} does not match the expected source commit or release status.`);
  }
  let indexed = releaseAssets(confirmedRelease);
  if (parsed.kind === 'file-update' && [...indexed.keys()].some((name) => !expectedAssetNames.has(name)))
    throw new Error(`GitCode file update Release ${tag} contains unexpected assets and is immutable.`);

  for (const asset of assets) {
    const existing = indexed.get(asset.name);
    if (!existing) {
      await uploadAsset({ repoPath, tag, asset, token, fetchImpl, uploadWithHttpsImpl });
    }
    let verified = false;
    for (let attempt = 0; attempt < 8; attempt++) {
      const updated = await getRelease(repoPath, tag, token, fetchImpl);
      if (updated) {
        indexed = releaseAssets(updated);
        const remoteAsset = indexed.get(asset.name);
        if (remoteAsset) {
          await downloadAndVerify({ owner: decodeURIComponent(owner), repo: decodeURIComponent(repo),
            tag, asset: remoteAsset, expected: asset, token, fetchImpl });
          verified = true;
          break;
        }
      }
      if (attempt === 7) break;
      const delayMs = Math.min(1000 * (2 ** attempt), 15000);
      process.stdout.write(`GitCode has not exposed ${asset.name} yet; checking again in ${Math.ceil(delayMs / 1000)} seconds...\n`);
      await new Promise(resolve => setTimeout(resolve, delayMs));
    }
    if (!verified) throw new Error(`GitCode did not expose uploaded Release asset ${asset.name} after retries.`);
  }

  if (parsed.kind === 'file-update' &&
      (indexed.size !== expectedAssetNames.size || [...indexed.keys()].some((name) => !expectedAssetNames.has(name)))) {
    throw new Error(`GitCode file update Release ${tag} does not contain exactly the verified role assets.`);
  }

  const result = {
    tag,
    version,
    repository: `${decodeURIComponent(owner)}/${decodeURIComponent(repo)}`,
    releasePageUrl: releaseInfo.pageUrl,
    assets: assets.map(({ name, size, sha256: digest }) => ({ name, size, sha256: digest }))
  };
  const output = JSON.stringify(result, null, 2) + '\n';
  if (environment.GITHUB_OUTPUT) {
    await fs.promises.appendFile(environment.GITHUB_OUTPUT, `release_page_url=${releaseInfo.pageUrl}\n`, 'utf8');
  }
  writeOutput(output);
  return result;
}

if (require.main === module) {
  publishGitCodeRelease().catch(error => {
    process.stderr.write(`GitCode release publication failed: ${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = {
  publishGitCodeRelease,
  parseReleaseTag,
  readRemoteTagCommit,
  inspectAssets,
  uploadWithHttps
};

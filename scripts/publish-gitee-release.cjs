'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { createReadStream } = require('node:fs');

const API_BASE = 'https://gitee.com/api/v5';
const MAX_RELEASE_ASSET_BYTES = 512 * 1024 * 1024;
const VERSION_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

function required(name) {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`Missing required environment variable ${name}.`);
  return value;
}

function repositoryPathSegment(value, name) {
  if (!/^[A-Za-z0-9_.-]+$/.test(value)) throw new Error(`${name} contains unsupported characters.`);
  return encodeURIComponent(value);
}

function readArray(payload, endpoint) {
  if (Array.isArray(payload)) return payload;
  for (const key of ['assets', 'files', 'attach_files']) {
    if (Array.isArray(payload?.[key])) return payload[key];
  }
  throw new Error(`Gitee returned an unexpected attachment list for ${endpoint}.`);
}

async function request(endpoint, token, options = {}) {
  const response = await fetch(`${API_BASE}${endpoint}`, {
    method: options.method || 'GET',
    headers: {
      Accept: 'application/json',
      Authorization: `Bearer ${token}`,
      ...(options.headers || {})
    },
    body: options.body,
    signal: AbortSignal.timeout(options.timeoutMs || 30000),
    redirect: 'error'
  });
  if (options.allowNotFound && response.status === 404) return null;
  if (!response.ok) {
    const detail = await response.text().catch(() => '');
    throw new Error(`Gitee API request failed with HTTP ${response.status} (${options.method || 'GET'} ${endpoint})${detail ? `: ${detail.slice(0, 384)}` : ''}.`);
  }
  if (response.status === 204) return null;
  try {
    return await response.json();
  } catch {
    throw new Error(`Gitee API returned invalid JSON (${options.method || 'GET'} ${endpoint}).`);
  }
}

async function computeFileSha256(filePath) {
  const hash = crypto.createHash('sha256');
  for await (const chunk of createReadStream(filePath)) hash.update(chunk);
  return hash.digest('hex');
}

async function downloadAttachmentDigest(endpoint, token, maxBytes) {
  const response = await fetch(`${API_BASE}${endpoint}`, {
    headers: { Accept: 'application/octet-stream', Authorization: `Bearer ${token}` },
    signal: AbortSignal.timeout(90000),
    redirect: 'follow'
  });
  if (!response.ok) throw new Error(`Gitee checksum attachment download failed with HTTP ${response.status}.`);
  const reader = response.body?.getReader();
  if (!reader) throw new Error('Gitee release attachment response had no body.');
  const hash = crypto.createHash('sha256');
  let size = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > maxBytes) {
      await reader.cancel();
      throw new Error('Gitee release attachment exceeded the expected file size.');
    }
    hash.update(value);
  }
  return { size, sha256: hash.digest('hex') };
}

function validateRelease(tag, version, targetCommitish, assets) {
  if (!/^v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$/.test(tag) ||
      tag.slice(1) !== version || !VERSION_PATTERN.test(version)) {
    throw new Error('Gitee release tag must be vX.Y.Z and match the installer version.');
  }
  if (!/^[0-9a-f]{40}$/i.test(targetCommitish)) throw new Error('GITHUB_SHA must be a full commit SHA.');
  const expectedAssets = [
    `VeyonCampus-Student-Setup-${version}-win-x64.exe`,
    `VeyonCampus-Teacher-Setup-${version}-win-x64.exe`,
    'SHA256SUMS'
  ].sort();
  const providedAssets = assets.map(asset => path.basename(asset)).sort();
  if (providedAssets.length !== expectedAssets.length ||
      providedAssets.some((asset, index) => asset !== expectedAssets[index])) {
    throw new Error('Pass the version-matched Student installer, Teacher installer, and SHA256SUMS exactly once.');
  }
}

function validateChecksums(checksumText, assets) {
  const expected = new Map(assets.filter(asset => asset.name !== 'SHA256SUMS')
    .map(asset => [asset.name, asset.sha256]));
  const lines = checksumText.trimEnd().split(/\r?\n/);
  if (lines.length !== expected.size) throw new Error('SHA256SUMS must contain exactly one record for each installer.');
  const actual = new Map();
  for (const line of lines) {
    const match = /^([0-9a-f]{64})  (.+)$/.exec(line);
    if (!match || actual.has(match[2])) throw new Error('SHA256SUMS has an invalid or duplicate record.');
    actual.set(match[2], match[1]);
  }
  if (actual.size !== expected.size || [...expected].some(([name, hash]) => actual.get(name) !== hash)) {
    throw new Error('SHA256SUMS does not match the installer files in this build.');
  }
}

function attachmentName(attachment) {
  return attachment.name || attachment.filename || null;
}

function indexAttachments(attachments) {
  const result = new Map();
  for (const attachment of attachments) {
    const name = attachmentName(attachment);
    if (!name) continue;
    if (result.has(name)) throw new Error(`Gitee has duplicate release attachments named ${name}.`);
    result.set(name, attachment);
  }
  return result;
}

async function verifyExistingAttachment(attachment, asset, attachmentsPath, token) {
  if (!attachment?.id) throw new Error(`Gitee did not expose an attachment id for ${asset.name}.`);
  const remote = await downloadAttachmentDigest(
    `${attachmentsPath}/${encodeURIComponent(attachment.id)}/download`, token, asset.size
  );
  if (remote.size !== asset.size || remote.sha256 !== asset.sha256) {
    throw new Error(`The existing Gitee attachment ${asset.name} differs from this build; release assets are immutable.`);
  }
}

async function publish() {
  const [tag, notesPath, ...assetPaths] = process.argv.slice(2);
  if (!tag || !notesPath || assetPaths.length !== 3) {
    throw new Error('Usage: node scripts/publish-gitee-release.cjs <tag> <release-notes-file> <student.exe> <teacher.exe> <SHA256SUMS>.');
  }
  const version = tag.startsWith('v') ? tag.slice(1) : '';
  const targetCommitish = required('GITHUB_SHA');
  validateRelease(tag, version, targetCommitish, assetPaths);
  const owner = repositoryPathSegment(required('GITEE_OWNER'), 'GITEE_OWNER');
  const repo = repositoryPathSegment(required('GITEE_REPO'), 'GITEE_REPO');
  const token = required('GITEE_TOKEN');
  const notes = await fs.promises.readFile(path.resolve(notesPath), 'utf8');
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
      sha256: await computeFileSha256(absolutePath)
    };
  }));
  const checksumsAsset = assets.find(asset => asset.name === 'SHA256SUMS');
  if (checksumsAsset.size > 8192) throw new Error('SHA256SUMS is unexpectedly large.');
  validateChecksums(await fs.promises.readFile(checksumsAsset.path, 'utf8'), assets);

  const repoPath = `/repos/${owner}/${repo}`;
  let release = await request(`${repoPath}/releases/tags/${encodeURIComponent(tag)}`, token, { allowNotFound: true });
  if (!release) {
    const createBody = {
      tag_name: tag,
      name: `Veyon Campus ${version}`,
      body: notes,
      target_commitish: targetCommitish,
      prerelease: false
    };
    release = await request(`${repoPath}/releases`, token, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(createBody)
    });
  }
  if (!Number.isSafeInteger(Number(release?.id)) || release.tag_name !== tag) {
    throw new Error('Gitee did not return the expected release id and tag.');
  }
  if (typeof release.target_commitish === 'string' && /^[0-9a-f]{40}$/i.test(release.target_commitish) &&
      release.target_commitish.toLowerCase() !== targetCommitish.toLowerCase()) {
    throw new Error('The existing Gitee release tag points to a different commit.');
  }

  const attachmentsPath = `${repoPath}/releases/${encodeURIComponent(release.id)}/attach_files`;
  let attachments = readArray(await request(attachmentsPath, token), attachmentsPath);
  let indexed = indexAttachments(attachments);
  const expectedNames = new Set(assets.map(asset => asset.name));
  for (const name of indexed.keys()) {
    if (!expectedNames.has(name)) throw new Error(`Gitee release contains an unexpected attachment: ${name}.`);
  }
  for (const asset of assets) {
    const existing = indexed.get(asset.name);
    if (existing) await verifyExistingAttachment(existing, asset, attachmentsPath, token);
  }
  if (assets.every(asset => indexed.has(asset.name))) {
    process.stdout.write(JSON.stringify({ releaseId: release.id, tag, repository: `${decodeURIComponent(owner)}/${decodeURIComponent(repo)}`,
      assets: assets.map(asset => asset.name), alreadyPublished: true }, null, 2) + '\n');
    return;
  }

  for (const asset of assets) {
    if (indexed.has(asset.name)) continue;
    const bytes = await fs.promises.readFile(asset.path);
    const form = new FormData();
    form.append('file', new Blob([bytes], { type: 'application/octet-stream' }), asset.name);
    await request(attachmentsPath, token, { method: 'POST', body: form, timeoutMs: 15 * 60 * 1000 });
  }
  attachments = readArray(await request(attachmentsPath, token), attachmentsPath);
  indexed = indexAttachments(attachments);
  if (indexed.size !== assets.length || !assets.every(asset => indexed.has(asset.name))) {
    throw new Error('Gitee did not retain exactly the expected release assets.');
  }
  for (const asset of assets) {
    const remoteSize = Number(indexed.get(asset.name).size);
    if (Number.isFinite(remoteSize) && remoteSize !== asset.size) {
      throw new Error(`Gitee recorded the wrong file size for ${asset.name}.`);
    }
  }
  const checksumRecord = indexed.get('SHA256SUMS');
  await verifyExistingAttachment(checksumRecord, checksumsAsset, attachmentsPath, token);
  process.stdout.write(JSON.stringify({ releaseId: release.id, tag, repository: `${decodeURIComponent(owner)}/${decodeURIComponent(repo)}`,
    assets: assets.map(asset => asset.name), alreadyPublished: false }, null, 2) + '\n');
}

if (require.main === module) {
  publish().catch(error => {
    process.stderr.write(`Gitee release publication failed: ${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = { validateRelease };

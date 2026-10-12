'use strict';

const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createReadStream } = require('node:fs');

const API_BASE = 'https://gitee.com/api/v5';
const MAX_RELEASE_ASSET_BYTES = 512 * 1024 * 1024;
const MAX_GITEE_ATTACHMENT_BYTES = 90_000_000;
const VERSION_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

function required(name, environment = process.env) {
  const value = environment[name]?.trim();
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

function readRemoteTagCommit(owner, repo, tag, exec = execFileSync) {
  const remote = `https://gitee.com/${decodeURIComponent(owner)}/${decodeURIComponent(repo)}.git`;
  let output;
  try {
    output = exec('git', ['ls-remote', remote, `refs/tags/${tag}`, `refs/tags/${tag}^{}`], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe']
    });
  } catch (error) {
    const detail = error?.stderr?.toString?.().trim() || error?.message || 'unknown Git error';
    throw new Error(`Could not verify Gitee source tag ${tag}: ${detail}`, { cause: error });
  }
  const refs = new Map(output.split(/\r?\n/).filter(Boolean).map(line => {
    const [sha, ref] = line.trim().split(/\s+/);
    return [ref, sha];
  }));
  return refs.get(`refs/tags/${tag}^{}`) || refs.get(`refs/tags/${tag}`) || null;
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
    const cause = error?.cause;
    const detail = cause?.code || cause?.message || error?.message || 'unknown network error';
    throw new Error(`Gitee API network request failed (${method} ${endpoint}): ${detail}`, { cause: error });
  }
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

async function downloadAttachmentDigest(endpoint, token, maxBytes, fetchImpl = global.fetch, aggregateHash = null) {
  let response;
  try {
    response = await fetchImpl(`${API_BASE}${endpoint}`, {
      headers: { Accept: 'application/octet-stream', Authorization: `Bearer ${token}` },
      signal: AbortSignal.timeout(90000),
      redirect: 'follow'
    });
  } catch (error) {
    const cause = error?.cause;
    const detail = cause?.code || cause?.message || error?.message || 'unknown network error';
    throw new Error(`Gitee attachment download failed (${endpoint}): ${detail}`, { cause: error });
  }
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
    aggregateHash?.update(value);
  }
  return { size, sha256: hash.digest('hex') };
}

function validateRelease(tag, version, targetCommitish, assets) {
  const isPrerelease = tag.startsWith('beta-v');
  const expectedTag = `${isPrerelease ? 'beta-' : ''}v${version}`;
  if (!/^(?:beta-)?v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$/.test(tag) ||
      tag !== expectedTag || !VERSION_PATTERN.test(version)) {
    throw new Error('Gitee release tag must be vX.Y.Z or beta-vX.Y.Z and match the installer version.');
  }
  if (!/^[0-9a-f]{40}$/i.test(targetCommitish)) throw new Error('GITHUB_SHA must be a full commit SHA.');
  const expectedAssets = [
    `VeyonCampus-Student-Setup-${version}-win-x64.exe`,
    `VeyonCampus-Teacher-Setup-${version}-win-x64.exe`,
    'SHA256SUMS'
  ];
  if (isPrerelease) expectedAssets.push('join-gitee-beta-installers.ps1');
  expectedAssets.sort();
  const providedAssets = assets.map(asset => path.basename(asset)).sort();
  if (providedAssets.length !== expectedAssets.length ||
      providedAssets.some((asset, index) => asset !== expectedAssets[index])) {
    throw new Error(isPrerelease
      ? 'Pass the version-matched Student installer, Teacher installer, SHA256SUMS, and Gitee join helper exactly once.'
      : 'Pass the version-matched Student installer, Teacher installer, and SHA256SUMS exactly once.');
  }
}

function validateChecksums(checksumText, assets) {
  const expected = new Map(assets.filter(asset => asset.name.endsWith('.exe'))
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
    if (!name) throw new Error('Gitee returned a release attachment without a filename.');
    if (result.has(name)) throw new Error(`Gitee has duplicate release attachments named ${name}.`);
    result.set(name, attachment);
  }
  return result;
}

async function verifyExistingAttachment(attachment, asset, attachmentsPath, token, fetchImpl = global.fetch) {
  if (!attachment?.id) throw new Error(`Gitee did not expose an attachment id for ${asset.name}.`);
  const remote = await downloadAttachmentDigest(
    `${attachmentsPath}/${encodeURIComponent(attachment.id)}/download`, token, asset.size, fetchImpl
  );
  if (remote.size !== asset.size || remote.sha256 !== asset.sha256) {
    throw new Error(`The existing Gitee attachment ${asset.name} differs from this build; release assets are immutable.`);
  }
}

async function verifyReleaseNotes(release, notes, repoPath, tag, releaseName, token, fetchImpl) {
  if (release.body === notes) return release;
  const updatedRelease = await request(`${repoPath}/releases/${encodeURIComponent(release.id)}`, token, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ tag_name: tag, name: release.name || releaseName, body: notes })
  }, fetchImpl);
  const currentRelease = updatedRelease || await request(
    `${repoPath}/releases/tags/${encodeURIComponent(tag)}`, token, {}, fetchImpl
  );
  if (currentRelease?.tag_name !== tag || currentRelease.body !== notes) {
    throw new Error('Gitee did not save the expected Beta release notes.');
  }
  return currentRelease;
}

async function splitReleaseAsset(asset, maxBytes, tempDirectory) {
  if (asset.size <= maxBytes) return [asset];
  const parts = [];
  let handle = null;
  let partPath = null;
  let partSize = 0;
  let partHash = null;
  let partNumber = 0;
  const finishPart = async () => {
    if (!handle) return;
    await handle.close();
    parts.push({
      path: partPath,
      name: `${asset.name}.part${String(partNumber).padStart(3, '0')}`,
      size: partSize,
      sha256: partHash.digest('hex')
    });
    handle = null;
    partPath = null;
    partSize = 0;
    partHash = null;
  };

  try {
    for await (const chunk of createReadStream(asset.path)) {
      let offset = 0;
      while (offset < chunk.length) {
        if (!handle) {
          partNumber += 1;
          partPath = path.join(tempDirectory, `${asset.name}.part${String(partNumber).padStart(3, '0')}`);
          handle = await fs.promises.open(partPath, 'wx');
          partHash = crypto.createHash('sha256');
        }
        const length = Math.min(maxBytes - partSize, chunk.length - offset);
        const piece = chunk.subarray(offset, offset + length);
        let written = 0;
        while (written < piece.length) {
          const result = await handle.write(piece, written, piece.length - written);
          written += result.bytesWritten;
        }
        partHash.update(piece);
        partSize += piece.length;
        offset += piece.length;
        if (partSize === maxBytes) await finishPart();
      }
    }
    await finishPart();
  } catch (error) {
    if (handle) await handle.close().catch(() => {});
    throw new Error(`Could not split Gitee release asset ${asset.name}: ${error.message}`, { cause: error });
  }
  if (parts.length < 2 || parts.reduce((sum, part) => sum + part.size, 0) !== asset.size) {
    throw new Error(`Gitee split parts do not reconstruct the original size of ${asset.name}.`);
  }
  return parts;
}

async function verifyAssetGroup(group, attachments, attachmentsPath, token, fetchImpl) {
  const combinedHash = crypto.createHash('sha256');
  let combinedSize = 0;
  for (const asset of group.assets) {
    const attachment = attachments.get(asset.name);
    if (!attachment?.id) throw new Error(`Gitee did not expose an attachment id for ${asset.name}.`);
    const remote = await downloadAttachmentDigest(
      `${attachmentsPath}/${encodeURIComponent(attachment.id)}/download`, token, asset.size, fetchImpl, combinedHash
    );
    if (remote.size !== asset.size || remote.sha256 !== asset.sha256) {
      throw new Error(`Gitee attachment ${asset.name} differs from this build; release assets are immutable.`);
    }
    combinedSize += remote.size;
  }
  const combinedSha256 = combinedHash.digest('hex');
  if (combinedSize !== group.source.size || combinedSha256 !== group.source.sha256) {
    throw new Error(`Gitee attachment sequence does not reconstruct ${group.source.name}.`);
  }
}

async function publishGiteeRelease(options = {}) {
  const args = options.args ?? process.argv.slice(2);
  const environment = options.environment ?? process.env;
  const fetchImpl = options.fetchImpl ?? global.fetch;
  const writeOutput = options.writeOutput ?? (value => process.stdout.write(value));
  const writeProgress = options.writeProgress ?? (value => process.stderr.write(value));
  const lookupTagCommit = options.lookupTagCommit ?? readRemoteTagCommit;
  const [tag, notesPath, ...assetPaths] = args;
  const isPrerelease = tag?.startsWith('beta-v') ?? false;
  if (!tag || !notesPath || assetPaths.length !== (isPrerelease ? 4 : 3)) {
    throw new Error('Usage: node scripts/publish-gitee-release.cjs <tag> <release-notes-file> <student.exe> <teacher.exe> <SHA256SUMS> [join-gitee-beta-installers.ps1].');
  }
  const version = tag.startsWith('beta-v') ? tag.slice(6) : tag.startsWith('v') ? tag.slice(1) : '';
  const targetCommitish = environment.GITEE_RELEASE_TARGET_COMMITISH?.trim() || required('GITHUB_SHA', environment);
  validateRelease(tag, version, targetCommitish, assetPaths);
  const owner = repositoryPathSegment(required('GITEE_OWNER', environment), 'GITEE_OWNER');
  const repo = repositoryPathSegment(required('GITEE_REPO', environment), 'GITEE_REPO');
  const token = required('GITEE_TOKEN', environment);
  const remoteTagCommit = await lookupTagCommit(owner, repo, tag);
  if (!remoteTagCommit || remoteTagCommit.toLowerCase() !== targetCommitish.toLowerCase()) {
    throw new Error(`Gitee source tag ${tag} does not match the expected commit ${targetCommitish}.`);
  }
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
  const helperAsset = assets.find(asset => asset.name === 'join-gitee-beta-installers.ps1');

  const repoPath = `/repos/${owner}/${repo}`;
  const releaseName = `Veyon Campus ${version}${isPrerelease ? ' Beta' : ''}`;
  let release = await request(`${repoPath}/releases/tags/${encodeURIComponent(tag)}`, token,
    { allowNotFound: true }, fetchImpl);
  if (!release) {
    const createBody = {
      tag_name: tag,
      name: releaseName,
      body: notes,
      target_commitish: targetCommitish,
      prerelease: isPrerelease
    };
    release = await request(`${repoPath}/releases`, token, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(createBody)
    }, fetchImpl);
  }
  if (!Number.isSafeInteger(Number(release?.id)) || release.tag_name !== tag) {
    throw new Error('Gitee did not return the expected release id and tag.');
  }
  release = await verifyReleaseNotes(release, notes, repoPath, tag, releaseName, token, fetchImpl);

  const attachmentsPath = `${repoPath}/releases/${encodeURIComponent(release.id)}/attach_files`;
  const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-gitee-release-'));
  try {
    let attachments = readArray(await request(attachmentsPath, token, {}, fetchImpl), attachmentsPath);
    let indexed = indexAttachments(attachments);
    const groups = [];
    const plannedAssets = [];
    const preverifiedNames = new Set();
    const installerAssets = assets.filter(asset => asset.name !== 'SHA256SUMS' && asset !== helperAsset);

    for (const installer of installerAssets) {
      const existingOriginal = indexed.get(installer.name);
      if (existingOriginal) {
        await verifyExistingAttachment(existingOriginal, installer, attachmentsPath, token, fetchImpl);
        preverifiedNames.add(installer.name);
        groups.push({ source: installer, assets: [installer] });
        plannedAssets.push(installer);
        continue;
      }
      const parts = await splitReleaseAsset(
        installer,
        options.chunkSizeBytes ?? options.maxAttachmentBytes ?? MAX_GITEE_ATTACHMENT_BYTES,
        tempDirectory
      );
      if (parts.length > 1) {
        writeProgress(`Split ${installer.name} (${installer.size} bytes) into ${parts.length} Gitee attachments.\n`);
      }
      groups.push({ source: installer, assets: parts });
      plannedAssets.push(...parts);
    }

    for (const asset of assets.filter(asset => asset === checksumsAsset || asset === helperAsset)) {
      groups.push({ source: asset, assets: [asset] });
      plannedAssets.push(asset);
    }

    const expectedNames = new Set(plannedAssets.map(asset => asset.name));
    if (expectedNames.size !== plannedAssets.length) throw new Error('Gitee attachment plan contains duplicate filenames.');
    for (const name of indexed.keys()) {
      if (!expectedNames.has(name)) throw new Error(`Gitee release contains an unexpected attachment: ${name}.`);
    }

    const alreadyPublished = plannedAssets.every(asset => indexed.has(asset.name));
    for (const asset of plannedAssets) {
      if (indexed.has(asset.name)) continue;
      if (asset.size > (options.maxAttachmentBytes ?? MAX_GITEE_ATTACHMENT_BYTES)) {
        throw new Error(`Gitee attachment ${asset.name} exceeds the ${options.maxAttachmentBytes ?? MAX_GITEE_ATTACHMENT_BYTES}-byte upload limit.`);
      }
      writeProgress(`Uploading Gitee attachment ${asset.name} (${asset.size} bytes).\n`);
      const bytes = await fs.promises.readFile(asset.path);
      const form = new FormData();
      form.append('file', new Blob([bytes], { type: 'application/octet-stream' }), asset.name);
      try {
        await request(attachmentsPath, token, {
          method: 'POST', body: form, timeoutMs: 15 * 60 * 1000
        }, fetchImpl);
      } catch (error) {
        throw new Error(`Could not upload Gitee attachment ${asset.name} (${asset.size} bytes): ${error.message}`, { cause: error });
      }
    }

    attachments = readArray(await request(attachmentsPath, token, {}, fetchImpl), attachmentsPath);
    indexed = indexAttachments(attachments);
    if (indexed.size !== plannedAssets.length || plannedAssets.some(asset => !indexed.has(asset.name))) {
      throw new Error('Gitee did not retain exactly the expected release assets.');
    }
    for (const asset of plannedAssets) {
      const attachment = indexed.get(asset.name);
      const remoteSize = Number(attachment.size);
      if (Number.isFinite(remoteSize) && remoteSize !== asset.size) {
        throw new Error(`Gitee recorded the wrong file size for ${asset.name}.`);
      }
    }
    for (const group of groups) {
      if (group.assets.length === 1 && preverifiedNames.has(group.assets[0].name)) continue;
      await verifyAssetGroup(group, indexed, attachmentsPath, token, fetchImpl);
    }
    writeOutput(JSON.stringify({
      releaseId: release.id,
      tag,
      repository: `${decodeURIComponent(owner)}/${decodeURIComponent(repo)}`,
      assets: plannedAssets.map(asset => asset.name),
      sourceInstallers: installerAssets.map(asset => ({ name: asset.name, size: asset.size, sha256: asset.sha256 })),
      alreadyPublished
    }, null, 2) + '\n');
  } finally {
    fs.rmSync(tempDirectory, { recursive: true, force: true });
  }
}

if (require.main === module) {
  publishGiteeRelease().catch(error => {
    process.stderr.write(`Gitee release publication failed: ${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = { publishGiteeRelease, validateRelease, splitReleaseAsset, readRemoteTagCommit };

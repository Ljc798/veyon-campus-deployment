'use strict';

const { execFileSync } = require('node:child_process');
const fs = require('node:fs');

const API_BASE = 'https://gitee.com/api/v5';
const GITHUB_REPOSITORY = 'Ljc798/veyon-campus-deployment';

function required(name, environment = process.env) {
  const value = environment[name]?.trim();
  if (!value) throw new Error(`Missing required environment variable ${name}.`);
  return value;
}

function repositoryPathSegment(value, name) {
  if (!/^[A-Za-z0-9_.-]+$/.test(value)) throw new Error(`${name} contains unsupported characters.`);
  return encodeURIComponent(value);
}

function parseReleaseTag(tag) {
  const match = /^(beta-)?v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.exec(tag || '');
  if (!match) throw new Error('Gitee release tag must use vX.Y.Z or beta-vX.Y.Z.');
  return { version: `${match[2]}.${match[3]}.${match[4]}`, prerelease: Boolean(match[1]) };
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
    throw new Error(`Gitee API request failed with HTTP ${response.status} (${method} ${endpoint})${detail ? `: ${detail.slice(0, 384)}` : ''}.`);
  }
  if (response.status === 204) return null;
  try {
    return await response.json();
  } catch {
    throw new Error(`Gitee API returned invalid JSON (${method} ${endpoint}).`);
  }
}

function releaseNotesWithDownloads(tag, version, releaseNotes) {
  const baseUrl = `https://github.com/${GITHUB_REPOSITORY}/releases/download/${encodeURIComponent(tag)}`;
  const student = `VeyonCampus-Student-Setup-${version}-win-x64.exe`;
  const teacher = `VeyonCampus-Teacher-Setup-${version}-win-x64.exe`;
  const section = [
    '## 完整安装包下载',
    '',
    '每台电脑按角色下载一个完整安装包：',
    `- 教师电脑：[${teacher}](${baseUrl}/${teacher})`,
    `- 学生电脑：[${student}](${baseUrl}/${student})`,
    `- SHA-256 校验值：[SHA256SUMS](${baseUrl}/SHA256SUMS)`,
    '',
    '安装包由 GitHub Release 提供完整文件。Gitee 单附件大小受限，Gitee 页面不再提供分卷。'
  ].join('\n');
  const originalNotes = releaseNotes.split(/\n\s*\n/)
    .filter(paragraph => !/join-gitee-beta-installers\.ps1|\.part(?:NNN|[0-9]{3})|Gitee.{0,120}分卷/i.test(paragraph))
    .join('\n\n').trim();
  return `${section}${originalNotes ? `\n\n---\n\n${originalNotes}` : ''}\n`;
}

function isLegacyDownloadAsset(name, version) {
  if (name === 'SHA256SUMS' || name === 'join-gitee-beta-installers.ps1') return true;
  const base = `VeyonCampus-(?:Student|Teacher)-Setup-${version.replaceAll('.', '\\.')}-win-x64\\.exe`;
  return new RegExp(`^${base}(?:\\.part[0-9]{3})?$`).test(name);
}

async function updateRelease(release, tag, releaseName, body, prerelease, repoPath, token, fetchImpl) {
  if (release.tag_name !== tag) throw new Error('Gitee returned a release for a different tag.');
  if (release.name === releaseName && release.body === body && release.prerelease === prerelease) return release;
  const updated = await request(`${repoPath}/releases/${encodeURIComponent(release.id)}`, token, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ tag_name: tag, name: releaseName, body, prerelease })
  }, fetchImpl);
  if (updated?.tag_name !== tag || updated.name !== releaseName || updated.body !== body ||
      updated.prerelease !== prerelease) {
    throw new Error('Gitee did not save the expected release notes and prerelease status.');
  }
  return updated;
}

async function removeOldDownloadAttachments(attachmentsPath, version, token, fetchImpl) {
  const attachments = readArray(await request(attachmentsPath, token, {}, fetchImpl), attachmentsPath);
  const removed = [];
  for (const attachment of attachments) {
    const name = attachment.name || attachment.filename;
    if (!name || !isLegacyDownloadAsset(name, version)) continue;
    if (attachment.id === undefined || attachment.id === null) {
      throw new Error(`Gitee did not expose an id for old download attachment ${name}.`);
    }
    await request(`${attachmentsPath}/${encodeURIComponent(attachment.id)}`, token, {
      method: 'DELETE'
    }, fetchImpl);
    removed.push(name);
  }
  const remaining = readArray(await request(attachmentsPath, token, {}, fetchImpl), attachmentsPath);
  const stale = remaining.map(item => item.name || item.filename)
    .filter(name => name && isLegacyDownloadAsset(name, version));
  if (stale.length) throw new Error(`Gitee still has old split download attachments: ${stale.join(', ')}.`);
  return removed;
}

async function publishGiteeRelease(options = {}) {
  const args = options.args ?? process.argv.slice(2);
  const environment = options.environment ?? process.env;
  const fetchImpl = options.fetchImpl ?? global.fetch;
  const writeOutput = options.writeOutput ?? (value => process.stdout.write(value));
  const [tag, notesPath, ...extraArgs] = args;
  if (!tag || !notesPath || extraArgs.length) {
    throw new Error('Usage: node scripts/publish-gitee-release.cjs <vX.Y.Z|beta-vX.Y.Z> <release-notes-file>.');
  }
  const { version, prerelease } = parseReleaseTag(tag);
  const targetCommitish = environment.GITEE_RELEASE_TARGET_COMMITISH?.trim() || required('GITHUB_SHA', environment);
  if (!/^[0-9a-f]{40}$/i.test(targetCommitish)) throw new Error('Release target must be a full commit SHA.');
  const owner = repositoryPathSegment(required('GITEE_OWNER', environment), 'GITEE_OWNER');
  const repo = repositoryPathSegment(required('GITEE_REPO', environment), 'GITEE_REPO');
  const token = required('GITEE_TOKEN', environment);
  const lookupTagCommit = options.lookupTagCommit ?? readRemoteTagCommit;
  const remoteTagCommit = await lookupTagCommit(owner, repo, tag);
  if (!remoteTagCommit || remoteTagCommit.toLowerCase() !== targetCommitish.toLowerCase()) {
    throw new Error(`Gitee source tag ${tag} does not match the expected commit ${targetCommitish}.`);
  }

  const rawNotes = await fs.promises.readFile(notesPath, 'utf8');
  const body = releaseNotesWithDownloads(tag, version, rawNotes);
  const repoPath = `/repos/${owner}/${repo}`;
  const releaseName = `Veyon Campus ${version}${prerelease ? ' Beta' : ''}`;
  let release = await request(`${repoPath}/releases/tags/${encodeURIComponent(tag)}`, token,
    { allowNotFound: true }, fetchImpl);
  if (!release) {
    release = await request(`${repoPath}/releases`, token, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        tag_name: tag,
        name: releaseName,
        body,
        target_commitish: targetCommitish,
        prerelease
      })
    }, fetchImpl);
  }
  if (!Number.isSafeInteger(Number(release?.id))) throw new Error('Gitee did not return a release id.');
  await updateRelease(release, tag, releaseName, body, prerelease, repoPath, token, fetchImpl);

  const attachmentsPath = `${repoPath}/releases/${encodeURIComponent(release.id)}/attach_files`;
  const removed = await removeOldDownloadAttachments(attachmentsPath, version, token, fetchImpl);
  writeOutput(JSON.stringify({
    releaseId: release.id,
    tag,
    repository: `${decodeURIComponent(owner)}/${decodeURIComponent(repo)}`,
    completeInstallerLinks: [
      `https://github.com/${GITHUB_REPOSITORY}/releases/download/${tag}/VeyonCampus-Teacher-Setup-${version}-win-x64.exe`,
      `https://github.com/${GITHUB_REPOSITORY}/releases/download/${tag}/VeyonCampus-Student-Setup-${version}-win-x64.exe`
    ],
    removedLegacyAttachments: removed
  }, null, 2) + '\n');
}

if (require.main === module) {
  publishGiteeRelease().catch(error => {
    process.stderr.write(`Gitee release publication failed: ${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = {
  publishGiteeRelease,
  parseReleaseTag,
  readRemoteTagCommit,
  releaseNotesWithDownloads,
  isLegacyDownloadAsset
};

// The public download page reads the same place the desktop client does: the newest release on
// GitCode (primary, reachable from mainland China) and GitHub (mirror). The CloudBase discovery API
// that used to serve this configuration is retired, so nothing here depends on it any more.
const RELEASE_SOURCES = Object.freeze([
  {
    source: 'gitcode',
    origin: 'https://gitcode.com',
    owner: 'ljc_1126',
    api: 'https://api.gitcode.com/api/v5/repos/ljc_1126/veyon-campus-deployment'
  },
  {
    source: 'github',
    origin: 'https://github.com',
    owner: 'Ljc798',
    api: 'https://api.github.com/repos/Ljc798/veyon-campus-deployment'
  }
]);
const STABLE_TAG = /^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/;

function validSource(record) {
  if (!record || !['github', 'gitcode'].includes(record.source) ||
      typeof record.version !== 'string' || record.version.length > 64 || !/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(record.version) ||
      record.tag !== 'v' + record.version || typeof record.updatedAt !== 'string' ||
      !Number.isFinite(Date.parse(record.updatedAt))) return false;
  const github = record.source === 'github';
  const origin = github ? 'https://github.com' : 'https://gitcode.com';
  const owner = github ? 'Ljc798' : 'ljc_1126';
  const repository = 'veyon-campus-deployment';
  const root = `${origin}/${owner}/${repository}`;
  const releasePath = github ? `/releases/tag/${record.tag}` : '/releases';
  return record.releaseUrl === `${root}${releasePath}` &&
    record.teacherDownloadUrl === `${root}/releases/download/${record.tag}/VeyonCampus-Teacher-Setup-${record.version}-win-x64.exe` &&
    record.studentDownloadUrl === `${root}/releases/download/${record.tag}/VeyonCampus-Student-Setup-${record.version}-win-x64.exe`;
}

export async function loadPublicDownloadPageConfig() {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 7000);
  try {
    const records = (await Promise.all(
      RELEASE_SOURCES.map(source => readLatestRelease(source, controller.signal))
    )).filter(Boolean);
    if (records.length === 0) throw new Error('下载页配置暂时无法读取。');
    records.sort((left, right) => compareVersions(right.version, left.version));
    return { configured: true, sources: records };
  } catch (error) {
    if (error?.name === 'AbortError') throw new Error('下载页配置读取超时。');
    throw error;
  } finally {
    clearTimeout(timeout);
  }
}

async function readLatestRelease(source, signal) {
  try {
    const response = await fetch(`${source.api}/releases/latest`, {
      headers: { Accept: 'application/json' },
      cache: 'no-store',
      credentials: 'omit',
      signal
    });
    if (!response.ok) return null;
    const payload = await response.json();
    const tag = typeof payload?.tag_name === 'string' ? payload.tag_name : '';
    if (!STABLE_TAG.test(tag) || payload?.prerelease === true) return null;
    const version = tag.slice(1);
    const root = `${source.origin}/${source.owner}/veyon-campus-deployment`;
    const record = {
      source: source.source,
      version,
      tag,
      releaseUrl: source.source === 'github' ? `${root}/releases/tag/${tag}` : `${root}/releases`,
      teacherDownloadUrl: `${root}/releases/download/${tag}/VeyonCampus-Teacher-Setup-${version}-win-x64.exe`,
      studentDownloadUrl: `${root}/releases/download/${tag}/VeyonCampus-Student-Setup-${version}-win-x64.exe`,
      updatedAt: typeof payload?.created_at === 'string' && Number.isFinite(Date.parse(payload.created_at))
        ? payload.created_at
        : new Date().toISOString()
    };
    // Guard the link shapes the page renders; a malformed record is worse than no record.
    return validSource(record) ? record : null;
  } catch (error) {
    if (error?.name === 'AbortError') throw error;
    return null;
  }
}

function compareVersions(left, right) {
  const a = left.split('.').map(Number);
  const b = right.split('.').map(Number);
  for (let index = 0; index < 3; index++) {
    if (a[index] !== b[index]) return a[index] - b[index];
  }
  return 0;
}

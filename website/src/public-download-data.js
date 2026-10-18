const DEFAULT_API_BASE = 'https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com';

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
  const baseUrl = (import.meta.env.VITE_API_BASE_PATH || DEFAULT_API_BASE).replace(/\/$/, '');
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 7000);
  try {
    const response = await fetch(`${baseUrl}/v1/download-page-config`, {
      headers: { Accept: 'application/json' },
      cache: 'no-store',
      credentials: 'omit',
      signal: controller.signal
    });
    let payload = null;
    if ((response.headers.get('content-type') || '').includes('application/json')) {
      try { payload = await response.json(); } catch { payload = null; }
    }
    if (!response.ok || typeof payload?.configured !== 'boolean' || !Array.isArray(payload.sources))
      throw new Error('下载页配置暂时无法读取。');
    if (!payload.configured && payload.sources.length === 0) return { configured: false, sources: [] };
    if (!payload.configured) throw new Error('下载页配置格式无效。');
    if (payload.sources.length !== 2 || payload.sources.some(record => !validSource(record)) ||
        new Set(payload.sources.map(record => record.source)).size !== 2)
      throw new Error('下载页配置格式无效。');
    return payload;
  } catch (error) {
    if (error?.name === 'AbortError') throw new Error('下载页配置读取超时。');
    throw error;
  } finally {
    clearTimeout(timeout);
  }
}

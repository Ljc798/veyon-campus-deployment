import { db, sessionUserId } from './cloudbase.js';

const CAMPUS_COLUMNS = 'id,name,region,city,status,created_at,updated_at';
const TELEMETRY_COLUMNS = 'day_hkt,unique_devices,heartbeat_signals,updated_at';
const DEPLOYMENT_TELEMETRY_COLUMNS = 'day_hkt,campus_id,deployment_id,application_version,unique_devices,heartbeat_signals,updated_at';
const PACKAGE_COLUMNS = 'package_id,display_name,campus_name,computer_prefix,schema_version,target_os,architecture,artifact_file_name,artifact_size_bytes,artifact_sha256,download_count,published_at';
const TEACHER_HEARTBEAT_COLUMNS = 'day_hkt,campus_id,package_id,teacher_version,student_version,configured_computer_count,updated_at';

export const ADMIN_DATASETS = [
  {
    id: 'admin_profiles', table: 'admin_profiles', title: '后台角色资料',
    description: 'RLS 只返回当前登录账号自己的角色记录。',
    columns: 'display_name,role,created_at',
    fields: [
      ['display_name', '显示名称'], ['role', '站点角色'], ['created_at', '登记时间', 'date']
    ],
    order: ['created_at', false]
  },
  {
    id: 'campuses', table: 'campuses', title: '校区资料',
    description: '授权站点角色可见的校区名称、地区、城市与状态。',
    columns: CAMPUS_COLUMNS,
    fields: [
      ['id', '编号'], ['name', '校区名称'], ['region', '地区'], ['city', '城市'],
      ['status', '状态'], ['created_at', '创建时间', 'date'], ['updated_at', '更新时间', 'date']
    ],
    order: ['updated_at', false]
  },
  {
    id: 'deployment_packages', table: 'deployment_packages', title: '已发布配置包目录',
    description: '只读取已发布目录字段；发布者资料、手机号指纹与撤回记录不开放给浏览器。',
    columns: PACKAGE_COLUMNS,
    fields: [
      ['package_id', '配置包编号', 'code'], ['display_name', '显示名称'], ['campus_name', '校区名称'],
      ['computer_prefix', '电脑名前缀'], ['schema_version', 'Schema'], ['target_os', '系统'],
      ['architecture', '架构'], ['artifact_file_name', '文件名', 'code'],
      ['artifact_size_bytes', '大小（字节）', 'number'], ['artifact_sha256', 'SHA-256', 'code'],
      ['download_count', '下载次数', 'number'], ['published_at', '发布时间', 'date']
    ],
    order: ['published_at', false]
  },
  {
    id: 'campus_daily_teacher_heartbeats', table: 'campus_daily_teacher_heartbeats', title: '教师每日心跳',
    description: '仅展示日期、配置包、版本与电脑数；匿名身份和发布者 HMAC 摘要不读取。',
    columns: TEACHER_HEARTBEAT_COLUMNS,
    fields: [
      ['day_hkt', 'UTC+8 日期'], ['campus_id', '正式校区编号', 'number'],
      ['package_id', '配置包编号', 'code'], ['teacher_version', '教师端版本'],
      ['student_version', '学生端版本'], ['configured_computer_count', '配置电脑数', 'number'],
      ['updated_at', '更新时间', 'date']
    ],
    order: ['day_hkt', false]
  },
  {
    id: 'telemetry_daily_hkt_stats', table: 'telemetry_daily_hkt_stats', title: '全站匿名汇总',
    description: '每日活跃安装标识与心跳请求数；跨日不能关联同一安装。',
    columns: TELEMETRY_COLUMNS,
    fields: [
      ['day_hkt', 'UTC+8 日期'], ['unique_devices', '每日活跃安装标识', 'number'],
      ['heartbeat_signals', '心跳次数', 'number'], ['updated_at', '更新时间', 'date']
    ],
    order: ['day_hkt', false]
  },
  {
    id: 'telemetry_daily_deployment_stats', table: 'telemetry_daily_deployment_stats', title: '校区与版本汇总',
    description: '按日期、正式校区、配置包和学生端版本聚合的匿名数据。',
    columns: DEPLOYMENT_TELEMETRY_COLUMNS,
    fields: [
      ['day_hkt', 'UTC+8 日期'], ['campus_id', '正式校区编号', 'number'],
      ['deployment_id', '配置包编号', 'code'], ['application_version', '学生端版本'],
      ['unique_devices', '每日活跃安装标识', 'number'],
      ['heartbeat_signals', '心跳次数', 'number'], ['updated_at', '更新时间', 'date']
    ],
    order: ['day_hkt', false]
  }
];

export const DATABASE_TABLES = [
  { name: 'admin_profiles', purpose: '后台账号角色', access: '当前账号自己的角色记录', dataset: 'admin_profiles' },
  { name: 'application_releases', purpose: '教师端与学生端签名发行清单', access: '服务端写入；浏览器只可读取公开 latest API', link: '/admin/api' },
  { name: 'campus_daily_teacher_heartbeats', purpose: '校区每日教师端快照', access: '授权管理员可读；界面隐藏身份摘要', dataset: 'campus_daily_teacher_heartbeats' },
  { name: 'campuses', purpose: '校区名称、地区、城市与状态', access: '授权管理员可读写', dataset: 'campuses' },
  { name: 'deployment_package_artifacts', purpose: '配置包私有对象键', access: '仅服务端可读；不向浏览器开放' },
  { name: 'deployment_package_download_attempts', purpose: '下载错误码限速状态', access: '仅服务端可读；含地址 HMAC 指纹' },
  { name: 'deployment_packages', purpose: '学生配置包已发布目录', access: '只读已发布安全字段', dataset: 'deployment_packages' },
  { name: 'telemetry_daily_deployment_devices', purpose: '按日、校区、配置包与版本去重的摘要', access: '仅服务端可读；不展示逐设备摘要' },
  { name: 'telemetry_daily_deployment_stats', purpose: '校区、配置包与版本每日汇总', access: '授权管理员可读', dataset: 'telemetry_daily_deployment_stats' },
  { name: 'telemetry_daily_hkt_devices', purpose: '全站逐日安装标识摘要', access: '仅服务端可读；不展示逐设备摘要' },
  { name: 'telemetry_daily_hkt_stats', purpose: '全站每日匿名汇总', access: '授权管理员可读', dataset: 'telemetry_daily_hkt_stats' },
  { name: 'telemetry_hkt_retention_state', purpose: '匿名数据保留清理进度', access: '仅服务端维护' }
];

function hongKongDay(date) {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Asia/Hong_Kong', year: 'numeric', month: '2-digit', day: '2-digit'
  }).formatToParts(date);
  const part = type => parts.find(item => item.type === type)?.value;
  return `${part('year')}-${part('month')}-${part('day')}`;
}

function shiftDay(day, amount) {
  const [year, month, date] = day.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, date + amount)).toISOString().slice(0, 10);
}

function requireDatabase() {
  if (!db) throw new Error('CloudBase 数据库客户端没有初始化。');
  return db;
}

export async function loadAdminProfile(session) {
  const userId = sessionUserId(session);
  if (!userId) return null;
  const client = requireDatabase();
  const { data, error } = await client
    .from('admin_profiles')
    .select('user_id,display_name,role')
    .eq('user_id', userId)
    .limit(1);
  if (error) throw error;
  return Array.isArray(data) ? data[0] ?? null : null;
}

export async function loadAdminData(days = 30) {
  const client = requireDatabase();
  const throughDay = hongKongDay(new Date());
  const fromDay = shiftDay(throughDay, -(days - 1));
  const [campusesResult, activeCountResult, statsResult, deploymentStatsResult] = await Promise.all([
    client.from('campuses')
      .select(CAMPUS_COLUMNS, { count: 'exact' })
      .order('updated_at', { ascending: false })
      .limit(200),
    client.from('campuses')
      .select('id', { count: 'exact' })
      .eq('status', 'active')
      .limit(1),
    client.from('telemetry_daily_hkt_stats')
      .select(TELEMETRY_COLUMNS)
      .gte('day_hkt', fromDay)
      .lte('day_hkt', throughDay)
      .order('day_hkt', { ascending: true })
      .limit(days),
    client.from('telemetry_daily_deployment_stats')
      .select(DEPLOYMENT_TELEMETRY_COLUMNS)
      .gte('day_hkt', fromDay)
      .lte('day_hkt', throughDay)
      .order('day_hkt', { ascending: false })
      .limit(1000)
  ]);
  if (campusesResult.error) throw campusesResult.error;
  if (activeCountResult.error) throw activeCountResult.error;
  if (statsResult.error) throw statsResult.error;
  if (deploymentStatsResult.error) throw deploymentStatsResult.error;
  return {
    campuses: Array.isArray(campusesResult.data) ? campusesResult.data : [],
    campusCount: Number(campusesResult.count ?? campusesResult.data?.length ?? 0),
    activeCampusCount: Number(activeCountResult.count ?? activeCountResult.data?.length ?? 0),
    telemetry: Array.isArray(statsResult.data) ? statsResult.data : [],
    deploymentTelemetry: Array.isArray(deploymentStatsResult.data) ? deploymentStatsResult.data : []
  };
}

export async function loadAdminDatasetPage(datasetId, page = 1, pageSize = 25) {
  const dataset = ADMIN_DATASETS.find(item => item.id === datasetId);
  if (!dataset) throw new Error('未开放这个数据集的浏览权限。');
  const currentPage = Math.max(1, Math.trunc(page));
  const boundedSize = Math.min(100, Math.max(1, Math.trunc(pageSize)));
  const from = (currentPage - 1) * boundedSize;
  const client = requireDatabase();
  const { data, error, count } = await client
    .from(dataset.table)
    .select(dataset.columns, { count: 'exact' })
    .order(dataset.order[0], { ascending: dataset.order[1] })
    .range(from, from + boundedSize - 1);
  if (error) throw error;
  return {
    datasetId,
    page: currentPage,
    pageSize: boundedSize,
    total: count == null ? null : Number(count),
    available: true,
    rows: Array.isArray(data) ? data : []
  };
}

export async function loadAdminDataCatalog(pageSize = 25) {
  const entries = await Promise.all(ADMIN_DATASETS.map(async dataset => {
    try {
      return [dataset.id, await loadAdminDatasetPage(dataset.id, 1, pageSize)];
    } catch {
      return [dataset.id, {
        datasetId: dataset.id,
        page: 1,
        pageSize,
        total: null,
        available: false,
        rows: []
      }];
    }
  }));
  return Object.fromEntries(entries);
}

async function requestApiJson(baseUrl, path) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 7000);
  try {
    const response = await fetch(`${baseUrl}${path}`, {
      headers: { Accept: 'application/json' },
      cache: 'no-store',
      credentials: 'omit',
      signal: controller.signal
    });
    let data = null;
    if ((response.headers.get('content-type') || '').includes('application/json')) {
      try { data = await response.json(); } catch { data = null; }
    }
    return { status: response.status, ok: response.ok, data };
  } catch {
    return { status: null, ok: false, data: null };
  } finally {
    clearTimeout(timeout);
  }
}

export async function loadApiOverview() {
  const baseUrl = (import.meta.env.VITE_API_BASE_PATH ||
    'https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com').replace(/\/$/, '');
  const [health, catalog, teacherRelease, studentRelease] = await Promise.all([
    requestApiJson(baseUrl, '/health'),
    requestApiJson(baseUrl, '/v1/deployment-packages?limit=1'),
    requestApiJson(baseUrl, '/v1/releases/latest?role=TeacherConsole&architecture=win-x64'),
    requestApiJson(baseUrl, '/v1/releases/latest?role=StudentSetup&architecture=win-x64')
  ]);
  const releaseSummary = result => {
    const release = result.data?.release;
    return {
      ok: result.ok,
      status: result.status,
      product: typeof release?.manifest?.product === 'string' ? release.manifest.product : null,
      schemaVersion: Number.isInteger(release?.manifest?.schemaVersion) ? release.manifest.schemaVersion : null,
      version: typeof release?.manifest?.version === 'string' ? release.manifest.version : null,
      architecture: typeof release?.manifest?.architecture === 'string' ? release.manifest.architecture : null,
      fileName: typeof release?.manifest?.fileName === 'string' ? release.manifest.fileName : null,
      sizeBytes: Number.isFinite(release?.manifest?.sizeBytes) ? release.manifest.sizeBytes : null,
      sha256: typeof release?.manifest?.sha256 === 'string' ? release.manifest.sha256 : null,
      downloadUrl: typeof release?.manifest?.downloadUrl === 'string' ? release.manifest.downloadUrl : null,
      signatureAlgorithm: typeof release?.signatureAlgorithm === 'string' ? release.signatureAlgorithm : null,
      signature: typeof release?.signature === 'string' ? release.signature : null,
      publishedAt: typeof release?.publishedAt === 'string' ? release.publishedAt : null
    };
  };
  return {
    baseUrl,
    health: { ok: health.ok && health.data?.status === 'ready', status: health.status },
    catalog: {
      ok: catalog.ok && Array.isArray(catalog.data?.items),
      status: catalog.status,
      checkedItems: Array.isArray(catalog.data?.items) ? catalog.data.items.length : null,
      hasMore: catalog.data?.hasMore === true
    },
    releases: {
      TeacherConsole: releaseSummary(teacherRelease),
      StudentSetup: releaseSummary(studentRelease)
    }
  };
}

export async function createCampus(fields) {
  const client = requireDatabase();
  const { data, error } = await client
    .from('campuses')
    .insert({
      name: fields.name.trim(),
      region: fields.region.trim(),
      city: fields.city.trim(),
      status: fields.status || 'active'
    })
    .select(CAMPUS_COLUMNS);
  if (error) throw error;
  return Array.isArray(data) ? data[0] ?? null : data;
}

export async function updateCampus(id, fields) {
  const client = requireDatabase();
  const { data, error } = await client
    .from('campuses')
    .update({
      name: fields.name.trim(),
      region: fields.region.trim(),
      city: fields.city.trim(),
      status: fields.status
    })
    .eq('id', id)
    .select(CAMPUS_COLUMNS);
  if (error) throw error;
  return Array.isArray(data) ? data[0] ?? null : data;
}

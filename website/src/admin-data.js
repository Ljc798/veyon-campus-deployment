import { db, getActiveSession, sessionUserId } from './cloudbase.js';

const CAMPUS_COLUMNS = 'id,name,region,city,status,created_at,updated_at';
const TELEMETRY_COLUMNS = 'day_hkt,unique_devices,heartbeat_signals,updated_at';
const DEPLOYMENT_TELEMETRY_COLUMNS = 'day_hkt,campus_id,deployment_id,application_version,unique_devices,heartbeat_signals,updated_at';
const PACKAGE_COLUMNS = 'package_id,display_name,campus_name,computer_prefix,schema_version,target_os,architecture,artifact_file_name,artifact_size_bytes,artifact_sha256,download_count,published_at';
const TEACHER_HEARTBEAT_COLUMNS = 'day_hkt,campus_id,package_id,teacher_version,student_version,configured_computer_count,updated_at';

export const ADMIN_DATASETS = [
  {
    id: 'admin_profiles', table: 'admin_profiles', title: '后台角色资料',
    description: 'owner/admin 可查看全部角色条目；其他角色只看见 RLS 允许的本人记录。',
    columns: 'user_id,display_name,role,created_at',
    privilegedView: true,
    fields: [
      ['user_id', '用户编号', 'code'], ['display_name', '显示名称'],
      ['role', '站点角色'], ['created_at', '登记时间', 'date']
    ],
    order: ['created_at', false]
  },
  {
    id: 'campuses', table: 'campuses', title: '校区资料',
    description: '授权站点角色可见的校区名称、地区、城市与状态。',
    columns: CAMPUS_COLUMNS,
    privilegedView: true,
    fields: [
      ['id', '编号'], ['name', '校区名称'], ['region', '地区'], ['city', '城市'],
      ['status', '状态'], ['created_at', '创建时间', 'date'], ['updated_at', '更新时间', 'date']
    ],
    order: ['updated_at', false]
  },
  {
    id: 'deployment_packages', table: 'deployment_packages', title: '已发布配置包目录',
    description: 'owner/admin 可查看全表；身份 HMAC 指纹由服务端遮罩，其他角色只见已发布目录字段。',
    columns: PACKAGE_COLUMNS,
    privilegedView: true,
    fields: [
      ['package_id', '配置包编号', 'code'], ['campus_id', '关联校区编号', 'number'],
      ['campus_name', '校区名称'], ['computer_prefix', '电脑名前缀'], ['display_name', '显示名称'],
      ['schema_version', 'Schema'], ['target_os', '系统'], ['architecture', '架构'],
      ['artifact_size_bytes', '大小（字节）', 'number'], ['artifact_sha256', '文件 SHA-256', 'code'],
      ['status', '状态'], ['created_by_user_id', '创建者用户编号', 'code'],
      ['created_at', '创建时间', 'date'], ['published_at', '发布时间', 'date'],
      ['download_count', '下载次数', 'number'], ['withdrawn_at', '撤回时间', 'date'],
      ['withdrawn_by_user_id', '撤回者用户编号', 'code'], ['withdrawn_reason', '撤回原因'],
      ['publisher_identity_fingerprint', '发布者身份指纹', 'code'],
      ['publisher_phone_fingerprint', '手机号校验指纹', 'code'],
      ['publisher_name', '发布者姓名'], ['artifact_file_name', '文件名', 'code']
    ],
    rlsFields: [
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
    description: '显示日期、配置包、版本与电脑数；匿名身份和发布者 HMAC 摘要由服务端遮罩。',
    columns: TEACHER_HEARTBEAT_COLUMNS,
    privilegedView: true,
    fields: [
      ['day_hkt', '日期'], ['campus_id', '正式校区编号', 'number'],
      ['campus_identity_digest', '校区身份摘要', 'code'], ['package_id', '配置包编号', 'code'],
      ['publisher_digest', '发布者身份摘要', 'code'], ['teacher_version', '教师端版本'],
      ['student_version', '学生端版本'], ['configured_computer_count', '配置电脑数', 'number'],
      ['updated_at', '更新时间', 'date']
    ],
    rlsFields: [
      ['day_hkt', '日期'], ['campus_id', '正式校区编号', 'number'],
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
    privilegedView: true,
    fields: [
      ['day_hkt', '日期'], ['unique_devices', '每日活跃安装标识', 'number'],
      ['heartbeat_signals', '心跳次数', 'number'], ['updated_at', '更新时间', 'date']
    ],
    order: ['day_hkt', false]
  },
  {
    id: 'telemetry_daily_deployment_stats', table: 'telemetry_daily_deployment_stats', title: '校区与版本汇总',
    description: '按日期、正式校区、配置包和学生端版本聚合的匿名数据。',
    columns: DEPLOYMENT_TELEMETRY_COLUMNS,
    privilegedView: true,
    fields: [
      ['day_hkt', '日期'], ['campus_id', '正式校区编号', 'number'],
      ['deployment_id', '配置包编号', 'code'], ['application_version', '学生端版本'],
      ['unique_devices', '每日活跃安装标识', 'number'],
      ['heartbeat_signals', '心跳次数', 'number'], ['updated_at', '更新时间', 'date']
    ],
    order: ['day_hkt', false]
  },
  {
    id: 'application_releases', table: 'application_releases', title: '应用发行清单',
    description: '仅 owner/admin 可通过受限只读 API 查看；私有对象键由服务端遮罩。',
    columns: 'release_id,role,product,version,architecture,file_name,object_key,size_bytes,sha256,signature_algorithm,signature,status,published_at,created_at',
    privilegedView: true, serviceOnly: true,
    fields: [
      ['release_id', '发行编号', 'code'], ['role', '角色'], ['product', '产品'],
      ['version', '版本'], ['architecture', '架构'], ['file_name', '文件名', 'code'],
      ['object_key', '私有对象键', 'code'], ['size_bytes', '大小（字节）', 'number'],
      ['sha256', 'SHA-256', 'code'], ['signature_algorithm', '签名算法'],
      ['signature', '签名', 'code'], ['status', '状态'],
      ['published_at', '发布时间', 'date'], ['created_at', '创建时间', 'date']
    ],
    order: ['published_at', false]
  },
  {
    id: 'deployment_package_artifacts', table: 'deployment_package_artifacts', title: '配置包私有对象索引',
    description: 'owner/admin 可查看对象索引记录；对象键会由服务端遮罩。',
    columns: 'package_id,storage_key,created_at',
    privilegedView: true, serviceOnly: true,
    fields: [
      ['package_id', '配置包编号', 'code'], ['storage_key', '私有对象键', 'code'],
      ['created_at', '创建时间', 'date']
    ],
    order: ['created_at', false]
  },
  {
    id: 'deployment_package_download_attempts', table: 'deployment_package_download_attempts', title: '下载校验与限错状态',
    description: 'owner/admin 可查看失败计数与封锁时间；客户端地址 HMAC 会由服务端遮罩。',
    columns: 'package_id,client_fingerprint,window_started_at,failure_count,blocked_until,last_attempt_at',
    privilegedView: true, serviceOnly: true,
    fields: [
      ['package_id', '配置包编号', 'code'], ['client_fingerprint', '客户端地址指纹', 'code'],
      ['window_started_at', '计数窗口开始', 'date'], ['failure_count', '失败次数', 'number'],
      ['blocked_until', '封锁至', 'date'], ['last_attempt_at', '最近尝试', 'date']
    ],
    order: ['last_attempt_at', false]
  },
  {
    id: 'telemetry_daily_deployment_devices', table: 'telemetry_daily_deployment_devices', title: '校区匿名设备明细',
    description: 'owner/admin 可分页查看每日分组记录；安装 HMAC 摘要会由服务端遮罩。',
    columns: 'day_hkt,campus_id,deployment_id,application_version,installation_digest,recorded_at',
    privilegedView: true, serviceOnly: true,
    fields: [
      ['day_hkt', '日期'], ['campus_id', '校区编号', 'number'],
      ['deployment_id', '配置包编号', 'code'], ['application_version', '学生端版本'],
      ['installation_digest', '安装摘要', 'code'], ['recorded_at', '记录时间', 'date']
    ],
    order: ['recorded_at', false]
  },
  {
    id: 'telemetry_daily_hkt_devices', table: 'telemetry_daily_hkt_devices', title: '全站匿名设备明细',
    description: 'owner/admin 可分页查看每日去重记录；安装 HMAC 摘要会由服务端遮罩。',
    columns: 'day_hkt,installation_digest,recorded_at',
    privilegedView: true, serviceOnly: true,
    fields: [
      ['day_hkt', '日期'], ['installation_digest', '安装摘要', 'code'],
      ['recorded_at', '记录时间', 'date']
    ],
    order: ['recorded_at', false]
  },
  {
    id: 'telemetry_hkt_retention_state', table: 'telemetry_hkt_retention_state', title: '匿名数据清理状态',
    description: 'owner/admin 可只读查看最近一次匿名摘要清理日期。',
    columns: 'id,last_cleanup_hkt',
    privilegedView: true, serviceOnly: true,
    fields: [
      ['id', '状态标识'], ['last_cleanup_hkt', '最近清理日期']
    ],
    order: ['id', true]
  }
];

export const DATABASE_TABLES = [
  { name: 'admin_profiles', purpose: '后台账号角色', access: 'owner/admin 可查看全部；其他账号仅 RLS 可见本人的记录', dataset: 'admin_profiles', ownerAdminApi: true },
  { name: 'application_releases', purpose: '教师端与学生端签名发行清单', access: 'owner/admin 只读；私有对象键由服务端遮罩', dataset: 'application_releases', ownerAdminApi: true, serviceOnly: true, link: '/admin/api' },
  { name: 'campus_daily_operations_reports', purpose: '用户主动开启后的 Teacher 每日更新与课堂汇总', access: '仅服务端读写；owner/admin 只看全站每日聚合', serviceOnly: true },
  { name: 'campus_daily_teacher_heartbeats', purpose: '校区每日教师端快照', access: 'owner/admin 可查看全部；HMAC 摘要由服务端遮罩', dataset: 'campus_daily_teacher_heartbeats', ownerAdminApi: true },
  { name: 'campuses', purpose: '校区名称、地区、城市与状态', access: 'owner/admin 只读全表；editor 可按 RLS 维护', dataset: 'campuses', ownerAdminApi: true },
  { name: 'deployment_package_artifacts', purpose: '配置包私有对象索引', access: 'owner/admin 只读；对象键由服务端遮罩', dataset: 'deployment_package_artifacts', ownerAdminApi: true, serviceOnly: true },
  { name: 'deployment_package_download_attempts', purpose: '下载失败计数与临时封锁状态', access: 'owner/admin 只读；客户端 HMAC 由服务端遮罩', dataset: 'deployment_package_download_attempts', ownerAdminApi: true, serviceOnly: true },
  { name: 'deployment_packages', purpose: '学生配置包目录、发布者和撤回记录', access: 'owner/admin 查看全表；身份指纹由服务端遮罩', dataset: 'deployment_packages', ownerAdminApi: true },
  { name: 'telemetry_daily_deployment_devices', purpose: '按日、校区、配置包与版本去重的记录', access: 'owner/admin 可分页查看；安装 HMAC 由服务端遮罩', dataset: 'telemetry_daily_deployment_devices', ownerAdminApi: true, serviceOnly: true },
  { name: 'telemetry_daily_deployment_stats', purpose: '校区、配置包与版本每日汇总', access: 'owner/admin 全表；其他授权角色按 RLS 读取', dataset: 'telemetry_daily_deployment_stats', ownerAdminApi: true },
  { name: 'telemetry_daily_hkt_devices', purpose: '全站逐日安装摘要记录', access: 'owner/admin 可分页查看；安装 HMAC 由服务端遮罩', dataset: 'telemetry_daily_hkt_devices', ownerAdminApi: true, serviceOnly: true },
  { name: 'telemetry_daily_hkt_stats', purpose: '全站每日匿名汇总', access: 'owner/admin 全表；其他授权角色按 RLS 读取', dataset: 'telemetry_daily_hkt_stats', ownerAdminApi: true },
  { name: 'telemetry_hkt_retention_state', purpose: '匿名数据保留清理进度', access: 'owner/admin 只读查看', dataset: 'telemetry_hkt_retention_state', ownerAdminApi: true, serviceOnly: true }
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

export async function loadAdminCampusPage(page = 1, pageSize = 25, search = '') {
  const client = requireDatabase();
  const currentPage = Math.max(1, Math.trunc(page));
  const boundedSize = Math.min(100, Math.max(1, Math.trunc(pageSize)));
  const term = String(search || '').trim().replace(/[\\%_]/g, '\\$&');
  const from = (currentPage - 1) * boundedSize;
  let pageQuery = client.from('campuses')
    .select(CAMPUS_COLUMNS, { count: 'exact' })
    .order('updated_at', { ascending: false })
    .order('id', { ascending: false });
  if (term) pageQuery = pageQuery.ilike('name', `%${term}%`);
  const pageRequest = pageQuery.range(from, from + boundedSize - 1);
  const allCountRequest = term
    ? client.from('campuses').select('id', { count: 'exact' }).limit(1)
    : null;
  const [pageResult, allCountResult] = await Promise.all([
    pageRequest,
    allCountRequest || Promise.resolve(null)
  ]);
  if (pageResult.error) throw pageResult.error;
  if (allCountResult?.error) throw allCountResult.error;
  const rows = Array.isArray(pageResult.data) ? pageResult.data : [];
  const total = Number(pageResult.count ?? rows.length);
  return {
    page: currentPage,
    pageSize: boundedSize,
    total,
    allCount: Number(allCountResult?.count ?? pageResult.count ?? rows.length),
    hasMore: currentPage * boundedSize < total,
    rows
  };
}

export async function loadAdminDatasetPage(datasetId, page = 1, pageSize = 25, role = '') {
  const dataset = ADMIN_DATASETS.find(item => item.id === datasetId);
  if (!dataset) throw new Error('未开放这个数据集的浏览权限。');
  const currentPage = Math.max(1, Math.trunc(page));
  const boundedSize = Math.min(100, Math.max(1, Math.trunc(pageSize)));
  const ownerOrAdmin = ['owner', 'admin'].includes(role);
  if (dataset.privilegedView && ownerOrAdmin) {
    const session = await getActiveSession();
    const accessToken = session?.access_token;
    if (!accessToken) throw new Error('管理员会话已失效，请重新登录。');
    const apiBase = (import.meta.env.VITE_API_BASE_PATH ||
      'https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com').replace(/\/$/, '');
    const url = new URL(`${apiBase}/v1/admin/database/${encodeURIComponent(dataset.table)}`, window.location.origin);
    url.searchParams.set('page', String(currentPage));
    url.searchParams.set('pageSize', String(Math.min(boundedSize, 50)));
    const response = await fetch(url, {
      headers: { Accept: 'application/json', Authorization: `Bearer ${accessToken}` },
      cache: 'no-store',
      credentials: 'omit'
    });
    if (!response.ok) {
      if (response.status === 401) throw new Error('管理员会话已失效，请重新登录。');
      if (response.status === 403) throw new Error('只有站点 owner/admin 可查看完整数据库记录。');
      throw new Error('管理员数据库只读 API 暂时不可用。');
    }
    const payload = await response.json();
    if (payload?.table !== dataset.table || !Array.isArray(payload.rows) ||
        typeof payload.hasMore !== 'boolean')
      throw new Error('管理员数据库 API 返回格式无效。');
    return {
      datasetId,
      page: currentPage,
      pageSize: Math.min(boundedSize, 50),
      total: null,
      hasMore: payload.hasMore,
      available: true,
      rows: payload.rows
    };
  }
  if (dataset.serviceOnly) throw new Error('仅站点 owner/admin 可查看此数据集。');
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

async function callAdminApi(path, options = {}) {
  const session = await getActiveSession();
  const accessToken = session?.access_token;
  if (!accessToken) throw new Error('管理员会话已失效，请重新登录。');
  const apiBase = (import.meta.env.VITE_API_BASE_PATH ||
    'https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com').replace(/\/$/, '');
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 15000);
  try {
    const response = await fetch(`${apiBase}${path}`, {
      method: options.method || 'GET',
      headers: {
        Accept: 'application/json',
        Authorization: `Bearer ${accessToken}`,
        ...(options.body ? { 'Content-Type': 'application/json' } : {})
      },
      ...(options.body ? { body: JSON.stringify(options.body) } : {}),
      cache: 'no-store',
      credentials: 'omit',
      signal: controller.signal
    });
    let payload = null;
    if ((response.headers.get('content-type') || '').includes('application/json')) {
      try { payload = await response.json(); } catch { payload = null; }
    }
    if (!response.ok) {
      if (response.status === 401) throw new Error('管理员会话已失效，请重新登录。');
      if (response.status === 403) throw new Error('只有站点 owner/admin 可以读取此数据。');
      if (typeof payload?.error === 'string') throw new Error(payload.error);
      throw new Error('管理员 API 暂时不可用。');
    }
    return payload;
  } catch (error) {
    if (error?.name === 'AbortError') throw new Error('管理员 API 请求超时，请稍后重试。');
    throw error;
  } finally {
    clearTimeout(timeout);
  }
}

export async function loadDailyOperationsReports(days = 30) {
  if (![7, 30, 90].includes(Number(days))) throw new Error('统计周期无效。');
  const payload = await callAdminApi('/v1/admin/operations/daily?days=' + Number(days));
  if (!payload || payload.days !== Number(days) || !Array.isArray(payload.rows))
    throw new Error('运维汇总 API 返回格式无效。');
  return payload.rows;
}

export async function loadReleaseDispatchStatus() {
  const payload = await callAdminApi('/v1/admin/releases/dispatch-status');
  if (!payload || typeof payload.configured !== 'boolean' ||
      typeof payload.workflowReady !== 'boolean' ||
      (payload.workflowError !== null && typeof payload.workflowError !== 'string') ||
      typeof payload.repository !== 'string' || typeof payload.workflowUrl !== 'string')
    throw new Error('版本发布状态 API 返回格式无效。');
  return payload;
}

export async function dispatchReleaseTag(tag) {
  const payload = await callAdminApi('/v1/admin/releases/dispatch', {
    method: 'POST',
    body: { tag }
  });
  if (payload?.accepted !== true || payload.tag !== tag || typeof payload.workflowUrl !== 'string')
    throw new Error('版本发布 API 返回格式无效。');
  return payload;
}

export async function loadAdminDataCatalog(pageSize = 25, role = '') {
  const ownerOrAdmin = ['owner', 'admin'].includes(role);
  const entries = await Promise.all(ADMIN_DATASETS.map(async dataset => {
    if (dataset.serviceOnly && !ownerOrAdmin) {
      return [dataset.id, {
        datasetId: dataset.id,
        page: 1,
        pageSize,
        total: null,
        available: false,
        restricted: true,
        rows: []
      }];
    }
    try {
      return [dataset.id, await loadAdminDatasetPage(dataset.id, 1, pageSize, role)];
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

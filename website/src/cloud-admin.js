import {
  cloudbaseReady,
  getActiveSession,
  signIn,
  signOut
} from './cloudbase.js';
import {
  createCampus,
  loadAdminData,
  loadAdminProfile,
  updateCampus
} from './admin-data.js';

const DISPLAY_TIME_ZONE = 'Asia/Hong_Kong';

const navigation = [
  ['/admin', '数据总览', 'grid'],
  ['/admin/campuses', '校区管理', 'building'],
  ['/admin/usage', '匿名统计', 'activity'],
  ['/admin/analytics', '趋势分析', 'chart'],
  ['/admin/settings', '账号与连接', 'settings']
];

const titles = {
  '/admin': ['数据总览', '校区与匿名使用汇总'],
  '/admin/campuses': ['校区管理', '维护真实校区资料'],
  '/admin/usage': ['匿名统计', '按 UTC+8 日期汇总的安装标识'],
  '/admin/analytics': ['趋势分析', '只显示数据库中已记录的汇总数据'],
  '/admin/settings': ['账号与连接', '身份、权限与服务状态']
};

const model = {
  root: null,
  header: () => '',
  footer: () => '',
  path: '/admin',
  range: 30,
  search: '',
  region: '全部地区',
  sequence: 0,
  session: null,
  profile: null,
  campuses: [],
  campusCount: 0,
  activeCampusCount: 0,
  telemetry: [],
  deploymentTelemetry: [],
  error: '',
  loginError: '',
  working: false
};

function escapeHtml(value) {
  return String(value == null ? '' : value).replace(/[&<>"']/g, char => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  })[char]);
}

function icon(name) {
  const glyphs = {
    grid: '<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
    building: '<path d="M3 21h18M5 21V5l7-3 7 3v16M9 8h.01M15 8h.01M9 12h.01M15 12h.01M10 21v-5h4v5"/>',
    activity: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
    chart: '<path d="M3 3v18h18M8 15l4-4 4 3 5-7"/>',
    settings: '<circle cx="12" cy="12" r="3"/><path d="m19 15 2 1-2 4-2-1a8 8 0 0 1-2 1l-.3 2h-4L10 20a8 8 0 0 1-2-1l-2 1-2-4 2-1a8 8 0 0 1 0-2l-2-1 2-4 2 1a8 8 0 0 1 2-1l.3-2h4L15 8a8 8 0 0 1 2 1l2-1 2 4-2 1a8 8 0 0 1 0 2Z"/>',
    logout: '<path d="M10 17l5-5-5-5M15 12H3M12 3h6a3 3 0 0 1 3 3v12a3 3 0 0 1-3 3h-6"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/>',
    download: '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3"/>',
    close: '<path d="m18 6-12 12M6 6l12 12"/>',
    info: '<circle cx="12" cy="12" r="10"/><path d="M12 11v5M12 8h.01"/>',
    refresh: '<path d="M20 7v5h-5M4 17v-5h5M5 9a7 7 0 0 1 12-2l3 5M4 12l3 5a7 7 0 0 0 12-2"/>'
  };
  return '<span class="icon" aria-hidden="true"><svg viewBox="0 0 24 24">' + (glyphs[name] || glyphs.info) + '</svg></span>';
}

function routeLink(path, label, className = '') {
  return '<a href="' + path + '" data-route="' + path + '" class="' + className + '">' + label + '</a>';
}

function toast(message) {
  const element = document.getElementById('toast');
  if (!element) return;
  element.textContent = message;
  element.className = 'toast show';
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { element.className = 'toast'; }, 3000);
}

function loginPage() {
  const body = '<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>管理员登录</span><h1>登录 Veyon Campus 管理工作区</h1><p>后台数据由 CloudBase PostgreSQL 提供，只有已授权账号可以读取。</p></div></section>' +
    '<section class="page-body"><div class="site-container contact-grid"><div class="contact-card"><h2>使用管理员账号</h2><p>请使用 CloudBase Auth 中已开通的用户名和密码。网站没有公开注册或自助提权入口。</p>' +
    (model.loginError ? '<div class="callout"><div><strong>登录未完成</strong><p>' + escapeHtml(model.loginError) + '</p></div></div>' : '') +
    '<form id="cloudbase-login"><div class="form-grid"><div class="field full"><label for="cloud-admin-username">用户名</label><input id="cloud-admin-username" name="username" type="text" autocomplete="username" required maxlength="128" /></div><div class="field full"><label for="cloud-admin-password">密码</label><input id="cloud-admin-password" name="password" type="password" autocomplete="current-password" required /></div><div class="field full"><button class="btn" type="submit">登录管理工作区</button></div></div></form></div>' +
    '<div class="contact-card"><h2>访问边界</h2><p>这个入口不会出现在公开展示页的导航、首页或页脚中。即使直接访问 `/admin`，数据库仍按 CloudBase 登录身份和 PostgreSQL 行级策略授权。</p><div class="privacy-note">' + icon('info') + '<span>登录成功后只显示实际登记的校区和按日汇总心跳；不会展示或导出原始安装标识。</span></div>' + routeLink('/', '返回公开首页', 'btn secondary') + '</div></div></section>';
  return '<div class="site-shell">' + model.header() + '<main class="site-main">' + body + '</main>' + model.footer() + '</div>';
}

function accessDeniedPage() {
  return '<div class="site-shell">' + model.header() + '<main class="site-main"><section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>需要授权</span><h1>此账号没有后台权限</h1><p>账号已登录，但 PostgreSQL 中没有对应的管理员角色记录。</p><div style="margin-top:24px"><button class="btn secondary" type="button" data-cb-action="logout">退出登录</button></div></div></section></main>' + model.footer() + '</div>';
}

function apiBasePath() {
  return (import.meta.env.VITE_API_BASE_PATH || 'https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com').replace(/\/$/, '');
}

function errorPage() {
  return '<div class="admin-shell"><main class="admin-main"><div class="admin-content"><section class="card card-pad"><h2>数据暂时无法读取</h2><p>请确认 CloudBase 环境、安全域名、登录会话和 PostgreSQL 角色授权后重试。</p><button class="btn secondary" type="button" data-cb-action="retry">重试</button> ' + routeLink('/', '返回网站首页', 'btn secondary') + '</section></div></main></div>';
}

function adminFrame(content) {
  const title = titles[model.path] || titles['/admin'];
  const nav = navigation.map(([path, label, symbol]) => routeLink(path,
    icon(symbol) + '<span>' + label + '</span>' + (path === '/admin/campuses' ? '<small class="nav-count">' + model.campusCount + '</small>' : ''),
    path === model.path ? 'active' : '')).join('');
  return '<div class="admin-shell"><aside class="sidebar"><a class="sidebar-brand" href="/" data-route="/"><img src="/assets/veyon-campus-mark.svg" alt=""/><div><strong>Veyon Campus</strong><small>ADMIN CONSOLE</small></div></a>' +
    '<div class="workspace-switch"><span class="workspace-avatar">VC</span><span><strong>Veyon Campus</strong><small>CloudBase PostgreSQL</small></span></div>' +
    '<div class="nav-section-label">管理</div><nav class="side-nav" aria-label="管理后台导航">' + nav + '</nav><div class="sidebar-spacer"></div>' +
    '<div class="sidebar-help"><strong>当前账号</strong><p>' + escapeHtml(model.profile.display_name) + ' · ' + escapeHtml(model.profile.role) + '</p>' + routeLink('/admin/settings', '查看账号与连接 ' + icon('settings'), '') + '</div>' +
    '<div class="sidebar-profile"><span class="profile-avatar">' + escapeHtml(model.profile.display_name.slice(0, 1) || '管') + '</span><span><strong>' + escapeHtml(model.profile.display_name) + '</strong><small>' + escapeHtml(model.profile.role) + '</small></span></div></aside>' +
    '<div class="admin-main"><header class="admin-topbar"><div class="topbar-title"><div><h1>' + title[0] + '</h1><small>' + title[1] + '</small></div></div><div class="topbar-actions"><span class="topbar-demo">' + icon('activity') + 'CloudBase PG</span><button class="icon-button" type="button" data-cb-action="logout" aria-label="退出登录">' + icon('logout') + '</button></div></header><main class="admin-content">' + content + '</main></div></div>';
}

function connectionBanner() {
  return '<div class="demo-banner"><div class="demo-banner-note">' + icon('info') + '<span><strong>数据库已连接</strong> — 页面数据来自 CloudBase PostgreSQL；数据库角色：' + escapeHtml(model.profile.role) + '。</span></div><div class="api-health" id="cloud-admin-api" data-state="checking"><span class="api-health-dot" aria-hidden="true"></span><span class="api-health-copy"><strong>遥测 API：检查中</strong><small>' + escapeHtml(apiBasePath()) + '/health</small></span></div></div>';
}

function liveStat(label, value, detail, symbol) {
  return '<article class="card stat-card"><div class="stat-top"><span>' + label + '</span><span class="stat-icon">' + icon(symbol) + '</span></div><div class="stat-value">' + escapeHtml(value) + '</div><div class="stat-bottom"><span>' + escapeHtml(detail) + '</span></div></article>';
}

function hongKongDay(date) {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: DISPLAY_TIME_ZONE, year: 'numeric', month: '2-digit', day: '2-digit'
  }).formatToParts(date);
  const part = type => parts.find(item => item.type === type)?.value;
  return `${part('year')}-${part('month')}-${part('day')}`;
}

function shiftDay(day, amount) {
  const [year, month, date] = day.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, date + amount)).toISOString().slice(0, 10);
}

function seriesForRange() {
  const byDay = new Map(model.telemetry.map(row => [row.day_hkt, Number(row.unique_devices || 0)]));
  const days = Number(model.range);
  const today = hongKongDay(new Date());
  const firstDay = shiftDay(today, -(days - 1));
  return Array.from({ length: days }, (_, index) => {
    const key = shiftDay(firstDay, index);
    return { day: key, value: byDay.get(key) || 0 };
  });
}

function liveChart() {
  const series = seriesForRange();
  const maxValue = Math.max(1, ...series.map(point => point.value));
  const points = series.map((point, index) => ({
    x: 38 + index * (528 / Math.max(1, series.length - 1)),
    y: 180 - (point.value / maxValue) * 145
  }));
  const line = points.map((point, index) => (index ? 'L' : 'M') + point.x.toFixed(1) + ' ' + point.y.toFixed(1)).join(' ');
  const area = line + ' L 566 190 L 38 190 Z';
  const labels = [series[0], series[Math.floor((series.length - 1) / 2)], series[series.length - 1]];
  return '<div class="chart-wrap"><svg viewBox="0 0 590 205" role="img" aria-label="每日活跃安装标识数量，使用真实 PG 汇总数据" preserveAspectRatio="none"><defs><linearGradient id="cloud-chart-fill" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="#4daf96" stop-opacity=".18"/><stop offset="1" stop-color="#4daf96" stop-opacity="0"/></linearGradient></defs>' +
    [24, 68, 112, 156, 190].map(y => '<line x1="38" y1="' + y + '" x2="566" y2="' + y + '" stroke="#edf1ef" stroke-width="1"/>').join('') +
    '<path d="' + area + '" fill="url(#cloud-chart-fill)"/><path d="' + line + '" fill="none" stroke="#238d79" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/>' +
    '<circle cx="' + points[points.length - 1].x.toFixed(1) + '" cy="' + points[points.length - 1].y.toFixed(1) + '" r="5" fill="#fff" stroke="#238d79" stroke-width="3"/></svg></div>' +
    '<div class="chart-labels"><span>' + labels[0].day + '</span><span>' + labels[1].day + '</span><span>' + labels[2].day + '</span></div>';
}

function regionRows() {
  const counts = new Map();
  for (const campus of model.campuses) counts.set(campus.region, (counts.get(campus.region) || 0) + 1);
  const rows = [...counts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 6);
  if (!rows.length) return '<div class="empty-state">尚未登记校区地区。</div>';
  const highest = rows[0][1];
  return rows.map(([region, count]) => '<div class="region-row"><strong>' + escapeHtml(region) + '</strong><div class="bar-track"><div class="bar-fill" style="width:' + Math.max(4, Math.round(count / highest * 100)) + '%"></div></div><small>' + count + '</small></div>').join('');
}

function campusCode(campus) {
  return 'VC-' + String(campus.id).padStart(6, '0');
}

function campusStatus(campus) {
  return campus.status === 'paused' ? '<span class="pill neutral">已暂停</span>' : '<span class="pill">有效</span>';
}

function updatedLabel(value) {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? '—' : parsed.toLocaleString('zh-HK', { timeZone: DISPLAY_TIME_ZONE, hour12: false });
}

function recentCampusRows(items) {
  return items.slice(0, 5).map(campus => '<tr><td><div class="campus-name"><span class="campus-mark">' + escapeHtml(campus.name.slice(0, 1)) + '</span><span><strong>' + escapeHtml(campus.name) + '</strong><small>' + campusCode(campus) + '</small></span></div></td><td>' + escapeHtml(campus.region) + ' · ' + escapeHtml(campus.city) + '</td><td>' + campusStatus(campus) + '</td></tr>').join('') || '<tr><td colspan="3"><div class="empty-state">数据库还没有校区记录。</div></td></tr>';
}

function dashboardPage() {
  const today = hongKongDay(new Date());
  const todayStats = model.telemetry.find(row => row.day_hkt === today);
  const totalSignals = model.telemetry.reduce((sum, row) => sum + Number(row.heartbeat_signals || 0), 0);
  const activeCampuses = model.activeCampusCount;
  const rangeOptions = [7, 30, 90].map(days => '<option value="' + days + '" ' + (Number(model.range) === days ? 'selected' : '') + '>最近 ' + days + ' 天</option>').join('');
  return connectionBanner() + '<div class="page-heading"><div><h2>真实数据概况</h2><p>统计只包含 CloudBase PG 已写入的数据，不会填充演示数字。</p></div><div class="heading-actions"><select class="control-select" data-cb-range aria-label="统计周期">' + rangeOptions + '</select><button class="btn secondary sm" type="button" data-cb-action="export-stats">' + icon('download') + '导出汇总</button></div></div>' +
    '<div class="stat-grid">' + liveStat('今日活跃安装标识', Number(todayStats?.unique_devices || 0).toLocaleString('zh-CN'), 'UTC+8 自然日去重', 'activity') + liveStat('已登记校区', model.campusCount.toLocaleString('zh-CN'), 'PostgreSQL 记录', 'building') + liveStat('启用校区', activeCampuses.toLocaleString('zh-CN'), '管理员维护的状态', 'grid') + liveStat('近 ' + model.range + ' 天心跳', totalSignals.toLocaleString('zh-CN'), '心跳请求总次数', 'chart') + '</div>' +
    '<div class="dashboard-grid"><section class="card card-pad chart-card"><div class="card-heading"><div><h3>每日活跃安装标识</h3><p>HMAC 按 UTC+8 日期轮换，跨日无法关联到同一设备。</p></div><span class="pill neutral">真实 PG 汇总</span></div>' + liveChart() + '</section><section class="card card-pad"><div class="card-heading"><div><h3>校区地区</h3><p>基于最近载入的校区资料，最多 200 条</p></div></div><div class="region-list">' + regionRows() + '</div></section></div>' +
    '<section class="card table-card"><div class="card-pad"><div class="card-heading"><div><h3>最近更新的校区</h3><p>仅显示校区资料，不按校区推断终端数量。</p></div>' + routeLink('/admin/campuses', '管理校区', '') + '</div></div><div class="table-wrap"><table><thead><tr><th>校区</th><th>地区 / 城市</th><th>状态</th></tr></thead><tbody>' + recentCampusRows(model.campuses) + '</tbody></table></div><div class="table-footer"><span>最多展示 200 条校区记录</span></div></section>';
}

function campusRows(items) {
  const canEdit = ['owner', 'admin', 'editor'].includes(model.profile.role);
  return items.map(campus => '<tr><td><div class="campus-name"><span class="campus-mark">' + escapeHtml(campus.name.slice(0, 1)) + '</span><span><strong>' + escapeHtml(campus.name) + '</strong><small>' + campusCode(campus) + '</small></span></div></td><td>' + escapeHtml(campus.region) + ' · ' + escapeHtml(campus.city) + '</td><td>' + campusStatus(campus) + '</td><td>' + updatedLabel(campus.updated_at) + '</td><td>' + (canEdit ? '<button class="row-action" type="button" data-cb-action="edit-campus" data-id="' + escapeHtml(campus.id) + '" aria-label="编辑校区">' + icon('settings') + '</button>' : '') + '</td></tr>').join('');
}

function campusPage() {
  const canEdit = ['owner', 'admin', 'editor'].includes(model.profile.role);
  const regions = ['全部地区', ...new Set(model.campuses.map(campus => campus.region))].sort((a, b) => a === '全部地区' ? -1 : a.localeCompare(b, 'zh-Hans'));
  const regionOptions = regions.map(region => '<option value="' + escapeHtml(region) + '" ' + (region === model.region ? 'selected' : '') + '>' + escapeHtml(region) + '</option>').join('');
  const rows = filteredCampuses();
  return connectionBanner() + '<div class="page-heading"><div><h2>校区资料</h2><p>心跳只通过已发布配置包编号关联数据库校区；不使用客户端自报校区名称或校区编号。</p></div><div class="heading-actions">' + (canEdit ? '<button class="btn sm" type="button" data-cb-action="add-campus">' + icon('plus') + '新增校区</button>' : '') + '<button class="btn secondary sm" type="button" data-cb-action="export-campuses">' + icon('download') + '导出 CSV</button></div></div>' +
    '<div class="stat-grid">' + liveStat('校区总数', model.campusCount.toLocaleString('zh-CN'), 'PostgreSQL 记录', 'building') + liveStat('当前列表地区数', new Set(model.campuses.map(campus => campus.region)).size.toLocaleString('zh-CN'), '最多检查 200 条记录', 'activity') + liveStat('启用校区', model.campuses.filter(campus => campus.status === 'active').length.toLocaleString('zh-CN'), '当前列表范围', 'grid') + liveStat('已暂停校区', model.campuses.filter(campus => campus.status === 'paused').length.toLocaleString('zh-CN'), '当前列表范围', 'settings') + '</div>' +
    '<div class="toolbar"><div class="search-field">' + icon('search') + '<input type="search" data-cb-search value="' + escapeHtml(model.search) + '" placeholder="搜索校区名称、地区或城市…" aria-label="搜索校区" /></div><select class="control-select" data-cb-region aria-label="按地区筛选">' + regionOptions + '</select></div>' +
    '<section class="card table-card"><div class="table-wrap"><table><thead><tr><th>校区</th><th>地区 / 城市</th><th>状态</th><th>最近更新</th><th></th></tr></thead><tbody id="cloud-campus-rows">' + (rows.length ? campusRows(rows) : '<tr><td colspan="5"><div class="empty-state">' + (model.campusCount ? '没有符合条件的校区。' : '数据库还没有校区记录，请按实际资料新增。') + '</div></td></tr>') + '</tbody></table></div><div class="pagination"><span id="cloud-campus-count">显示 ' + rows.length + ' 条；数据库记录 ' + model.campusCount + ' 条（列表最多载入 200 条）</span></div></section>';
}

function filteredCampuses() {
  const query = model.search.trim().toLocaleLowerCase();
  return model.campuses.filter(campus => {
    const matchesRegion = model.region === '全部地区' || campus.region === model.region;
    const text = [campus.name, campus.region, campus.city, campusCode(campus)].join(' ').toLocaleLowerCase();
    return matchesRegion && (!query || text.includes(query));
  });
}

function usagePage() {
  const rows = model.telemetry.slice().reverse();
  return connectionBanner() + '<div class="page-heading"><div><h2>匿名使用汇总</h2><p>只显示每日去重安装标识和接收次数；不提供逐设备记录。</p></div><div class="heading-actions"><select class="control-select" data-cb-range aria-label="统计周期">' + [7, 30, 90].map(days => '<option value="' + days + '" ' + (Number(model.range) === days ? 'selected' : '') + '>最近 ' + days + ' 天</option>').join('') + '</select><button class="btn secondary sm" type="button" data-cb-action="export-stats">' + icon('download') + '导出汇总</button></div></div>' +
    '<div class="usage-summary">' + liveStat('已载入天数', rows.length.toLocaleString('zh-CN'), '所选周期有数据的日期', 'calendar') + liveStat('每日活跃上限', rows.reduce((max, row) => Math.max(max, Number(row.unique_devices || 0)), 0).toLocaleString('zh-CN'), '按日 HMAC 去重', 'activity') + liveStat('期间心跳次数', rows.reduce((sum, row) => sum + Number(row.heartbeat_signals || 0), 0).toLocaleString('zh-CN'), '不是跨日独立设备数', 'chart') + '</div>' +
    '<section class="card table-card"><div class="table-wrap"><table><thead><tr><th>UTC+8 日期</th><th>当日活跃安装标识</th><th>心跳次数</th><th>汇总更新时间</th></tr></thead><tbody>' + (rows.length ? rows.map(row => '<tr><td><strong>' + escapeHtml(row.day_hkt) + '</strong></td><td>' + Number(row.unique_devices || 0).toLocaleString('zh-CN') + '</td><td>' + Number(row.heartbeat_signals || 0).toLocaleString('zh-CN') + '</td><td>' + updatedLabel(row.updated_at) + '</td></tr>').join('') : '<tr><td colspan="4"><div class="empty-state">数据库还没有收到匿名心跳。</div></td></tr>') + '</tbody></table></div><div class="table-footer"><span>原始安装标识不会提供给浏览器。</span></div></section>';
}

function analyticsPage() {
  const campuses = new Map(model.campuses.map(campus => [String(campus.id), campus.name]));
  const rows = model.deploymentTelemetry;
  const deploymentRows = rows.length
    ? rows.map(row => '<tr><td>' + escapeHtml(row.day_hkt) + '</td><td>' + escapeHtml(campuses.get(String(row.campus_id)) || ('校区 #' + row.campus_id)) + '</td><td><code>' + escapeHtml(row.deployment_id) + '</code></td><td>' + escapeHtml(row.application_version) + '</td><td>' + Number(row.unique_devices || 0).toLocaleString('zh-CN') + '</td><td>' + Number(row.heartbeat_signals || 0).toLocaleString('zh-CN') + '</td></tr>').join('')
    : '<tr><td colspan="6"><div class="empty-state">还没有关联到已发布配置包的校区版本心跳。</div></td></tr>';
  return connectionBanner() + '<div class="page-heading"><div><h2>版本趋势与校区覆盖</h2><p>汇总到校区、部署包编号与学生工具版本，不提供逐台记录。</p></div><div class="heading-actions"><select class="control-select" data-cb-range aria-label="统计周期">' + [7, 30, 90].map(days => '<option value="' + days + '" ' + (Number(model.range) === days ? 'selected' : '') + '>最近 ' + days + ' 天</option>').join('') + '</select><button class="btn secondary sm" type="button" data-cb-action="export-stats">' + icon('download') + '导出汇总</button></div></div>' +
    '<div class="analytics-grid"><section class="card card-pad"><div class="card-heading"><div><h3>每日活跃安装标识</h3><p>按 UTC+8 日期轮换摘要，跨日无法关联同一安装。</p></div></div>' + liveChart() + '</section><section class="card card-pad"><div class="card-heading"><div><h3>校区地区覆盖</h3><p>由管理员登记的校区地址字段统计</p></div></div><div class="region-list">' + regionRows() + '</div></section></div>' +
    '<section class="card table-card"><div class="table-wrap"><table><thead><tr><th>UTC+8 日期</th><th>校区</th><th>部署包编号</th><th>学生工具版本</th><th>每日去重安装数</th><th>心跳请求数</th></tr></thead><tbody>' + deploymentRows + '</tbody></table></div><div class="table-footer"><span>同一安装按日期、部署包和版本分别去重；只保留按日 HMAC 摘要 90 天。</span></div></section>' +
    '<div class="callout">' + icon('info') + '<div><strong>统计解释</strong><p>不同版本或部署包的每日去重数不能直接相加作为校区总安装数；跨日摘要不可关联，周期累计也不是周期独立设备总数。心跳是匿名公开接口，安装标识可重置、请求可能伪造；这些数据用于趋势参考，不是完整设备清单。</p></div></div>';
}

function settingsPage() {
  return connectionBanner() + '<div class="page-heading"><div><h2>账号与连接状态</h2><p>网站使用 CloudBase Auth 会话和 PostgreSQL 行级策略。</p></div><div class="heading-actions"><button class="btn secondary sm" type="button" data-cb-action="retry">' + icon('refresh') + '重新读取</button><button class="btn secondary sm" type="button" data-cb-action="logout">' + icon('logout') + '退出登录</button></div></div>' +
    '<div class="settings-layout"><section class="card settings-section"><h3>当前账号</h3><p>后台权限来自服务端维护的角色记录，不能由浏览器自行修改。</p><div class="settings-row"><span><strong>显示名称</strong><small>' + escapeHtml(model.profile.display_name) + '</small></span><span class="pill neutral">已登录</span></div><div class="settings-row"><span><strong>角色</strong><small>owner / admin 可管理账号外的站点资料；editor 可维护校区；viewer 只读。</small></span><span class="pill">' + escapeHtml(model.profile.role) + '</span></div><div class="settings-row"><span><strong>数据库</strong><small>CloudBase PostgreSQL · ap-shanghai</small></span><span class="pill">已连接</span></div></section>' +
    '<section class="card settings-section"><h3>隐私与保留</h3><p>遥测只保存按 UTC+8 日期轮换的 HMAC 去重值和每日汇总。</p><div class="settings-row"><span><strong>设备去重摘要</strong><small>自动清理 90 天前的逐日摘要；部署包摘要按包隔离。</small></span><span class="pill neutral">90 天</span></div><div class="settings-row"><span><strong>每日汇总</strong><small>保留 400 天；包含日期、校区、部署包、工具版本、活跃数和请求数。</small></span><span class="pill neutral">400 天</span></div><div class="settings-row"><span><strong>心跳请求正文</strong><small>不包含姓名、账号、电脑名、IP 字段或原始安装标识；网络服务仍可接收连接源 IP。</small></span><span class="pill neutral">最少数据</span></div></section></div>';
}

function livePage() {
  if (model.path === '/admin/campuses') return campusPage();
  if (model.path === '/admin/usage') return usagePage();
  if (model.path === '/admin/analytics') return analyticsPage();
  if (model.path === '/admin/settings') return settingsPage();
  return dashboardPage();
}

function openCampusModal(campus) {
  const editing = Boolean(campus);
  const body = '<form id="cloudbase-campus-form" data-campus-id="' + (editing ? escapeHtml(campus.id) : '') + '"><div class="form-grid"><div class="field full"><label for="campus-name">校区名称 *</label><input id="campus-name" name="name" required maxlength="100" value="' + escapeHtml(campus?.name || '') + '" placeholder="例如：春晓实验学校" /></div><div class="field"><label for="campus-region">省 / 地区 *</label><input id="campus-region" name="region" required maxlength="80" value="' + escapeHtml(campus?.region || '') + '" placeholder="例如：广东省" /></div><div class="field"><label for="campus-city">城市 *</label><input id="campus-city" name="city" required maxlength="80" value="' + escapeHtml(campus?.city || '') + '" placeholder="例如：深圳市" /></div><div class="field full"><label for="campus-status">状态</label><select id="campus-status" name="status"><option value="active" ' + (!campus || campus.status === 'active' ? 'selected' : '') + '>有效</option><option value="paused" ' + (campus?.status === 'paused' ? 'selected' : '') + '>已暂停</option></select></div></div><p class="field-hint">资料保存到 CloudBase PostgreSQL。当前匿名心跳没有校区编号，因此不会自动生成校区终端数。</p></form>';
  const footer = '<button class="btn secondary sm" type="button" data-cb-action="close-modal">取消</button><button class="btn sm" type="submit" form="cloudbase-campus-form">保存到数据库</button>';
  openModal(editing ? '编辑校区资料' : '新增校区', editing ? '更新 PostgreSQL 中的校区记录。' : '创建一条真实校区记录。', body, footer);
}

function openModal(title, subtitle, body, footer) {
  const html = '<div class="modal-backdrop" data-cb-action="backdrop"><section class="modal" role="dialog" aria-modal="true" aria-labelledby="cloud-modal-title"><header class="modal-header"><div><h2 id="cloud-modal-title">' + title + '</h2><p>' + subtitle + '</p></div><button class="row-action" type="button" data-cb-action="close-modal" aria-label="关闭">' + icon('close') + '</button></header><div class="modal-body">' + body + '</div><footer class="modal-footer">' + footer + '</footer></section></div>';
  model.root.insertAdjacentHTML('beforeend', html);
  setTimeout(() => document.getElementById('campus-name')?.focus(), 30);
}

function exportRows(filename, headings, rows) {
  const quote = value => '"' + String(value == null ? '' : value).replace(/"/g, '""') + '"';
  const csv = '\ufeff' + [headings, ...rows].map(row => row.map(quote).join(',')).join('\r\n');
  const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 500);
}

function exportTelemetry() {
  exportRows('veyon-campus-anonymous-daily-summary.csv', ['UTC+8 日期', '当日活跃安装标识', '心跳次数'],
    model.telemetry.map(row => [row.day_hkt, row.unique_devices, row.heartbeat_signals]));
}

function exportCampuses() {
  exportRows('veyon-campus-campuses.csv', ['编号', '校区名称', '地区', '城市', '状态', '更新时间'],
    model.campuses.map(row => [campusCode(row), row.name, row.region, row.city, row.status, row.updated_at]));
}

async function checkApi() {
  const panel = document.getElementById('cloud-admin-api');
  if (!panel) return;
  panel.dataset.state = 'checking';
  const title = panel.querySelector('strong');
  const detail = panel.querySelector('small');
  title.textContent = '遥测 API：检查中';
  detail.textContent = apiBasePath() + '/health';
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 5000);
  try {
    const response = await fetch(apiBasePath() + '/health', {
      headers: { Accept: 'application/json' }, cache: 'no-store', credentials: 'omit', signal: controller.signal
    });
    const type = response.headers.get('content-type') || '';
    const data = response.ok && type.includes('application/json') ? await response.json() : null;
    if (!data || data.status !== 'ready') throw new Error('unavailable');
    panel.dataset.state = 'ready';
    title.textContent = '遥测 API：已连接';
    detail.textContent = '健康检查返回 ready';
  } catch {
    panel.dataset.state = 'offline';
    title.textContent = '遥测 API：暂不可用';
    detail.textContent = 'PG 管理页可用；发布、下载与心跳业务仍待端到端验收';
  } finally {
    clearTimeout(timeout);
  }
}

async function redraw() {
  const sequence = ++model.sequence;
  const path = model.path;
  try {
    if (!cloudbaseReady) {
      model.root.innerHTML = '<div class="site-shell">' + model.header() + '<main class="site-main"><section class="page-hero"><div class="site-container"><span class="eyebrow">CloudBase 配置缺失</span><h1>管理工作区还未连接数据库</h1><p>请在 website/.env.local 配置环境 ID 和 publishable key，再启动 Vite。</p></div></section></main>' + model.footer() + '</div>';
      return;
    }
    model.root.innerHTML = '<div class="site-shell">' + model.header() + '<main class="site-main"><section class="page-hero"><div class="site-container"><span class="eyebrow">CloudBase</span><h1>正在检查登录与数据权限…</h1></div></section></main>' + model.footer() + '</div>';
    const session = await getActiveSession();
    if (sequence !== model.sequence || path !== model.path) return;
    if (!session) {
      model.session = null;
      model.profile = null;
      model.root.innerHTML = loginPage();
      return;
    }
    model.session = session;
    const profile = await loadAdminProfile(session);
    if (sequence !== model.sequence || path !== model.path) return;
    if (!profile) {
      model.root.innerHTML = accessDeniedPage();
      return;
    }
    model.profile = profile;
    const data = await loadAdminData(Number(model.range));
    if (sequence !== model.sequence || path !== model.path) return;
    model.campuses = data.campuses;
    model.campusCount = data.campusCount;
    model.activeCampusCount = data.activeCampusCount;
    model.telemetry = data.telemetry;
    model.deploymentTelemetry = data.deploymentTelemetry;
    model.error = '';
    model.root.innerHTML = adminFrame(livePage());
    checkApi();
  } catch {
    if (sequence !== model.sequence || path !== model.path) return;
    model.root.innerHTML = errorPage();
  }
}

function installHandlers(root) {
  if (root.dataset.cloudAdminHandlers === 'true') return;
  root.dataset.cloudAdminHandlers = 'true';
  root.addEventListener('submit', async event => {
    if (event.target.id === 'cloudbase-login') {
      event.preventDefault();
      if (model.working) return;
      model.working = true;
      const fields = new FormData(event.target);
      try {
        await signIn(String(fields.get('username') || '').trim(), String(fields.get('password') || ''));
        model.loginError = '';
        await redraw();
      } catch {
        model.loginError = '请检查用户名和密码；若持续失败，请确认 CloudBase 用户名密码登录已启用。';
        await redraw();
      } finally {
        model.working = false;
      }
    }
    if (event.target.id === 'cloudbase-campus-form') {
      event.preventDefault();
      if (model.working) return;
      model.working = true;
      const formElement = event.target;
      const values = new FormData(formElement);
      const fields = {
        name: String(values.get('name') || ''),
        region: String(values.get('region') || ''),
        city: String(values.get('city') || ''),
        status: String(values.get('status') || 'active')
      };
      try {
        const id = formElement.dataset.campusId;
        if (id) await updateCampus(id, fields);
        else await createCampus(fields);
        model.root.querySelector('.modal-backdrop')?.remove();
        toast('校区记录已保存到 PostgreSQL。');
        await redraw();
      } catch (error) {
        toast(error?.message || '保存失败，请检查数据库权限。');
      } finally {
        model.working = false;
      }
    }
  });
  root.addEventListener('click', async event => {
    const button = event.target.closest('[data-cb-action]');
    if (!button) return;
    const action = button.dataset.cbAction;
    if (action === 'add-campus') openCampusModal(null);
    if (action === 'edit-campus') {
      const campus = model.campuses.find(row => String(row.id) === button.dataset.id);
      if (campus) openCampusModal(campus);
    }
    if (action === 'close-modal' || (action === 'backdrop' && event.target === button)) button.closest('.modal-backdrop')?.remove();
    if (action === 'export-campuses') exportCampuses();
    if (action === 'export-stats') exportTelemetry();
    if (action === 'retry') await redraw();
    if (action === 'logout') {
      try { await signOut(); } catch { /* Render unauthenticated state below. */ }
      model.session = null;
      model.profile = null;
      model.loginError = '';
      await redraw();
    }
  });
  root.addEventListener('change', async event => {
    if (event.target.matches('[data-cb-range]')) {
      model.range = Number(event.target.value);
      await redraw();
    }
    if (event.target.matches('[data-cb-region]')) {
      model.region = event.target.value;
      const body = document.getElementById('cloud-campus-rows');
      const count = document.getElementById('cloud-campus-count');
      const rows = filteredCampuses();
      if (body) body.innerHTML = rows.length ? campusRows(rows) : '<tr><td colspan="5"><div class="empty-state">没有符合条件的校区。</div></td></tr>';
      if (count) count.textContent = '显示 ' + rows.length + ' 条；数据库记录 ' + model.campusCount + ' 条（列表最多载入 200 条）';
    }
  });
  root.addEventListener('input', event => {
    if (!event.target.matches('[data-cb-search]')) return;
    model.search = event.target.value;
    const body = document.getElementById('cloud-campus-rows');
    const count = document.getElementById('cloud-campus-count');
    const rows = filteredCampuses();
    if (body) body.innerHTML = rows.length ? campusRows(rows) : '<tr><td colspan="5"><div class="empty-state">没有符合条件的校区。</div></td></tr>';
    if (count) count.textContent = '显示 ' + rows.length + ' 条；数据库记录 ' + model.campusCount + ' 条（列表最多载入 200 条）';
  });
}

export function mountCloudAdmin(root, path, header, footer) {
  model.root = root;
  model.path = titles[path] ? path : '/admin';
  model.header = header;
  model.footer = footer;
  installHandlers(root);
  void redraw();
}

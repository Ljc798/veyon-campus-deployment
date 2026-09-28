const app = document.getElementById('app');
const toastEl = document.getElementById('toast');
const h = function () { return Array.from(arguments).join(''); };

const paths = {
  home: '<path d="m3 10 9-7 9 7"/><path d="M5 9v11h14V9M9 20v-6h6v6"/>',
  grid: '<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
  building: '<path d="M3 21h18M5 21V5l7-3 7 3v16M9 8h.01M15 8h.01M9 12h.01M15 12h.01M10 21v-5h4v5"/>',
  users: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8ZM20 8v6M23 11h-6"/>',
  chart: '<path d="M3 3v18h18M8 15l4-4 4 3 5-7"/><path d="M17 7h4v4"/>',
  settings: '<circle cx="12" cy="12" r="3"/><path d="m19.4 15 .1.1 1.1.9-1.3 2.2-1.3-.5a7.9 7.9 0 0 1-1.3.8l-.2 1.4h-2.6l-.2-1.4a7.9 7.9 0 0 1-1.3-.8l-1.3.5-1.3-2.2 1.1-.9a7.7 7.7 0 0 1 0-1.6l-1.1-.9 1.3-2.2 1.3.5c.4-.3.8-.6 1.3-.8l.2-1.4h2.6l.2 1.4c.5.2.9.5 1.3.8l1.3-.5 1.3 2.2-1.1.9c.1.5.1 1.1 0 1.6Z" transform="translate(-1 -1)"/>',
  book: '<path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2Z"/>',
  arrow: '<path d="M5 12h14M13 6l6 6-6 6"/>',
  chevron: '<path d="m9 18 6-6-6-6"/>',
  down: '<path d="m6 9 6 6 6-6"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/>',
  bell: '<path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4"/>',
  dots: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
  download: '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  shield: '<path d="M12 22s8-4 8-11V5l-8-3-8 3v6c0 7 8 11 8 11Z"/><path d="m9 12 2 2 4-4"/>',
  lock: '<rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 1 1 8 0v4"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  globe: '<circle cx="12" cy="12" r="10"/><path d="M2 12h20M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10Z"/>',
  device: '<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8M12 17v4"/>',
  activity: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
  check: '<path d="m5 12 4 4L19 6"/>',
  upload: '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M17 8l-5-5-5 5M12 3v12"/>',
  spark: '<path d="m12 3 1.9 5.8L20 11l-6.1 2.2L12 19l-1.9-5.8L4 11l6.1-2.2L12 3ZM19 14l1.1 2.2L22 17l-1.9.8L19 20l-1.1-2.2L16 17l1.9-.8L19 14Z"/>',
  menu: '<path d="M4 6h16M4 12h16M4 18h16"/>',
  close: '<path d="m18 6-12 12M6 6l12 12"/>',
  pin: '<path d="M20 10c0 5-8 12-8 12S4 15 4 10a8 8 0 1 1 16 0Z"/><circle cx="12" cy="10" r="2.5"/>',
  external: '<path d="M14 3h7v7M10 14 21 3M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>',
  mail: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="m3 7 9 6 9-6"/>',
  database: '<ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14c0 1.7 4 3 9 3s9-1.3 9-3V5M3 12c0 1.7 4 3 9 3s9-1.3 9-3"/>',
  warning: '<path d="m10.3 3.9-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.7-3.1l-8-14a2 2 0 0 0-3.4 0Z"/><path d="M12 9v4M12 17h.01"/>',
  monitor: '<rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/>',
  eye: '<path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/>',
  calendar: '<rect x="3" y="4" width="18" height="17" rx="2"/><path d="M16 2v4M8 2v4M3 10h18"/>',
  refresh: '<path d="M20 7v5h-5M4 17v-5h5"/><path d="M5.6 9a7 7 0 0 1 11.5-2L20 12M4 12l2.9 5a7 7 0 0 0 11.5-2"/>',
  filter: '<path d="M4 7h16M7 12h10M10 17h4"/><circle cx="9" cy="7" r="1" fill="currentColor"/><circle cx="14" cy="12" r="1" fill="currentColor"/>',
  trash: '<path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6M10 11v5M14 11v5"/>',
  help: '<circle cx="12" cy="12" r="10"/><path d="M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3M12 17h.01"/>',
  message: '<path d="M21 11.5a8.4 8.4 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.4 8.4 0 0 1-3.8-.9L3 21l1.9-5.7a8.4 8.4 0 0 1-.9-3.8A8.5 8.5 0 0 1 8.7 3.9a8.4 8.4 0 0 1 3.8-.9h.5a8.5 8.5 0 0 1 8 8v.5Z"/>',
  file: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z"/><path d="M14 2v6h6M8 13h8M8 17h8"/>',
  wrench: '<path d="M14.7 6.3a5 5 0 0 0-6.4 6.4L3 18l3 3 5.3-5.3a5 5 0 0 0 6.4-6.4L14 13l-3-3 3.7-3.7Z"/>',
  arrowUp: '<path d="m7 14 5-5 5 5"/>',
  arrowDown: '<path d="m7 10 5 5 5-5"/>',
  info: '<circle cx="12" cy="12" r="10"/><path d="M12 11v5M12 8h.01"/>',
  terminal: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="m7 9 3 3-3 3M13 15h4"/>',
  route: '<circle cx="6" cy="18" r="3"/><circle cx="18" cy="6" r="3"/><path d="M6 15V9a3 3 0 0 1 3-3h6M18 9v6a3 3 0 0 1-3 3H9"/>',
  fingerprint: '<path d="M12 11a2 2 0 0 0-2 2c0 2-.3 4-1 6M14 13c0 3-.5 5.4-1.5 7M8 13a4 4 0 0 1 8 0c0 1.6-.1 3-.3 4.4M6 13a6 6 0 0 1 12 0M4 13a8 8 0 0 1 16 0"/>'
};

function icon(name, extra) {
  return '<span class="icon ' + (extra || '') + '" aria-hidden="true"><svg viewBox="0 0 24 24">' + (paths[name] || paths.info) + '</svg></span>';
}

const seedCampuses = [
  { id: 'VC-0001', name: '青禾实验学校', region: '广东省', city: '深圳市', devices: 286, users: 42, version: '0.4.6', updated: '12 分钟前', status: '运行中', active: true },
  { id: 'VC-0002', name: '海棠中学', region: '江苏省', city: '南京市', devices: 214, users: 35, version: '0.4.6', updated: '38 分钟前', status: '运行中', active: true },
  { id: 'VC-0003', name: '云杉小学', region: '浙江省', city: '杭州市', devices: 168, users: 27, version: '0.4.5', updated: '1 小时前', status: '运行中', active: true },
  { id: 'VC-0004', name: '启明教育中心', region: '香港特别行政区', city: '九龙', devices: 142, users: 19, version: '0.4.6', updated: '2 小时前', status: '运行中', active: true },
  { id: 'VC-0005', name: '星河学校', region: '四川省', city: '成都市', devices: 123, users: 18, version: '0.4.4', updated: '昨天', status: '待更新', active: false },
  { id: 'VC-0006', name: '松涛外国语学校', region: '湖北省', city: '武汉市', devices: 104, users: 16, version: '0.4.6', updated: '昨天', status: '运行中', active: true },
  { id: 'VC-0007', name: '拾光中学', region: '福建省', city: '厦门市', devices: 88, users: 13, version: '0.4.5', updated: '2 天前', status: '待更新', active: false },
  { id: 'VC-0008', name: '知行实验学校', region: '北京市', city: '北京市', devices: 74, users: 11, version: '0.4.6', updated: '3 天前', status: '运行中', active: true }
];

const seedUsers = [
  { id: 'VCU-08A2', campus: '青禾实验学校', city: '深圳市', region: '广东省', version: '0.4.6', last: '12 分钟前', status: '活跃' },
  { id: 'VCU-19C4', campus: '海棠中学', city: '南京市', region: '江苏省', version: '0.4.6', last: '38 分钟前', status: '活跃' },
  { id: 'VCU-21D7', campus: '云杉小学', city: '杭州市', region: '浙江省', version: '0.4.5', last: '1 小时前', status: '活跃' },
  { id: 'VCU-22F1', campus: '启明教育中心', city: '九龙', region: '香港特别行政区', version: '0.4.6', last: '2 小时前', status: '活跃' },
  { id: 'VCU-36B0', campus: '星河学校', city: '成都市', region: '四川省', version: '0.4.4', last: '昨天', status: '待更新' },
  { id: 'VCU-47E9', campus: '松涛外国语学校', city: '武汉市', region: '湖北省', version: '0.4.6', last: '昨天', status: '活跃' },
  { id: 'VCU-5A15', campus: '拾光中学', city: '厦门市', region: '福建省', version: '0.4.5', last: '2 天前', status: '待更新' },
  { id: 'VCU-6C88', campus: '知行实验学校', city: '北京市', region: '北京市', version: '0.4.6', last: '3 天前', status: '活跃' },
  { id: 'VCU-7E31', campus: '青禾实验学校', city: '深圳市', region: '广东省', version: '0.4.6', last: '4 天前', status: '活跃' }
];

function loadSaved() {
  const defaults = { campuses: seedCampuses.slice(), prefs: { anonymous: true, optIn: true, preciseLocation: false, retention: '90' }, profile: { name: '项目工作区', owner: '演示管理员', region: '中国大陆与香港', timezone: 'Asia/Hong_Kong (UTC+8)' } };
  try {
    const raw = localStorage.getItem('veyon-campus-website-demo-v1');
    if (!raw) return defaults;
    const data = JSON.parse(raw);
    return { campuses: Array.isArray(data.campuses) ? data.campuses : defaults.campuses, prefs: Object.assign({}, defaults.prefs, data.prefs || {}), profile: Object.assign({}, defaults.profile, data.profile || {}) };
  } catch (e) { return defaults; }
}

const saved = loadSaved();
const state = { campuses: saved.campuses, prefs: saved.prefs, profile: saved.profile, range: '30', currentPath: '/', regionFilter: '全部地区', userRegion: '全部地区' };

function persist() {
  try { localStorage.setItem('veyon-campus-website-demo-v1', JSON.stringify({ campuses: state.campuses, prefs: state.prefs, profile: state.profile })); }
  catch (e) { showToast('浏览器未能保存演示设置，请检查本地存储权限。'); }
}

function escapeHtml(value) {
  return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) {
    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
  });
}

function navigate(path) {
  if (!path || path === state.currentPath) return;
  history.pushState({}, '', path);
  render();
  const hashAt = path.indexOf('#');
  if (hashAt >= 0) {
    const anchor = decodeURIComponent(path.slice(hashAt + 1));
    setTimeout(function () { const target = document.getElementById(anchor); if (target) target.scrollIntoView({ behavior: 'smooth' }); }, 0);
  } else window.scrollTo(0, 0);
}

function routeLink(path, label, className) {
  return '<a href="' + path + '" data-route="' + path + '" class="' + (className || '') + '">' + label + '</a>';
}

const siteNavItems = [
  ['/product', '产品能力'], ['/about', '关于项目'], ['/docs', '使用指南'], ['/contact', '联系反馈']
];

function publicHeader() {
  const activePath = state.currentPath;
  return '<header class="site-nav"><div class="site-nav-inner">' +
    routeLink('/', '<img src="/assets/veyon-campus-mark.svg" alt="" /><span class="brand-copy"><strong>Veyon Campus</strong><span>CLASSROOM DEPLOYMENT</span></span>', 'brand-lockup') +
    '<nav class="site-links" aria-label="主导航">' + siteNavItems.map(function (item) { return routeLink(item[0], item[1], activePath === item[0] ? 'active' : ''); }).join('') +
    routeLink('/admin', '进入管理后台 ' + icon('arrow'), 'btn sm') + '</nav>' +
    '<button class="mobile-menu" type="button" data-action="site-menu" aria-label="打开导航">' + icon('menu') + '</button></div></header>';
}

function publicFooter() {
  return '<footer class="site-footer"><div class="site-container"><div class="footer-grid">' +
    '<div class="footer-about">' + routeLink('/', '<img src="/assets/veyon-campus-mark.svg" alt="" /><span class="brand-copy"><strong>Veyon Campus</strong><span>CLASSROOM DEPLOYMENT</span></span>', 'brand-lockup') +
    '<p>为校园机房部署 Veyon 提供中文工具与操作参考，让批量准备过程更清晰、可检查。</p></div>' +
    '<div class="footer-col"><strong>产品</strong>' + routeLink('/product', '产品能力') + routeLink('/admin', '管理后台预览') + routeLink('/about', '关于项目') + '</div>' +
    '<div class="footer-col"><strong>帮助</strong>' + routeLink('/docs', '使用指南') + routeLink('/contact', '问题反馈') + '<a href="https://github.com/Ljc798/veyon-campus-deployment" target="_blank" rel="noopener">GitHub 仓库</a></div>' +
    '<div class="footer-col"><strong>说明</strong>' + routeLink('/privacy', '隐私说明') + '<a href="https://veyon.io/" target="_blank" rel="noopener">Veyon 官方网站</a>' + routeLink('/docs#status', '版本状态') + '</div>' +
    '</div><div class="footer-bottom"><span>© 2026 Veyon Campus · 校园机房部署工具</span><span class="demo-label">本网站为本地预览原型，后台数据为虚构演示数据。</span></div></div></footer>';
}

function publicPage(content) {
  return '<div class="site-shell">' + publicHeader() + '<main class="site-main">' + content + '</main>' + publicFooter() + '</div>';
}

function heroPreview() {
  return '<div class="hero-art"><div class="hero-orbit"></div><div class="preview-board"><div class="preview-top"><i></i><i></i><i></i><span>campus.veyon.local / overview</span></div>' +
    '<div class="preview-content"><aside class="preview-side"><b></b><i></i><i></i><i></i><i></i></aside><div class="preview-main"><div class="preview-title"><b></b><span></span></div><div class="preview-stats"><div class="preview-stat"><i></i><b></b></div><div class="preview-stat"><i></i><b></b></div><div class="preview-stat"><i></i><b></b></div></div>' +
    '<div class="preview-chart"><svg viewBox="0 0 420 120" preserveAspectRatio="none" aria-hidden="true"><path d="M0 102 C35 98,45 72,83 79 S130 87,165 56 S214 72,250 48 S299 62,335 32 S382 50,420 19" fill="none" stroke="#238d79" stroke-width="3"/><path d="M0 102 C35 98,45 72,83 79 S130 87,165 56 S214 72,250 48 S299 62,335 32 S382 50,420 19 L420 120 L0 120 Z" fill="url(#previewFill)" opacity=".45"/><defs><linearGradient id="previewFill" x1="0" y1="0" x2="0" y2="1"><stop stop-color="#61bba1"/><stop offset="1" stop-color="#fff" stop-opacity=".1"/></linearGradient></defs></svg></div></div></div></div>' +
    '<div class="float-chip one"><span class="chip-icon">' + icon('building') + '</span><span><strong>按校区查看</strong><small>地区 · 活跃 · 版本</small></span></div>' +
    '<div class="float-chip two"><span class="chip-icon">' + icon('shield') + '</span><span><strong>隐私优先设计</strong><small>聚合统计 · 主动选择</small></span></div></div>';
}

function featureCard(iconName, title, copy, link, label) {
  return '<article class="feature-card"><span class="feature-icon">' + icon(iconName) + '</span><h3>' + title + '</h3><p>' + copy + '</p>' + routeLink(link, label + ' ' + icon('arrow'), '') + '</article>';
}

function homePage() {
  return publicPage('<section class="hero"><div class="site-container hero-grid"><div class="hero-copy"><span class="eyebrow"><i class="eyebrow-dot"></i>校园机房部署工具</span><h1>让每间教室的<br /><em>部署更有条理</em></h1><p>Veyon Campus 帮助学校管理员准备 Veyon 教师端与学生端环境，并提供按校区查看使用情况的管理后台方案。</p><div class="hero-actions">' + routeLink('/product', '了解产品能力 ' + icon('arrow'), 'btn') + routeLink('/admin', '预览管理后台', 'btn secondary') + '</div><div class="hero-meta"><span>' + icon('device') + ' Windows 桌面应用</span><span>' + icon('globe') + ' 校区汇总视图</span><span>' + icon('shield') + ' 统计方案可选加入</span></div></div>' + heroPreview() + '</div></section>' +
    '<section class="trust-strip"><div class="site-container trust-row"><p>围绕校园机房部署中的重复工作设计，工具状态与统计数据透明呈现。</p><span class="trust-tag">' + icon('building') + ' 校区管理视图</span><span class="trust-tag">' + icon('activity') + ' 使用趋势概览</span><span class="trust-tag">' + icon('lock') + ' 隐私边界清晰</span><span class="trust-tag">' + icon('book') + ' 中文操作指引</span></div></section>' +
    '<section class="section"><div class="site-container"><div class="section-head"><span class="eyebrow"><i class="eyebrow-dot"></i>一个工作流，覆盖多个校区</span><h2>从本地部署到整体了解</h2><p>桌面工具聚焦教师与学生电脑的安装准备；网站原型展示未来如何按校区、地区和版本汇总使用情况。</p></div><div class="feature-grid">' +
    featureCard('device', '教师端与学生端', '教师端准备校区资料与配置，学生端按步骤完成本机安装和设置；不同角色分别打包。', '/product', '查看工具') +
    featureCard('building', '校区和机房概况', '后台按校区、地区查看登记数量、活跃情况和软件版本，帮助维护者了解推广覆盖面。', '/admin/campuses', '预览后台') +
    featureCard('chart', '趋势与版本分布', '将校区自行维护或主动提交的汇总信息放在一个视图里，观察时间趋势和版本更新进展。', '/admin/analytics', '查看分析') +
    '</div></div></section>' +
    '<section class="section tint"><div class="site-container workflow"><div class="workflow-copy"><span class="eyebrow"><i class="eyebrow-dot"></i>清晰的部署路径</span><h2>一套步骤，逐步铺开到更多校区</h2><p>从试装到批量使用，先明确每一步的输入、结果和验收方式。校区使用概况用于帮助维护者规划支持和更新。</p><div style="margin-top:22px">' + routeLink('/docs', '查看使用指南 ' + icon('arrow'), 'btn secondary') + '</div></div><div class="workflow-list">' +
    '<div class="workflow-step"><span class="step-number">01</span><div><h3>整理校区资料</h3><p>规划校区标识、机房名称与计算机编号，并确认目标 Windows 电脑环境。</p></div></div>' +
    '<div class="workflow-step"><span class="step-number">02</span><div><h3>教师端准备配置</h3><p>在教师电脑生成或选择校区配置，按文档核对密钥与目标机列表。</p></div></div>' +
    '<div class="workflow-step"><span class="step-number">03</span><div><h3>学生端逐批试装</h3><p>先在可恢复的测试机上操作，记录结果并完成教师端连接验收。</p></div></div>' +
    '<div class="workflow-step"><span class="step-number">04</span><div><h3>查看校区汇总</h3><p>如果未来启用统计，按明确告知和自愿选择提交必要的匿名汇总信息。</p></div></div>' +
    '</div></div></section>' +
    '<section class="section"><div class="site-container"><div class="callout">' + icon('info') + '<div><strong>关于当前版本与后台原型</strong><p>桌面 App 0.4.6 是 Windows 本地离线部署实验版，仍需完成真实 Windows 环境验收。本网站后台目前只运行虚构演示数据，不接收桌面 App 的使用信息。</p></div></div></div></section>' +
    '<section class="section tint"><div class="site-container"><div class="cta-panel"><div><h2>从了解工具开始</h2><p>阅读操作文档，查看当前能力与已知边界；也可以直接浏览管理后台的本地演示界面。</p></div>' + routeLink('/docs', '打开使用指南 ' + icon('arrow'), 'btn') + '</div></div></section>');
}

function productPage() {
  const cards = [
    ['terminal', '教师端配置准备', '教师控制台用于选择 Veyon 安装、校区配置和目标电脑等操作。密钥相关材料留在教师端的受控用户环境中。', '代码已实现 · 待 Windows 实机验收'],
    ['monitor', '学生端部署工具', '学生部署工具按选择的任务执行安装、校区配置和本机系统设置，运行前展示计划与检查结果。', '本地应用 · 需管理员权限'],
    ['users', '分离的应用角色', '教师控制台与学生部署工具是不同的构建角色，学生端使用的后台代理也单独运行。', '当前构建方案 · ZIP 待实机验收'],
    ['shield', '网站访问策略', '教师端可准备并推送签名的课堂网站策略，学生端代理处理规则与到期状态。', '功能代码已实现 · 浏览器效果待验收'],
    ['database', '校区汇总后台', '本网站设计了校区、地区、版本与活跃度的后台视图；真实数据服务、账号权限和统计接入尚未实现。', '网站原型 · 虚构数据'],
    ['book', '操作文档与验收', '仓库提供部署说明、常见问题、开发规划和 Windows 验收清单，方便按阶段核对实际结果。', '配套文档已提供']
  ];
  return publicPage('<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>产品能力</span><h1>教师端、学生端与校区视图</h1><p>围绕机房部署流程逐步建设。下方会区分已有代码、当前原型和仍需真实环境验收的部分。</p></div></section><section class="page-body"><div class="site-container"><div class="product-grid">' + cards.map(function (c, i) { return '<article class="product-card"><span class="feature-icon">' + icon(c[0]) + '</span><h3>' + c[1] + '</h3><p>' + c[2] + '</p><span class="status-label">' + c[3] + '</span></article>'; }).join('') + '</div><div style="margin-top:24px">' + routeLink('/admin', '预览校区管理后台 ' + icon('arrow'), 'btn') + '</div></div></section>');
}

function aboutPage() {
  return publicPage('<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>关于项目</span><h1>把机房部署经验整理成工具</h1><p>Veyon Campus 是一个围绕校园机房管理需求持续迭代的独立项目，聚焦部署准备、角色分工、中文说明与实际验收。</p></div></section><section class="page-body"><div class="site-container"><div class="story-grid"><div class="story-copy"><span class="eyebrow"><i class="eyebrow-dot"></i>为什么开始</span><h2>减少重复准备，也把风险讲清楚</h2><p>在学校机房里，教师电脑、学生电脑和校区配置往往需要逐台或分批准备。Veyon Campus 尝试把常用步骤整合到 Windows 桌面工具中，配套说明每一步将要修改什么、需要什么权限，以及怎样验证结果。</p><p>项目仍在实验阶段。工具开发、自动化检查和真实 Windows 设备验收不是同一件事；我们会在页面和文档中明确各自的进度。</p></div><aside class="story-panel"><h3>项目实践原则</h3><div class="principle"><span class="feature-icon">' + icon('eye') + '</span><div><strong>行为可预期</strong><span>开始运行前说明计划与权限需求，明确哪些系统设置会被修改。</span></div></div><div class="principle"><span class="feature-icon">' + icon('check') + '</span><div><strong>先验证，再扩大</strong><span>建议先在可恢复的测试环境试装，逐项确认系统状态和连接结果。</span></div></div><div class="principle"><span class="feature-icon">' + icon('lock') + '</span><div><strong>资料边界清晰</strong><span>敏感密钥留在受控环境；未来统计采用告知、主动选择与数据最少化。</span></div></div></aside></div><div class="callout" style="margin-top:28px">' + icon('info') + '<div><strong>项目关系说明</strong><p>本项目围绕第三方 Veyon 软件提供部署辅助。Veyon 本身由其上游项目维护，商标、软件权利和许可按上游声明适用；本仓库也尚未指定开源许可证。</p></div></div></div></section>');
}

function docsPage() {
  return publicPage('<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>使用指南</span><h1>先试装，再逐批部署</h1><p>按真实部署流程了解工具、准备环境、试装一台教师机和一台学生机，并在批量使用前完成检查。</p></div></section><section class="page-body"><div class="site-container docs-layout"><aside class="docs-toc"><strong>本页目录</strong><a href="#start">开始前</a><a href="#flow">部署步骤</a><a href="#status">版本状态</a><a href="#resources">更多文档</a></aside><article class="docs-article"><section id="start"><h2>开始前：了解风险和适用范围</h2><p>请在 Windows 10 或 Windows 11 电脑上操作，并以管理员权限运行需要修改系统设置的部署步骤。当前 App 是 Windows 本地离线部署实验版，Windows 实机兼容性验收仍在进行。</p><div class="callout">' + icon('warning') + '<div><strong>学生端操作可能修改系统设置</strong><p>部署流程可能更改计算机名称、管理员账户密码、学生账户和 Veyon 配置。开始前记录原状态，并准备可恢复环境。</p></div></div></section><section id="flow"><h2>推荐部署步骤</h2><ol><li>明确校区、机房和学生机编号，备份重要配置。</li><li>先在一台教师机和一台学生机上完成试装。</li><li>逐项核对安装、账户、计算机名和 Veyon 服务状态。</li><li>使用教师端完成认证、电脑列表及连接检查。</li><li>试装通过后按批次部署，并记录每台电脑的结果和异常。</li></ol><h3>课堂网站访问策略</h3><p>教师端的网站策略功能通过签名策略与学生端后台代理实现。浏览器效果、权限边界、重启恢复和到期解除仍需要 Windows 实机验收，不应仅凭构建成功推断为已验证功能。</p></section><section id="status"><h2>当前版本状态</h2><p>App 版本为 0.4.6。教师控制台与学生部署工具已拆分构建；策略编译、部署后只读检查和清理入口已有代码。真实 Windows 安装、角色 ZIP 内容、浏览器策略效果和系统恢复仍需按验收清单验证。</p><p>网站管理后台是单独的前端原型，目前不连接桌面 App，不提供真实账号认证或线上数据服务。</p></section><section id="resources"><h2>更多项目文档</h2><div class="docs-resource-list"><a class="resource-link" href="https://github.com/Ljc798/veyon-campus-deployment/blob/develop/README.md" target="_blank" rel="noopener">' + icon('book') + '<span><strong>仓库 README</strong><small>公开版使用说明与原脚本操作步骤</small></span></a><a class="resource-link" href="https://github.com/Ljc798/veyon-campus-deployment/blob/develop/QnA.md" target="_blank" rel="noopener">' + icon('help') + '<span><strong>常见问题</strong><small>安装、账户和部署过程问题排查</small></span></a><a class="resource-link" href="https://github.com/Ljc798/veyon-campus-deployment/blob/develop/docs/README.md" target="_blank" rel="noopener">' + icon('file') + '<span><strong>App 文档入口</strong><small>当前 App 能力、状态和验收资料</small></span></a><a class="resource-link" href="https://github.com/Ljc798/veyon-campus-deployment/blob/develop/docs/Windows%E5%AD%A6%E7%94%9F%E7%AB%AF%E9%BB%91%E7%99%BD%E5%90%8D%E5%8D%95%E4%B8%8E%E5%90%8E%E5%8F%B0%E4%BB%A3%E7%90%86%E8%87%AA%E6%B5%8B%E9%AA%8C%E6%94%B6%E5%8D%95.md" target="_blank" rel="noopener">' + icon('check') + '<span><strong>学生端验收单</strong><small>按环境逐项验证代理与网站规则</small></span></a></div></section></article></div></section>');
}

function contactPage() {
  return publicPage('<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>联系与反馈</span><h1>一起把部署过程做得更清楚</h1><p>报告问题时，请写明使用的工具版本、Windows 环境、预期结果与实际表现。请勿提交密码、私钥或真实学生资料。</p></div></section><section class="page-body"><div class="site-container contact-grid"><div class="contact-card"><h2>发送反馈</h2><p>表单目前只用于本地页面预览，不会发送或保存到服务器。正式反馈请使用仓库 Issues。</p><form id="contact-form"><div class="form-grid"><div class="field"><label for="contact-topic">反馈类型</label><select id="contact-topic" required><option value="">请选择</option><option>使用问题</option><option>功能建议</option><option>文档勘误</option><option>其他</option></select></div><div class="field"><label for="contact-version">工具版本</label><input id="contact-version" placeholder="例如：App 0.4.6" /></div><div class="field full"><label for="contact-message">问题描述</label><textarea id="contact-message" required placeholder="写明操作步骤、预期结果和实际表现"></textarea><span class="field-hint">不要包含密码、私钥、个人信息或真实校区部署包。</span></div><div class="field full"><button class="btn" type="submit">预览提交反馈 ' + icon('arrow') + '</button></div></div></form></div><div class="contact-card"><h2>推荐反馈渠道</h2><p>从仓库提交可复现的问题或文档改进建议，便于维护者跟踪处理。</p><div class="contact-links"><a class="contact-link-card" href="https://github.com/Ljc798/veyon-campus-deployment/issues" target="_blank" rel="noopener"><span class="feature-icon">' + icon('message') + '</span><span><strong>GitHub Issues</strong><small>提交问题、建议或勘误</small></span>' + icon('external') + '</a><a class="contact-link-card" href="https://github.com/Ljc798/veyon-campus-deployment/blob/develop/CONTRIBUTING.md" target="_blank" rel="noopener"><span class="feature-icon">' + icon('book') + '</span><span><strong>参与改进说明</strong><small>查看提交前的注意事项</small></span>' + icon('external') + '</a><a class="contact-link-card" href="https://github.com/Ljc798/veyon-campus-deployment" target="_blank" rel="noopener"><span class="feature-icon">' + icon('file') + '</span><span><strong>项目仓库</strong><small>查看源码、文档和发布记录</small></span>' + icon('external') + '</a></div><div class="privacy-note" style="margin-top:18px">' + icon('shield') + '<span>请不要上传真实部署包、管理员密码、私钥、学生姓名、设备 IP 或可识别个人的信息。</span></div></div></div></section>');
}

function privacyPage() {
  return publicPage('<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>隐私说明</span><h1>统计应该透明，也应该可选择</h1><p>这里说明当前网页原型实际会处理什么，以及未来正式接入使用统计前必须解决哪些问题。</p></div></section><section class="page-body"><div class="site-container"><div class="privacy-card"><h2>当前网页原型</h2><p>管理后台显示的校区、用户、设备、版本和趋势全部为虚构演示数据。原型不会读取桌面 App 的使用记录，也不会向服务器提交统计信息。</p><p>新增校区与设置偏好保存在当前浏览器的 localStorage 中，仅用于本地交互体验。清除该浏览器的站点数据即可移除这些本地演示记录。</p></div><div class="privacy-card"><h2>未来正式统计的设计原则</h2><ul><li>在启用前清楚说明收集项目、用途、保存时长和退出方式，并由管理员主动选择加入。</li><li>优先使用校区自行维护的地区和匿名汇总数字，避免精确定位与个人画像。</li><li>仅收集提供校区覆盖、活跃概况和版本分布所需的最少信息。</li><li>明确角色权限、数据导出与删除流程、留存期限、审计方式和安全责任。</li><li>不得把课堂屏幕、浏览历史、文件内容或学生个人资料用于一般使用统计。</li></ul></div><div class="privacy-card"><h2>上线前还需补齐</h2><p>正式后台仍需实现安全登录、校区隔离与授权、传输保护、服务端数据存储、操作审计和备份恢复，并根据实际部署地区完成适用的隐私与合规审查。当前页面只展示产品设计，不代表这些机制已经上线。</p></div><div style="margin-top:24px">' + routeLink('/admin/settings', '查看后台隐私设置原型 ' + icon('arrow'), 'btn secondary') + '</div></div></section>');
}

function notFoundPage() {
  return publicPage('<section class="page-hero" style="padding:100px 0"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>页面不存在</span><h1>找不到这个页面</h1><p>地址可能已经变更，或者页面还没有加入网站。</p><div style="margin-top:25px">' + routeLink('/', '返回首页 ' + icon('arrow'), 'btn') + '</div></div></section>');
}

const adminNav = [
  { path: '/admin', label: '数据总览', icon: 'grid' },
  { path: '/admin/campuses', label: '校区管理', icon: 'building', count: '26' },
  { path: '/admin/usage', label: '用户与终端', icon: 'users' },
  { path: '/admin/analytics', label: '趋势分析', icon: 'chart' }
];

function adminTitle(path) {
  const titles = { '/admin': ['数据总览', 'Veyon Campus 使用概况'], '/admin/campuses': ['校区管理', '校区与地区'], '/admin/usage': ['用户与终端', '活跃情况与版本'], '/admin/analytics': ['趋势分析', '增长、地区与版本'], '/admin/settings': ['后台设置', '偏好与隐私方案'] };
  return titles[path] || titles['/admin'];
}

function adminShell(content) {
  const path = state.currentPath;
  const title = adminTitle(path);
  return '<div class="admin-shell"><aside class="sidebar" id="sidebar"><div class="sidebar-brand"><img src="/assets/veyon-campus-mark.svg" alt="" /><div><strong>Veyon Campus</strong><small>ADMIN CONSOLE</small></div></div>' +
    '<div class="workspace-switch"><span class="workspace-avatar">VC</span><span><strong>' + escapeHtml(state.profile.name || '项目工作区') + '</strong><small>演示环境</small></span>' + icon('down') + '</div>' +
    '<div class="nav-section-label">管理</div><nav class="side-nav" aria-label="管理后台导航">' + adminNav.map(function (item) { const count = item.path === '/admin/campuses' ? String(26 + Math.max(0, state.campuses.length - seedCampuses.length)) : item.count; return routeLink(item.path, icon(item.icon) + '<span>' + item.label + '</span>' + (count ? '<small class="nav-count">' + count + '</small>' : ''), path === item.path ? 'active' : ''); }).join('') + '</nav>' +
    '<div class="nav-section-label" style="margin-top:24px">工作区</div><nav class="side-nav">' + routeLink('/admin/settings', icon('settings') + '<span>后台设置</span>', path === '/admin/settings' ? 'active' : '') + routeLink('/docs', icon('book') + '<span>使用文档</span>') + '</nav>' +
    '<div class="sidebar-spacer"></div><div class="sidebar-help"><strong>需要帮助？</strong><p>查看本地预览说明与当前功能边界。</p>' + routeLink('/contact', '联系与反馈 ' + icon('arrow'), '') + '</div><div class="sidebar-profile"><span class="profile-avatar">管</span><span><strong>' + escapeHtml(state.profile.owner || '演示管理员') + '</strong><small>仅本地预览</small></span>' + icon('down') + '</div></aside>' +
    '<div class="mobile-sidebar-overlay" data-action="sidebar-close"></div><div class="admin-main"><header class="admin-topbar"><div class="topbar-title"><button class="mobile-sidebar-toggle" type="button" data-action="sidebar-open" aria-label="打开管理菜单">' + icon('menu') + '</button><div><h1>' + title[0] + '</h1><small>' + title[1] + '</small></div></div><div class="topbar-actions"><button class="search-button" type="button" data-action="open-search">' + icon('search') + '<span>搜索页面或校区…</span><kbd>⌘ K</kbd></button><span class="topbar-divider"></span><button class="icon-button" type="button" data-action="notify" aria-label="通知">' + icon('bell') + '</button><span class="topbar-demo">' + icon('spark') + '演示数据</span></div></header><main class="admin-content">' + content + '</main></div></div>';
}

function demoBanner() {
  return '<div class="demo-banner">' + icon('info') + '<span><strong>本地演示环境</strong> — 数据为虚构示例，保存在当前浏览器；目前没有接入真实校区或桌面 App。</span>' + routeLink('/privacy', '了解数据说明 ' + icon('arrow'), '') + '</div>';
}

function pageHeading(title, sub, actions) {
  return '<div class="page-heading"><div><h2>' + title + '</h2><p>' + sub + '</p></div><div class="heading-actions">' + (actions || '') + '</div></div>';
}

function statCard(label, value, change, detail, iconName, direction) {
  return '<article class="card stat-card"><div class="stat-top"><span>' + label + '</span><span class="stat-icon">' + icon(iconName) + '</span></div><div class="stat-value">' + value + '</div><div class="stat-bottom"><span class="trend ' + (direction === 'down' ? 'down' : '') + '">' + icon(direction === 'down' ? 'arrowDown' : 'arrowUp') + change + '</span><span>' + detail + '</span></div></article>';
}

function lineChart(range) {
  const vals = range === '7' ? [17, 25, 21, 38, 33, 48, 44, 62, 57, 71, 68, 83] : (range === '90' ? [13, 17, 20, 24, 29, 33, 37, 45, 53, 61, 68, 82] : [12, 18, 23, 25, 36, 42, 38, 54, 57, 64, 71, 84]);
  const points = vals.map(function (v, i) { return { x: 38 + i * 48, y: 184 - v * 1.55 }; });
  const line = points.map(function (p, i) { return (i ? 'L' : 'M') + p.x + ' ' + p.y; }).join(' ');
  const area = line + ' L ' + points[points.length - 1].x + ' 190 L ' + points[0].x + ' 190 Z';
  const old = points.map(function (p, i) { return (i ? 'L' : 'M') + p.x + ' ' + (Math.min(179, p.y + 24 + (i % 3) * 5)); }).join(' ');
  return '<svg viewBox="0 0 590 205" role="img" aria-label="活跃校区数量趋势" preserveAspectRatio="none"><defs><linearGradient id="chart-fill" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="#4daf96" stop-opacity=".18"/><stop offset="1" stop-color="#4daf96" stop-opacity="0"/></linearGradient></defs>' +
    [24, 68, 112, 156, 190].map(function (y) { return '<line x1="38" y1="' + y + '" x2="566" y2="' + y + '" stroke="#edf1ef" stroke-width="1"/>'; }).join('') +
    '<path d="' + area + '" fill="url(#chart-fill)"/><path d="' + old + '" fill="none" stroke="#bdcfca" stroke-width="2" stroke-dasharray="5 6"/><path d="' + line + '" fill="none" stroke="#238d79" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/>' +
    points.map(function (p, i) { return i === points.length - 1 ? '<circle cx="' + p.x + '" cy="' + p.y + '" r="5" fill="#fff" stroke="#238d79" stroke-width="3"/>' : ''; }).join('') + '</svg>';
}

function regionBars() {
  const data = [['广东省', '428', 100], ['江苏省', '318', 74], ['浙江省', '290', 68], ['香港', '246', 57], ['四川省', '224', 52], ['湖北省', '186', 43]];
  return data.map(function (item) { return '<div class="region-row"><strong>' + item[0] + '</strong><div class="bar-track"><div class="bar-fill" style="width:' + item[2] + '%"></div></div><small>' + item[1] + '</small></div>'; }).join('');
}

function campusRows(items) {
  return items.map(function (c) {
    const initial = escapeHtml(c.name.slice(0, 1));
    const statusClass = c.status === '待更新' ? 'warn' : (c.status === '未开始' ? 'neutral' : '');
    return '<tr><td><div class="campus-name"><span class="campus-mark">' + initial + '</span><span><strong>' + escapeHtml(c.name) + '</strong><small>' + escapeHtml(c.id) + ' · 演示校区</small></span></div></td><td>' + escapeHtml(c.region) + ' · ' + escapeHtml(c.city) + '</td><td>' + Number(c.devices || 0).toLocaleString('zh-CN') + '</td><td>' + Number(c.users || 0) + '</td><td>' + escapeHtml(c.version) + '</td><td><span class="pill ' + statusClass + '">' + escapeHtml(c.status) + '</span></td><td>' + escapeHtml(c.updated) + '</td><td><button class="row-action" type="button" data-action="campus-detail" data-id="' + escapeHtml(c.id) + '" aria-label="查看校区详情">' + icon('dots') + '</button></td></tr>';
  }).join('');
}

function dashboardPage() {
  const actions = '<select class="control-select" data-action="range" aria-label="选择统计时间范围"><option value="7" ' + (state.range === '7' ? 'selected' : '') + '>最近 7 天</option><option value="30" ' + (state.range === '30' ? 'selected' : '') + '>最近 30 天</option><option value="90" ' + (state.range === '90' ? 'selected' : '') + '>最近 90 天</option></select><button class="btn secondary sm" type="button" data-action="export-dashboard">' + icon('download') + '导出报表</button>';
  const recent = state.campuses.slice(0, 5);
  return demoBanner() + pageHeading('早上好，管理员', '这是本地演示工作区的校区使用概况。数据区间：最近 ' + state.range + ' 天。', actions) +
    '<div class="stat-grid">' + statCard('匿名使用账号', '2,384', '12.8%', '较上一周期', 'users') + statCard('已登记校区', '26', '4 个', '新增校区', 'building') + statCard('活跃终端', '7,812', '8.2%', '较上一周期', 'device') + statCard('当前版本占比', '82.4%', '6.1%', '较上一周期', 'chart') + '</div>' +
    '<div class="dashboard-grid"><section class="card card-pad chart-card"><div class="card-heading"><div><h3>活跃校区趋势</h3><p>所选周期内有使用记录的校区数量</p></div><div class="chart-legend"><span><i class="legend-dot"></i>本周期</span><span><i class="legend-dot muted-dot"></i>上周期</span></div></div><div class="chart-wrap">' + lineChart(state.range) + '</div><div class="chart-labels"><span>第 1 天</span><span>第 ' + Math.max(2, Math.round(Number(state.range) / 3)) + ' 天</span><span>第 ' + Math.max(3, Math.round(Number(state.range) * 2 / 3)) + ' 天</span><span>今天</span></div></section>' +
    '<section class="card card-pad"><div class="card-heading"><div><h3>地区分布</h3><p>按管理员登记地区汇总 · 演示数据</p></div>' + routeLink('/admin/analytics', '查看分析 ' + icon('chevron'), '') + '</div><div class="region-list">' + regionBars() + '</div><div class="region-footnote">仅显示示例地区。正式统计应使用校区主动维护的地区信息。</div></section></div>' +
    '<div class="activity-grid"><section class="card table-card"><div class="card-pad"><div class="card-heading"><div><h3>最近校区</h3><p>近期登记或更新的演示记录</p></div>' + routeLink('/admin/campuses', '全部校区 ' + icon('chevron'), '') + '</div></div><div class="table-wrap"><table><thead><tr><th>校区</th><th>地区</th><th>终端</th><th>账号</th><th>版本</th><th>状态</th></tr></thead><tbody>' + recent.map(function (c) { return '<tr><td><div class="campus-name"><span class="campus-mark">' + escapeHtml(c.name.slice(0, 1)) + '</span><span><strong>' + escapeHtml(c.name) + '</strong><small>' + escapeHtml(c.id) + '</small></span></div></td><td>' + escapeHtml(c.region) + ' · ' + escapeHtml(c.city) + '</td><td>' + Number(c.devices || 0) + '</td><td>' + Number(c.users || 0) + '</td><td>' + escapeHtml(c.version) + '</td><td><span class="pill ' + (c.status === '待更新' ? 'warn' : '') + '">' + escapeHtml(c.status) + '</span></td></tr>'; }).join('') + '</tbody></table></div><div class="table-footer"><span>显示 ' + recent.length + ' 条演示记录</span>' + routeLink('/admin/campuses', '管理校区 ' + icon('arrow'), '') + '</div></section>' +
    '<section class="card card-pad"><div class="card-heading"><div><h3>最近动态</h3><p>演示工作区的示例事件</p></div>' + icon('dots') + '</div><div class="activity-list"><div class="activity-item"><span class="activity-icon">' + icon('building') + '</span><div><p><strong>海棠中学</strong> 更新了校区版本信息</p><small>江苏省 · 南京市</small></div><time>38 分钟前</time></div><div class="activity-item"><span class="activity-icon">' + icon('users') + '</span><div><p><strong>匿名使用账号</strong> 完成一次版本检查</p><small>广东省 · 深圳市</small></div><time>1 小时前</time></div><div class="activity-item"><span class="activity-icon">' + icon('shield') + '</span><div><p><strong>青禾实验学校</strong> 完成演示状态同步</p><small>广东省 · 深圳市</small></div><time>2 小时前</time></div></div><div class="privacy-note">' + icon('lock') + '<span>上方活动为虚构示例，不代表有真实用户或设备向本网站报告数据。</span></div></section></div>';
}

function campusPage() {
  const addAction = '<button class="btn sm" type="button" data-action="open-add-campus">' + icon('plus') + '新增校区</button>';
  const regions = ['全部地区'].concat(Array.from(new Set(state.campuses.map(function (c) { return c.region; }))));
  const opts = regions.map(function (r) { return '<option value="' + escapeHtml(r) + '" ' + (state.regionFilter === r ? 'selected' : '') + '>' + escapeHtml(r) + '</option>'; }).join('');
  return demoBanner() + pageHeading('校区管理', '维护校区地区信息，查看汇总终端与使用账号。校区名称为演示内容。', addAction) +
    '<div class="stat-grid">' + statCard('已登记校区', String(26 + Math.max(0, state.campuses.length - seedCampuses.length)), '4 个', '本月新增', 'building') + statCard('覆盖地区', '12', '3 个', '较上一周期', 'pin') + statCard('登记终端', '7,812', '8.2%', '较上一周期', 'device') + statCard('待关注校区', '3', '2 个', '版本较旧', 'warning', 'down') + '</div>' +
    '<div class="toolbar"><div class="search-field">' + icon('search') + '<input type="search" data-search-table="campus" placeholder="搜索校区名称、地区或编号…" aria-label="搜索校区" /></div><select class="control-select" data-filter="campus-region" aria-label="按地区筛选">' + opts + '</select><button class="btn secondary sm" type="button" data-action="export-campuses">' + icon('download') + '导出 CSV</button></div>' +
    '<section class="card table-card"><div class="table-wrap"><table><thead><tr><th>校区名称</th><th>地区 / 城市</th><th>登记终端</th><th>使用账号</th><th>版本</th><th>状态</th><th>最近更新</th><th></th></tr></thead><tbody id="campus-table-body">' + campusRows(state.campuses) + '</tbody></table></div><div class="pagination"><span id="campus-count">共 ' + state.campuses.length + ' 条演示记录</span><div class="page-buttons"><button type="button" aria-label="上一页">‹</button><button class="active" type="button">1</button><button type="button">2</button><button type="button">3</button><button type="button" aria-label="下一页">›</button></div></div></section><div class="privacy-note">' + icon('info') + '<span>新增记录仅保存在当前浏览器，用于演示校区维护交互。正式环境需要校区管理员账号、授权和服务端数据保存。</span></div>';
}

function usagePage() {
  const regions = ['全部地区'].concat(Array.from(new Set(seedUsers.map(function (u) { return u.region; }))));
  const opts = regions.map(function (r) { return '<option value="' + escapeHtml(r) + '" ' + (state.userRegion === r ? 'selected' : '') + '>' + escapeHtml(r) + '</option>'; }).join('');
  const rows = seedUsers.filter(function (u) { return state.userRegion === '全部地区' || u.region === state.userRegion; });
  return demoBanner() + pageHeading('用户与终端', '查看匿名账号、使用地区、桌面端版本与最近活动。没有个人姓名或精确定位。', '<select class="control-select" data-action="range" aria-label="选择统计周期"><option value="7" ' + (state.range === '7' ? 'selected' : '') + '>最近 7 天</option><option value="30" ' + (state.range === '30' ? 'selected' : '') + '>最近 30 天</option><option value="90" ' + (state.range === '90' ? 'selected' : '') + '>最近 90 天</option></select><button class="btn secondary sm" type="button" data-action="export-users">' + icon('download') + '导出 CSV</button>') +
    '<div class="usage-summary">' + statCard('匿名账号', '2,384', '12.8%', '较上一周期', 'users') + statCard('近 30 天活跃', '1,846', '9.4%', '较上一周期', 'activity') + statCard('已登记终端', '7,812', '8.2%', '较上一周期', 'device') + '</div>' +
    '<div class="two-col"><section class="card card-pad"><div class="card-heading"><div><h3>客户端版本</h3><p>已登记终端的版本分布 · 演示数据</p></div>' + routeLink('/admin/analytics', '趋势分析 ' + icon('chevron'), '') + '</div><div class="version-row"><strong>0.4.6</strong><div class="version-track"><span style="width:82.4%"></span></div><small>82.4%</small></div><div class="version-row"><strong>0.4.5</strong><div class="version-track"><span class="alt" style="width:11.2%"></span></div><small>11.2%</small></div><div class="version-row"><strong>0.4.4</strong><div class="version-track"><span class="old" style="width:6.4%"></span></div><small>6.4%</small></div><div class="usage-callout" style="margin-top:20px"><strong>版本状态说明</strong>版本占比只用于展示后台视图，不代表该版本已经完成目标 Windows 设备验收。</div></section><section class="card card-pad"><div class="card-heading"><div><h3>地区概况</h3><p>地区由校区或用户主动登记</p></div>' + icon('globe') + '</div><div class="region-list">' + regionBars() + '</div><div class="usage-callout" style="margin-top:15px">位置粒度以省 / 市为主，不使用后台偷偷读取的精确位置。</div></section></div>' +
    '<div class="toolbar"><div class="search-field">' + icon('search') + '<input type="search" data-search-table="usage" placeholder="搜索匿名账号、校区或地区…" aria-label="搜索匿名账号" /></div><select class="control-select" data-filter="user-region" aria-label="按地区筛选">' + opts + '</select><select class="control-select" aria-label="按活跃状态筛选"><option>全部状态</option><option>活跃</option><option>待更新</option></select></div>' +
    '<section class="card table-card"><div class="table-wrap"><table><thead><tr><th>匿名账号 ID</th><th>校区</th><th>登记地区</th><th>客户端版本</th><th>最近活动</th><th>状态</th><th></th></tr></thead><tbody id="usage-table-body">' + usageRows(rows) + '</tbody></table></div><div class="pagination"><span id="usage-count">显示 ' + rows.length + ' 条虚构示例记录</span><div class="page-buttons"><button class="active" type="button">1</button><button type="button">2</button><button type="button">3</button></div></div></section><div class="privacy-note">' + icon('shield') + '<span>账号标识为虚构且已匿名化示例。正式统计需明确告知、自愿启用，并提供退出和数据删除方式。</span></div>';
}

function usageRows(items) {
  return items.map(function (u) { return '<tr><td><strong style="color:#425966">' + escapeHtml(u.id) + '</strong><small style="display:block;margin-top:3px;color:#98a3a8">匿名演示 ID</small></td><td>' + escapeHtml(u.campus) + '</td><td>' + escapeHtml(u.region) + ' · ' + escapeHtml(u.city) + '</td><td>' + escapeHtml(u.version) + '</td><td>' + escapeHtml(u.last) + '</td><td><span class="pill ' + (u.status === '待更新' ? 'warn' : '') + '">' + escapeHtml(u.status) + '</span></td><td><button class="row-action" type="button" data-action="user-detail" data-id="' + escapeHtml(u.id) + '" aria-label="查看匿名记录说明">' + icon('dots') + '</button></td></tr>'; }).join('');
}

function analyticsPage() {
  const actions = '<select class="control-select" data-action="range" aria-label="选择统计周期"><option value="7" ' + (state.range === '7' ? 'selected' : '') + '>最近 7 天</option><option value="30" ' + (state.range === '30' ? 'selected' : '') + '>最近 30 天</option><option value="90" ' + (state.range === '90' ? 'selected' : '') + '>最近 90 天</option></select><button class="btn secondary sm" type="button" data-action="export-dashboard">' + icon('download') + '导出分析</button>';
  const region = [['广东省', 100, '428'], ['江苏省', 74, '318'], ['浙江省', 68, '290'], ['香港', 57, '246'], ['四川省', 52, '224'], ['湖北省', 43, '186'], ['福建省', 36, '154']];
  return demoBanner() + pageHeading('趋势分析', '用汇总指标了解区域覆盖、活跃变化与版本升级情况。', actions) +
    '<div class="analytics-grid"><section class="card card-pad"><div class="card-heading"><div><h3>活跃账号趋势</h3><p>按周期汇总的匿名账号数</p></div>' + icon('activity') + '</div><div class="metric-highlight"><strong>1,846</strong><span>近 30 天活跃账号</span></div><div class="sparkline">' + lineChart(state.range) + '</div></section><section class="card card-pad"><div class="card-heading"><div><h3>活跃校区趋势</h3><p>至少有一次状态更新的演示校区</p></div>' + icon('building') + '</div><div class="metric-highlight"><strong>22</strong><span>近 30 天活跃校区 · 示例</span></div><div class="sparkline">' + lineChart('7') + '</div></section></div>' +
    '<div class="analytics-grid"><section class="card card-pad"><div class="card-heading"><div><h3>地区覆盖排行</h3><p>登记校区所在的地区分布</p></div><span class="pill neutral">省 / 市</span></div>' + region.map(function (r) { return '<div class="region-row" style="grid-template-columns:66px 1fr 40px;margin:14px 0"><strong>' + r[0] + '</strong><div class="bar-track"><div class="bar-fill" style="width:' + r[1] + '%"></div></div><small>' + r[2] + '</small></div>'; }).join('') + '<div class="region-footnote">数据为演示值；正式统计使用登记地区，低样本量地区可合并展示。</div></section><section class="card card-pad"><div class="card-heading"><div><h3>版本升级漏斗</h3><p>展示版本迭代进展的分析视图</p></div>' + icon('chart') + '</div><div class="funnel-row"><span>当前版本 0.4.6</span><div class="funnel-bar"><i style="width:82.4%"></i></div><span>82.4%</span></div><div class="funnel-row"><span>上一版本 0.4.5</span><div class="funnel-bar"><i style="width:11.2%;background:#67bba3"></i></div><span>11.2%</span></div><div class="funnel-row"><span>较早版本</span><div class="funnel-bar"><i style="width:6.4%;background:#ddb16d"></i></div><span>6.4%</span></div><div class="usage-callout" style="margin-top:20px"><strong>建议关注</strong>对较早版本的校区提供升级说明，并先完成目标系统上的兼容性验收。</div></section></div>' +
    '<div class="callout">' + icon('info') + '<div><strong>阅读这些图表时请注意</strong><p>全部趋势、人数、终端数和地区值都是为本地页面演示而创建的虚构数据，不能用于判断真实推广规模。</p></div></div>';
}

function toggleButton(key, value, label) {
  return '<button class="toggle" type="button" role="switch" aria-checked="' + (value ? 'true' : 'false') + '" data-setting="' + key + '" aria-label="' + label + '"></button>';
}

function settingsPage() {
  return demoBanner() + pageHeading('后台设置', '偏好仅保存在当前浏览器，用于体验本地后台原型。', '<button class="btn secondary sm" type="button" data-action="reset-demo">' + icon('refresh') + '重置演示数据</button>') +
    '<div class="settings-layout"><nav class="card settings-nav" aria-label="设置导航"><a class="active" href="#profile">' + icon('building') + '工作区信息</a><a href="#privacy-settings">' + icon('shield') + '隐私与统计</a><a href="#data-settings">' + icon('database') + '数据与导出</a></nav><div class="settings-form">' +
    '<section class="card settings-section" id="profile"><h3>工作区信息</h3><p>本地原型工作区资料，提交后保存在当前浏览器。</p><div class="form-grid"><div class="field"><label for="workspace-name">工作区名称</label><input id="workspace-name" maxlength="80" value="' + escapeHtml(state.profile.name) + '" /></div><div class="field"><label for="workspace-owner">管理员显示名称</label><input id="workspace-owner" maxlength="60" value="' + escapeHtml(state.profile.owner) + '" /></div><div class="field"><label for="workspace-region">主要地区</label><select id="workspace-region"><option ' + (state.profile.region === '中国大陆与香港' ? 'selected' : '') + '>中国大陆与香港</option><option ' + (state.profile.region === '仅中国大陆' ? 'selected' : '') + '>仅中国大陆</option><option ' + (state.profile.region === '其他地区' ? 'selected' : '') + '>其他地区</option></select></div><div class="field"><label for="workspace-timezone">时区</label><select id="workspace-timezone"><option ' + (state.profile.timezone === 'Asia/Hong_Kong (UTC+8)' ? 'selected' : '') + '>Asia/Hong_Kong (UTC+8)</option><option ' + (state.profile.timezone === 'Asia/Shanghai (UTC+8)' ? 'selected' : '') + '>Asia/Shanghai (UTC+8)</option></select></div></div></section>' +
    '<section class="card settings-section" id="privacy-settings"><h3>隐私与统计方案预览</h3><p>这些选项表达未来正式服务的预期方案，不会开启真实遥测或数据上传。</p><div class="settings-row"><span><strong>展示匿名汇总信息</strong><small>默认只呈现校区数量、地区与版本分布等汇总数据。</small></span>' + toggleButton('anonymous', state.prefs.anonymous, '展示匿名汇总信息') + '</div><div class="settings-row"><span><strong>校区主动加入统计</strong><small>真实产品启用统计前，应由校区管理员明确选择加入并可随时撤回。</small></span>' + toggleButton('optIn', state.prefs.optIn, '校区主动加入统计') + '</div><div class="settings-row"><span><strong>精确位置数据</strong><small>原型默认关闭。当前不采集位置；建议使用校区登记的省 / 市地区。</small></span>' + toggleButton('preciseLocation', state.prefs.preciseLocation, '精确位置数据') + '</div><div class="settings-row"><span><strong>演示数据保留期限</strong><small>正式服务上线前需要定义服务端数据的删除和留存规则。</small></span><select class="control-select" data-setting-select="retention"><option value="30" ' + (state.prefs.retention === '30' ? 'selected' : '') + '>30 天</option><option value="90" ' + (state.prefs.retention === '90' ? 'selected' : '') + '>90 天</option><option value="180" ' + (state.prefs.retention === '180' ? 'selected' : '') + '>180 天</option></select></div></section>' +
    '<section class="card settings-section" id="data-settings"><h3>演示数据与导出</h3><p>清除或导出演示数据不会影响桌面 App 与真实部署数据。</p><div class="settings-row"><span><strong>浏览器本地保存</strong><small>当前浏览器中保存新增校区与偏好，未传输到外部服务。</small></span><span class="pill neutral">localStorage</span></div><div class="settings-row"><span><strong>导出演示数据</strong><small>生成 CSV 文件，便于查看表格导出交互。</small></span><button class="btn secondary sm" type="button" data-action="export-campuses">' + icon('download') + '导出校区 CSV</button></div><div class="settings-actions"><button class="btn sm" type="button" data-action="save-settings">保存设置</button></div></section>' +
    '</div></div>';
}

function adminPage() {
  let content;
  if (state.currentPath === '/admin/campuses') content = campusPage();
  else if (state.currentPath === '/admin/usage') content = usagePage();
  else if (state.currentPath === '/admin/analytics') content = analyticsPage();
  else if (state.currentPath === '/admin/settings') content = settingsPage();
  else content = dashboardPage();
  return adminShell(content);
}

function render() {
  const path = window.location.pathname.replace(/\/$/, '') || '/';
  state.currentPath = path;
  if (path === '/admin' || path.indexOf('/admin/') === 0) app.innerHTML = adminPage();
  else if (path === '/') app.innerHTML = homePage();
  else if (path === '/product') app.innerHTML = productPage();
  else if (path === '/about') app.innerHTML = aboutPage();
  else if (path === '/docs') app.innerHTML = docsPage();
  else if (path === '/contact') app.innerHTML = contactPage();
  else if (path === '/privacy') app.innerHTML = privacyPage();
  else app.innerHTML = notFoundPage();
  document.title = (path.indexOf('/admin') === 0 ? adminTitle(path)[0] + ' · 管理后台' : (path === '/' ? '校园机房部署工具' : (siteNavItems.find(function (x) { return x[0] === path; }) || ['', 'Veyon Campus'])[1])) + ' · Veyon Campus';
}

let toastTimer;
function showToast(message) {
  if (!toastEl) return;
  toastEl.className = 'toast show';
  toastEl.innerHTML = icon('check') + '<span>' + escapeHtml(message) + '</span>';
  clearTimeout(toastTimer);
  toastTimer = setTimeout(function () { toastEl.className = 'toast'; }, 2800);
}

function filteredCampuses(query) {
  const q = (query || '').trim().toLowerCase();
  return state.campuses.filter(function (c) {
    const matchesRegion = state.regionFilter === '全部地区' || c.region === state.regionFilter;
    const text = [c.id, c.name, c.region, c.city].join(' ').toLowerCase();
    return matchesRegion && (!q || text.indexOf(q) >= 0);
  });
}

function refreshCampusTable(query) {
  const body = document.getElementById('campus-table-body');
  const count = document.getElementById('campus-count');
  if (!body || !count) return;
  const rows = filteredCampuses(query);
  body.innerHTML = rows.length ? campusRows(rows) : '<tr><td colspan="8"><div class="empty-state">' + icon('search') + '没有找到符合条件的校区</div></td></tr>';
  count.textContent = '共 ' + rows.length + ' 条演示记录';
}

function filteredUsers(query) {
  const q = (query || '').trim().toLowerCase();
  return seedUsers.filter(function (u) {
    const matchesRegion = state.userRegion === '全部地区' || u.region === state.userRegion;
    const text = [u.id, u.campus, u.region, u.city, u.version].join(' ').toLowerCase();
    return matchesRegion && (!q || text.indexOf(q) >= 0);
  });
}

function refreshUsageTable(query) {
  const body = document.getElementById('usage-table-body');
  const count = document.getElementById('usage-count');
  if (!body || !count) return;
  const rows = filteredUsers(query);
  body.innerHTML = rows.length ? usageRows(rows) : '<tr><td colspan="7"><div class="empty-state">' + icon('search') + '没有找到符合条件的记录</div></td></tr>';
  count.textContent = '显示 ' + rows.length + ' 条虚构示例记录';
}

function csvEscape(value) { return '"' + String(value == null ? '' : value).replace(/"/g, '""') + '"'; }
function downloadCsv(name, headers, rows) {
  const data = '\ufeff' + [headers, ...rows].map(function (row) { return row.map(csvEscape).join(','); }).join('\r\n');
  const blob = new Blob([data], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.click();
  setTimeout(function () { URL.revokeObjectURL(url); }, 500);
  showToast('已导出本地演示 CSV。');
}

function exportCampuses() {
  downloadCsv('veyon-campus-demo-campuses.csv', ['校区编号', '校区名称', '地区', '城市', '终端数', '账号数', '版本', '状态'], state.campuses.map(function (c) { return [c.id, c.name, c.region, c.city, c.devices, c.users, c.version, c.status]; }));
}

function openModal(title, subtitle, body, footer) {
  const modalHtml = '<div class="modal-backdrop" data-action="backdrop-close"><section class="modal" role="dialog" aria-modal="true" aria-labelledby="modal-title"><header class="modal-header"><div><h2 id="modal-title">' + title + '</h2><p>' + subtitle + '</p></div><button class="row-action" type="button" data-action="close-modal" aria-label="关闭">' + icon('close') + '</button></header><div class="modal-body">' + body + '</div>' + (footer ? '<footer class="modal-footer">' + footer + '</footer>' : '') + '</section></div>';
  app.insertAdjacentHTML('beforeend', modalHtml);
}

function addCampusModal() {
  const body = '<form id="add-campus-form"><div class="form-grid"><div class="field full"><label for="new-campus-name">校区名称 *</label><input id="new-campus-name" name="name" required maxlength="80" placeholder="例如：春晓实验学校" /></div><div class="field"><label for="new-campus-region">省 / 地区 *</label><input id="new-campus-region" name="region" required maxlength="40" placeholder="例如：广东省" /></div><div class="field"><label for="new-campus-city">城市 *</label><input id="new-campus-city" name="city" required maxlength="40" placeholder="例如：深圳市" /></div></div><div class="privacy-note" style="margin-top:16px">' + icon('info') + '<span>本地演示只保存校区名称与地区，不添加真实联系人信息。</span></div></form>';
  const footer = '<button class="btn secondary sm" type="button" data-action="close-modal">取消</button><button class="btn sm" type="submit" form="add-campus-form">保存校区</button>';
  openModal('新增校区', '添加一条仅供本地预览使用的演示记录。', body, footer);
  setTimeout(function () { const input = document.getElementById('new-campus-name'); if (input) input.focus(); }, 50);
}

function campusDetail(id) {
  const c = state.campuses.find(function (x) { return x.id === id; });
  if (!c) return;
  const body = '<div class="usage-summary" style="margin:0 0 16px">' + statCard('登记终端', Number(c.devices || 0).toLocaleString('zh-CN'), '—', '演示值', 'device') + statCard('匿名账号', String(c.users || 0), '—', '演示值', 'users') + statCard('客户端版本', escapeHtml(c.version), '—', '示例版本', 'chart') + '</div><div class="privacy-card"><h2>' + escapeHtml(c.name) + '</h2><p>校区编号：' + escapeHtml(c.id) + '</p><p>地区：' + escapeHtml(c.region) + ' · ' + escapeHtml(c.city) + '</p><p>最近更新：' + escapeHtml(c.updated) + '</p><span class="pill ' + (c.status === '待更新' ? 'warn' : (c.status === '未开始' ? 'neutral' : '')) + '">' + escapeHtml(c.status) + '</span></div><div class="privacy-note">' + icon('info') + '<span>这是虚构演示校区。正式后台需要定义校区管理员权限及校区间的数据隔离。</span></div>';
  openModal('校区详情', '演示校区资料与汇总情况。', body, '<button class="btn secondary sm" type="button" data-action="close-modal">关闭</button>');
}

function openSearch() {
  if (document.getElementById('search-overlay')) return;
  const results = adminNav.map(function (item) { return routeLink(item.path, icon(item.icon) + item.label, ''); }).join('');
  app.insertAdjacentHTML('beforeend', '<div class="search-overlay" id="search-overlay" data-action="search-backdrop"><div class="search-dialog"><input type="search" id="global-search" placeholder="搜索校区或跳转页面…" aria-label="搜索校区或页面" /><div class="search-results"><small>快速跳转</small>' + results + routeLink('/admin/settings', icon('settings') + '后台设置', '') + '</div></div></div>');
  setTimeout(function () { const input = document.getElementById('global-search'); if (input) input.focus(); }, 30);
}

function closeSearch() { const el = document.getElementById('search-overlay'); if (el) el.remove(); }

app.addEventListener('click', function (event) {
  const route = event.target.closest('[data-route]');
  if (route) {
    event.preventDefault();
    navigate(route.getAttribute('data-route'));
    return;
  }
  const action = event.target.closest('[data-action]');
  if (!action) {
    if (event.target.classList.contains('modal-backdrop')) event.target.remove();
    if (event.target.id === 'search-overlay') closeSearch();
    return;
  }
  const name = action.getAttribute('data-action');
  if (name === 'site-menu') { const menu = app.querySelector('.site-links'); if (menu) menu.classList.toggle('mobile-open'); }
  if (name === 'open-search') openSearch();
  if (name === 'search-backdrop' && event.target === action) closeSearch();
  if (name === 'open-add-campus') addCampusModal();
  if (name === 'close-modal') { const modal = action.closest('.modal-backdrop'); if (modal) modal.remove(); }
  if (name === 'backdrop-close' && event.target === action) action.remove();
  if (name === 'export-campuses') exportCampuses();
  if (name === 'export-users') downloadCsv('veyon-campus-demo-users.csv', ['匿名账号 ID', '校区', '地区', '城市', '版本', '最近活动', '状态'], seedUsers.map(function (u) { return [u.id, u.campus, u.region, u.city, u.version, u.last, u.status]; }));
  if (name === 'export-dashboard') downloadCsv('veyon-campus-demo-summary.csv', ['指标', '演示值'], [['匿名使用账号', '2384'], ['已登记校区', state.campuses.length], ['活跃终端', '7812'], ['当前版本占比', '82.4%']]);
  if (name === 'notify') showToast('当前演示工作区没有待处理通知。');
  if (name === 'campus-detail') campusDetail(action.getAttribute('data-id'));
  if (name === 'user-detail') openModal('匿名记录说明', '这是用于展示字段结构的虚构样例。', '<div class="privacy-card"><h2>' + escapeHtml(action.getAttribute('data-id')) + '</h2><p>正式系统应使用不可直接识别个人的标识，并只保留支持产品统计所需的汇总字段。</p></div>', '<button class="btn secondary sm" type="button" data-action="close-modal">关闭</button>');
  if (name === 'sidebar-open') { const side = document.getElementById('sidebar'); const overlay = app.querySelector('.mobile-sidebar-overlay'); if (side) side.classList.add('open'); if (overlay) overlay.classList.add('open'); }
  if (name === 'sidebar-close') { const side = document.getElementById('sidebar'); const overlay = app.querySelector('.mobile-sidebar-overlay'); if (side) side.classList.remove('open'); if (overlay) overlay.classList.remove('open'); }
  if (name === 'reset-demo') { localStorage.removeItem('veyon-campus-website-demo-v1'); state.campuses = seedCampuses.slice(); state.prefs = { anonymous: true, optIn: true, preciseLocation: false, retention: '90' }; state.profile = { name: '项目工作区', owner: '演示管理员', region: '中国大陆与香港', timezone: 'Asia/Hong_Kong (UTC+8)' }; state.regionFilter = '全部地区'; render(); showToast('演示数据已重置。'); }
  if (name === 'save-settings') { const nameInput = document.getElementById('workspace-name'); const ownerInput = document.getElementById('workspace-owner'); const regionInput = document.getElementById('workspace-region'); const timezoneInput = document.getElementById('workspace-timezone'); if (nameInput && ownerInput && regionInput && timezoneInput) { state.profile = { name: nameInput.value.trim() || '项目工作区', owner: ownerInput.value.trim() || '演示管理员', region: regionInput.value, timezone: timezoneInput.value }; } persist(); render(); showToast('演示设置已保存在当前浏览器。'); }
  const setting = action.getAttribute('data-setting');
  if (setting) { state.prefs[setting] = !state.prefs[setting]; persist(); render(); showToast('已更新本地演示偏好。'); }
});

app.addEventListener('change', function (event) {
  const target = event.target;
  if (target.matches('[data-action="range"]')) { state.range = target.value; render(); }
  if (target.matches('[data-filter="campus-region"]')) { state.regionFilter = target.value; const input = app.querySelector('[data-search-table="campus"]'); refreshCampusTable(input ? input.value : ''); }
  if (target.matches('[data-filter="user-region"]')) { state.userRegion = target.value; const input = app.querySelector('[data-search-table="usage"]'); refreshUsageTable(input ? input.value : ''); }
  if (target.matches('[data-setting-select="retention"]')) { state.prefs.retention = target.value; persist(); showToast('已更新本地演示偏好。'); }
});

app.addEventListener('input', function (event) {
  if (event.target.matches('[data-search-table="campus"]')) refreshCampusTable(event.target.value);
  if (event.target.matches('[data-search-table="usage"]')) refreshUsageTable(event.target.value);
  if (event.target.id === 'global-search') {
    const query = event.target.value.trim().toLowerCase();
    const resultLinks = Array.from(app.querySelectorAll('#search-overlay .search-results a'));
    resultLinks.forEach(function (link) {
      const match = link.textContent.toLowerCase().indexOf(query) >= 0;
      link.style.display = match ? '' : 'none';
    });
    if (query) {
      const match = state.campuses.find(function (c) { return [c.name, c.region, c.city, c.id].join(' ').toLowerCase().indexOf(query) >= 0; });
      const results = app.querySelector('#search-overlay .search-results');
      let campusLink = results.querySelector('[data-campus-search]');
      if (match && !campusLink) { results.insertAdjacentHTML('beforeend', '<small data-campus-search>匹配的演示校区</small>'); campusLink = results.querySelector('[data-campus-search]'); }
      if (match) {
        let link = results.querySelector('[data-campus-hit]');
        if (!link) { results.insertAdjacentHTML('beforeend', '<a data-campus-hit href="/admin/campuses" data-route="/admin/campuses">' + icon('building') + '<span></span></a>'); link = results.querySelector('[data-campus-hit]'); }
        link.querySelector('span').textContent = match.name + ' · ' + match.city;
        link.style.display = '';
      } else { const link = results.querySelector('[data-campus-hit]'); if (link) link.style.display = 'none'; }
    }
  }
});

app.addEventListener('submit', function (event) {
  if (event.target.id === 'contact-form') {
    event.preventDefault();
    showToast('这是本地预览：反馈没有发送或保存。请使用 GitHub Issues 正式联系。');
  }
  if (event.target.id === 'add-campus-form') {
    event.preventDefault();
    const form = new FormData(event.target);
    const name = String(form.get('name') || '').trim();
    const region = String(form.get('region') || '').trim();
    const city = String(form.get('city') || '').trim();
    if (!name || !region || !city) { showToast('请填写校区名称、省 / 地区和城市。'); return; }
    const id = 'VC-DEMO-' + String(Date.now()).slice(-5);
    state.campuses.unshift({ id: id, name: name, region: region, city: city, devices: 0, users: 0, version: '—', updated: '刚刚', status: '未开始', active: false });
    persist();
    render();
    showToast('演示校区已添加到当前浏览器。');
  }
});

window.addEventListener('popstate', render);
document.addEventListener('keydown', function (event) {
  if (event.key === 'Escape') { closeSearch(); const modal = app.querySelector('.modal-backdrop'); if (modal) modal.remove(); }
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); openSearch(); }
});

render();

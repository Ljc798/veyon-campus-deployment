const app = document.getElementById('app');
const toastEl = document.getElementById('toast');
const cloudbaseConfigPresent = Boolean(import.meta.env.VITE_CLOUDBASE_ENV_ID && import.meta.env.VITE_CLOUDBASE_PUBLISHABLE_KEY);

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

const state = { currentPath: '/' };

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
    '<nav class="site-links" aria-label="主导航">' + siteNavItems.map(function (item) { return routeLink(item[0], item[1], activePath === item[0] ? 'active' : ''); }).join('') + '</nav>' +
    '<button class="mobile-menu" type="button" data-action="site-menu" aria-label="打开导航">' + icon('menu') + '</button></div></header>';
}

function publicFooter() {
  return '<footer class="site-footer"><div class="site-container"><div class="footer-grid">' +
    '<div class="footer-about">' + routeLink('/', '<img src="/assets/veyon-campus-mark.svg" alt="" /><span class="brand-copy"><strong>Veyon Campus</strong><span>CLASSROOM DEPLOYMENT</span></span>', 'brand-lockup') +
    '<p>为校园机房部署 Veyon 提供中文工具与操作参考，让批量准备过程更清晰、可检查。</p></div>' +
    '<div class="footer-col"><strong>产品</strong>' + routeLink('/product', '产品能力') + routeLink('/about', '关于项目') + '</div>' +
    '<div class="footer-col"><strong>帮助</strong>' + routeLink('/docs', '使用指南') + routeLink('/contact', '问题反馈') + '<a href="https://github.com/Ljc798/veyon-campus-deployment" target="_blank" rel="noopener">GitHub 仓库</a></div>' +
    '<div class="footer-col"><strong>说明</strong>' + routeLink('/privacy', '隐私说明') + '<a href="https://veyon.io/" target="_blank" rel="noopener">Veyon 官方网站</a>' + routeLink('/docs#status', '版本状态') + '</div>' +
    '</div><div class="footer-bottom"><span>© 2026 Veyon Campus · 校园机房部署工具</span><span class="demo-label">公开展示页不包含管理后台入口。</span></div></div></footer>';
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
  return publicPage('<section class="hero"><div class="site-container hero-grid"><div class="hero-copy"><span class="eyebrow"><i class="eyebrow-dot"></i>校园机房部署工具</span><h1>让每间教室的<br /><em>部署更有条理</em></h1><p>Veyon Campus 帮助学校管理员准备 Veyon 教师端与学生端环境，并提供清晰的中文操作说明。</p><div class="hero-actions">' + routeLink('/product', '了解产品能力 ' + icon('arrow'), 'btn') + '</div><div class="hero-meta"><span>' + icon('device') + ' Windows 桌面应用</span><span>' + icon('book') + ' 中文部署指引</span><span>' + icon('shield') + ' 隐私边界清晰</span></div></div>' + heroPreview() + '</div></section>' +
    '<section class="trust-strip"><div class="site-container trust-row"><p>围绕校园机房部署中的重复工作设计，工具状态与统计数据透明呈现。</p><span class="trust-tag">' + icon('building') + ' 校区管理视图</span><span class="trust-tag">' + icon('activity') + ' 使用趋势概览</span><span class="trust-tag">' + icon('lock') + ' 隐私边界清晰</span><span class="trust-tag">' + icon('book') + ' 中文操作指引</span></div></section>' +
    '<section class="section"><div class="site-container"><div class="section-head"><span class="eyebrow"><i class="eyebrow-dot"></i>一个工作流，覆盖多个校区</span><h2>从本地部署到整体了解</h2><p>桌面工具聚焦教师与学生电脑的安装准备；受保护的管理工作区读取真实校区资料与匿名按日汇总。</p></div><div class="feature-grid">' +
    featureCard('device', '教师端与学生端', '教师端准备校区资料与配置，学生端按步骤完成本机安装和设置；不同角色分别打包。', '/product', '查看工具') +
    featureCard('book', '逐步试装与验收', '先完成一台教师机和一台学生机的试装，再按步骤检查安装、服务和连接结果。', '/docs', '查看指南') +
    featureCard('shield', '透明的隐私边界', '说明网站管理数据、匿名心跳和桌面端分别处理哪些信息。', '/privacy', '查看说明') +
    '</div></div></section>' +
    '<section class="section tint"><div class="site-container workflow"><div class="workflow-copy"><span class="eyebrow"><i class="eyebrow-dot"></i>清晰的部署路径</span><h2>一套步骤，逐步铺开到更多校区</h2><p>从试装到批量使用，先明确每一步的输入、结果和验收方式。校区使用概况用于帮助维护者规划支持和更新。</p><div style="margin-top:22px">' + routeLink('/docs', '查看使用指南 ' + icon('arrow'), 'btn secondary') + '</div></div><div class="workflow-list">' +
    '<div class="workflow-step"><span class="step-number">01</span><div><h3>整理校区资料</h3><p>规划校区标识、机房名称与计算机编号，并确认目标 Windows 电脑环境。</p></div></div>' +
    '<div class="workflow-step"><span class="step-number">02</span><div><h3>教师端准备配置</h3><p>在教师电脑生成或选择校区配置，按文档核对密钥与目标机列表。</p></div></div>' +
    '<div class="workflow-step"><span class="step-number">03</span><div><h3>学生端逐批试装</h3><p>先在可恢复的测试机上操作，记录结果并完成教师端连接验收。</p></div></div>' +
    '<div class="workflow-step"><span class="step-number">04</span><div><h3>查看校区汇总</h3><p>授权管理员可维护校区资料并查看按 UTC 日汇总的匿名心跳数据。</p></div></div>' +
    '</div></div></section>' +
    '<section class="section"><div class="site-container"><div class="callout">' + icon('info') + '<div><strong>关于当前版本与云端工作区</strong><p>桌面 App 源码版本为 0.4.27，仍有 Windows 实机验收事项。CloudBase PostgreSQL 已提供真实校区与按日遥测汇总的数据层；管理工作区要求管理员登录，当前没有预置校区或虚构统计数字。</p></div></div></div></section>' +
    '<section class="section tint"><div class="site-container"><div class="cta-panel"><div><h2>从了解工具开始</h2><p>阅读操作文档，查看当前能力、版本状态与已知边界。</p></div>' + routeLink('/docs', '打开使用指南 ' + icon('arrow'), 'btn') + '</div></div></section>');
}

function productPage() {
  const cards = [
    ['terminal', '教师端配置准备', '教师控制台用于选择 Veyon 安装、校区配置和目标电脑等操作。密钥相关材料留在教师端的受控用户环境中。', '代码已实现 · 待 Windows 实机验收'],
    ['monitor', '学生端部署工具', '学生部署工具按选择的任务执行安装、校区配置和本机系统设置，运行前展示计划与检查结果。', '本地应用 · 需管理员权限'],
    ['users', '分离的应用角色', '教师控制台与学生部署工具是不同的构建角色，学生端使用的后台代理也单独运行。', '当前构建方案 · ZIP 待实机验收'],
    ['shield', '网站访问策略', '教师端可准备并推送签名的课堂网站策略，学生端代理处理规则与到期状态。', '功能代码已实现 · 浏览器效果待验收'],
    ['database', '校区与匿名汇总', '受保护的管理工作区通过 CloudBase Auth 登录，按 PostgreSQL 行级策略读写校区资料，并展示真实的每日匿名心跳汇总；目前尚未接入桌面端校区归属与版本数据。', 'CloudBase Auth + PostgreSQL 已接入'],
    ['book', '操作文档与验收', '仓库提供部署说明、常见问题、开发规划和 Windows 验收清单，方便按阶段核对实际结果。', '配套文档已提供']
  ];
  return publicPage('<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>产品能力</span><h1>教师端、学生端与校区视图</h1><p>围绕机房部署流程逐步建设。下方会区分已接入的数据功能和仍需真实设备或线上环境验收的部分。</p></div></section><section class="page-body"><div class="site-container"><div class="product-grid">' + cards.map(function (c, i) { return '<article class="product-card"><span class="feature-icon">' + icon(c[0]) + '</span><h3>' + c[1] + '</h3><p>' + c[2] + '</p><span class="status-label">' + c[3] + '</span></article>'; }).join('') + '</div></div></section>');
}

function aboutPage() {
  return publicPage('<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>关于项目</span><h1>把机房部署经验整理成工具</h1><p>Veyon Campus 是一个围绕校园机房管理需求持续迭代的独立项目，聚焦部署准备、角色分工、中文说明与实际验收。</p></div></section><section class="page-body"><div class="site-container"><div class="story-grid"><div class="story-copy"><span class="eyebrow"><i class="eyebrow-dot"></i>为什么开始</span><h2>减少重复准备，也把风险讲清楚</h2><p>在学校机房里，教师电脑、学生电脑和校区配置往往需要逐台或分批准备。Veyon Campus 尝试把常用步骤整合到 Windows 桌面工具中，配套说明每一步将要修改什么、需要什么权限，以及怎样验证结果。</p><p>项目仍在实验阶段。工具开发、自动化检查和真实 Windows 设备验收不是同一件事；我们会在页面和文档中明确各自的进度。</p></div><aside class="story-panel"><h3>项目实践原则</h3><div class="principle"><span class="feature-icon">' + icon('eye') + '</span><div><strong>行为可预期</strong><span>开始运行前说明计划与权限需求，明确哪些系统设置会被修改。</span></div></div><div class="principle"><span class="feature-icon">' + icon('check') + '</span><div><strong>先验证，再扩大</strong><span>建议先在可恢复的测试环境试装，逐项确认系统状态和连接结果。</span></div></div><div class="principle"><span class="feature-icon">' + icon('lock') + '</span><div><strong>资料边界清晰</strong><span>敏感密钥留在受控环境；未来统计采用告知、主动选择与数据最少化。</span></div></div></aside></div><div class="callout" style="margin-top:28px">' + icon('info') + '<div><strong>项目关系说明</strong><p>本项目围绕第三方 Veyon 软件提供部署辅助。Veyon 本身由其上游项目维护，商标、软件权利和许可按上游声明适用；本仓库也尚未指定开源许可证。</p></div></div></div></section>');
}

function docsPage() {
  return publicPage('<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>使用指南</span><h1>先试装，再逐批部署</h1><p>按真实部署流程了解工具、准备环境、试装一台教师机和一台学生机，并在批量使用前完成检查。</p></div></section><section class="page-body"><div class="site-container docs-layout"><aside class="docs-toc"><strong>本页目录</strong><a href="#start">开始前</a><a href="#flow">部署步骤</a><a href="#status">版本状态</a><a href="#resources">更多文档</a></aside><article class="docs-article"><section id="start"><h2>开始前：了解风险和适用范围</h2><p>请在 Windows 10 或 Windows 11 电脑上操作，并以管理员权限运行需要修改系统设置的部署步骤。当前 App 是 Windows 本地离线部署实验版，Windows 实机兼容性验收仍在进行。</p><div class="callout">' + icon('warning') + '<div><strong>学生端操作可能修改系统设置</strong><p>部署流程可能更改计算机名称、管理员账户密码、学生账户和 Veyon 配置。开始前记录原状态，并准备可恢复环境。</p></div></div></section><section id="flow"><h2>推荐部署步骤</h2><ol><li>明确校区、机房和学生机编号，备份重要配置。</li><li>先在一台教师机和一台学生机上完成试装。</li><li>逐项核对安装、账户、计算机名和 Veyon 服务状态。</li><li>使用教师端完成认证、电脑列表及连接检查。</li><li>试装通过后按批次部署，并记录每台电脑的结果和异常。</li></ol><h3>课堂网站访问策略</h3><p>教师端的网站策略功能通过签名策略与学生端后台代理实现。浏览器效果、权限边界、重启恢复和到期解除仍需要 Windows 实机验收，不应仅凭构建成功推断为已验证功能。</p></section><section id="status"><h2>当前版本状态</h2><p>App 源码版本为 0.4.27。教师控制台与学生部署工具已拆分构建；策略编译、部署后只读检查和清理入口已有代码。Windows 安装、角色 ZIP 内容、浏览器策略效果和系统恢复仍需按验收清单验证。</p><p>网站管理工作区现已接入 CloudBase Auth 与 PostgreSQL：管理员身份由账号会话确认，页面角色和数据库行级策略共同决定可读写范围。桌面端是否发送心跳仍由学生包中的遥测地址配置控制；已收到的心跳只按 UTC 日汇总，暂不关联校区或客户端版本。</p></section><section id="resources"><h2>更多项目文档</h2><div class="docs-resource-list"><a class="resource-link" href="https://github.com/Ljc798/veyon-campus-deployment/blob/develop/README.md" target="_blank" rel="noopener">' + icon('book') + '<span><strong>仓库 README</strong><small>公开版使用说明与原脚本操作步骤</small></span></a><a class="resource-link" href="https://github.com/Ljc798/veyon-campus-deployment/blob/develop/QnA.md" target="_blank" rel="noopener">' + icon('help') + '<span><strong>常见问题</strong><small>安装、账户和部署过程问题排查</small></span></a><a class="resource-link" href="https://github.com/Ljc798/veyon-campus-deployment/blob/develop/docs/README.md" target="_blank" rel="noopener">' + icon('file') + '<span><strong>App 文档入口</strong><small>当前 App 能力、状态和验收资料</small></span></a><a class="resource-link" href="https://github.com/Ljc798/veyon-campus-deployment/blob/develop/docs/Windows%E5%AD%A6%E7%94%9F%E7%AB%AF%E9%BB%91%E7%99%BD%E5%90%8D%E5%8D%95%E4%B8%8E%E5%90%8E%E5%8F%B0%E4%BB%A3%E7%90%86%E8%87%AA%E6%B5%8B%E9%AA%8C%E6%94%B6%E5%8D%95.md" target="_blank" rel="noopener">' + icon('check') + '<span><strong>学生端验收单</strong><small>按环境逐项验证代理与网站规则</small></span></a></div></section></article></div></section>');
}

function contactPage() {
  return publicPage('<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>联系与反馈</span><h1>一起把部署过程做得更清楚</h1><p>报告问题时，请写明使用的工具版本、Windows 环境、预期结果与实际表现。请勿提交密码、私钥或真实学生资料。</p></div></section><section class="page-body"><div class="site-container contact-grid"><div class="contact-card"><h2>发送反馈</h2><p>表单目前只用于本地页面预览，不会发送或保存到服务器。正式反馈请使用仓库 Issues。</p><form id="contact-form"><div class="form-grid"><div class="field"><label for="contact-topic">反馈类型</label><select id="contact-topic" required><option value="">请选择</option><option>使用问题</option><option>功能建议</option><option>文档勘误</option><option>其他</option></select></div><div class="field"><label for="contact-version">工具版本</label><input id="contact-version" placeholder="例如：App 0.4.27" /></div><div class="field full"><label for="contact-message">问题描述</label><textarea id="contact-message" required placeholder="写明操作步骤、预期结果和实际表现"></textarea><span class="field-hint">不要包含密码、私钥、个人信息或真实校区部署包。</span></div><div class="field full"><button class="btn" type="submit">预览提交反馈 ' + icon('arrow') + '</button></div></div></form></div><div class="contact-card"><h2>推荐反馈渠道</h2><p>从仓库提交可复现的问题或文档改进建议，便于维护者跟踪处理。</p><div class="contact-links"><a class="contact-link-card" href="https://github.com/Ljc798/veyon-campus-deployment/issues" target="_blank" rel="noopener"><span class="feature-icon">' + icon('message') + '</span><span><strong>GitHub Issues</strong><small>提交问题、建议或勘误</small></span>' + icon('external') + '</a><a class="contact-link-card" href="https://github.com/Ljc798/veyon-campus-deployment/blob/develop/CONTRIBUTING.md" target="_blank" rel="noopener"><span class="feature-icon">' + icon('book') + '</span><span><strong>参与改进说明</strong><small>查看提交前的注意事项</small></span>' + icon('external') + '</a><a class="contact-link-card" href="https://github.com/Ljc798/veyon-campus-deployment" target="_blank" rel="noopener"><span class="feature-icon">' + icon('file') + '</span><span><strong>项目仓库</strong><small>查看源码、文档和发布记录</small></span>' + icon('external') + '</a></div><div class="privacy-note" style="margin-top:18px">' + icon('shield') + '<span>请不要上传真实部署包、管理员密码、私钥、学生姓名、设备 IP 或可识别个人的信息。</span></div></div></div></section>');
}

function privacyPage() {
  return publicPage('<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>隐私说明</span><h1>统计应该透明，也应该可选择</h1><p>这里说明网站账号、校区资料与匿名心跳目前的实际数据边界。</p></div></section><section class="page-body"><div class="site-container"><div class="privacy-card"><h2>管理工作区数据</h2><p>管理员登录由 CloudBase Auth 处理。管理工作区从 PostgreSQL 读取校区资料和每日匿名汇总；角色由服务端维护，数据库行级安全策略限制可读写范围。公开展示页面不加载管理数据，也不显示管理入口。</p><p>校区名称、地区、城市与状态由授权管理员维护。当前尚未建立校区管理员隔离，每个获授权站点角色均可查看全部校区资料。</p></div><div class="privacy-card"><h2>匿名心跳数据</h2><p>启用遥测后，Agent 发送随机生成的安装标识。服务端按 UTC 日期生成 HMAC 摘要后写入 CloudBase PostgreSQL；原始安装标识不会写入数据库或应用日志。按日摘要保留 90 天，每日总数和心跳次数保留 400 天。摘要每天轮换，因此不能跨日识别同一安装。</p><ul><li>目前字段不含姓名、账号、主机名、IP、浏览历史、软件使用记录、客户端版本或校区编号。</li><li>学生包默认遥测地址为空；只有管理员显式配置地址后才会发送。</li><li>桌面 App 需要另行明确告知和确认启用条件；正式使用前需落实校区政策、退出和删除流程。</li></ul></div><div class="privacy-card"><h2>当前未上线项目</h2><p>CloudBase PostgreSQL 结构和应用代码已就绪，CloudRun、静态站点公开发布、kidscode.fun 绑定与证书部署尚未完成。服务端密钥不会下发到浏览器。上线前还需检查备份恢复、操作审计、滥用防护和适用的隐私要求。</p></div></div></section>');
}

function notFoundPage() {
  return publicPage('<section class="page-hero" style="padding:100px 0"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>页面不存在</span><h1>找不到这个页面</h1><p>地址可能已经变更，或者页面还没有加入网站。</p><div style="margin-top:25px">' + routeLink('/', '返回首页 ' + icon('arrow'), 'btn') + '</div></div></section>');
}

function render() {
  const path = window.location.pathname.replace(/\/$/, '') || '/';
  state.currentPath = path;
  const isAdminPath = path === '/admin' || path.indexOf('/admin/') === 0;
  if (isAdminPath) {
    if (cloudbaseConfigPresent || import.meta.env.DEV) {
      app.innerHTML = '<div class="site-shell">' + publicHeader() + '<main class="site-main"><section class="page-hero"><div class="site-container"><span class="eyebrow">CloudBase</span><h1>正在载入管理员登录…</h1></div></section></main>' + publicFooter() + '</div>';
      import('./src/cloud-admin.js').then(function (module) {
        if (state.currentPath === path) module.mountCloudAdmin(app, path, publicHeader, publicFooter);
      }).catch(function () {
        if (state.currentPath === path) app.innerHTML = notFoundPage();
      });
    } else app.innerHTML = notFoundPage();
    document.title = '管理员登录 · Veyon Campus';
    return;
  }
  if (path === '/') app.innerHTML = homePage();
  else if (path === '/product') app.innerHTML = productPage();
  else if (path === '/about') app.innerHTML = aboutPage();
  else if (path === '/docs') app.innerHTML = docsPage();
  else if (path === '/contact') app.innerHTML = contactPage();
  else if (path === '/privacy') app.innerHTML = privacyPage();
  else app.innerHTML = notFoundPage();
  document.title = (path === '/' ? '校园机房部署工具' : (siteNavItems.find(function (x) { return x[0] === path; }) || ['', 'Veyon Campus'])[1]) + ' · Veyon Campus';
}

let toastTimer;
function showToast(message) {
  if (!toastEl) return;
  toastEl.className = 'toast show';
  toastEl.innerHTML = icon('check') + '<span>' + escapeHtml(message) + '</span>';
  clearTimeout(toastTimer);
  toastTimer = setTimeout(function () { toastEl.className = 'toast'; }, 2800);
}

app.addEventListener('click', function (event) {
  const route = event.target.closest('[data-route]');
  if (route) {
    event.preventDefault();
    navigate(route.getAttribute('data-route'));
    return;
  }
  const action = event.target.closest('[data-action="site-menu"]');
  if (action) {
    const menu = app.querySelector('.site-links');
    if (menu) menu.classList.toggle('mobile-open');
  }
});

window.addEventListener('popstate', render);
app.addEventListener('submit', function (event) {
  if (event.target.id !== 'contact-form') return;
  event.preventDefault();
  showToast('这是页面预览：反馈没有发送或保存。请使用 GitHub Issues 正式联系。');
});

render();

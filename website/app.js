import { loadPublicDownloadPageConfig } from './src/public-download-data.js';

const app = document.getElementById('app');
const toastEl = document.getElementById('toast');
const staticOnly = import.meta.env.MODE === 'static-only';
const cloudbaseConfigPresent = Boolean(import.meta.env.VITE_CLOUDBASE_ENV_ID && import.meta.env.VITE_CLOUDBASE_PUBLISHABLE_KEY);
// Keep the built-in floor aligned with the latest stable release. Admin settings
// remain source-specific, but an older saved value must not roll the page back.
const DOWNLOAD_FALLBACK_RELEASES = Object.freeze({
  github: { version: '1.1.78', tag: 'v1.1.78', channel: '稳定版' },
  gitcode: { version: '1.1.78', tag: 'v1.1.78', channel: '稳定版' }
});

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

const state = { currentPath: '/', renderSequence: 0 };
let docsSearchModule = null;

function escapeHtml(value) {
  return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) {
    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
  });
}

function routeHref(path) {
  const host = window.location.hostname.toLowerCase();
  const isKidsCodeHost = host === 'kidscode.fun' || host === 'www.kidscode.fun';
  return path === '/' && isKidsCodeHost ? '/?site=veyon' : path;
}

function navigate(path) {
  if (!path || path === state.currentPath) return;
  const destination = routeHref(path);
  history.pushState({}, '', destination);
  render();
  const hashAt = path.indexOf('#');
  if (hashAt >= 0) {
    const anchor = decodeURIComponent(path.slice(hashAt + 1));
    setTimeout(function () { const target = document.getElementById(anchor); if (target) target.scrollIntoView({ behavior: 'smooth' }); }, 0);
  } else window.scrollTo(0, 0);
}

function routeLink(path, label, className) {
  return '<a href="' + routeHref(path) + '" data-route="' + path + '" class="' + (className || '') + '">' + label + '</a>';
}

const siteNavItems = [
  ['/product', '工具介绍'],
  ['/docs', '帮助与文档'],
  ['/about', '关于项目'],
  ['/contact', '问题反馈'],
  ['/download', '下载软件', 'btn nav-download']
];

function publicHeader() {
  const activePath = state.currentPath;
  const links = siteNavItems.map(function (item) {
    const className = item[2] || (activePath === item[0] ? 'active' : '');
    return routeLink(item[0], item[1], className);
  }).join('');
  return '<a class="skip-link" href="#main-content">跳到主要内容</a>' +
    '<header class="site-nav"><div class="site-nav-inner">' +
    routeLink('/', '<img src="/assets/veyon-campus-mark.svg" alt="" /><span class="brand-copy"><strong>Veyon Campus</strong><span>校园机房部署助手</span></span>', 'brand-lockup') +
    '<nav class="site-links" id="primary-navigation" aria-label="主导航">' + links + '</nav>' +
    '<button class="mobile-menu" type="button" data-action="site-menu" aria-controls="primary-navigation" aria-expanded="false" aria-label="打开导航">' + icon('menu') + '</button></div></header>';
}

function publicFooter() {
  return '<footer class="site-footer"><div class="site-container"><div class="footer-grid">' +
    '<div class="footer-about">' + routeLink('/', '<img src="/assets/veyon-campus-mark.svg" alt="" /><span class="brand-copy"><strong>Veyon Campus</strong><span>校园机房部署助手</span></span>', 'brand-lockup') +
    '<p>提供教师端、学生端 Windows 工具和中文部署指南，帮助学校机房准备并检查 Veyon。</p></div>' +
    '<div class="footer-col"><strong>开始使用</strong>' + routeLink('/download', '下载软件') + routeLink('/docs', '帮助与文档') + routeLink('/product', '工具介绍') + '</div>' +
    '<div class="footer-col"><strong>帮助</strong>' + routeLink('/contact', '问题反馈') +
    '<a href="https://github.com/Ljc798/veyon-campus-deployment/issues" target="_blank" rel="noopener noreferrer">提交问题</a></div>' +
    '<div class="footer-col"><strong>相关项目</strong><a href="https://veyon.io/" target="_blank" rel="noopener noreferrer">Veyon 官方网站</a>' +
    '<a href="https://gitcode.com/ljc_1126/veyon-campus-deployment/releases" target="_blank" rel="noopener noreferrer">版本发布页</a></div>' +
    '</div><div class="footer-bottom"><span>© 2026 Veyon Campus · 校园机房部署助手</span></div></div></footer>';
}

function publicPage(content) {
  return '<div class="site-shell">' + publicHeader() +
    '<main id="main-content" class="site-main" tabindex="-1">' + content + '</main>' +
    publicFooter() + '</div>';
}

function deploymentVisual() {
  return '<aside class="hero-art" aria-label="Veyon 校园部署流程示意">' +
    '<div class="deployment-visual">' +
    '<div class="deployment-visual-head"><span class="eyebrow">部署路线</span><span class="deployment-pill">先从一台测试机开始</span></div>' +
    '<ol class="deployment-steps">' +
    '<li class="deployment-step"><span class="deployment-index">01</span><span class="deployment-step-copy"><small>教师电脑</small><strong>准备校区配置</strong><span>安装教师端，核对部署资料</span></span><span class="deployment-step-icon">' + icon('terminal') + '</span></li>' +
    '<li class="deployment-step"><span class="deployment-index">02</span><span class="deployment-step-copy"><small>学生电脑</small><strong>按确认的步骤部署</strong><span>导入教师提供的配置并检查计划</span></span><span class="deployment-step-icon">' + icon('monitor') + '</span></li>' +
    '<li class="deployment-step"><span class="deployment-index">03</span><span class="deployment-step-copy"><small>连接检查</small><strong>确认 Veyon 正常连接</strong><span>试装通过后，再逐批扩展</span></span><span class="deployment-step-icon">' + icon('check') + '</span></li>' +
    '</ol>' +
    '<div class="deployment-visual-foot"><span class="deployment-foot-icon">' + icon('shield') + '</span><span><strong>每一步都能核对</strong><small>先试装，再按结果决定下一步</small></span></div>' +
    '</div></aside>';
}

function featureCard(iconName, title, copy, link, label) {
  return '<article class="feature-card"><span class="feature-icon">' + icon(iconName) + '</span><h3>' + title + '</h3><p>' + copy + '</p>' +
    (link ? routeLink(link, label + ' ' + icon('arrow'), '') : '') + '</article>';
}

function capabilitySection() {
  return '<section class="section tint" id="capabilities"><div class="site-container">' +
    '<div class="section-head section-head-left"><span class="eyebrow"><i class="eyebrow-dot"></i>课堂管控能力</span>' +
    '<h2>名单、下载与浏览器限制</h2>' +
    '<p>在教师端选好名单与模式，学生端按已确认的设置执行；效果以逐台回执为准，未生效时重启学生浏览器并重新下发名单。</p></div>' +
    '<div class="feature-grid">' +
    featureCard('globe', '网站黑白名单', '按名单阻止或只允许指定网站，支持阻止名单、白名单与组合模式。', '', '') +
    featureCard('download', '阻止 Edge 下载', '应用网站策略时同时禁止 Edge 下载文件，并关闭 Edge 冲浪游戏与 Chrome 小恐龙。', '', '') +
    featureCard('lock', '软件与系统限制', '按学生账户限制 EXE 运行并配置系统限制；解除时只撤销本工具写入的规则。', '', '') +
    '</div></div></section>';
}

function homePage() {
  return publicPage(
    '<section class="hero"><div class="site-container hero-grid">' +
    '<div class="hero-copy"><span class="eyebrow"><i class="eyebrow-dot"></i>给负责学校机房部署的老师和技术人员</span>' +
    '<h1>让教室电脑<br /><em>准备好 Veyon</em></h1>' +
    '<p>Veyon Campus 是一套学校机房部署工具。本网站提供教师端、学生端 Windows 下载和中文指南，帮助你准备配置、部署学生电脑并检查 Veyon 连接。</p>' +
    '<div class="hero-actions">' + routeLink('/download', icon('download') + '下载软件', 'btn') +
    routeLink('/docs', icon('book') + '查看部署指南', 'btn secondary') + '</div>' +
    '<div class="hero-meta"><span>' + icon('users') + '教师端与学生端分开安装</span><span>' + icon('device') + 'Windows 10/11 x64</span></div></div>' +
    deploymentVisual() + '</div></section>' +
    '<section class="trust-strip" aria-label="工具适用范围"><div class="site-container trust-row">' +
    '<div class="trust-item"><span class="trust-item-icon">' + icon('download') + '</span><span><strong>角色清楚</strong><small>教师电脑与学生电脑使用不同安装包</small></span></div>' +
    '<div class="trust-item"><span class="trust-item-icon">' + icon('book') + '</span><span><strong>中文步骤</strong><small>从准备到连接检查逐步说明</small></span></div>' +
    '<div class="trust-item"><span class="trust-item-icon">' + icon('shield') + '</span><span><strong>先试装</strong><small>确认一台教师机和一台学生机</small></span></div>' +
    '</div></section>' +
    '<section class="section"><div class="site-container">' +
    '<div class="section-head section-head-left"><span class="eyebrow"><i class="eyebrow-dot"></i>这个网站能帮你做什么</span>' +
    '<h2>从选安装包到完成检查</h2><p>下载、操作步骤和问题反馈都放在同一个入口，第一次使用也能知道下一步去哪里。</p></div>' +
    '<div class="feature-grid">' +
    featureCard('download', '选择正确的安装包', '教师电脑和学生电脑需要不同版本；下载页提供 GitHub 与 GitCode 两个渠道。', '/download', '查看下载') +
    featureCard('book', '按步骤准备和部署', '了解教师端、学生端分别要做什么，以及哪些设置需要先确认。', '/docs', '打开指南') +
    featureCard('check', '试装后再扩大使用', '先检查安装结果和 Veyon 连接，再按批次部署到其他电脑。', '/docs#verify', '查看检查步骤') +
    '</div><div class="docs-actions">' +
    routeLink('/product#capabilities', '查看课堂管控能力 ' + icon('arrow'), 'text-link') +
    '</div></div></section>' +
    '<section class="section tint"><div class="site-container">' +
    '<div class="callout guide-alert">' + icon('warning') + '<div><strong>请先在可恢复的测试电脑上试装</strong>' +
    '<p>学生端部署可能更改电脑名称、账户或 Veyon 设置，具体取决于你在工具中选择并确认的操作。开始前记录原状态，并确认有恢复办法。</p></div></div>' +
    '<div class="cta-panel site-cta"><div><h2>准备开始部署？</h2><p>先选教师端或学生端安装包，再按指南完成一台教师电脑和一台学生电脑的试装。</p></div>' +
    routeLink('/download', '下载软件 ' + icon('arrow'), 'btn') + '</div></div></section>'
  );
}

function fallbackDownloadSource(source) {
  const github = source === 'github';
  const release = DOWNLOAD_FALLBACK_RELEASES[source];
  const version = release.version;
  const tag = release.tag;
  const root = github
    ? 'https://github.com/Ljc798/veyon-campus-deployment'
    : 'https://gitcode.com/ljc_1126/veyon-campus-deployment';
  return {
    source,
    version,
    releaseUrl: source === 'github' ? `${root}/releases/tag/${tag}` : `${root}/releases`,
    channel: release.channel,
    studentDownloadUrl: `${root}/releases/download/${tag}/VeyonCampus-Student-Setup-${version}-win-x64.exe`,
    teacherDownloadUrl: `${root}/releases/download/${tag}/VeyonCampus-Teacher-Setup-${version}-win-x64.exe`
  };
}

function downloadRoleLink(label, href, format) {
  return '<a class="btn download-role" href="' + escapeHtml(href) + '">' +
    '<span class="download-role-copy"><strong>' + escapeHtml(label) + '</strong><small>Windows 10/11 x64 · ' + escapeHtml(format) + '</small></span>' + icon('download') + '</a>';
}

function downloadMethod(id, methodLabel, provider, release, format, instructions) {
  const updatedAt = release.updatedAt ? new Date(release.updatedAt) : null;
  const updatedLabel = updatedAt && !Number.isNaN(updatedAt.getTime())
    ? '<small class="status-as-of">最近同步：' + escapeHtml(updatedAt.toLocaleString('zh-HK', { timeZone: 'Asia/Hong_Kong', hour12: false })) + '</small>'
    : '';
  return '<section class="download-method" id="' + id + '" aria-labelledby="' + id + '-title">' +
    '<div class="download-method-heading"><div><span class="eyebrow">' + methodLabel + '</span><h2 id="' + id + '-title">' + provider + '</h2>' +
    '<span class="download-version-label">v' + escapeHtml(release.version) + '</span>' + updatedLabel + '</div>' +
    '<a class="download-release-link" href="' + escapeHtml(release.releaseUrl) + '" target="_blank" rel="noopener noreferrer">查看发行页 ' + icon('external') + '</a></div>' +
    '<p class="download-method-copy">' + instructions + '</p>' +
    '<div class="download-role-actions">' + downloadRoleLink('下载学生端', release.studentDownloadUrl, format) + downloadRoleLink('下载教师端', release.teacherDownloadUrl, format) + '</div></section>';
}

function compareReleaseVersions(left, right) {
  const leftParts = left.split('.').map(Number);
  const rightParts = right.split('.').map(Number);
  for (let index = 0; index < Math.max(leftParts.length, rightParts.length); index++) {
    const difference = (leftParts[index] || 0) - (rightParts[index] || 0);
    if (difference !== 0) return difference;
  }
  return 0;
}

function releaseForSource(settings, source) {
  const fallback = fallbackDownloadSource(source);
  const configured = settings?.configured === true
    ? settings.sources.find(item => item.source === source)
    : null;
  return configured && compareReleaseVersions(configured.version, fallback.version) >= 0
    ? configured
    : fallback;
}

function downloadPage(settings = null, statusMessage = '') {
  const github = releaseForSource(settings, 'github');
  const gitcode = releaseForSource(settings, 'gitcode');
  const betaNotice = github.channel === 'Beta 试用版'
    ? '<div class="callout download-notice">' + icon('info') + '<div><strong>GitHub 提供 1.0.15 Beta 实机试用包</strong><p>该版本用于测试；学生端 Edge 实时策略扩展需要按安装目录中的 EDGE-REALTIME-MANUAL-TEST.md 手动加载。GitCode 当前提供稳定版 v1.0.13。</p></div></div>'
    : '';
  const statusNotice = statusMessage
    ? '<div class="callout download-config-status">' + icon('info') + '<div><strong>下载页信息</strong><p>' + escapeHtml(statusMessage) + '</p></div></div>'
    : '';
  return publicPage(
    '<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>Windows 10/11 x64</span>' +
    '<h1>下载软件</h1><p>请按电脑用途选择教师端或学生端 EXE 安装程序；每个下载来源显示各自的最新版本。</p></div></section>' +
    '<section class="page-body"><div class="site-container download-content">' +
    statusNotice +
    '<div class="callout download-notice">' + icon('info') + '<div><strong>直接下载 EXE 安装程序</strong><p>按电脑用途下载教师端或学生端安装程序，下载完成后直接运行。</p></div></div>' +
    '<div class="callout download-warning">' + icon('warning') + '<div><strong>安装安全提醒</strong><p>安装程序没有 Windows 发布者数字签名，Windows 首次运行可能显示安全提示。请确认从本下载页获取文件，并核对角色和版本后运行。</p></div></div>' +
    betaNotice +
    '<div class="download-methods">' +
    downloadMethod('github-downloads', github.channel || '方式一', 'GitHub', github, 'EXE', '选择电脑角色，直接下载并运行对应的 EXE 安装程序。') +
    downloadMethod('gitcode-downloads', gitcode.channel || '方式二', 'GitCode', gitcode, 'EXE', '选择电脑角色，直接下载并运行对应的 EXE 安装程序。') +
    '</div></div></section>'
  );
}

function productPage() {
  return publicPage(
    '<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>工具介绍</span>' +
    '<h1>教师端准备，学生端部署</h1><p>教师电脑安装教师端，学生电脑安装学生端。按指南准备机房配置并完成部署后，在 Veyon 中检查两端连接。</p></div></section>' +
    '<section class="page-body"><div class="site-container">' +
    '<div class="product-grid product-roles">' +
    '<article class="product-card product-role-card"><span class="feature-icon">' + icon('terminal') + '</span><span class="role-kicker">教师电脑</span>' +
    '<h3>教师端工具</h3><p>在教师电脑填写机房信息，生成学生电脑要导入的配置。</p>' +
    '<ul class="product-points"><li>填写学校或机房名称</li><li>选择要提供给学生电脑的设置</li><li>生成配置，并发布到云端或交给学生端</li></ul>' +
    routeLink('/download#github-downloads', '选择教师端安装包 ' + icon('arrow'), 'text-link') + '</article>' +
    '<article class="product-card product-role-card"><span class="feature-icon">' + icon('monitor') + '</span><span class="role-kicker">学生电脑</span>' +
    '<h3>学生端工具</h3><p>在学生电脑导入教师端提供的配置，先看清楚将要安装或更改的内容。</p>' +
    '<ul class="product-points"><li>从云端获取配置，或导入本地文件</li><li>逐项确认要执行的设置</li><li>查看结果，再配合教师端检查连接</li></ul>' +
    routeLink('/download#github-downloads', '选择学生端安装包 ' + icon('arrow'), 'text-link') + '</article>' +
    '</div></div></section>' +
    capabilitySection() +
    '<section class="section tint"><div class="site-container"><div class="workflow">' +
    '<div class="workflow-copy"><span class="eyebrow"><i class="eyebrow-dot"></i>推荐顺序</span><h2>先在两台电脑上走完一遍</h2>' +
    '<p>确认教师端和学生端都能按预期工作后，再安排小批量部署。学校的账户规则、网络和设备管理策略可能影响实际结果。</p>' +
    routeLink('/docs', '阅读部署指南 ' + icon('arrow'), 'btn secondary') + '</div>' +
    '<div class="workflow-list">' +
    '<div class="workflow-step"><span class="step-number">01</span><div><h3>准备教师电脑</h3><p>安装教师端，整理校区配置和部署资料。</p></div></div>' +
    '<div class="workflow-step"><span class="step-number">02</span><div><h3>试装一台学生电脑</h3><p>导入匹配的配置，查看计划并确认所选操作。</p></div></div>' +
    '<div class="workflow-step"><span class="step-number">03</span><div><h3>由教师端检查连接</h3><p>确认实际结果后，再分批部署到更多电脑。</p></div></div>' +
    '</div></div></div></section>' +
    '<section class="section"><div class="site-container"><div class="callout guide-alert">' + icon('warning') +
    '<div><strong>部署前请确认授权和恢复方式</strong><p>仅在获授权的学校设备上使用。学生端的部分操作需要管理员权限，也可能更改账户或系统设置。</p></div></div></div></section>'
  );
}

function aboutPage() {
  return publicPage(
    '<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>关于 Veyon Campus</span>' +
    '<h1>为校园机房准备 Veyon</h1><p>这里提供教师端、学生端 Windows 工具、中文部署步骤和下载入口。</p></div></section>' +
    '<section class="page-body"><div class="site-container">' +
    '<div class="story-grid"><div class="story-copy"><span class="eyebrow"><i class="eyebrow-dot"></i>工具用途</span>' +
    '<h2>把重复的准备工作变成清楚的步骤</h2>' +
    '<p>学校机房通常有教师电脑和学生电脑。教师端用于准备机房配置；学生端按确认的内容安装或调整目标电脑。两端完成后，可在 Veyon 中检查教师电脑能否连接到学生电脑。</p>' +
    '<p>本网站集中提供安装包、操作说明和反馈入口。实际的课堂查看与控制由已安装并配置好的 Veyon 软件完成。</p></div>' +
    '<aside class="story-panel"><h3>你可以从这里开始</h3>' +
    '<div class="principle"><span class="feature-icon">' + icon('download') + '</span><div><strong>下载对应角色</strong><span>教师电脑和学生电脑使用不同的安装包。</span></div></div>' +
    '<div class="principle"><span class="feature-icon">' + icon('book') + '</span><div><strong>按指南准备</strong><span>先试装一台教师电脑和一台学生电脑。</span></div></div>' +
    '<div class="principle"><span class="feature-icon">' + icon('check') + '</span><div><strong>核对实际结果</strong><span>确认 Veyon 连接正常后，再逐步扩大使用范围。</span></div></div>' +
    '</aside></div>' +
    '<div class="callout guide-alert about-note">' + icon('info') + '<div><strong>面向获授权的学校设备</strong>' +
    '<p>开始前请确认学校允许的账户、网络和设备管理方式。安装安全提醒与当前下载渠道见下载页面。</p>' +
    routeLink('/download', '查看下载与安装提醒 ' + icon('arrow'), 'text-link') + '</div></div></div></section>'
  );
}

function docsPage() {
  return publicPage(
    '<section class="docs-home-hero"><div class="site-container docs-home-heading">' +
    '<h1>有什么问题？<br><em>直接搜一下</em></h1>' +
    '<p>按老师的实际问题查找部署步骤、课堂操作和故障处理。搜索只返回已有指南，不会编造答案。</p>' +
    '<form class="docs-search-form" id="docs-search-form" role="search">' +
    '<label class="sr-only" for="docs-search-input">搜索帮助文档</label>' +
    '<span class="docs-search-icon">' + icon('search') + '</span>' +
    '<input id="docs-search-input" name="q" type="search" maxlength="160" autocomplete="off" placeholder="例如：学生电脑显示离线怎么办？" aria-controls="docs-search-results" aria-describedby="docs-search-hint" />' +
    '<button class="btn" type="submit">搜索</button></form>' +
    '<p class="docs-search-hint" id="docs-search-hint">支持中文问题、错误码、版本号和常见组件名；最多 160 个字符。按 Enter 搜索，按 Esc 清空。查询在浏览器中对照本站文档索引，不提交给搜索服务；查询词保存在地址片段，复制链接时会一并带上。</p></div></section>' +
    '<section class="page-body docs-home-body"><div class="site-container">' +
    '<section class="docs-popular" aria-labelledby="docs-popular-title"><div class="docs-section-heading"><h2 id="docs-popular-title">大家经常查这些</h2></div>' +
    '<div class="docs-popular-links">' +
    '<button type="button" data-doc-query="学生端怎么安装">学生端怎么安装</button>' +
    '<button type="button" data-doc-query="电脑不在线怎么办">电脑不在线</button>' +
    '<button type="button" data-doc-query="怎么开启网站限制">怎么开启网站限制</button>' +
    '<button type="button" data-doc-query="怎么结束课堂">怎么结束课堂</button>' +
    '<button type="button" data-doc-query="怎么更新学生电脑">怎么更新学生电脑</button>' +
    '<button type="button" data-doc-query="怎么打印座位图">怎么打印座位图</button>' +
    '</div></section>' +
    '<section class="docs-search-area" id="docs-search-results" aria-labelledby="docs-results-title">' +
    '<div class="docs-search-toolbar"><div><h2 id="docs-results-title">帮助文档</h2></div>' +
    '<label class="docs-category-filter">分类筛选<select id="docs-category-filter" aria-label="按分类筛选帮助文档"><option value="">全部分类</option></select></label></div>' +
    '<p class="docs-search-status" id="docs-search-status" role="status" aria-live="polite">正在读取文档目录…</p>' +
    '<p class="docs-search-failure-help" id="docs-search-failure-help" hidden>如果搜索或文档目录持续不可用，可' +
    routeLink('/contact', '反馈这个问题', 'text-link') + '。</p>' +
    '<nav class="docs-static-fallback" id="docs-static-fallback" aria-label="常用帮助文档" hidden>' +
    '<a href="/docs/articles/student-setup.html">学生端安装指南</a>' +
    '<a href="/docs/articles/student-offline.html">学生电脑离线排查</a>' +
    '<a href="/docs/articles/apply-website-policy.html">推送网站名单</a>' +
    '<a href="/docs/articles/end-class.html">结束课堂与解除限制</a>' +
    '<a href="/docs/articles/student-update.html">更新学生电脑</a>' +
    '<a href="/docs/articles/print-seat-chart.html">打印座位图</a>' +
    '</nav>' +
    '<div class="docs-category-grid" id="docs-category-grid" role="group" aria-label="文档分类"></div>' +
    '<div class="docs-featured-grid" id="docs-featured-grid"></div>' +
    '<div class="docs-result-list" id="docs-result-list" aria-busy="false"></div>' +
    '<div class="docs-empty" id="docs-empty" hidden><strong>没有找到合适的文章</strong><p>可以换一种说法试试，或在全部分类中试试：</p>' +
    '<div class="docs-empty-suggestions" role="group" aria-label="替代搜索词">' +
    '<button type="button" data-doc-query="学生电脑离线">学生电脑离线</button>' +
    '<button type="button" data-doc-query="学生电脑更新">学生电脑更新</button>' +
    '<button type="button" data-doc-query="座位图">座位图</button></div>' +
    routeLink('/contact', '去问题反馈页 ' + icon('arrow'), 'text-link') + '</div>' +
    '</section></div></section>'
  );
}

function contactPage() {
  return publicPage(
    '<section class="page-hero"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>问题反馈</span>' +
    '<h1>遇到部署问题？</h1><p>说明问题发生在哪一步、屏幕上显示了什么，我们就能更快帮你定位。</p></div></section>' +
    '<section class="page-body"><div class="site-container contact-grid">' +
    '<div class="contact-card"><span class="feature-icon">' + icon('message') + '</span><h2>提交问题</h2>' +
    '<p>点击下方按钮打开 Veyon Campus 的 GitHub 问题页。请描述你使用的工具角色、Windows 版本和出现问题的步骤。</p>' +
    '<a class="btn" href="https://github.com/Ljc798/veyon-campus-deployment/issues/new" target="_blank" rel="noopener noreferrer">前往 GitHub 提交问题 ' + icon('external') + '</a></div>' +
    '<aside class="contact-card contact-guidance"><h2>提交前准备</h2><ul class="product-points">' +
    '<li>教师端还是学生端，以及安装包版本</li><li>Windows 版本和出现问题前做了什么</li>' +
    '<li>页面或工具显示的完整错误提示</li><li>已经尝试过哪些恢复或检查步骤</li></ul>' +
    '<div class="privacy-note">' + icon('shield') + '<span>请勿上传密码、私钥、电话号码、真实学生资料、校区配置包或未脱敏的电脑 IP。</span></div></aside>' +
    '</div></section>'
  );
}

function notFoundPage() {
  return publicPage('<section class="page-hero" style="padding:100px 0"><div class="site-container"><span class="eyebrow"><i class="eyebrow-dot"></i>页面不存在</span><h1>找不到这个页面</h1><p>地址可能已经变更，或者页面还没有加入网站。</p><div style="margin-top:25px">' + routeLink('/', '返回首页 ' + icon('arrow'), 'btn') + '</div></div></section>');
}

function render() {
  const path = window.location.pathname.replace(/\/$/, '') || '/';
  if (path === '/docs') {
    const legacyDocs = {
      before: 'prepare-before-install', download: 'download-tools', teacher: 'teacher-first-setup',
      student: 'student-setup', verify: 'deployment-check', help: 'report-issue'
    };
    const legacyAnchor = window.location.hash.slice(1).toLowerCase();
    if (legacyDocs[legacyAnchor]) {
      window.location.replace('/docs/articles/' + legacyDocs[legacyAnchor] + '.html');
      return;
    }
  }
  if (path !== '/docs') docsSearchModule?.unmountDocsSearch();
  const renderSequence = ++state.renderSequence;
  state.currentPath = path;
  const isAdminPath = path === '/admin' || path.indexOf('/admin/') === 0;
  if (isAdminPath) {
    if (!staticOnly && (cloudbaseConfigPresent || import.meta.env.DEV)) {
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
  else if (path === '/download') {
    app.innerHTML = downloadPage(null, '正在读取下载页设置，稍后会显示已同步版本。');
    loadPublicDownloadPageConfig().then(function (settings) {
      if (state.renderSequence !== renderSequence || state.currentPath !== path) return;
      app.innerHTML = downloadPage(settings, settings.configured ? '' : '管理员尚未同步最新版本，当前显示备用版本。');
    }).catch(function () {
      if (state.renderSequence !== renderSequence || state.currentPath !== path) return;
      app.innerHTML = downloadPage(null, '暂时无法读取最新版本，当前显示备用版本；请稍后刷新。');
    });
  }
  else if (path === '/about') app.innerHTML = aboutPage();
  else if (path === '/docs') {
    app.innerHTML = docsPage();
    import('./src/docs-search.js').then(function (module) {
      docsSearchModule = module;
      if (state.currentPath === path) module.mountDocsSearch(app);
    }).catch(function () {
      const status = document.getElementById('docs-search-status');
      if (status) status.textContent = '帮助文档组件未能加载；请刷新页面重试，仍无法使用时请前往问题反馈。';
      const failureHelp = document.getElementById('docs-search-failure-help');
      if (failureHelp) failureHelp.hidden = false;
      const staticFallback = document.getElementById('docs-static-fallback');
      if (staticFallback) staticFallback.hidden = false;
    });
  }
  else if (path === '/contact') app.innerHTML = contactPage();
  else app.innerHTML = notFoundPage();
  const pageTitle = path === '/' ? '校园机房部署工具' : path === '/download' ? '下载软件' : (siteNavItems.find(function (x) { return x[0] === path; }) || ['', 'Veyon Campus'])[1];
  document.title = pageTitle + ' · Veyon Campus';
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
    if (menu) {
      const isOpen = menu.classList.toggle('mobile-open');
      action.setAttribute('aria-expanded', String(isOpen));
      action.setAttribute('aria-label', isOpen ? '关闭导航' : '打开导航');
    }
  }
});

window.addEventListener('popstate', render);
render();

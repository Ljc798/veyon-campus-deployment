#!/usr/bin/env python3
"""把真实 styles.css + editorial.css 内联，生成自包含的视觉预览稿。

用法：python docs/experiments/web-redesign/build-preview.py
产物：docs/experiments/web-redesign/preview.html（可直接用浏览器打开，无需构建、无需服务）

预览稿只使用 website/ 的真实类名与真实样式表，DOM 为版面示意，
所有数值均为占位示例并已在页面顶部显式标注，不代表真实记录。
"""
from pathlib import Path
import re

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
STYLES = (ROOT / 'website' / 'styles.css').read_text(encoding='utf-8')
EDITORIAL = (ROOT / 'website' / 'editorial.css').read_text(encoding='utf-8')
# index.html 里站点选择器有一段内联 <style>；真实页面的加载顺序是
# styles.css → 内联 style → editorial.css，预览稿必须完整复现这个顺序。
INDEX_HTML = (ROOT / 'website' / 'index.html').read_text(encoding='utf-8')
INLINE_MATCH = re.search(r'<style>(.*?)</style>', INDEX_HTML, re.S)
INLINE_STYLES = INLINE_MATCH.group(1) if INLINE_MATCH else ''
OUTPUT = HERE / 'preview.html'

TEMPLATE = r'''<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Veyon Campus · 编辑部风格视觉预览</title>
<style>
__BASE_STYLES__
</style>
<style>
__INLINE_STYLES__
</style>
<style>
__EDITORIAL_STYLES__
</style>
<style>
/* 仅用于把多个页面区块放进同一份预览稿，不改变任何视觉皮肤 */
body { background: var(--paper); }
.preview-head { padding: 38px 0 26px; border-bottom: 3px solid var(--navy); background: var(--surface); }
.preview-head .site-container { max-width: 1240px; }
.preview-head h1 { margin: 12px 0 10px; color: var(--navy); font-family: var(--font-serif); font-size: clamp(30px, 4vw, 46px); font-weight: 600; letter-spacing: -.025em; }
.preview-head p { max-width: 780px; margin: 0; color: var(--muted); font-size: 14px; line-height: 1.8; }
.preview-note { margin-top: 16px; padding: 12px 15px; border-left: 2px solid var(--accent); background: var(--accent-soft); color: var(--ink); font-size: 13px; line-height: 1.7; }
.preview-note strong { color: var(--accent); }
.preview-stage { max-width: 1240px; margin: 40px auto 0; padding: 0 24px; }
.preview-stage-label { display: flex; margin-bottom: 14px; gap: 12px; align-items: baseline; border-bottom: 1px solid var(--line-strong); padding-bottom: 10px; }
.preview-stage-label b { color: var(--accent); font: 700 12px/1 var(--font-mono); letter-spacing: .14em; }
.preview-stage-label h2 { margin: 0; color: var(--navy); font-family: var(--font-serif); font-size: 24px; font-weight: 600; }
.preview-stage-label span { margin-left: auto; color: var(--muted); font-size: 12px; }
.preview-frame { overflow: hidden; border: 1px solid var(--line-strong); background: var(--surface); }
.preview-frame .site-nav, .preview-frame .admin-topbar, .preview-frame .sidebar,
.preview-frame .docs-toc { position: static; }
.preview-frame .site-shell, .preview-frame .admin-shell { min-height: 0; }
.preview-frame .sidebar { height: auto; min-height: 620px; }
.preview-frame .admin-main { min-width: 0; }
.preview-frame .hero { padding: 62px 0 52px; }
.preview-frame .section { padding: 58px 0; }
.preview-frame .page-hero { padding: 40px 0 32px; }
.preview-frame .admin-content { padding: 26px 28px 44px; }
.preview-stage + .preview-stage { margin-top: 52px; }
.preview-end { max-width: 1240px; margin: 54px auto 0; padding: 22px 24px 60px; border-top: 1px solid var(--line); color: var(--muted); font-size: 12px; line-height: 1.9; }
.preview-swatch { display: inline-block; width: 12px; height: 12px; margin: 0 5px -2px 0; border: 1px solid var(--line-strong); }
.component-row { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; padding: 22px; }
@media (max-width: 680px) {
  .preview-stage { padding: 0 12px; }
  .preview-frame .sidebar { position: fixed; height: 100dvh; min-height: 0; }
  .preview-frame .admin-topbar { position: sticky; }
  .preview-frame .admin-content { padding: 16px 12px 30px; }
}
</style>
</head>
<body>

<header class="preview-head">
  <div class="site-container">
    <span class="eyebrow"><i class="eyebrow-dot"></i>Design preview · 编辑部 / 技术白皮书</span>
    <h1>Veyon Campus 网站视觉预览</h1>
    <p>公开官网、站点选择器与管理员后台在同一套暖纸墨蓝设计语言下的实际渲染。以下页面直接引用 <code>website/styles.css</code> 与 <code>website/editorial.css</code> 的完整内容，未修改任何类名。</p>
    <div class="preview-note"><strong>视觉预览稿</strong> — 页面中的校区名、数值、日期与状态均为<strong>版面占位示例</strong>，仅用于检验排版与层级，不代表真实记录，也不会写入任何数据库或发送到服务端。真实后台仍由 CloudBase Auth 与 PostgreSQL RLS 提供数据。</div>
  </div>
</header>

<!-- ── 01 站点选择器 ───────────────────────────────────────────── -->
<section class="preview-stage" id="stage-01">
  <div class="preview-stage-label"><b>01</b><h2>站点选择器</h2><span>kidscode.fun 首页 / /sites</span></div>
  <div class="preview-frame">
    <main id="site-selector" style="min-height:0;padding:44px 24px">
      <div class="selector-shell" style="width:min(100%,1080px)">
        <header class="selector-heading">
          <p class="selector-brand">KidsCode · 站点入口</p>
          <h1>欢迎回来</h1>
          <p class="selector-intro">选择你要访问的站点。</p>
        </header>
        <nav class="site-options" aria-label="选择站点">
          <a class="site-card" href="#">
            <span class="site-label">新站</span>
            <span class="site-index" aria-hidden="true">01 / NEW</span>
            <h2>Veyon 校园站</h2>
            <p>下载教师端和学生端工具，查看中文部署指南。</p>
            <span class="site-go">进入 Veyon 站点 →</span>
          </a>
          <a class="site-card" href="#">
            <span class="site-label">老站</span>
            <span class="site-index" aria-hidden="true">02 / ARCHIVE</span>
            <h2>KidsCode</h2>
            <p>返回原有的 KidsCode 停服通知页面。</p>
            <span class="site-go">返回 KidsCode 老站 →</span>
          </a>
        </nav>
        <p class="selector-footnote">两个入口都可以直接在浏览器中打开。</p>
      </div>
    </main>
  </div>
</section>

<!-- ── 02 公开官网 ─────────────────────────────────────────────── -->
<section class="preview-stage" id="stage-02">
  <div class="preview-stage-label"><b>02</b><h2>公开官网 · 首页</h2><span>/ · 静态内容，无后台入口</span></div>
  <div class="preview-frame">
    <div class="site-shell">
      <header class="site-nav"><div class="site-nav-inner">
        <a class="brand-lockup" href="#"><span class="brand-copy"><strong>Veyon Campus</strong><span>校园机房部署助手</span></span></a>
        <nav class="site-links" aria-label="主导航">
          <a href="#">工具介绍</a><a href="#">部署指南</a><a href="#" class="active">关于项目</a><a href="#">问题反馈</a>
          <a href="#" class="btn nav-download">下载软件</a>
        </nav>
      </div></header>
      <main class="site-main">
        <section class="hero"><div class="site-container">
          <div class="hero-grid">
            <div class="hero-copy">
              <span class="eyebrow"><i class="eyebrow-dot"></i>Veyon Campus · 校园机房部署助手</span>
              <h1>把机房的<em>准备工作</em>，写成一份看得懂的清单</h1>
              <p>教师端与学生端 Windows 工具，配合中文部署指南，帮助学校完成 Veyon 的安装、检查与日常课堂管控。</p>
              <div class="hero-actions">
                <a class="btn" href="#" data-icon="arrow">下载教师端</a>
                <a class="btn secondary" href="#">阅读部署指南</a>
              </div>
              <div class="hero-meta">
                <span data-icon="check">支持 Windows 10 / 11</span>
                <span data-icon="clock">中文部署指南</span>
                <span data-icon="shield">签名安装包</span>
              </div>
            </div>
            <div class="hero-art">
              <div class="deployment-visual">
                <div class="deployment-visual-head"><strong style="font-size:13px;color:var(--navy)">部署检查清单</strong><span class="deployment-pill">3 个步骤</span></div>
                <ol class="deployment-steps">
                  <li class="deployment-step"><span class="deployment-index">01</span><span class="deployment-step-copy"><small>准备</small><strong>安装教师端</strong><span>在教师机完成安装并生成连接证书。</span></span><span class="deployment-step-icon" data-icon="device"></span></li>
                  <li class="deployment-step"><span class="deployment-index">02</span><span class="deployment-step-copy"><small>部署</small><strong>安装学生端</strong><span>按机房批量部署，核对设备身份。</span></span><span class="deployment-step-icon" data-icon="users"></span></li>
                  <li class="deployment-step"><span class="deployment-index">03</span><span class="deployment-step-copy"><small>检查</small><strong>核对地点与设备</strong><span>确认电脑目录与座位绑定一致。</span></span><span class="deployment-step-icon" data-icon="check"></span></li>
                </ol>
                <div class="deployment-visual-foot"><span class="deployment-foot-icon" data-icon="info"></span><span><strong>状态来自本机读取</strong><small>未取得可信状态前显示「等待核对」</small></span></div>
              </div>
            </div>
          </div>
        </div></section>
        <section class="trust-strip"><div class="site-container"><div class="trust-row">
          <div class="trust-item"><span class="trust-item-icon" data-icon="shield"></span><span><strong>签名发布</strong><small>安装包带发布签名与摘要校验</small></span></div>
          <div class="trust-item"><span class="trust-item-icon" data-icon="book"></span><span><strong>中文指南</strong><small>按实际部署顺序编排的说明</small></span></div>
          <div class="trust-item"><span class="trust-item-icon" data-icon="database"></span><span><strong>数据边界清楚</strong><small>后台只展示已授权的真实记录</small></span></div>
        </div></div></section>
        <section class="section"><div class="site-container">
          <div class="section-head section-head-left">
            <span class="eyebrow"><i class="eyebrow-dot"></i>工具与文档</span>
            <h2>先装好，再核对，然后开始上课</h2>
            <p>把复杂的机房准备工作拆成可逐步确认的动作，每一步都有明确的状态说明。</p>
          </div>
          <div class="feature-grid">
            <article class="feature-card"><span class="feature-icon" data-icon="device"></span><h3>教师端控制台</h3><p>管理课堂、座位图与设备目录，查看每台学生电脑的可信状态。</p><a href="#">了解教师端 <span data-icon="arrow"></span></a></article>
            <article class="feature-card"><span class="feature-icon" data-icon="users"></span><h3>学生端安装</h3><p>按机房批量部署，安装后自动完成身份固定与策略初始化。</p><a href="#">查看安装说明 <span data-icon="arrow"></span></a></article>
            <article class="feature-card"><span class="feature-icon" data-icon="book"></span><h3>部署与验收指南</h3><p>从准备到验收的完整中文步骤，包含常见问题与回退方式。</p><a href="#">打开文档 <span data-icon="arrow"></span></a></article>
          </div>
        </div></section>
        <section class="section tint"><div class="site-container">
          <div class="cta-panel"><div><h2>准备好开始部署了吗？</h2><p>先下载稳定版安装包，再按中文指南逐步核对，避免在机房现场反复试错。</p></div><a class="btn" href="#" data-icon="download">前往下载页</a></div>
        </div></section>
      </main>
      <footer class="site-footer"><div class="site-container"><div class="footer-grid">
        <div class="footer-about"><strong class="brand-lockup" style="font-size:16px">Veyon Campus</strong><p>校园机房部署助手。公开页面仅介绍产品与提供文档，不包含管理员入口。</p></div>
        <div class="footer-col"><strong>工具</strong><a href="#">教师端</a><a href="#">学生端</a><a href="#">下载页</a></div>
        <div class="footer-col"><strong>文档</strong><a href="#">部署指南</a><a href="#">使用说明</a><a href="#">常见问题</a></div>
        <div class="footer-col"><strong>项目</strong><a href="#">关于项目</a><a href="#">问题反馈</a></div>
      </div><div class="footer-bottom"><span>© Veyon Campus · 视觉预览稿</span><span>版面料理 · 编辑部风格示例</span></div></div></footer>
    </div>
  </div>
</section>

<!-- ── 03 管理员后台 ───────────────────────────────────────────── -->
<section class="preview-stage" id="stage-03">
  <div class="preview-stage-label"><b>03</b><h2>管理员后台 · 数据总览</h2><span>/admin · 需 CloudBase Auth 授权</span></div>
  <div class="preview-frame">
    <div class="admin-shell">
      <aside class="sidebar">
        <a class="sidebar-brand" href="#"><div><strong>Veyon Campus</strong><small>管理后台</small></div></a>
        <div class="workspace-switch"><span class="workspace-avatar">VC</span><span><strong>Veyon Campus</strong><small>CloudBase PostgreSQL</small></span></div>
        <div class="nav-section-label">管理</div>
        <nav class="side-nav" aria-label="管理后台导航">
          <a href="#" class="active" data-icon="grid"><span>数据总览</span></a>
          <a href="#" data-icon="building"><span>校区管理</span><small class="nav-count">12</small></a>
          <a href="#" data-icon="activity"><span>匿名统计</span></a>
          <a href="#" data-icon="chart"><span>趋势分析</span></a>
          <a href="#" data-icon="database"><span>数据库资料</span></a>
          <a href="#" data-icon="terminal"><span>API 能力</span></a>
          <a href="#" data-icon="download"><span>下载页管理</span></a>
          <a href="#" data-icon="download"><span>版本发布</span></a>
          <a href="#" data-icon="settings"><span>账号与连接</span></a>
        </nav>
        <div class="sidebar-spacer"></div>
        <div class="sidebar-help"><strong>当前账号</strong><p>示例管理员 · 管理员</p><a href="#">查看账号与连接 <span data-icon="settings"></span></a></div>
        <div class="sidebar-profile"><span class="profile-avatar">管</span><span><strong>示例管理员</strong><small>管理员</small></span></div>
      </aside>
      <div class="admin-main">
        <header class="admin-topbar">
          <div class="topbar-title"><div><h1>数据总览</h1><small>校区与匿名使用汇总</small></div></div>
          <div class="topbar-actions">
            <button class="search-button" type="button" data-icon="search"><span>查找管理页面</span><kbd>⌘ K / Ctrl K</kbd></button>
            <span class="topbar-demo" data-icon="activity">管理工作台</span>
            <button class="icon-button" type="button" data-icon="logout" aria-label="退出登录"></button>
          </div>
        </header>
        <main class="admin-content">
          <div class="demo-banner"><div class="demo-banner-note" data-icon="info"><span><strong>数据已连接</strong> — 当前账号权限：管理员。</span></div><div class="api-health" data-state="ready"><span class="api-health-dot"></span><span class="api-health-copy"><strong>服务连接：正常</strong><small>这里只检查服务访问状态</small></span></div></div>
          <div class="page-heading"><div><h2>数据总览</h2><p>以下数值为版面占位示例，真实页面只显示数据库中已授权的记录。</p></div><div class="heading-actions"><button class="btn secondary sm" type="button" data-icon="refresh">刷新</button><button class="btn sm" type="button" data-icon="download">导出 CSV</button></div></div>
          <div class="stat-grid">
            <article class="card stat-card"><div class="stat-top"><span>已登记校区</span><span class="stat-icon" data-icon="building"></span></div><div class="stat-value">12</div><div class="stat-bottom"><span>来源：campuses 表（占位）</span></div></article>
            <article class="card stat-card"><div class="stat-top"><span>启用校区</span><span class="stat-icon" data-icon="check"></span></div><div class="stat-value">10</div><div class="stat-bottom"><span>状态为启用（占位）</span></div></article>
            <article class="card stat-card"><div class="stat-top"><span>按日汇总</span><span class="stat-icon" data-icon="activity"></span></div><div class="stat-value">28</div><div class="stat-bottom"><span>Asia/Hong_Kong（占位）</span></div></article>
            <article class="card stat-card"><div class="stat-top"><span>API 端点</span><span class="stat-icon" data-icon="terminal"></span></div><div class="stat-value">21</div><div class="stat-bottom"><span>来自 OpenAPI 契约</span></div></article>
          </div>
          <div class="dashboard-grid">
            <section class="card card-pad chart-card">
              <div class="card-heading"><div><h3>按日匿名汇总趋势</h3><p>示例曲线，仅用于检验排版与图表配色</p></div><div class="chart-legend"><span><i class="legend-dot"></i>已记录</span><span><i class="legend-dot muted-dot"></i>未记录</span></div></div>
              <div class="chart-wrap"><svg viewBox="0 0 640 220" preserveAspectRatio="none" aria-hidden="true">
                <g stroke="var(--line)" stroke-width="1"><line x1="0" y1="40" x2="640" y2="40"/><line x1="0" y1="90" x2="640" y2="90"/><line x1="0" y1="140" x2="640" y2="140"/><line x1="0" y1="190" x2="640" y2="190"/></g>
                <polyline points="0,168 64,150 128,158 192,118 256,132 320,96 384,110 448,72 512,88 576,54 640,66" fill="none" stroke="var(--brand)" stroke-width="2.2" stroke-linejoin="round" stroke-linecap="round"/>
                <g fill="var(--surface)" stroke="var(--brand)" stroke-width="2"><circle cx="192" cy="118" r="3.5"/><circle cx="320" cy="96" r="3.5"/><circle cx="448" cy="72" r="3.5"/><circle cx="576" cy="54" r="3.5"/></g>
              </svg></div>
              <div class="chart-labels" style="padding-left:0"><span>10/07</span><span>10/14</span><span>10/21</span><span>10/28</span><span>11/04</span></div>
            </section>
            <section class="card card-pad">
              <div class="card-heading"><div><h3>地区分布</h3><p>占位示例</p></div></div>
              <div class="region-list">
                <div class="region-row"><strong>华东</strong><div class="bar-track"><div class="bar-fill" style="width:72%"></div></div><small>72%</small></div>
                <div class="region-row"><strong>华南</strong><div class="bar-track"><div class="bar-fill" style="width:54%"></div></div><small>54%</small></div>
                <div class="region-row"><strong>华北</strong><div class="bar-track"><div class="bar-fill" style="width:38%"></div></div><small>38%</small></div>
                <div class="region-row"><strong>西南</strong><div class="bar-track"><div class="bar-fill" style="width:21%"></div></div><small>21%</small></div>
              </div>
              <div class="region-footnote">数据只来自已授权字段，空结果会显示空状态。</div>
            </section>
          </div>
          <section class="card table-card">
            <div class="card-pad card-heading"><div><h3>最近更新的校区</h3><p>示例行，仅用于检验表格层级与可读性</p></div><a class="text-link" href="#">查看全部 <span data-icon="arrow"></span></a></div>
            <div class="table-wrap"><table><thead><tr><th>校区名称</th><th>地区 / 城市</th><th>状态</th><th>更新时间</th><th>操作</th></tr></thead><tbody>
              <tr><td><div class="campus-name"><span class="campus-mark">示</span><span><strong>示例第一中学</strong><small>占位 · 非真实记录</small></span></div></td><td>华东 / 上海</td><td><span class="pill">启用</span></td><td>2026-11-05 09:12</td><td><button class="row-action" type="button" data-icon="dots" aria-label="更多操作"></button></td></tr>
              <tr><td><div class="campus-name"><span class="campus-mark">示</span><span><strong>示例实验中学</strong><small>占位 · 非真实记录</small></span></div></td><td>华南 / 广州</td><td><span class="pill warn">待核对</span></td><td>2026-11-04 16:40</td><td><button class="row-action" type="button" data-icon="dots" aria-label="更多操作"></button></td></tr>
              <tr><td><div class="campus-name"><span class="campus-mark">示</span><span><strong>示例城北小学</strong><small>占位 · 非真实记录</small></span></div></td><td>华北 / 北京</td><td><span class="pill neutral">未启用</span></td><td>2026-11-03 11:05</td><td><button class="row-action" type="button" data-icon="dots" aria-label="更多操作"></button></td></tr>
            </tbody></table></div>
            <div class="pagination"><span>共 12 条 · 第 1 / 1 页（占位）</span><div class="page-buttons"><button type="button">1</button><button type="button">2</button></div></div>
          </section>
        </main>
      </div>
    </div>
  </div>
</section>

<!-- ── 04 组件与色板 ───────────────────────────────────────────── -->
<section class="preview-stage" id="stage-04">
  <div class="preview-stage-label"><b>04</b><h2>设计语言 · 组件样本</h2><span>按钮 / 状态 / 边注 / 空状态</span></div>
  <div class="preview-frame">
    <div class="component-row">
      <button class="btn" type="button">主要操作</button>
      <button class="btn secondary" type="button">次要操作</button>
      <button class="btn ghost" type="button">文字操作</button>
      <button class="btn dark sm" type="button">深色按钮</button>
      <span class="pill">启用</span><span class="pill warn">待核对</span><span class="pill neutral">未启用</span>
      <span class="method-badge">GET</span><span class="method-badge post">POST</span>
    </div>
    <div style="padding:0 22px 22px">
      <div class="callout" style="margin-bottom:16px"><div><strong>边注 · 需要人工核对</strong><p>颜色不是唯一状态提示：所有状态都同时保留文字标签与图标，关键说明不小于 12px。</p></div></div>
      <div class="privacy-note" style="margin-top:0" data-icon="info"><span>数据边界：后台只查询经 grants / RLS 授权的字段；公开页面不显示管理员入口。</span></div>
    </div>
    <div style="padding:0 22px 26px">
      <div class="card" style="border-radius:var(--radius)"><div class="empty-state"><span data-icon="database"></span>暂无记录 — 数据库中没有符合条件的记录时显示空状态，不以虚构数据填充。</div></div>
    </div>
    <div class="component-row" style="border-top:1px solid var(--line);font-size:12px;color:var(--muted);gap:20px">
      <span><i class="preview-swatch" style="background:#f8f6f0"></i>纸面 #F8F6F0</span>
      <span><i class="preview-swatch" style="background:#132e46"></i>墨蓝 #132E46</span>
      <span><i class="preview-swatch" style="background:#187b70"></i>品牌青绿 #187B70</span>
      <span><i class="preview-swatch" style="background:#b04a30"></i>朱赭批注 #B04A30</span>
      <span>字体：系统衬线 + 系统无衬线，无外部 CDN</span>
    </div>
  </div>
</section>

<footer class="preview-end">
  本预览稿由 <code>docs/experiments/web-redesign/build-preview.py</code> 生成，内联了两份真实样式表，因此可脱离构建与网络直接打开。
  它不调用任何 API、不加载 CloudBase SDK、不写入浏览器存储。生产网站的实际路由、权限与数据行为未改动。
</footer>

<script>
const GLYPHS = {
  grid: '<rect x="3" y="3" width="7" height="7"/><rect x="14" y="3" width="7" height="7"/><rect x="3" y="14" width="7" height="7"/><rect x="14" y="14" width="7" height="7"/>',
  building: '<path d="M3 21h18M5 21V5l7-3 7 3v16M9 8h.01M15 8h.01M9 12h.01M15 12h.01M10 21v-5h4v5"/>',
  activity: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
  chart: '<path d="M3 3v18h18M8 15l4-4 4 3 5-7"/>',
  database: '<ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14c0 1.7 4 3 9 3s9-1.3 9-3V5M3 12c0 1.7 4 3 9 3s9-1.3 9-3"/>',
  terminal: '<path d="m4 17 6-5-6-5M12 19h8"/>',
  settings: '<circle cx="12" cy="12" r="3"/><path d="M19 15l2 1-2 4-2-1-2 1-.3 2h-4L10 20l-2-1-2 1-2-4 2-1v-2l-2-1 2-4 2 1 2-1 .3-2h4L15 8l2 1 2-1 2 4-2 1v2Z"/>',
  logout: '<path d="M10 17l5-5-5-5M15 12H3M12 3h6a3 3 0 0 1 3 3v12a3 3 0 0 1-3 3h-6"/>',
  download: '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/>',
  info: '<circle cx="12" cy="12" r="10"/><path d="M12 11v5M12 8h.01"/>',
  refresh: '<path d="M20 7v5h-5M4 17v-5h5M5 9a7 7 0 0 1 12-2l3 5M4 12l3 5a7 7 0 0 0 12-2"/>',
  arrow: '<path d="M5 12h14M13 6l6 6-6 6"/>',
  check: '<path d="m5 12 4 4L19 6"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  shield: '<path d="M12 22s8-4 8-11V5l-8-3-8 3v6c0 7 8 11 8 11Z"/><path d="m9 12 2 2 4-4"/>',
  book: '<path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2Z"/>',
  device: '<rect x="3" y="4" width="18" height="13"/><path d="M8 21h8M12 17v4"/>',
  users: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8ZM20 8v6M23 11h-6"/>',
  dots: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>'
};
document.querySelectorAll('[data-icon]').forEach(function (el) {
  // 只插入图标，绝不覆盖容器里已有的文字/子元素。
  var holder = document.createElement('span');
  holder.className = 'icon';
  holder.setAttribute('aria-hidden', 'true');
  holder.innerHTML = '<svg viewBox="0 0 24 24">' + (GLYPHS[el.dataset.icon] || '') + '</svg>';
  if (el.children.length === 0 && el.textContent.trim() === '') {
    el.appendChild(holder);
  } else {
    el.insertBefore(holder, el.firstChild);
  }
});
if (window.innerWidth < 700) {
  var badge = document.createElement('div');
  badge.textContent = 'viewport ' + window.innerWidth + ' x ' + window.innerHeight;
  badge.style.cssText = 'position:fixed;right:8px;bottom:8px;z-index:999;padding:5px 9px;border-radius:2px;background:#132e46;color:#fff;font:11px monospace';
  document.body.appendChild(badge);
}
var hash = window.location.hash;
if (hash && hash.indexOf('#stage-') === 0) {
  var active = document.querySelector(hash);
  if (active) {
    document.querySelectorAll('.preview-stage').forEach(function (el) {
      if (el !== active) el.style.display = 'none';
    });
    document.querySelector('.preview-head').style.display = 'none';
    document.querySelector('.preview-end').style.display = 'none';
    active.style.marginTop = '0';
  }
}
</script>
</body>
</html>
'''

html = (TEMPLATE.replace('__BASE_STYLES__', STYLES)
        .replace('__INLINE_STYLES__', INLINE_STYLES)
        .replace('__EDITORIAL_STYLES__', EDITORIAL))
OUTPUT.write_text(html, encoding='utf-8')
print(f'Wrote {OUTPUT} ({len(html):,} chars); styles.css + index.html inline style + editorial.css inlined.')

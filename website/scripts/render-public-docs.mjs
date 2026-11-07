import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { CATEGORIES, escapeHtml, loadPublicDocuments, renderMarkdown, scanForPublicSecrets } from './public-docs-lib.mjs';

const websiteDirectory = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const distDirectory = path.join(websiteDirectory, 'dist');
const contentDirectory = path.join(websiteDirectory, 'content', 'docs');
const indexPath = path.join(distDirectory, 'index.html');
if (!fs.existsSync(indexPath)) throw new Error('Vite output website/index.html is missing; run vite build first');
for (const name of [
  '__search-evaluation.local.html', '__search-evaluation.local.js', '__search-evaluation.local-cases.json'
]) {
  const localEvaluationFile = path.join(distDirectory, 'docs', name);
  if (fs.existsSync(localEvaluationFile)) fs.rmSync(localEvaluationFile, { force: true });
}

const documents = loadPublicDocuments(contentDirectory);
const published = documents.filter(document => document.status === 'published');
const publishedSlugs = new Set(published.map(document => document.slug));
if (published.length < 25 || published.length > 40)
  throw new Error(`first-wave public article count must be between 25 and 40; got ${published.length}`);

const publishedHeadingIds = new Map(published.map(document => {
  const headings = renderMarkdown(document.body, { publishedSlugs, sourcePath: document.sourcePath }).headings;
  return [document.slug, new Set(headings.map(heading => heading.id))];
}));

for (const document of published) {
  for (const reference of document.sourceRefs) {
    if (path.isAbsolute(reference) || reference.split(/[\\/]/).includes('..'))
      throw new Error(`${document.sourcePath}: unsafe source reference '${reference}'`);
  }
}

const rootHtml = fs.readFileSync(indexPath, 'utf8');
const stylesheetTags = [...rootHtml.matchAll(/<link\b[^>]*rel="stylesheet"[^>]*>/gi)].map(match => match[0]);
const stylesheetLinks = stylesheetTags.map(tag => tag.match(/\bhref="([^"]+)"/i)?.[1]).filter(Boolean);
if (stylesheetLinks.length === 0) throw new Error('could not resolve Vite stylesheet for generated documentation articles');
const stylesheetHtml = stylesheetLinks.map(href => `<link rel="stylesheet" href="${escapeHtml(href)}">`).join('\n    ');
const articleDirectory = path.join(distDirectory, 'docs', 'articles');
fs.rmSync(articleDirectory, { recursive: true, force: true });
fs.mkdirSync(articleDirectory, { recursive: true });

const catalog = [];
for (const document of published) {
  const context = {
    publishedSlugs,
    publishedHeadingIds,
    currentSlug: document.slug,
    currentHeadingIds: publishedHeadingIds.get(document.slug),
    sourcePath: document.sourcePath
  };
  const rendered = renderMarkdown(document.body, context);
  const textLength = document.body.replace(/\s+/g, '').length;
  const readingMinutes = Math.max(1, Math.ceil(textLength / 450));
  const url = `/docs/articles/${document.slug}.html`;
  const category = CATEGORIES[document.category];
  const audienceLabel = document.audience.map(item => ({ teacher: '教师', student: '学生电脑', 'school-it': '学校 IT' })[item]).join('、');
  const verificationLabel = document.verificationStatus === 'source-reviewed-needs-field-test'
    ? `源码核对 ${document.verifiedFor} · 现场环境需再核验`
    : `源码核对 ${document.verifiedFor}`;
  const related = document.related.slice(0, 3).map(slug => {
    const item = published.find(candidate => candidate.slug === slug);
    return `<li><a href="/docs/articles/${escapeHtml(slug)}.html">${escapeHtml(item.title)}</a><span>${escapeHtml(item.summary)}</span></li>`;
  }).join('\n');
  const toc = rendered.headings.map(heading => `<a class="toc-level-${heading.level}" href="#${escapeHtml(heading.id)}">${escapeHtml(heading.text)}</a>`).join('\n');
  const body = `<!doctype html>
<html lang="zh-CN">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <meta name="description" content="${escapeHtml(document.summary)}">
    <meta name="pagefind:source" content="public-docs-v1">
    <meta data-pagefind-meta="summary[content]" content="${escapeHtml(document.summary)}">
    <meta data-pagefind-meta="verifiedFor[content]" content="${escapeHtml(document.verifiedFor)}">
    <meta data-pagefind-meta="category[content]" content="${escapeHtml(category)}">
    <meta data-pagefind-meta="lastReviewed[content]" content="${escapeHtml(document.lastReviewed)}">
    <meta data-pagefind-meta="keywords[content]" content="${escapeHtml(document.keywords.join(' '))}">
    <meta data-pagefind-meta="aliases[content]" content="${escapeHtml(document.aliases.join(' '))}">
    <meta data-pagefind-meta="errorCodes[content]" content="${escapeHtml(document.errorCodes.join(' '))}">
    <link rel="canonical" href="https://kidscode.fun${url}">
    <meta property="og:type" content="article">
    <meta property="og:title" content="${escapeHtml(document.title)} · 帮助与文档">
    <meta property="og:description" content="${escapeHtml(document.summary)}">
    <meta property="og:url" content="https://kidscode.fun${url}">
    <link rel="icon" type="image/svg+xml" href="/assets/veyon-campus-mark.svg">
    ${stylesheetHtml}
    <title>${escapeHtml(document.title)} · 帮助与文档 · Veyon Campus</title>
  </head>
  <body>
    <div class="site-shell">
      <a class="skip-link" href="#main-content">跳到主要内容</a>
      <header class="site-nav"><div class="site-nav-inner">
        <a class="brand-lockup" href="/?site=veyon"><img src="/assets/veyon-campus-mark.svg" alt=""><span class="brand-copy"><strong>Veyon Campus</strong><span>校园机房部署助手</span></span></a>
        <nav class="site-links" aria-label="主导航"><a href="/product">工具介绍</a><a class="active" href="/docs/">帮助与文档</a><a href="/about">关于项目</a><a href="/contact">问题反馈</a><a class="btn nav-download" href="/download">下载软件</a></nav>
      </div></header>
      <main id="main-content" class="site-main docs-article-page">
        <section class="page-hero docs-article-hero"><div class="site-container">
          <nav class="docs-breadcrumb" aria-label="面包屑"><a href="/docs/">帮助与文档</a><span aria-hidden="true">/</span><span>${escapeHtml(category)}</span></nav>
          <h1 data-pagefind-meta="title">${escapeHtml(document.title)}</h1>
          <p class="docs-article-summary">${escapeHtml(document.summary)}</p>
          <div class="docs-article-meta"><span>${escapeHtml(audienceLabel)}</span><span>${escapeHtml(verificationLabel)}</span><span>更新于 ${escapeHtml(document.lastReviewed)}</span><span>约 ${readingMinutes} 分钟</span><span data-pagefind-filter="category">${escapeHtml(category)}</span></div>
        </div></section>
        <section class="page-body"><div class="site-container docs-layout">
          <aside class="docs-toc"><strong>本文目录</strong><nav aria-label="本文目录">${toc}</nav><a class="docs-back-link" href="/docs/">返回搜索与分类</a></aside>
          <article class="docs-article"><div class="docs-content" data-pagefind-body>${rendered.html}<p class="docs-search-terms"><span>也可以搜：</span>${escapeHtml(document.keywords.slice(0, 8).join('、'))}</p></div>
            <aside class="docs-related" aria-labelledby="related-title"><h2 id="related-title">接下来可以看</h2><ul>${related}</ul></aside>
          </article>
        </div></section>
      </main>
      <footer class="site-footer"><div class="site-container"><div class="footer-grid"><span>Veyon Campus · 帮助与文档</span><a href="/contact">问题反馈</a></div></div></footer>
    </div>
  </body>
</html>`;
  scanForPublicSecrets(body, url);
  fs.writeFileSync(path.join(articleDirectory, `${document.slug}.html`), body, 'utf8');
  catalog.push({
    slug: document.slug,
    title: document.title,
    summary: document.summary,
    category: document.category,
    categoryLabel: category,
    audience: document.audience,
    keywords: document.keywords,
    aliases: document.aliases,
    errorCodes: document.errorCodes,
    verifiedFor: document.verifiedFor,
    verificationStatus: document.verificationStatus,
    lastReviewed: document.lastReviewed,
    readingMinutes,
    url,
    related: document.related
  });
}

scanForPublicSecrets(JSON.stringify(catalog), 'docs/catalog.json');
fs.writeFileSync(path.join(distDirectory, 'docs', 'catalog.json'), `${JSON.stringify(catalog, null, 2)}\n`, 'utf8');

const docsIndex = rootHtml
  .replace(/<html lang="zh-CN">/i, '<html lang="zh-CN" data-pagefind-ignore>')
  .replace(/<meta name="description" content="[^"]*"\s*\/?\s*>/i, '<meta name="description" content="搜索 Veyon Campus 部署、课堂控制、座位管理和故障排查指南。">')
  .replace(/<link rel="canonical" href="[^"]*"\s*\/?\s*>/i, '<link rel="canonical" href="https://kidscode.fun/docs">')
  .replace(/<meta property="og:title" content="[^"]*"\s*\/?\s*>/i, '<meta property="og:title" content="有什么问题？直接搜一下 · 帮助与文档">')
  .replace(/<meta property="og:description" content="[^"]*"\s*\/?\s*>/i, '<meta property="og:description" content="搜索 Veyon Campus 部署、课堂控制、座位管理和故障排查指南。">')
  .replace(/<meta property="og:url" content="[^"]*"\s*\/?\s*>/i, '<meta property="og:url" content="https://kidscode.fun/docs">')
  .replace(/<title>[^<]*<\/title>/i, '<title>有什么问题？直接搜一下 · 帮助与文档 · Veyon Campus</title>');
if (!docsIndex.includes('https://kidscode.fun/docs') || !docsIndex.includes('帮助与文档') ||
    !/<html lang="zh-CN"[^>]*data-pagefind-ignore/i.test(docsIndex))
  throw new Error('failed to prepare the /docs shell metadata and index exclusion');
fs.writeFileSync(path.join(distDirectory, 'docs', 'index.html'), docsIndex, 'utf8');

const articleRoot = path.resolve(articleDirectory) + path.sep;
const excludeNonArticleHtml = directory => {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const filePath = path.join(directory, entry.name);
    if (entry.isDirectory()) excludeNonArticleHtml(filePath);
    else if (entry.isFile() && entry.name.endsWith('.html') && !path.resolve(filePath).startsWith(articleRoot)) {
      const html = fs.readFileSync(filePath, 'utf8');
      if (!/<html\b[^>]*>/i.test(html)) throw new Error(`HTML root element missing: ${filePath}`);
      const ignoredHtml = html
        .replace(/<html\b([^>]*)>/i, (tag, attributes) =>
          /\bdata-pagefind-ignore\b/i.test(attributes) ? tag : `<html${attributes} data-pagefind-ignore>`)
        .replace(/<body\b([^>]*)>/i, (tag, attributes) =>
          /\bdata-pagefind-ignore\b/i.test(attributes) ? tag : `<body${attributes} data-pagefind-ignore>`);
      fs.writeFileSync(filePath, ignoredHtml, 'utf8');
    }
  }
};
excludeNonArticleHtml(distDirectory);

const sitemapEntries = [
  'https://kidscode.fun/', 'https://kidscode.fun/product', 'https://kidscode.fun/docs',
  'https://kidscode.fun/download', 'https://kidscode.fun/about', 'https://kidscode.fun/contact',
  ...catalog.map(document => `https://kidscode.fun${document.url}`)
];
const sitemap = `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${sitemapEntries.map(url => `  <url><loc>${escapeHtml(url)}</loc></url>`).join('\n')}\n</urlset>\n`;
fs.writeFileSync(path.join(distDirectory, 'sitemap.xml'), sitemap, 'utf8');
fs.writeFileSync(path.join(distDirectory, 'robots.txt'), 'User-agent: *\nAllow: /\nSitemap: https://kidscode.fun/sitemap.xml\n', 'utf8');

console.log(`Rendered ${catalog.length} source-reviewed public documentation pages.`);

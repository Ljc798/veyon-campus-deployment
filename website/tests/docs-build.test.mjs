import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadPublicDocuments, renderMarkdown, scanForPublicSecrets } from '../scripts/public-docs-lib.mjs';
import { mergeSearchResults, queryVariants, relaxQuery } from '../src/docs-search.js';

const websiteDirectory = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const distDirectory = path.join(websiteDirectory, 'dist');
const documents = loadPublicDocuments(path.join(websiteDirectory, 'content', 'docs'));
const published = documents.filter(document => document.status === 'published');
assert.ok(published.length >= 25 && published.length <= 40, `expected 25-40 published articles; got ${published.length}`);
assert.throws(() => scanForPublicSecrets('联系电话：13812345678', 'fixture'), /public safety scan matched/);
assert.throws(() => scanForPublicSecrets('teacher@example.edu', 'fixture'), /public safety scan matched/);

const slugs = new Set(published.map(document => document.slug));
const hostileMarkdown = renderMarkdown('<img src=x onerror=alert(1)>\n\n[坏链接](javascript:alert(1))', {
  publishedSlugs: slugs,
  sourcePath: 'security-fixture.md'
}).html;
assert.match(hostileMarkdown, /&lt;img/);
assert.doesNotMatch(hostileMarkdown, /<img\b|javascript:/i, 'raw HTML and script URLs must not survive Markdown rendering');
const headingContext = {
  publishedSlugs: new Set([...slugs, 'anchor-target']),
  publishedHeadingIds: new Map([['anchor-target', new Set(['valid-heading'])]]),
  currentSlug: 'security-fixture',
  currentHeadingIds: new Set(['local-heading']),
  sourcePath: 'security-fixture.md'
};
assert.throws(() => renderMarkdown('[失效文章](./missing-article.md)', headingContext), /unpublished or missing article/);
assert.throws(() => renderMarkdown('[失效锚点](./anchor-target.md#missing-heading)', headingContext), /missing heading/);
assert.throws(() => renderMarkdown('[未知路径](/docs/not-a-route)', headingContext), /unknown route/);
assert.throws(() => renderMarkdown('[相对路径遗漏](missing-article.html)', headingContext), /unsupported relative link/);
assert.throws(() => renderMarkdown('[当前页失效锚点](#missing-heading)', headingContext), /missing heading/);
for (const document of published) {
  const rendered = renderMarkdown(document.body, { publishedSlugs: slugs, sourcePath: document.sourcePath });
  assert.ok(rendered.headings.length > 0, `${document.slug}: article has no headings`);
  scanForPublicSecrets(document.body, document.sourcePath);
}

const queryCases = JSON.parse(fs.readFileSync(path.join(websiteDirectory, 'tests', 'docs-search-queries.json'), 'utf8'));
assert.ok(queryCases.length >= 30, `expected at least 30 search cases; got ${queryCases.length}`);
const queryIds = new Set();
for (const testCase of queryCases) {
  assert.ok(testCase.id && !queryIds.has(testCase.id), `search case id is missing or duplicated: ${testCase.id}`);
  queryIds.add(testCase.id);
  assert.ok(typeof testCase.query === 'string' && testCase.query.trim().length > 0, `${testCase.id}: query is empty`);
  assert.ok(slugs.has(testCase.expectedSlug), `${testCase.id}: expected article '${testCase.expectedSlug}' does not exist`);
  assert.ok(Array.isArray(testCase.acceptableSlugs) && testCase.acceptableSlugs.every(slug => slugs.has(slug)),
    `${testCase.id}: an acceptable article does not exist`);
  assert.ok(testCase.verifiedFor, `${testCase.id}: version scope is missing`);
}

// Every query also yields one relaxed variant (the same question with its interrogative scaffolding
// removed) so a long Chinese sentence is not reduced to zero results by Pagefind's AND matching.
// These assertions keep checking the curated alias expansions, and separately prove that relaxation is
// the only variant added on top of them.
const aliasVariants = query => {
  const relaxed = relaxQuery(query);
  const variants = queryVariants(query);
  if (relaxed) assert.ok(variants.includes(relaxed), `relaxed variant missing for: ${query}`);
  return variants.filter(variant => variant !== relaxed);
};

assert.deepEqual(aliasVariants('怎么打印座位表'), ['怎么打印座位表', '打印 座位表', '座位图']);
assert.deepEqual(aliasVariants('机房里新加了一台学生电脑，怎么登记？'), [
  '机房里新加了一台学生电脑，怎么登记？', '机房 新增 学生电脑 UUID', '学生电脑 Host 地址 设备目录'
]);
assert.deepEqual(aliasVariants('Veyon 里出现两个同名机房怎么办？'), [
  'Veyon 里出现两个同名机房怎么办？', '同名地点 设备目录 UUID', '重复机房 Veyon'
]);
assert.deepEqual(aliasVariants('名单里网站地址要不要带 www？'), [
  '名单里网站地址要不要带 www？', '白名单 域名 www 子域名', '网站名单 域名匹配'
]);
assert.deepEqual(aliasVariants('教师端连不上学生端，但 Veyon 本身看起来没问题'), [
  '教师端连不上学生端，但 Veyon 本身看起来没问题', '教师端 连接 学生电脑', 'Veyon 连接'
]);
assert.deepEqual(aliasVariants('教师端怎么远程更新学生电脑？'), [
  '教师端怎么远程更新学生电脑？', '学生端更新 待核对', '后台维护服务'
]);
assert.deepEqual(aliasVariants('教师控制台本身怎么更新？'), [
  '教师控制台本身怎么更新？', '教师端 检查更新 下载', 'TeacherConsole 更新'
]);
assert.deepEqual(aliasVariants('1.1.77 学生端更新'), [
  '1.1.77 学生端更新', '学生端更新 待核对', '后台维护服务'
]);
assert.deepEqual(aliasVariants('学生电脑后来开机，课堂限制会怎样？'), [
  '学生电脑后来开机，课堂限制会怎样？', '课堂 离线 设备重新上线', '课堂策略 离线 重试'
]);
assert.deepEqual(aliasVariants('推送的网页限制到点会自动解除吗？'), [
  '推送的网页限制到点会自动解除吗？', '下课 恢复网站策略', '课堂策略 自动解除 到期'
]);

// Relaxation must actually shorten the question, keep the meaningful words, and stay a no-op for a
// query that is already just keywords. Word boundaries follow Intl.Segmenter (it splits 管理员 into
// 管理 + 员), so assert on the words that must survive rather than on the segmenter's exact output.
const relaxedInstall = relaxQuery('学生电脑安装之前要准备什么？');
assert.ok(relaxedInstall.split(' ').includes('安装') && relaxedInstall.split(' ').includes('准备'));
assert.ok(!relaxedInstall.includes('什么') && !relaxedInstall.includes('之前'));
const relaxedAdmin = relaxQuery('学生电脑没有管理员权限还能安装吗？');
assert.ok(relaxedAdmin.split(' ').includes('权限') && relaxedAdmin.split(' ').includes('安装'));
assert.ok(!relaxedAdmin.includes('还能') && !relaxedAdmin.includes('吗'));
assert.equal(relaxQuery('安装'), '');
assert.equal(relaxQuery('座位图'), '');
assert.equal(relaxQuery('   '), '');
const result = (url, matchedQuery) => ({
  data: { url, plain_excerpt: 'matching excerpt', meta: { title: url } },
  matchedQuery
});
const merged = mergeSearchResults([
  [result('/docs/articles/direct-first.html', '原始问题'), result('/docs/articles/shared.html', '原始问题')],
  [result('/docs/articles/alias-first.html', '别名说法'), result('/docs/articles/shared.html', '别名说法')]
], new Set(['direct-first', 'alias-first', 'shared']));
assert.deepEqual(merged.map(item => item.url), [
  '/docs/articles/direct-first.html', '/docs/articles/alias-first.html', '/docs/articles/shared.html'
]);
assert.equal(merged[1].matchedQuery, '别名说法', 'alias results must retain the query used for excerpt highlighting');
const sectionResult = {
  data: {
    url: '/docs/articles/sectioned.html',
    plain_excerpt: 'whole article excerpt',
    meta: { title: '按段落展示' },
    sub_results: [{
      url: '/docs/articles/sectioned.html#network-checks',
      plain_excerpt: '最相关段落摘要'
    }]
  },
  matchedQuery: '局域网连不上'
};
const sectionMerged = mergeSearchResults([[sectionResult], [
  result('/docs/articles/sectioned.html', '原始问题')
]], new Set(['sectioned']));
assert.equal(sectionMerged.length, 1, 'one article must not be duplicated across query variants or section anchors');
assert.equal(sectionMerged[0].url, '/docs/articles/sectioned.html#network-checks');
assert.equal(sectionMerged[0].plainExcerpt, '最相关段落摘要');

for (const relative of ['docs/index.html', 'docs/catalog.json', 'sitemap.xml', 'robots.txt', 'pagefind/pagefind.js'])
  assert.ok(fs.existsSync(path.join(distDirectory, relative)), `build output is missing ${relative}`);
const catalog = JSON.parse(fs.readFileSync(path.join(distDirectory, 'docs', 'catalog.json'), 'utf8'));
assert.equal(catalog.length, published.length, 'catalog count differs from published source count');
const articleRoot = path.join(distDirectory, 'docs', 'articles');
const articleRootPrefix = path.resolve(articleRoot) + path.sep;
const generatedHtml = [];
const generatedPagefindJson = [];
let pagefindFileCount = 0;
let pagefindBytes = 0;
const walkOutput = directory => {
  if (!fs.existsSync(directory)) return;
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) walkOutput(fullPath);
    else if (entry.isFile()) {
      if (entry.name.endsWith('.html')) generatedHtml.push(fullPath);
      if (fullPath.includes(`${path.sep}pagefind${path.sep}`)) {
        pagefindFileCount++;
        pagefindBytes += fs.statSync(fullPath).size;
        if (entry.name.endsWith('.json')) generatedPagefindJson.push(fullPath);
      }
    }
  }
};
walkOutput(distDirectory);
for (const file of generatedHtml) {
  const html = fs.readFileSync(file, 'utf8');
  if (path.resolve(file).startsWith(articleRootPrefix)) continue;
  assert.match(html, /<html\b[^>]*data-pagefind-ignore/i, `non-article HTML must stay out of Pagefind: ${file}`);
}
const generatedArticlePaths = generatedHtml
  .filter(file => path.resolve(file).startsWith(articleRootPrefix))
  .map(file => path.resolve(file)).sort();
assert.deepEqual(generatedArticlePaths, published.map(document =>
  path.resolve(articleRoot, `${document.slug}.html`)).sort(), 'only published articles may be rendered into the searchable article directory');
for (const file of generatedPagefindJson)
  scanForPublicSecrets(fs.readFileSync(file, 'utf8'), path.relative(distDirectory, file));
for (const document of published) {
  const url = `/docs/articles/${document.slug}.html`;
  const file = path.join(articleRoot, `${document.slug}.html`);
  assert.ok(fs.existsSync(file), `generated article is missing: ${url}`);
  const html = fs.readFileSync(file, 'utf8');
  assert.match(html, /<html lang="zh-CN">/);
  assert.ok(html.includes(`https://kidscode.fun${url}`), `${url}: canonical URL is missing`);
  assert.ok(html.includes('data-pagefind-body'), `${url}: searchable body is missing`);
  assert.ok(html.includes('data-pagefind-filter="category"'), `${url}: category filter is missing`);
  assert.ok(html.includes(`data-pagefind-meta="verifiedFor[content]" content="${document.verifiedFor}"`),
    `${url}: source-verified version metadata is missing from the Pagefind index`);
  scanForPublicSecrets(html, url);
}
assert.ok(fs.readFileSync(path.join(distDirectory, 'docs', 'index.html'), 'utf8').includes('data-pagefind-ignore'),
  'the /docs SPA shell must be excluded from search indexing');
console.log(`Pagefind static payload: ${pagefindFileCount} files, ${pagefindBytes} bytes (uncompressed build output).`);
console.log(`Documentation source and static output checks passed for ${published.length} articles and ${queryCases.length} search cases.`);

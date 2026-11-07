import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const websiteDirectory = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const distDirectory = path.join(websiteDirectory, 'dist');
const docsDirectory = path.join(distDirectory, 'docs');
// catalog.json is written next to the docs index, not at the dist root; checking the wrong path made
// this script refuse to run on a fully valid build, so the ranking evaluation could never start.
const requiredFiles = [
  path.join(distDirectory, 'index.html'),
  path.join(docsDirectory, 'index.html'),
  path.join(docsDirectory, 'catalog.json'),
  path.join(distDirectory, 'pagefind', 'pagefind.js')
];
for (const required of requiredFiles) {
  if (!fs.existsSync(required))
    throw new Error(`run the normal website build first; missing ${path.relative(websiteDirectory, required)}`);
}

const cases = JSON.parse(fs.readFileSync(path.join(websiteDirectory, 'tests', 'docs-search-queries.json'), 'utf8'));
const catalog = JSON.parse(fs.readFileSync(path.join(docsDirectory, 'catalog.json'), 'utf8'));
if (!Array.isArray(cases) || cases.length < 30 || !Array.isArray(catalog))
  throw new Error('search evaluation cases or generated article catalog are invalid');
const slugs = new Set(catalog.map(article => article.slug));
const ids = new Set();
for (const testCase of cases) {
  if (!testCase.id || ids.has(testCase.id) || typeof testCase.query !== 'string' ||
      !slugs.has(testCase.expectedSlug) || !Array.isArray(testCase.acceptableSlugs) ||
      !testCase.acceptableSlugs.every(slug => slugs.has(slug)) || !testCase.verifiedFor)
    throw new Error(`invalid search evaluation case: ${testCase.id || '(missing id)'}`);
  ids.add(testCase.id);
}

const page = `<!doctype html>
<html lang="zh-CN" data-pagefind-ignore>
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <meta name="robots" content="noindex, nofollow">
    <title>文档搜索排名评测（本地）</title>
    <style>
      :root { color-scheme: light; font: 16px/1.55 system-ui, sans-serif; color: #203744; background: #f8f6f0; }
      body { max-width: 1180px; margin: 32px auto; padding: 0 20px; }
      h1 { color: #132e46; font-size: clamp(28px, 4vw, 40px); line-height: 1.2; }
      .table-scroll { overflow-x: auto; margin-top: 22px; }
      table { width: 100%; min-width: 850px; border-collapse: collapse; background: #fffefb; }
      caption { padding: 8px 0; text-align: left; font-weight: 700; }
      th, td { padding: 10px 12px; border: 1px solid #d7d0c3; text-align: left; vertical-align: top; }
      th { color: #132e46; background: #f0ece2; }
      .pass { color: #12675e; font-weight: 700; }
      .fail, .error { color: #963c32; font-weight: 700; }
      #summary { padding: 14px 0; font-weight: 700; }
      #summary[data-state="fail"] { color: #963c32; }
      #summary[data-state="pass"] { color: #12675e; }
      #download-report { min-height: 40px; padding: 8px 14px; border: 1px solid #9cabb0; border-radius: 4px; color: #203744; background: #fffefb; font: inherit; cursor: pointer; }
      #download-report:focus-visible { outline: 2px solid #236b7b; outline-offset: 3px; }
      #download-report:disabled { color: #78878a; cursor: not-allowed; }
      #evaluation-frame { position: fixed; left: -10000px; top: 0; width: 1280px; height: 900px; border: 0; }
      @media (max-width: 700px) { body { margin: 18px auto; padding: 0 12px; } th, td { padding: 8px; } }
    </style>
    <script type="module" src="/docs/__search-evaluation.local.js"></script>
  </head>
  <body>
    <main>
      <h1>文档搜索排名评测</h1>
      <p>本地评测工具会在同源页面中运行查询；查询内容不会发送到外部服务。请只在本地构建预览中打开此页。</p>
      <p>逐题名次取标准结果或可接受结果中排得最靠前的一篇。平均倒数排名（MRR）按搜索页当次实际显示的结果列表计算（最多 24 篇）；目标文章未出现在列表中记为 0。它是诊断指标，不额外改变前 3 名通过门槛。</p>
      <p id="evaluation-status" role="status" aria-live="polite">正在准备评测…</p>
      <p id="evaluation-summary"></p>
      <p><button id="download-report" type="button" disabled>下载本地 JSON 评测记录</button></p>
      <div class="table-scroll" role="region" aria-label="逐条查询结果表" tabindex="0">
        <table>
          <caption>逐条查询结果</caption>
          <thead><tr><th scope="col">状态</th><th scope="col">问题</th><th scope="col">标准结果</th><th scope="col">实际前 3 名</th><th scope="col">认可结果显示名次</th><th scope="col">耗时</th></tr></thead>
          <tbody id="evaluation-results"></tbody>
        </table>
      </div>
    </main>
    <iframe id="evaluation-frame" title="文档搜索评测运行页" aria-hidden="true" tabindex="-1"></iframe>
  </body>
</html>
`;

fs.writeFileSync(path.join(docsDirectory, '__search-evaluation.local.html'), page, 'utf8');
fs.writeFileSync(path.join(docsDirectory, '__search-evaluation.local-cases.json'), `${JSON.stringify(cases)}\n`, 'utf8');
fs.copyFileSync(
  path.join(websiteDirectory, 'tests', 'docs-search-evaluation.js'),
  path.join(docsDirectory, '__search-evaluation.local.js')
);
console.log(`Prepared a local browser evaluator for ${cases.length} documentation search cases.`);

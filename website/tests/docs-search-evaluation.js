const status = document.querySelector('#evaluation-status');
const summary = document.querySelector('#evaluation-summary');
const results = document.querySelector('#evaluation-results');
const frame = document.querySelector('#evaluation-frame');
const reportButton = document.querySelector('#download-report');
let loadedCases = [];
let reportRows = [];
let reportPayload = null;

reportButton.addEventListener('click', () => {
  if (!reportPayload) return;
  const blob = new Blob([`${JSON.stringify(reportPayload, null, 2)}\n`], { type: 'application/json' });
  const downloadUrl = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = downloadUrl;
  link.download = `veyon-campus-doc-search-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
  document.body.append(link);
  link.click();
  link.remove();
  window.setTimeout(() => URL.revokeObjectURL(downloadUrl), 1000);
});

function enableReport(payload) {
  reportPayload = {
    format: 'veyon-campus-docs-search-evaluation',
    schemaVersion: 2,
    generatedAt: new Date().toISOString(),
    ...payload
  };
  reportButton.disabled = false;
}

const pause = milliseconds => new Promise(resolve => window.setTimeout(resolve, milliseconds));

async function waitFor(readValue, timeoutMs, description) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const value = readValue();
    if (value) return value;
    await pause(50);
  }
  throw new Error(`timed out waiting for ${description}`);
}

function appendCell(row, value, className = '') {
  const cell = document.createElement('td');
  cell.textContent = value;
  if (className) cell.className = className;
  row.append(cell);
}

function appendRow(testCase, state, actual, elapsedMs, detail = '', targetRank = null) {
  const row = document.createElement('tr');
  const className = state === '命中' ? 'pass' : state === '错误' ? 'error' : 'fail';
  appendCell(row, detail ? `${state}：${detail}` : state, className);
  appendCell(row, testCase.query);
  appendCell(row, [testCase.expectedSlug, ...(testCase.acceptableSlugs || [])].join(' / '));
  appendCell(row, actual.length ? actual.join(' → ') : '无');
  appendCell(row, targetRank ? `第 ${targetRank} 名` : state === '错误' ? '未完成' : '未出现在结果列表');
  appendCell(row, Number.isFinite(elapsedMs) ? `${Math.round(elapsedMs)} ms` : '—');
  results.append(row);
}

function getSearchStatus(doc) {
  return doc.querySelector('#docs-search-status')?.textContent.trim() || '';
}

function displayedResultSlugs(doc) {
  return [...doc.querySelectorAll('#docs-result-list a[href]')].map(link => {
    const url = new URL(link.href, doc.location.href);
    return url.pathname.match(/^\/docs\/articles\/([a-z0-9-]+)\.html$/)?.[1] || '';
  }).filter(Boolean);
}

function pagefindResources(frameWindow) {
  const resources = frameWindow.performance.getEntriesByType('resource').filter(entry => {
    try { return new URL(entry.name).pathname.startsWith('/pagefind/'); }
    catch { return false; }
  });
  const unique = new Map();
  for (const entry of resources) {
    const bytes = entry.encodedBodySize || entry.transferSize || 0;
    unique.set(entry.name, Math.max(unique.get(entry.name) || 0, bytes));
  }
  return { files: unique.size, bytes: [...unique.values()].reduce((sum, value) => sum + value, 0) };
}

function percentile(values, fraction) {
  if (!values.length) return 0;
  return [...values].sort((left, right) => left - right)[Math.ceil(values.length * fraction) - 1];
}

async function run() {
  try {
    reportButton.disabled = true;
    loadedCases = [];
    reportRows = [];
    reportPayload = null;
    const response = await fetch('/docs/__search-evaluation.local-cases.json', {
      cache: 'no-store', credentials: 'same-origin'
    });
    if (!response.ok) throw new Error('无法读取本地查询用例');
    const cases = await response.json();
    loadedCases = cases;

    const frameLoaded = new Promise((resolve, reject) => {
      const timeout = window.setTimeout(() => reject(new Error('文档搜索页加载超时')), 30000);
      frame.addEventListener('load', () => {
        window.clearTimeout(timeout);
        resolve();
      }, { once: true });
      frame.src = '/docs/';
    });
    await frameLoaded;
    const doc = frame.contentDocument;
    const initial = await waitFor(() => {
      const message = getSearchStatus(doc);
      if (message.includes('篇帮助文档') || message === '选择一个分类，或输入你遇到的问题。')
        return { ready: true, message };
      if (message.startsWith('暂时无法读取文档目录') || message.startsWith('帮助文档组件未能加载'))
        return { ready: false, message };
      return null;
    }, 20000, '文档目录');

    if (!initial.ready) {
      reportRows = cases.map(testCase => ({
        id: testCase.id,
        query: testCase.query,
        expectedSlug: testCase.expectedSlug,
        acceptableSlugs: testCase.acceptableSlugs || [],
        verifiedFor: testCase.verifiedFor,
        critical: Boolean(testCase.critical),
        top3: [],
        displayedResults: [],
        displayedResultCount: 0,
        bestAllowedRank: null,
        reciprocalRankDisplayed: 0,
        hit: false,
        status: 'error',
        elapsedMs: null,
        error: initial.message
      }));
      for (const testCase of cases) appendRow(testCase, '错误', [], NaN, initial.message);
      status.textContent = `评测未开始：${initial.message}`;
      summary.textContent = '请先修复文档首页或目录加载问题，再运行此评测。';
      summary.dataset.state = 'fail';
      enableReport({ status: 'not-run', caseCount: cases.length, metrics: null, cases: reportRows });
      return;
    }

    const queryInput = doc.querySelector('#docs-search-input');
    const searchForm = doc.querySelector('#docs-search-form');
    const elapsed = [];
    let passed = 0;
    let criticalPassed = 0;
    let criticalCount = 0;
    let reciprocalRankDisplayedSum = 0;
    let criticalReciprocalRankDisplayedSum = 0;
    let firstSearchBytes = { files: 0, bytes: 0 };

    for (let index = 0; index < cases.length; index++) {
      const testCase = cases[index];
      const started = performance.now();
      status.textContent = `正在评测 ${index + 1}/${cases.length}：${testCase.id}`;
      queryInput.value = testCase.query;
      searchForm.dispatchEvent(new frame.contentWindow.Event('submit', { bubbles: true, cancelable: true }));

      try {
        const message = await waitFor(() => {
          const value = getSearchStatus(doc);
          if (/^找到 \d+ 篇相关文档。$/.test(value) || value === '没有找到匹配的文章。' ||
              value.startsWith('搜索暂不可用') || value.startsWith('搜索结果不完整')) return value;
          return null;
        }, 20000, testCase.id);
        const displayedResults = displayedResultSlugs(doc);
        const actual = displayedResults.slice(0, 3);
        const allowed = new Set([testCase.expectedSlug, ...(testCase.acceptableSlugs || [])]);
        const rankIndex = displayedResults.findIndex(slug => allowed.has(slug));
        const targetRank = rankIndex < 0 ? null : rankIndex + 1;
        const reciprocalRank = targetRank ? 1 / targetRank : 0;
        const hit = actual.some(slug => allowed.has(slug));
        const isError = message.startsWith('搜索暂不可用') || message.startsWith('搜索结果不完整');
        const duration = performance.now() - started;
        elapsed.push(duration);
        if (index === 0) {
          try { firstSearchBytes = pagefindResources(frame.contentWindow); }
          catch { firstSearchBytes = { files: 0, bytes: 0 }; }
        }
        if (hit && !isError) passed++;
        if (!isError) reciprocalRankDisplayedSum += reciprocalRank;
        if (testCase.critical) {
          criticalCount++;
          if (hit && !isError) criticalPassed++;
          if (!isError) criticalReciprocalRankDisplayedSum += reciprocalRank;
        }
        appendRow(testCase, isError ? '错误' : hit ? '命中' : '未命中', actual, duration,
          isError ? message : '', isError ? null : targetRank);
        reportRows.push({
          id: testCase.id,
          query: testCase.query,
          expectedSlug: testCase.expectedSlug,
          acceptableSlugs: testCase.acceptableSlugs || [],
          verifiedFor: testCase.verifiedFor,
          critical: Boolean(testCase.critical),
          top3: actual,
          displayedResults,
          displayedResultCount: displayedResults.length,
          bestAllowedRank: isError ? null : targetRank,
          reciprocalRankDisplayed: isError ? 0 : reciprocalRank,
          hit: hit && !isError,
          status: isError ? 'error' : hit ? 'hit' : 'miss',
          elapsedMs: Math.round(duration),
          ...(isError ? { error: message } : {})
        });
      } catch (error) {
        const duration = performance.now() - started;
        elapsed.push(duration);
        if (testCase.critical) criticalCount++;
        const displayedResults = displayedResultSlugs(doc);
        const actual = displayedResults.slice(0, 3);
        appendRow(testCase, '错误', actual, duration, error.message);
        reportRows.push({
          id: testCase.id,
          query: testCase.query,
          expectedSlug: testCase.expectedSlug,
          acceptableSlugs: testCase.acceptableSlugs || [],
          verifiedFor: testCase.verifiedFor,
          critical: Boolean(testCase.critical),
          top3: actual,
          displayedResults,
          displayedResultCount: displayedResults.length,
          bestAllowedRank: null,
          reciprocalRankDisplayed: 0,
          hit: false,
          status: 'error',
          elapsedMs: Math.round(duration),
          error: error.message
        });
      }
    }

    const overallTarget = Math.ceil(cases.length * 0.9);
    const passedTarget = passed >= overallTarget;
    const criticalTarget = criticalCount > 0 && criticalPassed === criticalCount;
    const overallRate = (passed * 100 / cases.length).toFixed(1);
    const criticalRate = criticalCount ? (criticalPassed * 100 / criticalCount).toFixed(1) : '0.0';
    const meanReciprocalRankDisplayed = reciprocalRankDisplayedSum / cases.length;
    const criticalMeanReciprocalRankDisplayed = criticalCount
      ? criticalReciprocalRankDisplayedSum / criticalCount
      : 0;
    const meetsTarget = passedTarget && criticalTarget;
    const p95 = Math.round(percentile(elapsed, 0.95));
    const totalSearchResourcesKb = (firstSearchBytes.bytes / 1024).toFixed(1);
    summary.textContent = `整体前 3 名：${passed}/${cases.length}（${overallRate}%，目标至少 90%）；` +
      `关键问题：${criticalPassed}/${criticalCount}（${criticalRate}%，要求 100%）；` +
      `显示列表平均倒数排名 MRR：${meanReciprocalRankDisplayed.toFixed(3)}（关键问题 ${criticalMeanReciprocalRankDisplayed.toFixed(3)}；每题最多 24 篇）；` +
      `查询耗时 P95：${p95} ms；首次查询读取 Pagefind 资源约 ${totalSearchResourcesKb} KB / ${firstSearchBytes.files} 个文件。`;
    summary.dataset.state = meetsTarget ? 'pass' : 'fail';
    status.textContent = meetsTarget
      ? '评测完成，前 3 名命中率达到任务书目标；MRR 根据每题实际显示结果计算，仅用于比较排序变化。'
      : '评测完成，前 3 名命中率未达到任务书目标；请查看未命中项和目标排名。';
    enableReport({
      status: meetsTarget ? 'pass' : 'fail',
      caseCount: cases.length,
      metrics: {
        passed,
        overallHitRatePercent: Number(overallRate),
        overallTargetCases: overallTarget,
        overallTargetMet: passedTarget,
        criticalPassed,
        criticalCount,
        criticalHitRatePercent: Number(criticalRate),
        criticalTargetMet: criticalTarget,
        meanReciprocalRankDisplayed,
        criticalMeanReciprocalRankDisplayed,
        queryP95Ms: p95,
        firstQueryPagefindResources: firstSearchBytes
      },
      cases: reportRows
    });
  } catch (error) {
    status.textContent = `评测失败：${error.message}`;
    summary.textContent = '请确认已完成网站构建，并在本地预览中打开此评测页。';
    summary.dataset.state = 'fail';
    if (loadedCases.length > 0) {
      const recordedIds = new Set(reportRows.map(testCase => testCase.id));
      for (const testCase of loadedCases) {
        if (recordedIds.has(testCase.id)) continue;
        reportRows.push({
          id: testCase.id,
          query: testCase.query,
          expectedSlug: testCase.expectedSlug,
          acceptableSlugs: testCase.acceptableSlugs || [],
          verifiedFor: testCase.verifiedFor,
          critical: Boolean(testCase.critical),
          top3: [],
          displayedResults: [],
          displayedResultCount: 0,
          bestAllowedRank: null,
          reciprocalRankDisplayed: 0,
          hit: false,
          status: 'not-run',
          elapsedMs: null,
          error: error.message
        });
      }
      enableReport({ status: 'error', caseCount: loadedCases.length, metrics: null, cases: reportRows });
    }
  }
}

const localEvaluationHosts = new Set(['localhost', '127.0.0.1', '::1', '[::1]']);
if (localEvaluationHosts.has(window.location.hostname.toLowerCase())) {
  void run();
} else {
  reportButton.disabled = true;
  status.textContent = '此评测页仅在本机预览中运行。';
  summary.textContent = '请在 localhost 或 127.0.0.1 打开的本地预览中使用。';
}

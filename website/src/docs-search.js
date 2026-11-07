const CATEGORIES = Object.freeze({
  'getting-started': '快速开始',
  teacher: '教师端',
  student: '学生端',
  classroom: '课堂控制',
  devices: '设备与座位',
  maintenance: '维护与更新',
  faq: '常见问题',
  troubleshooting: '故障排查'
});
const VERIFICATION_STATUSES = new Set(['source-reviewed', 'source-reviewed-needs-field-test']);

const FEATURED = [
  'download-tools', 'student-offline', 'apply-website-policy', 'end-class',
  'student-update', 'print-seat-chart'
];
const DOCS_OPERATION_TIMEOUT_MS = 15000;
const SEARCH_RESULT_RANKS_PER_VARIANT = 12;
const MAX_DISPLAYED_SEARCH_RESULTS = 24;
const ARTICLE_SLUG_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const CJK_WORD_PATTERN = /^[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Hangul}]+$/u;
const CJK_RUN_PATTERN = /[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Hangul}]+/gu;
const QUERY_SEGMENTER = typeof Intl.Segmenter === 'function'
  ? new Intl.Segmenter('zh-CN', { granularity: 'word' })
  : null;
const PAGEFIND_RANKING = Object.freeze({
  metaWeights: Object.freeze({
    title: 6,
    errorCodes: 5,
    aliases: 4,
    keywords: 3,
    summary: 1.5,
    verifiedFor: 0.5,
    category: 0,
    lastReviewed: 0
  })
});
const DEFAULT_SEARCH_SUGGESTIONS = Object.freeze(['学生电脑离线', '学生电脑更新', '座位图']);
const PAGEFIND_ENTRY = '/pagefind/pagefind.js';
let pagefindApiPromise = null;
let activeDocsSearchCleanup = null;

const QUERY_ALIASES = Object.freeze([
  [['不在线', '离线', '掉线'], ['学生电脑 离线', '设备连接 状态']],
  [['找不到学生电脑', '教师机找不到学生机', '教师端连接不上', '教师端连不上', '连不上学生端', 'Veyon 本身没问题'], ['教师端 连接 学生电脑', 'Veyon 连接']],
  [['学生端怎么安装', '学生版安装', '学生电脑怎么装', '怎么装 Veyon Campus', '学生电脑装 Veyon'], ['学生端 部署工具', '学生电脑 安装']],
  [['学生端更新', '更新待确认', '更新一直转圈', '更新失败', '更新学生电脑', '学生电脑更新', '远程更新学生电脑'], ['学生端更新 待核对', '后台维护服务']],
  [['教师端更新', '教师软件更新', '教师控制台更新', '教师控制台本身怎么更新'], ['教师端 检查更新 下载', 'TeacherConsole 更新']],
  [['409'], ['HTTP 409 状态冲突']],
  [['怎么打印座位图', '怎么打印座位表', '座位表打印'], ['打印 座位表', '座位图']],
  [['学生名字', '学生姓名', '换座'], ['座位 姓名 UUID', '电脑编号 学生姓名']],
  [['开启网站限制', '网站黑名单', '网站白名单', '拉黑网站'], ['网站限制 策略 名单', '阻止名单 允许名单']],
  [['怎么结束课堂', '下课'], ['恢复网站访问 结束课堂', '课堂限制 解除']],
  [['推送的网页限制', '策略到点自动解除', '限制到点会自动解除'], ['下课 恢复网站策略', '课堂策略 自动解除 到期']],
  [['学生电脑后来开机', '课堂进行中开机', '上课时离线', '学生电脑重新上线'], ['课堂 离线 设备重新上线', '课堂策略 离线 重试']],
  [['卸载学生端', '学生端卸载', '学生电脑卸载', '学生端不让卸载', '全部卸载', '卸载失败', 'Maintenance 后台服务无法安全卸载'], ['学生端卸载 后台限制 清理', '管理员维护记录 服务状态 卸载']],
  [['维护服务', 'maintenance', 'agent'], ['后台维护服务 学生电脑', '学生更新']],
  [['电脑编号', 'PC-'], ['电脑编号 学生姓名 UUID', '座位图 Host']],
  [['新加学生电脑', '机房新加电脑', '机房里新加', '新增学生电脑'], ['机房 新增 学生电脑 UUID', '学生电脑 Host 地址 设备目录']],
  [['unins000.msg', '卸载消息文件', '卸载程序缺少文件'], ['卸载程序 文件缺失 官方安装包修复', 'Windows 卸载消息文件恢复']],
  [['多出电脑', '多出来', '多出设备', '虚空设备', '电脑数量不对', '设备数量不一致', '名单不一致'], ['课堂设备列表 目标地点 Veyon', '座位图 UUID Host 设备清单']],
  [['重复地点', '地点重名', '两个同名机房', '两个同名地点', '机房重名'], ['同名地点 设备目录 UUID', '重复机房 Veyon']],
  [['带 www', 'www', '网站地址', '域名是否带'], ['白名单 域名 www 子域名', '网站名单 域名匹配']]
]);

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, character => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  })[character]);
}

function decodePlainExcerpt(value) {
  const textarea = document.createElement('textarea');
  textarea.innerHTML = String(value ?? '').replace(/[<>]/g, character => character === '<' ? '&lt;' : '&gt;');
  return textarea.value;
}

function appendPagefindExcerpt(target, value) {
  const excerpt = String(value ?? '');
  if (!/<mark>/i.test(excerpt)) return false;

  const fragment = document.createDocumentFragment();
  let activeMark = null;
  for (const part of excerpt.split(/(<\/?mark>)/gi)) {
    const token = part.toLocaleLowerCase('en-US');
    if (token === '<mark>') {
      if (activeMark) {
        activeMark.append(document.createTextNode(part));
      } else {
        activeMark = document.createElement('mark');
        fragment.append(activeMark);
      }
      continue;
    }
    if (token === '</mark>' && activeMark) {
      activeMark = null;
      continue;
    }
    (activeMark || fragment).append(document.createTextNode(decodePlainExcerpt(part)));
  }
  target.append(fragment);
  return true;
}

function withTimeout(promise, message) {
  let timeout;
  const deadline = new Promise((_, reject) => {
    timeout = window.setTimeout(() => reject(new Error(message)), DOCS_OPERATION_TIMEOUT_MS);
  });
  return Promise.race([promise, deadline]).finally(() => window.clearTimeout(timeout));
}

function ensurePagefindApi() {
  if (!pagefindApiPromise) {
    const attempt = (async () => {
      // pagefind.js 由构建后的 `pagefind --site dist` 生成，构建期不存在；
      // 必须让打包器保持运行时导入，否则 Rollup 会因无法解析而中断构建。
      const pagefind = await import(/* @vite-ignore */ PAGEFIND_ENTRY);
      await pagefind.options({ ranking: PAGEFIND_RANKING });
      await pagefind.init();
      return pagefind;
    })();
    pagefindApiPromise = attempt;
    void attempt.catch(() => {
      if (pagefindApiPromise === attempt) pagefindApiPromise = null;
    });
  }
  return withTimeout(pagefindApiPromise, '文档搜索索引加载超时');
}

function buildExcerpt(text, query) {
  const fragment = document.createDocumentFragment();
  const phrase = String(query ?? '');
  const terms = new Set((phrase.match(/[\p{L}\p{N}]+/gu) || [])
    .filter(term => !CJK_WORD_PATTERN.test(term)));
  if (QUERY_SEGMENTER) {
    for (const part of QUERY_SEGMENTER.segment(phrase)) {
      if (part.isWordLike && part.segment.length >= 2) terms.add(part.segment);
    }
  } else {
    for (const run of phrase.match(CJK_RUN_PATTERN) || []) {
      const characters = Array.from(run);
      for (let length = 2; length <= Math.min(3, characters.length); length++) {
        for (let index = 0; index + length <= characters.length; index++)
          terms.add(characters.slice(index, index + length).join(''));
      }
    }
  }
  const normalizedText = text.toLocaleLowerCase('zh-CN');
  const matchingTerms = [...terms]
    .filter(term => (term.length > 1 || !CJK_WORD_PATTERN.test(term)) &&
      normalizedText.includes(term.toLocaleLowerCase('zh-CN')))
    .sort((left, right) => right.length - left.length)
    .slice(0, 8);
  if (matchingTerms.length === 0) {
    fragment.append(document.createTextNode(text));
    return fragment;
  }
  const expression = new RegExp(matchingTerms.map(term => term.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|'), 'giu');
  let cursor = 0;
  for (const match of text.matchAll(expression)) {
    if (match.index > cursor) fragment.append(document.createTextNode(text.slice(cursor, match.index)));
    const mark = document.createElement('mark');
    mark.textContent = match[0];
    fragment.append(mark);
    cursor = match.index + match[0].length;
  }
  if (cursor < text.length) fragment.append(document.createTextNode(text.slice(cursor)));
  return fragment;
}

function cardMarkup(article, variant = '') {
  return '<article class="docs-result-card ' + variant + '">' +
    '<div class="docs-result-meta"><span>' + escapeHtml(article.categoryLabel || CATEGORIES[article.category] || '帮助文档') + '</span>' +
    '<span>' + escapeHtml(verificationText(article)) + '</span></div>' +
    '<h3><a href="' + escapeHtml(article.url) + '">' + escapeHtml(article.title) + '</a></h3>' +
    '<p>' + escapeHtml(article.summary) + '</p></article>';
}

function verificationText(article) {
  if (!article?.verifiedFor) return '';
  const label = `源码核对 ${article.verifiedFor}`;
  return article.verificationStatus === 'source-reviewed-needs-field-test'
    ? `${label} · 需现场验证`
    : label;
}

function categoryCounts(articles) {
  const counts = new Map();
  for (const article of articles) counts.set(article.category, (counts.get(article.category) || 0) + 1);
  return counts;
}

function renderCategories(root, articles, selectedCategory) {
  const grid = root.querySelector('#docs-category-grid');
  const filter = root.querySelector('#docs-category-filter');
  const counts = categoryCounts(articles);
  const categories = Object.entries(CATEGORIES).filter(([key]) => counts.has(key));
  filter.innerHTML = '<option value="">全部分类</option>' + categories.map(([key, label]) =>
    '<option value="' + escapeHtml(key) + '"' + (selectedCategory === key ? ' selected' : '') + '>' + escapeHtml(label) + '</option>'
  ).join('');
  grid.innerHTML = categories.map(([key, label]) =>
    '<button type="button" class="docs-category-link' + (selectedCategory === key ? ' active' : '') + '" aria-pressed="' + (selectedCategory === key ? 'true' : 'false') + '" data-doc-category="' + escapeHtml(key) + '">' +
    '<span>' + escapeHtml(label) + '</span><small>' + counts.get(key) + ' 篇</small></button>'
  ).join('');
}

function renderFeatured(root, articles, category) {
  const selected = category
    ? articles.filter(article => article.category === category)
    : articles.filter(article => FEATURED.includes(article.slug));
  root.querySelector('#docs-featured-grid').innerHTML = selected.map(article => cardMarkup(article, 'featured')).join('');
}

function normalizeQueryPhrase(value) {
  return String(value ?? '').normalize('NFKC').toLocaleLowerCase('zh-CN').replace(/[\p{P}\p{S}\p{Z}\s]+/gu, '');
}

// Pagefind matches space-separated terms with AND semantics, so a full Chinese question only returns
// pages that contain every clause of it. Measured against the built index, "安装" returned 16 pages
// while "学生电脑安装之前要准备什么？" returned none. Dropping the interrogative scaffolding and
// re-joining the remaining words restores recall without touching the curated alias rules.
const QUERY_STOP_WORDS = new Set([
  '怎么办', '怎么处理', '怎么解决', '怎么回事', '怎么样', '怎么', '怎样', '如何', '为什么',
  '什么原因', '什么时候', '什么', '哪里', '哪个', '哪些', '是不是', '是否', '能不能',
  '可不可以', '可以吗', '能吗', '要不要', '有没有', '还会', '还能', '还有', '请问', '麻烦',
  '帮我', '我想', '我要', '需要', '应该', '一般', '通常', '之前', '以后', '的时候', '时候',
  '现在', '已经', '一直', '总是', '还是', '这个', '那个', '这些', '那些', '一下', '一个',
  // Single-character function words only ever appear here as their own segment, so dropping them
  // cannot break a domain term such as 在线 or 不要.
  '要', '会', '能', '就', '都', '也', '还', '又', '很', '太', '把', '被', '给', '让', '对',
  '在', '和', '与', '或', '是',
  '的', '了', '吗', '呢', '吧', '啊', '呀', '哦', '嘛', '么'
]);

function segmentQueryWords(value) {
  const text = String(value ?? '').normalize('NFKC');
  if (!QUERY_SEGMENTER)
    return text.split(/[\p{P}\p{S}\p{Z}\s]+/u).filter(Boolean);
  const words = [];
  for (const part of QUERY_SEGMENTER.segment(text)) {
    const word = part.segment.trim();
    if (word && !/^[\p{P}\p{S}\p{Z}\s]+$/u.test(word)) words.push(word);
  }
  return words;
}

/**
 * Returns a shorter, space-separated form of the question with the interrogative scaffolding removed,
 * or an empty string when nothing would be gained. Pagefind keeps AND semantics, so fewer terms means
 * more pages qualify; the caller still runs the exact query first and merges by rank.
 */
export function relaxQuery(query) {
  const trimmed = String(query ?? '').trim();
  if (!trimmed) return '';
  const words = segmentQueryWords(trimmed);
  const kept = words.filter(word => !QUERY_STOP_WORDS.has(word.toLocaleLowerCase('zh-CN')));
  // Nothing was scaffolding, so there is nothing to relax: re-spacing a keyword-only query would only
  // spend one of the limited search variants on an identical index lookup.
  if (kept.length === words.length || kept.length === 0) return '';
  const relaxed = kept.join(' ').trim();
  if (!relaxed || relaxed === trimmed) return '';
  return normalizeQueryPhrase(relaxed).length >= 2 ? relaxed : '';
}

function queryContainsSearchNeedle(query, needle) {
  const normalizedNeedle = normalizeQueryPhrase(needle);
  if (!normalizedNeedle) return false;
  if (!/^[a-z0-9]+$/i.test(normalizedNeedle))
    return normalizeQueryPhrase(query).includes(normalizedNeedle);
  const queryTokens = String(query).normalize('NFKC').toLocaleLowerCase('zh-CN').match(/[a-z0-9]+/g) || [];
  const needleTokens = String(needle).normalize('NFKC').toLocaleLowerCase('zh-CN').match(/[a-z0-9]+/g) || [];
  return needleTokens.length > 0 && queryTokens.some((_, start) =>
    needleTokens.every((token, offset) => queryTokens[start + offset] === token));
}

function readSearchStateFromUrl(url) {
  const prefix = '#search?';
  const params = url.hash.startsWith(prefix)
    ? new URLSearchParams(url.hash.slice(prefix.length))
    : url.searchParams;
  return {
    query: params.get('q') || '',
    category: params.get('category') || ''
  };
}

export function queryVariants(query, articles = []) {
  const trimmed = query.trim();
  const variants = [trimmed];
  // Second, before the curated aliases: this is the same intent expressed with fewer AND terms, so it
  // is the cheapest way to stop a long question from returning nothing at all.
  const relaxed = relaxQuery(trimmed);
  if (relaxed) variants.push(relaxed);
  for (const article of articles) {
    const aliases = Array.isArray(article.aliases) ? article.aliases : [];
    const errorCodes = Array.isArray(article.errorCodes) ? article.errorCodes : [];
    const matchesAlias = aliases.some(alias => normalizeQueryPhrase(alias).length >= 2 &&
      queryContainsSearchNeedle(query, alias));
    const matchesErrorCode = errorCodes.some(errorCode => queryContainsSearchNeedle(query, errorCode));
    if (matchesAlias || matchesErrorCode) variants.push(article.title);
    if (variants.length >= 4) break;
  }
  for (const [needles, alternatives] of QUERY_ALIASES) {
    if (needles.some(needle => queryContainsSearchNeedle(query, needle))) {
      variants.push(...alternatives);
      if (variants.length >= 4) break;
    }
  }
  return [...new Set(variants.filter(Boolean))].slice(0, 4);
}

function renderEmptySuggestions(root, variants) {
  const container = root.querySelector('.docs-empty-suggestions');
  if (!container) return;
  const suggestions = variants.length > 1
    ? variants.slice(1, 4)
    : DEFAULT_SEARCH_SUGGESTIONS;
  container.replaceChildren(...suggestions.map(query => {
    const button = document.createElement('button');
    button.type = 'button';
    button.dataset.docQuery = query;
    // The relaxed variant is space-separated so Pagefind sees separate terms; a teacher should still
    // read it as one phrase, so spaces between Chinese characters are dropped for display only.
    button.textContent = suggestionLabel(query);
    return button;
  }));
}

function suggestionLabel(query) {
  const parts = String(query ?? '').split(/\s+/).filter(Boolean);
  let label = '';
  for (const part of parts) {
    if (label && /[\p{Script=Han}]$/u.test(label) && /^[\p{Script=Han}]/u.test(part)) label += part;
    else label += (label ? ' ' : '') + part;
  }
  return label || String(query ?? '');
}

function validArticleUrl(value, slugs) {
  if (typeof value !== 'string') return null;
  const match = value.match(/^\/docs\/articles\/([a-z0-9-]+)\.html(?:#[\p{L}\p{N}-]+)?$/u);
  return match && slugs.has(match[1]) ? value : null;
}

/**
 * Recall floor over the article catalog. Pagefind matches space-separated terms with AND, so a question
 * whose exact wording never occurs inside a single article returns nothing at all even when an obviously
 * relevant article exists. This scorer only runs when every Pagefind variant came back empty, so it can
 * never displace a real full-text hit; it exists so "no results" means the corpus truly has nothing.
 */
export function catalogFallbackResults(query, articles) {
  const words = segmentQueryWords(query)
    .map(word => normalizeQueryPhrase(word))
    .filter(word => word.length > 0 && !QUERY_STOP_WORDS.has(word));
  if (words.length === 0) return [];
  const scored = [];
  for (const article of articles) {
    const title = normalizeQueryPhrase(article.title);
    const errorCodes = normalizeQueryPhrase((article.errorCodes || []).join(' '));
    const aliases = normalizeQueryPhrase((article.aliases || []).join(' '));
    const keywords = normalizeQueryPhrase((article.keywords || []).join(' '));
    const summary = normalizeQueryPhrase(article.summary || '');
    let score = 0;
    for (const word of words) {
      if (title.includes(word)) score += 6;
      else if (errorCodes.includes(word)) score += 5;
      else if (aliases.includes(word)) score += 4;
      else if (keywords.includes(word)) score += 3;
      else if (summary.includes(word)) score += 1.5;
    }
    if (score > 0) scored.push({ article, score });
  }
  scored.sort((left, right) => right.score - left.score ||
    String(left.article.title).localeCompare(String(right.article.title), 'zh-CN'));
  return scored.slice(0, 12).map(({ article }) => ({
    data: { url: article.url, plain_excerpt: article.summary || '' },
    matchedQuery: query
  }));
}

export function mergeSearchResults(resultLists, articleSlugs, articles = [], maximum = 24) {
  const collected = [];
  const seen = new Set();
  for (let rank = 0; rank < maximum && collected.length < maximum; rank++) {
    for (const resultList of resultLists) {
      if (collected.length >= maximum) break;
      const result = resultList[rank];
      if (!result) continue;
      const data = result.data;
      const pageUrl = validArticleUrl(data.url, articleSlugs);
      if (!pageUrl) continue;
      const articleUrl = pageUrl.split('#', 1)[0];
      const bestSection = Array.isArray(data.sub_results)
        ? data.sub_results.find(section => {
          const sectionUrl = validArticleUrl(section?.url, articleSlugs);
          return sectionUrl && sectionUrl.split('#', 1)[0] === articleUrl;
        })
        : null;
      const sectionUrl = validArticleUrl(bestSection?.url, articleSlugs);
      if (seen.has(articleUrl)) continue;
      seen.add(articleUrl);
      const catalogArticle = articles.find(article => article.url === articleUrl);
      const highlightedExcerpt = [bestSection?.excerpt, data.excerpt]
        .find(excerpt => typeof excerpt === 'string' && /<mark>/i.test(excerpt)) || '';
      collected.push({
        url: sectionUrl || pageUrl,
        title: data.meta?.title || catalogArticle?.title || '帮助文档',
        summary: data.meta?.summary || catalogArticle?.summary || '',
        categoryLabel: data.meta?.category || catalogArticle?.categoryLabel ||
          CATEGORIES[catalogArticle?.category] || '',
        verifiedFor: data.meta?.verifiedFor || catalogArticle?.verifiedFor || '',
        plainExcerpt: bestSection?.plain_excerpt || bestSection?.excerpt || data.plain_excerpt || data.excerpt || '',
        highlightedExcerpt,
        matchedQuery: result.matchedQuery
      });
    }
  }
  return collected;
}

export function unmountDocsSearch() {
  const cleanup = activeDocsSearchCleanup;
  activeDocsSearchCleanup = null;
  cleanup?.();
}

export function mountDocsSearch(root) {
  if (!root) return;
  const form = root.querySelector('#docs-search-form');
  const input = root.querySelector('#docs-search-input');
  const status = root.querySelector('#docs-search-status');
  const failureHelp = root.querySelector('#docs-search-failure-help');
  const staticFallback = root.querySelector('#docs-static-fallback');
  const results = root.querySelector('#docs-result-list');
  const featured = root.querySelector('#docs-featured-grid');
  const categoryGrid = root.querySelector('#docs-category-grid');
  const empty = root.querySelector('#docs-empty');
  const filter = root.querySelector('#docs-category-filter');
  if (!form || form.dataset.docsSearchMounted === 'true' || !input || !status || !results ||
      !featured || !categoryGrid || !empty || !filter) return;
  unmountDocsSearch();
  form.dataset.docsSearchMounted = 'true';

  const catalogRetry = document.createElement('button');
  catalogRetry.type = 'button';
  catalogRetry.className = 'btn secondary docs-catalog-retry';
  catalogRetry.textContent = '重试读取目录';
  catalogRetry.hidden = true;
  status.after(catalogRetry);

  let articles = [];
  let requestNumber = 0;
  let catalogRequestNumber = 0;
  let catalogAbortController = null;
  let timer = 0;
  let currentQuery = '';
  let inputHistoryEntryOpen = false;
  let selectedCategory = '';
  let articleSlugs = new Set();
  const initialUrl = new URL(window.location.href);
  const initialSearchState = readSearchStateFromUrl(initialUrl);
  input.value = initialSearchState.query.slice(0, 160);
  const initialCategory = initialSearchState.category;
  if (Object.hasOwn(CATEGORIES, initialCategory)) selectedCategory = initialCategory;

  const syncSearchUrl = ({ push = false } = {}) => {
    const url = new URL(window.location.href);
    const query = input.value.trim().slice(0, 160);
    url.searchParams.delete('q');
    url.searchParams.delete('category');
    const params = new URLSearchParams();
    if (query) params.set('q', query);
    if (selectedCategory) params.set('category', selectedCategory);
    if (query || selectedCategory) url.hash = `search?${params.toString()}`;
    else if (url.hash.startsWith('#search?')) url.hash = '';
    const nextLocation = `${url.pathname}${url.search}${url.hash}`;
    const currentLocation = `${window.location.pathname}${window.location.search}${window.location.hash}`;
    if (nextLocation === currentLocation) return false;
    const updateHistory = push ? window.history.pushState : window.history.replaceState;
    updateHistory.call(window.history, window.history.state, '', nextLocation);
    return true;
  };
  syncSearchUrl();

  const showInitial = () => {
    results.replaceChildren();
    results.setAttribute('aria-busy', 'false');
    empty.hidden = true;
    if (failureHelp) failureHelp.hidden = true;
    if (articles.length === 0) return;
    status.textContent = '选择一个分类，或输入你遇到的问题。';
    renderCategories(root, articles, selectedCategory);
    renderFeatured(root, articles, selectedCategory);
  };

  const executeSearch = async query => {
    currentQuery = query.trim().slice(0, 160);
    const request = ++requestNumber;
    if (!currentQuery) { showInitial(); return; }
    if (articles.length === 0) return;
    status.textContent = '正在搜索本地文档索引…';
    if (failureHelp) failureHelp.hidden = true;
    results.setAttribute('aria-busy', 'true');
    results.replaceChildren();
    featured.replaceChildren();
    categoryGrid.replaceChildren();
    empty.hidden = true;
    try {
      const pagefind = await ensurePagefindApi();
      if (request !== requestNumber) return;
      const filters = selectedCategory ? { category: CATEGORIES[selectedCategory] } : undefined;
      const searchableArticles = selectedCategory
        ? articles.filter(article => article.category === selectedCategory)
        : articles;
      const variants = queryVariants(currentQuery, searchableArticles);
      let searchFailures = 0;
      const searchAttempts = await withTimeout(
        Promise.all(variants.map(async variant => {
          try {
            return {
              variant,
              search: await pagefind.search(variant, filters ? { filters } : {})
            };
          } catch {
            searchFailures++;
            return null;
          }
        })),
        '文档搜索超时');
      if (request !== requestNumber) return;
      const searches = searchAttempts.filter(Boolean);
      if (searches.length === 0) {
        status.textContent = '搜索暂不可用；仍可使用下面的分类和文章目录。';
        if (failureHelp) failureHelp.hidden = false;
        renderCategories(root, articles, selectedCategory);
        renderFeatured(root, articles, selectedCategory);
        return;
      }
      const resultLists = await withTimeout((async () => {
        const lists = searches.map(() => []);
        for (let rank = 0; rank < SEARCH_RESULT_RANKS_PER_VARIANT; rank++) {
          if (request !== requestNumber) break;
          const rankedResults = await Promise.all(searches.map(async ({ search, variant }) => {
            const result = search.results[rank];
            if (!result) return null;
            try {
              return {
                data: await result.data(),
                matchedQuery: variant
              };
            } catch {
              searchFailures++;
              return null;
            }
          }));
          if (request !== requestNumber) break;
          rankedResults.forEach((result, index) => lists[index].push(result));
          if (mergeSearchResults(lists, articleSlugs, articles, MAX_DISPLAYED_SEARCH_RESULTS).length >=
              MAX_DISPLAYED_SEARCH_RESULTS) break;
        }
        return lists;
      })(), '搜索结果读取超时');
      if (request !== requestNumber) return;
      // Only when the full-text index produced nothing: a zero-result search is almost always a
      // wording mismatch rather than a missing article, and the catalog knows every published title.
      // The rank loops push null placeholders, so presence of a real hit must be tested per entry.
      if (!resultLists.some(list => list.some(Boolean))) {
        const fallback = catalogFallbackResults(currentQuery, searchableArticles);
        if (fallback.length > 0) resultLists.push(fallback);
      }
      const collected = mergeSearchResults(resultLists, articleSlugs, articles, MAX_DISPLAYED_SEARCH_RESULTS);
      if (request !== requestNumber) return;
      if (collected.length === 0) {
        if (searchFailures > 0) {
          status.textContent = '搜索暂不可用；仍可使用下面的分类和文章目录。';
          if (failureHelp) failureHelp.hidden = false;
          renderCategories(root, articles, selectedCategory);
          renderFeatured(root, articles, selectedCategory);
          return;
        }
        status.textContent = '没有找到匹配的文章。';
        renderEmptySuggestions(root, variants);
        empty.hidden = false;
        return;
      }
      status.textContent = searchFailures > 0
        ? `搜索结果不完整：已显示 ${collected.length} 篇相关文档，部分结果暂不可用。`
        : `找到 ${collected.length} 篇相关文档。`;
      if (failureHelp) failureHelp.hidden = searchFailures === 0;
      for (const article of collected) {
        const card = document.createElement('article');
        card.className = 'docs-result-card';
        const heading = document.createElement('h3');
        const link = document.createElement('a');
        link.href = article.url;
        link.textContent = article.title;
        heading.append(link);
        const meta = document.createElement('div');
        meta.className = 'docs-result-meta';
        const category = document.createElement('span');
        category.textContent = article.categoryLabel;
        const version = document.createElement('span');
        const catalogArticle = articles.find(candidate => candidate.url === article.url.split('#', 1)[0]);
        version.textContent = verificationText(catalogArticle || article);
        meta.append(category, version);
        const summary = document.createElement('p');
        summary.textContent = article.summary;
        const excerpt = document.createElement('p');
        excerpt.className = 'docs-result-excerpt';
        if (!appendPagefindExcerpt(excerpt, article.highlightedExcerpt)) {
          excerpt.append(buildExcerpt(decodePlainExcerpt(article.plainExcerpt), article.matchedQuery));
        }
        card.append(meta, heading, summary, excerpt);
        results.append(card);
      }
    } catch {
      if (request !== requestNumber) return;
      requestNumber++;
      results.setAttribute('aria-busy', 'false');
      status.textContent = '搜索暂不可用；仍可使用下面的分类和文章目录。';
      if (failureHelp) failureHelp.hidden = false;
      empty.hidden = true;
      renderCategories(root, articles, selectedCategory);
      renderFeatured(root, articles, selectedCategory);
    } finally {
      if (request === requestNumber) results.setAttribute('aria-busy', 'false');
    }
  };

  const scheduleSearch = query => {
    clearTimeout(timer);
    requestNumber++;
    if (articles.length > 0) results.setAttribute('aria-busy', 'true');
    timer = window.setTimeout(() => {
      void executeSearch(query);
    }, 220);
  };

  form.addEventListener('submit', event => {
    event.preventDefault();
    clearTimeout(timer);
    syncSearchUrl({ push: true });
    inputHistoryEntryOpen = false;
    void executeSearch(input.value);
  });
  input.addEventListener('input', () => {
    if (syncSearchUrl({ push: !inputHistoryEntryOpen })) inputHistoryEntryOpen = true;
    scheduleSearch(input.value);
  });
  input.addEventListener('blur', () => { inputHistoryEntryOpen = false; });
  input.addEventListener('focus', () => { void ensurePagefindApi().catch(() => {}); }, { once: true });
  input.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
      input.value = '';
      clearTimeout(timer);
      currentQuery = '';
      requestNumber++;
      showInitial();
      syncSearchUrl({ push: true });
      inputHistoryEntryOpen = false;
    }
  });
  filter.addEventListener('change', () => {
    clearTimeout(timer);
    selectedCategory = filter.value;
    syncSearchUrl({ push: true });
    inputHistoryEntryOpen = false;
    const query = input.value.trim();
    if (query) void executeSearch(query);
    else {
      currentQuery = '';
      requestNumber++;
      showInitial();
    }
  });
  root.addEventListener('click', event => {
    const queryButton = event.target.closest('[data-doc-query]');
    if (queryButton) {
      clearTimeout(timer);
      if (queryButton.closest('.docs-empty-suggestions')) {
        selectedCategory = '';
        filter.value = '';
      }
      input.value = queryButton.dataset.docQuery || '';
      syncSearchUrl({ push: true });
      inputHistoryEntryOpen = false;
      void executeSearch(input.value);
      input.focus();
      return;
    }
    const categoryButton = event.target.closest('[data-doc-category]');
    if (categoryButton) {
      clearTimeout(timer);
      selectedCategory = categoryButton.dataset.docCategory || '';
      filter.value = selectedCategory;
      syncSearchUrl({ push: true });
      inputHistoryEntryOpen = false;
      const query = input.value.trim();
      if (query) void executeSearch(query);
      else {
        currentQuery = '';
        requestNumber++;
        showInitial();
      }
    }
  });

  const handlePopState = () => {
    if (!form.isConnected) {
      unmountDocsSearch();
      return;
    }
    clearTimeout(timer);
    const url = new URL(window.location.href);
    const searchState = readSearchStateFromUrl(url);
    input.value = searchState.query.slice(0, 160);
    const category = searchState.category;
    selectedCategory = Object.hasOwn(CATEGORIES, category) ? category : '';
    filter.value = selectedCategory;
    inputHistoryEntryOpen = false;
    syncSearchUrl();
    if (input.value.trim()) void executeSearch(input.value);
    else {
      currentQuery = '';
      requestNumber++;
      showInitial();
    }
  };
  window.addEventListener('popstate', handlePopState);

  activeDocsSearchCleanup = () => {
    clearTimeout(timer);
    requestNumber++;
    catalogRequestNumber++;
    catalogAbortController?.abort();
    catalogAbortController = null;
    window.removeEventListener('popstate', handlePopState);
    delete form.dataset.docsSearchMounted;
  };

  const loadCatalog = async () => {
    const request = ++catalogRequestNumber;
    const retrying = !catalogRetry.hidden;
    const restoreFocus = document.activeElement === catalogRetry;
    const controller = new AbortController();
    catalogAbortController?.abort();
    catalogAbortController = controller;
    const timeout = window.setTimeout(() => controller.abort(), DOCS_OPERATION_TIMEOUT_MS);
    requestNumber++;
    catalogRetry.hidden = !retrying;
    catalogRetry.disabled = retrying;
    if (retrying) catalogRetry.textContent = '正在重试…';
    if (failureHelp) failureHelp.hidden = true;
    status.textContent = '正在读取文档目录…';
    try {
      const response = await fetch('/docs/catalog.json', {
        cache: 'no-cache', credentials: 'same-origin', signal: controller.signal
      });
      if (!response.ok) throw new Error('document catalog unavailable');
      const data = await response.json();
      if (!Array.isArray(data) || data.length < 25 || data.length > 40 ||
        data.some(article => !article || typeof article !== 'object' || Array.isArray(article) ||
          typeof article.slug !== 'string' || article.slug.length > 64 || !ARTICLE_SLUG_PATTERN.test(article.slug) ||
          typeof article.title !== 'string' || article.title.length > 100 ||
          typeof article.summary !== 'string' || article.summary.length > 220 ||
          typeof article.categoryLabel !== 'string' || article.categoryLabel.length > 32 ||
          typeof article.verifiedFor !== 'string' || article.verifiedFor.length > 32 ||
          typeof article.verificationStatus !== 'string' || !VERIFICATION_STATUSES.has(article.verificationStatus) ||
          (article.aliases !== undefined && (!Array.isArray(article.aliases) || article.aliases.length > 24 ||
            article.aliases.some(alias => typeof alias !== 'string' || alias.length === 0 || alias.length > 120))) ||
          (article.errorCodes !== undefined && (!Array.isArray(article.errorCodes) || article.errorCodes.length > 12 ||
            article.errorCodes.some(errorCode => typeof errorCode !== 'string' || errorCode.length === 0 || errorCode.length > 48))) ||
          !Object.hasOwn(CATEGORIES, article.category) || article.url !== `/docs/articles/${article.slug}.html`) ||
        new Set(data.map(article => article.slug)).size !== data.length)
        throw new Error('document catalog is invalid');
      if (request !== catalogRequestNumber) return;
      articles = data;
      articleSlugs = new Set(articles.map(article => article.slug));
      if (staticFallback) staticFallback.hidden = true;
      showInitial();
      status.textContent = `${articles.length} 篇帮助文档，可搜索或按分类浏览。`;
      catalogRetry.hidden = true;
      if (restoreFocus) input.focus();
      if (input.value.trim()) void executeSearch(input.value);
    } catch {
      if (request !== catalogRequestNumber) return;
      articles = [];
      articleSlugs = new Set();
      status.textContent = '暂时无法读取文档目录。检查网络后可重试，或通过问题反馈联系项目维护者。';
      if (failureHelp) failureHelp.hidden = false;
      if (staticFallback) staticFallback.hidden = false;
      catalogRetry.textContent = '重试读取目录';
      catalogRetry.hidden = false;
    } finally {
      window.clearTimeout(timeout);
      if (catalogAbortController === controller) catalogAbortController = null;
      if (request === catalogRequestNumber) catalogRetry.disabled = false;
    }
  };

  catalogRetry.addEventListener('click', () => { void loadCatalog(); });
  void loadCatalog();
}

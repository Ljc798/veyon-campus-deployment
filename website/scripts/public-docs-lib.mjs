import fs from 'node:fs';
import { isIP } from 'node:net';
import path from 'node:path';

export const CATEGORIES = Object.freeze({
  'getting-started': '快速开始',
  teacher: '教师端',
  student: '学生端',
  classroom: '课堂控制',
  devices: '设备与座位',
  maintenance: '维护与更新',
  faq: '常见问题',
  troubleshooting: '故障排查'
});

const ALLOWED_FIELDS = new Set([
  'id', 'slug', 'title', 'summary', 'category', 'audience', 'keywords', 'aliases', 'errorCodes', 'verifiedFor',
  'verificationStatus', 'lastReviewed', 'sourceRefs', 'related', 'status'
]);
const SLUG = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const AUDIENCES = new Set(['teacher', 'student', 'school-it']);
const STATUSES = new Set(['draft', 'published', 'obsolete']);
const VERIFICATION = new Set(['source-reviewed', 'source-reviewed-needs-field-test']);
const DOCS_ROUTE_ANCHORS = new Set([
  'before', 'download', 'teacher', 'student', 'verify', 'help',
  'main-content', 'docs-search-form', 'docs-search-input', 'docs-search-hint',
  'docs-popular-title', 'docs-search-results', 'docs-results-title', 'docs-category-filter',
  'docs-search-status', 'docs-category-grid', 'docs-featured-grid', 'docs-result-list', 'docs-empty'
]);
const INTERNAL_ROUTE_ANCHORS = new Map([
  ['/docs', DOCS_ROUTE_ANCHORS],
  ['/download', new Set(['main-content', 'github-downloads', 'github-downloads-title', 'gitcode-downloads', 'gitcode-downloads-title'])],
  ['/contact', new Set(['main-content'])]
]);
const MAX_SOURCE_FILES = 80;
const MAX_ARTICLE_BYTES = 96 * 1024;
const MAX_TOTAL_SOURCE_BYTES = 2 * 1024 * 1024;

export function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, character => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  })[character]);
}

function parseValue(raw, lineNumber) {
  const value = raw.trim();
  if (value.startsWith('[') || value.startsWith('"')) {
    try { return JSON.parse(value); }
    catch { throw new Error(`frontmatter line ${lineNumber}: expected a JSON string or array`); }
  }
  return value;
}

export function parsePublicDocument(source, sourcePath = '<memory>') {
  if (Buffer.byteLength(source, 'utf8') > MAX_ARTICLE_BYTES)
    throw new Error(`${sourcePath}: article exceeds ${MAX_ARTICLE_BYTES} bytes`);
  const normalized = source.replace(/^\uFEFF/, '').replace(/\r\n/g, '\n');
  if (!normalized.startsWith('---\n')) throw new Error(`${sourcePath}: frontmatter must start with ---`);
  const close = normalized.indexOf('\n---\n', 4);
  if (close < 0) throw new Error(`${sourcePath}: closing frontmatter delimiter is missing`);
  const fields = {};
  const lines = normalized.slice(4, close).split('\n');
  lines.forEach((line, index) => {
    if (!line.trim()) return;
    const match = line.match(/^([A-Za-z][A-Za-z0-9]*):\s*(.*?)\s*$/);
    if (!match) throw new Error(`${sourcePath}: invalid frontmatter at line ${index + 2}`);
    const [, key, raw] = match;
    if (!ALLOWED_FIELDS.has(key)) throw new Error(`${sourcePath}: frontmatter field '${key}' is not allowed`);
    if (Object.hasOwn(fields, key)) throw new Error(`${sourcePath}: duplicate frontmatter field '${key}'`);
    fields[key] = parseValue(raw, index + 2);
  });

  const requireString = (key, maximum = 180) => {
    const value = fields[key];
    if (typeof value !== 'string' || value.length === 0 || value.length > maximum)
      throw new Error(`${sourcePath}: '${key}' must be a non-empty string of at most ${maximum} characters`);
    return value;
  };
  const requireStringArray = (key, maximumCount, maximumLength = 120) => {
    const value = fields[key];
    if (!Array.isArray(value) || value.length > maximumCount ||
        value.some(item => typeof item !== 'string' || item.length === 0 || item.length > maximumLength))
      throw new Error(`${sourcePath}: '${key}' must be an array of at most ${maximumCount} short strings`);
    return value;
  };

  requireString('id', 64);
  const slug = requireString('slug', 64);
  if (!SLUG.test(slug) || fields.id !== slug) throw new Error(`${sourcePath}: id and slug must be the same URL-safe slug`);
  requireString('title', 100);
  requireString('summary', 220);
  if (!Object.hasOwn(CATEGORIES, fields.category)) throw new Error(`${sourcePath}: unknown category`);
  const audience = requireStringArray('audience', 3, 24);
  if (audience.length === 0 || audience.some(item => !AUDIENCES.has(item))) throw new Error(`${sourcePath}: unknown audience`);
  const keywords = requireStringArray('keywords', 24, 80);
  const aliases = fields.aliases === undefined ? [] : requireStringArray('aliases', 24, 120);
  const errorCodes = fields.errorCodes === undefined ? [] : requireStringArray('errorCodes', 12, 48);
  const sourceRefs = requireStringArray('sourceRefs', 12, 200);
  const related = requireStringArray('related', 8, 64);
  if (related.some(item => !SLUG.test(item))) throw new Error(`${sourcePath}: related entries must be slugs`);
  requireString('verifiedFor', 32);
  if (!VERIFICATION.has(requireString('verificationStatus', 64))) throw new Error(`${sourcePath}: unsupported verification status`);
  const lastReviewed = requireString('lastReviewed', 10);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(lastReviewed) || Number.isNaN(Date.parse(`${lastReviewed}T00:00:00Z`)))
    throw new Error(`${sourcePath}: lastReviewed must be YYYY-MM-DD`);
  if (!STATUSES.has(requireString('status', 16))) throw new Error(`${sourcePath}: unsupported status`);

  const body = normalized.slice(close + 5).trim();
  if (!body) throw new Error(`${sourcePath}: article body is empty`);
  return { ...fields, keywords, aliases, errorCodes, audience, sourceRefs, related, body, sourcePath };
}

function slugifyHeading(value) {
  return value.toLocaleLowerCase('zh-CN')
    .replace(/[^\p{L}\p{N}]+/gu, '-')
    .replace(/^-+|-+$/g, '') || 'section';
}

function assertHeadingExists(slug, anchor, context) {
  const headingIds = slug === context.currentSlug
    ? context.currentHeadingIds
    : context.publishedHeadingIds?.get(slug);
  if (headingIds && !headingIds.has(anchor))
    throw new Error(`${context.sourcePath}: link points to missing heading '${anchor}' in article '${slug}'`);
}

function normalizeMarkdownLink(rawHref, context) {
  const href = rawHref.trim();
  if (!href || /[\u0000-\u0020\\]/.test(href)) return null;
  const relative = href.match(/^\.\/([a-z0-9-]+)\.md(?:#([\p{L}\p{N}-]+))?$/u);
  if (relative) {
    if (!context.publishedSlugs.has(relative[1])) throw new Error(`${context.sourcePath}: link points to unpublished or missing article '${relative[1]}'`);
    if (relative[2]) assertHeadingExists(relative[1], relative[2], context);
    return `/docs/articles/${relative[1]}.html${relative[2] ? `#${relative[2]}` : ''}`;
  }
  if (href.startsWith('#')) {
    const anchor = href.match(/^#([\p{L}\p{N}-]+)$/u)?.[1];
    if (!anchor) throw new Error(`${context.sourcePath}: invalid in-page anchor '${href}'`);
    if (context.currentSlug) assertHeadingExists(context.currentSlug, anchor, context);
    return href;
  }
  if (href.startsWith('/') && !href.startsWith('//')) {
    const pathOnly = href.split(/[?#]/, 1)[0];
    const routePath = pathOnly.length > 1 ? pathOnly.replace(/\/$/, '') : pathOnly;
    const article = href.match(/^\/docs\/articles\/([a-z0-9-]+)\.html(?:#([\p{L}\p{N}-]+))?$/u);
    const routeAnchors = INTERNAL_ROUTE_ANCHORS.get(routePath);
    if (article) {
      if (!context.publishedSlugs.has(article[1]))
        throw new Error(`${context.sourcePath}: link points to unpublished or missing article '${article[1]}'`);
      if (article[2]) assertHeadingExists(article[1], article[2], context);
      return href;
    }
    if (routeAnchors) {
      const hashAt = href.indexOf('#');
      if (hashAt >= 0) {
        const anchor = href.slice(hashAt + 1);
        if (!routeAnchors.has(anchor))
          throw new Error(`${context.sourcePath}: internal link points to an unknown anchor '${anchor}' on route '${pathOnly}'`);
      }
      return href;
    }
    throw new Error(`${context.sourcePath}: internal link points to an unknown route '${href}'`);
  }
  if (href.startsWith('./') || href.startsWith('../'))
    throw new Error(`${context.sourcePath}: unsupported relative link '${href}'`);
  try {
    const parsed = new URL(href);
    if (parsed.protocol !== 'https:' || parsed.username || parsed.password) return null;
    return parsed.href;
  } catch {
    if (/^[A-Za-z][A-Za-z0-9+.-]*:/.test(href)) return null;
    throw new Error(`${context.sourcePath}: unsupported relative link '${href}'`);
  }
}

export function renderInline(text, context) {
  const token = /`([^`]+)`|\[([^\]]+)\]\(([^)]+)\)|\*\*([^*]+)\*\*|\*([^*]+)\*/g;
  let output = '';
  let cursor = 0;
  for (const match of text.matchAll(token)) {
    output += escapeHtml(text.slice(cursor, match.index));
    if (match[1] !== undefined) output += `<code>${escapeHtml(match[1])}</code>`;
    else if (match[2] !== undefined) {
      const href = normalizeMarkdownLink(match[3], context);
      const label = escapeHtml(match[2]);
      if (!href) output += label;
      else if (href.startsWith('https://'))
        output += `<a href="${escapeHtml(href)}" target="_blank" rel="noopener noreferrer">${label}</a>`;
      else output += `<a href="${escapeHtml(href)}">${label}</a>`;
    } else if (match[4] !== undefined) output += `<strong>${escapeHtml(match[4])}</strong>`;
    else output += `<em>${escapeHtml(match[5])}</em>`;
    cursor = match.index + match[0].length;
  }
  return output + escapeHtml(text.slice(cursor));
}

export function renderMarkdown(source, context) {
  const lines = source.replace(/\r\n/g, '\n').split('\n');
  const output = [];
  const headings = [];
  const ids = new Set();
  let paragraph = [];
  let listType = null;
  let code = null;

  const flushParagraph = () => {
    if (paragraph.length) {
      output.push(`<p>${paragraph.map(line => renderInline(line, context)).join('<br>')}</p>`);
      paragraph = [];
    }
  };
  const closeList = () => {
    if (listType) { output.push(`</${listType}>`); listType = null; }
  };
  const closeCode = () => {
    output.push(`<pre><code>${escapeHtml(code.join('\n'))}</code></pre>`);
    code = null;
  };

  for (const line of lines) {
    if (code !== null) {
      if (/^\s*```\s*$/.test(line)) closeCode();
      else code.push(line);
      continue;
    }
    if (/^\s*```/.test(line)) {
      flushParagraph(); closeList(); code = [];
      continue;
    }
    const heading = line.match(/^\s*(#{1,6})\s+(.+?)\s*#*\s*$/);
    if (heading) {
      flushParagraph(); closeList();
      const text = heading[2];
      let id = slugifyHeading(text);
      let suffix = 2;
      while (ids.has(id)) id = `${slugifyHeading(text)}-${suffix++}`;
      ids.add(id);
      const level = Math.min(4, Math.max(2, heading[1].length));
      headings.push({ id, text, level });
      output.push(`<h${level} id="${escapeHtml(id)}">${renderInline(text, context)} <a class="doc-heading-anchor" href="#${escapeHtml(id)}" aria-label="链接到：${escapeHtml(text)}">¶</a></h${level}>`);
      continue;
    }
    const item = line.match(/^\s*(?:([-*])\s+|(\d+)\.\s+)(.*)$/);
    if (item) {
      flushParagraph();
      const nextType = item[2] ? 'ol' : 'ul';
      if (listType !== nextType) { closeList(); output.push(`<${nextType}>`); listType = nextType; }
      output.push(`<li>${renderInline(item[3], context)}</li>`);
      continue;
    }
    const quote = line.match(/^\s*>\s?(.*)$/);
    if (quote) {
      flushParagraph(); closeList();
      output.push(`<blockquote><p>${renderInline(quote[1], context)}</p></blockquote>`);
      continue;
    }
    if (/^\s*(?:---+|\*\*\*+)\s*$/.test(line)) {
      flushParagraph(); closeList(); output.push('<hr>'); continue;
    }
    if (!line.trim()) { flushParagraph(); closeList(); continue; }
    closeList(); paragraph.push(line.trim());
  }
  flushParagraph();
  closeList();
  if (code !== null) closeCode();
  return { html: output.join('\n'), headings };
}

function listMarkdownFiles(directory) {
  if (!fs.existsSync(directory)) throw new Error(`public documentation directory is missing: ${directory}`);
  const rootStat = fs.lstatSync(directory);
  if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) throw new Error('public documentation root must be a real directory');
  const found = [];
  const visit = current => {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const fullPath = path.join(current, entry.name);
      const stat = fs.lstatSync(fullPath);
      if (stat.isSymbolicLink()) throw new Error(`symbolic links are not allowed in public content: ${fullPath}`);
      if (stat.isDirectory()) visit(fullPath);
      else if (stat.isFile() && entry.name.endsWith('.md')) found.push(fullPath);
      else throw new Error(`unsupported entry in public content: ${fullPath}`);
    }
  };
  visit(directory);
  if (found.length > MAX_SOURCE_FILES) throw new Error(`public documentation exceeds ${MAX_SOURCE_FILES} markdown files`);
  return found.sort();
}

export function loadPublicDocuments(contentDirectory) {
  const files = listMarkdownFiles(contentDirectory);
  let totalBytes = 0;
  const documents = files.map(file => {
    const stat = fs.statSync(file);
    totalBytes += stat.size;
    return parsePublicDocument(fs.readFileSync(file, 'utf8'), file);
  });
  if (totalBytes > MAX_TOTAL_SOURCE_BYTES) throw new Error('public documentation exceeds total source byte limit');
  const documentsBySlug = new Map();
  for (const document of documents) {
    if (documentsBySlug.has(document.slug)) throw new Error(`duplicate public document slug: ${document.slug}`);
    documentsBySlug.set(document.slug, document);
    const expected = `${document.slug}.md`;
    if (path.basename(document.sourcePath) !== expected)
      throw new Error(`${document.sourcePath}: filename must match slug '${expected}'`);
  }
  for (const document of documents) {
    for (const slug of document.related) {
      const related = documentsBySlug.get(slug);
      if (!related) throw new Error(`${document.sourcePath}: related article '${slug}' does not exist`);
      if (document.status === 'published' && related.status !== 'published')
        throw new Error(`${document.sourcePath}: published article relates to non-published article '${slug}'`);
    }
  }
  return documents;
}

const IP_ADDRESS_CANDIDATES = [
  /(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)/g,
  /(?<![0-9a-f:])(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}(?:%[a-z0-9_.-]+)?(?![0-9a-f:])/gi
];

function containsIpLiteral(text) {
  return IP_ADDRESS_CANDIDATES.some(pattern =>
    [...text.matchAll(pattern)].some(([candidate]) => isIP(candidate.split('%', 1)[0]) !== 0));
}

export function scanForPublicSecrets(text, label) {
  if (containsIpLiteral(text)) throw new Error(`${label}: public safety scan matched an IP address`);
  const patterns = [
    /-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----/i,
    /-----BEGIN (?:RSA |EC |X509 )?(?:PUBLIC KEY|CERTIFICATE)-----/i,
    /\b(?:api[_-]?key|access[_-]?token|client[_-]?secret)\s*[:=]\s*[A-Za-z0-9_\-/.+=]{12,}/i,
    /\b(?:private[_-]?key[a-z0-9_-]*|signing[_-]?key[a-z0-9_-]*|(?:access|refresh|auth|bearer)?[_-]?token[a-z0-9_-]*|api[_-]?key[a-z0-9_-]*|client[_-]?secret[a-z0-9_-]*|secret[a-z0-9_-]*|public[_-]?key[a-z0-9_-]*|teacherchannelrootcertificatebase64)\s*["']?\s*[:=]\s*["']?[A-Za-z0-9_+/.=-]{12,}/i,
    /\b(?:password|passwd)\s*["']?\s*[:=]\s*["']?[^\s"']{10,}/i,
    /\b[A-F0-9]{8}-(?:[A-F0-9]{4}-){3}[A-F0-9]{12}\b/i,
    /(?<!\d)1[3-9]\d{9}(?!\d)/,
    /\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b/i,
    /\b\d{3}-\d{4}-\d{4}\b/
  ];
  for (const pattern of patterns) if (pattern.test(text)) throw new Error(`${label}: public safety scan matched ${pattern}`);
}

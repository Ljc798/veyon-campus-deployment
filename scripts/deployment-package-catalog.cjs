'use strict';

// The deployment-package catalogue is the static replacement for the retired CloudBase search API.
//
// A campus package is at most 64 KiB, changes a handful of times per school, and every client that
// needs it already trusts bytes it can hash. That makes a JSON document plus one ZIP per package a
// better fit than a database: the files live in the repository, GitCode serves them from mainland
// China in ~0.2s, and nothing can pause or bill.
//
//   packages/deployment-catalog.json          the index every client reads
//   packages/<packageId>.zip                  the exact archive a teacher publishes
//
// The document deliberately mirrors the old HTTP response field names so the client's existing
// records keep working, and it keeps withdrawn rows instead of deleting them: a teacher who sees
// "撤回" needs to know the package existed.

const crypto = require('node:crypto');

const CATALOG_SCHEMA_VERSION = 1;
const CATALOG_FILE_NAME = 'deployment-catalog.json';
const PACKAGE_DIRECTORY_NAME = 'packages';
const PACKAGE_FILE_EXTENSION = '.zip';
const MAX_CATALOG_BYTES = 1024 * 1024;
const MAX_CATALOG_PACKAGES = 5000;
const DEFAULT_PAGE_SIZE = 20;
const MAX_PAGE_SIZE = 49;
const MAX_OFFSET = 10000;
const MAX_QUERY_LENGTH = 100;

const PACKAGE_STATUS = Object.freeze({ published: 'published', withdrawn: 'withdrawn' });

const SHA256_PATTERN = /^[0-9A-F]{64}$/;
const OWNER_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/;
const REF_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$/;

function fail(message) {
  throw new Error(message);
}

/// The validator hands back the package id as 32 bare hex characters because that is what PostgreSQL
/// accepted for its uuid column. The catalogue and the archive filename use the canonical dashed
/// form so the value can be pasted into a URL or a `Guid.Parse` without a second normalisation step.
function normalizePackageId(value) {
  if (typeof value !== 'string') fail('packageId must be a string.');
  const compact = value.trim().replace(/[{}-]/g, '').toLowerCase();
  if (!/^[0-9a-f]{32}$/.test(compact) || compact === '0'.repeat(32))
    fail('packageId must be a non-empty GUID.');
  return [
    compact.slice(0, 8),
    compact.slice(8, 12),
    compact.slice(12, 16),
    compact.slice(16, 20),
    compact.slice(20)
  ].join('-');
}

function requireString(value, label, maximum = 400) {
  if (typeof value !== 'string' || value.length < 1 || value.length > maximum || /\p{Cc}/u.test(value))
    fail(`${label} is not a valid string.`);
  return value;
}

// ---------------------------------------------------------------- URL construction

/// Where the catalogue and the archives are served from.
///
/// GitCode's raw *file* endpoint lives on the API host and takes the ref as a query parameter; its
/// `raw.gitcode.com` subdomain only renders text previews and answers binary requests with a
/// placeholder SVG. GitHub's raw host is the one path shape that omits `/raw/`.
function createSource(options) {
  const kind = options.kind ?? 'gitcode';
  if (kind !== 'gitcode' && kind !== 'github') fail('kind must be gitcode or github.');
  const owner = requireString(options.owner, 'owner', 100);
  const repository = requireString(options.repository, 'repository', 100);
  const ref = requireString(options.ref ?? 'develop', 'ref', 200);
  if (!OWNER_PATTERN.test(owner) || !OWNER_PATTERN.test(repository))
    fail('owner and repository must be plain slugs.');
  if (!REF_PATTERN.test(ref) || ref.includes('..')) fail('ref must be a plain branch or tag name.');
  return { kind, owner, repository, ref };
}

function rawUrl(source, relativePath) {
  const ref = encodeURIComponent(source.ref);
  return source.kind === 'github'
    ? `https://raw.githubusercontent.com/${source.owner}/${source.repository}/${ref}/${relativePath}`
    : `https://api.gitcode.com/api/v5/repos/${source.owner}/${source.repository}/raw/${relativePath}?ref=${ref}`;
}

function catalogUrl(source) {
  return rawUrl(source, `${PACKAGE_DIRECTORY_NAME}/${CATALOG_FILE_NAME}`);
}

function packageUrl(source, packageId) {
  return rawUrl(source, `${PACKAGE_DIRECTORY_NAME}/${normalizePackageId(packageId)}${PACKAGE_FILE_EXTENSION}`);
}

// ---------------------------------------------------------------- document

function createEmptyCatalog() {
  return { schemaVersion: CATALOG_SCHEMA_VERSION, generatedAt: new Date().toISOString(), packages: [] };
}

function isPlainObject(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function normalizeEntry(raw, index) {
  const label = `packages[${index}]`;
  if (!isPlainObject(raw)) fail(`${label} must be an object.`);
  const packageId = normalizePackageId(raw.packageId);
  const status = raw.status ?? PACKAGE_STATUS.published;
  if (status !== PACKAGE_STATUS.published && status !== PACKAGE_STATUS.withdrawn)
    fail(`${label}.status must be published or withdrawn.`);
  const sha256 = requireString(raw.sha256, `${label}.sha256`, 64).toUpperCase();
  if (!SHA256_PATTERN.test(sha256)) fail(`${label}.sha256 must be 64 hex characters.`);
  const sizeBytes = raw.sizeBytes;
  if (!Number.isInteger(sizeBytes) || sizeBytes < 1 || sizeBytes > 64 * 1024)
    fail(`${label}.sizeBytes is out of range.`);
  const publishedAt = requireString(raw.publishedAt, `${label}.publishedAt`, 40);
  if (Number.isNaN(Date.parse(publishedAt))) fail(`${label}.publishedAt must be an ISO timestamp.`);
  const downloadUrl = requireString(raw.downloadUrl, `${label}.downloadUrl`, 2048);
  if (!/^https:\/\//i.test(downloadUrl)) fail(`${label}.downloadUrl must be HTTPS.`);
  const mirrors = raw.mirrors ?? [];
  if (!Array.isArray(mirrors)) fail(`${label}.mirrors must be an array.`);
  for (const mirror of mirrors) {
    if (typeof mirror !== 'string' || !/^https:\/\//i.test(mirror))
      fail(`${label}.mirrors entries must be HTTPS URLs.`);
  }
  const schemaVersion = raw.schemaVersion;
  if (!Number.isInteger(schemaVersion) || schemaVersion < 3 || schemaVersion > 8)
    fail(`${label}.schemaVersion must be 3-8.`);
  return {
    packageId,
    campusId: Number.isInteger(raw.campusId) && raw.campusId > 0 ? raw.campusId : null,
    campusName: requireString(raw.campusName, `${label}.campusName`, 100),
    displayName: requireString(raw.displayName, `${label}.displayName`, 200),
    computerPrefix: requireString(raw.computerPrefix, `${label}.computerPrefix`, 32),
    schemaVersion,
    targetOs: requireString(raw.targetOs ?? 'windows', `${label}.targetOs`, 32),
    architecture: requireString(raw.architecture ?? 'x64', `${label}.architecture`, 32),
    fileName: requireString(raw.fileName ?? `campus-package${PACKAGE_FILE_EXTENSION}`,
      `${label}.fileName`, 240),
    sizeBytes,
    sha256,
    downloadUrl,
    mirrors: mirrors.map((mirror) => mirror.trim()).filter((mirror) => mirror.length > 0),
    downloadCount: Number.isInteger(raw.downloadCount) && raw.downloadCount >= 0 ? raw.downloadCount : 0,
    publishedAt,
    requiresPhoneLast4: raw.requiresPhoneLast4 === true,
    status,
    ...(status === PACKAGE_STATUS.withdrawn && typeof raw.withdrawnAt === 'string'
      ? { withdrawnAt: raw.withdrawnAt }
      : {})
  };
}

function parseCatalog(value) {
  const text = typeof value === 'string' ? value : JSON.stringify(value);
  if (Buffer.byteLength(text, 'utf8') > MAX_CATALOG_BYTES) fail('catalogue exceeds 1 MiB.');
  let document;
  try {
    document = typeof value === 'string' ? JSON.parse(value) : value;
  } catch (error) {
    fail(`catalogue is not valid JSON: ${error.message}`);
  }
  if (!isPlainObject(document)) fail('catalogue must be an object.');
  if (document.schemaVersion !== CATALOG_SCHEMA_VERSION)
    fail(`catalogue schemaVersion must be ${CATALOG_SCHEMA_VERSION}.`);
  if (!Array.isArray(document.packages)) fail('catalogue.packages must be an array.');
  if (document.packages.length > MAX_CATALOG_PACKAGES)
    fail(`catalogue cannot hold more than ${MAX_CATALOG_PACKAGES} packages.`);
  const packages = document.packages.map(normalizeEntry);
  const seen = new Set();
  for (const entry of packages) {
    if (seen.has(entry.packageId)) fail(`catalogue repeats packageId ${entry.packageId}.`);
    seen.add(entry.packageId);
  }
  return {
    schemaVersion: CATALOG_SCHEMA_VERSION,
    generatedAt: typeof document.generatedAt === 'string' ? document.generatedAt : new Date().toISOString(),
    packages
  };
}

function serializeCatalog(catalog) {
  const document = {
    schemaVersion: CATALOG_SCHEMA_VERSION,
    generatedAt: catalog.generatedAt,
    packages: [...catalog.packages].sort(compareForStorage)
  };
  const text = `${JSON.stringify(document, null, 2)}\n`;
  if (Buffer.byteLength(text, 'utf8') > MAX_CATALOG_BYTES) fail('catalogue would exceed 1 MiB.');
  return text;
}

// Newest first, then by id so two packages published in the same second keep a stable order. The
// id comparison is byte-wise on purpose: the SQL function ordered by the uuid column, and
// localeCompare would reorder ASCII digits and letters under some ICU locales.
function compareForStorage(left, right) {
  const byTime = Date.parse(right.publishedAt) - Date.parse(left.publishedAt);
  if (byTime !== 0 && Number.isFinite(byTime)) return byTime;
  if (left.packageId === right.packageId) return 0;
  return left.packageId < right.packageId ? 1 : -1;
}

// ---------------------------------------------------------------- mutation

function upsertPackage(catalog, entry) {
  const normalized = normalizeEntry(entry, catalog.packages.length);
  const packages = catalog.packages.filter((existing) => existing.packageId !== normalized.packageId);
  packages.push(normalized);
  return { ...catalog, generatedAt: new Date().toISOString(), packages };
}

/// Withdrawing keeps the row so a stale client can tell "removed" from "never existed"; the client
/// filters it out of searches and refuses to download it.
function withdrawPackage(catalog, packageId, withdrawnAt = new Date().toISOString()) {
  const target = normalizePackageId(packageId);
  let found = false;
  const packages = catalog.packages.map((entry) => {
    if (entry.packageId !== target) return entry;
    found = true;
    return normalizeEntry({ ...entry, status: PACKAGE_STATUS.withdrawn, withdrawnAt }, 0);
  });
  if (!found) fail(`catalogue does not contain packageId ${target}.`);
  return { ...catalog, generatedAt: new Date().toISOString(), packages };
}

// ---------------------------------------------------------------- search

/// Plain case-insensitive substring match on campus name and computer prefix, which is what the SQL
/// function's escaped ILIKE reduced to for every query a teacher actually types.
function matches(entry, needle) {
  if (needle.length === 0) return true;
  return entry.campusName.toLowerCase().includes(needle) ||
    entry.computerPrefix.toLowerCase().includes(needle);
}

function searchCatalog(catalog, options = {}) {
  const query = options.query ?? '';
  if (typeof query !== 'string' || query.length > MAX_QUERY_LENGTH || /\p{Cc}/u.test(query))
    fail(`query must be at most ${MAX_QUERY_LENGTH} characters without control characters.`);
  const campusId = options.campusId ?? null;
  if (campusId !== null && (!Number.isInteger(campusId) || campusId <= 0))
    fail('campusId must be a positive integer.');
  const limit = normalizeInteger(options.limit, DEFAULT_PAGE_SIZE, 1, MAX_PAGE_SIZE);
  const offset = normalizeInteger(options.offset, 0, 0, MAX_OFFSET);

  const needle = query.trim().toLowerCase();
  const matched = catalog.packages
    .filter((entry) => entry.status === PACKAGE_STATUS.published)
    .filter((entry) => campusId === null || entry.campusId === campusId)
    .filter((entry) => matches(entry, needle))
    .sort(compareForStorage);

  const slice = matched.slice(offset, offset + limit + 1);
  const hasMore = slice.length > limit;
  if (hasMore) slice.pop();
  return { items: slice, limit, offset, hasMore };
}

function normalizeInteger(value, fallback, minimum, maximum) {
  if (value === undefined || value === null) return fallback;
  if (!Number.isInteger(value) || value < minimum || value > maximum)
    fail(`expected an integer between ${minimum} and ${maximum}.`);
  return value;
}

/// The URLs a client may try for one entry, in order. The first one is the canonical archive.
function downloadUrls(entry) {
  const urls = [entry.downloadUrl];
  for (const mirror of entry.mirrors) {
    if (!urls.some((existing) => existing.toLowerCase() === mirror.toLowerCase())) urls.push(mirror);
  }
  return urls;
}

// ---------------------------------------------------------------- archive

/// Computes the digest the catalogue advertises. Uppercase hex matches every other digest this
/// project publishes.
function sha256Hex(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex').toUpperCase();
}

module.exports = {
  CATALOG_FILE_NAME,
  CATALOG_SCHEMA_VERSION,
  DEFAULT_PAGE_SIZE,
  MAX_CATALOG_BYTES,
  MAX_PAGE_SIZE,
  PACKAGE_DIRECTORY_NAME,
  PACKAGE_FILE_EXTENSION,
  PACKAGE_STATUS,
  catalogUrl,
  createEmptyCatalog,
  createSource,
  downloadUrls,
  normalizePackageId,
  packageUrl,
  parseCatalog,
  searchCatalog,
  serializeCatalog,
  sha256Hex,
  upsertPackage,
  withdrawPackage
};

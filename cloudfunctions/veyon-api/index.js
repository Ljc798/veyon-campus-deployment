'use strict';

const http = require('node:http');
const crypto = require('node:crypto');
const net = require('node:net');
const { URL } = require('node:url');
const {
  InvalidPackageError,
  MAX_ARCHIVE_BYTES,
  canonicalizeArchive,
  canonicalizeFolderFiles,
  parseJson
} = require('./package-validator');

const MAX_REQUEST_BYTES = 128 * 1024;
const DEFAULT_PAGE_SIZE = 20;
const MAX_PAGE_SIZE = 49;
const MAX_DOWNLOAD_ATTEMPTS = 10;
const ATTEMPT_WINDOW_MS = 15 * 60 * 1000;
const BLOCK_DURATION_MS = 15 * 60 * 1000;
const MAX_ATTEMPT_KEYS = 8192;

class CloudBaseFailure extends Error {
  constructor(status, message) {
    super(message || 'CloudBase request failed.');
    this.status = status;
  }
}

function loadConfig(environment = process.env) {
  const envId = (environment.CloudBase__EnvId || '').trim();
  const apiKey = (environment.CloudBase__ApiKey || '').trim();
  const encodedHashKey = (environment.Telemetry__DailyHashKey || '').trim();
  const bucketId = (environment.CloudBase__DeploymentPackageBucket ||
    'deployment-package-artifacts').trim();
  if (!/^[A-Za-z0-9-]+$/.test(envId) || !apiKey)
    throw new Error('CloudBase server configuration is incomplete.');
  if (!/^[A-Za-z0-9+/]*={0,2}$/.test(encodedHashKey))
    throw new Error('Telemetry key configuration is invalid.');
  const hashKey = Buffer.from(encodedHashKey, 'base64');
  if (hashKey.length < 32 || hashKey.toString('base64') !== encodedHashKey)
    throw new Error('Telemetry key configuration is invalid.');
  if (!/^[A-Za-z0-9-]+$/.test(bucketId))
    throw new Error('CloudBase storage bucket configuration is invalid.');

  const apiBase = 'https://' + envId + '.api.tcloudbasegateway.com';
  return {
    envId,
    apiKey,
    hashKey,
    bucketId,
    apiBase,
    rdbBase: apiBase + '/v1/rdb/rest'
  };
}

function hmacHex(key, value) {
  return crypto.createHmac('sha256', key).update(value).digest('hex').toUpperCase();
}

function identityFingerprint(config, purpose, value) {
  return hmacHex(config.hashKey,
    'VeyonCampus/DeploymentPackages/' + purpose + '/v1\n' + value);
}

function hktDay(now = new Date()) {
  return new Date(now.getTime() + 8 * 60 * 60 * 1000).toISOString().slice(0, 10);
}

function createHeartbeatDigests(config, day, installationId, deploymentId) {
  const dayKey = crypto.createHmac('sha256', config.hashKey).update(day, 'ascii').digest();
  const digest = hmacHex(dayKey, Buffer.from(installationId, 'ascii'));
  let deploymentDigest = null;
  if (deploymentId) {
    deploymentDigest = hmacHex(dayKey,
      Buffer.from('deployment:' + deploymentId + ':' + installationId, 'utf8'));
  }
  dayKey.fill(0);
  return { digest, deploymentDigest };
}

async function readResponseBuffer(response, maximumBytes) {
  if (!response.body) return Buffer.alloc(0);
  const reader = response.body.getReader();
  const parts = [];
  let length = 0;
  while (true) {
    const result = await reader.read();
    if (result.done) break;
    const part = Buffer.from(result.value);
    length += part.length;
    if (length > maximumBytes) {
      await reader.cancel();
      throw new Error('Upstream response exceeded its size limit.');
    }
    parts.push(part);
  }
  return Buffer.concat(parts, length);
}

async function parseFailure(response) {
  let message = 'CloudBase rejected the request.';
  try {
    const bytes = await readResponseBuffer(response, 8192);
    const parsed = JSON.parse(bytes.toString('utf8'));
    if (typeof parsed.message === 'string') message = parsed.message;
    else if (typeof parsed.error === 'string') message = parsed.error;
  } catch {
    // The underlying message is used only for status mapping and is never logged or returned.
  }
  return new CloudBaseFailure(response.status, message);
}

async function cloudRequest(config, path, options = {}) {
  const headers = { Accept: 'application/json' };
  const token = options.token || config.apiKey;
  headers.Authorization = 'Bearer ' + token;
  let body;
  if (options.bytes) {
    body = options.bytes;
    headers['Content-Type'] = options.contentType || 'application/octet-stream';
    if (options.noUpsert) headers['x-upsert'] = 'false';
  } else if (options.body !== undefined) {
    body = JSON.stringify(options.body);
    headers['Content-Type'] = 'application/json';
  }

  let response;
  try {
    response = await fetch(config.apiBase + path, {
      method: options.method || 'GET',
      headers,
      body,
      signal: AbortSignal.timeout(options.timeoutMs || 10000),
      redirect: 'error'
    });
  } catch {
    throw new Error('CloudBase is temporarily unavailable.');
  }
  if (!response.ok) throw await parseFailure(response);
  if (options.response === 'bytes')
    return readResponseBuffer(response, options.maximumBytes || MAX_ARCHIVE_BYTES);
  if (options.response === 'none') {
    if (response.body) await response.body.cancel().catch(() => {});
    return null;
  }
  const bytes = await readResponseBuffer(response, options.maximumBytes || 256 * 1024);
  if (bytes.length === 0) return null;
  try {
    return JSON.parse(bytes.toString('utf8'));
  } catch {
    throw new Error('CloudBase returned an invalid response.');
  }
}

async function rpc(config, name, body, timeoutMs = 10000) {
  const result = await cloudRequest(config,
    '/v1/rdb/rest/rpc/' + encodeURIComponent(name), {
      method: 'POST',
      body,
      timeoutMs,
      maximumBytes: 256 * 1024
    });
  return result;
}

async function table(config, name, query) {
  const result = await cloudRequest(config,
    '/v1/rdb/rest/' + encodeURIComponent(name) + '?' + query.toString(), {
      method: 'GET',
      timeoutMs: 10000,
      maximumBytes: 256 * 1024
    });
  if (!Array.isArray(result)) throw new Error('CloudBase returned an invalid table response.');
  return result;
}

async function getCurrentUser(config, accessToken) {
  let result;
  try {
    result = await cloudRequest(config, '/auth/v1/user/me', {
      method: 'GET',
      token: accessToken,
      timeoutMs: 30000,
      maximumBytes: 16 * 1024
    });
  } catch (error) {
    if (error instanceof CloudBaseFailure && (error.status === 401 || error.status === 403))
      return null;
    throw error;
  }
  if (!result || typeof result.sub !== 'string' ||
      result.sub.trim().length < 1 || result.sub.length > 64 ||
      /\p{Cc}/u.test(result.sub) ||
      (typeof result.status === 'string' && result.status.toUpperCase() !== 'ACTIVE'))
    return null;
  return { userId: result.sub };
}

function positiveInteger(value, label, required) {
  if (value === undefined || value === null || value === '') {
    if (!required) return null;
    throw new InvalidRequestError(label + ' must be a positive integer');
  }
  const text = String(value);
  if (!/^[1-9][0-9]{0,15}$/.test(text))
    throw new InvalidRequestError(label + ' must be a positive integer');
  const number = Number(text);
  if (!Number.isSafeInteger(number) || number <= 0)
    throw new InvalidRequestError(label + ' must be a positive integer');
  return number;
}

class InvalidRequestError extends Error {}

function integerParameter(value, fallback, minimum, maximum, label) {
  if (value === null) return fallback;
  if (!/^(?:0|[1-9][0-9]*)$/.test(value))
    throw new InvalidRequestError(label + ' is invalid');
  const result = Number(value);
  if (!Number.isSafeInteger(result) || result < minimum || result > maximum)
    throw new InvalidRequestError(label + ' is invalid');
  return result;
}

function sendJson(response, status, payload, headers = {}) {
  const body = Buffer.from(JSON.stringify(payload), 'utf8');
  response.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': body.length,
    ...headers
  });
  response.end(body);
}

function sendProblem(response, status, detail, title) {
  sendJson(response, status, {
    type: 'about:blank',
    title: title || (status >= 500 ? '服务暂时不可用' : '请求无法处理'),
    status,
    detail
  });
}

function noContent(response, headers = {}) {
  response.writeHead(204, headers);
  response.end();
}

const CORS_HEADERS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
  'Access-Control-Allow-Headers': 'Accept, Authorization, Content-Type',
  'Access-Control-Max-Age': '600'
};

async function readRequestBody(request, maximumBytes) {
  const chunks = [];
  let length = 0;
  let oversized = false;
  for await (const chunk of request) {
    const bytes = Buffer.from(chunk);
    length += bytes.length;
    if (length > maximumBytes) {
      oversized = true;
      continue;
    }
    chunks.push(bytes);
  }
  if (oversized) throw new RequestTooLargeError();
  return Buffer.concat(chunks, length);
}

class RequestTooLargeError extends Error {}

function parseJsonObject(bytes, allowedFields, allowEmpty = false) {
  if (bytes.length === 0 && allowEmpty) return {};
  let value;
  try {
    value = parseJson(bytes, '请求正文');
  } catch {
    throw new InvalidRequestError('请求正文不是有效 JSON。');
  }
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new InvalidRequestError('请求正文必须是 JSON 对象。');
  if (Object.keys(value).some((key) => !allowedFields.has(key)))
    throw new InvalidRequestError('请求正文含有未允许的字段。');
  return value;
}

function contentDispositionParameter(header, parameter) {
  const quoted = new RegExp('(?:^|;)\\s*' + parameter + '="([^"]*)"\\s*(?=;|$)', 'i').exec(header);
  if (quoted) return quoted[1];
  const bare = new RegExp('(?:^|;)\\s*' + parameter + '=([^;\\s]+)\\s*(?=;|$)', 'i').exec(header);
  return bare ? bare[1] : null;
}

function parseMultipart(contentType, body) {
  const match = /(?:^|;)\s*boundary=(?:"([^"]+)"|([^;\s]+))/i.exec(contentType);
  const boundary = match && (match[1] || match[2]);
  if (!boundary || boundary.length > 70 || /[\r\n]/.test(boundary))
    throw new InvalidRequestError('multipart/form-data 边界无效。');
  const delimiter = Buffer.from('--' + boundary, 'ascii');
  const separator = Buffer.from('\r\n--' + boundary, 'ascii');
  if (body.length < delimiter.length + 4 ||
      !body.subarray(0, delimiter.length).equals(delimiter))
    throw new InvalidRequestError('multipart/form-data 格式无效。');

  const fields = new Map();
  const files = [];
  let cursor = delimiter.length;
  let partCount = 0;
  while (cursor < body.length) {
    if (body.subarray(cursor, cursor + 2).toString('ascii') === '--') break;
    if (body.subarray(cursor, cursor + 2).toString('ascii') !== '\r\n')
      throw new InvalidRequestError('multipart/form-data 分隔符无效。');
    cursor += 2;
    const headersEnd = body.indexOf(Buffer.from('\r\n\r\n'), cursor);
    if (headersEnd < 0 || headersEnd - cursor > 8192)
      throw new InvalidRequestError('multipart/form-data 文件头无效。');
    const headerText = body.subarray(cursor, headersEnd).toString('latin1');
    const dispositionLine = headerText.split('\r\n').find((line) =>
      /^content-disposition:/i.test(line));
    if (!dispositionLine)
      throw new InvalidRequestError('multipart/form-data 缺少 Content-Disposition。');
    const disposition = dispositionLine.slice(dispositionLine.indexOf(':') + 1).trim();
    if (!/^form-data\b/i.test(disposition))
      throw new InvalidRequestError('multipart/form-data 类型无效。');
    const name = contentDispositionParameter(disposition, 'name');
    if (!name || name.length > 64)
      throw new InvalidRequestError('multipart/form-data 字段名无效。');
    const filename = contentDispositionParameter(disposition, 'filename');
    let decodedFilename = filename;
    const filenameStar = /(?:^|;)\s*filename\*=utf-8''([^;]+)/i.exec(disposition);
    if (filenameStar) {
      try { decodedFilename = decodeURIComponent(filenameStar[1]); }
      catch { throw new InvalidRequestError('上传文件名编码无效。'); }
    }
    cursor = headersEnd + 4;
    const next = body.indexOf(separator, cursor);
    if (next < 0) throw new InvalidRequestError('multipart/form-data 缺少结束分隔符。');
    const bytes = body.subarray(cursor, next);
    cursor = next + 2 + delimiter.length;
    partCount++;
    if (partCount > 8) throw new InvalidRequestError('上传字段数量超过限制。');

    if (decodedFilename !== null) {
      files.push({ name, fileName: decodedFilename, bytes });
    } else {
      if (fields.has(name)) throw new InvalidRequestError('multipart/form-data 文本字段不能重复。');
      if (bytes.length > 2048) throw new InvalidRequestError('multipart/form-data 文本字段过大。');
      try {
        fields.set(name, new TextDecoder('utf-8', { fatal: true }).decode(bytes));
      } catch {
        throw new InvalidRequestError('multipart/form-data 文本字段不是有效 UTF-8。');
      }
    }
  }
  if (partCount < 1) throw new InvalidRequestError('multipart/form-data 不能为空。');
  return { fields, files };
}

function bearerToken(request) {
  const header = request.headers.authorization;
  if (typeof header !== 'string' || !/^Bearer /i.test(header)) return null;
  const token = header.slice(7).trim();
  if (token.length < 1 || token.length > 8192 || /\s/.test(token)) return null;
  return token;
}

function downloadClientAddress(request) {
  const originalHeaders = [];
  for (let index = 0; index < request.rawHeaders.length; index += 2) {
    if (request.rawHeaders[index].toLowerCase() === 'x-original-forwarded-for')
      originalHeaders.push(request.rawHeaders[index + 1]);
  }
  if (originalHeaders.length === 1) {
    const forwarded = originalHeaders[0].trim();
    if (!forwarded.includes(',') && net.isIP(forwarded)) return normalizeIp(forwarded);
  }
  return normalizeIp(request.socket.remoteAddress || '') || null;
}

function normalizeIp(address) {
  if (address.startsWith('::ffff:') && net.isIP(address.slice(7)) === 4)
    return address.slice(7);
  return net.isIP(address) ? address.toLowerCase() : null;
}

function attemptKey(address, packageId) {
  return (address || 'unknown') + ':' + packageId;
}

const downloadAttempts = new Map();

function getAttemptWindow(key, now) {
  let state = downloadAttempts.get(key);
  if (!state || now - state.startedAt >= ATTEMPT_WINDOW_MS) {
    state = { startedAt: now, failures: 0, blockedUntil: 0, lastActivity: now };
    downloadAttempts.set(key, state);
  }
  state.lastActivity = now;
  return state;
}

function pruneAttemptWindows(now) {
  for (const [key, state] of downloadAttempts) {
    if (state.blockedUntil <= now && now - state.lastActivity >= ATTEMPT_WINDOW_MS)
      downloadAttempts.delete(key);
  }
  if (downloadAttempts.size < MAX_ATTEMPT_KEYS) return;
  for (const [key, state] of downloadAttempts) {
    if (state.blockedUntil <= now) downloadAttempts.delete(key);
    if (downloadAttempts.size < MAX_ATTEMPT_KEYS) break;
  }
}

function isDownloadBlocked(key, now) {
  const state = downloadAttempts.get(key);
  if (!state) return 0;
  state.lastActivity = now;
  return state.blockedUntil > now ? Math.ceil((state.blockedUntil - now) / 1000) : 0;
}

function recordDownloadFailure(key, now) {
  pruneAttemptWindows(now);
  let state = getAttemptWindow(key, now);
  state.failures++;
  if (state.failures >= MAX_DOWNLOAD_ATTEMPTS) {
    state.blockedUntil = now + BLOCK_DURATION_MS;
    return Math.ceil(BLOCK_DURATION_MS / 1000);
  }
  if (downloadAttempts.size > MAX_ATTEMPT_KEYS) return BLOCK_DURATION_MS / 1000;
  return 0;
}

function recordDownloadSuccess(key) {
  downloadAttempts.delete(key);
}

function publicPackage(item) {
  return {
    packageId: item.package_id,
    campusId: item.campus_id,
    displayName: item.display_name,
    campusName: item.campus_name,
    computerPrefix: item.computer_prefix,
    schemaVersion: item.schema_version,
    targetOs: item.target_os,
    architecture: item.architecture,
    fileName: item.artifact_file_name,
    sizeBytes: item.artifact_size_bytes,
    sha256: item.artifact_sha256,
    downloadCount: item.download_count,
    publishedAt: item.published_at,
    requiresPhoneLast4: item.requires_phone_verification
  };
}

function routeIsSensitive(method, pathname) {
  return method === 'POST' && pathname.startsWith('/v1/deployment-packages');
}

function storageObjectPath(config, objectKey) {
  const encoded = objectKey.split('/').map(encodeURIComponent).join('/');
  return '/v1/storages/object/' + encodeURIComponent(config.bucketId) + '/' + encoded;
}

function rpcRows(value) {
  if (value === null) return [];
  if (!Array.isArray(value)) throw new Error('CloudBase returned an invalid RPC response.');
  return value;
}

function mapAssignment(row) {
  return {
    userId: row.user_id,
    campusId: row.campus_id,
    isActive: row.is_active,
    createdByUserId: row.created_by_user_id,
    createdAt: row.created_at
  };
}

async function getPublishableCampuses(config, userId) {
  const profileQuery = new URLSearchParams({
    select: 'role',
    user_id: 'eq.' + userId,
    limit: '1'
  });
  const profiles = await table(config, 'admin_profiles', profileQuery);
  if (profiles.some((profile) => profile.role === 'owner' || profile.role === 'admin')) {
    const query = new URLSearchParams({
      select: 'id,campus_name:name',
      status: 'eq.active',
      order: 'id.asc',
      limit: '200'
    });
    const campuses = await table(config, 'campuses', query);
    return campuses.map((campus) => ({
      campusId: campus.id,
      campusName: campus.campus_name
    }));
  }

  const assignmentsQuery = new URLSearchParams({
    select: 'campus_id',
    user_id: 'eq.' + userId,
    is_active: 'eq.true',
    limit: '200'
  });
  const assignments = await table(config, 'deployment_package_publishers', assignmentsQuery);
  const campusIds = [...new Set(assignments.map((row) => Number(row.campus_id)))].sort((a, b) => a - b);
  if (campusIds.length === 0) return [];
  const campusQuery = new URLSearchParams({
    select: 'id,campus_name:name',
    status: 'eq.active',
    id: 'in.(' + campusIds.join(',') + ')',
    order: 'id.asc',
    limit: '200'
  });
  const campuses = await table(config, 'campuses', campusQuery);
  return campuses.map((campus) => ({
    campusId: campus.id,
    campusName: campus.campus_name
  }));
}

function jsonBodyAllowed(request) {
  const mediaType = (request.headers['content-type'] || '').split(';', 1)[0].trim().toLowerCase();
  return mediaType === 'application/json';
}

async function requiredJsonBody(request, allowedFields, maxBytes, allowEmpty = false) {
  const bytes = await readRequestBody(request, maxBytes);
  if (!jsonBodyAllowed(request) && (bytes.length > 0 || !allowEmpty))
    throw new InvalidRequestError('请求必须使用 application/json。');
  return parseJsonObject(bytes, allowedFields, allowEmpty);
}

function mapError(response, error, context) {
  if (response.headersSent) {
    response.end();
    return;
  }
  if (error instanceof RequestTooLargeError) {
    sendProblem(response, 413, '请求正文超过大小限制。');
    return;
  }
  if (error instanceof InvalidRequestError || error instanceof InvalidPackageError) {
    sendJson(response, 400, { error: error.message });
    return;
  }
  if (error instanceof CloudBaseFailure) {
    if (context === 'download-artifact' && error.status === 404) {
      sendProblem(response, 502, 'The published artifact is temporarily unavailable.');
      return;
    }
    if (context === 'download-rpc' && error.status < 500) {
      sendJson(response, 404, { error: 'Published package not found or no longer available' });
      return;
    }
    if (context === 'publish' && /duplicate key|already exists/i.test(error.message)) {
      sendJson(response, 409, { error: 'This packageId has already been published' });
      return;
    }
    if (context === 'withdraw' && /not found or withdrawal is not authorized/i.test(error.message)) {
      sendJson(response, 404, { error: 'Published package not found or withdrawal is not authorized' });
      return;
    }
    if (context === 'publisher-write' && /only owner\/admin/i.test(error.message)) {
      sendJson(response, 403, { error: '当前账号没有管理教师发布权限。' });
      return;
    }
    if (context === 'publisher-write' && /campus does not exist/i.test(error.message)) {
      sendJson(response, 404, { error: 'Campus not found' });
      return;
    }
    if (context === 'publish' && error.status >= 400 && error.status < 500) {
      sendProblem(response, 502, 'CloudBase 拒绝了配置包发布。');
      return;
    }
  }
  if (context === 'publish' || context === 'withdraw' || context === 'publisher-write')
    sendProblem(response, 503, 'CloudBase 服务暂时不可用。');
  else if (context === 'download-artifact' &&
      error.message === 'Upstream response exceeded its size limit.')
    sendProblem(response, 502, 'The published artifact is temporarily unavailable.');
  else if (context === 'download-artifact')
    sendProblem(response, 503, 'CloudBase 服务暂时不可用。');
  else if (context === 'download')
    sendProblem(response, 503, 'CloudBase 服务暂时不可用。');
  else if (context === 'search')
    sendProblem(response, 503, '部署包目录暂时不可用。');
  else
    sendProblem(response, 503, 'CloudBase 服务暂时不可用。');
}

async function handleHeartbeat(request, response, config) {
  let body;
  try {
    body = await requiredJsonBody(request,
      new Set(['installationId', 'applicationVersion', 'deploymentId']), 2048);
  } catch (error) {
    mapError(response, error, 'heartbeat');
    return;
  }
  const installationId = body.installationId;
  if (typeof installationId !== 'string' || !/^[0-9a-f]{32}$/i.test(installationId)) {
    sendJson(response, 400, { error: 'installationId must be a 32-character hexadecimal value' });
    return;
  }
  const applicationVersion = body.applicationVersion == null ? 'unknown' : body.applicationVersion;
  if (typeof applicationVersion !== 'string' || applicationVersion.length > 64 ||
      !/^(?:unknown|[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:[-+][0-9A-Za-z.-]+)?)$/.test(applicationVersion)) {
    sendJson(response, 400, { error: 'applicationVersion must be a semantic numeric version' });
    return;
  }
  let deploymentId = null;
  if (body.deploymentId != null) {
    if (typeof body.deploymentId !== 'string' ||
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(body.deploymentId) ||
        /^0{8}-0{4}-0{4}-0{4}-0{12}$/i.test(body.deploymentId)) {
      sendJson(response, 400, { error: 'deploymentId must be a non-empty GUID when supplied' });
      return;
    }
    deploymentId = body.deploymentId.replace(/-/g, '').toLowerCase();
  }

  const day = hktDay();
  const digests = createHeartbeatDigests(config, day, installationId, deploymentId);
  try {
    await rpc(config, 'record_telemetry_heartbeat_v2', {
      p_day_hkt: day,
      p_installation_digest: digests.digest,
      p_deployment_digest: digests.deploymentDigest,
      p_application_version: applicationVersion,
      p_deployment_id: deploymentId
    }, 5000);
    noContent(response, { 'Cache-Control': 'no-store' });
  } catch (error) {
    mapError(response, error, 'heartbeat');
  }
}

async function handleSearch(request, response, config, url) {
  let campusId;
  let limit;
  let offset;
  const query = url.searchParams.get('query') || '';
  try {
    campusId = positiveInteger(url.searchParams.get('campusId'), 'campusId', false);
    if (query.length > 100 || /\p{Cc}/u.test(query))
      throw new InvalidRequestError('query must be at most 100 characters');
    limit = integerParameter(url.searchParams.get('limit'), DEFAULT_PAGE_SIZE, 1, MAX_PAGE_SIZE,
      'limit');
    offset = integerParameter(url.searchParams.get('offset'), 0, 0, 10000, 'offset');
  } catch (error) {
    mapError(response, error, 'search');
    return;
  }
  try {
    const rows = rpcRows(await rpc(config, 'search_deployment_packages', {
      p_query: query,
      p_campus_id: campusId,
      p_limit: limit + 1,
      p_offset: offset
    }));
    const hasMore = rows.length > limit;
    if (hasMore) rows.pop();
    sendJson(response, 200, {
      items: rows.map(publicPackage),
      limit,
      offset,
      hasMore
    });
  } catch (error) {
    mapError(response, error, 'search');
  }
}

async function handlePublish(request, response, config) {
  const contentType = request.headers['content-type'] || '';
  if (!/^multipart\/form-data(?:;|$)/i.test(contentType)) {
    sendJson(response, 400, {
      error: 'Use multipart/form-data with campusName, publisherName, teacherPhoneLast4 and either archive or files.'
    });
    return;
  }

  let form;
  let campusName;
  let publisherName;
  let canonical;
  let teacherPhoneLast4;
  try {
    const requestBytes = await readRequestBody(request, MAX_REQUEST_BYTES);
    form = parseMultipart(contentType, requestBytes);
    for (const key of form.fields.keys()) {
      if (key !== 'campusName' && key !== 'publisherName' && key !== 'teacherPhoneLast4')
        throw new InvalidRequestError('Only campusName, publisherName, teacherPhoneLast4 and the archive/files fields are accepted.');
    }
    for (const file of form.files) {
      if (file.name !== 'archive' && file.name !== 'files')
        throw new InvalidRequestError('Only the archive or files multipart field is accepted.');
    }
    campusName = (form.fields.get('campusName') || '').normalize('NFKC').trim();
    if (campusName.length < 1 || campusName.length > 100 || /\p{Cc}/u.test(campusName))
      throw new InvalidRequestError('校区名称必须为 1–100 个字符。');
    publisherName = (form.fields.get('publisherName') || '').normalize('NFKC').trim();
    if (publisherName.length < 1 || publisherName.length > 100 || /\p{Cc}/u.test(publisherName))
      throw new InvalidRequestError('教师姓名必须为 1–100 个字符。');
    teacherPhoneLast4 = form.fields.get('teacherPhoneLast4') || '';
    if (!/^[0-9]{4}$/.test(teacherPhoneLast4))
      throw new InvalidRequestError('教师手机号后四位必须是 4 位数字。');

    const archives = form.files.filter((file) => file.name === 'archive');
    const folderFiles = form.files.filter((file) => file.name === 'files');
    if (archives.length > 1 || (archives.length > 0 && folderFiles.length > 0))
      throw new InvalidRequestError('Upload either one archive or the files from one package folder.');
    if (archives.length === 1) {
      if (archives[0].bytes.length < 1 || archives[0].bytes.length > MAX_ARCHIVE_BYTES)
        throw new InvalidRequestError('ZIP archive must be between 1 byte and 64 KiB.');
      canonical = canonicalizeArchive(archives[0].bytes);
    } else {
      canonical = canonicalizeFolderFiles(folderFiles.map((file) => ({
        fileName: file.fileName,
        bytes: file.bytes
      })));
    }

    const objectKey = 'deployment-packages/v3/' + canonical.packageId + '.zip';
    const digest = crypto.createHash('sha256').update(canonical.archiveBytes).digest('hex').toUpperCase();
    const publisherFingerprint = identityFingerprint(config, 'publisher-name', publisherName);
    const phoneFingerprint = identityFingerprint(config, 'download-phone', teacherPhoneLast4);
    await cloudRequest(config, storageObjectPath(config, objectKey), {
      method: 'POST',
      bytes: canonical.archiveBytes,
      contentType: 'application/zip',
      noUpsert: true,
      timeoutMs: 30000,
      response: 'none'
    });
    try {
      await rpc(config, 'publish_deployment_package_public', {
        p_package_id: canonical.packageId,
        p_campus_name: campusName,
        p_publisher_name: publisherName,
        p_computer_prefix: canonical.computerPrefix,
        p_artifact_size_bytes: canonical.archiveBytes.length,
        p_artifact_sha256: digest,
        p_publisher_identity_fingerprint: publisherFingerprint,
        p_phone_fingerprint: phoneFingerprint
      }, 30000);
    } catch (error) {
      await cloudRequest(config, storageObjectPath(config, objectKey), {
        method: 'DELETE',
        response: 'none',
        timeoutMs: 10000
      }).catch(() => {});
      throw error;
    }

    const packageId = canonical.packageId;
    sendJson(response, 201, {
      packageId: packageId.slice(0, 8) + '-' + packageId.slice(8, 12) + '-' +
        packageId.slice(12, 16) + '-' + packageId.slice(16, 20) + '-' + packageId.slice(20),
      campusId: null,
      campusName,
      publisherName,
      computerPrefix: canonical.computerPrefix,
      schemaVersion: 3,
      targetOs: 'windows',
      architecture: 'x64',
      fileName: 'veyon-campus-config-v3-' + packageId + '.zip',
      sizeBytes: canonical.archiveBytes.length,
      sha256: digest
    }, { 'Cache-Control': 'no-store' });
  } catch (error) {
    mapError(response, error, 'publish');
  }
}

async function handleDownload(request, response, config, packageId) {
  let body;
  try {
    body = await requiredJsonBody(request, new Set(['teacherPhoneLast4']), 4096);
  } catch (error) {
    mapError(response, error, 'download-artifact');
    return;
  }
  const teacherPhoneLast4 = body.teacherPhoneLast4;
  if (typeof teacherPhoneLast4 !== 'string' || !/^[0-9]{4}$/.test(teacherPhoneLast4)) {
    sendJson(response, 400, { error: '教师手机号后四位必须是 4 位数字。' });
    return;
  }
  const key = attemptKey(downloadClientAddress(request), packageId);
  const now = Date.now();
  const retryAfter = isDownloadBlocked(key, now);
  if (retryAfter > 0) {
    sendJson(response, 429, { error: 'Too Many Requests' }, {
      'Retry-After': String(retryAfter),
      'Cache-Control': 'no-store'
    });
    return;
  }

  const fingerprint = identityFingerprint(config, 'download-phone', teacherPhoneLast4);
  let artifact;
  try {
    const rows = rpcRows(await rpc(config, 'get_deployment_package_download_with_phone', {
      p_package_id: packageId,
      p_phone_fingerprint: fingerprint
    }));
    artifact = rows[0] || null;
  } catch (error) {
    mapError(response, error, 'download-rpc');
    return;
  }
  if (!artifact) {
    const blocked = recordDownloadFailure(key, Date.now());
    if (blocked > 0) {
      sendJson(response, 429, { error: 'Too Many Requests' }, {
        'Retry-After': String(blocked),
        'Cache-Control': 'no-store'
      });
      return;
    }
    sendProblem(response, 403, '教师手机号后四位不正确，或此配置包已撤回。');
    return;
  }

  recordDownloadSuccess(key);
  const expectedKey = 'deployment-packages/v3/' + packageId + '.zip';
  if (artifact.storage_key !== expectedKey ||
      !Number.isInteger(artifact.artifact_size_bytes) ||
      artifact.artifact_size_bytes < 1 || artifact.artifact_size_bytes > MAX_ARCHIVE_BYTES ||
      typeof artifact.artifact_sha256 !== 'string' ||
      !/^[0-9a-f]{64}$/i.test(artifact.artifact_sha256)) {
    sendProblem(response, 502, 'The published artifact is temporarily unavailable.');
    return;
  }

  let bytes;
  try {
    bytes = await cloudRequest(config, storageObjectPath(config, artifact.storage_key), {
      method: 'GET',
      response: 'bytes',
      maximumBytes: MAX_ARCHIVE_BYTES,
      timeoutMs: 30000
    });
  } catch (error) {
    mapError(response, error, 'download');
    return;
  }
  const expectedDigest = Buffer.from(artifact.artifact_sha256, 'hex');
  const actualDigest = crypto.createHash('sha256').update(bytes).digest();
  if (bytes.length !== artifact.artifact_size_bytes ||
      !crypto.timingSafeEqual(actualDigest, expectedDigest)) {
    sendProblem(response, 502, 'The published artifact is temporarily unavailable.');
    return;
  }
  try {
    await rpc(config, 'record_deployment_package_download', { p_package_id: packageId }, 10000);
  } catch (error) {
    mapError(response, error, 'download');
    return;
  }
  const filename = 'veyon-campus-config-v3-' + packageId + '.zip';
  response.writeHead(200, {
    ...CORS_HEADERS,
    'Cache-Control': 'no-store',
    'Content-Type': 'application/zip',
    'Content-Disposition': 'attachment; filename="' + filename + '"',
    'Content-Length': bytes.length
  });
  response.end(bytes);
}

async function handleWithdraw(request, response, config, packageId) {
  const token = bearerToken(request);
  if (!token) {
    sendJson(response, 401, { error: 'Unauthorized' });
    return;
  }
  let body;
  try {
    body = await requiredJsonBody(request, new Set(['reason']), 16 * 1024, true);
  } catch (error) {
    mapError(response, error, 'withdraw');
    return;
  }
  if (body.reason != null &&
      (typeof body.reason !== 'string' || body.reason.length > 500 ||
        /\p{Cc}/u.test(body.reason))) {
    sendJson(response, 400, {
      error: 'reason must be at most 500 characters and contain no control characters'
    });
    return;
  }
  try {
    const user = await getCurrentUser(config, token);
    if (!user) {
      sendJson(response, 401, { error: 'Unauthorized' });
      return;
    }
    await rpc(config, 'withdraw_deployment_package', {
      p_package_id: packageId,
      p_actor_user_id: user.userId,
      p_reason: body.reason || ''
    });
    noContent(response, { 'Cache-Control': 'no-store' });
  } catch (error) {
    mapError(response, error, 'withdraw');
  }
}

async function handleMine(request, response, config) {
  const token = bearerToken(request);
  if (!token) {
    sendJson(response, 401, { error: 'Unauthorized' });
    return;
  }
  try {
    const user = await getCurrentUser(config, token);
    if (!user) {
      sendJson(response, 401, { error: 'Unauthorized' });
      return;
    }
    const rows = rpcRows(await rpc(config, 'search_deployment_packages_by_publisher', {
      p_publisher_fingerprint: identityFingerprint(config, 'publisher-user', user.userId),
      p_limit: 49,
      p_offset: 0
    }));
    sendJson(response, 200, { items: rows.map(publicPackage) }, { 'Cache-Control': 'no-store' });
  } catch (error) {
    mapError(response, error, 'mine');
  }
}

async function handleMyCampuses(request, response, config) {
  const token = bearerToken(request);
  if (!token) {
    sendJson(response, 401, { error: 'Unauthorized' });
    return;
  }
  try {
    const user = await getCurrentUser(config, token);
    if (!user) {
      sendJson(response, 401, { error: 'Unauthorized' });
      return;
    }
    const campuses = await getPublishableCampuses(config, user.userId);
    sendJson(response, 200, { items: campuses }, { 'Cache-Control': 'no-store' });
  } catch (error) {
    mapError(response, error, 'campuses');
  }
}

async function handlePublisherAssignments(request, response, config, url) {
  const token = bearerToken(request);
  if (!token) {
    sendJson(response, 401, { error: 'Unauthorized' });
    return;
  }
  let campusId;
  try {
    campusId = positiveInteger(url.searchParams.get('campusId'), 'campusId', true);
  } catch (error) {
    mapError(response, error, 'publisher-read');
    return;
  }
  try {
    const user = await getCurrentUser(config, token);
    if (!user) {
      sendJson(response, 401, { error: 'Unauthorized' });
      return;
    }
    const query = new URLSearchParams({
      select: 'role',
      user_id: 'eq.' + user.userId,
      limit: '1'
    });
    const profiles = await table(config, 'admin_profiles', query);
    if (!profiles.some((profile) => profile.role === 'owner' || profile.role === 'admin')) {
      sendJson(response, 403, { error: '当前账号没有管理教师发布权限。' });
      return;
    }
    const assignmentsQuery = new URLSearchParams({
      select: 'user_id,campus_id,is_active,created_by_user_id,created_at',
      campus_id: 'eq.' + campusId,
      order: 'created_at.desc',
      limit: '200'
    });
    const assignments = await table(config, 'deployment_package_publishers', assignmentsQuery);
    sendJson(response, 200, { items: assignments.map(mapAssignment) }, {
      'Cache-Control': 'no-store'
    });
  } catch (error) {
    mapError(response, error, 'publisher-read');
  }
}

async function handleSetPublisher(request, response, config) {
  const token = bearerToken(request);
  if (!token) {
    sendJson(response, 401, { error: 'Unauthorized' });
    return;
  }
  let body;
  try {
    body = await requiredJsonBody(request, new Set(['userId', 'campusId', 'isActive']), 16 * 1024);
  } catch (error) {
    mapError(response, error, 'publisher-write');
    return;
  }
  const userId = typeof body.userId === 'string' ? body.userId.trim() : '';
  let campusId;
  try {
    campusId = positiveInteger(body.campusId, 'campusId', true);
  } catch (error) {
    mapError(response, error, 'publisher-write');
    return;
  }
  if (userId.length < 1 || userId.length > 64 || /\p{Cc}/u.test(userId) ||
      typeof body.isActive !== 'boolean') {
    sendJson(response, 400, {
      error: 'userId must be between 1 and 64 characters; campusId and isActive are required'
    });
    return;
  }
  try {
    const user = await getCurrentUser(config, token);
    if (!user) {
      sendJson(response, 401, { error: 'Unauthorized' });
      return;
    }
    await rpc(config, 'set_deployment_package_publisher', {
      p_user_id: userId,
      p_campus_id: campusId,
      p_is_active: body.isActive,
      p_actor_user_id: user.userId
    });
    noContent(response, { 'Cache-Control': 'no-store' });
  } catch (error) {
    mapError(response, error, 'publisher-write');
  }
}

function createRequestHandler(config) {
  return async (request, response) => {
    response.setHeader('Access-Control-Allow-Origin', '*');
    response.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
    response.setHeader('Access-Control-Allow-Headers', 'Accept, Authorization, Content-Type');
    response.setHeader('Access-Control-Max-Age', '600');
    try {
      const url = new URL(request.url || '/', 'http://127.0.0.1');
      const pathname = url.pathname;
      if (routeIsSensitive(request.method, pathname))
        response.setHeader('Cache-Control', 'no-store');
      if (request.method === 'OPTIONS') {
        noContent(response, CORS_HEADERS);
        return;
      }
      if (request.method === 'GET' && pathname === '/health') {
        sendJson(response, 200, { status: 'ready' });
        return;
      }
      if (request.method === 'POST' && pathname === '/v1/heartbeat') {
        await handleHeartbeat(request, response, config);
        return;
      }
      if (pathname === '/v1/deployment-packages' && request.method === 'GET') {
        await handleSearch(request, response, config, url);
        return;
      }
      if (pathname === '/v1/deployment-packages' && request.method === 'POST') {
        await handlePublish(request, response, config);
        return;
      }
      const download = /^\/v1\/deployment-packages\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/download$/i.exec(pathname);
      if (download && request.method === 'POST') {
        const id = download[1].toLowerCase();
        const packageId = id.replace(/-/g, '');
        await handleDownload(request, response, config, packageId);
        return;
      }
      const withdraw = /^\/v1\/deployment-packages\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/withdraw$/i.exec(pathname);
      if (withdraw && request.method === 'POST') {
        const id = withdraw[1].toLowerCase();
        const packageId = id.replace(/-/g, '');
        await handleWithdraw(request, response, config, packageId);
        return;
      }
      sendJson(response, 404, { error: 'Not Found' });
    } catch (error) {
      mapError(response, error, 'request');
    }
  };
}

function start() {
  const config = loadConfig();
  const server = http.createServer(createRequestHandler(config));
  server.requestTimeout = 30000;
  server.headersTimeout = 10000;
  server.maxHeadersCount = 100;
  server.listen(9000, '0.0.0.0');
}

if (require.main === module) start();

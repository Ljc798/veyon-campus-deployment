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
const {
  createCampusPackageFileName,
  createCampusPackageObjectKey,
  isSafeCampusPackageFileName
} = require('./package-naming');

const MAX_REQUEST_BYTES = 128 * 1024;
const DEFAULT_PAGE_SIZE = 20;
const MAX_PAGE_SIZE = 49;
const APPLICATION_RELEASE_MAX_BYTES = 512 * 1024 * 1024;
const APPLICATION_RELEASE_SIGNATURE_ALGORITHM = 'RSA-PSS-SHA256';
const APPLICATION_RELEASE_ROLES = new Set(['TeacherConsole', 'StudentSetup']);
const APPLICATION_RELEASE_VERSION_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;
const GITHUB_RELEASE_OWNER = 'Ljc798';
const GITHUB_RELEASE_REPOSITORY = 'veyon-campus-deployment';
const GITHUB_RELEASE_WORKFLOW = 'windows-installers.yml';
const GITHUB_RELEASE_WORKFLOW_URL = `https://github.com/${GITHUB_RELEASE_OWNER}/${GITHUB_RELEASE_REPOSITORY}/actions/workflows/${GITHUB_RELEASE_WORKFLOW}`;
const GITHUB_API_BASE_URL = 'https://api.github.com';
const GITHUB_API_VERSION = '2026-03-10';
const MAX_ADMIN_DATABASE_PAGE_SIZE = 50;
const MAX_ADMIN_DATABASE_OFFSET = 1000000;
const ADMIN_DATABASE_TABLES = Object.freeze({
  admin_profiles: {
    columns: 'user_id,display_name,role,created_at',
    order: 'created_at.desc,user_id.asc',
    redact: []
  },
  application_releases: {
    columns: 'release_id,role,product,version,architecture,file_name,object_key,size_bytes,sha256,signature_algorithm,signature,status,published_at,created_at',
    order: 'published_at.desc,release_id.asc',
    redact: ['object_key']
  },
  campus_daily_teacher_heartbeats: {
    columns: 'day_hkt,campus_identity_digest,campus_id,package_id,publisher_digest,teacher_version,student_version,configured_computer_count,updated_at',
    order: 'day_hkt.desc,campus_identity_digest.asc',
    redact: ['campus_identity_digest', 'publisher_digest']
  },
  campuses: {
    columns: 'id,name,region,city,status,created_at,updated_at',
    order: 'updated_at.desc,id.asc',
    redact: []
  },
  deployment_package_artifacts: {
    columns: 'package_id,storage_key,created_at',
    order: 'created_at.desc,package_id.asc',
    redact: ['storage_key']
  },
  deployment_package_download_attempts: {
    columns: 'package_id,client_fingerprint,window_started_at,failure_count,blocked_until,last_attempt_at',
    order: 'last_attempt_at.desc,package_id.asc,client_fingerprint.asc',
    redact: ['client_fingerprint']
  },
  deployment_packages: {
    columns: 'package_id,campus_id,campus_name,computer_prefix,display_name,schema_version,target_os,architecture,artifact_size_bytes,artifact_sha256,status,created_by_user_id,created_at,published_at,download_count,withdrawn_at,withdrawn_by_user_id,withdrawn_reason,publisher_identity_fingerprint,publisher_phone_fingerprint,publisher_name,artifact_file_name',
    order: 'created_at.desc,package_id.asc',
    redact: ['publisher_identity_fingerprint', 'publisher_phone_fingerprint']
  },
  telemetry_daily_deployment_devices: {
    columns: 'day_hkt,campus_id,deployment_id,application_version,installation_digest,recorded_at',
    order: 'recorded_at.desc,day_hkt.desc,campus_id.asc,deployment_id.asc,application_version.asc,installation_digest.asc',
    redact: ['installation_digest']
  },
  telemetry_daily_deployment_stats: {
    columns: 'day_hkt,campus_id,deployment_id,application_version,unique_devices,heartbeat_signals,updated_at',
    order: 'day_hkt.desc,campus_id.asc,deployment_id.asc,application_version.asc',
    redact: []
  },
  telemetry_daily_hkt_devices: {
    columns: 'day_hkt,installation_digest,recorded_at',
    order: 'recorded_at.desc,day_hkt.desc,installation_digest.asc',
    redact: ['installation_digest']
  },
  telemetry_daily_hkt_stats: {
    columns: 'day_hkt,unique_devices,heartbeat_signals,updated_at',
    order: 'day_hkt.desc',
    redact: []
  },
  telemetry_hkt_retention_state: {
    columns: 'id,last_cleanup_hkt',
    order: 'id.asc',
    redact: []
  }
});

class CloudBaseFailure extends Error {
  constructor(status, message) {
    super(message || 'CloudBase request failed.');
    this.status = status;
  }
}

class GitHubApiFailure extends Error {
  constructor(status, message) {
    super(message || 'GitHub release service is temporarily unavailable.');
    this.status = status;
  }
}

function isDefinitivePublishRejection(error) {
  return error instanceof CloudBaseFailure &&
    [400, 401, 403, 404, 409, 413, 415, 422].includes(error.status);
}

function loadConfig(environment = process.env) {
  const envId = (environment.CloudBase__EnvId || '').trim();
  const apiKey = (environment.CloudBase__ApiKey || '').trim();
  const encodedHashKey = (environment.Telemetry__DailyHashKey || '').trim();
  const bucketId = (environment.CloudBase__DeploymentPackageBucket ||
    'deployment-package-artifacts').trim();
  const releaseBucketId = (environment.CloudBase__ApplicationReleaseBucket ||
    'application-release-artifacts').trim();
  const publicApiBaseUrl = (environment.CloudBase__ApplicationReleasePublicBaseUrl ||
    'https://veyon-control-d3gs8hmuyd09c00a7-1348081197.ap-shanghai.app.tcloudbase.com/').trim();
  const githubReleaseDispatchToken = (environment.GITHUB_RELEASE_DISPATCH_TOKEN || '').trim();
  if (!/^[A-Za-z0-9-]+$/.test(envId) || !apiKey)
    throw new Error('CloudBase server configuration is incomplete.');
  if (!/^[A-Za-z0-9+/]*={0,2}$/.test(encodedHashKey))
    throw new Error('Telemetry key configuration is invalid.');
  const hashKey = Buffer.from(encodedHashKey, 'base64');
  if (hashKey.length < 32 || hashKey.toString('base64') !== encodedHashKey)
    throw new Error('Telemetry key configuration is invalid.');
  if (!/^[A-Za-z0-9-]+$/.test(bucketId))
    throw new Error('CloudBase storage bucket configuration is invalid.');
  if (!/^[A-Za-z0-9-]+$/.test(releaseBucketId))
    throw new Error('Application release bucket configuration is invalid.');
  let parsedPublicApiBaseUrl;
  try {
    parsedPublicApiBaseUrl = new URL(publicApiBaseUrl);
  } catch {
    throw new Error('Application release public API URL configuration is invalid.');
  }
  if ((parsedPublicApiBaseUrl.protocol !== 'https:' &&
      !(parsedPublicApiBaseUrl.protocol === 'http:' && parsedPublicApiBaseUrl.hostname === '127.0.0.1')) ||
      parsedPublicApiBaseUrl.username || parsedPublicApiBaseUrl.password ||
      parsedPublicApiBaseUrl.search || parsedPublicApiBaseUrl.hash ||
      !['', '/'].includes(parsedPublicApiBaseUrl.pathname))
    throw new Error('Application release public API URL configuration is invalid.');

  const apiBase = 'https://' + envId + '.api.tcloudbasegateway.com';
  return {
    envId,
    apiKey,
    hashKey,
    bucketId,
    releaseBucketId,
    githubReleaseDispatchToken,
    githubReleaseWorkflowUrl: GITHUB_RELEASE_WORKFLOW_URL,
    publicApiBaseUrl: parsedPublicApiBaseUrl.href.endsWith('/')
      ? parsedPublicApiBaseUrl.href
      : parsedPublicApiBaseUrl.href + '/',
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

function parseSemanticVersion(value) {
  if (typeof value !== 'string' || value.length > 64 || !APPLICATION_RELEASE_VERSION_PATTERN.test(value))
    return null;
  const [withoutBuild] = value.split('+', 1);
  const prereleaseStart = withoutBuild.indexOf('-');
  const core = prereleaseStart < 0 ? withoutBuild : withoutBuild.slice(0, prereleaseStart);
  const prerelease = prereleaseStart < 0 ? null : withoutBuild.slice(prereleaseStart + 1);
  return {
    core: core.split('.').map((part) => BigInt(part)),
    prerelease: prerelease === null ? null : prerelease.split('.')
  };
}

function compareSemanticVersions(left, right) {
  const leftVersion = parseSemanticVersion(left);
  const rightVersion = parseSemanticVersion(right);
  if (!leftVersion || !rightVersion) throw new InvalidRequestError('Release version must be semantic version X.Y.Z.');
  for (let index = 0; index < 3; index++) {
    if (leftVersion.core[index] !== rightVersion.core[index])
      return leftVersion.core[index] < rightVersion.core[index] ? -1 : 1;
  }
  if (leftVersion.prerelease === null || rightVersion.prerelease === null) {
    if (leftVersion.prerelease === rightVersion.prerelease) return 0;
    return leftVersion.prerelease === null ? 1 : -1;
  }
  const length = Math.max(leftVersion.prerelease.length, rightVersion.prerelease.length);
  for (let index = 0; index < length; index++) {
    const leftPart = leftVersion.prerelease[index];
    const rightPart = rightVersion.prerelease[index];
    if (leftPart === undefined || rightPart === undefined) {
      if (leftPart === rightPart) return 0;
      return leftPart === undefined ? -1 : 1;
    }
    if (leftPart === rightPart) continue;
    const leftNumeric = /^(0|[1-9][0-9]*)$/.test(leftPart);
    const rightNumeric = /^(0|[1-9][0-9]*)$/.test(rightPart);
    if (leftNumeric && rightNumeric) {
      const difference = BigInt(leftPart) - BigInt(rightPart);
      if (difference !== 0n) return difference < 0n ? -1 : 1;
    } else if (leftNumeric !== rightNumeric) {
      return leftNumeric ? -1 : 1;
    } else {
      return leftPart < rightPart ? -1 : 1;
    }
  }
  return 0;
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
      typeof result.status !== 'string' || result.status.toUpperCase() !== 'ACTIVE')
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
  const forwardedAddresses = [];
  for (let index = 0; index < request.rawHeaders.length; index += 2) {
    if (request.rawHeaders[index].toLowerCase() === 'x-forwarded-for') {
      forwardedAddresses.push(...request.rawHeaders[index + 1].split(',').map(address => address.trim()));
    }
  }
  const forwardedAddress = forwardedAddresses.at(-1);
  if (forwardedAddress && net.isIP(forwardedAddress)) return normalizeIp(forwardedAddress);
  return normalizeIp(request.socket.remoteAddress || '') || null;
}

function normalizeIp(address) {
  if (address.startsWith('::ffff:') && net.isIP(address.slice(7)) === 4)
    return address.slice(7);
  return net.isIP(address) ? address.toLowerCase() : null;
}

function publicPackage(item) {
  return {
    packageId: item.package_id,
    campusId: item.campus_id,
    displayName: item.campus_name,
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
  return pathname.startsWith('/v1/releases') || pathname.startsWith('/v1/admin/') ||
    pathname === '/v1/heartbeat/teacher' ||
    method === 'POST' && pathname.startsWith('/v1/deployment-packages');
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
  if (error instanceof GitHubApiFailure) {
    sendJson(response, error.status, { error: error.message }, { 'Cache-Control': 'no-store' });
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
    if (context === 'publish' && error.status >= 400 && error.status < 500) {
      sendProblem(response, 502, 'CloudBase 拒绝了配置包发布。');
      return;
    }
  }
  if (context === 'publish' || context === 'withdraw')
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
  else if (context === 'admin-release')
    sendJson(response, 503, { error: '版本发布服务暂时不可用。' }, { 'Cache-Control': 'no-store' });
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

function normalizeReleaseRow(row) {
  if (!row || typeof row !== 'object' || !APPLICATION_RELEASE_ROLES.has(row.role)) return null;
  const releaseId = typeof row.release_id === 'string' ? row.release_id.toLowerCase() : '';
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(releaseId))
    return null;
  const roleProduct = row.role === 'TeacherConsole' ? 'VeyonCampus.TeacherConsole' : 'VeyonCampus.StudentSetup';
  const roleFile = row.role === 'TeacherConsole' ? 'Teacher' : 'Student';
  const version = row.version;
  if (row.product !== roleProduct || row.architecture !== 'win-x64' ||
      !parseSemanticVersion(version) || row.signature_algorithm !== APPLICATION_RELEASE_SIGNATURE_ALGORITHM ||
      typeof row.file_name !== 'string' ||
      row.file_name !== `VeyonCampus-${roleFile}-Setup-${version}-win-x64.exe` ||
      !Number.isSafeInteger(Number(row.size_bytes)) || Number(row.size_bytes) < 1 ||
      Number(row.size_bytes) > APPLICATION_RELEASE_MAX_BYTES ||
      typeof row.sha256 !== 'string' || !/^[A-F0-9]{64}$/.test(row.sha256) ||
      typeof row.signature !== 'string' || row.signature.length > 8192) return null;
  const signatureBytes = Buffer.from(row.signature, 'base64');
  if (signatureBytes.length < 256 || signatureBytes.toString('base64') !== row.signature) return null;
  const normalizedId = releaseId.replace(/-/g, '');
  if (row.object_key !== `releases/${row.role}/win-x64/${normalizedId}.exe`) return null;
  return {
    releaseId,
    product: roleProduct,
    role: row.role,
    version,
    architecture: 'win-x64',
    fileName: row.file_name,
    sizeBytes: Number(row.size_bytes),
    sha256: row.sha256,
    signature: row.signature,
    signatureAlgorithm: APPLICATION_RELEASE_SIGNATURE_ALGORITHM,
    objectKey: row.object_key,
    publishedAt: row.published_at
  };
}

function makeReleaseManifest(config, release) {
  return {
    schemaVersion: 1,
    product: release.product,
    role: release.role,
    version: release.version,
    architecture: release.architecture,
    fileName: release.fileName,
    sizeBytes: release.sizeBytes,
    sha256: release.sha256,
    downloadUrl: new URL(`v1/releases/${release.releaseId}/artifact`, config.publicApiBaseUrl).href
  };
}

async function readReleaseRows(config, query) {
  return table(config, 'application_releases', query);
}

async function readLatestReleaseEnvelopes(config, roles) {
  const query = new URLSearchParams({
    select: 'release_id,role,product,version,architecture,file_name,object_key,size_bytes,sha256,signature_algorithm,signature,published_at',
    role: roles.length === 1 ? 'eq.' + roles[0] : 'in.(' + roles.join(',') + ')',
    architecture: 'eq.win-x64',
    status: 'eq.published',
    order: 'published_at.desc',
    limit: '1000'
  });
  const rows = await readReleaseRows(config, query);
  const releases = rows.map(normalizeReleaseRow).filter(Boolean);
  releases.sort((left, right) => compareSemanticVersions(right.version, left.version) ||
    Date.parse(right.publishedAt) - Date.parse(left.publishedAt));
  return Object.fromEntries(roles.map((role) => {
    const latest = releases.find((release) => release.role === role);
    return [role, latest ? {
      manifest: makeReleaseManifest(config, latest),
      signatureAlgorithm: latest.signatureAlgorithm,
      signature: latest.signature,
      publishedAt: latest.publishedAt
    } : null];
  }));
}

async function handleLatestRelease(request, response, config, url) {
  const role = url.searchParams.get('role');
  const architecture = url.searchParams.get('architecture') || 'win-x64';
  if (!APPLICATION_RELEASE_ROLES.has(role)) {
    sendJson(response, 400, { error: 'role must be TeacherConsole or StudentSetup' });
    return;
  }
  if (architecture !== 'win-x64') {
    sendJson(response, 400, { error: 'architecture must be win-x64' });
    return;
  }

  try {
    const releases = await readLatestReleaseEnvelopes(config, [role]);
    sendJson(response, 200, { release: releases[role] }, { 'Cache-Control': 'no-store' });
  } catch (error) {
    mapError(response, error, 'release-catalog');
  }
}

async function handleReleaseArtifact(request, response, config, releaseId) {
  try {
    const query = new URLSearchParams({
      select: 'release_id,role,product,version,architecture,file_name,object_key,size_bytes,sha256,signature_algorithm,signature,published_at',
      release_id: 'eq.' + releaseId,
      status: 'eq.published',
      limit: '1'
    });
    const rows = await readReleaseRows(config, query);
    const release = normalizeReleaseRow(rows[0]);
    if (!release) {
      sendJson(response, 404, { error: 'Published release not found' });
      return;
    }
    const signedRows = await cloudRequest(config,
      '/v1/storages/object/sign/' + encodeURIComponent(config.releaseBucketId), {
        method: 'POST',
        body: { expiresIn: 600, paths: [release.objectKey] },
        timeoutMs: 15000,
        maximumBytes: 16 * 1024
      });
    const entries = Array.isArray(signedRows) ? signedRows : signedRows?.data;
    const signedRow = Array.isArray(entries)
      ? entries.find((entry) => entry.path === release.objectKey)
      : null;
    if (!signedRow || typeof signedRow.signedURL !== 'string' || signedRow.error) {
      sendJson(response, 503, { error: 'Application release artifact is temporarily unavailable' });
      return;
    }
    const signedUrl = new URL(signedRow.signedURL, config.apiBase);
    if (signedUrl.protocol !== 'https:' &&
        !(signedUrl.protocol === 'http:' && signedUrl.hostname === '127.0.0.1'))
      throw new Error('CloudBase returned an invalid signed release URL.');
    if (signedUrl.username || signedUrl.password || signedUrl.hash)
      throw new Error('CloudBase returned an invalid signed release URL.');
    response.writeHead(302, {
      ...CORS_HEADERS,
      'Cache-Control': 'no-store',
      'Content-Length': '0',
      Location: signedUrl.href
    });
    response.end();
  } catch (error) {
    mapError(response, error, 'release-artifact');
  }
}

async function handleTeacherHeartbeat(request, response, config) {
  let body;
  try {
    body = await requiredJsonBody(request,
      new Set(['publisherInstanceId', 'packageId', 'teacherVersion', 'studentVersion', 'configuredComputerCount']), 2048);
  } catch (error) {
    mapError(response, error, 'teacher-heartbeat');
    return;
  }
  if (typeof body.publisherInstanceId !== 'string' || !/^[0-9a-f]{32}$/i.test(body.publisherInstanceId)) {
    sendJson(response, 400, { error: 'publisherInstanceId must be a 32-character hexadecimal value' });
    return;
  }
  if (typeof body.packageId !== 'string' ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(body.packageId)) {
    sendJson(response, 400, { error: 'packageId must be a non-empty GUID' });
    return;
  }
  if (!parseSemanticVersion(body.teacherVersion) || !parseSemanticVersion(body.studentVersion)) {
    sendJson(response, 400, { error: 'teacherVersion and studentVersion must be semantic versions' });
    return;
  }
  if (!Number.isInteger(body.configuredComputerCount) ||
      body.configuredComputerCount < 0 || body.configuredComputerCount > 150) {
    sendJson(response, 400, { error: 'configuredComputerCount must be between 0 and 150' });
    return;
  }

  const packageQuery = new URLSearchParams({
    select: 'campus_id,campus_name,computer_prefix,status',
    package_id: 'eq.' + body.packageId.toLowerCase(),
    status: 'eq.published',
    limit: '1'
  });
  const day = hktDay();
  const dayKey = crypto.createHmac('sha256', config.hashKey)
    .update('VeyonCampus/TeacherHeartbeat/v1\n' + day, 'ascii').digest();
  const publisherDigest = hmacHex(dayKey, Buffer.from(body.publisherInstanceId.toLowerCase(), 'ascii'));
  dayKey.fill(0);
  try {
    const packageRows = await table(config, 'deployment_packages', packageQuery);
    const publishedPackage = packageRows[0];
    if (!publishedPackage) {
      sendJson(response, 404, { error: 'Published package not found' }, { 'Cache-Control': 'no-store' });
      return;
    }
    let campusIdentity;
    if (publishedPackage.campus_id !== null && publishedPackage.campus_id !== undefined) {
      const campusId = positiveInteger(String(publishedPackage.campus_id), 'campusId', true);
      campusIdentity = 'registered:' + campusId;
    } else {
      if (typeof publishedPackage.campus_name !== 'string' ||
          typeof publishedPackage.computer_prefix !== 'string')
        throw new Error('Published package campus metadata is invalid.');
      const campusName = publishedPackage.campus_name.normalize('NFKC').trim().toLowerCase();
      const computerPrefix = publishedPackage.computer_prefix.trim().toUpperCase();
      if (!campusName || !computerPrefix)
        throw new Error('Published package campus metadata is invalid.');
      campusIdentity = 'anonymous:' + campusName + '\n' + computerPrefix;
    }
    const campusIdentityDigest = hmacHex(config.hashKey,
      'VeyonCampus/TeacherHeartbeat/Campus/v1\n' + campusIdentity);
    let latestReleases = { teacherConsole: null, studentSetup: null };
    try {
      const releases = await readLatestReleaseEnvelopes(config, ['TeacherConsole', 'StudentSetup']);
      latestReleases = {
        teacherConsole: releases.TeacherConsole,
        studentSetup: releases.StudentSetup
      };
    } catch {
      // Version lookup is optional: a release catalog outage must not block the daily campus heartbeat.
    }
    await rpc(config, 'record_campus_teacher_heartbeat_v1', {
      p_day_hkt: day,
      p_publisher_digest: publisherDigest,
      p_package_id: body.packageId.toLowerCase(),
      p_campus_identity_digest: campusIdentityDigest,
      p_teacher_version: body.teacherVersion,
      p_student_version: latestReleases.studentSetup?.manifest.version || body.studentVersion,
      p_configured_computer_count: body.configuredComputerCount
    }, 5000);
    sendJson(response, 200, { latestReleases }, { 'Cache-Control': 'no-store' });
  } catch (error) {
    mapError(response, error, 'teacher-heartbeat');
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
    if (canonical.campus.normalize('NFKC').trim() !== campusName)
      throw new InvalidRequestError('上传表单校区名称必须与配置包 manifest.json 一致。');

    const objectKey = createCampusPackageObjectKey(campusName, canonical.packageId);
    const fileName = createCampusPackageFileName(campusName, canonical.packageId);
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
      if (isDefinitivePublishRejection(error)) {
        await cloudRequest(config, storageObjectPath(config, objectKey), {
          method: 'DELETE',
          response: 'none',
          timeoutMs: 10000
        }).catch(() => {});
      }
      throw error;
    }

    const packageId = canonical.packageId;
    sendJson(response, 201, {
      packageId: packageId.slice(0, 8) + '-' + packageId.slice(8, 12) + '-' +
        packageId.slice(12, 16) + '-' + packageId.slice(16, 20) + '-' + packageId.slice(20),
      campusId: null,
      campusName,
      computerPrefix: canonical.computerPrefix,
      schemaVersion: 3,
      targetOs: 'windows',
      architecture: 'x64',
      fileName,
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
  const address = downloadClientAddress(request) || 'unknown';
  let downloadAuthorization;
  try {
    const rows = rpcRows(await rpc(config, 'get_deployment_package_download_with_rate_limit', {
      p_package_id: packageId,
      p_phone_fingerprint: identityFingerprint(config, 'download-phone', teacherPhoneLast4),
      p_client_fingerprint: identityFingerprint(config, 'download-source', address)
    }));
    downloadAuthorization = rows[0] || null;
  } catch (error) {
    mapError(response, error, 'download-rpc');
    return;
  }
  if (downloadAuthorization?.decision === 'blocked') {
    const retryAfter = Number(downloadAuthorization.retry_after_seconds);
    if (!Number.isSafeInteger(retryAfter) || retryAfter < 1 || retryAfter > 900) {
      sendProblem(response, 502, 'The published artifact is temporarily unavailable.');
      return;
    }
    sendJson(response, 429, { error: 'Too Many Requests' }, {
      'Retry-After': String(retryAfter),
      'Cache-Control': 'no-store'
    });
    return;
  }
  if (downloadAuthorization?.decision !== 'authorized') {
    sendProblem(response, 403, '教师手机号后四位不正确，或此配置包已撤回。');
    return;
  }

  const artifact = downloadAuthorization;
  const compactPackageId = packageId.replace(/-/g, '').toLowerCase();
  const legacyKey = `deployment-packages/v3/${compactPackageId}.zip`;
  const legacyFileName = `veyon-campus-config-v3-${compactPackageId}.zip`;
  const fileName = artifact.artifact_file_name;
  const isNamedFile = isSafeCampusPackageFileName(fileName, compactPackageId);
  const isLegacyFile = fileName === legacyFileName;
  const expectedNamedKey = isNamedFile ? `deployment-packages/v3/${fileName}` : null;
  if ((!isNamedFile && !isLegacyFile) ||
      (artifact.storage_key !== expectedNamedKey && artifact.storage_key !== legacyKey) ||
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
  const encodedFileName = encodeURIComponent(fileName).replace(/[!'()*]/g, (character) =>
    '%' + character.charCodeAt(0).toString(16).toUpperCase());
  response.writeHead(200, {
    ...CORS_HEADERS,
    'Cache-Control': 'no-store',
    'Content-Type': 'application/zip',
    'Content-Disposition': 'attachment; filename="campus-package.zip"; filename*=UTF-8\'\'' + encodedFileName,
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

async function requireOwnerOrAdmin(request, response, config, token = bearerToken(request)) {
  if (!token) {
    sendJson(response, 401, { error: 'Unauthorized' }, { 'Cache-Control': 'no-store' });
    return null;
  }
  const user = await getCurrentUser(config, token);
  if (!user) {
    sendJson(response, 401, { error: 'Unauthorized' }, { 'Cache-Control': 'no-store' });
    return null;
  }
  const roleQuery = new URLSearchParams({
    select: 'role',
    user_id: 'eq.' + user.userId,
    limit: '1'
  });
  const profiles = await table(config, 'admin_profiles', roleQuery);
  if (!['owner', 'admin'].includes(profiles[0]?.role)) {
    sendJson(response, 403, { error: 'Owner or admin role required' }, { 'Cache-Control': 'no-store' });
    return null;
  }
  return user;
}

async function requestGitHubReleaseApi(config, path, method = 'GET', payload = null) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 10000);
  let result;
  try {
    result = await fetch(new URL(path, GITHUB_API_BASE_URL), {
      method,
      headers: {
        Accept: 'application/vnd.github+json',
        Authorization: 'Bearer ' + config.githubReleaseDispatchToken,
        'X-GitHub-Api-Version': GITHUB_API_VERSION,
        'User-Agent': 'Veyon-Campus-Release-Manager',
        ...(payload === null ? {} : { 'Content-Type': 'application/json' })
      },
      ...(payload === null ? {} : { body: JSON.stringify(payload) }),
      signal: controller.signal
    });
  } catch {
    throw new GitHubApiFailure(503, '无法连接 GitHub 发布服务，请稍后重试。');
  } finally {
    clearTimeout(timeout);
  }

  if (!result.ok) {
    if (method === 'GET' && result.status === 404) return { status: 404, data: null };
    if (result.status === 401 || result.status === 403)
      throw new GitHubApiFailure(503, 'GitHub 发布凭据无效或权限不足，请联系项目维护者。');
    throw new GitHubApiFailure(502, 'GitHub 未接受此版本发布请求。');
  }
  if (result.status === 204) return { status: 204, data: null };
  try {
    return { status: result.status, data: await result.json() };
  } catch {
    throw new GitHubApiFailure(502, 'GitHub 发布服务返回了无效响应。');
  }
}

async function handleAdminReleaseDispatchStatus(request, response, config) {
  try {
    const user = await requireOwnerOrAdmin(request, response, config);
    if (!user) return;
    sendJson(response, 200, {
      configured: Boolean(config.githubReleaseDispatchToken),
      repository: `${GITHUB_RELEASE_OWNER}/${GITHUB_RELEASE_REPOSITORY}`,
      workflow: GITHUB_RELEASE_WORKFLOW,
      workflowUrl: GITHUB_RELEASE_WORKFLOW_URL,
      tagPattern: 'vX.Y.Z'
    }, { 'Cache-Control': 'no-store' });
  } catch (error) {
    mapError(response, error, 'admin-release');
  }
}

const pendingReleaseDispatches = new Set();

async function handleAdminReleaseDispatch(request, response, config) {
  try {
    const user = await requireOwnerOrAdmin(request, response, config);
    if (!user) return;
    if (!config.githubReleaseDispatchToken) {
      sendJson(response, 503, { error: '后台发布服务尚未配置，请联系项目维护者。' }, { 'Cache-Control': 'no-store' });
      return;
    }

    const body = await requiredJsonBody(request, new Set(['tag']), 4096);
    if (typeof body.tag !== 'string' || body.tag.length > 64 ||
        !/^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(body.tag)) {
      throw new InvalidRequestError('请输入稳定版本 tag，格式为 vX.Y.Z。');
    }
    const version = body.tag.slice(1);

    const tagResult = await requestGitHubReleaseApi(config,
      `/repos/${GITHUB_RELEASE_OWNER}/${GITHUB_RELEASE_REPOSITORY}/git/ref/tags/${encodeURIComponent(body.tag)}`);
    if (tagResult.status === 404 || tagResult.data?.ref !== `refs/tags/${body.tag}`) {
      sendJson(response, 404, { error: 'GitHub 上不存在此版本 tag；请先提交并推送稳定版本 tag。' }, { 'Cache-Control': 'no-store' });
      return;
    }

    const currentReleases = await readLatestReleaseEnvelopes(config, ['TeacherConsole', 'StudentSetup']);
    for (const role of ['TeacherConsole', 'StudentSetup']) {
      const currentVersion = currentReleases[role]?.manifest?.version;
      if (currentVersion && compareSemanticVersions(version, currentVersion) <= 0) {
        sendJson(response, 409, {
          error: `${role} 已有版本 ${currentVersion}，发布版本必须更高。`
        }, { 'Cache-Control': 'no-store' });
        return;
      }
    }

    const workflowRuns = await requestGitHubReleaseApi(config,
      `/repos/${GITHUB_RELEASE_OWNER}/${GITHUB_RELEASE_REPOSITORY}/actions/workflows/${GITHUB_RELEASE_WORKFLOW}/runs?per_page=100`);
    if (!Array.isArray(workflowRuns.data?.workflow_runs))
      throw new GitHubApiFailure(502, 'GitHub 发布服务返回了无效工作流状态。');
    const activeRun = workflowRuns.data.workflow_runs.some(run =>
      run?.head_branch === body.tag &&
      ['queued', 'in_progress', 'waiting', 'requested', 'pending'].includes(run.status));
    if (activeRun) {
      sendJson(response, 409, {
        error: '此版本已有进行中的构建或发布，请先查看 GitHub Actions 状态。',
        workflowUrl: GITHUB_RELEASE_WORKFLOW_URL
      }, { 'Cache-Control': 'no-store' });
      return;
    }

    if (pendingReleaseDispatches.has(body.tag)) {
      sendJson(response, 409, { error: '此版本的发布工作流刚刚已触发，请先查看 GitHub Actions 状态。' }, { 'Cache-Control': 'no-store' });
      return;
    }
    pendingReleaseDispatches.add(body.tag);
    try {
      const dispatch = await requestGitHubReleaseApi(config,
        `/repos/${GITHUB_RELEASE_OWNER}/${GITHUB_RELEASE_REPOSITORY}/actions/workflows/${GITHUB_RELEASE_WORKFLOW}/dispatches`,
        'POST', { ref: body.tag });
      const runUrl = typeof dispatch.data?.html_url === 'string' &&
        /^https:\/\/github\.com\/Ljc798\/veyon-campus-deployment\/actions\/runs\/[0-9]+$/.test(dispatch.data.html_url)
        ? dispatch.data.html_url
        : null;
      sendJson(response, 202, {
        accepted: true,
        tag: body.tag,
        workflow: GITHUB_RELEASE_WORKFLOW,
        workflowUrl: GITHUB_RELEASE_WORKFLOW_URL,
        runUrl
      }, { 'Cache-Control': 'no-store' });
    } finally {
      pendingReleaseDispatches.delete(body.tag);
    }
  } catch (error) {
    mapError(response, error, 'admin-release');
  }
}

async function handleAdminDatabase(request, response, config, tableName, url) {
  const token = bearerToken(request);
  if (!token) {
    sendJson(response, 401, { error: 'Unauthorized' }, { 'Cache-Control': 'no-store' });
    return;
  }

  let page;
  let pageSize;
  try {
    page = integerParameter(url.searchParams.get('page'), 1, 1, 1000001, 'page');
    pageSize = integerParameter(url.searchParams.get('pageSize'), 25, 1,
      MAX_ADMIN_DATABASE_PAGE_SIZE, 'pageSize');
  } catch (error) {
    mapError(response, error, 'admin-database');
    return;
  }
  const offset = (page - 1) * pageSize;
  if (!Number.isSafeInteger(offset) || offset > MAX_ADMIN_DATABASE_OFFSET) {
    sendJson(response, 400, { error: 'page is outside the supported range' }, { 'Cache-Control': 'no-store' });
    return;
  }

  const spec = ADMIN_DATABASE_TABLES[tableName];
  if (!spec) {
    sendJson(response, 404, { error: 'Database table is not available' }, { 'Cache-Control': 'no-store' });
    return;
  }

  try {
    const user = await requireOwnerOrAdmin(request, response, config, token);
    if (!user) return;

    const query = new URLSearchParams({
      select: spec.columns,
      order: spec.order,
      limit: String(pageSize + 1),
      offset: String(offset)
    });
    const records = await table(config, tableName, query);
    const hasMore = records.length > pageSize;
    const rows = records.slice(0, pageSize).map(record => {
      const safeRecord = { ...record };
      for (const field of spec.redact) {
        if (Object.hasOwn(safeRecord, field)) safeRecord[field] = '[已隐藏]';
      }
      return safeRecord;
    });
    sendJson(response, 200, {
      table: tableName,
      page,
      pageSize,
      rows,
      hasMore,
      redactedFields: spec.redact
    }, { 'Cache-Control': 'no-store' });
  } catch (error) {
    mapError(response, error, 'admin-database');
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
      const adminDatabase = /^\/v1\/admin\/database\/([a-z_]+)$/.exec(pathname);
      if (adminDatabase && request.method === 'GET') {
        await handleAdminDatabase(request, response, config, adminDatabase[1], url);
        return;
      }
      if (request.method === 'GET' && pathname === '/v1/admin/releases/dispatch-status') {
        await handleAdminReleaseDispatchStatus(request, response, config);
        return;
      }
      if (request.method === 'POST' && pathname === '/v1/admin/releases/dispatch') {
        await handleAdminReleaseDispatch(request, response, config);
        return;
      }
      if (request.method === 'POST' && pathname === '/v1/heartbeat') {
        await handleHeartbeat(request, response, config);
        return;
      }
      if (request.method === 'POST' && pathname === '/v1/heartbeat/teacher') {
        await handleTeacherHeartbeat(request, response, config);
        return;
      }
      if (request.method === 'GET' && pathname === '/v1/releases/latest') {
        await handleLatestRelease(request, response, config, url);
        return;
      }
      const releaseArtifact = /^\/v1\/releases\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/artifact$/i.exec(pathname);
      if (request.method === 'GET' && releaseArtifact) {
        await handleReleaseArtifact(request, response, config, releaseArtifact[1].toLowerCase());
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

module.exports = { createRequestHandler, loadConfig, compareSemanticVersions };

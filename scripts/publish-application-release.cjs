'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { createReadStream } = require('node:fs');

const MAX_ARTIFACT_BYTES = 512 * 1024 * 1024;
const VERSION_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;
const DEFINITIVE_REJECTION_STATUSES = new Set([400, 401, 403, 404, 409, 413, 415, 422]);

class CloudBaseHttpFailure extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

function isDefinitiveCloudBaseRejection(error) {
  return error instanceof CloudBaseHttpFailure && DEFINITIVE_REJECTION_STATUSES.has(error.status);
}

function parseArguments(args) {
  const options = {};
  for (let index = 0; index < args.length; index++) {
    const name = args[index];
    if (name === '--confirm-publication') {
      options.confirmed = true;
      continue;
    }
    if (!['--role', '--version', '--installer'].includes(name) || options[name])
      throw new Error(`Unexpected or duplicate argument: ${name}`);
    const value = args[++index];
    if (!value || value.startsWith('--')) throw new Error(`Missing value for ${name}`);
    options[name] = value;
  }
  if (!options.confirmed) throw new Error('Pass --confirm-publication to publish a public application release.');
  if (!['TeacherConsole', 'StudentSetup'].includes(options['--role']))
    throw new Error('--role must be TeacherConsole or StudentSetup.');
  if (!options['--version'] || options['--version'].length > 64 ||
      !VERSION_PATTERN.test(options['--version']))
    throw new Error('--version must be a valid SemVer X.Y.Z value.');
  if (!options['--installer']) throw new Error('--installer is required.');
  return {
    role: options['--role'],
    version: options['--version'],
    installerPath: path.resolve(options['--installer'])
  };
}

function canonicalPayload(manifest) {
  return Buffer.from(JSON.stringify({
    schemaVersion: manifest.schemaVersion,
    product: manifest.product,
    role: manifest.role,
    version: manifest.version,
    architecture: manifest.architecture,
    fileName: manifest.fileName,
    sizeBytes: manifest.sizeBytes,
    sha256: manifest.sha256,
    downloadUrl: manifest.downloadUrl
  }), 'utf8');
}

async function requestCloudBase(apiBase, apiKey, requestPath, options = {}) {
  const headers = { Accept: 'application/json', Authorization: `Bearer ${apiKey}` };
  if (options.bytes) {
    headers['Content-Type'] = 'application/vnd.microsoft.portable-executable';
    headers['Content-Length'] = String(options.bytes);
    headers['x-upsert'] = 'false';
  } else if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }
  const response = await fetch(new URL(requestPath, apiBase), {
    method: options.method || 'GET',
    headers,
    body: options.body !== undefined ? JSON.stringify(options.body) : options.stream,
    ...(options.stream ? { duplex: 'half' } : {}),
    signal: AbortSignal.timeout(options.timeoutMs || 60000),
    redirect: 'error'
  });
  if (!response.ok) {
    const detail = await response.text().catch(() => '');
    throw new CloudBaseHttpFailure(response.status,
      `CloudBase request failed with HTTP ${response.status}${detail ? `: ${detail.slice(0, 512)}` : ''}`);
  }
  if (options.noResponse) {
    await response.body?.cancel().catch(() => {});
    return null;
  }
  return response.json();
}

async function computeFileSha256(filePath) {
  const hash = crypto.createHash('sha256');
  for await (const chunk of createReadStream(filePath)) hash.update(chunk);
  return hash.digest('hex').toUpperCase();
}

function createReleaseSigningKey(privateKeyPem, passphrase = '') {
  return passphrase
    ? crypto.createPrivateKey({ key: privateKeyPem, passphrase })
    : crypto.createPrivateKey(privateKeyPem);
}

async function publish() {
  if (process.env.CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW !== 'yes')
    throw new Error('Rotate the CloudBase service API key first and set CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW=yes.');
  const envId = (process.env.CloudBase__EnvId || '').trim();
  const apiKey = (process.env.CloudBase__ApiKey || '').trim();
  const signingKeyPath = process.env.VEYONCAMPUS_RELEASE_PRIVATE_KEY_PATH;
  const publicKeyPath = process.env.VEYONCAMPUS_RELEASE_PUBLIC_KEY_PATH;
  if (!/^[A-Za-z0-9-]+$/.test(envId) || !apiKey || !signingKeyPath || !publicKeyPath)
    throw new Error('Set CloudBase__EnvId, CloudBase__ApiKey, VEYONCAMPUS_RELEASE_PRIVATE_KEY_PATH, and VEYONCAMPUS_RELEASE_PUBLIC_KEY_PATH in the process environment.');
  const projectConfig = JSON.parse(await fs.promises.readFile(path.resolve(__dirname, '../cloudbaserc.json'), 'utf8'));
  const functionConfig = projectConfig.functions?.find((entry) => entry.name === 'veyon-api');
  const releaseBucket = functionConfig?.envVariables?.CloudBase__ApplicationReleaseBucket;
  const publicApiBaseValue = functionConfig?.envVariables?.CloudBase__ApplicationReleasePublicBaseUrl;
  if (envId !== projectConfig.envId || !/^[A-Za-z0-9-]+$/.test(releaseBucket || '') ||
      typeof publicApiBaseValue !== 'string')
    throw new Error('CloudBase environment or release bucket configuration does not match cloudbaserc.json.');
  const publicApiBaseUrl = new URL(publicApiBaseValue);
  if (publicApiBaseUrl.protocol !== 'https:' || publicApiBaseUrl.pathname !== '/' ||
      publicApiBaseUrl.search || publicApiBaseUrl.hash || publicApiBaseUrl.username || publicApiBaseUrl.password)
    throw new Error('The configured public API base URL must be an HTTPS root URL.');

  const { role, version, installerPath } = parseArguments(process.argv.slice(2));
  const roleName = role === 'TeacherConsole' ? 'Teacher' : 'Student';
  const product = role === 'TeacherConsole' ? 'VeyonCampus.TeacherConsole' : 'VeyonCampus.StudentSetup';
  const fileName = `VeyonCampus-${roleName}-Setup-${version}-win-x64.exe`;
  if (path.basename(installerPath) !== fileName)
    throw new Error(`Installer filename must be exactly ${fileName}.`);
  const fileInfo = await fs.promises.lstat(installerPath);
  if (!fileInfo.isFile() || fileInfo.isSymbolicLink() || fileInfo.size < 1 || fileInfo.size > MAX_ARTIFACT_BYTES)
    throw new Error('Installer must be a regular file between 1 byte and 512 MiB.');

  const privateKeyPem = await fs.promises.readFile(signingKeyPath, 'utf8');
  const privateKey = createReleaseSigningKey(privateKeyPem,
    process.env.VEYONCAMPUS_RELEASE_PRIVATE_KEY_PASSPHRASE || '');
  if (privateKey.asymmetricKeyType !== 'rsa' ||
      privateKey.asymmetricKeyDetails.modulusLength < 2048 || privateKey.asymmetricKeyDetails.modulusLength > 4096)
    throw new Error('Release signing key must be RSA 2048–4096 bits.');
  const pinnedPublicKeyPem = await fs.promises.readFile(publicKeyPath, 'utf8');
  if (pinnedPublicKeyPem.includes('PRIVATE KEY'))
    throw new Error('The embedded release trust file must contain only a public key.');
  const pinnedPublicKey = crypto.createPublicKey(pinnedPublicKeyPem);
  const signingPublicKey = crypto.createPublicKey(privateKey);
  if (!pinnedPublicKey.export({ type: 'spki', format: 'der' })
    .equals(signingPublicKey.export({ type: 'spki', format: 'der' })))
    throw new Error('The pinned public key does not match the release signing private key.');

  const releaseId = crypto.randomUUID();
  const objectKey = `releases/${role}/win-x64/${releaseId.replace(/-/g, '')}.exe`;
  const manifest = {
    schemaVersion: 1,
    product,
    role,
    version,
    architecture: 'win-x64',
    fileName,
    sizeBytes: fileInfo.size,
    sha256: await computeFileSha256(installerPath),
    downloadUrl: new URL(`v1/releases/${releaseId}/artifact`, publicApiBaseUrl).href
  };
  const signature = crypto.sign('sha256', canonicalPayload(manifest), {
    key: privateKey,
    padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
    saltLength: 32
  }).toString('base64');
  const apiBase = `https://${envId}.api.tcloudbasegateway.com/`;
  const bucketPath = `/v1/storages/object/${encodeURIComponent(releaseBucket)}/` +
    objectKey.split('/').map(encodeURIComponent).join('/');
  const objectUploaded = { value: false };

  try {
    await requestCloudBase(apiBase, apiKey, bucketPath, {
      method: 'POST',
      bytes: fileInfo.size,
      stream: createReadStream(installerPath),
      timeoutMs: 15 * 60 * 1000,
      noResponse: true
    });
    objectUploaded.value = true;
    await requestCloudBase(apiBase, apiKey, '/v1/rdb/rest/rpc/publish_application_release_v1', {
      method: 'POST',
      body: {
        p_release_id: releaseId,
        p_role: role,
        p_version: version,
        p_size_bytes: fileInfo.size,
        p_sha256: manifest.sha256,
        p_signature: signature
      },
      timeoutMs: 30000
    });
  } catch (error) {
    if (objectUploaded.value && isDefinitiveCloudBaseRejection(error)) {
      try {
        await requestCloudBase(apiBase, apiKey, bucketPath, {
          method: 'DELETE', noResponse: true, timeoutMs: 30000
        });
      } catch {}
    }
    throw error;
  }
  console.log(JSON.stringify({ releaseId, role, version, fileName, sizeBytes: fileInfo.size,
    sha256: manifest.sha256, publishedAt: new Date().toISOString() }, null, 2));
}

if (require.main === module) {
  publish().catch((error) => {
    process.stderr.write(`Release publication failed: ${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = {
  canonicalPayload,
  parseArguments,
  createReleaseSigningKey,
  CloudBaseHttpFailure,
  isDefinitiveCloudBaseRejection
};

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
  const payload = {
    schemaVersion: manifest.schemaVersion,
    product: manifest.product,
    role: manifest.role,
    version: manifest.version,
    architecture: manifest.architecture,
    fileName: manifest.fileName,
    sizeBytes: manifest.sizeBytes,
    sha256: manifest.sha256,
    downloadUrl: manifest.downloadUrl
  };
  if (manifest.schemaVersion === 2) payload.policyCapabilities = manifest.policyCapabilities;
  return Buffer.from(JSON.stringify(payload), 'utf8');
}

function legacyCanonicalPayload(manifest) {
  return canonicalPayload({ ...manifest, schemaVersion: 1, policyCapabilities: undefined });
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

function parseSemanticVersion(value) {
  const match = VERSION_PATTERN.exec(value);
  if (!match) throw new Error('Release version is not valid SemVer.');
  return {
    core: match.slice(1, 4).map(BigInt),
    prerelease: match[4] ? match[4].split('.') : null
  };
}

function compareSemanticVersions(left, right) {
  const a = parseSemanticVersion(left);
  const b = parseSemanticVersion(right);
  for (let index = 0; index < 3; index++) {
    if (a.core[index] !== b.core[index]) return a.core[index] < b.core[index] ? -1 : 1;
  }
  if (a.prerelease === null || b.prerelease === null) {
    if (a.prerelease === b.prerelease) return 0;
    return a.prerelease === null ? 1 : -1;
  }
  const length = Math.max(a.prerelease.length, b.prerelease.length);
  for (let index = 0; index < length; index++) {
    const leftPart = a.prerelease[index];
    const rightPart = b.prerelease[index];
    if (leftPart === undefined || rightPart === undefined) {
      if (leftPart === rightPart) return 0;
      return leftPart === undefined ? -1 : 1;
    }
    if (leftPart === rightPart) continue;
    const leftNumeric = /^(0|[1-9][0-9]*)$/.test(leftPart);
    const rightNumeric = /^(0|[1-9][0-9]*)$/.test(rightPart);
    if (leftNumeric && rightNumeric) {
      const leftNumber = BigInt(leftPart);
      const rightNumber = BigInt(rightPart);
      return leftNumber < rightNumber ? -1 : 1;
    }
    if (leftNumeric !== rightNumeric) return leftNumeric ? -1 : 1;
    return leftPart < rightPart ? -1 : 1;
  }
  return 0;
}

async function getLatestRelease(apiBaseUrl, role) {
  const url = new URL(`v1/releases/latest?role=${encodeURIComponent(role)}&architecture=win-x64`, apiBaseUrl);
  const response = await fetch(url, {
    headers: { Accept: 'application/json' },
    cache: 'no-store',
    signal: AbortSignal.timeout(15000),
    redirect: 'error'
  });
  if (!response.ok) throw new Error(`Could not check the current ${role} release (HTTP ${response.status}).`);
  const payload = await response.json();
  if (!Object.hasOwn(payload || {}, 'release')) throw new Error('CloudBase returned an invalid latest-release response.');
  return payload.release;
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
    schemaVersion: 2,
    product,
    role,
    version,
    architecture: 'win-x64',
    fileName,
    sizeBytes: fileInfo.size,
    sha256: await computeFileSha256(installerPath),
    downloadUrl: new URL(`v1/releases/${releaseId}/artifact`, publicApiBaseUrl).href,
    policyCapabilities: { studentSystemPolicy: 1 }
  };
  const currentRelease = await getLatestRelease(publicApiBaseUrl, role);
  if (currentRelease !== null) {
    if (!currentRelease?.manifest || typeof currentRelease.manifest.version !== 'string' ||
        typeof currentRelease.manifest.sha256 !== 'string' ||
        typeof currentRelease.manifest.fileName !== 'string' ||
        !Number.isSafeInteger(currentRelease.manifest.sizeBytes)) {
      throw new Error('CloudBase returned an invalid current release manifest.');
    }
    const currentVersion = currentRelease.manifest.version;
    const versionOrder = compareSemanticVersions(version, currentVersion);
    const sameArtifact = version === currentVersion &&
      currentRelease.manifest.sha256 === manifest.sha256 &&
      currentRelease.manifest.fileName === manifest.fileName &&
      currentRelease.manifest.sizeBytes === manifest.sizeBytes;
    if (sameArtifact) {
      console.log(JSON.stringify({ alreadyPublished: true, role, version, fileName, sizeBytes: fileInfo.size,
        sha256: manifest.sha256 }, null, 2));
      return;
    }
    if (versionOrder === 0) throw new Error('This role/version already exists with different installer bytes; release versions are immutable.');
    if (versionOrder < 0) throw new Error(`A newer ${role} version is already published (${currentVersion}).`);
  }
  const signature = crypto.sign('sha256', canonicalPayload(manifest), {
    key: privateKey,
    padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
    saltLength: 32
  }).toString('base64');
  const legacySignature = crypto.sign('sha256', legacyCanonicalPayload(manifest), {
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
    await requestCloudBase(apiBase, apiKey, '/v1/rdb/rest/rpc/publish_application_release_v2', {
      method: 'POST',
      body: {
        p_release_id: releaseId,
        p_role: role,
        p_version: version,
        p_size_bytes: fileInfo.size,
        p_sha256: manifest.sha256,
        p_signature: signature,
        p_legacy_signature: legacySignature,
        p_student_system_policy_capability: manifest.policyCapabilities.studentSystemPolicy
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
  legacyCanonicalPayload,
  parseArguments,
  createReleaseSigningKey,
  CloudBaseHttpFailure,
  isDefinitiveCloudBaseRejection,
  compareSemanticVersions
};

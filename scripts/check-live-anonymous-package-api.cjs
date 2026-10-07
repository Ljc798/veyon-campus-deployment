'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { canonicalizeArchive, canonicalizeFolderFiles } = require('../cloudfunctions/veyon-api/package-validator');
const { createCampusPackageObjectKey } = require('../cloudfunctions/veyon-api/package-naming');

function createSyntheticPackage() {
  const campusName = `Synthetic API E2E ${crypto.randomUUID()}`;
  const computerPrefix = `API-${crypto.randomUUID().replace(/-/g, '').slice(0, 7).toUpperCase()}-`;
  const veyonPublicKey = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 })
    .publicKey.export({ type: 'spki', format: 'pem' });
  const policyPublicKey = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 })
    .publicKey.export({ type: 'spki', format: 'pem' });
  const applicationPolicyPublicKey = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 })
    .publicKey.export({ type: 'spki', format: 'pem' });
  const studentSystemPolicyPublicKey = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 })
    .publicKey.export({ type: 'spki', format: 'pem' });
  const veyonPath = 'synthetic-campus-public.pem';
  const policyPath = 'website-policy-public.pem';
  const applicationPolicyPath = 'application-policy-public.pem';
  const studentSystemPolicyPath = 'student-system-policy-public.pem';
  const veyonBytes = Buffer.from(veyonPublicKey, 'ascii');
  const policyBytes = Buffer.from(policyPublicKey, 'ascii');
  const applicationPolicyBytes = Buffer.from(applicationPolicyPublicKey, 'ascii');
  const studentSystemPolicyBytes = Buffer.from(studentSystemPolicyPublicKey, 'ascii');
  const packageId = crypto.randomUUID();
  const manifest = {
    schemaVersion: 5,
    packageId,
    targetOs: 'windows',
    architecture: 'x64',
    campus: campusName,
    computerPrefix,
    publicKey: {
      path: veyonPath,
      size: veyonBytes.length,
      sha256: crypto.createHash('sha256').update(veyonBytes).digest('hex')
    },
    websitePolicyPublicKey: {
      path: policyPath,
      size: policyBytes.length,
      sha256: crypto.createHash('sha256').update(policyBytes).digest('hex')
    },
    applicationPolicyPublicKey: {
      path: applicationPolicyPath,
      size: applicationPolicyBytes.length,
      sha256: crypto.createHash('sha256').update(applicationPolicyBytes).digest('hex')
    },
    studentSystemPolicyPublicKey: {
      path: studentSystemPolicyPath,
      size: studentSystemPolicyBytes.length,
      sha256: crypto.createHash('sha256').update(studentSystemPolicyBytes).digest('hex')
    },
    compatibility: {
      studentApp: { minInclusive: '0.4.49', maxExclusive: '0.4.50' },
      veyon: { minInclusive: '4.11.2.0', maxExclusive: '4.11.2.1' },
      studentAgent: { minInclusive: '0.4.49', maxExclusive: '0.4.50' }
    }
  };
  const campus = {
    campus: campusName,
    computerPrefix,
    keyFile: veyonPath,
    websitePolicyKeyFile: policyPath,
    applicationPolicyKeyFile: applicationPolicyPath,
    systemPolicyKeyFile: studentSystemPolicyPath
  };
  const payloadFiles = [
    { fileName: 'campus.json', bytes: Buffer.from(JSON.stringify(campus), 'utf8') },
    { fileName: veyonPath, bytes: veyonBytes },
    { fileName: policyPath, bytes: policyBytes },
    { fileName: applicationPolicyPath, bytes: applicationPolicyBytes },
    { fileName: studentSystemPolicyPath, bytes: studentSystemPolicyBytes },
    { fileName: 'README.md', bytes: Buffer.from('# Synthetic API E2E package\n', 'utf8') }
  ];
  manifest.files = payloadFiles.map((file) => ({
    path: file.fileName,
    size: file.bytes.length,
    sha256: crypto.createHash('sha256').update(file.bytes).digest('hex')
  }));
  const canonical = canonicalizeFolderFiles([
    { fileName: 'manifest.json', bytes: Buffer.from(JSON.stringify(manifest), 'utf8') },
    ...payloadFiles
  ]);
  return {
    packageId: canonical.packageId,
    schemaVersion: canonical.schemaVersion,
    manifest,
    payloadFileNames: payloadFiles.map((file) => file.fileName),
    campusName,
    computerPrefix,
    archiveBytes: canonical.archiveBytes
  };
}

function compactPackageId(packageId) {
  return String(packageId || '').replace(/-/g, '').toLowerCase();
}

function formatPackageId(packageId) {
  const compact = compactPackageId(packageId);
  if (!/^[a-f0-9]{32}$/.test(compact))
    throw new Error('Package ID must be a UUID.');
  return `${compact.slice(0, 8)}-${compact.slice(8, 12)}-${compact.slice(12, 16)}-${compact.slice(16, 20)}-${compact.slice(20)}`;
}

function loadConfiguration(args) {
  if (args.length !== 1 || args[0] !== '--confirm-live-synthetic-test')
    throw new Error('Pass --confirm-live-synthetic-test to publish and then remove one synthetic package.');
  if (process.env.CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW !== 'yes')
    throw new Error('Rotate the CloudBase service API key first and set CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW=yes.');

  const envId = (process.env.CloudBase__EnvId || '').trim();
  const serviceApiKey = (process.env.CloudBase__ApiKey || '').trim();
  const adminBearerToken = (process.env.VEYONCAMPUS_LIVE_TEST_ADMIN_BEARER_TOKEN || '').trim();
  if (!/^[A-Za-z0-9-]+$/.test(envId) || !serviceApiKey || !adminBearerToken)
    throw new Error('Set CloudBase__EnvId, CloudBase__ApiKey, and VEYONCAMPUS_LIVE_TEST_ADMIN_BEARER_TOKEN through a protected environment.');

  const projectConfig = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../cloudbaserc.json'), 'utf8'));
  const functionConfig = projectConfig.functions?.find((entry) => entry.name === 'veyon-api');
  const functionEnvironment = functionConfig?.envVariables || {};
  const packageBucket = functionEnvironment.CloudBase__DeploymentPackageBucket;
  const publicApiBaseAddress = new URL(functionEnvironment.CloudBase__ApplicationReleasePublicBaseUrl || '');
  if (envId !== projectConfig.envId || !/^[A-Za-z0-9-]+$/.test(packageBucket || '') ||
      publicApiBaseAddress.protocol !== 'https:' || publicApiBaseAddress.pathname !== '/' ||
      publicApiBaseAddress.search || publicApiBaseAddress.hash || publicApiBaseAddress.username ||
      publicApiBaseAddress.password)
    throw new Error('CloudBase environment or public API configuration does not match cloudbaserc.json.');

  return {
    envId,
    serviceApiKey,
    adminBearerToken,
    packageBucket,
    publicApiBaseAddress,
    storageApiBaseAddress: new URL(`https://${envId}.api.tcloudbasegateway.com/`)
  };
}

function multipartField(boundary, name, value) {
  return Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="${name}"\r\n\r\n${value}\r\n`, 'utf8');
}

function multipartFile(boundary, fieldName, fileName, bytes, contentType) {
  return Buffer.concat([
    Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="${fieldName}"; filename="${fileName}"\r\nContent-Type: ${contentType}\r\n\r\n`, 'utf8'),
    bytes,
    Buffer.from('\r\n', 'ascii')
  ]);
}

async function fetchWithTimeout(fetchImplementation, url, options = {}) {
  return fetchImplementation(url, {
    ...options,
    redirect: 'error',
    signal: AbortSignal.timeout(options.timeoutMs || 30000)
  });
}

async function readJsonResponse(response, operation) {
  if (!response.ok)
    throw new Error(`${operation} returned HTTP ${response.status}.`);
  try {
    return await response.json();
  } catch {
    throw new Error(`${operation} returned invalid JSON.`);
  }
}

async function searchForPackage(configuration, campusName, fetchImplementation) {
  const url = new URL('v1/deployment-packages', configuration.publicApiBaseAddress);
  url.searchParams.set('query', campusName);
  const response = await fetchWithTimeout(fetchImplementation, url, {
    headers: { Accept: 'application/json' }
  });
  return readJsonResponse(response, 'Anonymous package search');
}

async function cleanupSyntheticPackage(configuration, fixture, publishAttempted, fetchImplementation) {
  if (!publishAttempted) return [];
  const cleanupErrors = [];
  const packageId = compactPackageId(fixture.packageId);
  const routePackageId = formatPackageId(packageId);
  try {
    const response = await fetchWithTimeout(fetchImplementation,
      new URL(`v1/deployment-packages/${routePackageId}/withdraw`, configuration.publicApiBaseAddress), {
        method: 'POST',
        headers: {
          Accept: 'application/json',
          Authorization: `Bearer ${configuration.adminBearerToken}`,
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ reason: 'Automated synthetic API E2E cleanup' })
      });
    if (response.status !== 204 && response.status !== 404)
      cleanupErrors.push(`Synthetic package withdrawal returned HTTP ${response.status}.`);
    await response.body?.cancel();
  } catch (error) {
    cleanupErrors.push(`Synthetic package withdrawal failed (${error.name}).`);
  }

  try {
    const objectKey = createCampusPackageObjectKey(fixture.campusName, packageId.replace(/-/g, ''));
    const encodedObjectKey = objectKey.split('/').map(encodeURIComponent).join('/');
    const storageUrl = new URL(
      `v1/storages/object/${encodeURIComponent(configuration.packageBucket)}/${encodedObjectKey}`,
      configuration.storageApiBaseAddress);
    const response = await fetchWithTimeout(fetchImplementation, storageUrl, {
      method: 'DELETE',
      headers: { Authorization: `Bearer ${configuration.serviceApiKey}` }
    });
    if (!response.ok && response.status !== 404)
      cleanupErrors.push(`Synthetic package object deletion returned HTTP ${response.status}.`);
    await response.body?.cancel();
  } catch (error) {
    cleanupErrors.push(`Synthetic package object deletion failed (${error.name}).`);
  }

  try {
    const result = await searchForPackage(configuration, fixture.campusName, fetchImplementation);
    if (!Array.isArray(result.items) || result.items.some((item) =>
      compactPackageId(item.packageId) === packageId))
      cleanupErrors.push('Synthetic package remains visible in the public search directory.');
  } catch (error) {
    cleanupErrors.push(`Synthetic package cleanup could not be verified (${error.message}).`);
  }
  return cleanupErrors;
}

async function executeLiveCheck(configuration, fixture, fetchImplementation = global.fetch) {
  const expectedPhoneSuffix = '2468';
  let publishAttempted = false;
  let operationError = null;

  try {
    const initialSearch = await searchForPackage(configuration, fixture.campusName, fetchImplementation);
    if (!Array.isArray(initialSearch.items) || initialSearch.items.length !== 0)
      throw new Error('Random synthetic campus name unexpectedly matched an existing package.');

    const boundary = `VeyonCampusLiveE2E${crypto.randomBytes(16).toString('hex')}`;
    const uploadBody = Buffer.concat([
      multipartField(boundary, 'campusName', fixture.campusName),
      multipartField(boundary, 'publisherName', 'Synthetic API E2E'),
      multipartField(boundary, 'teacherPhoneLast4', expectedPhoneSuffix),
      multipartFile(boundary, 'archive', 'synthetic-package.zip', fixture.archiveBytes, 'application/zip'),
      Buffer.from(`--${boundary}--\r\n`, 'ascii')
    ]);
    publishAttempted = true;
    const publishResponse = await fetchWithTimeout(fetchImplementation,
      new URL('v1/deployment-packages', configuration.publicApiBaseAddress), {
        method: 'POST',
        headers: {
          Accept: 'application/json',
          'Content-Type': `multipart/form-data; boundary=${boundary}`
        },
        body: uploadBody
      });
    const published = await readJsonResponse(publishResponse, 'Anonymous package publication');
    assert.equal(publishResponse.status, 201);
    assert.equal(compactPackageId(published.packageId), compactPackageId(fixture.packageId));
    assert.equal(published.schemaVersion, fixture.schemaVersion);
    assert.equal(Object.hasOwn(published, 'publisherName'), false);
    assert.equal(published.campusName, fixture.campusName);
    assert.equal(published.computerPrefix, fixture.computerPrefix);
    assert.equal(published.sizeBytes, fixture.archiveBytes.length);
    const expectedDigest = crypto.createHash('sha256').update(fixture.archiveBytes).digest('hex').toUpperCase();
    assert.equal(published.sha256, expectedDigest);

    const searchResult = await searchForPackage(configuration, fixture.campusName, fetchImplementation);
    const matchingPackage = searchResult.items.find((item) =>
      compactPackageId(item.packageId) === compactPackageId(fixture.packageId));
    assert.ok(matchingPackage);
    for (const privateField of ['publisherName', 'teacherPhoneLast4', 'publisherIdentityFingerprint',
      'phoneFingerprint', 'storageKey'])
      assert.equal(Object.hasOwn(matchingPackage, privateField), false);

    const wrongSuffixResponse = await fetchWithTimeout(fetchImplementation,
      new URL(`v1/deployment-packages/${formatPackageId(fixture.packageId)}/download`, configuration.publicApiBaseAddress), {
        method: 'POST',
        headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
        body: JSON.stringify({ teacherPhoneLast4: '9753' })
      });
    assert.equal(wrongSuffixResponse.status, 403);
    await wrongSuffixResponse.body?.cancel();

    const downloadResponse = await fetchWithTimeout(fetchImplementation,
      new URL(`v1/deployment-packages/${formatPackageId(fixture.packageId)}/download`, configuration.publicApiBaseAddress), {
        method: 'POST',
        headers: { Accept: 'application/zip', 'Content-Type': 'application/json' },
        body: JSON.stringify({ teacherPhoneLast4: expectedPhoneSuffix })
      });
    if (!downloadResponse.ok)
      throw new Error(`Anonymous package download returned HTTP ${downloadResponse.status}.`);
    const downloadedArchive = Buffer.from(await downloadResponse.arrayBuffer());
    const downloadedDigest = crypto.createHash('sha256').update(downloadedArchive).digest('hex').toUpperCase();
    assert.equal(downloadedArchive.length, published.sizeBytes);
    assert.equal(downloadedDigest, published.sha256);
    const parsedPackage = canonicalizeArchive(downloadedArchive);
    assert.equal(parsedPackage.packageId.toLowerCase(), fixture.packageId.toLowerCase());
    assert.equal(parsedPackage.schemaVersion, fixture.schemaVersion);
    assert.equal(parsedPackage.campus, fixture.campusName);
    assert.equal(parsedPackage.computerPrefix, fixture.computerPrefix);
  } catch (error) {
    operationError = error;
  }

  const cleanupErrors = await cleanupSyntheticPackage(configuration, fixture, publishAttempted,
    fetchImplementation);
  if (operationError) {
    const cleanupDetail = cleanupErrors.length > 0
      ? ` Cleanup for synthetic package ${fixture.packageId} needs attention: ${cleanupErrors.join(' ')}`
      : '';
    throw new Error(`Anonymous package API E2E failed: ${operationError.message}${cleanupDetail}`, {
      cause: operationError
    });
  }
  if (cleanupErrors.length > 0) {
    throw new Error(`Synthetic package ${fixture.packageId} cleanup failed: ${cleanupErrors.join(' ')}`);
  }
  return { packageId: fixture.packageId };
}

async function runLiveCheck(args = process.argv.slice(2)) {
  const configuration = loadConfiguration(args);
  const fixture = createSyntheticPackage();
  await executeLiveCheck(configuration, fixture);
  process.stdout.write('Anonymous publish/search/wrong-suffix/download/hash/parse/withdraw/object-cleanup E2E passed.\n');
}

if (require.main === module)
  runLiveCheck().catch((error) => {
    process.stderr.write(`Anonymous package API E2E preflight failed: ${error.message}\n`);
    process.exitCode = 1;
  });

module.exports = { createSyntheticPackage, executeLiveCheck, formatPackageId, loadConfiguration };

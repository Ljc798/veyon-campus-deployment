'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const { test } = require('node:test');
const { createRequestHandler, loadConfig, compareSemanticVersions } = require('./index');
const { canonicalizeArchive, canonicalizeFolderFiles } = require('./package-validator');
const { createCampusPackageFileName, createCampusPackageObjectKey } = require('./package-naming');
const {
  createSyntheticPackage,
  executeLiveCheck,
  formatPackageId,
  loadConfiguration: loadLiveCheckConfiguration
} =
  require('../../scripts/check-live-anonymous-package-api.cjs');
const {
  canonicalPayload: canonicalReleasePayload,
  legacyCanonicalPayload: legacyCanonicalReleasePayload,
  legacySystemPolicyCanonicalPayload,
  parseArguments: parseReleaseArguments,
  createReleaseSigningKey,
  CloudBaseHttpFailure,
  isDefinitiveCloudBaseRejection
} = require('../../scripts/publish-application-release.cjs');

const fixtureReleaseId = '0cfa0bf8-5b29-4da4-870d-7f612839dc61';
const releaseSignature = Buffer.alloc(256, 0x39).toString('base64');

test('CloudBase web-function bootstrap has an LF-only shebang', () => {
  const bootstrap = fs.readFileSync(`${__dirname}/scf_bootstrap`);
  const attributes = fs.readFileSync(`${__dirname}/../../.gitattributes`, 'utf8');
  assert.equal(bootstrap.includes(Buffer.from('\r')), false);
  assert.ok(bootstrap.toString('utf8').startsWith('#!/bin/bash\n'));
  assert.ok(bootstrap.toString('utf8').includes('/var/lang/node20/bin/node /var/user/index.js'));
  assert.match(attributes, /^cloudfunctions\/veyon-api\/scf_bootstrap text eol=lf$/m);
});

function responseJson(value) {
  return new Response(JSON.stringify(value), {
    status: 200,
    headers: { 'Content-Type': 'application/json' }
  });
}

test('campus package object names use the campus name without computer prefixes', () => {
  const packageId = '00112233445566778899aabbccddeeff';
  assert.equal(createCampusPackageFileName(' 智学前程-test11 ', packageId),
    '智学前程-test11-00112233445566778899aabbccddeeff.zip');
  assert.equal(createCampusPackageObjectKey('智学前程-test11', packageId),
    'deployment-packages/v3/智学前程-test11-00112233445566778899aabbccddeeff.zip');
  assert.equal(createCampusPackageFileName('学校/东区', packageId),
    '学校-东区-00112233445566778899aabbccddeeff.zip');
  assert.equal(createCampusPackageFileName('...', packageId),
    'campus-00112233445566778899aabbccddeeff.zip');
});

function createPackageFixture(schemaVersion = 3) {
  const campusName = 'Synthetic API Validation Campus';
  const computerPrefix = 'API-';
  const veyonPublicKey = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 })
    .publicKey.export({ type: 'spki', format: 'pem' });
  const policyPublicKey = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 })
    .publicKey.export({ type: 'spki', format: 'pem' });
  const veyonBytes = Buffer.from(veyonPublicKey, 'ascii');
  const policyBytes = Buffer.from(policyPublicKey, 'ascii');
  const applicationPolicyPublicKey = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 })
    .publicKey.export({ type: 'spki', format: 'pem' });
  const applicationPolicyBytes = Buffer.from(applicationPolicyPublicKey, 'ascii');
  const studentSystemPolicyPublicKey = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 })
    .publicKey.export({ type: 'spki', format: 'pem' });
  const studentSystemPolicyBytes = Buffer.from(studentSystemPolicyPublicKey, 'ascii');
  const veyonPath = 'synthetic-campus-public.pem';
  const policyPath = 'website-policy-public.pem';
  const packageId = crypto.randomUUID();
  const manifest = {
    schemaVersion,
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
    }
  };
  if (schemaVersion >= 4) {
    manifest.applicationPolicyPublicKey = {
      path: 'application-policy-public.pem',
      size: applicationPolicyBytes.length,
      sha256: crypto.createHash('sha256').update(applicationPolicyBytes).digest('hex')
    };
    manifest.compatibility = {
      studentApp: { minInclusive: '0.4.47', maxExclusive: '0.4.48' },
      veyon: { minInclusive: '4.11.2.0', maxExclusive: '4.11.2.1' }
    };
  }
  if (schemaVersion === 5) {
    manifest.studentSystemPolicyPublicKey = {
      path: 'student-system-policy-public.pem',
      size: studentSystemPolicyBytes.length,
      sha256: crypto.createHash('sha256').update(studentSystemPolicyBytes).digest('hex')
    };
    manifest.compatibility.studentAgent = { minInclusive: '0.4.38', maxExclusive: '0.5.0' };
  }
  const campus = {
    campus: campusName,
    computerPrefix,
    keyFile: veyonPath,
    websitePolicyKeyFile: policyPath
  };
  if (schemaVersion >= 4) campus.applicationPolicyKeyFile = manifest.applicationPolicyPublicKey.path;
  if (schemaVersion === 5) campus.systemPolicyKeyFile = manifest.studentSystemPolicyPublicKey.path;
  const payloadFiles = [
    { fileName: 'campus.json', bytes: Buffer.from(JSON.stringify(campus), 'utf8') },
    { fileName: veyonPath, bytes: veyonBytes },
    { fileName: policyPath, bytes: policyBytes }
  ];
  if (schemaVersion >= 4) {
    payloadFiles.push({ fileName: manifest.applicationPolicyPublicKey.path, bytes: applicationPolicyBytes });
    if (schemaVersion === 5)
      payloadFiles.push({ fileName: manifest.studentSystemPolicyPublicKey.path, bytes: studentSystemPolicyBytes });
    payloadFiles.push({ fileName: 'README.md', bytes: Buffer.from('# Synthetic package\n', 'utf8') });
    manifest.files = payloadFiles.map((file) => ({
      path: file.fileName,
      size: file.bytes.length,
      sha256: crypto.createHash('sha256').update(file.bytes).digest('hex')
    }));
  }
  const packageFiles = [
    { fileName: 'manifest.json', bytes: Buffer.from(JSON.stringify(manifest), 'utf8') },
    ...payloadFiles
  ];
  return { packageId, campusName, computerPrefix, manifest, packageFiles,
    canonical: canonicalizeFolderFiles(packageFiles) };
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

function createMockCloudBase() {
  const packageFixture = createPackageFixture();
  const downloadAttempts = new Map();
  const releaseRows = [
    {
      release_id: fixtureReleaseId,
      role: 'StudentSetup',
      product: 'VeyonCampus.StudentSetup',
      version: '1.9.0',
      architecture: 'win-x64',
      file_name: 'VeyonCampus-Student-Setup-1.9.0-win-x64.exe',
      object_key: `releases/StudentSetup/win-x64/${fixtureReleaseId.replace(/-/g, '')}.exe`,
      size_bytes: 256,
      sha256: 'A'.repeat(64),
      signature_algorithm: 'RSA-PSS-SHA256',
      signature: releaseSignature,
      published_at: '2026-09-29T01:00:00Z',
      status: 'published'
    },
    {
      release_id: 'b9d297bc-0e9a-45a2-9c09-7b299af084a9',
      role: 'StudentSetup',
      product: 'VeyonCampus.StudentSetup',
      version: '1.10.0',
      architecture: 'win-x64',
      file_name: 'VeyonCampus-Student-Setup-1.10.0-win-x64.exe',
      object_key: 'releases/StudentSetup/win-x64/b9d297bc0e9a45a29c097b299af084a9.exe',
      size_bytes: 512,
      sha256: 'B'.repeat(64),
      signature_algorithm: 'RSA-PSS-SHA256',
      signature: releaseSignature,
      manifest_schema_version: 3,
      student_system_policy_capability: 1,
      application_policy_capability: 1,
      legacy_signature: releaseSignature,
      legacy_system_policy_signature: releaseSignature,
      published_at: '2026-09-30T01:00:00Z',
      status: 'published'
    },
    {
      release_id: '5c7e2bad-7a04-47ea-a607-092a354130f7',
      role: 'TeacherConsole',
      product: 'VeyonCampus.TeacherConsole',
      version: '0.4.40',
      architecture: 'win-x64',
      file_name: 'VeyonCampus-Teacher-Setup-0.4.40-win-x64.exe',
      object_key: 'releases/TeacherConsole/win-x64/5c7e2bad7a0447eaa607092a354130f7.exe',
      size_bytes: 768,
      sha256: 'C'.repeat(64),
      signature_algorithm: 'RSA-PSS-SHA256',
      signature: releaseSignature,
      manifest_schema_version: 2,
      student_system_policy_capability: 1,
      legacy_signature: releaseSignature,
      published_at: '2026-10-01T01:00:00Z',
      status: 'published'
    }
  ];
  const state = {
    packageFixture,
    releaseRows,
    storedPackage: null,
    uploadedObjectKey: null,
    legacyStorageKey: false,
    publishedPackage: null,
    packageStatus: 'published',
    publishResponseLost: false,
    publishResponseStatus: null,
    deletedPackageCount: 0,
    phoneFingerprint: null,
    heartbeatRpc: null,
    heartbeatRpcCalls: [],
    heartbeatLookup: null,
    heartbeatPackageOverrides: new Map(),
    releaseSignRequest: null,
    releaseLookupUnavailable: false,
    seenAuthorizationHeaders: [],
    adminRoleQueryAuthorizations: [],
    adminTableAuthorizations: [],
    adminRoles: {
      'fixture-owner-user': 'owner',
      'fixture-admin-user': 'admin',
      'fixture-viewer-user': 'viewer'
    },
    adminDatabaseRows: {
      application_releases: [
        { release_id: '00112233-4455-6677-8899-aabbccddeeff', object_key: 'releases/private.exe' }
      ],
      campus_daily_teacher_heartbeats: [
        { day_hkt: '2026-10-02', campus_identity_digest: 'A'.repeat(64), publisher_digest: 'B'.repeat(64) }
      ],
      deployment_package_artifacts: [
        {
          package_id: '00112233-4455-6677-8899-aabbccddeeff',
          storage_key: 'deployment-packages/v3/private-one.zip',
          created_at: '2026-10-01T00:00:00.000Z'
        },
        {
          package_id: '10112233-4455-6677-8899-aabbccddeeff',
          storage_key: 'deployment-packages/v3/private-two.zip',
          created_at: '2026-10-02T00:00:00.000Z'
        }
      ],
      deployment_package_download_attempts: [
        { package_id: '00112233-4455-6677-8899-aabbccddeeff', client_fingerprint: 'C'.repeat(64) }
      ],
      deployment_packages: [
        {
          package_id: '00112233-4455-6677-8899-aabbccddeeff',
          publisher_identity_fingerprint: 'D'.repeat(64),
          publisher_phone_fingerprint: 'E'.repeat(64)
        }
      ],
      telemetry_daily_deployment_devices: [
        { day_hkt: '2026-10-02', installation_digest: 'F'.repeat(64) }
      ],
      telemetry_daily_hkt_devices: [
        {
          day_hkt: '2026-10-02',
          installation_digest: 'A'.repeat(64),
          recorded_at: '2026-10-02T01:00:00.000Z'
        }
      ]
    }
  };

  async function fetchMock(urlValue, options = {}) {
    const url = new URL(urlValue);
    const headers = new Headers(options.headers || {});
    state.seenAuthorizationHeaders.push(headers.get('authorization'));

    if (url.pathname === '/auth/v1/user/me') {
      const token = (headers.get('authorization') || '').replace(/^Bearer /i, '');
      const userId = token === 'fixture-owner-token' ? 'fixture-owner-user'
        : token === 'fixture-admin-token' ? 'fixture-admin-user'
          : token === 'fixture-viewer-token' ? 'fixture-viewer-user'
            : token === 'fixture-blocked-token' ? 'fixture-blocked-user'
              : token === 'fixture-statusless-token' ? 'fixture-statusless-user' : null;
      if (!userId) return new Response(JSON.stringify({ error: 'Unauthorized' }), { status: 401 });
      if (token === 'fixture-statusless-token') return responseJson({ sub: userId });
      if (token === 'fixture-blocked-token') return responseJson({ sub: userId, status: 'BLOCKED' });
      return responseJson({ sub: userId, status: 'ACTIVE' });
    }

    if (url.pathname === '/v1/rdb/rest/admin_profiles') {
      state.adminRoleQueryAuthorizations.push(headers.get('authorization'));
      const userId = (url.searchParams.get('user_id') || '').replace(/^eq\./, '');
      const role = state.adminRoles[userId];
      return responseJson(role ? [{ role }] : []);
    }

    const adminTable = /^\/v1\/rdb\/rest\/([a-z_]+)$/.exec(url.pathname);
    if (adminTable && url.searchParams.has('offset') &&
        Object.hasOwn(state.adminDatabaseRows, adminTable[1])) {
      state.adminTableAuthorizations.push(headers.get('authorization'));
      const limit = Number(url.searchParams.get('limit') || 100);
      const offset = Number(url.searchParams.get('offset') || 0);
      return responseJson(state.adminDatabaseRows[adminTable[1]].slice(offset, offset + limit));
    }

    if (url.pathname === '/v1/rdb/rest/application_releases') {
      const filterValue = (name) => (url.searchParams.get(name) || '').replace(/^eq\./, '');
      if (state.releaseLookupUnavailable)
        return new Response(JSON.stringify({ error: 'release catalog unavailable' }), { status: 503 });
      const roleFilter = url.searchParams.get('role') || '';
      const roles = roleFilter.startsWith('in.(')
        ? roleFilter.slice(4, -1).split(',')
        : roleFilter.startsWith('eq.') ? [roleFilter.slice(3)] : [];
      return responseJson(state.releaseRows.filter((release) =>
        (!filterValue('release_id') || release.release_id === filterValue('release_id')) &&
        (roles.length === 0 || roles.includes(release.role)) &&
        (!filterValue('architecture') || release.architecture === filterValue('architecture')) &&
        (!filterValue('status') || release.status === filterValue('status'))));
    }

    if (url.pathname === '/v1/rdb/rest/deployment_packages') {
      state.heartbeatLookup = Object.fromEntries(url.searchParams);
      const requestedPackageId = (url.searchParams.get('package_id') || '').replace(/^eq\./, '');
      const compactRequestedPackageId = requestedPackageId.replace(/-/g, '').toLowerCase();
      const compactStoredPackageId = (state.publishedPackage?.p_package_id || '').replace(/-/g, '').toLowerCase();
      const override = state.heartbeatPackageOverrides.get(compactRequestedPackageId);
      if (override) return responseJson([override]);
      if (!state.publishedPackage || compactRequestedPackageId !== compactStoredPackageId ||
          state.packageStatus !== 'published') return responseJson([]);
      return responseJson([{
        campus_id: null,
        campus_name: state.packageFixture.campusName,
        computer_prefix: state.packageFixture.computerPrefix,
        status: state.packageStatus
      }]);
    }

    if (url.pathname.startsWith('/v1/rdb/rest/rpc/')) {
      const rpcName = decodeURIComponent(url.pathname.split('/').at(-1));
      const body = JSON.parse(options.body || '{}');
      if (rpcName === 'publish_deployment_package_public') {
        if (state.publishResponseStatus) {
          const status = state.publishResponseStatus;
          state.publishResponseStatus = null;
          return new Response(JSON.stringify({ error: 'duplicate key violates unique constraint' }), {
            status,
            headers: { 'Content-Type': 'application/json' }
          });
        }
        state.publishedPackage = body;
        state.phoneFingerprint = body.p_phone_fingerprint;
        if (state.publishResponseLost) {
          state.publishResponseLost = false;
          throw new Error('Simulated lost RPC response after commit.');
        }
        return responseJson(null);
      }
      if (rpcName === 'search_deployment_packages') {
        const packageId = state.publishedPackage?.p_package_id;
        if (!packageId) return responseJson([]);
        return responseJson([{
          package_id: state.packageFixture.packageId.toLowerCase(),
          campus_id: null,
          display_name: `${state.packageFixture.campusName} / ${state.packageFixture.computerPrefix}`,
          campus_name: state.packageFixture.campusName,
          computer_prefix: state.packageFixture.computerPrefix,
          schema_version: state.publishedPackage.p_schema_version || 3,
          target_os: 'windows',
          architecture: 'x64',
          artifact_file_name: createCampusPackageFileName(state.packageFixture.campusName, packageId),
          artifact_size_bytes: state.storedPackage?.length || 1,
          artifact_sha256: state.publishedPackage.p_artifact_sha256,
          download_count: 0,
          published_at: '2026-09-30T01:00:00Z',
          requires_phone_verification: true
        }]);
      }
      if (rpcName === 'get_deployment_package_download_with_rate_limit') {
        state.lastDownloadClientFingerprint = body.p_client_fingerprint;
        const packageId = state.publishedPackage?.p_package_id;
        if (!packageId || packageId !== body.p_package_id || state.packageStatus !== 'published')
          return responseJson([{ decision: 'not_found', retry_after_seconds: 0 }]);
        if (!/^[A-F0-9]{64}$/.test(body.p_client_fingerprint))
          throw new Error('Download limiter received an invalid client fingerprint.');

        const key = `${packageId}:${body.p_client_fingerprint}`;
        const now = Date.now();
        let attempt = downloadAttempts.get(key);
        if (attempt?.blockedUntil > now) {
          return responseJson([{
            decision: 'blocked',
            retry_after_seconds: Math.ceil((attempt.blockedUntil - now) / 1000)
          }]);
        }
        if (!attempt || now - attempt.windowStartedAt >= 15 * 60 * 1000) {
          attempt = { windowStartedAt: now, failures: 0, blockedUntil: 0 };
          downloadAttempts.set(key, attempt);
        }
        if (body.p_phone_fingerprint !== state.phoneFingerprint) {
          attempt.failures++;
          if (attempt.failures >= 10) {
            attempt.blockedUntil = now + 15 * 60 * 1000;
            return responseJson([{
              decision: 'blocked',
              retry_after_seconds: Math.ceil((attempt.blockedUntil - now) / 1000)
            }]);
          }
          return responseJson([{ decision: 'invalid', retry_after_seconds: 0 }]);
        }

        downloadAttempts.delete(key);
        return responseJson([{
          decision: 'authorized',
          retry_after_seconds: 0,
          package_id: packageId,
          storage_key: state.legacyStorageKey
            ? `deployment-packages/v${state.publishedPackage.p_schema_version || 3}/${packageId}.zip`
            : createCampusPackageObjectKey(state.packageFixture.campusName, packageId, state.publishedPackage.p_schema_version || 3),
          artifact_file_name: createCampusPackageFileName(state.packageFixture.campusName, packageId),
          artifact_size_bytes: state.storedPackage.length,
          artifact_sha256: state.publishedPackage.p_artifact_sha256
        }]);
      }
      if (rpcName === 'record_deployment_package_download') return responseJson(null);
      if (rpcName === 'record_campus_teacher_heartbeat_v1') {
        state.heartbeatRpc = body;
        state.heartbeatRpcCalls.push(body);
        return responseJson(null);
      }
      throw new Error(`Unexpected CloudBase RPC: ${rpcName}`);
    }

    if (url.pathname.startsWith('/v1/storages/object/sign/')) {
      state.releaseSignRequest = JSON.parse(options.body || '{}');
      const objectPath = state.releaseSignRequest.paths[0];
      return responseJson([{
        path: objectPath,
        signedURL: `/object/sign/application-release-artifacts/${objectPath}?token=fixture`,
        error: null
      }]);
    }

    if (url.pathname.startsWith('/v1/storages/object/deployment-package-artifacts/')) {
      if (options.method === 'POST') {
        state.storedPackage = Buffer.from(options.body);
        state.uploadedObjectKey = decodeURIComponent(
          url.pathname.slice('/v1/storages/object/deployment-package-artifacts/'.length));
        return new Response(null, { status: 200 });
      }
      if (options.method === 'DELETE') {
        state.deletedPackageCount++;
        state.storedPackage = null;
        return new Response(null, { status: 200 });
      }
      if (options.method === 'GET' && state.storedPackage) {
        state.lastDownloadedObjectKey = decodeURIComponent(
          url.pathname.slice('/v1/storages/object/deployment-package-artifacts/'.length));
        return new Response(state.storedPackage, { status: 200 });
      }
    }

    throw new Error(`Unexpected CloudBase request: ${options.method || 'GET'} ${url.pathname}`);
  }

  return { state, fetchMock };
}

test('anonymous package, release, and campus heartbeat APIs work end to end against a CloudBase contract double', async () => {
  const originalFetch = global.fetch;
  const mockCloudBase = createMockCloudBase();
  global.fetch = mockCloudBase.fetchMock;
  const callerAuthorizationHeaders = [];
  const config = loadConfig({
    CloudBase__EnvId: 'fixture-env',
    CloudBase__ApiKey: 'fixture-service-role-key',
    Telemetry__DailyHashKey: Buffer.alloc(32, 0x55).toString('base64'),
    CloudBase__ApplicationReleasePublicBaseUrl: 'https://fixture.example/'
  });
  const apiHandler = createRequestHandler(config);
  const server = http.createServer((request, response) => {
    callerAuthorizationHeaders.push(request.headers.authorization || null);
    void apiHandler(request, response);
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const address = server.address();
  const baseUrl = `http://127.0.0.1:${address.port}`;

  try {
    const adminPageResponse = await originalFetch(
      `${baseUrl}/v1/admin/database/deployment_package_artifacts?page=1&pageSize=1`,
      { headers: { Authorization: 'Bearer fixture-owner-token' } });
    assert.equal(adminPageResponse.status, 200);
    assert.match(adminPageResponse.headers.get('cache-control') || '', /no-store/i);
    const adminPage = await adminPageResponse.json();
    assert.equal(adminPage.table, 'deployment_package_artifacts');
    assert.equal(adminPage.hasMore, true);
    assert.deepEqual(adminPage.redactedFields, ['storage_key']);
    assert.equal(adminPage.rows.length, 1);
    assert.equal(adminPage.rows[0].storage_key, '[已隐藏]');

    const adminNextPageResponse = await originalFetch(
      `${baseUrl}/v1/admin/database/deployment_package_artifacts?page=2&pageSize=1`,
      { headers: { Authorization: 'Bearer fixture-admin-token' } });
    assert.equal(adminNextPageResponse.status, 200);
    const adminNextPage = await adminNextPageResponse.json();
    assert.equal(adminNextPage.hasMore, false);
    assert.equal(adminNextPage.rows.length, 1);

    const digestPageResponse = await originalFetch(
      `${baseUrl}/v1/admin/database/telemetry_daily_hkt_devices`,
      { headers: { Authorization: 'Bearer fixture-owner-token' } });
    assert.equal(digestPageResponse.status, 200);
    const digestPage = await digestPageResponse.json();
    assert.deepEqual(digestPage.redactedFields, ['installation_digest']);
    assert.equal(digestPage.rows[0].installation_digest, '[已隐藏]');

    const redactedCases = [
      ['application_releases', ['object_key']],
      ['campus_daily_teacher_heartbeats', ['campus_identity_digest', 'publisher_digest']],
      ['deployment_package_download_attempts', ['client_fingerprint']],
      ['deployment_packages', ['publisher_identity_fingerprint', 'publisher_phone_fingerprint']],
      ['telemetry_daily_deployment_devices', ['installation_digest']]
    ];
    for (const [tableName, fields] of redactedCases) {
      const response = await originalFetch(`${baseUrl}/v1/admin/database/${tableName}`, {
        headers: { Authorization: 'Bearer fixture-owner-token' }
      });
      assert.equal(response.status, 200);
      const payload = await response.json();
      assert.deepEqual(payload.redactedFields, fields);
      for (const field of fields) assert.equal(payload.rows[0][field], '[已隐藏]');
    }

    const viewerAdminPage = await originalFetch(
      `${baseUrl}/v1/admin/database/deployment_package_artifacts`,
      { headers: { Authorization: 'Bearer fixture-viewer-token' } });
    assert.equal(viewerAdminPage.status, 403);

    const invalidAdminPage = await originalFetch(
      `${baseUrl}/v1/admin/database/deployment_package_artifacts`,
      { headers: { Authorization: 'Bearer invalid-fixture-token' } });
    assert.equal(invalidAdminPage.status, 401);

    const unknownTablePage = await originalFetch(
      `${baseUrl}/v1/admin/database/unknown_table`,
      { headers: { Authorization: 'Bearer fixture-owner-token' } });
    assert.equal(unknownTablePage.status, 404);

    const blockedAdminPage = await originalFetch(
      `${baseUrl}/v1/admin/database/deployment_package_artifacts`,
      { headers: { Authorization: 'Bearer fixture-blocked-token' } });
    assert.equal(blockedAdminPage.status, 401);

    const statuslessAdminPage = await originalFetch(
      `${baseUrl}/v1/admin/database/deployment_package_artifacts`,
      { headers: { Authorization: 'Bearer fixture-statusless-token' } });
    assert.equal(statuslessAdminPage.status, 401);

    const oversizedPage = await originalFetch(
      `${baseUrl}/v1/admin/database/deployment_package_artifacts?pageSize=51`,
      { headers: { Authorization: 'Bearer fixture-owner-token' } });
    assert.equal(oversizedPage.status, 400);
    const protectedApiCallCount = callerAuthorizationHeaders.length;
    assert.equal(mockCloudBase.state.adminRoleQueryAuthorizations.length, 4 + redactedCases.length);
    assert.ok(mockCloudBase.state.adminRoleQueryAuthorizations.every(
      value => value === 'Bearer fixture-service-role-key'));
    assert.equal(mockCloudBase.state.adminTableAuthorizations.length, 3 + redactedCases.length);
    assert.ok(mockCloudBase.state.adminTableAuthorizations.every(
      value => value === 'Bearer fixture-service-role-key'));

    const packageFixture = mockCloudBase.state.packageFixture;
    const boundary = 'VeyonCampusBoundary9a7f';
    const createPublicationBody = (campusName, fixture = packageFixture) => Buffer.concat([
      multipartField(boundary, 'campusName', campusName),
      multipartField(boundary, 'publisherName', 'Synthetic Teacher'),
      multipartField(boundary, 'teacherPhoneLast4', '2468'),
      multipartFile(boundary, 'archive', 'ignored-name.zip', fixture.canonical.archiveBytes, 'application/zip'),
      Buffer.from(`--${boundary}--\r\n`, 'ascii')
    ]);

    const mislabeledPublish = await originalFetch(`${baseUrl}/v1/deployment-packages`, {
      method: 'POST',
      headers: { 'Content-Type': `multipart/form-data; boundary=${boundary}` },
      body: createPublicationBody('Different Campus')
    });
    assert.equal(mislabeledPublish.status, 400);
    assert.equal(mockCloudBase.state.storedPackage, null);
    assert.equal(mockCloudBase.state.publishedPackage, null);

    const publishResponse = await originalFetch(`${baseUrl}/v1/deployment-packages`, {
      method: 'POST',
      headers: { 'Content-Type': `multipart/form-data; boundary=${boundary}` },
      body: createPublicationBody(packageFixture.campusName)
    });
    assert.equal(publishResponse.status, 201);
    const published = await publishResponse.json();
    assert.equal(published.packageId, packageFixture.packageId.toLowerCase());
    assert.equal(Object.hasOwn(published, 'publisherName'), false);
    assert.equal(published.fileName, createCampusPackageFileName(packageFixture.campusName, packageFixture.canonical.packageId));
    assert.equal(mockCloudBase.state.uploadedObjectKey,
      createCampusPackageObjectKey(packageFixture.campusName, packageFixture.canonical.packageId));
    assert.equal(published.sha256, crypto.createHash('sha256')
      .update(packageFixture.canonical.archiveBytes).digest('hex').toUpperCase());

    const searchResponse = await originalFetch(`${baseUrl}/v1/deployment-packages?query=Synthetic%20API`);
    assert.equal(searchResponse.status, 200);
    const searchResult = await searchResponse.json();
    assert.equal(searchResult.items[0].packageId, published.packageId);
    assert.equal(searchResult.items[0].fileName, published.fileName);
    assert.equal(searchResult.items[0].displayName, packageFixture.campusName);
    assert.equal(searchResult.items[0].displayName.includes(packageFixture.computerPrefix), false);

    const wrongSuffixResponse = await originalFetch(`${baseUrl}/v1/deployment-packages/${published.packageId}/download`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ teacherPhoneLast4: '9999' })
    });
    assert.equal(wrongSuffixResponse.status, 403);

    const downloadResponse = await originalFetch(`${baseUrl}/v1/deployment-packages/${published.packageId}/download`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ teacherPhoneLast4: '2468' })
    });
    assert.equal(downloadResponse.status, 200);
    const downloadedArchive = Buffer.from(await downloadResponse.arrayBuffer());
    assert.deepEqual(downloadedArchive, packageFixture.canonical.archiveBytes);
    assert.equal(downloadResponse.headers.get('content-disposition'),
      `attachment; filename="campus-package.zip"; filename*=UTF-8''${encodeURIComponent(published.fileName)}`);
    assert.equal(mockCloudBase.state.lastDownloadedObjectKey,
      createCampusPackageObjectKey(packageFixture.campusName, packageFixture.canonical.packageId));
    assert.match(mockCloudBase.state.lastDownloadClientFingerprint, /^[A-F0-9]{64}$/);

    mockCloudBase.state.legacyStorageKey = true;
    const legacyDownload = await originalFetch(`${baseUrl}/v1/deployment-packages/${published.packageId}/download`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ teacherPhoneLast4: '2468' })
    });
    assert.equal(legacyDownload.status, 200);
    assert.equal(mockCloudBase.state.lastDownloadedObjectKey,
      `deployment-packages/v3/${packageFixture.canonical.packageId}.zip`);
    mockCloudBase.state.legacyStorageKey = false;

    const rateLimitedAddress = '198.51.100.42';
    for (let attempt = 0; attempt < 10; attempt++) {
      const failedAttempt = await originalFetch(
        `${baseUrl}/v1/deployment-packages/${published.packageId}/download`, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'X-Forwarded-For': `203.0.113.${attempt + 1}, ${rateLimitedAddress}`,
            'X-Original-Forwarded-For': `192.0.2.${attempt + 1}`
          },
          body: JSON.stringify({ teacherPhoneLast4: '9999' })
        });
      assert.equal(failedAttempt.status, attempt === 9 ? 429 : 403);
    }
    const blockedCorrectSuffix = await originalFetch(
      `${baseUrl}/v1/deployment-packages/${published.packageId}/download`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Forwarded-For': `192.0.2.250, ${rateLimitedAddress}`,
          'X-Original-Forwarded-For': '192.0.2.250'
        },
        body: JSON.stringify({ teacherPhoneLast4: '2468' })
      });
    assert.equal(blockedCorrectSuffix.status, 429);
    const unaffectedAddress = await originalFetch(
      `${baseUrl}/v1/deployment-packages/${published.packageId}/download`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Forwarded-For': '198.51.100.250, 198.51.100.43',
          'X-Original-Forwarded-For': rateLimitedAddress
        },
        body: JSON.stringify({ teacherPhoneLast4: '2468' })
      });
    assert.equal(unaffectedAddress.status, 200);

    const latestResponse = await originalFetch(`${baseUrl}/v1/releases/latest?role=StudentSetup&architecture=win-x64`);
    assert.equal(latestResponse.status, 200);
    const latestResult = await latestResponse.json();
    assert.equal(latestResult.release.manifest.version, '1.10.0');
    assert.equal(latestResult.release.manifest.downloadUrl,
      `https://fixture.example/v1/releases/b9d297bc-0e9a-45a2-9c09-7b299af084a9/artifact`);

    const teacherLatestResponse = await originalFetch(`${baseUrl}/v1/releases/latest?role=TeacherConsole&architecture=win-x64`);
    assert.equal(teacherLatestResponse.status, 200);
    const teacherLatestResult = await teacherLatestResponse.json();
    assert.equal(teacherLatestResult.release.manifest.role, 'TeacherConsole');
    assert.equal(teacherLatestResult.release.manifest.product, 'VeyonCampus.TeacherConsole');
    assert.equal(teacherLatestResult.release.manifest.version, '0.4.40');
    assert.equal(teacherLatestResult.release.manifest.fileName,
      'VeyonCampus-Teacher-Setup-0.4.40-win-x64.exe');
    assert.equal(teacherLatestResult.release.manifest.schemaVersion, 1);
    assert.equal(teacherLatestResult.release.signature, releaseSignature);

    const capabilityLatestResponse = await originalFetch(
      `${baseUrl}/v2/releases/latest?role=TeacherConsole&architecture=win-x64`);
    assert.equal(capabilityLatestResponse.status, 200);
    const capabilityLatestResult = await capabilityLatestResponse.json();
    assert.equal(capabilityLatestResult.release.manifest.schemaVersion, 2);
    assert.deepEqual(capabilityLatestResult.release.manifest.policyCapabilities,
      { studentSystemPolicy: 1 });
    assert.equal(capabilityLatestResult.release.signature, releaseSignature);
    const studentV2Response = await originalFetch(
      `${baseUrl}/v2/releases/latest?role=StudentSetup&architecture=win-x64`);
    assert.equal(studentV2Response.status, 200);
    const studentV2Result = await studentV2Response.json();
    assert.equal(studentV2Result.release.manifest.schemaVersion, 2);
    assert.deepEqual(studentV2Result.release.manifest.policyCapabilities, { studentSystemPolicy: 1 });
    assert.equal(studentV2Result.release.signature, releaseSignature);

    const latestV3Response = await originalFetch(
      `${baseUrl}/v3/releases/latest?role=StudentSetup&architecture=win-x64`);
    assert.equal(latestV3Response.status, 200);
    const latestV3Result = await latestV3Response.json();
    assert.equal(latestV3Result.release.manifest.schemaVersion, 3);
    assert.deepEqual(latestV3Result.release.manifest.policyCapabilities,
      { applicationPolicy: 1, studentSystemPolicy: 1 });
    assert.equal(latestV3Result.release.signature, releaseSignature);

    const artifactResponse = await originalFetch(latestResult.release.manifest.downloadUrl.replace(
      'https://fixture.example', baseUrl), { redirect: 'manual' });
    assert.equal(artifactResponse.status, 302);
    assert.equal(artifactResponse.headers.get('cache-control'), 'no-store');
    assert.match(artifactResponse.headers.get('location'), /\/object\/sign\/application-release-artifacts\//);
    assert.equal(mockCloudBase.state.releaseSignRequest.expiresIn, 600);
    assert.equal(mockCloudBase.state.releaseSignRequest.paths[0],
      'releases/StudentSetup/win-x64/b9d297bc0e9a45a29c097b299af084a9.exe');

    const heartbeatResponse = await originalFetch(`${baseUrl}/v1/heartbeat/teacher`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        publisherInstanceId: '1f1a85f4b08e40a2af73bc87f5dfd4f0',
        packageId: published.packageId,
        teacherVersion: '0.4.40',
        studentVersion: '0.4.40',
        configuredComputerCount: 24
      })
    });
    assert.equal(heartbeatResponse.status, 200);
    assert.equal(heartbeatResponse.headers.get('cache-control'), 'no-store');
    const heartbeatResult = await heartbeatResponse.json();
    assert.equal(heartbeatResult.latestReleases.teacherConsole.manifest.version, '0.4.40');
    assert.equal(heartbeatResult.latestReleases.studentSetup.manifest.version, '1.10.0');
    assert.match(mockCloudBase.state.heartbeatRpc.p_publisher_digest, /^[A-F0-9]{64}$/);
    assert.match(mockCloudBase.state.heartbeatRpc.p_campus_identity_digest, /^[A-F0-9]{64}$/);
    const anonymousCampusKey = 'VeyonCampus/TeacherHeartbeat/Campus/v1\nanonymous:' +
      packageFixture.campusName.normalize('NFKC').trim().toLowerCase() + '\n' +
      packageFixture.computerPrefix.trim().toUpperCase();
    assert.equal(mockCloudBase.state.heartbeatRpc.p_campus_identity_digest,
      crypto.createHmac('sha256', Buffer.alloc(32, 0x55)).update(anonymousCampusKey).digest('hex').toUpperCase());
    assert.equal(mockCloudBase.state.heartbeatRpc.p_configured_computer_count, 24);
    assert.equal(mockCloudBase.state.heartbeatRpc.p_student_version, '1.10.0');
    assert.equal(mockCloudBase.state.heartbeatRpc.p_package_id, published.packageId);
    assert.equal(mockCloudBase.state.heartbeatLookup.package_id, `eq.${published.packageId}`);
    assert.equal(Object.hasOwn(mockCloudBase.state.heartbeatRpc, 'publisherInstanceId'), false);

    const duplicateNamePackageIds = [
      '11111111-1111-4111-8111-111111111111',
      '22222222-2222-4222-8222-222222222222'
    ];
    const registeredCampusIds = [731, 984];
    for (let index = 0; index < duplicateNamePackageIds.length; index++) {
      const packageId = duplicateNamePackageIds[index];
      const campusId = registeredCampusIds[index];
      mockCloudBase.state.heartbeatPackageOverrides.set(packageId.replace(/-/g, ''), {
        campus_id: campusId,
        campus_name: 'Repeated Name Campus',
        computer_prefix: 'ROOM-',
        status: 'published'
      });
      const registeredHeartbeat = await originalFetch(`${baseUrl}/v1/heartbeat/teacher`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          publisherInstanceId: '2f1a85f4b08e40a2af73bc87f5dfd4f0',
          packageId,
          teacherVersion: '0.4.40',
          studentVersion: '0.4.40',
          configuredComputerCount: 24
        })
      });
      assert.equal(registeredHeartbeat.status, 200);
      const campusKey = `VeyonCampus/TeacherHeartbeat/Campus/v1\nregistered:${campusId}`;
      const expectedDigest = crypto.createHmac('sha256', Buffer.alloc(32, 0x55))
        .update(campusKey).digest('hex').toUpperCase();
      const recorded = mockCloudBase.state.heartbeatRpcCalls.at(-1);
      assert.equal(recorded.p_package_id, packageId);
      assert.equal(recorded.p_campus_identity_digest, expectedDigest);
      assert.equal(mockCloudBase.state.heartbeatLookup.package_id, `eq.${packageId}`);
    }
    assert.notEqual(
      mockCloudBase.state.heartbeatRpcCalls.at(-2).p_campus_identity_digest,
      mockCloudBase.state.heartbeatRpcCalls.at(-1).p_campus_identity_digest);
    assert.ok(callerAuthorizationHeaders.slice(0, protectedApiCallCount).every((value) => value !== null));
    assert.ok(callerAuthorizationHeaders.slice(protectedApiCallCount).every((value) => value === null));

    const missingTokenPage = await originalFetch(
      `${baseUrl}/v1/admin/database/deployment_package_artifacts`);
    assert.equal(missingTokenPage.status, 401);

    mockCloudBase.state.releaseLookupUnavailable = true;
    const heartbeatWithoutReleaseCatalog = await originalFetch(`${baseUrl}/v1/heartbeat/teacher`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        publisherInstanceId: '1f1a85f4b08e40a2af73bc87f5dfd4f0',
        packageId: published.packageId,
        teacherVersion: '0.4.40',
        studentVersion: '0.4.40',
        configuredComputerCount: 24
      })
    });
    assert.equal(heartbeatWithoutReleaseCatalog.status, 200);
    const heartbeatWithoutReleaseResult = await heartbeatWithoutReleaseCatalog.json();
    assert.deepEqual(heartbeatWithoutReleaseResult.latestReleases, {
      teacherConsole: null,
      studentSetup: null
    });
    assert.equal(mockCloudBase.state.heartbeatRpc.p_student_version, '0.4.40');
    mockCloudBase.state.releaseLookupUnavailable = false;

    const invalidHeartbeat = await originalFetch(`${baseUrl}/v1/heartbeat/teacher`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        publisherInstanceId: '1f1a85f4b08e40a2af73bc87f5dfd4f0',
        packageId: published.packageId,
        teacherVersion: '0.4.40',
        studentVersion: '0.4.40',
        configuredComputerCount: 151
      })
    });
    assert.equal(invalidHeartbeat.status, 400);

    const previousHeartbeatRpc = mockCloudBase.state.heartbeatRpc;
    const missingPackageHeartbeat = await originalFetch(`${baseUrl}/v1/heartbeat/teacher`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        publisherInstanceId: '1f1a85f4b08e40a2af73bc87f5dfd4f0',
        packageId: crypto.randomUUID(),
        teacherVersion: '0.4.40',
        studentVersion: '0.4.40',
        configuredComputerCount: 24
      })
    });
    assert.equal(missingPackageHeartbeat.status, 404);
    assert.strictEqual(mockCloudBase.state.heartbeatRpc, previousHeartbeatRpc);

    const v4Package = createPackageFixture(4);
    mockCloudBase.state.packageFixture = v4Package;
    const v4Publish = await originalFetch(`${baseUrl}/v1/deployment-packages`, {
      method: 'POST',
      headers: { 'Content-Type': `multipart/form-data; boundary=${boundary}` },
      body: createPublicationBody(v4Package.campusName, v4Package)
    });
    assert.equal(v4Publish.status, 201);
    const v4Published = await v4Publish.json();
    assert.equal(v4Published.schemaVersion, 4);
    assert.equal(mockCloudBase.state.publishedPackage.p_schema_version, 4);
    assert.equal(mockCloudBase.state.uploadedObjectKey,
      createCampusPackageObjectKey(v4Package.campusName, v4Package.canonical.packageId, 4));
    const v4Download = await originalFetch(`${baseUrl}/v1/deployment-packages/${v4Published.packageId}/download`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ teacherPhoneLast4: '2468' })
    });
    assert.equal(v4Download.status, 200);
    assert.deepEqual(Buffer.from(await v4Download.arrayBuffer()), v4Package.canonical.archiveBytes);
    assert.equal(mockCloudBase.state.lastDownloadedObjectKey,
      createCampusPackageObjectKey(v4Package.campusName, v4Package.canonical.packageId, 4));

    const v5Package = createPackageFixture(5);
    mockCloudBase.state.packageFixture = v5Package;
    const v5Publish = await originalFetch(`${baseUrl}/v1/deployment-packages`, {
      method: 'POST',
      headers: { 'Content-Type': `multipart/form-data; boundary=${boundary}` },
      body: createPublicationBody(v5Package.campusName, v5Package)
    });
    assert.equal(v5Publish.status, 201);
    const v5Published = await v5Publish.json();
    assert.equal(v5Published.schemaVersion, 5);
    assert.equal(mockCloudBase.state.publishedPackage.p_schema_version, 5);
    assert.equal(mockCloudBase.state.uploadedObjectKey,
      createCampusPackageObjectKey(v5Package.campusName, v5Package.canonical.packageId, 5));
    const v5Download = await originalFetch(`${baseUrl}/v1/deployment-packages/${v5Published.packageId}/download`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ teacherPhoneLast4: '2468' })
    });
    assert.equal(v5Download.status, 200);
    assert.deepEqual(Buffer.from(await v5Download.arrayBuffer()), v5Package.canonical.archiveBytes);
    assert.equal(mockCloudBase.state.lastDownloadedObjectKey,
      createCampusPackageObjectKey(v5Package.campusName, v5Package.canonical.packageId, 5));

    const uncertainPackage = createPackageFixture();
    mockCloudBase.state.packageFixture = uncertainPackage;
    mockCloudBase.state.publishResponseLost = true;
    const uncertainPublishResponse = await originalFetch(`${baseUrl}/v1/deployment-packages`, {
      method: 'POST',
      headers: { 'Content-Type': `multipart/form-data; boundary=${boundary}` },
      body: createPublicationBody(uncertainPackage.campusName, uncertainPackage)
    });
    assert.equal(uncertainPublishResponse.status, 503);
    assert.equal(mockCloudBase.state.deletedPackageCount, 0);
    assert.deepEqual(mockCloudBase.state.storedPackage, uncertainPackage.canonical.archiveBytes);
    assert.equal(mockCloudBase.state.publishedPackage.p_package_id,
      uncertainPackage.packageId.replace(/-/g, ''));
    const uncertainPackageSearch = await originalFetch(`${baseUrl}/v1/deployment-packages?query=Synthetic%20API`);
    assert.equal(uncertainPackageSearch.status, 200);
    assert.equal((await uncertainPackageSearch.json()).items[0].packageId,
      uncertainPackage.packageId.toLowerCase());

    const rejectedPackage = createPackageFixture();
    mockCloudBase.state.packageFixture = rejectedPackage;
    mockCloudBase.state.publishResponseStatus = 409;
    const rejectedPublishResponse = await originalFetch(`${baseUrl}/v1/deployment-packages`, {
      method: 'POST',
      headers: { 'Content-Type': `multipart/form-data; boundary=${boundary}` },
      body: createPublicationBody(rejectedPackage.campusName, rejectedPackage)
    });
    assert.equal(rejectedPublishResponse.status, 409);
    assert.equal(mockCloudBase.state.deletedPackageCount, 1);
    assert.equal(mockCloudBase.state.storedPackage, null);

    assert.equal(compareSemanticVersions('1.10.0', '1.9.0'), 1);
    assert.equal(compareSemanticVersions('9007199254740993.0.0', '9007199254740992.0.0'), 1);
    assert.equal(compareSemanticVersions('1.0.0', '1.0.0-rc.2'), 1);
    assert.equal(compareSemanticVersions('1.0.0-rc.10', '1.0.0-rc.2'), 1);
  } finally {
    global.fetch = originalFetch;
    await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
});

test('live anonymous API check builds a valid synthetic v5 package and requires explicit confirmation', () => {
  const fixture = createSyntheticPackage();
  const parsed = canonicalizeArchive(fixture.archiveBytes);
  assert.equal(parsed.packageId, fixture.packageId);
  assert.equal(fixture.schemaVersion, 5);
  assert.equal(parsed.schemaVersion, 5);
  assert.equal(parsed.campus, fixture.campusName);
  assert.equal(parsed.computerPrefix, fixture.computerPrefix);
  assert.equal(fixture.payloadFileNames.length, 6);
  assert.equal(fixture.manifest.files.length, 6);
  assert.equal(fixture.manifest.applicationPolicyPublicKey.path, 'application-policy-public.pem');
  assert.equal(fixture.manifest.studentSystemPolicyPublicKey.path, 'student-system-policy-public.pem');
  assert.ok(fixture.manifest.compatibility.studentAgent);
  assert.match(fixture.campusName,
    /^Synthetic API E2E [0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
  assert.match(fixture.computerPrefix, /^API-[0-9A-F]{7}-$/);
  assert.throws(() => loadLiveCheckConfiguration([]), /Pass --confirm-live-synthetic-test/);
});

test('v4 application policy package requires a separate key, compatibility ranges, and complete payload digests', () => {
  const fixture = createPackageFixture(4);
  const parsed = canonicalizeArchive(fixture.canonical.archiveBytes);
  assert.equal(parsed.schemaVersion, 4);
  assert.equal(parsed.campus, fixture.campusName);
  assert.equal(parsed.archiveBytes.length, fixture.canonical.archiveBytes.length);
  const incompleteManifest = { ...fixture.manifest };
  delete incompleteManifest.applicationPolicyPublicKey;
  const incompleteFiles = fixture.packageFiles.map(file => file.fileName === 'manifest.json'
    ? { ...file, bytes: Buffer.from(JSON.stringify(incompleteManifest), 'utf8') }
    : file);
  assert.throws(() => canonicalizeFolderFiles(incompleteFiles), /schemaVersion=3\/4\/5/);
  const tamperedKey = fixture.packageFiles.map(file => file.fileName === 'application-policy-public.pem'
    ? { ...file, bytes: Buffer.from(file.bytes.toString('utf8').replace('BEGIN PUBLIC KEY', 'BEGIN PRIVATE KEY'), 'utf8') }
    : file);
  assert.throws(() => canonicalizeFolderFiles(tamperedKey), /does not match|SHA-256|file size|文件大小/i);
  const missingPayload = fixture.packageFiles.map(file => file.fileName === 'manifest.json'
    ? { ...file, bytes: Buffer.from(JSON.stringify({ ...fixture.manifest, files: fixture.manifest.files.slice(1) }), 'utf8') }
    : file);
  assert.throws(() => canonicalizeFolderFiles(missingPayload), /five payload files|五个载荷文件/i);
  const invalidCompatibility = fixture.packageFiles.map(file => file.fileName === 'manifest.json'
    ? { ...file, bytes: Buffer.from(JSON.stringify({ ...fixture.manifest, compatibility: {
      ...fixture.manifest.compatibility,
      studentApp: { minInclusive: '0.4.47', maxExclusive: '0.4.47' }
    } }), 'utf8') }
    : file);
  assert.throws(() => canonicalizeFolderFiles(invalidCompatibility), /minInclusive < maxExclusive/i);
});

test('v5 system policy package requires its dedicated key and Student Agent compatibility range', () => {
  const fixture = createPackageFixture(5);
  const parsed = canonicalizeArchive(fixture.canonical.archiveBytes);
  assert.equal(parsed.schemaVersion, 5);
  assert.equal(parsed.campus, fixture.campusName);
  assert.equal(fixture.manifest.files.length, 6);
  assert.equal(fixture.canonical.archiveBytes.length, parsed.archiveBytes.length);
  const missingSystemKey = { ...fixture.manifest };
  delete missingSystemKey.studentSystemPolicyPublicKey;
  const missingKeyFiles = fixture.packageFiles.map(file => file.fileName === 'manifest.json'
    ? { ...file, bytes: Buffer.from(JSON.stringify(missingSystemKey), 'utf8') }
    : file);
  assert.throws(() => canonicalizeFolderFiles(missingKeyFiles), /schemaVersion=3\/4\/5/);
  const missingAgentRange = { ...fixture.manifest, compatibility: { ...fixture.manifest.compatibility } };
  delete missingAgentRange.compatibility.studentAgent;
  const missingAgentFiles = fixture.packageFiles.map(file => file.fileName === 'manifest.json'
    ? { ...file, bytes: Buffer.from(JSON.stringify(missingAgentRange), 'utf8') }
    : file);
  assert.throws(() => canonicalizeFolderFiles(missingAgentFiles), /studentAgent/);
  const missingPayload = fixture.packageFiles.map(file => file.fileName === 'manifest.json'
    ? { ...file, bytes: Buffer.from(JSON.stringify({ ...fixture.manifest, files: fixture.manifest.files.slice(1) }), 'utf8') }
    : file);
  assert.throws(() => canonicalizeFolderFiles(missingPayload), /six payload files|六个载荷文件/);
});

test('live anonymous API check runs publish, download, and cleanup against a local HTTP double', async () => {
  const fixture = createSyntheticPackage();
  const routePackageId = formatPackageId(fixture.packageId);
  const state = {
    published: false,
    withdrawn: false,
    objectDeleted: false,
    adminPreflightAuthorization: null,
    adminPreflightRequestIndex: null,
    publishRequestIndex: null,
    requestIndex: 0,
    publicAuthorizationHeaders: [],
    withdrawalAuthorization: null,
    storageAuthorization: null,
    deletedObjectKey: null,
    uploadBody: Buffer.alloc(0),
    wrongSuffixStatus: null
  };
  const server = http.createServer((request, response) => {
    void (async () => {
      const url = new URL(request.url || '/', 'http://127.0.0.1');
      const authorization = request.headers.authorization || null;
      const requestBody = async () => {
        const chunks = [];
        for await (const chunk of request) chunks.push(chunk);
        return Buffer.concat(chunks);
      };
      const sendJson = (status, value) => {
        response.writeHead(status, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify(value));
      };

      if (request.method === 'GET' && url.pathname === '/v1/admin/database/admin_profiles') {
        state.adminPreflightAuthorization = authorization;
        state.adminPreflightRequestIndex = state.requestIndex++;
        sendJson(authorization === 'Bearer fixture-admin-token' ? 200 : 401, {
          table: 'admin_profiles', rows: [], page: 1, pageSize: 1
        });
        return;
      }

      if (request.method === 'GET' && url.pathname === '/v1/deployment-packages') {
        state.publicAuthorizationHeaders.push(authorization);
        const campusQuery = url.searchParams.get('query');
          const items = state.published && !state.withdrawn && campusQuery === fixture.campusName
          ? [{
            packageId: routePackageId,
            campusName: fixture.campusName,
            computerPrefix: fixture.computerPrefix,
            fileName: createCampusPackageFileName(fixture.campusName, fixture.packageId.replace(/-/g, '')),
            sizeBytes: fixture.archiveBytes.length
          }]
          : [];
        sendJson(200, { items, limit: 20, offset: 0, hasMore: false });
        return;
      }
      if (request.method === 'POST' && url.pathname === '/v1/deployment-packages') {
        state.publicAuthorizationHeaders.push(authorization);
        state.publishRequestIndex = state.requestIndex++;
        state.uploadBody = await requestBody();
        state.published = true;
        sendJson(201, {
          packageId: routePackageId,
          schemaVersion: fixture.schemaVersion,
          campusName: fixture.campusName,
          computerPrefix: fixture.computerPrefix,
          fileName: createCampusPackageFileName(fixture.campusName, fixture.packageId.replace(/-/g, '')),
          sizeBytes: fixture.archiveBytes.length,
          sha256: crypto.createHash('sha256').update(fixture.archiveBytes).digest('hex').toUpperCase()
        });
        return;
      }
      if (request.method === 'POST' &&
          url.pathname === `/v1/deployment-packages/${routePackageId}/download`) {
        state.publicAuthorizationHeaders.push(authorization);
        const payload = JSON.parse((await requestBody()).toString('utf8'));
        if (payload.teacherPhoneLast4 !== '2468') {
          state.wrongSuffixStatus = 403;
          response.writeHead(403);
          response.end();
          return;
        }
        response.writeHead(200, { 'Content-Type': 'application/zip' });
        response.end(fixture.archiveBytes);
        return;
      }
      if (request.method === 'POST' &&
          url.pathname === `/v1/deployment-packages/${routePackageId}/withdraw`) {
        state.withdrawalAuthorization = authorization;
        state.withdrawn = authorization === 'Bearer fixture-admin-token';
        response.writeHead(state.withdrawn ? 204 : 401);
        response.end();
        return;
      }
      const storageObjectPrefix = `/v1/storages/object/${encodeURIComponent(configuration.packageBucket)}/`;
      if (request.method === 'DELETE' && url.pathname.startsWith(storageObjectPrefix)) {
        state.storageAuthorization = authorization;
        state.deletedObjectKey = url.pathname.slice(storageObjectPrefix.length)
          .split('/').map(decodeURIComponent).join('/');
        const expectedObjectKey = createCampusPackageObjectKey(
          fixture.campusName, fixture.packageId.replace(/-/g, ''));
        state.objectDeleted = authorization === 'Bearer fixture-service-role-key' &&
          state.deletedObjectKey === expectedObjectKey;
        response.writeHead(state.objectDeleted ? 204 : 401);
        response.end();
        return;
      }
      response.writeHead(404);
      response.end();
    })().catch(() => {
      if (!response.headersSent) response.writeHead(500);
      response.end();
    });
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const address = server.address();
  const localBaseAddress = new URL(`http://127.0.0.1:${address.port}/`);
  const configuration = {
    publicApiBaseAddress: localBaseAddress,
    storageApiBaseAddress: localBaseAddress,
    packageBucket: 'fixture-bucket',
    adminBearerToken: 'fixture-admin-token',
    serviceApiKey: 'fixture-service-role-key'
  };

  try {
    const result = await executeLiveCheck(configuration, fixture);
    assert.equal(result.packageId, fixture.packageId);
    assert.equal(state.published, true);
    assert.equal(state.withdrawn, true);
    assert.equal(state.objectDeleted, true);
    assert.equal(state.adminPreflightAuthorization, 'Bearer fixture-admin-token');
    assert.ok(state.adminPreflightRequestIndex < state.publishRequestIndex);
    assert.equal(state.deletedObjectKey, createCampusPackageObjectKey(
      fixture.campusName, fixture.packageId.replace(/-/g, '')));
    assert.equal(state.wrongSuffixStatus, 403);
    assert.ok(state.publicAuthorizationHeaders.length >= 5);
    assert.ok(state.publicAuthorizationHeaders.every((value) => value === null));
    assert.equal(state.withdrawalAuthorization, 'Bearer fixture-admin-token');
    assert.equal(state.storageAuthorization, 'Bearer fixture-service-role-key');
    const uploadText = state.uploadBody.toString('utf8');
    assert.ok(uploadText.includes(fixture.campusName));
    assert.ok(uploadText.includes('teacherPhoneLast4'));
    assert.ok(state.uploadBody.includes(fixture.archiveBytes));
  } finally {
    await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
  }
});

test('live anonymous API check cleans up after an ambiguous publish response', async () => {
  const fixture = createSyntheticPackage();
  const state = {
    publishMayHaveCommitted: false,
    withdrawn: false,
    objectDeleted: false,
    adminPreflightAuthorization: null,
    publishRequestIndex: null,
    requestIndex: 0,
    publicAuthorizationHeaders: [],
    withdrawalAuthorization: null,
    storageAuthorization: null,
    deletedObjectKey: null
  };
  const localBaseAddress = new URL('http://127.0.0.1/');
  const configuration = {
    publicApiBaseAddress: localBaseAddress,
    storageApiBaseAddress: localBaseAddress,
    packageBucket: 'fixture-bucket',
    adminBearerToken: 'fixture-admin-token',
    serviceApiKey: 'fixture-service-role-key'
  };
  const fetchDouble = async (urlValue, options = {}) => {
    const url = new URL(urlValue);
    const method = options.method || 'GET';
    const authorization = new Headers(options.headers || {}).get('authorization');
    if (method === 'GET' && url.pathname === '/v1/admin/database/admin_profiles') {
      state.adminPreflightAuthorization = authorization;
      state.requestIndex++;
      return new Response(JSON.stringify({ table: 'admin_profiles', rows: [], page: 1, pageSize: 1 }), {
        status: authorization === 'Bearer fixture-admin-token' ? 200 : 401,
        headers: { 'Content-Type': 'application/json' }
      });
    }
    if (method === 'GET' && url.pathname === '/v1/deployment-packages') {
      state.publicAuthorizationHeaders.push(authorization);
      return new Response(JSON.stringify({ items: [] }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' }
      });
    }
    if (method === 'POST' && url.pathname === '/v1/deployment-packages') {
      state.publicAuthorizationHeaders.push(authorization);
      state.publishRequestIndex = state.requestIndex++;
      state.publishMayHaveCommitted = true;
      return new Response(JSON.stringify({ error: 'simulated lost publish response' }), { status: 503 });
    }
    if (method === 'POST' && url.pathname.endsWith('/withdraw')) {
      state.withdrawalAuthorization = authorization;
      state.withdrawn = authorization === 'Bearer fixture-admin-token';
      return new Response(null, { status: state.withdrawn ? 204 : 401 });
    }
    const storageObjectPrefix = `/v1/storages/object/${encodeURIComponent(configuration.packageBucket)}/`;
    if (method === 'DELETE' && url.pathname.startsWith(storageObjectPrefix)) {
      state.storageAuthorization = authorization;
      state.deletedObjectKey = url.pathname.slice(storageObjectPrefix.length)
        .split('/').map(decodeURIComponent).join('/');
      const expectedObjectKey = createCampusPackageObjectKey(
        fixture.campusName, fixture.packageId.replace(/-/g, ''));
      state.objectDeleted = authorization === 'Bearer fixture-service-role-key' &&
        state.deletedObjectKey === expectedObjectKey;
      return new Response(null, { status: state.objectDeleted ? 204 : 401 });
    }
    return new Response(null, { status: 404 });
  };

  await assert.rejects(
    executeLiveCheck(configuration, fixture, fetchDouble),
    /Anonymous package API E2E failed: Anonymous package publication returned HTTP 503/
  );
  assert.equal(state.publishMayHaveCommitted, true);
  assert.equal(state.withdrawn, true);
  assert.equal(state.objectDeleted, true);
  assert.equal(state.adminPreflightAuthorization, 'Bearer fixture-admin-token');
  assert.ok(state.publishRequestIndex > 0);
  assert.equal(state.deletedObjectKey, createCampusPackageObjectKey(
    fixture.campusName, fixture.packageId.replace(/-/g, '')));
  assert.ok(state.publicAuthorizationHeaders.every((value) => value === null));
  assert.equal(state.withdrawalAuthorization, 'Bearer fixture-admin-token');
  assert.equal(state.storageAuthorization, 'Bearer fixture-service-role-key');
});

test('live anonymous API check rejects an invalid administrator token before publishing', async () => {
  const fixture = createSyntheticPackage();
  const configuration = {
    publicApiBaseAddress: new URL('http://127.0.0.1/'),
    storageApiBaseAddress: new URL('http://127.0.0.1/'),
    packageBucket: 'fixture-bucket',
    adminBearerToken: 'invalid-fixture-admin-token',
    serviceApiKey: 'fixture-service-role-key'
  };
  let publishRequests = 0;
  const fetchDouble = async (urlValue, options = {}) => {
    const url = new URL(urlValue);
    const authorization = new Headers(options.headers || {}).get('authorization');
    if (url.pathname === '/v1/admin/database/admin_profiles') {
      return new Response(JSON.stringify({ error: 'Unauthorized' }), {
        status: authorization === 'Bearer fixture-admin-token' ? 200 : 401,
        headers: { 'Content-Type': 'application/json' }
      });
    }
    if (url.pathname === '/v1/deployment-packages' && options.method === 'POST')
      publishRequests++;
    return new Response(null, { status: 404 });
  };

  await assert.rejects(
    executeLiveCheck(configuration, fixture, fetchDouble),
    /Administrator cleanup preflight returned HTTP 401; no synthetic package was published\./
  );
  assert.equal(publishRequests, 0);
});

test('package prefix SQL constraint allows prefixes that end in a hyphen', () => {
  const fixture = createPackageFixture();
  assert.equal(fixture.canonical.computerPrefix, 'API-');

  const migration = fs.readFileSync(
    `${__dirname}/../../cloudbase/migrations/20261001090000_align_package_prefix_validation.sql`,
    'utf8'
  );
  assert.match(migration,
    /ADD CONSTRAINT deployment_packages_computer_prefix_check\s+CHECK\s*\(\s*computer_prefix ~ '\^\[A-Za-z0-9\]\[A-Za-z0-9-\]\{0,11\}\$'\s+AND\s+computer_prefix ~ '\[A-Za-z\]'/);
  assert.doesNotMatch(migration, /computer_prefix\s*!~\s*'-\$'/);
});

test('v5 package and release migrations retain legacy protocols while adding policy capabilities', () => {
  const packageMigration = fs.readFileSync(
    `${__dirname}/../../cloudbase/migrations/20261006100000_support_student_system_policy_packages.sql`,
    'utf8');
  assert.match(packageMigration, /schema_version IN \(3, 4, 5\)/);
  assert.match(packageMigration, /deployment-packages\/v\[345\]/);
  assert.match(packageMigration, /publish_deployment_package_public[\s\S]*?NOT IN \(3, 4, 5\)/);

  const releaseMigration = fs.readFileSync(
    `${__dirname}/../../cloudbase/migrations/20261006110000_support_release_policy_capabilities.sql`,
    'utf8');
  assert.match(releaseMigration, /manifest_schema_version IN \(1, 2\)/);
  assert.match(releaseMigration, /legacy_signature/);
  assert.match(releaseMigration, /publish_application_release_v2/);
  assert.match(releaseMigration, /p_student_system_policy_capability/);
  assert.match(releaseMigration, /FROM PUBLIC, anon, authenticated, service_role/);
  assert.match(releaseMigration, /TO service_role/);

  const applicationReleaseMigration = fs.readFileSync(
    `${__dirname}/../../cloudbase/migrations/20261006120000_support_application_policy_release_capability.sql`,
    'utf8');
  assert.match(applicationReleaseMigration, /manifest_schema_version IN \(1, 2, 3\)/);
  assert.match(applicationReleaseMigration, /application_policy_capability/);
  assert.match(applicationReleaseMigration, /legacy_system_policy_signature/);
  assert.match(applicationReleaseMigration, /publish_application_release_v3/);
  assert.match(applicationReleaseMigration, /p_application_policy_capability/);
  assert.match(applicationReleaseMigration, /TO service_role/);
});

test('CloudBase function deployment requires package and release capability migrations in order', () => {
  const deploymentScript = fs.readFileSync(`${__dirname}/../../scripts/deploy-cloudbase-api.sh`, 'utf8');
  const preflightStart = deploymentScript.indexOf('for required_version in');
  const deployStart = deploymentScript.indexOf('fn deploy veyon-api');
  assert.ok(preflightStart >= 0 && deployStart > preflightStart);
  const preflight = deploymentScript.slice(preflightStart, deployStart);
  const requiredMigrations = [
    '20261006100000',
    '20261006110000',
    '20261006120000'
  ];
  let previousIndex = -1;
  for (const migration of requiredMigrations) {
    const index = preflight.indexOf(migration);
    assert.ok(index > previousIndex, `deployment preflight must require ${migration} in order`);
    previousIndex = index;
  }
  assert.match(preflight, /远端尚未确认应用迁移[\s\S]*?已停止部署/);
});

test('API policy permits required paths while the handler authorizes admin database reads', () => {
  const policy = fs.readFileSync(`${__dirname}/../../cloudbase/authz.user.rego`, 'utf8');
  const api = fs.readFileSync(`${__dirname}/index.js`, 'utf8');
  assert.ok(policy.includes('"/health"'));
  assert.ok(policy.includes('"/v1/heartbeat"'));
  assert.ok(policy.includes('"/v1/heartbeat/teacher"'));
  assert.ok(policy.includes('"/v1/releases/latest"'));
  assert.ok(policy.includes('"/v2/releases/latest"'));
  assert.ok(policy.includes('"/v3/releases/latest"'));
  assert.ok(policy.includes('"/v1/deployment-packages"'));
  assert.ok(policy.includes('startswith(input.request.path, "/v1/releases/"'));
  assert.ok(policy.includes('startswith(input.request.path, "/v1/admin/database/"'));
  assert.ok(api.includes("pathname.startsWith('/v1/admin/')"));
  const releaseRoute = api.match(/const releaseArtifact = ([^\r\n]+);/);
  assert.ok(releaseRoute);
  assert.ok(releaseRoute[1].includes('[0-9a-f]{8}-'));
  assert.ok(releaseRoute[1].includes('[0-9a-f]{12}'));
  assert.ok(releaseRoute[1].includes('/artifact$/i'));
  assert.ok(api.includes("request.method === 'GET' && releaseArtifact"));
});

test('OpenAPI describes admin database access, public endpoints, and missing-package behavior', () => {
  const specification = fs.readFileSync(
    `${__dirname}/../../src/VeyonCampus.Telemetry.Server/openapi/deployment-packages.yaml`,
    'utf8'
  ).replace(/\r\n/g, '\n');
  assert.ok(specification.includes('  /health:\n'));
  assert.ok(specification.includes('  /v1/admin/database/{table}:\n'));
  assert.ok(specification.includes('  /v1/heartbeat:\n'));
  assert.ok(specification.includes('  /v1/heartbeat/teacher:\n'));
  assert.ok(specification.includes('campusName must match the NFKC-normalized'));
  const documentedPaths = [...specification.matchAll(/^  (\/[^:\n]+):$/gm)]
    .map((match) => match[1]).sort();
  assert.deepEqual(documentedPaths, [
    '/health',
    '/v1/admin/database/{table}',
    '/v1/admin/releases/dispatch',
    '/v1/admin/releases/dispatch-status',
    '/v1/deployment-packages',
    '/v1/deployment-packages/{packageId}/download',
    '/v1/deployment-packages/{packageId}/withdraw',
    '/v1/heartbeat',
    '/v1/heartbeat/teacher',
    '/v1/releases/latest',
    '/v1/releases/{releaseId}/artifact',
    '/v2/releases/latest',
    '/v3/releases/latest'
  ].sort());
  const teacherHeartbeat = specification.slice(specification.indexOf('  /v1/heartbeat/teacher:\n'));
  assert.match(teacherHeartbeat, /'404': \{ \$ref: '#\/components\/responses\/NotFound' \}/);
  assert.ok(teacherHeartbeat.includes('pseudonymous digest'));
  assert.ok(teacherHeartbeat.includes('latestReleases'));
  assert.ok(teacherHeartbeat.includes("$ref: '#/components/schemas/ApplicationRelease'"));
  const releaseV2 = specification.slice(specification.indexOf('  /v2/releases/latest:\n'));
  assert.ok(releaseV2.includes('studentSystemPolicy'));
  const releaseV3 = specification.slice(specification.indexOf('  /v3/releases/latest:\n'));
  assert.ok(releaseV3.includes('applicationPolicy'));
  assert.ok(releaseV3.includes('studentSystemPolicy'));
  const adminReleaseDispatchStatus = specification.slice(
    specification.indexOf('  /v1/admin/releases/dispatch-status:\n'),
    specification.indexOf('  /v1/admin/releases/dispatch:\n')
  );
  assert.match(adminReleaseDispatchStatus, /security:\s+- CloudBaseAccessToken: \[\]/);
  const adminReleaseDispatch = specification.slice(
    specification.indexOf('  /v1/admin/releases/dispatch:\n'),
    specification.indexOf('  /v1/releases/latest:\n')
  );
  assert.match(adminReleaseDispatch, /security:\s+- CloudBaseAccessToken: \[\]/);
  assert.ok(adminReleaseDispatch.includes('existing GitHub vX.Y.Z tag'));
  const adminDatabase = specification.slice(
    specification.indexOf('  /v1/admin/database/{table}:\n'),
    specification.indexOf('  /v1/releases/latest:\n')
  );
  assert.match(adminDatabase, /CloudBaseAccessToken/);
  assert.match(adminDatabase, /- name: pageSize\s+in: query\s+required: false\s+schema: \{ type: integer, minimum: 1, maximum: 50, default: 25 \}/);
  assert.match(adminDatabase, /redactedFields/);
  assert.match(specification, /PostgreSQL RPC blocks that pair for 15 minutes across function instances/);
  assert.doesNotMatch(specification, /horizontal instances do not share this in-memory limit/);

  const api = fs.readFileSync(`${__dirname}/index.js`, 'utf8');
  const tableMapStart = api.indexOf('const ADMIN_DATABASE_TABLES = Object.freeze({');
  const tableMapEnd = api.indexOf('\n});', tableMapStart);
  assert.ok(tableMapStart >= 0 && tableMapEnd > tableMapStart);
  const backendTables = [...api.slice(tableMapStart, tableMapEnd).matchAll(/^  ([a-z_]+): \{$/gm)]
    .map((match) => match[1]).sort();
  const contractTables = [...adminDatabase.slice(
    adminDatabase.indexOf('- name: table'), adminDatabase.indexOf('- name: page')
  ).matchAll(/^\s+- ([a-z_]+)$/gm)].map((match) => match[1]).sort();
  const websiteData = fs.readFileSync(`${__dirname}/../../website/src/admin-data.js`, 'utf8');
  const websiteInventoryStart = websiteData.indexOf('export const DATABASE_TABLES = [');
  const websiteInventoryEnd = websiteData.indexOf('\n];', websiteInventoryStart);
  assert.ok(websiteInventoryStart >= 0 && websiteInventoryEnd > websiteInventoryStart);
  const websiteTables = [...websiteData.slice(websiteInventoryStart, websiteInventoryEnd)
    .matchAll(/name: '([a-z_]+)'/g)].map((match) => match[1]).sort();
  assert.equal(backendTables.length, 12);
  assert.deepEqual(contractTables, backendTables);
  assert.deepEqual(websiteTables, backendTables);
});

test('release publisher uses the fixed signed-manifest field order and strict SemVer', () => {
  const manifest = {
    schemaVersion: 1,
    product: 'VeyonCampus.StudentSetup',
    role: 'StudentSetup',
    version: '1.10.0',
    architecture: 'win-x64',
    fileName: 'VeyonCampus-Student-Setup-1.10.0-win-x64.exe',
    sizeBytes: 512,
    sha256: 'A'.repeat(64),
    downloadUrl: 'https://fixture.example/v1/releases/b9d297bc-0e9a-45a2-9c09-7b299af084a9/artifact'
  };
  assert.equal(canonicalReleasePayload(manifest).toString('utf8'), JSON.stringify(manifest));
  assert.equal(parseReleaseArguments([
    '--role', 'StudentSetup', '--version', '1.10.0', '--installer', 'student.exe', '--confirm-publication'
  ]).role, 'StudentSetup');
  assert.throws(() => parseReleaseArguments([
    '--role', 'StudentSetup', '--version', '01.10.0', '--installer', 'student.exe', '--confirm-publication'
  ]), /SemVer/);
  const capable = {
    ...manifest,
    schemaVersion: 2,
    policyCapabilities: { studentSystemPolicy: 1 }
  };
  assert.equal(canonicalReleasePayload(capable).toString('utf8'), JSON.stringify(capable));
  assert.equal(JSON.parse(legacyCanonicalReleasePayload(capable).toString('utf8')).schemaVersion, 1);
  const capableV3 = {
    ...manifest,
    schemaVersion: 3,
    policyCapabilities: { applicationPolicy: 1, studentSystemPolicy: 1 }
  };
  assert.equal(canonicalReleasePayload(capableV3).toString('utf8'), JSON.stringify(capableV3));
  const capableV2Payload = JSON.parse(legacySystemPolicyCanonicalPayload(capableV3).toString('utf8'));
  assert.equal(capableV2Payload.schemaVersion, 2);
  assert.deepEqual(capableV2Payload.policyCapabilities, { studentSystemPolicy: 1 });
});

test('release publisher accepts passphrase-protected PKCS#8 signing keys', () => {
  const passphrase = 'fixture-release-passphrase';
  const generated = crypto.generateKeyPairSync('rsa', {
    modulusLength: 2048,
    publicKeyEncoding: { type: 'spki', format: 'pem' },
    privateKeyEncoding: {
      type: 'pkcs8',
      format: 'pem',
      cipher: 'aes-256-cbc',
      passphrase
    }
  });
  const privateKey = createReleaseSigningKey(generated.privateKey, passphrase);
  assert.equal(privateKey.asymmetricKeyType, 'rsa');
  assert.throws(() => createReleaseSigningKey(generated.privateKey, 'incorrect-passphrase'));
});

test('release publisher treats only definitive client rejections as safe to roll back', () => {
  for (const status of [400, 401, 403, 404, 409, 413, 415, 422])
    assert.equal(isDefinitiveCloudBaseRejection(new CloudBaseHttpFailure(status, 'rejected')), true);
  for (const status of [408, 429, 500, 502, 503])
    assert.equal(isDefinitiveCloudBaseRejection(new CloudBaseHttpFailure(status, 'uncertain')), false);
  assert.equal(isDefinitiveCloudBaseRejection(new Error('network timeout')), false);
});

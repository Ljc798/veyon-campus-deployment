#!/usr/bin/env node
'use strict';

const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { createReadStream } = require('node:fs');
const { compareSemver, strictJsonParse } = require('./file-update-planner.cjs');
const { inspectAssets: inspectGitCodeAssets, publishGitCodeRelease, readRemoteTagCommit } = require('./publish-gitcode-release.cjs');
const { publishGitHubFileUpdateRelease } = require('./publish-github-release.cjs');
const { verifyRelease } = require('./file-update-release.cjs');

const MAX_MANIFEST_BYTES = 16 * 1024 * 1024;
const MAX_SIGNATURE_BYTES = 64 * 1024;
const MAX_COMPONENT_PACK_BYTES = 1_900_000_000;
const MAX_RELEASE_SEQUENCE = 2_147_483_647;
const MAX_EVIDENCE_RECORD_BYTES = 2 * 1024 * 1024;
const MAX_PLAN_REPORT_BYTES = 64 * 1024 * 1024;
const G0_FAILURE_SCENARIOS = Object.freeze([
  'before-installer-start',
  'after-student-setup-move',
  'installer-running',
  'agent-staged',
  'task-switched',
  'helper-waiting-old-process',
  'candidate-agent-started',
  'before-health-readback',
  'after-health-readback',
  'transaction-commit',
  'disk-full',
  'target-file-in-use',
  'system-task-creation-failure',
  'system-task-acl-failure',
  'helper-killed',
  'forced-reboot',
  'power-loss'
]);
const GITCODE_OWNER = 'ljc_1126';
const GITCODE_REPOSITORY = 'veyon-campus-deployment';
const GITHUB_OWNER = 'Ljc798';
const GITHUB_REPOSITORY = 'veyon-campus-deployment';

function fail(message, cause) {
  throw new Error(message, cause ? { cause } : undefined);
}

function parseArguments(argv) {
  if (argv.length === 0 || argv.includes('--help') || argv.includes('-h')) return { command: 'help' };
  if (argv[0] !== 'publish') fail("Use 'publish' or --help.");
  const allowed = new Set([
    'release-directory', 'public-key', 'object-store', 'source-commit', 'release-notes',
    'source-plan-review', 'g0-acceptance-record'
  ]);
  const values = Object.create(null);
  for (let index = 1; index < argv.length; index++) {
    const token = argv[index];
    if (!token.startsWith('--')) fail(`Unexpected argument '${token}'.`);
    const key = token.slice(2);
    if (!allowed.has(key)) fail(`Unknown argument '--${key}'.`);
    const value = argv[++index];
    if (value === undefined || value.startsWith('--')) fail(`Missing value for '--${key}'.`);
    if (Object.hasOwn(values, key)) fail(`Duplicate argument '--${key}'.`);
    values[key] = value;
  }
  for (const key of allowed) if (!Object.hasOwn(values, key)) fail(`Missing required '--${key}'.`);
  if (!/^[0-9a-f]{40}$/i.test(values['source-commit'])) fail("'--source-commit' must be a full Git commit SHA.");
  return { command: 'publish', values };
}

function sameFileIdentity(left, right) {
  return left.isFile() && right.isFile() && left.size === right.size && left.dev === right.dev &&
    left.ino === right.ino && left.mtimeMs === right.mtimeMs && left.nlink === right.nlink;
}

function exactKeys(value, expected, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail(`${label} must be a JSON object.`);
  const actual = Object.keys(value).sort();
  const wanted = [...expected].sort();
  if (actual.length !== wanted.length || actual.some((key, index) => key !== wanted[index]))
    fail(`${label} contains missing or unknown fields.`);
}

function readEvidenceFile(repositoryRoot, relativePath, maximumBytes, label) {
  if (typeof relativePath !== 'string' || relativePath.length < 1 || relativePath.length > 512 ||
      path.isAbsolute(relativePath) || relativePath.split(/[\\/]/).some((part) => part === '..' || part === '.' || part === ''))
    fail(`${label} must use a safe repository-relative path.`);
  const root = fs.realpathSync(repositoryRoot);
  const pieces = relativePath.split(/[\\/]/);
  let current = root;
  let before;
  for (let index = 0; index < pieces.length; index++) {
    current = path.join(current, pieces[index]);
    try { before = fs.lstatSync(current); }
    catch { fail(`${label} is missing.`); }
    if (before.isSymbolicLink() || (index < pieces.length - 1 && !before.isDirectory()))
      fail(`${label} path contains a link or non-directory parent.`);
    if (index === pieces.length - 1 && (!before.isFile() || before.nlink > 1 || before.size < 1 || before.size > maximumBytes))
      fail(`${label} must be a regular, unlinked file no larger than ${maximumBytes} bytes.`);
  }
  const bytes = fs.readFileSync(current);
  let after;
  try { after = fs.lstatSync(current); }
  catch { fail(`${label} changed while being read.`); }
  if (!sameFileIdentity(before, after) || bytes.length !== after.size)
    fail(`${label} changed while being read.`);
  return { bytes, sha256: crypto.createHash('sha256').update(bytes).digest('hex'), path: current };
}

function verifyEvidenceReference(repositoryRoot, reference, label) {
  exactKeys(reference, ['path', 'sha256'], label);
  if (typeof reference.sha256 !== 'string' || !/^[a-f0-9]{64}$/.test(reference.sha256))
    fail(`${label} SHA-256 is invalid.`);
  const file = readEvidenceFile(repositoryRoot, reference.path, MAX_PLAN_REPORT_BYTES, label);
  if (file.sha256 !== reference.sha256) fail(`${label} bytes differ from the recorded SHA-256.`);
  return file;
}

function isUtcTimestamp(value) {
  return typeof value === 'string' && value.endsWith('Z') && Number.isFinite(Date.parse(value));
}

function validateSourcePlanReview({ repositoryRoot, reviewPath, manifest, verification }) {
  const recordFile = readEvidenceFile(repositoryRoot, reviewPath, MAX_EVIDENCE_RECORD_BYTES, 'Three-source plan review record');
  const review = strictJsonParse(recordFile.bytes, 'three-source plan review record', MAX_EVIDENCE_RECORD_BYTES);
  exactKeys(review, ['schemaVersion', 'kind', 'status', 'role', 'targetVersion', 'targetManifestSha256',
    'reviewer', 'reviewedAtUtc', 'sourcePlans'], 'Three-source plan review record');
  if (review.schemaVersion !== 1 || review.kind !== 'VeyonCampus.FileUpdatePlanReview' ||
      review.status !== 'passed' || review.role !== manifest.role || review.targetVersion !== manifest.version ||
      review.targetManifestSha256 !== verification.manifestSha256 ||
      typeof review.reviewer !== 'string' || !review.reviewer.trim() || !isUtcTimestamp(review.reviewedAtUtc) ||
      !Array.isArray(review.sourcePlans) || review.sourcePlans.length < 3 || review.sourcePlans.length > 256)
    fail('Three-source plan review must be passed for this exact role and signed target manifest.');
  const versions = new Set();
  const versionPrecedences = [];
  const manifestDigests = new Set();
  const sourceSequences = new Set();
  for (const [index, source] of review.sourcePlans.entries()) {
    exactKeys(source, ['sourceVersion', 'sourceManifestSha256', 'planReportPath', 'planReportSha256'],
      `Source plan review ${index + 1}`);
    if (typeof source.sourceVersion !== 'string' || typeof source.sourceManifestSha256 !== 'string' ||
        !/^[a-f0-9]{64}$/.test(source.sourceManifestSha256) ||
        compareSemver(manifest.version, source.sourceVersion) <= 0 ||
        versions.has(source.sourceVersion) ||
        versionPrecedences.some((version) => compareSemver(version, source.sourceVersion) === 0) ||
        manifestDigests.has(source.sourceManifestSha256))
      fail(`Source plan review ${index + 1} does not identify a distinct older source version.`);
    if (typeof source.planReportSha256 !== 'string' || !/^[a-f0-9]{64}$/.test(source.planReportSha256))
      fail(`Source plan review ${index + 1} report SHA-256 is invalid.`);
    const reportFile = readEvidenceFile(repositoryRoot, source.planReportPath, MAX_PLAN_REPORT_BYTES,
      `Source plan report ${index + 1}`);
    if (reportFile.sha256 !== source.planReportSha256)
      fail(`Source plan report ${index + 1} differs from its recorded SHA-256.`);
    const plan = strictJsonParse(reportFile.bytes, `source plan report ${index + 1}`, MAX_PLAN_REPORT_BYTES);
    if (plan.kind !== 'VeyonCampus.FileUpdatePlan' || plan.status !== 'planned' || plan.deployable !== false ||
        plan.readOnly !== true || plan.trust?.source !== 'verified' || plan.trust?.target !== 'verified' ||
        plan.trust?.targetKeyId !== verification.keyId ||
        plan.source?.role !== manifest.role || plan.source?.version !== source.sourceVersion ||
        plan.source?.manifestSha256 !== source.sourceManifestSha256 ||
        !Number.isSafeInteger(plan.source?.releaseSequence) || plan.source.releaseSequence < 1 ||
        plan.source.releaseSequence >= manifest.releaseSequence ||
        plan.target?.role !== manifest.role || plan.target?.version !== manifest.version ||
        plan.target?.manifestSha256 !== verification.manifestSha256 ||
        plan.target?.releaseSequence !== manifest.releaseSequence ||
        sourceSequences.has(plan.source.releaseSequence))
      fail(`Source plan report ${index + 1} is not a verified, read-only plan for this exact target.`);
    versions.add(source.sourceVersion);
    versionPrecedences.push(source.sourceVersion);
    manifestDigests.add(source.sourceManifestSha256);
    sourceSequences.add(plan.source.releaseSequence);
  }
}

function validateG0Acceptance({ repositoryRoot, acceptancePath }) {
  const recordFile = readEvidenceFile(repositoryRoot, acceptancePath, MAX_EVIDENCE_RECORD_BYTES,
    'G0 Windows acceptance record');
  const record = strictJsonParse(recordFile.bytes, 'G0 Windows acceptance record', MAX_EVIDENCE_RECORD_BYTES);
  exactKeys(record, ['schemaVersion', 'kind', 'status', 'approvedBy', 'approvedAtUtc', 'atomicUpdateCommit',
    'windows10', 'windows11', 'twoDeviceRollout', 'failureInjection'], 'G0 Windows acceptance record');
  if (record.schemaVersion !== 1 || record.kind !== 'VeyonCampus.AtomicUpdateG0Acceptance' ||
      record.status !== 'passed' || typeof record.approvedBy !== 'string' || !record.approvedBy.trim() ||
      !isUtcTimestamp(record.approvedAtUtc) || !/^[0-9a-f]{40}$/i.test(record.atomicUpdateCommit))
    fail('G0 acceptance record is incomplete or is not approved as passed.');
  for (const osKey of ['windows10', 'windows11']) {
    const evidence = record[osKey];
    exactKeys(evidence, ['status', 'successfulUpgrade', 'rollbackVerified', 'recoveryRequiredVerified',
      'evidencePath', 'evidenceSha256'], `G0 ${osKey} evidence`);
    if (evidence.status !== 'passed' || evidence.successfulUpgrade !== true || evidence.rollbackVerified !== true ||
        evidence.recoveryRequiredVerified !== true)
      fail(`G0 ${osKey} success, rollback, and recovery acceptance is incomplete.`);
    verifyEvidenceReference(repositoryRoot, { path: evidence.evidencePath, sha256: evidence.evidenceSha256 },
      `G0 ${osKey} evidence`);
  }
  const rollout = record.twoDeviceRollout;
  exactKeys(rollout, ['status', 'signedCommandVerified', 'lanDeliveryVerified', 'firewallVerified',
    'concurrentUpdateVerified', 'perDeviceReadbackVerified', 'evidencePath', 'evidenceSha256'],
  'G0 two-device rollout evidence');
  if (rollout.status !== 'passed' || ['signedCommandVerified', 'lanDeliveryVerified', 'firewallVerified',
    'concurrentUpdateVerified', 'perDeviceReadbackVerified'].some((key) => rollout[key] !== true))
    fail('G0 two-device command, LAN, firewall, concurrency, and per-device readback acceptance is incomplete.');
  verifyEvidenceReference(repositoryRoot, { path: rollout.evidencePath, sha256: rollout.evidenceSha256 },
    'G0 two-device rollout evidence');
  const failureInjection = record.failureInjection;
  exactKeys(failureInjection, ['status', 'evidencePath', 'evidenceSha256', 'scenarios'], 'G0 failure-injection evidence');
  if (failureInjection.status !== 'passed') fail('G0 failure-injection evidence is not marked passed.');
  exactKeys(failureInjection.scenarios, G0_FAILURE_SCENARIOS, 'G0 failure scenarios');
  if (G0_FAILURE_SCENARIOS.some((scenario) => failureInjection.scenarios[scenario] !== true))
    fail('G0 record does not show a passing result for every required failure scenario.');
  verifyEvidenceReference(repositoryRoot,
    { path: failureInjection.evidencePath, sha256: failureInjection.evidenceSha256 }, 'G0 failure-injection evidence');
  return { atomicUpdateCommit: record.atomicUpdateCommit.toLowerCase() };
}

function verifyG0CommitIsAncestor(atomicUpdateCommit, sourceCommit, repositoryRoot, exec = execFileSync) {
  try {
    exec('git', ['merge-base', '--is-ancestor', atomicUpdateCommit, sourceCommit], {
      cwd: repositoryRoot,
      stdio: 'ignore'
    });
  } catch (error) {
    if (error?.status === 1) fail('The release source commit predates the Windows-tested G0 atomic updater.');
    fail('Could not verify that the G0-tested atomic updater is an ancestor of the release source commit.', error);
  }
  const g0SensitivePaths = [
    'src/VeyonCampus.Core/StudentApplicationUpdateHandoff.cs',
    'src/VeyonCampus.Core/StudentApplicationUpdates.cs',
    'src/VeyonCampus.Core/StudentUpdateAtomicTransaction.cs',
    'src/VeyonCampus.Core/TeacherMobileStatusProtocol.cs',
    'src/VeyonCampus.Agent/Program.cs',
    'tools/VeyonCampus.UpdateHelper/Program.cs',
    'src/VeyonCampus.App/TeacherViewModel.cs'
  ];
  try {
    exec('git', ['diff', '--quiet', atomicUpdateCommit, sourceCommit, '--', ...g0SensitivePaths], {
      cwd: repositoryRoot,
      stdio: 'ignore'
    });
  } catch (error) {
    if (error?.status === 1)
      fail('G0-sensitive updater code changed after Windows acceptance; rerun the affected G0 tests before publishing.');
    fail('Could not compare G0-sensitive updater code with the Windows-accepted commit.', error);
  }
}

function copyRegularFile(sourcePath, targetPath, maximumBytes, label) {
  const absoluteSource = path.resolve(sourcePath);
  let before;
  try { before = fs.lstatSync(absoluteSource); }
  catch { fail(`${label} is missing or unreadable.`); }
  if (!before.isFile() || before.isSymbolicLink() || before.nlink > 1 || before.size < 1 || before.size > maximumBytes)
    fail(`${label} must be a regular, unlinked file no larger than ${maximumBytes} bytes.`);
  let inputFd;
  let outputFd;
  let created = false;
  const hash = crypto.createHash('sha256');
  const buffer = Buffer.allocUnsafe(1024 * 1024);
  let copiedBytes = 0;
  try {
    inputFd = fs.openSync(absoluteSource, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
    const opened = fs.fstatSync(inputFd);
    if (!sameFileIdentity(before, opened)) fail(`${label} changed while it was being opened.`);
    outputFd = fs.openSync(targetPath, fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL, 0o600);
    created = true;
    while (true) {
      const read = fs.readSync(inputFd, buffer, 0, buffer.length, null);
      if (read === 0) break;
      const bytes = buffer.subarray(0, read);
      hash.update(bytes);
      let written = 0;
      while (written < read) {
        const count = fs.writeSync(outputFd, bytes, written, read - written);
        if (count <= 0) fail(`${label} could not be copied completely.`);
        written += count;
      }
      copiedBytes += read;
    }
    if (copiedBytes !== before.size || !sameFileIdentity(opened, fs.fstatSync(inputFd)) ||
        !sameFileIdentity(before, fs.lstatSync(absoluteSource)))
      fail(`${label} changed while it was being copied.`);
    fs.fsyncSync(outputFd);
    const targetStat = fs.fstatSync(outputFd);
    if (!targetStat.isFile() || targetStat.size !== copiedBytes || targetStat.nlink !== 1)
      fail(`${label} staging copy has an invalid file shape.`);
    return { size: copiedBytes, sha256: hash.digest('hex') };
  } catch (error) {
    if (created) {
      try { fs.unlinkSync(targetPath); } catch { /* preserve original failure */ }
    }
    throw error;
  } finally {
    if (outputFd !== undefined) fs.closeSync(outputFd);
    if (inputFd !== undefined) fs.closeSync(inputFd);
  }
}

function fileUpdateTag(manifest) {
  const roleTag = manifest.role === 'TeacherConsole' ? 'teacher' : 'student';
  const channelTag = manifest.channel === 'stable' ? '' : 'beta-';
  return `file-${channelTag}${roleTag}-v${manifest.version}`;
}

function expectedManifestAssetName(manifest) {
  return `VeyonCampus-${manifest.role}-${manifest.version}-win-x64.file-manifest.json`;
}

async function stageVerifiedAssets({ releaseDirectory, objectStore, publicKey, temporaryRoot }) {
  const originalVerification = verifyRelease({
    'release-directory': releaseDirectory,
    'public-key': publicKey,
    'object-store': objectStore
  });
  if (!originalVerification.signatureVerified || !originalVerification.objectsVerified ||
      originalVerification.expired || originalVerification.deployable)
    fail('Local file update package is expired, unverified, or unexpectedly marked deployable.');

  const packageDirectory = path.join(temporaryRoot, 'verified-package');
  const componentPackDirectory = path.join(packageDirectory, 'component-packs');
  fs.mkdirSync(componentPackDirectory, { recursive: true, mode: 0o700 });
  const originalPackDirectory = path.join(path.resolve(releaseDirectory), 'component-packs');
  const originalPackNames = fs.readdirSync(originalPackDirectory).sort();
  const manifestInfo = fs.lstatSync(path.join(path.resolve(releaseDirectory), 'manifest.json'));
  const signatureInfo = fs.lstatSync(path.join(path.resolve(releaseDirectory), 'manifest.sig'));
  const packInfo = originalPackNames.map((name) => fs.lstatSync(path.join(originalPackDirectory, name)));
  const packageBytes = manifestInfo.size + signatureInfo.size + packInfo.reduce((total, info) => total + info.size, 0);
  if (typeof fs.statfsSync !== 'function') fail('The runtime cannot verify temporary staging disk capacity.');
  const disk = fs.statfsSync(temporaryRoot);
  const availableBytes = disk.bavail * disk.bsize;
  const requiredBytes = packageBytes + manifestInfo.size + signatureInfo.size + 64 * 1024 * 1024;
  if (!Number.isSafeInteger(availableBytes) || availableBytes < requiredBytes)
    fail('Insufficient free disk space to stage and independently verify every file update asset.');
  const manifestPath = path.join(packageDirectory, 'manifest.json');
  const signaturePath = path.join(packageDirectory, 'manifest.sig');
  const manifestCopy = copyRegularFile(path.join(path.resolve(releaseDirectory), 'manifest.json'),
    manifestPath, MAX_MANIFEST_BYTES, 'Signed file manifest');
  const signatureCopy = copyRegularFile(path.join(path.resolve(releaseDirectory), 'manifest.sig'),
    signaturePath, MAX_SIGNATURE_BYTES, 'Detached manifest signature');
  for (const name of originalPackNames) {
    copyRegularFile(path.join(originalPackDirectory, name),
      path.join(componentPackDirectory, name), MAX_COMPONENT_PACK_BYTES, `Component pack '${name}'`);
  }

  const copiedVerification = verifyRelease({
    'release-directory': packageDirectory,
    'public-key': publicKey,
    'object-store': objectStore
  });
  if (copiedVerification.manifestSha256 !== originalVerification.manifestSha256 ||
      copiedVerification.keyId !== originalVerification.keyId ||
      copiedVerification.verifiedComponentPackCount !== originalVerification.verifiedComponentPackCount ||
      copiedVerification.verifiedComponentPackBytes !== originalVerification.verifiedComponentPackBytes)
    fail('Staged package verification did not match the original signed release package.');

  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  const fileName = expectedManifestAssetName(manifest);
  const uploadManifestPath = path.join(temporaryRoot, fileName);
  const uploadSignaturePath = path.join(temporaryRoot, fileName.replace(/\.json$/, '.sig'));
  const uploadManifest = copyRegularFile(manifestPath, uploadManifestPath, MAX_MANIFEST_BYTES, 'Upload manifest');
  const uploadSignature = copyRegularFile(signaturePath, uploadSignaturePath, MAX_SIGNATURE_BYTES, 'Upload signature');
  if (uploadManifest.sha256 !== originalVerification.manifestSha256 ||
      uploadSignature.sha256 !== signatureCopy.sha256 || uploadSignature.size < 1)
    fail('Named upload metadata differs from the verified signed release package.');

  const assetPaths = [uploadManifestPath, uploadSignaturePath,
    ...originalPackNames.map((name) => path.join(componentPackDirectory, name))];
  const assets = await inspectGitCodeAssets(assetPaths);
  if (assets.length < 3 || assets.length > 34)
    fail('A file update role release must contain its manifest, signature, and 1 to 32 component packs.');
  for (const asset of assets) {
    try { fs.chmodSync(asset.path, 0o400); }
    catch { fail(`Could not protect staged release asset '${asset.name}'.`); }
  }
  for (const fileName of ['manifest.json', 'manifest.sig']) fs.chmodSync(path.join(packageDirectory, fileName), 0o400);
  for (const name of originalPackNames) fs.chmodSync(path.join(componentPackDirectory, name), 0o400);
  return {
    manifest,
    verification: copiedVerification,
    manifestBytes: manifestCopy.size,
    assetPaths,
    assets: assets.map(({ name, size, sha256 }) => ({ name, size, sha256 })),
    packageBytes,
    tag: fileUpdateTag(manifest)
  };
}

function assertPublisherAssets(provider, published, expected) {
  if (!Array.isArray(published?.assets)) fail(`${provider} publisher returned no verified attachment inventory.`);
  const actual = new Map(published.assets.map((asset) => [asset?.name, asset]));
  if (actual.size !== expected.length || actual.size !== published.assets.length)
    fail(`${provider} publisher returned a different attachment count from the signed package.`);
  for (const asset of expected) {
    const remote = actual.get(asset.name);
    if (!remote || remote.size !== asset.size || remote.sha256 !== asset.sha256)
      fail(`${provider} publisher did not verify the expected bytes for ${asset.name}.`);
  }
}

function cloudBaseApiBase(envId) {
  return `https://${envId}.api.tcloudbasegateway.com/`;
}

async function requestCloudBase(apiBase, apiKey, requestPath, options = {}, fetchImpl = global.fetch) {
  const headers = { Accept: 'application/json', Authorization: `Bearer ${apiKey}` };
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  let response;
  try {
    response = await fetchImpl(new URL(requestPath, apiBase), {
      method: options.method || 'GET',
      headers,
      body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
      signal: AbortSignal.timeout(options.timeoutMs || 30000),
      redirect: 'error'
    });
  } catch (error) {
    fail(`CloudBase request failed (${options.method || 'GET'} ${requestPath}).`, error);
  }
  if (!response.ok) {
    const detail = await response.text().catch(() => '');
    fail(`CloudBase request failed with HTTP ${response.status}${detail ? `: ${detail.slice(0, 384)}` : ''}.`);
  }
  try { return await response.json(); }
  catch { fail(`CloudBase returned invalid JSON for ${options.method || 'GET'} ${requestPath}.`); }
}

function catalogQuery({ role, channel, version = null }) {
  const query = new URLSearchParams({
    select: 'release_id,role,product,version,architecture,channel,release_sequence,manifest_size_bytes,manifest_sha256,manifest_key_id,status',
    role: `eq.${role}`,
    architecture: 'eq.win-x64',
    channel: `eq.${channel}`,
    order: 'release_sequence.desc',
    limit: '1'
  });
  if (version) query.set('version', `eq.${version}`);
  return `v1/rdb/rest/file_update_releases?${query.toString()}`;
}

async function readCatalogRow({ apiBase, apiKey, role, channel, version = null, requestCloudBaseImpl = requestCloudBase }) {
  const rows = await requestCloudBaseImpl(apiBase, apiKey, catalogQuery({ role, channel, version }));
  if (!Array.isArray(rows) || rows.length > 1) fail('CloudBase returned an invalid file update catalog query result.');
  return rows[0] || null;
}

function sameCatalogRelease(row, release) {
  return row?.role === release.manifest.role && row?.product === release.manifest.product &&
    row?.version === release.manifest.version && row?.architecture === 'win-x64' &&
    row?.channel === release.manifest.channel && row?.release_sequence === release.manifest.releaseSequence &&
    row?.manifest_size_bytes === release.verification.manifestBytes &&
    row?.manifest_sha256 === release.verification.manifestSha256 &&
    row?.manifest_key_id === release.verification.keyId;
}

async function readPublishedCatalogRowAfterWrite({ apiBase, apiKey, manifest, verification,
  requestCloudBaseImpl, attempts = 3 }) {
  let lastError = null;
  for (let attempt = 0; attempt < attempts; attempt++) {
    try {
      const row = await readCatalogRow({
        apiBase,
        apiKey,
        role: manifest.role,
        channel: manifest.channel,
        version: manifest.version,
        requestCloudBaseImpl
      });
      if (row) {
        if (row.status !== 'published' || !sameCatalogRelease(row, { manifest, verification }))
          fail('CloudBase catalog row exists but does not match the signed release metadata.');
        return row;
      }
    } catch (error) {
      lastError = error;
    }
    if (attempt + 1 < attempts) await new Promise((resolve) => setTimeout(resolve, 250 * (2 ** attempt)));
  }
  if (lastError) fail('CloudBase catalog publication could not be read back; inspect the catalog before retrying.', lastError);
  fail('CloudBase catalog publication was not visible after the bounded readback retries.');
}

async function readCatalogState({ apiBase, apiKey, release, requestCloudBaseImpl }) {
  const { role, channel, version } = release.manifest;
  const existing = await readCatalogRow({ apiBase, apiKey, role, channel, version, requestCloudBaseImpl });
  if (existing) {
    if (existing.status !== 'published' || !sameCatalogRelease(existing, release))
      fail('This role/version already has a different or withdrawn immutable file update catalog record.');
    return { latest: await readCatalogRow({ apiBase, apiKey, role, channel, requestCloudBaseImpl }), existing };
  }
  const latest = await readCatalogRow({ apiBase, apiKey, role, channel, requestCloudBaseImpl });
  if (latest) {
    if (!Number.isSafeInteger(latest.release_sequence) || latest.release_sequence < 1 ||
        latest.release_sequence >= MAX_RELEASE_SEQUENCE ||
        typeof latest.version !== 'string' || compareSemver(version, latest.version) <= 0 ||
        release.manifest.releaseSequence <= latest.release_sequence)
      fail('Signed release version and sequence must both exceed the existing catalog high water mark.');
  }
  return { latest, existing: null };
}

function normalizeRpcReleaseId(value) {
  const first = Array.isArray(value) ? value[0] : value;
  const result = typeof first === 'string' ? first : first?.publish_file_update_release_v1;
  if (typeof result !== 'string' ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(result))
    fail('CloudBase publication RPC did not return a valid release ID.');
  return result;
}

function verifyProjectEnvironment(environment, projectConfig) {
  if (environment.CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW !== 'yes')
    fail('Set CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW=yes after confirming the service key rotation.');
  if ((environment.CloudBase__EnvId || '').trim() !== projectConfig.envId ||
      !(environment.CloudBase__ApiKey || '').trim())
    fail('CloudBase__EnvId must match cloudbaserc.json and CloudBase__ApiKey must contain the service-role key.');
  if ((environment.GITCODE_OWNER || GITCODE_OWNER) !== GITCODE_OWNER ||
      (environment.GITCODE_REPO || GITCODE_REPOSITORY) !== GITCODE_REPOSITORY)
    fail('File update assets may only be published to the fixed GitCode project.');
}

function readGitHubTagCommit(owner, repository, tag, exec = execFileSync) {
  const remote = `https://github.com/${owner}/${repository}.git`;
  let output;
  try {
    output = exec('git', ['ls-remote', remote, `refs/tags/${tag}`, `refs/tags/${tag}^{}`], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe']
    });
  } catch (error) {
    fail(`Could not verify GitHub source tag ${tag}.`, error);
  }
  const refs = new Map(output.split(/\r?\n/).filter(Boolean).map((line) => {
    const [sha, ref] = line.trim().split(/\s+/);
    return [ref, sha];
  }));
  return refs.get(`refs/tags/${tag}^{}`) || refs.get(`refs/tags/${tag}`) || null;
}

function compareAssetInventories(provider, published, expected) {
  assertPublisherAssets(provider, published, expected);
  const expectedByName = new Map(expected.map((asset) => [asset.name, asset]));
  for (const asset of published.assets) {
    const reference = expectedByName.get(asset.name);
    if (asset.size !== reference.size || asset.sha256 !== reference.sha256)
      fail(`${provider} Release mirror differs from the staged package at ${asset.name}.`);
  }
}

async function publishFileUpdateRelease(options = {}) {
  const parsed = options.args ? parseArguments(options.args) :
    options.values ? { command: 'publish', values: options.values } : parseArguments(process.argv.slice(2));
  if (parsed.command !== 'publish') return { help: true };
  const values = parsed.values;
  for (const key of ['release-directory', 'public-key', 'object-store', 'source-commit', 'release-notes',
    'source-plan-review', 'g0-acceptance-record']) {
    if (typeof values[key] !== 'string' || !values[key].trim()) fail(`Missing required '--${key}'.`);
  }
  if (!/^[0-9a-f]{40}$/i.test(values['source-commit'])) fail("'--source-commit' must be a full Git commit SHA.");
  const environment = options.environment || process.env;
  const repoRoot = path.resolve(options.repositoryRoot || path.resolve(__dirname, '..'));
  const projectConfig = options.projectConfig || JSON.parse(fs.readFileSync(path.join(repoRoot, 'cloudbaserc.json'), 'utf8'));
  verifyProjectEnvironment(environment, projectConfig);
  const apiKey = environment.CloudBase__ApiKey.trim();
  const apiBase = cloudBaseApiBase(projectConfig.envId);
  const projectFunction = projectConfig.functions?.find((fn) => fn.name === 'veyon-api');
  if (!projectFunction?.envVariables?.CloudBase__ApplicationReleasePublicBaseUrl)
    fail('cloudbaserc.json does not contain the configured public API base URL.');
  const publicApiBase = new URL(projectFunction.envVariables.CloudBase__ApplicationReleasePublicBaseUrl);
  if (publicApiBase.protocol !== 'https:' || publicApiBase.pathname !== '/' || publicApiBase.search || publicApiBase.hash)
    fail('The configured CloudBase public API base URL must be an HTTPS root URL.');

  const releaseDirectory = path.resolve(values['release-directory']);
  const publicKeyPath = path.resolve(values['public-key']);
  const objectStore = path.resolve(values['object-store']);
  const notesPath = path.resolve(values['release-notes']);
  const notesInfo = fs.lstatSync(notesPath);
  if (!notesInfo.isFile() || notesInfo.isSymbolicLink() || notesInfo.size < 1)
    fail('File update release notes must be a non-empty regular file.');
  const sourceCommit = values['source-commit'];
  const lookupTagCommit = options.lookupTagCommit || ((provider, actualTag) => provider === 'gitcode'
    ? readRemoteTagCommit(encodeURIComponent(GITCODE_OWNER), encodeURIComponent(GITCODE_REPOSITORY), actualTag)
    : readGitHubTagCommit(GITHUB_OWNER, GITHUB_REPOSITORY, actualTag));

  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'veyon-file-update-publish-'));
  try {
    const staged = await stageVerifiedAssets({
      releaseDirectory,
      objectStore,
      publicKey: publicKeyPath,
      temporaryRoot
    });
    const manifest = staged.manifest;
    const tag = staged.tag;
    if (manifest.releaseSequence < 1 || manifest.releaseSequence > MAX_RELEASE_SEQUENCE ||
        manifest.role !== (manifest.product === 'VeyonCampus.TeacherConsole' ? 'TeacherConsole' : 'StudentSetup'))
      fail('The signed role, product, or releaseSequence is invalid.');
    if (fileUpdateTag(manifest) !== tag) fail('The signed manifest does not match its role-scoped immutable Release tag.');
    validateSourcePlanReview({
      repositoryRoot: repoRoot,
      reviewPath: values['source-plan-review'],
      manifest,
      verification: staged.verification
    });
    const g0Acceptance = validateG0Acceptance({
      repositoryRoot: repoRoot,
      acceptancePath: values['g0-acceptance-record']
    });
    verifyG0CommitIsAncestor(g0Acceptance.atomicUpdateCommit, sourceCommit, repoRoot,
      options.verifyG0CommitAncestry || execFileSync);
    const gitcodeTagCommit = await lookupTagCommit('gitcode', tag);
    const githubTagCommit = await lookupTagCommit('github', tag);
    if (!gitcodeTagCommit || gitcodeTagCommit.toLowerCase() !== sourceCommit.toLowerCase() ||
        !githubTagCommit || githubTagCommit.toLowerCase() !== sourceCommit.toLowerCase())
      fail(`GitCode and GitHub tags '${tag}' must both exist at the requested source commit before publishing.`);

    const requestCloudBaseImpl = options.requestCloudBaseImpl || requestCloudBase;
    const catalogState = await readCatalogState({
      apiBase,
      apiKey,
      release: { manifest, verification: { ...staged.verification, manifestBytes: staged.manifestBytes } },
      requestCloudBaseImpl
    });

    const gitcodeEnvironment = {
      ...environment,
      GITCODE_OWNER,
      GITCODE_REPO: GITCODE_REPOSITORY
    };
    const gitcodePublish = options.publishGitCodeImpl || publishGitCodeRelease;
    const gitcodeResult = await gitcodePublish({
      args: [tag, manifest.version, sourceCommit, notesPath, ...staged.assetPaths],
      environment: gitcodeEnvironment,
      lookupTagCommit: async () => gitcodeTagCommit,
      fetchImpl: options.gitcodeFetchImpl,
      uploadWithHttpsImpl: options.gitcodeUploadImpl,
      writeOutput: options.writeOutput || (() => {})
    });
    compareAssetInventories('GitCode', gitcodeResult, staged.assets);

    const githubPublish = options.publishGitHubImpl || publishGitHubFileUpdateRelease;
    const githubResult = await githubPublish({
      tag,
      version: manifest.version,
      assetPaths: staged.assetPaths,
      notesPath,
      githubCli: options.githubCli
    });
    compareAssetInventories('GitHub', githubResult, staged.assets);

    const finalGitCodeTagCommit = await lookupTagCommit('gitcode', tag);
    const finalGitHubTagCommit = await lookupTagCommit('github', tag);
    if (!finalGitCodeTagCommit || finalGitCodeTagCommit.toLowerCase() !== sourceCommit.toLowerCase() ||
        !finalGitHubTagCommit || finalGitHubTagCommit.toLowerCase() !== sourceCommit.toLowerCase())
      fail(`GitCode or GitHub tag '${tag}' moved during attachment publication; CloudBase index was not written.`);

    if (catalogState.existing) {
      return {
        role: manifest.role,
        version: manifest.version,
        releaseSequence: manifest.releaseSequence,
        tag,
        assetCount: staged.assets.length,
        assetsVerifiedOn: ['gitcode', 'github'],
        catalogReleaseId: catalogState.existing.release_id,
        catalogAlreadyPublished: true,
        installedMachineModified: false
      };
    }

    const releaseId = crypto.randomUUID();
    const rpcPath = 'v1/rdb/rest/rpc/publish_file_update_release_v1';
    const body = {
      p_release_id: releaseId,
      p_role: manifest.role,
      p_version: manifest.version,
      p_channel: manifest.channel,
      p_release_sequence: manifest.releaseSequence,
      p_manifest_size_bytes: staged.verification.manifestBytes,
      p_manifest_sha256: staged.verification.manifestSha256,
      p_manifest_key_id: staged.verification.keyId
    };
    try {
      const rpcResult = await requestCloudBaseImpl(apiBase, apiKey, rpcPath, { method: 'POST', body, timeoutMs: 30000 });
      normalizeRpcReleaseId(rpcResult);
    } catch (error) {
      try {
        await readPublishedCatalogRowAfterWrite({
          apiBase,
          apiKey,
          manifest,
          verification: { ...staged.verification, manifestBytes: body.p_manifest_size_bytes },
          requestCloudBaseImpl
        });
      } catch (readError) {
        fail(`CloudBase RPC failed (${error.message}) and catalog readback did not prove success.`, readError);
      }
    }
    const publishedRow = await readPublishedCatalogRowAfterWrite({
      apiBase,
      apiKey,
      manifest,
      verification: { ...staged.verification, manifestBytes: body.p_manifest_size_bytes },
      requestCloudBaseImpl
    });

    return {
      role: manifest.role,
      version: manifest.version,
      releaseSequence: manifest.releaseSequence,
      tag,
      assetCount: staged.assets.length,
      assetsVerifiedOn: ['gitcode', 'github'],
      catalogReleaseId: publishedRow.release_id,
      catalogAlreadyPublished: false,
      installedMachineModified: false
    };
  } finally {
    fs.rmSync(temporaryRoot, { recursive: true, force: true });
  }
}

function formatHelp() {
  return [
    'Publish one signed file update role release (GitCode, GitHub, then CloudBase index):',
    '  node scripts/publish-file-update-release.cjs publish --release-directory DIR --public-key PEM',
    '    --object-store DIR --source-commit GIT_SHA --release-notes FILE',
    '    --source-plan-review FILE --g0-acceptance-record FILE',
    '',
    'Requires pre-existing role-scoped Git tags on both providers, GitCode credentials, gh auth,',
    'CloudBase__EnvId / CloudBase__ApiKey, the service-key rotation acknowledgement, three',
    'verified source-to-target plan reports for this exact target, and a passed Windows 10/11 G0 record.',
    'The CloudBase catalog row is written only after both attachment mirrors pass readback checks.'
  ].join('\n');
}

async function main() {
  const parsed = parseArguments(process.argv.slice(2));
  if (parsed.command === 'help') {
    process.stdout.write(formatHelp() + '\n');
    return;
  }
  const result = await publishFileUpdateRelease({ values: parsed.values });
  process.stdout.write(JSON.stringify(result, null, 2) + '\n');
}

if (require.main === module) {
  main().catch((error) => {
    process.stderr.write(`File update release publication failed: ${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = {
  catalogQuery,
  fileUpdateTag,
  parseArguments,
  publishFileUpdateRelease,
  readCatalogRow,
  readGitHubTagCommit,
  requestCloudBase,
  stageVerifiedAssets
};

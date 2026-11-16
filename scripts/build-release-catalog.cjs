'use strict';

// Builds the release-catalogue asset that clients read to discover the newest version.
//
// The previous discovery path was a CloudBase function. It is gone, so the catalogue now travels as a
// release asset on GitCode (primary, reachable from mainland China) and GitHub (mirror). Each entry
// carries the same signed manifest the installer is verified against, so a client that trusts the
// pinned Developer Release public key needs no other trust anchor.
//
//   node scripts/build-release-catalog.cjs --role TeacherConsole --version 1.1.81 \
//        --installer artifacts/VeyonCampus-Teacher-Setup-1.1.81-win-x64.exe \
//        --tag v1.1.81 --output artifacts/release-catalog.json
//
// Re-running the script for another role merges into the same file.

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { createReadStream } = require('node:fs');

const { canonicalPayload, canonicalStudentAgentTargetPayload, createReleaseSigningKey } =
  require('./publish-application-release.cjs');

const SIGNATURE_ALGORITHM = 'RSA-PSS-SHA256';
const MAX_ARTIFACT_BYTES = 512 * 1024 * 1024;
const VERSION_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/;
const TAG_PATTERN = /^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/;
const ROLES = {
  TeacherConsole: { product: 'VeyonCampus.TeacherConsole', fileName: 'Teacher' },
  StudentSetup: { product: 'VeyonCampus.StudentSetup', fileName: 'Student' },
  MaintenanceService: { product: 'VeyonCampus.MaintenanceService', fileName: 'Maintenance' }
};

function parseArguments(args) {
  const options = {};
  const known = new Set(['--role', '--version', '--installer', '--tag', '--output', '--agent-target',
    '--maintenance-protocol-version']);
  for (let index = 0; index < args.length; index += 1) {
    const name = args[index];
    if (!known.has(name)) throw new Error(`Unexpected argument: ${name}`);
    if (options[name] !== undefined) throw new Error(`Duplicate argument: ${name}`);
    const value = args[index + 1];
    if (!value || value.startsWith('--')) throw new Error(`Missing value for ${name}`);
    options[name] = value;
    index += 1;
  }
  for (const required of ['--role', '--version', '--installer', '--tag', '--output']) {
    if (!options[required]) throw new Error(`${required} is required.`);
  }
  if (!ROLES[options['--role']]) throw new Error('--role must be TeacherConsole, StudentSetup, or MaintenanceService.');
  if (!VERSION_PATTERN.test(options['--version'])) throw new Error('--version must be X.Y.Z.');
  if (!TAG_PATTERN.test(options['--tag'])) throw new Error('--tag must be vX.Y.Z.');
  if (options['--tag'].slice(1) !== options['--version'])
    throw new Error('--tag and --version must describe the same version.');
  const installerPath = path.resolve(options['--installer']);
  return {
    role: options['--role'],
    version: options['--version'],
    tag: options['--tag'],
    installerPath,
    outputPath: path.resolve(options['--output']),
    agentTargetPath: options['--agent-target']
      ? path.resolve(options['--agent-target'])
      : `${installerPath}.agent-target.json`,
    maintenanceProtocolVersion: options['--maintenance-protocol-version'] === undefined
      ? null
      : Number(options['--maintenance-protocol-version'])
  };
}

function releaseAssetUrl(host, owner, repository, tag, fileName) {
  return `https://${host}/${owner}/${repository}/releases/download/${tag}/${fileName}`;
}

function sources(environment) {
  const primary = {
    host: 'gitcode.com',
    owner: (environment.GITCODE_OWNER || 'ljc_1126').trim(),
    repository: (environment.GITCODE_REPO || 'veyon-campus-deployment').trim()
  };
  const mirror = {
    host: 'github.com',
    owner: (environment.GITHUB_OWNER || 'Ljc798').trim(),
    repository: (environment.GITHUB_REPO || 'veyon-campus-deployment').trim()
  };
  for (const source of [primary, mirror]) {
    if (!/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(source.owner) ||
        !/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(source.repository))
      throw new Error('Release owner and repository must be plain slugs.');
  }
  return { primary, mirror };
}

async function computeSha256(filePath) {
  const hash = crypto.createHash('sha256');
  await new Promise((resolve, reject) => {
    const stream = createReadStream(filePath);
    stream.on('data', chunk => hash.update(chunk));
    stream.on('error', reject);
    stream.on('end', resolve);
  });
  return hash.digest('hex').toUpperCase();
}

function readExistingCatalog(outputPath, tag) {
  if (!fs.existsSync(outputPath)) return { schemaVersion: 1, tag, roles: {} };
  const existing = JSON.parse(fs.readFileSync(outputPath, 'utf8'));
  if (existing.schemaVersion !== 1 || typeof existing.roles !== 'object' || existing.roles === null)
    throw new Error('Existing catalogue is not a schemaVersion 1 document.');
  if (existing.tag !== tag) return { schemaVersion: 1, tag, roles: {} };
  return existing;
}

async function build({ environment = process.env, args = process.argv.slice(2) } = {}) {
  const options = parseArguments(args);
  const { primary, mirror } = sources(environment);
  const privateKeyPath = environment.VEYONCAMPUS_RELEASE_PRIVATE_KEY_PATH;
  const publicKeyPath = environment.VEYONCAMPUS_RELEASE_PUBLIC_KEY_PATH;
  if (!privateKeyPath || !publicKeyPath)
    throw new Error('Set VEYONCAMPUS_RELEASE_PRIVATE_KEY_PATH and VEYONCAMPUS_RELEASE_PUBLIC_KEY_PATH.');

  const role = ROLES[options.role];
  const fileName = `VeyonCampus-${role.fileName}-Setup-${options.version}-win-x64.exe`;
  if (path.basename(options.installerPath) !== fileName)
    throw new Error(`Installer filename must be exactly ${fileName}.`);
  const info = await fs.promises.lstat(options.installerPath);
  if (!info.isFile() || info.isSymbolicLink() || info.size < 1 || info.size > MAX_ARTIFACT_BYTES)
    throw new Error('Installer must be a regular file between 1 byte and 512 MiB.');

  const privateKey = createReleaseSigningKey(await fs.promises.readFile(privateKeyPath, 'utf8'),
    environment.VEYONCAMPUS_RELEASE_PRIVATE_KEY_PASSPHRASE || '');
  const pinnedPublicKey = crypto.createPublicKey(await fs.promises.readFile(publicKeyPath, 'utf8'));
  if (!pinnedPublicKey.export({ type: 'spki', format: 'der' })
    .equals(crypto.createPublicKey(privateKey).export({ type: 'spki', format: 'der' })))
    throw new Error('The pinned public key does not match the release signing private key.');

  const sha256 = await computeSha256(options.installerPath);
  const manifest = {
    schemaVersion: options.role === 'MaintenanceService' ? 5 : 3,
    product: role.product,
    role: options.role,
    version: options.version,
    architecture: 'win-x64',
    fileName,
    sizeBytes: info.size,
    sha256,
    downloadUrl: releaseAssetUrl(primary.host, primary.owner, primary.repository, options.tag, fileName),
    ...(options.role === 'MaintenanceService'
      ? { releaseChannel: 'stable', maintenanceProtocolVersion: options.maintenanceProtocolVersion }
      : { policyCapabilities: { applicationPolicy: 1, studentSystemPolicy: 1 } })
  };
  if (options.role === 'MaintenanceService' &&
      (!Number.isInteger(manifest.maintenanceProtocolVersion) ||
       manifest.maintenanceProtocolVersion < 1 || manifest.maintenanceProtocolVersion > 32767))
    throw new Error('--maintenance-protocol-version is required for MaintenanceService (1–32767).');

  const signature = crypto.sign('sha256', canonicalPayload(manifest), {
    key: privateKey,
    padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
    saltLength: 32
  }).toString('base64');
  const release = {
    manifest,
    signatureAlgorithm: SIGNATURE_ALGORITHM,
    signature,
    publishedAt: new Date().toISOString()
  };

  const catalog = readExistingCatalog(options.outputPath, options.tag);
  let studentAgentTarget;
  let evidence;
  if (options.role === 'StudentSetup') {
    try {
      evidence = JSON.parse(await fs.promises.readFile(options.agentTargetPath, 'utf8'));
    } catch (error) {
      // Republishing an existing version downloads the previous catalogue instead of the build
      // evidence, so the sidecar is carried over - but only when it still describes these exact bytes.
      const previous = catalog.roles.StudentSetup?.studentAgentTarget;
      if (previous?.manifest?.installerSha256?.toUpperCase() === sha256 &&
          previous.manifest.setupVersion === options.version) {
        studentAgentTarget = previous;
      } else {
        throw new Error(`StudentSetup requires build-generated Agent target evidence: ${error.message}`);
      }
    }
  }
  if (options.role === 'StudentSetup' && studentAgentTarget === undefined) {
    const targetManifest = {
      schemaVersion: 1,
      purpose: 'VeyonCampus.StudentSetup.AgentTarget.v1',
      product: 'VeyonCampus.StudentSetup',
      setupVersion: options.version,
      installerSha256: sha256,
      studentAgentVersion: evidence.studentAgentVersion
    };
    if (evidence.setupVersion !== options.version ||
        String(evidence.installerSha256).toUpperCase() !== sha256 ||
        typeof targetManifest.studentAgentVersion !== 'string' ||
        !/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(targetManifest.studentAgentVersion))
      throw new Error('Agent target evidence does not match the installer digest and version.');
    studentAgentTarget = {
      manifest: targetManifest,
      signatureAlgorithm: SIGNATURE_ALGORITHM,
      signature: crypto.sign('sha256', canonicalStudentAgentTargetPayload(targetManifest), {
        key: privateKey,
        padding: crypto.constants.RSA_PKCS1_PSS_PADDING,
        saltLength: 32
      }).toString('base64')
    };
  }

  catalog.roles[options.role] = {
    release,
    ...(studentAgentTarget ? { studentAgentTarget } : {}),
    mirrors: [releaseAssetUrl(mirror.host, mirror.owner, mirror.repository, options.tag, fileName)]
  };
  await fs.promises.mkdir(path.dirname(options.outputPath), { recursive: true });
  await fs.promises.writeFile(options.outputPath, `${JSON.stringify(catalog, null, 2)}\n`, 'utf8');
  console.log(JSON.stringify({
    output: options.outputPath,
    tag: options.tag,
    role: options.role,
    version: options.version,
    sizeBytes: info.size,
    sha256,
    downloadUrl: manifest.downloadUrl,
    roles: Object.keys(catalog.roles)
  }, null, 2));
  return catalog;
}

if (require.main === module) {
  build().catch(error => {
    process.stderr.write(`Release catalogue build failed: ${error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = { build, parseArguments, releaseAssetUrl };

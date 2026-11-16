'use strict';

// Publishes one campus package into the static catalogue, replacing the retired CloudBase upload.
//
//   node scripts/publish-deployment-package.cjs \
//        --package-dir "D:\\配置\\春晓实验学校" \
//        --output-dir packages \
//        --display-name "春晓实验学校 · 教师机"
//
//   node scripts/publish-deployment-package.cjs --output-dir packages --withdraw <packageId>
//
// The command only writes local files. Publishing means committing `packages/` and pushing it to
// GitCode; that keeps the signing/publishing decision in a human's hands and out of a browser tab.
//
// Validation is not reimplemented here: `canonicalizeFolderFiles` is the same function the CloudBase
// function ran, and `makeZip` is the same serializer, so the archive bytes are identical to what the
// old upload produced for the same folder.

const fs = require('node:fs');
const path = require('node:path');

const {
  InvalidPackageError,
  canonicalizeFolderFiles
} = require('../cloudfunctions/veyon-api/package-validator');
const {
  CATALOG_FILE_NAME,
  PACKAGE_DIRECTORY_NAME,
  catalogUrl,
  createEmptyCatalog,
  createSource,
  packageUrl,
  parseCatalog,
  serializeCatalog,
  sha256Hex,
  upsertPackage,
  withdrawPackage,
  normalizePackageId
} = require('./deployment-package-catalog.cjs');

const ARGUMENTS = new Set([
  '--package-dir', '--output-dir', '--display-name', '--campus-id', '--withdraw',
  '--owner', '--repo', '--ref', '--mirror-owner', '--mirror-repo',
  '--published-at', '--dry-run'
]);

function parseArguments(args) {
  const options = {};
  for (let index = 0; index < args.length; index += 1) {
    const name = args[index];
    if (!ARGUMENTS.has(name)) throw new Error(`Unknown argument: ${name}`);
    if (name === '--dry-run') {
      options.dryRun = true;
      continue;
    }
    const value = args[index + 1];
    if (value === undefined || value.startsWith('--')) throw new Error(`Missing value for ${name}`);
    if (options[name] !== undefined) throw new Error(`Duplicate argument: ${name}`);
    options[name] = value;
    index += 1;
  }
  if (!options['--package-dir'] && !options['--withdraw'])
    throw new Error('Either --package-dir or --withdraw is required.');
  if (options['--package-dir'] && options['--withdraw'])
    throw new Error('--package-dir and --withdraw cannot be combined.');
  return options;
}

function createSources(options) {
  const primary = createSource({
    kind: 'gitcode',
    owner: options['--owner'] ?? process.env.GITCODE_OWNER ?? 'ljc_1126',
    repository: options['--repo'] ?? process.env.GITCODE_REPO ?? 'veyon-campus-deployment',
    ref: options['--ref'] ?? 'develop'
  });
  const mirrorOwner = options['--mirror-owner'] ?? process.env.GITHUB_OWNER;
  const mirrorRepository = options['--mirror-repo'] ?? process.env.GITHUB_REPO;
  const mirror = mirrorOwner && mirrorRepository
    ? createSource({
        kind: 'github',
        owner: mirrorOwner,
        repository: mirrorRepository,
        ref: options['--ref'] ?? 'develop'
      })
    : null;
  return { primary, mirror };
}

/// Reads the flat package folder a teacher generated. Nested one-level folders are accepted because
/// the desktop app writes `文件名/manifest.json` when it zips a folder the user selected.
function readPackageFiles(directory) {
  const resolved = path.resolve(directory);
  const info = fs.statSync(resolved);
  if (!info.isDirectory()) throw new Error(`--package-dir is not a directory: ${resolved}`);
  const files = [];
  for (const name of fs.readdirSync(resolved).sort()) {
    const fullPath = path.join(resolved, name);
    const entry = fs.lstatSync(fullPath);
    if (entry.isSymbolicLink()) throw new Error(`配置包文件夹不能包含链接：${name}`);
    if (entry.isDirectory()) continue;
    if (!entry.isFile()) throw new Error(`配置包文件夹包含非普通文件：${name}`);
    files.push({ fileName: name, bytes: fs.readFileSync(fullPath) });
  }
  if (files.length === 0) throw new Error(`配置包文件夹为空：${resolved}`);
  return files;
}

function readExistingCatalog(outputDirectory) {
  const catalogPath = path.join(outputDirectory, CATALOG_FILE_NAME);
  if (!fs.existsSync(catalogPath)) return { catalog: createEmptyCatalog(), catalogPath, existed: false };
  const catalog = parseCatalog(fs.readFileSync(catalogPath, 'utf8'));
  return { catalog, catalogPath, existed: true };
}

function publish(options) {
  const outputDirectory = path.resolve(options['--output-dir'] ?? PACKAGE_DIRECTORY_NAME);
  const { primary, mirror } = createSources(options);
  const { catalog, catalogPath, existed } = readExistingCatalog(outputDirectory);

  if (options['--withdraw']) {
    const withdrawn = withdrawPackage(catalog, options['--withdraw']);
    if (!options.dryRun) {
      fs.mkdirSync(outputDirectory, { recursive: true });
      fs.writeFileSync(catalogPath, serializeCatalog(withdrawn), 'utf8');
    }
    return {
      action: 'withdraw',
      packageId: options['--withdraw'].toLowerCase(),
      catalogPath,
      catalogExisted: existed,
      publishedPackages: withdrawn.packages.filter((entry) => entry.status === 'published').length
    };
  }

  const files = readPackageFiles(options['--package-dir']);
  let canonical;
  try {
    canonical = canonicalizeFolderFiles(files);
  } catch (error) {
    if (error instanceof InvalidPackageError)
      throw new Error(`配置包校验失败：${error.message}`);
    throw error;
  }

  const archiveBytes = canonical.archiveBytes;
  if (!Buffer.isBuffer(archiveBytes) || archiveBytes.length < 1)
    throw new Error('配置包归档生成失败：校验器没有返回 ZIP 字节。');
  const digest = sha256Hex(archiveBytes);
  const packageId = normalizePackageId(canonical.packageId);
  const archivedAt = options['--published-at'] ?? new Date().toISOString();
  if (Number.isNaN(Date.parse(archivedAt)))
    throw new Error('--published-at must be an ISO timestamp.');

  const campusId = options['--campus-id'] === undefined ? null : Number(options['--campus-id']);
  if (campusId !== null && (!Number.isInteger(campusId) || campusId <= 0))
    throw new Error('--campus-id must be a positive integer.');

  const entry = {
    packageId,
    campusId,
    campusName: canonical.campus,
    displayName: options['--display-name'] ?? canonical.campus,
    computerPrefix: canonical.computerPrefix,
    schemaVersion: canonical.schemaVersion,
    targetOs: 'windows',
    architecture: 'x64',
    fileName: 'campus-package.zip',
    sizeBytes: archiveBytes.length,
    sha256: digest,
    downloadUrl: packageUrl(primary, packageId),
    mirrors: mirror ? [packageUrl(mirror, packageId)] : [],
    publishedAt: archivedAt,
    requiresPhoneLast4: false
  };

  const updated = upsertPackage(catalog, entry);
  if (!options.dryRun) {
    fs.mkdirSync(outputDirectory, { recursive: true });
    fs.writeFileSync(path.join(outputDirectory, `${packageId}.zip`), archiveBytes);
    fs.writeFileSync(catalogPath, serializeCatalog(updated), 'utf8');
  }

  return {
    action: existed ? 'upsert' : 'create',
    packageId,
    campusName: entry.campusName,
    displayName: entry.displayName,
    computerPrefix: entry.computerPrefix,
    schemaVersion: entry.schemaVersion,
    sizeBytes: entry.sizeBytes,
    sha256: digest,
    archivePath: path.join(outputDirectory, `${packageId}.zip`),
    catalogPath,
    catalogUrl: catalogUrl(primary),
    downloadUrl: entry.downloadUrl,
    mirrors: entry.mirrors,
    publishedAt: entry.publishedAt,
    publishedPackages: updated.packages.filter((row) => row.status === 'published').length,
    dryRun: options.dryRun === true
  };
}

function main() {
  const options = parseArguments(process.argv.slice(2));
  const result = publish(options);
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
  if (!result.dryRun) {
    process.stdout.write(
      '\n下一步：提交 packages/ 并推送到 GitCode，客户端即可在约 1 分钟内搜索到这个配置包。\n');
  }
}

if (require.main === module) {
  try {
    main();
  } catch (error) {
    process.stderr.write(`发布配置包失败：${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = { parseArguments, publish, readPackageFiles };

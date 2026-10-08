const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');

const repositoryRoot = path.resolve(__dirname, '..');
const websiteRoot = path.join(repositoryRoot, 'website');

function packageForModuleId(moduleId, root = websiteRoot) {
  if (typeof moduleId !== 'string' || moduleId.startsWith('\0') || !path.isAbsolute(moduleId)) return null;

  const absoluteId = path.resolve(moduleId.split('?')[0]);
  const absoluteRoot = path.resolve(root);
  const relativeId = path.relative(absoluteRoot, absoluteId);
  if (!relativeId || relativeId.startsWith(`..${path.sep}`) || relativeId === '..' || path.isAbsolute(relativeId)) return null;

  const parts = relativeId.split(path.sep);
  const nodeModulesIndex = parts.lastIndexOf('node_modules');
  if (nodeModulesIndex < 0 || nodeModulesIndex + 1 >= parts.length) return null;

  const packageParts = [parts[nodeModulesIndex + 1]];
  if (packageParts[0].startsWith('@')) {
    if (nodeModulesIndex + 2 >= parts.length) return null;
    packageParts.push(parts[nodeModulesIndex + 2]);
  }

  const packageRoot = path.join(absoluteRoot, ...parts.slice(0, nodeModulesIndex + 1), ...packageParts);
  const packageJsonPath = path.join(packageRoot, 'package.json');
  if (!fs.existsSync(packageJsonPath)) return null;

  try {
    const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));
    if (typeof packageJson.name !== 'string' || typeof packageJson.version !== 'string') return null;
    return {
      name: packageJson.name,
      version: packageJson.version,
      license: packageJson.license ?? null,
      packageRoot,
    };
  } catch {
    return null;
  }
}

function licenseMetadataBlockers(packages) {
  const noRedistribution = new Set(['NONE', 'NOASSERTION', 'UNLICENSED']);
  return packages.filter((packageInfo) =>
    typeof packageInfo.license !== 'string' ||
    packageInfo.license.trim().length === 0 ||
    noRedistribution.has(packageInfo.license.trim().toUpperCase()));
}

async function collectWebsiteBundlePackages() {
  const requireFromWebsite = createRequire(path.join(websiteRoot, 'package.json'));
  const { build } = requireFromWebsite('vite');
  const includedPackages = new Map();
  const plugin = {
    name: 'veyon-campus-bundle-license-audit',
    generateBundle() {
      for (const moduleId of this.getModuleIds()) {
        if (!this.getModuleInfo(moduleId)?.isIncluded) continue;
        const packageInfo = packageForModuleId(moduleId);
        if (packageInfo) includedPackages.set(`${packageInfo.name}@${packageInfo.version}`, packageInfo);
      }
    },
  };

  await build({
    configFile: path.join(websiteRoot, 'vite.config.js'),
    root: websiteRoot,
    logLevel: 'silent',
    plugins: [plugin],
    build: { write: false, emptyOutDir: false },
  });

  return [...includedPackages.values()].sort((left, right) => {
    const leftKey = `${left.name}@${left.version}`;
    const rightKey = `${right.name}@${right.version}`;
    return leftKey.localeCompare(rightKey);
  });
}

async function main(argv = process.argv.slice(2)) {
  if (argv.some((argument) => argument === '--help' || argument === '-h')) {
    console.log('Usage: node scripts/check-website-bundle-licenses.cjs [--strict]');
    console.log('Analyzes the Vite browser bundle module graph; --strict exits 2 when an included npm package has no license metadata.');
    return 0;
  }

  const strict = argv.includes('--strict');
  const unknown = argv.filter((argument) => argument !== '--strict');
  if (unknown.length) {
    console.error(`Unsupported option: ${unknown.join(' ')}`);
    return 2;
  }

  const packages = await collectWebsiteBundlePackages();
  const missing = licenseMetadataBlockers(packages);
  console.log(`Vite browser bundle includes ${packages.length} npm packages:`);
  for (const packageInfo of packages) {
    const license = typeof packageInfo.license === 'string' && packageInfo.license.trim()
      ? packageInfo.license.trim()
      : '未声明';
    console.log(`- ${packageInfo.name}@${packageInfo.version}: ${license}`);
  }

  if (missing.length) {
    console.log(`Bundle license check found ${missing.length} included package(s) without usable license metadata: ${missing.map((item) => `${item.name}@${item.version}`).join(', ')}.`);
    if (strict) return 2;
  }
  return 0;
}

if (require.main === module) {
  main().then((code) => { process.exitCode = code; }).catch((error) => {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  });
}

module.exports = { packageForModuleId, licenseMetadataBlockers, collectWebsiteBundlePackages, main };

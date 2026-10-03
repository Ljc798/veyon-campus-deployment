const { mkdirSync, readFileSync, writeFileSync } = require('node:fs');
const { dirname, join, resolve } = require('node:path');

const repositoryRoot = resolve(__dirname, '..');
const reportPath = join(repositoryRoot, 'artifacts', 'dependency-audit', 'npm-vulnerability-report.json');
const registryUrl = 'https://registry.npmjs.org/-/npm/v1/security/advisories/bulk';
const projectDirectories = ['website', 'cloudfunctions/veyon-api'];
const packageVersions = new Map();
const projectReports = [];

function addVersion(name, version, scope) {
  let packageInfo = packageVersions.get(name);
  if (!packageInfo) {
    packageInfo = { versions: new Set(), scopes: new Set() };
    packageVersions.set(name, packageInfo);
  }
  packageInfo.versions.add(version);
  packageInfo.scopes.add(scope);
}

for (const relativeDirectory of projectDirectories) {
  const projectDirectory = join(repositoryRoot, relativeDirectory);
  const manifestPath = join(projectDirectory, 'package.json');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  const declaredDependencies = {
    ...manifest.dependencies,
    ...manifest.devDependencies,
    ...manifest.optionalDependencies,
  };
  const lockPath = join(projectDirectory, 'package-lock.json');

  let packageLock;
  try {
    packageLock = JSON.parse(readFileSync(lockPath, 'utf8'));
  } catch (error) {
    if (Object.keys(declaredDependencies).length > 0) {
      throw new Error(`${relativeDirectory} declares npm dependencies but has no readable package-lock.json: ${error.message}`);
    }
    projectReports.push({ directory: relativeDirectory, packageCount: 0, lockfile: null });
    continue;
  }

  if (!packageLock.packages || typeof packageLock.packages !== 'object') {
    throw new Error(`${relativeDirectory}/package-lock.json has no packages object.`);
  }

  let packageCount = 0;
  for (const [packagePath, packageInfo] of Object.entries(packageLock.packages)) {
    if (!packagePath.startsWith('node_modules/') || typeof packageInfo.version !== 'string') continue;

    const name = packagePath.split('node_modules/').at(-1);
    const scope = packageInfo.dev === true ? 'development' : 'production';
    addVersion(name, packageInfo.version, scope);
    packageCount++;
  }

  projectReports.push({
    directory: relativeDirectory,
    packageCount,
    lockfileVersion: packageLock.lockfileVersion,
  });
}

const requestPackages = Object.fromEntries(
  [...packageVersions.entries()]
    .sort(([left], [right]) => (left < right ? -1 : left > right ? 1 : 0))
    .map(([name, packageInfo]) => [name, [...packageInfo.versions].sort()]),
);

async function runAudit() {
const auditReport = {
  generatedAt: new Date().toISOString(),
  source: registryUrl,
  scope: 'All locked production and development npm package versions in website and cloudfunctions/veyon-api.',
  projects: projectReports,
  packageCount: packageVersions.size,
  packageVersions: requestPackages,
  advisories: [],
};

try {
  const response = await fetch(registryUrl, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(requestPackages),
    signal: AbortSignal.timeout(60_000),
  });
  const responseText = await response.text();
  let advisoryData;
  try {
    advisoryData = responseText ? JSON.parse(responseText) : {};
  } catch {
    throw new Error(`npm registry returned non-JSON data (HTTP ${response.status}).`);
  }
  if (!response.ok) {
    throw new Error(`npm registry returned HTTP ${response.status}: ${JSON.stringify(advisoryData)}`);
  }

  for (const [name, entries] of Object.entries(advisoryData)) {
    const packageInfo = packageVersions.get(name);
    for (const advisory of Array.isArray(entries) ? entries : []) {
      auditReport.advisories.push({
        package: name,
        lockedVersions: packageInfo ? [...packageInfo.versions].sort() : [],
        scopes: packageInfo ? [...packageInfo.scopes].sort() : [],
        id: advisory.id,
        title: advisory.title,
        severity: advisory.severity,
        vulnerableVersions: advisory.vulnerable_versions,
        url: advisory.url,
      });
    }
  }
} catch (error) {
  auditReport.error = error.message;
  mkdirSync(dirname(reportPath), { recursive: true });
  writeFileSync(reportPath, `${JSON.stringify(auditReport, null, 2)}\n`, 'utf8');
  console.error(`npm vulnerability audit failed: ${error.message}`);
  process.exit(1);
}

mkdirSync(dirname(reportPath), { recursive: true });
writeFileSync(reportPath, `${JSON.stringify(auditReport, null, 2)}\n`, 'utf8');

if (auditReport.advisories.length > 0) {
  console.error(`Found ${auditReport.advisories.length} npm vulnerability advisory entries across ${packageVersions.size} locked packages:`);
  for (const advisory of auditReport.advisories) {
    console.error(`- ${advisory.package}@${advisory.lockedVersions.join(',')} ${advisory.severity}: ${advisory.title} (${advisory.url})`);
  }
  process.exit(1);
}

console.log(`Audited ${packageVersions.size} locked npm packages; no matching security advisories were returned.`);
}

runAudit().catch((error) => {
  console.error(`npm vulnerability audit failed: ${error.message}`);
  process.exitCode = 1;
});

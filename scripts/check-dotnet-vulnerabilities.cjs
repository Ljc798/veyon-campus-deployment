const { mkdirSync, writeFileSync } = require('node:fs');
const { spawnSync } = require('node:child_process');
const { dirname, resolve } = require('node:path');

const reportPath = resolve('artifacts/dependency-audit/dotnet-vulnerability-report.json');
const command = 'dotnet';
const args = [
  'package',
  'list',
  '--project', 'VeyonCampus.slnx',
  '--no-restore',
  '--include-transitive',
  '--vulnerable',
  '--format', 'json',
  '--output-version', '1',
];

const result = spawnSync(command, args, {
  cwd: process.cwd(),
  encoding: 'utf8',
  maxBuffer: 16 * 1024 * 1024,
});

if (result.error) {
  throw new Error(`Could not start dotnet package audit: ${result.error.message}`);
}
if (result.status !== 0) {
  process.stderr.write(result.stderr || 'dotnet package audit failed.\n');
  process.exit(result.status ?? 1);
}

let packageReport;
try {
  packageReport = JSON.parse(result.stdout);
} catch (error) {
  throw new Error(`dotnet package audit did not return valid JSON: ${error.message}`);
}

const findings = [];
function collectVulnerabilityEntries(value, path = '$') {
  if (Array.isArray(value)) {
    value.forEach((item, index) => collectVulnerabilityEntries(item, `${path}[${index}]`));
    return;
  }
  if (!value || typeof value !== 'object') return;

  for (const [key, child] of Object.entries(value)) {
    if (/^(vulnerablePackages|vulnerabilities)$/i.test(key)) {
      const entries = Array.isArray(child)
        ? child
        : child && typeof child === 'object'
          ? Object.values(child)
          : [];
      entries.forEach((entry, index) => {
        findings.push({ reportPath: `${path}.${key}[${index}]`, finding: entry });
      });
    } else {
      collectVulnerabilityEntries(child, `${path}.${key}`);
    }
  }
}

collectVulnerabilityEntries(packageReport);

const report = {
  generatedAt: new Date().toISOString(),
  solution: 'VeyonCampus.slnx',
  scope: 'direct and transitive NuGet packages',
  findings,
  packageReport,
};

mkdirSync(dirname(reportPath), { recursive: true });
writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`, 'utf8');

if (findings.length > 0) {
  console.error(`Found ${findings.length} vulnerable NuGet package/advisory entries:`);
  for (const { reportPath: findingPath, finding } of findings) {
    const packageName = finding?.name ?? finding?.id ?? 'unknown package';
    const version = finding?.resolvedVersion ?? finding?.version ?? 'unknown version';
    const severity = finding?.severity ?? 'unknown severity';
    const advisory = finding?.url ?? finding?.advisoryUrl ?? '';
    console.error(`- ${packageName} ${version} (${severity}) ${advisory} [${findingPath}]`);
  }
  process.exitCode = 1;
} else {
  console.log(`No vulnerable NuGet packages found. Full report: ${reportPath}`);
}

const { readFileSync, writeFileSync } = require('node:fs');
const { dirname, join } = require('node:path');

const scriptDirectory = dirname(__filename);
const repositoryRoot = join(scriptDirectory, '..');
const lockPath = join(repositoryRoot, 'website', 'package-lock.json');
const outputPath = join(repositoryRoot, 'docs', 'website-npm-license-inventory.md');
const lock = JSON.parse(readFileSync(lockPath, 'utf8'));

function getLicense(packageInfo) {
  if (!packageInfo.license) return '未声明';
  if (typeof packageInfo.license === 'string') return packageInfo.license.replaceAll('|', '\\|');
  if (typeof packageInfo.license === 'object') {
    return JSON.stringify(packageInfo.license).replaceAll('|', '\\|');
  }
  return String(packageInfo.license).replaceAll('|', '\\|');
}

const packages = Object.entries(lock.packages)
  .filter(([packagePath, packageInfo]) => packagePath.startsWith('node_modules/') && packageInfo.version)
  .map(([packagePath, packageInfo]) => ({
    name: packagePath.split('node_modules/').at(-1),
    version: packageInfo.version,
    scope: packageInfo.dev === true ? '开发依赖' : '生产依赖',
    optional: packageInfo.optional === true ? '是' : '否',
    license: getLicense(packageInfo),
  }))
  .sort((left, right) => {
    if (left.name !== right.name) return left.name < right.name ? -1 : 1;
    return left.version < right.version ? -1 : left.version > right.version ? 1 : 0;
  });

const missing = packages.filter((packageInfo) => packageInfo.license === '未声明');
const productionCount = packages.filter((packageInfo) => packageInfo.scope === '生产依赖').length;
const developmentCount = packages.length - productionCount;
const rows = packages.map((packageInfo) =>
  `| \`${packageInfo.name}\` | \`${packageInfo.version}\` | ${packageInfo.scope} | ${packageInfo.optional} | ${packageInfo.license} |`,
);

const content = [
  '# Website npm 依赖许可字段清单',
  '',
  `由 \`website/package-lock.json\`（lockfileVersion ${lock.lockfileVersion}）生成，共 ${packages.length} 个锁定包：生产依赖 ${productionCount} 个，开发依赖 ${developmentCount} 个。此表仅转录锁文件的 \`license\` 元数据，不代表发行物许可文本和再分发义务已审核。`,
  '',
  `当前有 ${missing.length} 个包未声明许可证：${missing.map((packageInfo) => `\`${packageInfo.name}@${packageInfo.version}\``).join('、') || '无'}。这些条目需要负责人核实后才能完成网站发布许可审查。`,
  '',
  '| 包名 | 锁定版本 | 范围 | Optional | 锁文件许可证字段 |',
  '|---|---:|---|---|---|',
  ...rows,
  '',
  '重生成：`node scripts/generate-website-license-inventory.cjs`。校验报告是否同步：`node scripts/generate-website-license-inventory.cjs --check`。`--strict` 检查完整锁文件，包含不随静态网站发布的可选依赖；公开发行应另运行 `node scripts/check-website-bundle-licenses.cjs --strict`，只按实际 Vite 浏览器 bundle 检查。',
  '',
].join('\n');

if (process.argv.includes('--check')) {
  let existing;
  try {
    existing = readFileSync(outputPath, 'utf8');
  } catch {
    console.error(`License inventory is missing: ${outputPath}`);
    process.exit(1);
  }
  if (existing !== content) {
    console.error(`License inventory is out of date: ${outputPath}`);
    process.exit(1);
  }
  if (process.argv.includes('--strict') && missing.length > 0) {
    console.error(`Strict license check failed: ${missing.length} package(s) have no license field.`);
    process.exit(2);
  }
  console.log(`Website license inventory is current (${packages.length} packages; ${missing.length} missing license fields).`);
  process.exit(0);
}

writeFileSync(outputPath, content, 'utf8');
console.log(`Wrote ${outputPath} (${packages.length} packages; ${missing.length} missing license fields).`);

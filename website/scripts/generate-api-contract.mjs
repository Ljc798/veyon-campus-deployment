import { copyFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const websiteDirectory = resolve(scriptDirectory, '..');
const repositoryDirectory = resolve(websiteDirectory, '..');
const specificationPath = resolve(
  repositoryDirectory,
  'src/VeyonCampus.Telemetry.Server/openapi/deployment-packages.yaml'
);
const source = await readFile(specificationPath, 'utf8');
const supportedMethods = new Set(['get', 'post', 'put', 'patch', 'delete', 'options', 'head']);
const seen = new Set();
const pathsHeader = /^paths:[ \t]*\r?$/m.exec(source);
if (!pathsHeader) throw new Error('OpenAPI document is missing the top-level paths section.');
const pathsStart = pathsHeader.index + pathsHeader[0].length;
const remainder = source.slice(pathsStart);
const componentsHeader = /^components:[ \t]*\r?$/m.exec(remainder);
const pathsSource = componentsHeader ? remainder.slice(0, componentsHeader.index) : remainder;
const pathMatches = [...pathsSource.matchAll(/^  (\/[^:\r\n]+):[ \t]*$/gm)];
if (pathMatches.length === 0) throw new Error('OpenAPI paths section contains no route entries.');

const operations = [];
for (let pathIndex = 0; pathIndex < pathMatches.length; pathIndex++) {
  const pathMatch = pathMatches[pathIndex];
  const path = pathMatch[1];
  const pathStart = pathMatch.index + pathMatch[0].length;
  const pathEnd = pathMatches[pathIndex + 1]?.index ?? pathsSource.length;
  const pathSource = pathsSource.slice(pathStart, pathEnd);
  const methodMatches = [...pathSource.matchAll(/^    (get|post|put|patch|delete|options|head):[ \t]*$/gim)];
  for (let methodIndex = 0; methodIndex < methodMatches.length; methodIndex++) {
    const methodMatch = methodMatches[methodIndex];
    const method = methodMatch[1].toLowerCase();
    if (!supportedMethods.has(method)) continue;
    const operationStart = methodMatch.index + methodMatch[0].length;
    const operationEnd = methodMatches[methodIndex + 1]?.index ?? pathSource.length;
    const operationSource = pathSource.slice(operationStart, operationEnd);
    const scalar = name => {
      const match = new RegExp(`^      ${name}: ([^\\r\\n]+)$`, 'm').exec(operationSource);
      return match?.[1]?.trim() || null;
    };
    const key = `${method.toUpperCase()} ${path}`;
    if (seen.has(key)) throw new Error(`Duplicate OpenAPI operation: ${key}`);
    seen.add(key);
    const operationId = scalar('operationId');
    const purpose = scalar('x-display-purpose');
    const access = scalar('x-access');
    if (!operationId || !purpose || !access) {
      throw new Error(`${key} must define operationId, x-display-purpose, and x-access.`);
    }
    operations.push({
      method: method.toUpperCase(),
      path,
      operationId,
      purpose,
      access,
      liveCheck: scalar('x-live-check'),
      contractText: operationSource.replace(/^ {6}/gm, '').trim()
    });
  }
}

if (operations.length === 0) throw new Error('OpenAPI document contains no operations.');

const generatedPath = resolve(websiteDirectory, 'src/api-contract.generated.js');
await writeFile(
  generatedPath,
  `// Generated from src/VeyonCampus.Telemetry.Server/openapi/deployment-packages.yaml.\n` +
    `export const API_OPERATIONS = Object.freeze(${JSON.stringify(operations)});\n`,
  'utf8'
);

const publicApiDirectory = resolve(websiteDirectory, 'public/api');
await mkdir(publicApiDirectory, { recursive: true });
await copyFile(specificationPath, resolve(publicApiDirectory, 'openapi.yaml'));
console.log(`Generated ${operations.length} API operations and copied OpenAPI 3.1 contract.`);

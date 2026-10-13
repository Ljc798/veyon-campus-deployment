import { rm, rmdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const outputDir = fileURLToPath(new URL('../dist', import.meta.url));
const apiSpecPath = join(outputDir, 'api', 'openapi.yaml');
const apiDirPath = dirname(apiSpecPath);

try {
  await rm(apiSpecPath);
} catch (error) {
  if (error.code !== 'ENOENT') throw error;
}

try {
  await rmdir(apiDirPath);
} catch (error) {
  if (error.code !== 'ENOENT' && error.code !== 'ENOTEMPTY') throw error;
}

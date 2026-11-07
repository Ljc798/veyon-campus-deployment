import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const docsDirectory = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'dist', 'docs');
for (const name of [
  '__search-evaluation.local.html', '__search-evaluation.local.js', '__search-evaluation.local-cases.json'
]) {
  const target = path.join(docsDirectory, name);
  if (fs.existsSync(target)) fs.rmSync(target, { force: true });
}
console.log('Removed local documentation search evaluation files from dist/docs.');

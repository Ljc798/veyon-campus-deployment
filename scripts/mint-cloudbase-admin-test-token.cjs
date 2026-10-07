'use strict';

const envId = process.argv[2] || '';
if (!/^[A-Za-z0-9-]+$/.test(envId)) {
  process.stderr.write('Invalid CloudBase environment ID.\n');
  process.exit(2);
}

async function readCredentials() {
  const chunks = [];
  let length = 0;
  for await (const chunk of process.stdin) {
    length += chunk.length;
    if (length > 16384) throw new Error('Credentials input is too large.');
    chunks.push(chunk);
  }
  const input = Buffer.concat(chunks).toString('utf8');
  const separator = input.indexOf('\n');
  if (separator < 1) throw new Error('Username and password input is incomplete.');
  const username = input.slice(0, separator).replace(/\r$/, '');
  const password = input.slice(separator + 1).replace(/\r?\n$/, '');
  if (!username || !password) throw new Error('Username and password are required.');
  return { username, password };
}

async function main() {
  const { username, password } = await readCredentials();
  let response;
  try {
    response = await fetch(`https://${envId}.api.tcloudbasegateway.com/auth/v1/signin`, {
      method: 'POST',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password }),
      redirect: 'error',
      signal: AbortSignal.timeout(30000)
    });
  } catch (error) {
    throw new Error(`CloudBase Auth login request failed (${error.name}).`);
  }
  if (!response.ok) {
    await response.body?.cancel();
    throw new Error(`CloudBase Auth login returned HTTP ${response.status}; check the account and try again.`);
  }
  let session;
  try {
    session = await response.json();
  } catch {
    throw new Error('CloudBase Auth returned an invalid login response.');
  }
  if (typeof session.access_token !== 'string' || session.access_token.length === 0)
    throw new Error('CloudBase Auth login response did not contain an access token.');

  // Stdout is captured by the parent runner and never displayed or persisted.
  process.stdout.write(session.access_token);
}

main().catch((error) => {
  process.stderr.write(`${error.message}\n`);
  process.exitCode = 1;
});

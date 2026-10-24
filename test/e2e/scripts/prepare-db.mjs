// Builds what the browser tests run on: the API and the Blazor app (built once here so the
// two dev servers do not compile the shared project at the same time), and a fresh copy of
// the seeded fixture database that the tests are free to change.
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(root, '..', '..');
const fixtureDir = resolve(root, '.fixture');
const baseDb = resolve(fixtureDir, 'e2e.db');
const runDb = resolve(fixtureDir, 'run.db');

function run(args, env = {}) {
  console.log(`> dotnet ${args.join(' ')}`);
  const result = spawnSync('dotnet', args, { cwd: repo, stdio: 'inherit', env: { ...process.env, ...env } });
  if (result.status !== 0) {
    console.error(`dotnet ${args[0]} failed with exit code ${result.status}`);
    process.exit(result.status ?? 1);
  }
}

mkdirSync(fixtureDir, { recursive: true });
run(['build', 'src/Server/Server.csproj', '-c', 'Debug', '--nologo', '-v', 'q']);
run(['build', 'src/WasmApp/WasmApp.csproj', '-c', 'Debug', '--nologo', '-v', 'q']);
run(
  ['test', 'test/RssReaderTests/RssReaderTests.csproj', '--nologo', '-v', 'q',
   '--filter', 'FullyQualifiedName~E2eFixtureBuilder'],
  { E2E_FIXTURE_PATH: baseDb },
);

for (const suffix of ['', '-wal', '-shm']) {
  rmSync(runDb + suffix, { force: true });
}
if (!existsSync(baseDb)) {
  console.error(`The fixture builder did not write ${baseDb}`);
  process.exit(1);
}
copyFileSync(baseDb, runDb);
console.log(`Fixture database ready: ${runDb}`);

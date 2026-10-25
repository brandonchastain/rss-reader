import { defineConfig, devices } from '@playwright/test';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '..', '..');
const runDb = resolve(here, '.fixture', 'run.db');

const API = 'http://localhost:8080';
const SITE = 'http://localhost:8443';

// The real API (Development environment, so the test user is the signed-in admin) and the
// real Blazor app, on the seeded database. Nothing here talks to the internet: the
// scheduler is off and the seeded feeds are never fetched.
export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  timeout: 45_000,
  expect: { timeout: 10_000 },
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL: SITE,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    // The viewport the reader is designed for first: a phone.
    ...devices['Pixel 7'],
  },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
  webServer: [
    {
      command: `dotnet run --no-build --project src/Server/Server.csproj -c Debug --no-launch-profile`,
      cwd: repo,
      url: `${API}/api/healthz`,
      timeout: 120_000,
      reuseExistingServer: !process.env.CI,
      stdout: 'pipe',
      stderr: 'pipe',
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: API,
        RssAppConfig__DbLocation: runDb,
        RssAppConfig__SchedulerEnabled: 'false',
      },
    },
    {
      command: `dotnet run --no-build --project src/WasmApp/WasmApp.csproj -c Debug --no-launch-profile`,
      cwd: repo,
      url: SITE,
      timeout: 120_000,
      reuseExistingServer: !process.env.CI,
      stdout: 'pipe',
      stderr: 'pipe',
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: SITE,
      },
    },
  ],
});

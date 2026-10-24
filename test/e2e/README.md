# Browser tests

Playwright drives the real API and the real Blazor app on a seeded database. Nothing
touches the internet: the feed scheduler is off and the seeded feeds are never fetched
(the API refuses loopback feed addresses, so posts are inserted straight into the database).

```bash
cd test/e2e
npm ci
npx playwright install chromium
npm test            # builds both apps, seeds a fresh database, starts both servers, runs the tests
```

`npm test` runs `scripts/prepare-db.mjs` first: it builds the API and the app, runs the
`E2eFixtureBuilder` test in `test/RssReaderTests` (which writes the seeded database through the
real repositories, so it cannot drift from the schema), and copies it to `.fixture/run.db`, which
the tests are free to change. Run it again for a clean database. `E2eFixtureBuilder` documents
exactly what is seeded (post titles, tags, read and saved state) and the specs rely on it by name.

The servers start on 8080 (API, Development environment, so the test user is signed in as
administrator) and 8443 (app). Locally an already-running pair is reused; in CI they are always
started fresh. Failures keep a trace and screenshot under `test-results/`
(`npx playwright show-trace <trace.zip>`).

Not covered here, on purpose: real sign-in with Microsoft (needs an account; the post-deploy
`.github/scripts/smoke-prod.sh` checks the sign-in plumbing) and anonymous access (the test
user authenticates every request; `SecurityContractTests` covers the 401s).

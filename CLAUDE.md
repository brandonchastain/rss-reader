# CLAUDE.md

## Commands
- Build: `dotnet build rss-reader.sln`
- Test: `dotnet test test/RssReaderTests/RssReaderTests.csproj --filter "TestCategory!=Network"` (what CI runs)
- Browser tests: `cd test/e2e && npm test`
- Run locally: the `run-locally` skill. Always use a Debug build, because a Release build of the frontend calls the production API.

## Deploying
- Merging to `main` deploys to production through CI. Ask before merging.
- Never run `az containerapp update`, `docker push` or `swa deploy` by hand: `az containerapp update` once dropped template settings and caused an outage. To roll back, revert the PR.
- To check production, use `https://api.rss.brandonchastain.com/api/healthz`. The site host has no `/api`.
- The full loop is the `ship` skill.

## Rules
- In production, Litestream owns the live database (`/tmp/storage.db`). Nothing else copies or writes it outside the repositories.
- Schema changes are additive only, in `src/Server/Data/DatabaseSchema.cs`.
- User-scoped controllers derive from `UserScopedController` and use `CurrentUser`. Never trust a user id from the request. Add a cross-user test to `ApiContractTests`.
- Every endpoint declares `[Authorize]` or `[AllowAnonymous]` (enforced by `ApiContractTests.EveryEndpoint_DeclaresItsAuthorization`).
- Don't loosen the OPML parser limits: DTD processing is off, and files are capped at 1 MB.
- UI: global CSS is in `src/WasmApp/wwwroot/css/app.css`; component CSS is in a `.razor.css` file beside the component. Check UI changes at 1280 px and 375 px wide.
- Docs: follow the `docs` skill. Update the docs in the same change as the code.

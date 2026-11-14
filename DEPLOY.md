# Deploying

Merging to `main` deploys production. `.github/workflows/deploy.yml` does it in three
steps:

1. Runs the tests, then builds and pushes the API image to GHCR.
2. Deploys `infrastructure/main.bicep` with that image.
3. Publishes the frontend to Static Web Apps, then runs a smoke check.

PRs that touch `infrastructure/` get a what-if.

**Don't deploy by hand** (`docker push`, `az containerapp update`, `swa deploy`).
`az containerapp update` once dropped settings the template declares and caused an
outage. The template is the only writer of container app config.

```bash
# Re-deploy main without a code change, and wait for it
gh workflow run deploy.yml --ref main
gh run watch $(gh run list --workflow deploy.yml --branch main --limit 1 --json databaseId --jq '.[0].databaseId') --exit-status
```

**To roll back,** revert the PR and merge the revert. Schema changes are additive, so
older code still runs on the newer database.

## Credentials

None live on your machine.

- **GHCR:** CI pushes with its own `GITHUB_TOKEN`. The package is public, and the
  Container App pulls it anonymously. If pulls fail with 403, make the package public
  again. Don't add a token.
- **Azure:** CI signs in with OIDC as the `gh-actions-rss-reader` managed identity
  (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` secrets).
- **Sign-in providers:** the `AUTH_{GOOGLE,MICROSOFT,GITHUB}_CLIENT_{ID,SECRET}` repo
  secrets. To rotate one, update the secret and re-run the deploy.

## Checking production

- API: `https://api.rss.brandonchastain.com/api/healthz` should report `"status":"healthy"`
  with no `"phase":"seeding"`.
- Site: `https://rss.brandonchastain.com/`. The site host has no `/api`, so a 500 there
  is expected.
- Sign-in plumbing: `bash .github/scripts/smoke-prod.sh`. CI runs it on every deploy.
  After changing auth code, also sign in once with each provider in a private window.

```bash
az containerapp logs show --name rss-reader-api --resource-group rss-container-rg --follow
az containerapp revision list --name rss-reader-api --resource-group rss-container-rg -o table
```

Logs go to the `rss-reader-logs` Log Analytics workspace (Warning and above).

## Troubleshooting

- **API answers 503 for a minute after a deploy.** This is normal: the new revision is
  waiting for the old one to hand over. If the old one died, its 60 s lease expires on
  its own.
- **App won't boot.** The startup restore failed. Look for `litestream restore` or
  `PRAGMA quick_check failed` in the logs. Check that the app's identity has
  `Storage Blob Data Contributor` on the storage account and that the `litestream`
  container holds a replica.

## New environment

1. `az group create --name rss-container-rg --location westus2`
2. Push an image to `ghcr.io/<user>/rss-reader-api` and make the package public.
3. Create DNS records for the API hostname first: a CNAME to the app's FQDN, and a TXT
   `asuid.<host>` holding the `customDomainVerificationId`. Then, from
   `infrastructure/`, run
   `az deployment group create -g rss-container-rg --template-file main.bicep --parameters main.bicepparam containerImage=...`.
   The deploying identity needs rights to create role assignments.
4. There is no replica to restore yet. For the first boot, set
   `RssAppConfig__Durability__RestoreFromBlob` to `false` in `main.bicep`, then set it
   back to `true`.
5. Set the repo secrets above. From then on, merge to deploy.

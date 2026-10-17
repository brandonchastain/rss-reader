# Self-hosting

The app is two parts: the ASP.NET Core API with its SQLite database, shipped as
one Docker image, and a Blazor WebAssembly frontend, which is a folder of static
files. The API does the logins itself (Google, Microsoft, GitHub) and is the only
piece that needs to stay running. How it keeps data safe is described once, in
[ARCHITECTURE.md](ARCHITECTURE.md#backup--replication).

Two ways to host:

- **One box** — the API container serves the frontend too. One container, one
  volume, one hostname.
- **Split** — the API on a container host, the frontend on any static host
  (what `rss.brandonchastain.com` does: Container Apps plus Static Web Apps).

## 1. Register a login provider

Pick at least one. Each gives you a client id and secret; the callback URL is
`https://<api host>/auth/callback/<provider>` (add
`http://localhost:8080/auth/callback/<provider>` while testing).

- **GitHub:** Settings > Developer settings > OAuth Apps > New OAuth App.
- **Google:** Cloud console > APIs & Services > OAuth consent screen (external),
  then Credentials > OAuth client ID, type *Web application*.
- **Microsoft:** Entra admin center > App registrations, account type *any org
  directory and personal Microsoft accounts*, platform *Web*, then a client secret.

## 2. Run the API

```bash
docker pull ghcr.io/brandonchastain/rss-reader-api:latest

docker run -d --name rss-reader \
  -p 8080:8080 \
  -v /srv/rss-reader/data:/data \
  -e RssAppConfig__BackupDbPath=/data/db/storage.db \
  -e RssAppConfig__Auth__DataProtectionKeysPath=/data/keys \
  -e RssAppConfig__Auth__AllowedFrontendOrigins__0=https://rss.example.com \
  -e RssAppConfig__Auth__Providers__github__ClientId=... \
  -e RssAppConfig__Auth__Providers__github__ClientSecret=... \
  ghcr.io/brandonchastain/rss-reader-api:latest
```

(Build it yourself with `docker build -f src/Server/Dockerfile -t rss-reader-api .`.)

- `/data` is the durability surface: the live database is `/tmp/storage.db`
  inside the container, copied to `BackupDbPath` every 5 minutes and restored
  from it at boot; cached feed images live under `/data/images/`; the session
  cookie's encryption keys under `/data/keys/` so a restart does not sign
  everyone out. Back up `/data` and you have everything. Leave `BackupDbPath`
  unset and the container runs in throwaway mode.
- `AllowedFrontendOrigins` is the list of origins the API accepts credentialed
  calls from and will send a user back to after login. For a one-box setup it
  is the API's own public origin. Index further origins as `__1`, `__2`.
- Add `google` and `microsoft` provider pairs the same way. A provider with no
  pair is simply not offered. `Authority` is optional (defaults to the public
  Google and Microsoft `common` endpoints).
- `RssAppConfig__AdminAadUserIds`: comma-separated user ids (as shown by
  `/auth/me`, e.g. `github:12345`) that may open `/admin`.
- For a single-user box with no login at all, `RssAppConfig__IsTestUserEnabled=true`
  makes every request the test user. Only on a network you control.

Every other setting is listed under [Knobs](ARCHITECTURE.md#knobs).

```bash
curl http://localhost:8080/api/healthz
curl http://localhost:8080/auth/providers     # ["github"]
```

## 3. Serve the frontend

Publish it once:

```bash
dotnet publish -c Release src/WasmApp/WasmApp.csproj -o publish
```

`publish/wwwroot` is the whole site. Before publishing, set `ApiBaseUrl` in
`src/WasmApp/wwwroot/appsettings.json` to the API's public origin with a
trailing slash, or leave it empty when the API serves the frontend itself.

**One box.** Mount the folder into the image's web root; the API serves it and
routes unknown paths to `index.html`:

```bash
docker run ... -v /srv/rss-reader/site:/app/wwwroot:ro ...
```

**Any static host** (nginx, Caddy, S3, Pages, Static Web Apps, ...): upload
`publish/wwwroot`, and make unknown paths fall back to `index.html` (the Blazor
router handles `/timeline`, `/feeds` and so on). `staticwebapp.config.json` in
that folder is the Static Web Apps form of those rules and sets the cache
headers worth copying: `_framework/*` immutable except `blazor.boot.json`,
`blazor.webassembly.js`, `dotnet.js` and `dotnet.native.wasm`, which are
`no-cache`. The frontend origin must appear in the API's
`AllowedFrontendOrigins`.

## 4. TLS

Put the API behind TLS (a reverse proxy such as Caddy or Traefik, or your
cloud's ingress) and make sure it forwards `X-Forwarded-Proto` and
`X-Forwarded-Host`; the API uses them to build the OAuth redirect URI and to
mark its cookie `Secure`. The session cookie is `SameSite=None; Secure`, which
browsers require for a frontend on another origin and accept on `http://localhost`
for development only. For a split setup the API and the frontend should share a
parent domain (`rss.example.com` and `api.rss.example.com`), which keeps some
browsers' third-party-cookie rules out of the way.

## Azure (what production runs on)

`infrastructure/main.bicep` builds the Container Apps environment, the API app
with its Azure Files volume, and the Static Web App. First-time steps:

1. `az group create --name rss-container-rg --location westus2`.
2. Get an image into `ghcr.io/<user>/rss-reader-api` (run the Deploy workflow
   once CI has Azure access, or push one with `gh auth token | docker login
   ghcr.io -u <user> --password-stdin`, then `docker logout ghcr.io`) and make the
   package **public**: the Container App pulls anonymously. If a pull later answers
   `error from registry: denied`, Docker is sending a stale ghcr.io credential whose
   token lacks the packages scope; `docker logout ghcr.io` fixes it.
3. `az deployment group create --resource-group rss-container-rg --template-file
   main.bicep --parameters main.bicepparam containerImage=... githubClientId=...
   githubClientSecret=...` from `infrastructure/`. Set `customDomain` and
   `apiCustomDomain` to your own names, or empty them. The API hostname needs
   its DNS records first (`CNAME` to the container app's FQDN and the `asuid`
   `TXT` record with the environment's `customDomainVerificationId`); the
   template then binds it and Azure issues the managed certificate.
4. On a brand-new environment pre-create an empty database at
   `/data/db/storage.db` on the file share (or copy one from a Docker run); the
   entrypoint refuses to boot without one.
5. Hand deploys to CI: `gh secret set` for `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID` (an OIDC-federated managed identity with deploy
   rights on the resource group) and the `AUTH_*_CLIENT_ID/SECRET` pairs. From
   here, merge to `main`; see [DEPLOY.md](DEPLOY.md).

Optional read replicas (`enableReadReplica`) are described in
[DEPLOY.md](DEPLOY.md#read-replicas-optional-off).

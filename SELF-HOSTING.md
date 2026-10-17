# Self-hosting

The app is three parts: a Blazor WebAssembly frontend (static files), a small
Azure Functions proxy that adds the gateway key, and the ASP.NET Core API with
its SQLite database. The API ships as one Docker image and is the only piece
that needs to stay running. How it keeps data safe is described once, in
[ARCHITECTURE.md](ARCHITECTURE.md#backup--replication); this guide does not
restate it.

Two ways to host:

- **Docker on a machine you own** — the simplest. One container, one volume.
- **Azure** — what `rss.brandonchastain.com` runs on. Container Apps for the
  API, Static Web Apps for the frontend and proxy, Azure Files for the data
  volume. The Bicep template builds all of it.

## Docker on your own host

### 1. Get the image

```bash
docker pull ghcr.io/brandonchastain/rss-reader-api:latest
```

Or build it from a checkout:

```bash
docker build -f src/Server/Dockerfile -t rss-reader-api:local .
```

### 2. Run it with a data volume

```bash
docker run -d --name rss-reader \
  -p 8080:8080 \
  -v /srv/rss-reader/data:/data \
  -e RssAppConfig__BackupDbPath=/data/db/storage.db \
  -e RSSREADER_API_KEY=change-me \
  ghcr.io/brandonchastain/rss-reader-api:latest
```

What those do:

- `/data` is the durability surface. The live database is `/tmp/storage.db`
  inside the container; every `BackupInterval` (5 minutes by default) it is
  copied to `BackupDbPath`, and every boot seeds `/tmp` from that copy. Cached
  feed images live under `/data/images/`. Back up the `/data` directory and you
  have everything.
- Leaving `BackupDbPath` unset runs the container in local-dev mode: nothing is
  copied to `/data` and the database is lost when the container is removed.
- `RSSREADER_API_KEY` is the shared secret the proxy presents on every request.
  Pick a long random value.

Check it is up:

```bash
curl http://localhost:8080/api/healthz
```

### 3. Identity

The API trusts the identity headers that Azure Static Web Apps' Easy Auth
injects (`x-ms-client-principal`). Off Azure you have two options:

- **Single user, no login:** set `RssAppConfig__IsTestUserEnabled=true` on the
  container and `IS_TEST_USER_ENABLED=true` on the proxy. Every request is the
  test user. Only do this on a network you control.
- **Real logins:** put the proxy and frontend behind something that sets the
  same principal header (the SWA CLI's auth emulator does this locally; a
  reverse proxy with its own OIDC login can be made to). This is not packaged.

### 4. Frontend and proxy

The frontend is static output from `dotnet publish -c Release src/WasmApp`,
and the proxy is the Node function in `api/`. The supported way to run both is
the Static Web Apps CLI, which also emulates Easy Auth:

```bash
npm install -g @azure/static-web-apps-cli
dotnet publish -c Release src/WasmApp/WasmApp.csproj --output src/WasmApp/bin/release/net9.0/publish
cd api && npm ci && cd ..
RSSREADER_API_URL=http://localhost:8080 RSSREADER_API_KEY=change-me \
  swa start src/WasmApp/bin/release/net9.0/publish/wwwroot --api-location api
```

The proxy reads `RSSREADER_API_URL`, `RSSREADER_API_KEY` and optionally
`IS_TEST_USER_ENABLED` from its environment (locally from
`api/local.settings.json`). Put the whole thing behind TLS before exposing it.

### 5. Tuning

Every setting the API and proxy read is listed under
[Knobs](ARCHITECTURE.md#knobs). The ones worth a look on a small host:
`BackupInterval` (how often the database file is copied), the scheduler
intervals (how hard it polls feeds), and `cpuCore`/`memorySize` if you are
sizing a VM.

## Azure (first-time buildout)

This is the one-time setup for a new environment. After it exists, every deploy
runs from CI on merge to `main`; see [DEPLOY.md](DEPLOY.md). Running the
template by hand after that is how production drifted once, so do not.

### Prerequisites

- Azure CLI (`az --version`) and a subscription
- GitHub CLI authenticated (`gh auth login`), with a fork or this repo
- Docker only if you need to push the very first image yourself

### Step 1: Resource group

```bash
az login
az account set --subscription "YOUR_SUBSCRIPTION_ID"
az group create --name rss-container-rg --location westus2
```

### Step 2: First image

The template references `ghcr.io/<user>/rss-reader-api`, so an image must
exist before the first deployment. Easiest is to run the Deploy workflow once
CI has Azure access (Step 4). To push one from a laptop first, use the `gh`
token rather than creating a personal access token:

```powershell
gh auth refresh --scopes write:packages
gh auth token | docker login ghcr.io -u <user> --password-stdin
docker build -t ghcr.io/<user>/rss-reader-api:latest -f src/Server/Dockerfile .
docker push ghcr.io/<user>/rss-reader-api:latest
docker logout ghcr.io
```

Then open the package on GitHub (Profile > Packages > `rss-reader-api` >
Package settings), set **Visibility** to **Public** and link it to the repo. The
Container App pulls anonymously; a private package fails every pull with
`ImagePullBackOff`.

Do not skip the `docker logout ghcr.io` at the end. If Docker keeps a ghcr.io
credential whose token lacks the packages scope, it sends that credential on
every pull and the registry answers `error from registry: denied`, even for
this public image. The fix is simply `docker logout ghcr.io`; no login is
needed to pull a public package.

### Step 3: Deploy the template

```powershell
cd infrastructure

# Random base64url gateway key
$bytes = New-Object byte[] 64
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$GATEWAY_SECRET_KEY = [Convert]::ToBase64String($bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

az deployment group create `
  --resource-group rss-container-rg `
  --template-file main.bicep `
  --parameters main.bicepparam `
  --parameters containerImage="ghcr.io/<user>/rss-reader-api:latest" `
  --parameters gatewaySecretKey=$GATEWAY_SECRET_KEY
```

`main.bicepparam` holds the sizing (0.5 vCPU, 1 GiB, one always-on replica)
and names; every parameter is documented under
[Knobs](ARCHITECTURE.md#infrastructure-infrastructuremainbicep-values-in-mainbicepparam).
Set `customDomain` to your own domain or remove the custom-domain resource.

On a brand-new environment there is no database on the share yet. The entrypoint
looks for `/data/db/storage.db`, then for a Litestream blob replica to bootstrap
from, and exits if it finds neither, so pre-create an empty database at that
path on the file share before the first start (or copy one from a Docker run).

### Step 4: Hand deploys to CI

```bash
gh secret set GATEWAY_SECRET_KEY
gh secret set AZURE_CLIENT_ID        # the gh-actions-rss-reader managed identity
gh secret set AZURE_TENANT_ID
gh secret set AZURE_SUBSCRIPTION_ID
```

The workflow logs in with OIDC through a user-assigned managed identity
(`gh-actions-rss-reader`, created outside the template) with deploy rights on
the resource group, and reads the Static Web Apps deployment token from Azure
at run time. Nothing else is stored. From here, merge to
`main` and the Deploy workflow builds, pushes and deploys.

### Optional: read replicas

The template can deploy a read-only Container App that scales 0 to N and
restores its own copy of the database from a Litestream blob replica. It needs
`RSSREADER_ENABLE_LITESTREAM_REPLICATION=true` on the writer so that replica
exists. Enable with `enableReadReplica=true` and `maxReadReplicas` in
`main.bicepparam`, then merge. It is off in production; a single warm replica
has been enough.

# Self-hosting

The app has two parts: an API (one Docker image, with SQLite inside) and a frontend (a
folder of static files). There are two ways to host it:

- **One box:** the API container also serves the frontend.
- **Split:** the API on a container host, the frontend on any static host.

## 1. Register a sign-in provider

Register at least one. The callback is `https://<api host>/auth/callback/<provider>`.

- **GitHub:** Settings > Developer settings > OAuth Apps.
- **Google:** Cloud console > OAuth consent screen (external), then an OAuth client of
  type *Web application*.
- **Microsoft:** Entra > App registrations, platform *Web*, plus a client secret. By
  default only personal Microsoft accounts can sign in (the `consumers` authority).

## 2. Run the API

```bash
docker run -d --name rss-reader -p 8080:8080 \
  -v /srv/rss-reader/data:/data \
  -e RssAppConfig__DbLocation=/data/storage.db \
  -e RssAppConfig__Auth__DataProtectionKeysPath=/data/keys \
  -e RssAppConfig__Auth__AllowedFrontendOrigins__0=https://rss.example.com \
  -e RssAppConfig__Auth__Providers__github__ClientId=... \
  -e RssAppConfig__Auth__Providers__github__ClientSecret=... \
  ghcr.io/brandonchastain/rss-reader-api:latest
```

- `/data` holds everything worth keeping: the database and the cookie keys. Back it up
  yourself. To copy it while the API runs:
  `sqlite3 /srv/rss-reader/data/storage.db ".backup '/srv/backups/storage.db'"`.
- `AllowedFrontendOrigins` lists the origins the API accepts calls from and returns users
  to after sign-in. Add more as `__1`, `__2`, and so on.
- `RssAppConfig__AdminAadUserIds` is a comma-separated list of ids (as `/auth/me` shows
  them, for example `github:12345`) that can open `/admin`.
- For a private single-user box with no sign-in, set
  `RssAppConfig__IsTestUserEnabled=true` and
  `RssAppConfig__AllowTestUserInProduction=true`.
- Every other setting is documented in `src/Server/Config/`.

Check it with `curl http://localhost:8080/api/healthz`.

## 3. Serve the frontend

1. Set `ApiBaseUrl` in `src/WasmApp/wwwroot/appsettings.json` to the API's public origin,
   with a trailing slash. Leave it empty for one box.
2. Publish: `dotnet publish -c Release src/WasmApp/WasmApp.csproj -o publish`.
3. Serve `publish/wwwroot`, in one of two ways:
   - **One box:** mount it at `/app/wwwroot`
     (`-v /srv/rss-reader/site:/app/wwwroot`). Keep the mount writable, because the API
     caches feed icons under `wwwroot/images`.
   - **Static host:** upload it, and make unknown paths fall back to `index.html`.
     `staticwebapp.config.json` shows the routing and cache rules to copy.

## 4. TLS

Put the API behind a TLS reverse proxy that forwards `X-Forwarded-Proto` and
`X-Forwarded-Host`. For a split setup, give both parts the same parent domain
(`rss.example.com` and `api.rss.example.com`) so that browsers keep the session cookie.

# Copilot Instructions

## Build, test, and lint commands
- `dotnet build rss-reader.sln` (runs the full solution plus Roslyn analyzers; there is no separate lint step).
- `dotnet build src\Server\Server.csproj` when working on the backend only.
- `dotnet build src\WasmApp\WasmApp.csproj` when iterating on the Blazor WebAssembly UI.
- `dotnet test test\SerializerTests\SerializerTests.csproj` to run the MSTest suite.
- To run a single test: `dotnet test test\SerializerTests\SerializerTests.csproj --filter "FullyQualifiedName~<TestNameFragment>"`.
- Frontend WASM assets are produced with `swa build` from the repo root, and deployment is handled via `swa deploy --env production` (the `swa-cli.config.json` file points at the target Static Web App).
- The Azure Functions API proxy under `api/` is started locally with `npm run start` (which runs `func start`).

## Local and containerized testing
- F5 in Visual Studio launches the backend, the Wasm app, and a browser window if you prefer the non-containerized dev loop.
  (Run the `dotnet run` command for the Wasm app in an async/background session so it keeps serving content even while you're working.)
- For container testing:
  1. `cd src` and `docker build -f Server/Dockerfile -t rss-reader-api:local .`
  2. Ensure `C:\dev\rssreader\docker-data` exists (will hold `/data/storage.db`) and run:
     ```
     docker run -d --name rss-reader-test -p 8080:8080 -v c:\dev\rssreader\docker-data:/data rss-reader-api:local
     ```
  3. Use `curl http://localhost:8080/api/feed` or a browser to exercise the API.
  4. Logs and container state are checked with `docker logs rss-reader-test`, `docker ps`, and `docker exec -it rss-reader-test /bin/bash`.
- The backend writes the active SQLite database to `/tmp/storage.db` inside the container; the volume mount copies it to `/data/storage.db` on the host, so you can inspect or back up the file directly.

## Deployment and registry commands
- Push the backend image to GHCR (after setting `$GITHUB_USERNAME` and `$GITHUB_PAT` with a PAT that has `read:packages` and `write:packages`):
  ```
  cd src
  docker build -f Server/Dockerfile -t ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest .
  docker push ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest
  ```
- Update the Azure Container App with the new image:
  ```
  az containerapp update \
    --name rss-reader-api \
    --resource-group rss-container-rg \
    --image ghcr.io/$($env:GITHUB_USERNAME)/rss-reader-api:latest
  ```
- Infrastructure is defined in `infrastructure/main.bicep`; deployments require Azure CLI, the generated gateway secret key, and GHCR credentials as described in `DEPLOY.md`.
- Frontend deployment follows `swa build && swa deploy --env production` from the repo root, and you should confirm `swa-cli.config.json` is pointed at the intended Static Web App before running the deploy step.

## High-level architecture
- Frontend: `src/WasmApp` is a Blazor WebAssembly app written in C# that runs in the browser and calls `/api/*`.
- API proxy: The `api/` folder holds an Azure Functions proxy that forwards `/api/feed/*`, `/api/item/*`, and `/api/user/*` requests to the backend; this keeps the client from talking directly to the server and simplifies hosting inside Static Web Apps.
- Backend: `src/Server` is an ASP.NET Core Web API that exposes controllers for feeds, items, and users, and it writes to an SQLite database.
- Shared: `src/Shared` houses DTOs and helper logic shared between the frontend and backend assemblies, so changes there affect both projects.
- Database: SQLite lives in `/data/storage.db` inside the container and is mounted to `C:\dev\rssreader\docker-data` on the host when running locally or in Docker; in Azure, the storage mount is handled by Azure Files.

## Key conventions
- All projects target `net9.0`; rely on `dotnet build` to trigger the Roslyn analyzers that enforce coding rules.
- Tests live in `test/SerializerTests`; they use MSTest and the Microsoft.NET.Test.Sdk packages referenced by that project.
- The repository has no global `.editorconfig` or ESLint config—follow the conventions enforced by each project’s analyzers.
- Docker builds keep state at `C:\dev\rssreader\docker-data`; don’t delete this directory unless you intend to reset all persisted feeds/posts.
- Azure deployments rely on GHCR credentials (set via environment vars) and the containerapp open-source references in `infrastructure/main.bicep`; keep secrets and PATs out of source control.
- Before pushing the frontend, double-check `swa-cli.config.json` points to the correct Static Web App environment.
- When working on the API proxy, use `npm run start` to launch `func start` from the `api/` directory; no additional backend logic lives there.

## References
- README.md gives a summary of features and links to the architecture, local testing, and deploy guides.
- Architecture, LOCAL-TESTING, and DEPLOY docs hold the authoritative diagrams, Docker workflow, and Azure deployment steps referenced above.

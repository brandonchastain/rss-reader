# RSS Reader

A simple browser-based RSS reader, live at <https://rss.brandonchastain.com>.

Subscribe to RSS and Atom feeds, read them in one infinite-scroll timeline, organize
feeds with tags, save and search posts, and import or export your feeds as OPML.

## Layout

| Path | What |
|---|---|
| `src/Server` | ASP.NET Core API, SQLite database, sign-in, feed refresh |
| `src/WasmApp` | Blazor WebAssembly frontend (static files) |
| `src/Shared` | Contracts shared by both |
| `infrastructure` | Azure Bicep template and Litestream config |
| `test/` | Unit tests, Playwright e2e, Docker durability test, k6 load scenarios |

## Docs

- [LOCAL-TESTING.md](LOCAL-TESTING.md): run and test it on your machine
- [ARCHITECTURE.md](ARCHITECTURE.md): how the pieces fit and how data is kept safe
- [DEPLOY.md](DEPLOY.md): how production deploys, and what to do when it breaks
- [SELF-HOSTING.md](SELF-HOSTING.md): run your own copy

## OPML

Import reads `<outline type="rss" xmlUrl="...">` elements and takes tags from a
comma-separated `category` attribute. Other outlines are skipped, so folders from
other readers don't become tags. Files are limited to 1 MB. Export writes the same shape.

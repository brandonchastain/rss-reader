# RssReader | A simple browser-based RSS reader

## Features
🗞️ Subscribe to RSS and Atom feeds

📰 Browse recent posts in an infinite-scroll feed

🏷️ Categorize feeds using <b>tags</b>

🚰 Curate your own feed with <b>tag filters</b> and <b>saved posts</b>

🔍 <b>Search</b> for posts

📥 <b>Import and export</b> feeds to/from other reader apps

📱 Responsive design for mobile & desktop

# Docs
* [ARCHITECTURE.md](ARCHITECTURE.md) — how the pieces fit, how data is kept safe, and every tunable knob
* [LOCAL-TESTING.md](LOCAL-TESTING.md) — run the stack on your machine
* [SELF-HOSTING.md](SELF-HOSTING.md) — host it yourself, on Docker or on Azure
* [DEPLOY.md](DEPLOY.md) — how production deploys from CI, and the runbook
* [OPML-FORMAT.md](OPML-FORMAT.md) — the import/export format

# Running costs
Background work is what keeps the always-on replica off Container Apps' cheaper idle
rate, and every blob call is billed, so the cadences are settings in
`infrastructure/main.bicep`, each a trade of dollars against recovery:

* `LITESTREAM_SYNC_INTERVAL` (30 min): how much a hard crash can lose. At 30 s it cost
  about $3/month in blob listings alone.
* `RssAppConfig__Durability__LeaseDuration` / `LeaseRenewInterval` (60 s / 20 s): how
  long a crashed writer's lease blocks the next one.
* `RssAppConfig__Durability__HandoffPollInterval` (10 s): how long a deploy waits
  before the handover starts.
* `RssAppConfig__FeedRefreshInterval` (1 h): how fresh posts are. It runs as one
  short burst an hour, so it costs little.

Each deploy also uploads one full copy of the database (about 230 MB) while
Litestream re-establishes its position; that is inherent, so fewer deploys is
the only lever there.

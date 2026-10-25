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
Blob replication (BLOB-DURABILITY-SPEC.md) costs about $0.10 a day in storage
operations, most of it the writer lease renewing every 10 s. Two config
changes halve that, at the price of a slower crash recovery:

* `RssAppConfig:Durability:LeaseDuration` 30 s → 60 s and `LeaseRenewInterval`
  10 s → 20 s (a crashed writer's lease then takes 60 s to expire, not 30).
* `sync-interval` in `infrastructure/litestream.yml` 30 s → 60 s (up to a
  minute of writes lost in a crash instead of 30 s).

Each deploy also uploads one full copy of the database (about 230 MB) while
Litestream re-establishes its position; that is inherent, so fewer deploys is
the only lever there.

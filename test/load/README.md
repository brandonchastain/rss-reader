# Load tests

k6 scenarios in `scenarios/`, run against a local API on 8080 (see
[LOCAL-TESTING.md](../../LOCAL-TESTING.md)).

```bash
k6 run -e BASE_URL=http://localhost:8080 scenarios/read-baseline.js
test/load/capacity.sh          # steps through request rates, prints where p95 leaves 500 ms
test/load/refresh-pacing.sh    # request latency during a 5,000-feed refresh cycle (see the script header)
```

To mimic production, cap the container at the live size (`--cpus` and `--memory`
matching `infrastructure/main.bicepparam`) and use a copy of the production database.

## Findings worth keeping

These were measured on 2026-10-24 with 0.5 vCPU and 1 GiB. Production is now 0.25 vCPU
and 0.5 GiB, so re-measure before relying on the numbers.

- **Capacity:** about 110 req/s sustained on the read-heavy mix, then a cliff, not a
  slope. Real traffic peaks around 10 req/s.
- **Refresh pacing:** an unpaced refresh of 5,000 feeds took the whole CPU, and request
  p95 at 50 req/s rose to 513 ms. Pacing at 5 fetches a second brought it down to 30 ms.
  At 20 a second it was 216 ms. The scheduler now starts at most
  `SchedulerFetchesPerSecond` (default 10) fetches a second. Yielding to live traffic was
  tried and dropped: it barely helped.

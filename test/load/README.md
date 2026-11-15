# Load tests

k6 scenarios in `scenarios/`, run against a local API on 8080 (the `run-locally`
skill starts one).

```bash
k6 run -e BASE_URL=http://localhost:8080 scenarios/read-baseline.js
test/load/capacity.sh          # steps through request rates, prints where p95 leaves 500 ms
test/load/refresh-pacing.sh    # request latency during a 5,000-feed refresh cycle (see the script header)
```

To mimic production, cap the container at the live size (`--cpus` and `--memory`
matching `infrastructure/main.bicepparam`) and use a copy of the production database.

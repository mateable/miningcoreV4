# Benchmark: REST API Throughput

## What's Being Measured

5,000 sequential GET requests against `/api/health-check` (safe endpoint with no DB dependency). Measures raw HTTP throughput of the API layer.

## Results

| Metric | MVC Controllers | Minimal APIs |
|--------|-----------------|--------------|
| Total Requests | 5,000 | 5,000 |
| Total Time | 10.14 s | 2.56 s |
| Throughput | ~493 req/s | **~1,956 req/s** |
| Success Rate | 100% | 100% |
| Improvement | — | **~4× faster** |

## Raw Benchmarks

### Before (MVC Controllers)
```
Total Requests: 5000
Total Time: 10.14 seconds
Requests/sec: 493
Successful: 5000
Failed: 0
```

### After (Minimal APIs)
```
Total Requests: 5000
Total Time: 2.56 seconds
Requests/sec: 1956
Successful: 5000
Failed: 0
```

## Endpoint Verification

All endpoints tested manually with `curl`:
- `GET /api/help` — returns route listing
- `GET /api/health-check` — returns `👍`
- `GET /api/pools` — returns pool list with stats
- `GET /api/pools/{poolId}` — returns single pool
- `GET /api/blocks` — returns blocks (with query params)
- `GET /api/admin/stats/gc` — returns GC stats (`gcGen0`, `memAllocated`)

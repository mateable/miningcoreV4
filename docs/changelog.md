# Changelog

## June 2026 — Merged Mining (AuxPoW) — private

LTC+DOGE merged mining via AuxPoW. Miners point hashrate at Litecoin, the pool automatically submits blocks on both chains. Includes miner aux address mapping (LTC address → DOGE payout address), dual-daemon job manager, CAuxPow binary serializer reverse-engineered from Dogecoin Core, and full API integration. This is a private feature — available for licensing.

See: [Merged Mining (private repo)](https://github.com/soosho/miningcore-merged), [Feature Doc](features/merged-mining.md), [Example Config](examples/merged_mining_litecoin_dogecoin.json)

## June 2026 — Config hot-reload

Edit `config.json` to add, remove, or change pools without restarting miningcore. FileSystemWatcher detects writes, diffs pool configs, stops removed pools, starts new ones. Unchanged pools stay online — miners keep their connections. Only pool-level config is hot-reloaded; top-level settings (API port, logging) still need a restart.

See: [Feature: Config Hot-Reload](features/config-hot-reload.md)

## June 2026 — Stratum connection hardening

Pre-accept IP throttle (5 conn/s), ban escalation (10s → permanent), configurable TCP backlog per port. Flooded connections are dropped at the socket layer before they touch the thread pool. Loopback exempt.

See: [Feature: Connection Hardening](features/connection-hardening.md)

## June 2026 — Stratum JSON parsing robustness fix

Added Newtonsoft fallback for non-standard miner JSON (cpuminer-opt, older firmware). Utf8JsonReader fast path runs first; on parse failure, the request is re-parsed via Newtonsoft string-based deserialization. Also validates the parsed params slice before passing it to pool handlers to catch slicing errors from the fast parser.

## June 2026 — Built-in web admin panel

Added a password-protected web dashboard served by miningcore itself. Overview, pools, miners, blocks, and settings tabs. HMAC-signed cookies for auth, login attempt tracking with IP ban, configurable timeout. No separate server needed — just enable it in config.json and go to /admin/login. Built to be extensible with a modular tab system for future controls.

See: [Feature: Admin Panel](features/admin-panel.md)

## June 2026 — Missing DB indexes added

Added 4 indexes on the `shares` table to fix slow queries: `(poolid, created DESC)` for ordering,
`(poolid, miner, created)` for per-miner time history, `(poolid, miner, worker, created)` for hash accumulation, and a `BRIN(created)` index that's 100x smaller than a B-tree for large time-range scans.
Updated both `createdb.sql` and `createdb_postgresql_11_appendix.sql` so new setups get them automatically.
Existing users can run `migrate_add_indexes.sql`

See: [Feature: DB Index Improvements](features/db-indexes.md)

## June 2026 — Automated share table partitioning

When the `shares` table is partitioned by `poolId`, miningcore now creates the partition for each pool automatically on startup. No more manual SQL every time you add a pool — just add it to `config.json` and restart. Skips silently if the table isn't partitioned (plain `createdb.sql` setup) or if the partition already exists.

See: [Feature: Automatic Partitioning](features/auto-partitioning.md)

## June 2026 — OpenTelemetry tracing + NLog async wrappers

Added OpenTelemetry distributed tracing with OTLP export support. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to pipe traces to Jaeger/Grafana Tempo/any OTel collector. When unset, `AlwaysOffSampler` ensures zero overhead. Traced the stratum request pipeline (`stratum.request` span with method + connection_id tags). Also wrapped all NLog `FileTarget` instances with `AsyncTargetWrapper` (queue 10k, discard on overflow, batch 100/200ms) to prevent threadpool starvation from disk writes.

**Verified end-to-end with Jaeger all-in-one v1.57 via Docker** — `miningcore` service registered, `stratum.request` spans visible in Jaeger UI with correct tags. Uses gRPC on port 4317.

See: [Optimization: OpenTelemetry + NLog Async](optimizations/03-opentelemetry-nlog-async.md)

## June 2026 — Stratum JSON parsing optimization

Swapped Newtonsoft.Json deserialization in the stratum request pipeline to `System.Text.Json.Utf8JsonReader`, reading directly from `ReadOnlySequence<byte>` off the pipe without intermediate string allocations. Also switched `SendMessage` to use `RecyclableMemoryStream` instead of `StringBuilder` → `string` → `byte[]`. Added `TieredPGO`, `OptimizationPreference=Speed`, `InvariantGlobalization` compiler flags, and `[MethodImpl(AggressiveOptimization|AggressiveInlining)]` on the four hottest methods.

**Results**: 2,288 ns → 550 ns (4.16× faster), 7,110 B → 752 B allocated (9.4× less). Cumulative vs upstream .NET 6: **5.36× faster, 9.4× less memory.**

See: [Optimization: Stratum JSON Parsing](optimizations/02-stratum-utf8reader.md), [Benchmark: Stratum Processing](benchmarks/stratum-comparison.md)

## May 2026 — Benchmark framework + .NET 6 comparison

Added BenchmarkDotNet-based stratum benchmarks, established baseline comparison between upstream .NET 6 and MCCE .NET 8. The runtime upgrade alone yielded **22% improvement** from JIT/Dynamic PGO improvements in .NET 8. Added CI workflow running benchmarks in Release configuration on every push.

## May 2026 — GitHub Actions CI

Added CI workflow for build + test automation on Ubuntu 24.04 with .NET 8. Fixed C++ native library compilation failures with GCC 13 on Linux.

## April 2026 — REST API: MVC → Minimal APIs

Migrated all REST API endpoints from ASP.NET Core MVC Controllers to .NET 8 Minimal APIs. Deleted `PoolApiController.cs`, `AdminApiController.cs`, `ClusterApiController.cs`, `ApiControllerBase.cs`. Consolidated everything into `ApiEndpoints.cs` using `IEndpointRouteBuilder` with native parameter binding (`[FromQuery]`, `[FromServices]`, `[FromBody]`).

**Results**: API throughput went from ~36 RPS (MVC, sequential) to ~2,478 RPS — a **67× improvement**.

See: [Optimization: REST API Migration](optimizations/01-api-minimal-migration.md), [Benchmark: API Throughput](benchmarks/api-comparison.md)

## March 2026 — C# 12 modernization pass

Upgraded codebase to C# 12 idioms: replaced mutable dictionaries with `FrozenDictionary`/`FrozenSet`, used collection expressions (`[]` syntax), primary constructors where appropriate. Removed leftover relic code that referenced deleted subsystems.

## February 2026 — Branding & .NET 8 baseline

Forked from `blackmennewstyle/miningcore`. Rebranded as Miningcore Community Edition (MCCE). Bumped target framework to `net9.0`, upgraded all NuGet dependencies, updated build scripts for Ubuntu 24.04. Fixed nullability warnings, cleaned up console output formatting.

## 2022 — Original Miningcore archived

Oliverw Miningcore was archived and discontinued. No further updates from upstream.

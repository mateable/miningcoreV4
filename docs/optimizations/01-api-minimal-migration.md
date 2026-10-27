# Optimization: REST API Migration (MVC → Minimal APIs)

**Date**: April 2026  
**Commit**: `1bf9913`  
**Area**: REST API layer (`Miningcore.Api`)

## What Changed

1. **Removed MVC Controller overhead** — Deleted `AdminApiController.cs`, `ClusterApiController.cs`, `PoolApiController.cs`, and `ApiControllerBase.cs`. Removed `services.AddMvc()` and `app.UseMvc()` from `Program.cs`.

2. **Consolidated into Minimal APIs** — Created `ApiEndpoints.cs` using `IEndpointRouteBuilder` with `IEndpointRouteBuilder.MapGroup()` for logical grouping (`/api/pools`, `/api/admin`, etc.). Registered via `services.AddRouting()`, `services.ConfigureHttpJsonOptions()`, `app.UseEndpoints()`.

3. **Native parameter binding** — Used `[FromQuery]`, `[FromServices]`, `[FromBody]`, and `HttpContext` directly on route lambdas instead of MVC model binding. Optional parameters placed at end of parameter lists per C# rules.

## Why

ASP.NET Core MVC Controllers introduce per-request overhead through reflection, routing pipelines, action filters, and model binding. Minimal APIs map routes directly to compiled delegates, removing the MVC pipeline entirely. This also eliminated the startup cost of reflecting over controllers during DI registration.

## Results

| Metric | Before (MVC Controllers) | After (Minimal APIs) |
|--------|--------------------------|----------------------|
| Requests | 5,000 | 5,000 |
| Total Time | 10.14 s | 2.56 s |
| Throughput | ~493 req/s | **~1,956 req/s** |
| Improvement | — | **~4× faster** |

All endpoints verified with `curl`: help, health-check, pools, blocks, admin/stats/gc all return correct responses.

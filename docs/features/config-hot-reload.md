# Feature: Config Hot-Reload

**Date**: June 2026  
**Area**: Core / Pool Management

## What it does

Watches `config.json` for changes and automatically adds, removes, or restarts pools without restarting the miningcore process. Miners on unchanged pools stay connected.

## Why this matters

For a pool operator running multiple coins, adding a new coin or tweaking a pool config meant restarting the entire server. Every connected miner gets disconnected, reconnects, re-subscribes, re-authorizes. For a pool with hundreds of miners, that's hundreds of TCP handshakes and stratum sessions hitting zero within a single restart.

This feature eliminates that downtime. Edit `config.json`, save, and only the affected pools get cycled. Everyone else keeps mining.

## How it works

1. A `FileSystemWatcher` monitors `config.json` for writes
2. On change, waits 500ms (debounce — ignores rapid saves from editors)
3. Reloads the config JSON and diffs it against the current pool list
4. Detects three cases:
   - **Pool removed or disabled** → cancels the pool's `CancellationToken`, waits up to 10s for graceful shutdown, removes from pool registry
   - **Pool config changed** (coin, address, ports, daemon endpoints, payment settings) → stops the old pool instance, starts a fresh one
   - **New pool added** → resolves the coin template, creates the pool instance, starts it alongside existing pools
5. Unchanged pools are not touched — their `CancellationToken` stays alive, stratum listeners keep running

Pool config comparison ignores `_`-prefixed keys in the `extra` section, so comment/documentation fields don't trigger false restarts.

## What you see in the logs

```
[I] Config hot-reload active — watching config.json
[I] Config change detected — reloading pools...
[I] Pool 'dogecoin' started
[I] Pool 'litecoin' started
```

When a config change triggers a restart:

```
[I] Config change detected — reloading pools...
[I] Pool 'litecoin' config changed — restarting
[I] Pool 'litecoin' started
```

When a pool is disabled or removed:

```
[I] Config change detected — reloading pools...
[I] Pool 'dogecoin' disabled
```

## Limitations

- **Top-level config changes** (API port, logging, persistence) still require a process restart. Only pool-level config is hot-reloaded.
- **Coin template changes** (`coins.json`) are re-read on reload, but adding brand-new coin definitions requires the template file to exist before the config references them.
- **Pool startup errors** are logged but don't crash the process — other pools continue running.

## Files

| File | Purpose |
|------|---------|
| `src/Miningcore/Configuration/ConfigHotReloadService.cs` | FileSystemWatcher, diff logic, pool start/stop orchestration |
| `src/Miningcore/Program.cs` | Registration of the hot-reload service in the DI container |

# Optimization: OpenTelemetry Tracing + NLog Async Wrappers

**Date**: June 2026  
**Area**: Logging & Telemetry

## NLog Async Wrappers

### Why

When miningcore writes logs to a file, it normally waits for the disk to finish writing before moving on. If the disk is slow (HDD, network drive, heavy I/O), this can block the pool's worker threads and cause share processing delays. The async wrapper fixes this by queuing log messages and writing them on a background thread — mining never waits for the disk.

### What changed

All file log targets are automatically wrapped with `AsyncTargetWrapper`:
- **Queue**: 10,000 messages (you won't lose logs under normal conditions)
- **If queue fills up**: newer logs are dropped instead of blocking the pool
- **Flushes**: every 200ms or every 100 messages, whichever comes first
- Console output is unchanged (doesn't need async — it's already fast)

### How to use

Nothing to configure. It just works — all file targets in `config.json` (`logFile`, `apiLogFile`, `perPoolLogFile`) are wrapped automatically.

---

## OpenTelemetry Tracing

### What it is in plain words

Think of it as a **detailed timeline** of what your pool is doing, second by second.

Normally when something goes wrong, you scroll through log files looking for error messages. Tracing gives you a visual timeline showing:

> "At 14:32:01, miner X sent a share → it took 45ms to validate → the RPC call to the daemon took 30ms → share accepted"

You can see exactly where time is being spent, which shares are slow, and which daemon calls are lagging.

### What it's NOT

- It does **not** replace Prometheus metrics (hashrate, shares, connections — those still work as before)
- It does **not** replace NLog (logs are still written to file/console)
- It does **not** slow down your pool (when disabled, zero overhead)

### Benefits — why you'd turn it on

| Situation | What you see with tracing | What you can fix |
|-----------|--------------------------|------------------|
| Miners complaining about high reject rate | Stratum submissions taking unusually long | Find bottleneck in share validation |
| Pool feels sluggish | Daemon RPC calls timing out or retrying | Spot which daemon is slow |
| Unexplained spikes | GC pauses or threadpool starvation visible as gaps | Tune GC or connection limits |
| After upgrading miningcore | Before/after trace comparison | Prove performance didn't regress |
| Debugging a new coin integration | Full trace from miner → stratum → daemon → response | Pinpoint where integration fails |

### When to leave it OFF

Running it yourself requires setting up an extra service (Jaeger or Grafana Tempo) and connecting it. If your pool is small and running fine, you don't need it. When it's off (`otlpEndpoint` empty), **zero overhead** — it doesn't even check for a collector.

### Architecture — how it fits together

```
Miner → miningcore ──?──→ OTLP Collector ──→ Jaeger/Grafana Tempo ──→ Web UI
                      └──?── (optional, only if configured)
```

- **Miningcore** creates trace data for each stratum request (method name, duration, connection ID)
- **OTLP Collector** receives the traces and stores them (Jaeger is the simplest — one Docker command)
- **Web UI** (Jaeger at `localhost:16686`) lets you search and browse traces visually

### What's currently traced

| What | Trace name | Shows you |
|------|-----------|-----------|
| Every stratum request from a miner | `stratum.request` | Which method (subscribe/authorize/submit), how long it took, which connection |

More can be added later (share validation times, daemon RPC latency, payment processing duration).

### How to set it up

**Step 1: Start Jaeger (one-time, runs in background)**

```bash
docker run -d --name jaeger \
  -p 4317:4317 \
  -p 16686:16686 \
  jaegertracing/all-in-one:1.57
```

**Step 2: Add to config.json**

```json
{
  "logging": {
    "otlpEndpoint": "http://localhost:4317"
  }
}
```

**Step 3: Start miningcore as usual**

**Step 4: Open the UI**

Go to `http://localhost:16686` in your browser, select service `miningcore`, click "Find Traces".

### How to view traces

1. Open `http://localhost:16686` in your browser
2. In the Search panel, select `miningcore` as the Service
3. Click "Find Traces"
4. Click on any trace to see its timeline — each `stratum.request` span shows the method name and duration
5. Look for slow traces (long duration) to find bottlenecks

### Cost / overhead

| Scenario | Memory per request | CPU per request |
|----------|-------------------|-----------------|
| Tracing disabled (otlpEndpoint empty) | 0 bytes | 0 cycles |
| Tracing enabled (collector running) | ~200 bytes for sampled requests | negligible |
| Collector not reachable | OTel SDK retries then drops | minimal |

When disabled, `ActivitySource.StartActivity()` returns `null` immediately — no object created, no allocations. This is verified by the benchmark (752 B allocated per request regardless).

### Key env vars (optional overrides)

These are standard OpenTelemetry env vars that work automatically if set:

| Variable | Purpose | Default |
|----------|---------|---------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Collector address | (from config.json) |
| `OTEL_TRACES_SAMPLER` | How many traces to keep | `always_on` |
| `OTEL_TRACES_SAMPLER_ARG` | Sampling ratio | — |

In production with many miners, set `OTEL_TRACES_SAMPLER=parentbased_traceidratio` and `OTEL_TRACES_SAMPLER_ARG=0.1` to only keep 10% of traces — enough to spot problems without wasting storage.

### Verified working

Tested with Jaeger all-in-one v1.57 via Docker. `miningcore` service registers itself in Jaeger, `stratum.request` spans visible with method and connection_id tags. Uses gRPC on port 4317.

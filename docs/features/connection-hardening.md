# Feature: Stratum Connection Hardening

**Date**: June 2026  
**Area**: Stratum / Security

## What it does

Three layers of protection against connection floods, junk senders, and repeat offenders. All active by default — no config changes needed unless you want to tune thresholds.

## Why this matters

A single script can open thousands of TCP connections to a stratum port. Without protection, each connection spawns a `Task`, hits the JSON parser, hits the auth handler, and gets logged. That's thread pool exhaustion, disk I/O saturation from log writes, and potentially `validateaddress` RPC calls hammering the coin daemon.

Original miningcore had a flat 3-minute ban for junk/noise. No rate limiting before accept, no escalation for repeat offenders, no control over the TCP backlog.

## How it works

### Layer 1: Pre-accept throttle

Before the socket is even accepted, the IP is checked against a per-second sliding window. Max 5 connections per second per IP. Loopback is exempt.

Excess connections get `socket.Close()` — they never reach the thread pool, never allocate a `StratumConnection`, never get logged.

### Layer 2: Ban escalation

Repeat offenders get progressively longer bans. Tracked per IP across the process lifetime:

| Strike | Duration |
|--------|----------|
| 1 | 10 seconds |
| 2 | 60 seconds |
| 3 | 5 minutes |
| 4 | 30 minutes |
| 5 | 2 hours |
| 6 | 24 hours |
| 7 | Permanent |

Applied to: junk JSON, failed SSL handshake, and any unhandled connection error. The existing `loginFailureBanTimeout` for unauthorized workers uses a flat short ban (not escalated) to avoid locking out miners with typos.

### Layer 3: TCP backlog

The kernel's TCP accept backlog is now configurable per stratum port. When the backlog fills, the kernel rejects new SYNs at the TCP level — the miningcore process never sees them.

```json
"ports": {
    "3042": {
        "listenAddress": "0.0.0.0",
        "difficulty": 8,
        "tcpBacklog": 16,
        "varDiff": { ... }
    }
}
```

| Setting | Effect |
|---------|--------|
| `tcpBacklog` omitted | Defaults to 32 |
| `tcpBacklog: 8` | Aggressive — rejects overflow fast (small pools, low attack surface) |
| `tcpBacklog: 128` | Permissive — accept more queued connections (large pools) |

## What it does NOT do

- Does not rate-limit authenticated/authorized miners — only pre-auth connections
- Does not affect stratum share/job throughput
- Does not drop legit miners during ban escalation since each strike requires a new offense

## Files

| File | Purpose |
|------|---------|
| `src/Miningcore/Banning/Abstractions.cs` | `IBanManager` interface — `ThrottleConnect`, `EscalateBan` |
| `src/Miningcore/Banning/IntegratedBanManager.cs` | Sliding window throttle, escalation state, in-memory ban cache |
| `src/Miningcore/Stratum/StratumServer.cs` | Pre-accept throttle check in `Listen`, escalate in `OnConnectionError` |
| `src/Miningcore/Configuration/ClusterConfig.cs` | `PoolEndpoint.TcpBacklog` config field |

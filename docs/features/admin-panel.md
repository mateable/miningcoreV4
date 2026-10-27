# Feature: Built-in Web Admin Panel

**Date**: June 2026  
**Area**: API / Dashboard

## What it is

A password-protected web dashboard served directly by miningcore. No separate server, no reverse proxy, no SSH needed. Open a browser, log in, and see your pool stats, blocks, miners, payments, and server health.

## How to enable

Add this to `config.json`:

```json
{
  "adminPanel": {
    "enabled": true,
    "password": "your-password-here",
    "sessionTimeout": 3600,
    "maxLoginAttempts": 5,
    "loginBanDuration": 300
  }
}
```

Restart miningcore, then go to `http://your-server:4000/admin/login`.

## What it shows

| Tab | What you see |
|-----|-------------|
| **Overview** | Pool hashrate, connected miners, shares/sec, network difficulty, block height |
| **Pools** | All configured pools with individual stats |
| **Miners** | Search any miner address across all pools |
| **Blocks** | Recent confirmed/pending/orphaned blocks with effort and miner |
| **Settings** | GC stats, Force GC button |

All data comes from the existing API — the dashboard is just a friendly face on top of what was already there.

## Security

- **HMAC-signed session cookies** — not plaintext tokens
- **Login attempt tracking** — too many wrong passwords from one IP = temporary ban
- **Configurable** — timeout duration, max attempts, ban length all in config
- **IP whitelist still works** — if your IP is in `AdminIpWhitelist`, you bypass login entirely
- **No password = no auth** — leave `password` empty and it's open to anyone on the whitelist

## Expanding it

The dashboard is built with a modular tab system. To add a new tab in the future:

1. Add a button in the HTML `nav`
2. Add a render function in the `loaders` object  
3. Add the backend endpoint if it doesn't already exist

No framework, no build step, no npm — just vanilla HTML + JS.

## Architecture

```
Browser [https://pool.example.com:4000/admin]
   │
   ├── GET  /admin/login     → Login page (HTML, no auth required)
   ├── POST /admin/login     → Verify password, set cookie
   ├── GET  /admin/           → Dashboard (HTML, requires cookie)
   ├── GET  /admin/logout     → Delete cookie, redirect to login
   │
   ├── GET  /api/admin/*      → Existing admin API (requires cookie)
   ├── GET  /api/pools        → Pool data (public, no cookie needed)
   └── WS   /notifications    → Real-time events (public)
```

## Config reference

| Field | Default | Description |
|-------|---------|-------------|
| `enabled` | `false` | Turn the admin panel on/off |
| `password` | `""` | Login password. Empty = no password auth needed |
| `sessionTimeout` | `3600` | Minutes before auto-logout |
| `maxLoginAttempts` | `5` | Failed attempts before temporary IP ban |
| `loginBanDuration` | `300` | Ban duration in seconds |

# Admin Panel

MCCE ships a built-in web admin dashboard, separate from the public `/api`. This
document covers what it does, how to turn it on, and the API surface behind it.

## Enabling it

In your cluster config:

```json
"adminPanel": {
    "enabled": true,
    "port": 8080,
    "password": "a strong password here",
    "sessionTimeout": 3600,
    "maxLoginAttempts": 5,
    "loginBanDuration": 300,
    "ipWhitelist": ["1.2.3.4"]
}
```

- `port` must differ from the main API port - the admin panel runs as its own
  Kestrel app on its own port, and proxies to the real API internally.
- `password` is required for login. Leave it unset only if `ipWhitelist` is
  also set and you're relying on network-level restriction instead.
- `ipWhitelist` (optional) is an exact-address allowlist, same matching
  behavior as `api.adminIpWhitelist` - no CIDR ranges. Checked before the
  login form, so a non-whitelisted IP never even sees the password prompt.
- `sessionTimeout` / `maxLoginAttempts` / `loginBanDuration` control the
  HMAC-signed session cookie lifetime and brute-force throttling.

Then visit `http://<host>:<port>/admin`.

## Changing the password from the UI

Settings tab → "Admin Panel Password". This only changes the in-memory value
for the running process - it does **not** rewrite `config.json`, so it
reverts to the configured password on restart. If you want it to stick,
update `config.json` directly.

## Feature list

| Tab | What it shows / does |
|---|---|
| Overview | Per-pool hashrate, network stats, recent blocks across all pools |
| Pools | Per-pool hashrate/difficulty/height/ports detail |
| Miners | Live connected-miner table per pool (address, worker, IP, difficulty, valid/invalid shares, connected-since) with a one-click "Ban address" action; miner search by address across pools |
| Blocks | Last 15 blocks per pool with status/effort/reward |
| Payments | Last 15 payments per pool |
| Health | Daemon-online status per pool + process GC stats |
| Bans | Active IP bans (from the integrated ban manager) with one-click unban |
| Farms | Large Farm / Botnet Detection (yiimp parity) - flags any payout address receiving hashrate from more distinct IPs than an adjustable threshold (default 10, remembered per-browser). Detection only, never auto-bans; one-click "Ban address" on a flag |
| Alerts | Pool-oriented alert rules (yiimp parity: CoinTriggers, redesigned) - add a rule like "pool X poolHashrate < 1000" or "pool X minutesSinceBlockHeightChange > 30", see it show up here (and a sidebar badge) when triggered, and get an email via the existing notification pipeline on every triggered/cleared transition. Never executes commands - purely a visible/email alert, see Known limitations |
| Logs | Last ~300 lines of application log output, in-memory, no `logFile`/SSH needed |
| Audit Log | Last 200 admin actions (who/what/when) - bans, payouts, balance adjustments, password changes |
| Settings | Force GC, Run Payout Cycle Now, manual balance adjustment, per-pool minimum-confirmations override, change admin password |

Overview and Pools also group multiple pools of the same coin together and
show a Solo/PPLNS badge per pool (from `paymentProcessing.payoutScheme`) -
purely cosmetic, no behavior change.

## API reference

Everything below lives on the real API (not the admin-panel's own Kestrel
process) under `/api/admin/*`, and is IP-restricted by `api.adminIpWhitelist`
independently of the admin panel's own auth. The admin panel's dashboard
reaches these through its generic `/api/admin/{**rest}` proxy, so anything
added here is automatically usable from the dashboard without touching the
proxy plumbing.

| Endpoint | Method | Notes |
|---|---|---|
| `/api/admin/pools/{poolId}/connections` | GET | Live Stratum connections for one pool |
| `/api/admin/bans` | GET | Active IP bans |
| `/api/admin/bans/unban` | POST (form: `address`) | Lifts an IP ban immediately |
| `/api/admin/pools/{poolId}/miner-bans` | GET | Addresses banned from this pool |
| `/api/admin/pools/{poolId}/miner-bans/ban` | POST (form: `address`) | Bans an address from this pool. Enforcement is wired into `BitcoinPool.OnAuthorizeAsync` only — see the caveat in `Banning/MinerAddressBanManager.cs` |
| `/api/admin/pools/{poolId}/miner-bans/unban` | POST (form: `address`) | Lifts a miner-address ban |
| `/api/admin/logs` | GET (`?count=`) | Recent in-memory log lines |
| `/api/admin/auditlog` | GET (`?count=`) | Recent admin actions |
| `/api/admin/payouts/run` | POST | Triggers an immediate payout cycle (same code path as the periodic timer, serialized against it so they can't overlap) |
| `/api/admin/pools/{poolId}/miners/{address}/balance/adjust` | POST (form: `amount`, `reason`) | Credits/debits a miner's pending balance; `reason` is required and goes in the audit log |
| `/api/admin/stats/gc` | GET | GC generation counts + managed memory |
| `/api/admin/forcegc` | POST | Forces a Gen2 GC |
| `/api/admin/payment/processing/{enable,disable}` | GET | Global payment processing toggle (also per-pool: `/payment/processing/{poolId}/{enable,disable}`) |
| `/api/admin/pools/{poolId}/miners/{address}/getbalance` | GET | Raw balance lookup |
| `/api/admin/pools/{poolId}/miners/{address}/settings` | GET/POST | Miner payout threshold/address settings |
| `/api/admin/pools/{poolId}/min-confirmations` | GET/POST (form: `value`) | Per-pool override for the *displayed* confirmation-progress denominator on immature blocks. Leave `value` blank to reset to the configured default. Does not change real payout eligibility - see the override class's own doc comment (`Payments/MinConfirmationsOverride.cs`) |
| `/api/admin/pools/{poolId}/rpc` | POST (form: `method`, `params` as a JSON array string) | Runs any RPC command against this pool's daemon, like `bitcoin-cli`. **No command allowlist** - see Known limitations. Bitcoin-family pools only (`PoolBase.ExecuteDaemonRpcAsync` default throws `NotSupportedException` for other pool types) |
| `/api/admin/alerts/rules` | GET | Lists all alert rules |
| `/api/admin/alerts/rules` | POST (form: `poolId`, `metric`, `operator`, `threshold`, `description`) | Adds a rule. `metric` is one of `poolHashrate`, `connectedMiners`, `networkHashrate`, `networkDifficulty`, `minutesSinceBlockHeightChange`; `operator` is one of `<`, `>`, `<=`, `>=`, `==` |
| `/api/admin/alerts/rules/{id}/{enable,disable}` | POST | Toggles a rule without deleting it |
| `/api/admin/alerts/rules/{id}/delete` | POST | Removes a rule permanently |
| `/api/admin/alerts/active` | GET | Currently-triggered alerts with their live current value - also what the dashboard polls every 30s for the sidebar badge |

Large Farm/Botnet Detection has **no dedicated backend endpoint** - it's
computed entirely client-side in the dashboard from data the Miners tab
already fetches (`/api/admin/pools/{poolId}/connections`), grouping by
`(pool, address)` and counting distinct `remoteIp` values.

Alert rules ARE evaluated server-side and independently of the dashboard
being open - `Mining/StatsRecorder.cs` calls `AlertRuleManager.Evaluate()`
on its own regular stats cycle (not tied to anyone polling `/alerts/active`)
and publishes the existing `AdminNotification` message bus event on every
triggered/cleared transition, which `NotificationService` already turns
into a real email if `notifications.enabled` and `notifications.admin.enabled`
are both true in `config.json`. Rules themselves persist to
`alert_rules.json` next to the binary (survives restarts, unlike most of
the rest of the admin panel's in-memory settings).

## Known limitations

- **The raw daemon RPC console has no command allowlist.** It runs exactly
  what you type against the real daemon - this includes wallet-draining or
  destructive commands if your daemon exposes them. The *only* access
  control is the same IP-allowlist/auth that gates the rest of `/api/admin`
  - treat that allowlist as the real security boundary, and never expose
  the admin panel to an untrusted network.
- **Raw daemon RPC console and miner-address banning only work on
  Bitcoin-family pools** - other pool types' `ExecuteDaemonRpcAsync`/
  `OnAuthorizeAsync` would need their own implementation (not done here -
  this fork's actual coins are all Bitcoin-family, so this is full coverage
  for this deployment specifically).
- **Password changes, IP-whitelist changes, and the per-pool minimum-
  confirmations override made through the API are not persisted to
  `config.json`.** They apply to the running process only.
- **Coin/pool add/edit/remove** is not a dedicated admin-panel feature here -
  it's already handled by the existing `ConfigHotReloadService`: edit
  `config.json` and save it, and pools are started/stopped/restarted
  automatically within ~500ms, no admin-panel UI needed for it.
- The audit log and in-memory log viewer are both in-memory ring buffers -
  cleared on restart, same tradeoff as the rest of the admin panel's
  in-process state.
- **Alert rules fire into the existing email pipeline, but won't actually
  send anything unless SMTP is already configured in `config.json`**
  (`notifications.enabled` and `notifications.admin.enabled`, plus real
  `notifications.email` host/user/password). There's no dedicated SMTP
  settings UI here - same rationale as skipping a coin/pool UI: it's
  already editable via `config.json` + the existing hot-reload, so a
  second way to set the same values wasn't worth building.
- **Alert rule metrics only cover what `StatsRecorder` already tracks**
  (pool hashrate, connected miners, network hashrate/difficulty, minutes
  since block height changed) - no payout-balance or share-rejection-rate
  metrics yet, since those aren't computed on the same cycle today.

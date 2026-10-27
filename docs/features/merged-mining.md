# Feature: Merged Mining (AuxPoW)

**Date**: June 2026  
**Area**: Blockchain / Job Management  
**Author**: Soosho  
**Status**: 🔒 **Private feature — available for licensing**  
**Private repo**: [soosho/miningcore-merged](https://github.com/soosho/miningcore-merged) (private)  
**Contact**: soosho @ GitHub

> ⚠️ **This code is NOT included in the public `soosho/miningcore` repository.**
> The merged mining feature is maintained in a private repository and is available
> for licensing. Contact the author for access, pricing, or integration support.
> 
> This document describes what the feature does and how it works. The actual
> implementation is in the private repo linked above.

---

<img width="867" height="403" alt="photo_2026-06-10_00-07-14" src="https://github.com/user-attachments/assets/3a51fd2b-9b5d-4a73-b060-6224027e70ba" />

---

## What it does

Enables **merged mining** where miners point their hashrate at one parent chain (e.g. Litecoin) and the pool automatically submits blocks on **both** the parent chain and one or more auxiliary chains (e.g. Dogecoin) — without the miner knowing or doing anything extra.

### Why this matters

- **Miners earn more**: They mine LTC normally and get DOGE block rewards on top — no extra work, no extra power.
- **Aux chains get more security**: Dogecoin's hashrate is effectively backed by Litecoin's Scrypt miners.
- **Pool operators attract more hashrate**: Offering merged mining is a competitive advantage for LTC pools.

---

## How AuxPoW works (high level)

```

Miner only mines LTC (Scrypt)
         │
         ▼
   LTC getblocktemplate ───→ LTC block template
   DOGE getauxblock    ───→ DOGE aux block (hash + target + chainid)
         │                        │
         └────────┬───────────────┘
                  │
                  ▼
         Parent Coinbase (LTC)
         ├── normal coinbase data
         ├── aux merkle root hash          ← DOGE hash embedded here
         │    magic: fabe6d6d
         │    format: "fabe6d6d" + merkle_hash + aux_size
         └── goes into LTC merkle root
                  │
                  ▼
         LTC block header (includes merkle root)
                  │
                  ▼
         AuxPoW structure (when DOGE found):
         ├── Aux chain coinbase TX
         ├── Aux merkle branch hashes[]
         ├── Aux merkle branch index
         ├── Parent block merkle branch hashes[]
         ├── Parent merkle branch index
         └── Parent block header (80 bytes)
```

### Key insight

Dogecoin daemon has a `getauxblock` RPC that serves dual purpose:

- **Without arguments**: Returns `{hash, target, chainid}` — creates a new aux block template
- **With arguments** `getauxblock <hash> <auxpow>`: Submits a solved AuxPoW for validation. Returns `true` on success.

The aux chain's block hash is embedded in the LTC coinbase as a merkle root with `fabe6d6d` magic bytes. When a share solves the LTC block, the pool:

1. Extracts the LTC block header and coinbase transaction from the raw block hex
2. Builds a properly serialized `CAuxPow` binary (matching Dogecoin Core's `SERIALIZE_METHODS` order)
3. Submits it via `getauxblock <hash> <auxpow>` on the DOGE daemon
4. The DOGE daemon deserializes the CAuxPow, validates it, and accepts the block

### CAuxPow Serialization Order

The binary format must match Dogecoin Core's `src/auxpow.h` exactly:

| # | Field | Type | Description |
|---|-------|------|-------------|
| 1 | `coinbaseTx` | CTransaction | Parent chain's coinbase transaction (raw bytes) |
| 2 | `hashBlock` | uint256 | SHA256d of parent block header (32 bytes) |
| 3 | `vMerkleBranch` | vector\<uint256\> | Merkle branch from coinbase to parent block root |
| 4 | `nIndex` | int32 | Coinbase transaction index in block (always 0) |
| 5 | `vChainMerkleBranch` | vector\<uint256\> | Chain merkle branch (empty for aux daemon) |
| 6 | `nChainIndex` | int32 | Chain index (always 0) |
| 7 | `parentBlock` | CPureBlockHeader | Parent block header (80 bytes, raw) |

This was reverse-engineered from the Dogecoin Core source (`src/rpc/mining.cpp` → `AuxMiningSubmitBlock` → `CDataStream ss >> CAuxPow pow`). Incorrect ordering produces `CDataStream::read(): end of data` errors from the daemon.

---

## Workflow / Step-by-step flow

```
                  ┌──────────────────┐
                  │   LTC Daemon     │
                  │   (127.0.0.1:9332)│
                  └────────┬─────────┘
                           │ getblocktemplate
                           ▼
                  ┌──────────────────┐     ┌──────────────────┐
                  │  LTC Job Manager │     │  DOGE Daemon     │
                  │  (parent chain)  │     │  (127.0.0.1:22555)│
                  └────────┬─────────┘     └────────┬─────────┘
                           │                        │ getauxblock
                           │                        ▼
                           │               ┌──────────────────┐
                           │               │ DOGE Aux Data    │
                           │               │ {hash, target,   │
                           │               │  chainid}        │
                           │               └────────┬─────────┘
                           │                        │
                           └────────┬───────────────┘
                                    │
                                    ▼
                           ┌──────────────────────┐
                           │  Coinbase (LTC)       │
                           │  - scriptSig includes │
                           │    fabe6d6d + merkle  │
                           └──────────┬───────────┘
                                      │
                                      ▼
                           ┌──────────────────────┐
                           │  Stratum (port 3042)  │
                           │  Miners connect       │
                           └──────────┬───────────┘
                                      │ share submitted
                                      ▼
                           ┌──────────────────────┐
                           │  Share check:         │
                           │  1. ≥ LTC diff?       │→ submitblock to LTC
                           │  2. ≥ DOGE diff?      │→ getauxblock to DOGE
                           └──────────────────────┘
```

---

---

## Architecture

```
miningcore process
├── bitcoin pool    (port 3040) — standalone, no merged mining
├── litecoin pool   (port 3042) — MergedBitcoinJobManager
│       ├── LTC daemon (9332): getblocktemplate
│       └── DOGE daemon (22555): getauxblock
└── dogecoin pool   (port 3043) — standalone, for direct DOGE miners
```

The DOGE pool stays as a normal standalone pool. Merged mining happens inside the LTC pool — it reaches out to the DOGE daemon via `getauxblock`, embeds the aux data in the LTC coinbase, and submits aux blocks. The DOGE pool handles its own stratum port for miners who want to mine DOGE directly.

---

## How it works

### Step-by-step flow

**1. Job creation**
- LTC pool fetches block template from LTC daemon via `getblocktemplate`
- LTC pool also calls `getauxblock` on DOGE daemon — returns `{hash, target, chainid}`
- The DOGE aux block hash is embedded in the LTC coinbase transaction using the `fabe6d6d` magic marker
- This makes the LTC merkle root dependent on the DOGE block hash

**2. Mining**
- Miners connect to the LTC stratum port (3042) as usual
- They receive normal LTC stratum jobs — nothing about DOGE is exposed
- Mining happens with standard Scrypt ASICs

**3. Block found on LTC**
- Share is submitted, checked against LTC difficulty
- If it solves the LTC block → submit to LTC daemon via `submitblock`
- On success → retrieve the raw block hex via `getblock <hash> 0`
- Submit the same block to DOGE via `getauxblock <hash> <ltc_block_hex>`
- The DOGE daemon validates the auxpow (parent header + coinbase) and accepts or rejects

**4. Block found on DOGE directly**
- Miners on the DOGE stratum port (3043) mine DOGE directly
- Blocks found go through normal `submitblock` to the DOGE daemon
- Payouts handled by the DOGE pool's payment processor

**5. Database recording**
- LTC blocks: `poolid='litecoin'`, `source='pool'`
- DOGE blocks from merged mining: `poolid='dogecoin'`, `source='merged'`
- DOGE blocks from direct mining: `poolid='dogecoin'`, `source='pool'`
- No database migration needed — everything uses existing columns

**6. Payments**
- The DOGE payout handler reads `blocks` where `poolid='dogecoin'`
- Both merged mining blocks and direct mining blocks are processed
- Miners get DOGE credits based on their share contributions
- Payouts go to miners according to the PPLNS scheme

### Coinbase format

The aux merkle root is embedded in the parent chain coinbase with the `fabe6d6d` magic prefix:

```
scriptSig = <block_height> <flags> <timestamp> 08 fabe6d6d <merkle_root> <count> 00000000000000
```

This follows the Bitcoin merged mining specification.

---

## Configuration

Litecoin pool's `extra` section in `config.json`:

```json
{
  "id": "litecoin",
  "coin": "litecoin",
  "addressType": "Legacy",
  "address": "LLU1sU4gzjdg7HSbuCKNZYjHcyk1zhNh4s",
  "rewardRecipients": [
    {
      "address": "LRoYKFHdpepX2qC2eK9Yh2NNRnNG4FMir4",
      "percentage": 1.0
    }
  ],
  "extra": {
    "enabled": true,
    "auxDaemons": [
      {
        "host": "127.0.0.1",
        "port": 22555,
        "user": "pool",
        "password": "Z08ukBeWtwXHepk46G",
        "chainId": 98
      }
    ]
  },
  "daemons": [
    {
      "host": "127.0.0.1",
      "port": 9332,
      "user": "pool",
      "password": "Z08ukBeWtwXHepk46G"
    }
  ],
  "ports": {
    "3042": {
      "listenAddress": "0.0.0.0",
      "difficulty": 1024,
      "name": "Scrypt ASIC Mining"
    }
  },
  "paymentProcessing": {
    "enabled": true,
    "minimumPayment": 0.5,
    "payoutScheme": "PPLNS",
    "payoutSchemeConfig": { "factor": 2.0 }
  }
}
```

| Field | Description |
|-------|-------------|
| `extra.enabled` | Enables merged mining |
| `extra.auxDaemons[].host` | Aux chain daemon hostname |
| `extra.auxDaemons[].port` | Aux chain daemon RPC port |
| `extra.auxDaemons[].user` | RPC username |
| `extra.auxDaemons[].password` | RPC password |
| `extra.auxDaemons[].chainId` | Chain ID from `getauxblock` RPC (DOGE mainnet = 98) |

The aux daemon credentials are stripped from the API — they're only in `config.json`.

A full working example is at `examples/merged_mining_litecoin_dogecoin.json`.

---

## API

### Pool listing

`GET /api/pools` shows 2 pools — DOGE is hidden because its data is merged into LTC:

```
bitcoin   — standalone
litecoin  — with mergedMining section
```

### Merged pool detail

`GET /api/pools/litecoin` includes aux chain data under `mergedMining`:

```json
{
  "pool": {
    "id": "litecoin",
    "mergedMining": {
      "enabled": true,
      "auxChains": [{
        "poolId": "dogecoin",
        "chainId": 98,
        "address": "D9dUKzSF6GnHbEX7Yppb...",
        "networkStats": {
          "blockHeight": 6241293,
          "networkDifficulty": 35913521.66,
          "networkHashrate": 2.12e15
        },
        "totalConfirmedBlocks": 0
      }]
    },
    "totalBlocks": 5,
    "totalPaid": 15002.3
  }
}
```

No host/port credentials are exposed in the API.

### Aggregated endpoints

These endpoints union data from both the parent pool and its aux chains:

| Endpoint | What it returns |
|----------|----------------|
| `GET /api/pools/litecoin` | Combined block totals, payments, pool stats |
| `GET /api/pools/litecoin/blocks` | LTC + DOGE blocks, sorted by time |
| `GET /api/pools/litecoin/payments` | LTC + DOGE payments, sorted by time |
| `GET /api/pools/litecoin/miners/{addr}` | Miner stats from LTC pool only |
| `GET /api/pools/litecoin/miners/{addr}` | Miner stats — includes `auxAddress` if set |

---

## Miner Aux Address (DOGE payout attribution)

When a miner finds a block on the parent chain (LTC), the pool submits the aux block (DOGE) to the Dogecoin daemon. To credit the aux block reward to the miner's **Dogecoin address** (rather than their Litecoin address), miners can set their aux chain address via the API or web UI.

### How it works

1. Miner mines on the LTC pool as usual — username = LTC address, password = `d=X;...`
2. Miner sets their DOGE address via API or web UI (IP-verified for security)
3. The mapping is stored in the `miner_aux_address` database table
4. When a block is found, `MergedBitcoinJobManager` looks up the miner's DOGE address from `miner_aux_address` (caches in-memory for performance)
5. The aux share is attributed to the miner's DOGE address in the `blocks` table
6. The DOGE payout handler credits the miner's DOGE address for the block reward

### API endpoints

| Endpoint | Auth | Description |
|----------|------|-------------|
| `GET /api/pools/{poolId}/miners/{address}/aux-address` | Public | Read the miner's aux address |
| `POST /api/pools/{poolId}/miners/{address}/aux-address` | IP verified | Set/update aux address |

**POST request body:**
```json
{
  "ipAddress": "123.45.67.89",
  "auxAddress": "D9dUKzSF6GnHbEX7YppbtHSt1veq5LKGSr"
}
```

The `ipAddress` must match one of the IP addresses that recently submitted shares for the given LTC address — this prevents unauthorized address changes.

**POST response:**
```json
{
  "poolId": "litecoin",
  "address": "LLU1sU4gzjdg7HSbuCKNZYjHcyk1zhNh4s",
  "auxAddress": "D9dUKzSF6GnHbEX7YppbtHSt1veq5LKGSr",
  "created": "2026-06-09T12:00:00Z",
  "updated": "2026-06-09T12:00:00Z"
}
```

### Miner stats response

`GET /api/pools/litecoin/miners/{addr}` includes an `auxAddress` field:

```json
{
  "pendingShares": 1234.56,
  "pendingBalance": 0.0123,
  "totalPaid": 5.678,
  "auxAddress": "D9dUKzSF6GnHbEX7YppbtHSt1veq5LKGSr"
}
```

The `auxAddress` is `null` if the miner hasn't set one.

### Miner settings response

`GET /api/pools/{poolId}/miners/{address}/settings` also includes `auxAddress`:

```json
{
  "paymentThreshold": 1.0,
  "auxAddress": "D9dUKzSF6GnHbEX7YppbtHSt1veq5LKGSr"
}
```

### Web UI

The pool-ui miner dashboard (`/miner/{address}`) displays the current aux address and provides a collapsible form to set or update it. The form requires the miner's DOGE address and their mining rig IP for ownership verification.

---

## Database

The aux address mapping requires a new table. Run `add_miner_aux_address.sql` or add it to `createdb.sql`:

| Table | How merged mining uses it |
|-------|--------------------------|
| `miner_aux_address` | `poolid` + `address` (LTC) → `auxaddress` (DOGE), primary key on `(poolid, address)` |
| `blocks` | `poolid='dogecoin'`, `source='merged'` for aux blocks |
| `shares` | Normal flow — shares go to the LTC pool |
| `payments` | `poolid='dogecoin'` — DOGE payout handler reads these |
| `balance_changes` | `poolid='dogecoin'` — miners credited from DOGE payouts |

The DOGE payout handler processes merged mining blocks the same as direct mining blocks — it reads confirmed blocks from the `blocks` table, calculates miner share contributions, and issues payments.

---

## Error handling

- **DOGE daemon down** — LTC mining continues without merged mining. A warning is logged. When the DOGE daemon comes back, merged mining resumes automatically on the next LTC block template.
- **Stale aux data** — The aux block data is re-fetched with each LTC block template. If the aux chain advances while a job is active, the aux block won't be stale because it's tied to the LTC block that was just solved.
- **Aux submission rejected** — Logged as a warning. LTC mining continues. Common causes: DOGE daemon saw a different chain tip, or the parent header doesn't meet DOGE's auxpow validation.
- **`CDataStream::read(): end of data`** — Indicates incorrect CAuxPow binary serialization order. The field order must match Dogecoin Core's `SERIALIZE_METHODS` exactly (see CAuxPow Serialization Order section above).
- **`Unable to cast 'System.Boolean' to 'JToken'`** — The `getauxblock <hash> <auxpow>` submission returns a boolean (`true`/`false`), not a JSON object. The RPC client must use `<object>` generic type, not `<JToken>`, when calling this method.

## Verified Working ✅

End-to-end tested on regtest (June 2026):

```
[I] Parent block 212 accepted — attempting aux chain submission(s)
[I] Aux block submitted! chainId=98 hash=0175c311... height=151
```

---

## Requirements

- Dogecoin Core with `getauxblock` RPC support
- Both LTC and DOGE daemons fully synced
- LTC pool configured with aux daemon credentials in `extra` section

---

## Files

| File | Type | Purpose |
|------|------|---------|
| `src/Miningcore/Blockchain/Bitcoin/DaemonResponses/AuxBlock.cs` | New | `getauxblock` RPC response model |
| `src/Miningcore/Blockchain/Bitcoin/Configuration/MergedMiningExtraConfig.cs` | New | Per-pool merged mining config |
| `src/Miningcore/Blockchain/Bitcoin/AuxPowUtils.cs` | New | Aux merkle tree, coinbase script helpers, and **CAuxPow binary serializer** (reverse-engineered from Dogecoin Core source) |
| `src/Miningcore/Blockchain/Bitcoin/MergedBitcoinJobManager.cs` | New | Dual RPC job manager (LTC + DOGE); aux address lookup + in-memory cache; `submitauxblock` RPC |
| `src/Miningcore/Blockchain/Bitcoin/BitcoinJob.cs` | Modified | `SetAuxBlocks()` + `AppendCoinbaseFinal` hook |
| `src/Miningcore/Blockchain/Bitcoin/BitcoinPool.cs` | Modified | Resolves `MergedBitcoinJobManager` when merged mining enabled |
| `src/Miningcore/AutofacModule.cs` | Modified | Register `MergedBitcoinJobManager` in DI |
| `src/Miningcore/AutoMapperProfile.cs` | Modified | Added entity↔model mappings for `MinerAuxAddress` |
| `src/Miningcore/Api/Responses/GetPoolsResponse.cs` | Modified | `MergedMiningStats` and `MergedMiningAuxChain` models |
| `src/Miningcore/Api/Extensions/MiningPoolExtensions.cs` | Modified | Populate `mergedMining` from extra config |
| `src/Miningcore/Api/ApiEndpoints.cs` | Modified | Union aux chain data in all pool endpoints; GET/POST aux-address |
| `src/Miningcore/Api/Responses/GetMinerStatsResponse.cs` | Modified | Added `AuxAddress` field |
| `src/Miningcore/Api/Responses/GetMinerSettingsResponse.cs` | Modified | Added `AuxAddress` field |
| `src/Miningcore/Api/Responses/MinerAuxAddress.cs` | New | Aux address response DTO |
| `src/Miningcore/Api/Requests/UpdateMinerAuxAddressRequest.cs` | New | Aux address update request DTO |
| `src/Miningcore/Persistence/Model/MinerAuxAddress.cs` | New | Domain model |
| `src/Miningcore/Persistence/Postgres/Entities/MinerAuxAddress.cs` | New | DB entity |
| `src/Miningcore/Persistence/Repositories/IMinerAuxAddressRepository.cs` | New | Repository interface |
| `src/Miningcore/Persistence/Postgres/Repositories/MinerAuxAddressRepository.cs` | New | Repository implementation |
| `src/Miningcore/Persistence/Postgres/Scripts/add_miner_aux_address.sql` | New | Migration script |
| `src/Miningcore/Persistence/Postgres/Scripts/createdb.sql` | Modified | Added `miner_aux_address` table |
| `examples/merged_mining_litecoin_dogecoin.json` | New | Working example config |
| `docs/features/merged-mining.md` | New | This document |<｜end▁of▁thinking｜>

<｜｜DSML｜｜parameter name="filePath" string="true">/root/miningcore/docs/features/merged-mining.md

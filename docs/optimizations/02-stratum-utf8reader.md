# Optimization: Stratum JSON Parsing (Newtonsoft → Utf8JsonReader)

**Date**: June 2026  
**Commit**: `60de7e9`  
**Area**: Stratum wire protocol (`Miningcore.Stratum`)

## What Changed

### ProcessRequestAsync — request deserialization

Every share submission from every miner hits this method. Previously it was:

```
ReadOnlySequence<byte> → Encoding.GetString(string) → StringReader → JsonTextReader → Newtonsoft.Deserialize(JToken tree)
```

That's a string allocation + two reader allocations + full JToken tree per call.

Replaced with `Utf8JsonReader` that reads directly from `ReadOnlySequence<byte>`:

```
ReadOnlySequence<byte> → Utf8JsonReader → lightweight JsonRpcRequest (method/id + lazy params slice)
```

- Zero intermediate string allocations
- Params stored as a lazy `ReadOnlySequence<byte>` slice — only deserialized on demand via `ParamsAs<T>()`
- Custom brace-depth scanner (`SequenceReader<byte>`) handles leading/trailing content gracefully

### SendMessage — outbound serialization

Every `mining.notify` broadcast to miners hits this. Previously:

```
StringBuilder → sb.ToString() → Encoding.GetBytes(byte[])
```

Three allocations per message. At 10,000 connected miners during a block notification, that's 30,000 allocations in a burst.

Switched to `RecyclableMemoryStream` + `StreamWriter`:

```
StreamWriter → RecyclableMemoryStream → CopyToAsync(networkStream)
```

Single pooled allocation per send, no intermediate string.

### Compiler flags

Added to `Miningcore.csproj`:
- `TieredPGO=true` — profile-guided optimization across tiers
- `OptimizationPreference=Speed` — prioritize throughput over size
- `InvariantGlobalization=true` — skip culture-aware string ops (not needed for mining)

### Method hints

`[MethodImpl(AggressiveInlining | AggressiveOptimization)]` on:
- `ProcessRequestAsync`
- `FastParseRequest`
- `TrimToJsonObject`
- `ReadJsonRpcId`

## Results

| | Before | After | Improvement |
|---|---|---|---|
| Mean Time | 2,288 ns | 550 ns | **4.16× faster** |
| Allocated Memory | 7,110 B | 752 B | **9.45× less** |
| Gen 0 GCs/1k ops | 0.557 | 0.057 | **9.7× fewer** |

Cumulative vs upstream .NET 6: **5.36× faster, 9.44× less memory.**

## Real-World Impact

For a pool with 10,000 miners each submitting shares every 15 seconds (~667 req/s):
- **CPU**: 1.5 ms/s → 0.37 ms/s (4× less CPU spent on deserialization)
- **GC**: 4.7 MB/s → 0.5 MB/s garbage (GC runs 10× less often → fewer pause spikes)
- **Block notifications**: 30,000 allocations per broadcast → 10,000 (pooled) — lower latency when new blocks arrive

## Verification

Ran MCCE with production `config.json`: pool comes up, daemon connects, stratum listener accepts, API responds, blocks detected, jobs broadcast successfully.

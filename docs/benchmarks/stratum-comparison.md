# Benchmark: Stratum Request Processing

## What's Being Measured

`ProcessRequest_Handle_Valid_Request` — simulates parsing a raw TCP JSON-RPC request from a miner (`mining.authorize` with worker name + password). This is the inner loop for every share submission.

## Comparison Across Versions

| Version | Mean Time | Allocated | Gen 0/1k | vs Upstream |
|---------|-----------|-----------|----------|-------------|
| Upstream .NET 6 | 2,947 ns | 7,100 B | 0.553 | baseline |
| MCCE .NET 8 (pre-optimize) | 2,288 ns | 7,110 B | 0.557 | 1.29× faster |
| MCCE Optimized | **550 ns** | **752 B** | 0.057 | **5.36× faster** |

## Raw Results

### Upstream .NET 6 (Ubuntu 22.04)
```
|                              Method |     Mean |     Error |    StdDev |   Gen0 |   Gen1 | Allocated |
|------------------------------------ |---------:|----------:|----------:|-------:|-------:|----------:|
| ProcessRequest_Handle_Valid_Request | 2.947 us | 0.0584 us | 0.0942 us | 0.5531 | 0.0038 |    7.1 KB |
```

### MCCE .NET 8 — Baseline (Ubuntu 24.04)
```
|                              Method |     Mean |     Error |    StdDev |   Gen0 |   Gen1 | Allocated |
|------------------------------------ |---------:|----------:|----------:|-------:|-------:|----------:|
| ProcessRequest_Handle_Valid_Request | 2.288 us | 0.0453 us | 0.1137 us | 0.5569 |      - |   7.11 KB |
```

### MCCE Optimized — Utf8JsonReader (Windows 11, i7-12700K)
```
|                              Method |     Mean |   Error |  StdDev |   Gen0 | Allocated |
|------------------------------------ |---------:|--------:|--------:|-------:|----------:|
| ProcessRequest_Handle_Valid_Request | 549.9 ns | 2.72 ns | 2.13 ns | 0.0572 |     752 B |
```

## Key Insights

1. **.NET 6 → .NET 8**: 22% improvement from JIT/Dynamic PGO improvements alone — zero code changes
2. **Newtonsoft → Utf8JsonReader**: 4.16× improvement by eliminating string + reader + JToken allocations
3. **Cumulative**: 5.36× faster than the original upstream code with 9.44× less memory allocated

# MateableCoin (MTBC) support: Balloon + Whirlpool

This adds two PoW algorithms, `balloon` and `whirlpool`, so this pool
software can run stratum pools for MateableCoin (MTBC) on either algo.

Built and tested end-to-end against a real, fully synced MTBC node: native
lib builds clean, the C# side builds clean (`dotnet build`, 0 errors), and
real miners (cpuminer-opt, `-a balloon` / `-a whirlpool`) connected to a
pool instance running this code and got thousands of real shares accepted,
correctly recorded in Postgres. Block-found + payout behavior was not
exercised (that needs a real block, or faking one — neither was done here).

## What changed

- `src/Miningcore/Crypto/Hashing/Algorithms/Balloon.cs`,
  `Whirlpool.cs` — new hash algorithm wrapper classes.
- `src/Miningcore/Native/Multihash.cs` — two new `DllImport` declarations.
- `src/Native/libmultihash/balloon.c`, `balloon.h` — native Balloon hash.
  Same source already used in production by MTBC's own YIIMP-based pool —
  dropped in unmodified, since its function signature
  (`void balloon_hash(const char* input, char* output, uint32_t len)`)
  already matches what this repo's native exports expect.
- `src/Native/libmultihash/whirlpool.c`, `whirlpool.h` — native Whirlpool
  hash. This repo already ships the underlying `sph_whirlpool` primitive
  (used internally by other composite algos like x16r) — confirmed
  byte-identical to MateableCoin's own copy of the same file before writing
  this wrapper. `whirlpool.c` is new, but it's a direct mirror of
  MateableCoin's own `src/crypto/whirlpool/whirlpool.c`: four chained passes
  of `sph_whirlpool1` (the final/corrected NESSIE variant). Confirm this
  against your own copy of the MateableCoin node source if you want to
  double check it yourself.
- `src/Native/libmultihash/exports.cpp`, `Makefile` — wiring for both
  natives (`-lssl -lcrypto` added to `LDLIBS` since Balloon needs OpenSSL's
  AES/EVP).
- `src/Miningcore/coins.json` — two new coin templates:
  `mateablecoin-balloon`, `mateablecoin-whirlpool`.
- `src/Miningcore/Stratum/StratumServer.cs` — one-line bugfix, unrelated to
  MTBC specifically (see below).
- `examples/mateablecoin_balloon_whirlpool_pool.json` — example pool config
  for both algos, placeholders for your own daemon/wallet details.

## Building

This repo does **not** auto-build native libs as part of `dotnet build`
(that MSBuild target is commented out in `Miningcore.csproj` on purpose, to
keep day-to-day builds fast). Build the native lib yourself:
```
cd src/Native/libmultihash && make clean && make
```
then copy the resulting `libmultihash.so` into your actual output/publish
directory alongside the other native `.so` files — or run
`src/Miningcore/build-libs-linux.sh <outdir>` to rebuild everything at once
(takes much longer, since it also builds RandomX and friends from source).

Also needs `git fetch --unshallow` if you cloned with `--depth 1` — the
build's GitVersion step walks git history and fails on a shallow clone.

## The one non-obvious gotcha: `getblocktemplate` needs an extra `algo` param

MateableCoin's daemon takes **a second positional parameter** on
`getblocktemplate` naming which algorithm to build a template for (`scrypt`,
`yescrypt`, `whirlpool`, `ghostrider`, `balloon`) — see `src/rpc/mining.cpp`
in the MateableCoin node source to confirm. Without it, the daemon silently
defaults to whatever its current global algo is (observed as `scrypt`
during testing) and you'd get templates for the wrong algorithm with no
error.

This repo already has an extension point for exactly this: `gbtArgs` in the
pool config (`BitcoinPoolConfigExtra.GBTArgs`), which overrides the entire
params array sent with `getblocktemplate`. Already set in the example
config:
```json
"gbtArgs": [{"rules": ["segwit"]}, "balloon"]
```
(or `"whirlpool"` for the other pool). No code changes needed for this part.

## Bonus fix: a real NullReferenceException bug

While testing, every single stratum connection initially failed with:
```
System.NullReferenceException: Object reference not set to an instance of an object.
   at Miningcore.Stratum.StratumServer.Listen(...) in .../StratumServer.cs:line 131
```
Cause: `SetupBanManagement()` (in `Mining/PoolBase.cs`) only assigns
`banManager` when `poolConfig.Banning.Enabled == true` — by design, every
other call site in `StratumServer.cs` correctly null-guards with
`banManager?.`, except this one, which called `banManager.ThrottleConnect(...)`
directly. Any pool with per-pool banning disabled hit this on every
connection attempt (the client just saw `stratum_subscribe timed out` with
no indication why). Fixed here with the missing `?.` — unrelated to MTBC,
just something found along the way.

## If your daemon runs multiple wallets (multiwallet mode)

If your MTBC node serves more than one wallet as separate HTTP paths (e.g.
`/wallet/pool`), set `httpPath` in the daemon config block accordingly. If
your daemon just runs a single default wallet, remove the `httpPath` line.

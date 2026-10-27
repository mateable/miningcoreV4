# Feature: Automatic Share Table Partitioning

**Date**: June 2026  
**Area**: Persistence / Database

## What it does

When miningcore starts a pool, it checks if the `shares` table is partitioned by `poolId`. If it is and no partition exists yet for that pool, it creates one automatically. No manual SQL needed.

## Why this matters

The `shares` table is the biggest table in the database — every accepted share from every miner gets written there. For a medium pool doing 1000 shares/second, that's 86 million rows per day.

Without partitioning, all those rows go into one giant table. Queries scanning for a single pool's stats still touch every other pool's data. With partitioning, PostgreSQL splits the table into one child table per pool. A query for `poolid = 'btc1'` only scans the `shares_btc1` partition — the other pools' data is invisible to the query planner.

**Before**: you had to manually run a SQL script every time you added a pool, or risk the pool crashing on the first share if you forgot.

**After**: add the pool to `config.json`, restart, done. The partition gets created during pool startup.

## How it works

1. Pool starts → `PoolBase.RunAsync()` calls `partitionManager.EnsureSharesPartitionAsync(poolId)`
2. Check if `shares` table is partitioned (look at `relkind = 'p'` in pg_class)
3. If not partitioned (plain table from `createdb.sql`) → skip, nothing changes
4. If partitioned, check if `shares_{poolId}` already exists
5. If not, run `CREATE TABLE shares_{poolId} PARTITION OF shares FOR VALUES IN ('{poolId}')`
6. All subsequent share inserts for that pool route to the correct partition automatically

## What you see in the logs

First start after enabling partitioning:
```
[I] [PartitionManager] Creating shares partition 'shares_btc1' for pool 'btc1'
[I] [PartitionManager] Created shares partition 'shares_btc1'
```

Subsequent starts: nothing (partition already exists).

If using the plain unpartitioned table: nothing (skips silently).

## How to enable it

If you're already using `createdb.sql` (plain table), you need to switch to partitioning first. This requires a one-time migration:

```sql
-- Create the partitioned table
CREATE TABLE shares_new (
    poolid TEXT NOT NULL, ... 
) PARTITION BY LIST (poolid);

-- Copy existing data (if any) into partitions
INSERT INTO shares_new SELECT * FROM shares;

-- Swap tables
ALTER TABLE shares RENAME TO shares_old;
ALTER TABLE shares_new RENAME TO shares;

-- Recreate indexes on the new parent table
CREATE INDEX IDX_SHARES_CREATED ON SHARES(created);
```

Or if you're starting fresh with `createdb_postgresql_11_appendix.sql`, just run that script once and the automation handles partitions from then on.

No config.json changes needed — the partition manager activates automatically based on the table structure it finds.

## Idempotent

Starting miningcore multiple times with the same pool config is safe:
- Partition exists → skipped
- Partition missing → created
- Table not partitioned → no action
- Pool ID has special characters → skipped with a warning

## SQL injection safe

Pool IDs are validated before use in SQL — only alphanumeric + hyphens + underscores allowed. Anything else gets a warning and is skipped.

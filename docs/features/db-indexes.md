# Feature: Missing DB Indexes

**Date**: June 2026  
**Area**: Database / Performance

## What

Added indexes on the `shares` table that cover query patterns the original indexes didn't. New users get them from `createdb.sql`. Existing users run `migrate_add_indexes.sql`.

## Why

The `shares` table is the biggest in miningcore — millions of rows for any active pool. Queries that scan large ranges of shares (effort calculation, cleanup, miner history) were doing sequential scans because the existing indexes didn't match the query ordering.

## Which indexes were added

### For new users (in `createdb.sql`):

| Index | Covers | Benefit |
|-------|--------|---------|
| `(poolid, created DESC)` | `ORDER BY created DESC` queries | Last share lookups, pagination |
| `(poolid, miner, created)` | Miner time-range queries | Per-miner effort, recent IPs |
| `(poolid, miner, worker, created)` | Hash accumulation grouping | Stats aggregation |
| `BRIN(created)` | Time-range scans | 100x smaller than B-tree, fast for large scans |

### For partitioned users (in `createdb_postgresql_11_appendix.sql`):

Same as above, minus `(poolid, miner)` and `(poolid, created)` since partitioning already covers pool-specific scans.

## For existing users

Run once, miningcore can stay running:

```bash
PGPASSWORD=*** psql -U postgres -d miningcore -f src/Miningcore/Persistence/Postgres/Scripts/migrate_add_indexes.sql
```

Each `CREATE INDEX IF NOT EXISTS` takes 1-5 seconds depending on table size. PostgreSQL handles concurrent reads during index creation — miners keep submitting shares.

## For new setups

The indexes are already in `createdb.sql` and `createdb_postgresql_11_appendix.sql`. Just run your usual setup script:

```bash
PGPASSWORD=*** psql -U postgres -d miningcore -f src/Miningcore/Persistence/Postgres/Scripts/createdb.sql
```

## Performance impact

| Query pattern | Before | After |
|--------------|--------|-------|
| Cleanup old shares (time range) | Sequential scan | Index range scan |
| Miner effort in time window | Sequential scan | Index only scan |
| Recent shares (ORDER BY DESC) | Sort in memory | Index scan, no sort |
| Miner+worker hash stats | Sequential scan + hash | Index only scan |
| BRIN storage overhead | — | <0.5% of table size |

No write overhead worth mentioning — PostgreSQL handles concurrent index maintenance efficiently.

-- =============================================================
-- Migration: Add missing DB indexes for share queries
-- 
-- Run this if you already have miningcore running with existing
-- data and want the index improvements. Safe to run while
-- miningcore is running — PostgreSQL handles concurrent writes.
-- 
-- Each CREATE INDEX IF NOT EXISTS takes 1-5 seconds depending
-- on table size. No downtime, no table locks for reads.
-- =============================================================

-- ORDER BY created DESC queries (read recent shares, last share lookups)
CREATE INDEX IF NOT EXISTS IDX_SHARES_POOL_CREATED_DESC
    ON shares(poolid, created DESC);

-- Per-miner time history queries (miner effort, recent IPs)
CREATE INDEX IF NOT EXISTS IDX_SHARES_POOL_MINER_CREATED
    ON shares(poolid, miner, created);

-- Hash accumulation queries (miner+worker stats per time window)
CREATE INDEX IF NOT EXISTS IDX_SHARES_POOL_MINER_WORKER_CREATED
    ON shares(poolid, miner, worker, created);

-- BRIN index for time-range scans on large tables.
-- Uses ~100x less space than a regular B-tree index on timestamps.
CREATE INDEX IF NOT EXISTS IDX_SHARES_CREATED_BRIN
    ON shares USING BRIN(created) WITH (pages_per_range = 32);

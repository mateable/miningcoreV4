SET ROLE miningcore;

DROP TABLE shares;

CREATE TABLE shares
(
	poolid TEXT NOT NULL,
	blockheight BIGINT NOT NULL,
	difficulty DOUBLE PRECISION NOT NULL,
	networkdifficulty DOUBLE PRECISION NOT NULL,
	miner TEXT NOT NULL,
	worker TEXT NULL,
	useragent TEXT NULL,
	ipaddress TEXT NOT NULL,
    source TEXT NULL,
	created TIMESTAMP WITH TIME ZONE NOT NULL
) PARTITION BY LIST (poolid);

-- Time-range index: effort calculation, cleanup, stats aggregation
CREATE INDEX IDX_SHARES_CREATED ON SHARES(created);

-- Per-miner difficulty lookups
CREATE INDEX IDX_SHARES_MINER_DIFFICULTY on SHARES(miner, difficulty);

-- ORDER BY created DESC queries (read recent shares per pool)
CREATE INDEX IDX_SHARES_POOL_CREATED_DESC on SHARES(poolid, created DESC);

-- Per-miner time history queries
CREATE INDEX IDX_SHARES_POOL_MINER_CREATED on SHARES(poolid, miner, created);

-- Hash accumulation queries (miner+worker stats per time window)
CREATE INDEX IDX_SHARES_POOL_MINER_WORKER_CREATED on SHARES(poolid, miner, worker, created);

-- BRIN index: tiny footprint (~100x smaller than B-tree), fast for large time-range scans
CREATE INDEX IDX_SHARES_CREATED_BRIN on SHARES USING BRIN(created) WITH (pages_per_range = 32);

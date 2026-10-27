using System.Data;
using Dapper;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Persistence.Postgres;

public class PartitionManager
{
    public PartitionManager(IConnectionFactory cf)
    {
        Contract.RequiresNonNull(cf);

        this.cf = cf;
        logger = LogManager.GetLogger(nameof(PartitionManager));
    }

    private readonly IConnectionFactory cf;
    private readonly ILogger logger;

    /// <summary>
    /// Ensures a partition exists for the given pool in the shares table.
    /// Does nothing if the shares table isn't partitioned, or if the partition already exists.
    /// </summary>
    public async Task EnsureSharesPartitionAsync(string poolId, CancellationToken ct)
    {
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(poolId));

        // sanitize poolId for use in a table name (avoid SQL injection)
        if(!IsSafeIdentifier(poolId))
        {
            logger.Warn(() => $"Skipping partition for pool '{poolId}' — name contains unsafe characters");
            return;
        }

        await using var con = (Npgsql.NpgsqlConnection) await cf.OpenConnectionAsync();

        // Check if the shares table is partitioned
        var relkind = await con.QuerySingleOrDefaultAsync<char?>(new CommandDefinition(
            @"SELECT c.relkind FROM pg_catalog.pg_class c WHERE c.relname = 'shares'",
            cancellationToken: ct));

        if(relkind != 'p')
        {
            // shares table isn't partitioned — nothing to do (operator is using plain table)
            return;
        }

        // Check if this pool already has a partition
        var partitionExists = await con.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            @"SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_class
                WHERE relname = @name AND relkind = 'r'
            )", new { name = $"shares_{poolId}" }, cancellationToken: ct));

        if(partitionExists == true)
            return;

        // Create the partition
        var partitionName = $"shares_{poolId}";
        logger.Info(() => $"Creating shares partition '{partitionName}' for pool '{poolId}'");

        await con.ExecuteAsync(new CommandDefinition(
            $"CREATE TABLE {partitionName} PARTITION OF shares FOR VALUES IN ('{poolId}')",
            cancellationToken: ct));

        logger.Info(() => $"Created shares partition '{partitionName}'");
    }

    /// <summary>
    /// Only allows alphanumeric characters, hyphens, and underscores — prevents SQL injection.
    /// </summary>
    private static bool IsSafeIdentifier(string name)
    {
        foreach(var c in name)
        {
            if(!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }

        return true;
    }
}

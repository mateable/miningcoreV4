using Miningcore.Api.Responses;
using Miningcore.Blockchain;
using Miningcore.Configuration;

namespace Miningcore.Mining;

public interface IMiningPool
{
    PoolConfig Config { get; }
    PoolStats PoolStats { get; }
    BlockchainStats NetworkStats { get; }
    double ShareMultiplier { get; }
    void Configure(PoolConfig pc, ClusterConfig cc);
    double HashrateFromShares(double shares, double interval);
    Task RunAsync(CancellationToken ct);
    IEnumerable<LiveMinerConnection> GetLiveConnections();

    /// <summary>
    /// Executes an arbitrary RPC call against this pool's configured daemon, for the
    /// admin-panel RPC console. Default (PoolBase) throws NotSupportedException - only
    /// pool types that override this (currently just Bitcoin-family) support it, since
    /// the RPC client type/shape differs per blockchain family.
    /// </summary>
    Task<object> ExecuteDaemonRpcAsync(string method, object[] parameters, CancellationToken ct);
}

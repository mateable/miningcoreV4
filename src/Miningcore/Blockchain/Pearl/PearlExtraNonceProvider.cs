using Miningcore.Blockchain;

namespace Miningcore.Blockchain.Pearl;

public class PearlExtraNonceProvider : ExtraNonceProviderBase
{
    public PearlExtraNonceProvider(string poolId, int size, byte? clusterInstanceId)
        : base(poolId, size, clusterInstanceId)
    {
    }
}

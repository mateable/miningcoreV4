using System.Collections.Concurrent;

namespace Miningcore.Banning;

/// <summary>
/// Bans a payout address from mining a specific pool at all (distinct from
/// IntegratedBanManager, which bans an IP). In-memory only, lost on restart.
///
/// Enforcement is currently wired into BitcoinPool.OnAuthorizeAsync only -
/// mining.authorize fails for a banned address, same as an invalid one. The
/// other coin families (Ethereum/Cryptonote/etc.) in this codebase have
/// their own OnAuthorizeAsync and are NOT covered yet; this pool only has
/// Bitcoin-family coins configured (MTBC, DOGE, LTC) so that's the real
/// coverage gap today, not a theoretical one, and should be closed before
/// relying on this for a non-Bitcoin-family coin.
/// </summary>
public static class MinerAddressBanManager
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> bansByPool = new();

    public static bool IsBanned(string poolId, string address)
    {
        if(string.IsNullOrEmpty(address) || !bansByPool.TryGetValue(poolId, out var pool))
            return false;

        return pool.ContainsKey(address);
    }

    public static void Ban(string poolId, string address)
    {
        var pool = bansByPool.GetOrAdd(poolId, _ => new ConcurrentDictionary<string, DateTime>());
        pool[address] = DateTime.UtcNow;
    }

    public static bool Unban(string poolId, string address)
    {
        return bansByPool.TryGetValue(poolId, out var pool) && pool.TryRemove(address, out _);
    }

    public static IEnumerable<string> ListBans(string poolId)
    {
        return bansByPool.TryGetValue(poolId, out var pool) ? pool.Keys.ToArray() : Array.Empty<string>();
    }
}

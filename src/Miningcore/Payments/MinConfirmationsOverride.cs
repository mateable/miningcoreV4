using System.Collections.Concurrent;

namespace Miningcore.Payments;

/// <summary>
/// Admin-panel-adjustable per-pool override for the minimum-confirmations
/// value used to compute a pending block's ConfirmationProgress display.
///
/// Important: this does NOT change when a block actually becomes eligible
/// for payout - that's entirely determined by the daemon's own coinbase
/// maturity rules (the "immature" -> "generate" transaction category
/// reported by the daemon itself, a real consensus rule Miningcore cannot
/// and should not override). This only changes what fraction/percentage is
/// *displayed* while a block is still immature. Same in-memory-only,
/// resets-on-restart tradeoff as the admin panel's other live-editable
/// settings (password, IP allowlist) - set it permanently via
/// paymentProcessing.minimumConfirmations in config.json instead if you
/// want it to stick.
/// </summary>
public static class MinConfirmationsOverride
{
    private static readonly ConcurrentDictionary<string, int> overrides = new();

    public static void Set(string poolId, int value)
    {
        overrides[poolId] = value;
    }

    public static bool Clear(string poolId)
    {
        return overrides.TryRemove(poolId, out _);
    }

    /// <summary>
    /// Returns the override for this pool if one is set, otherwise the
    /// configured/default value passed in unchanged.
    /// </summary>
    public static int Resolve(string poolId, int configured)
    {
        return overrides.TryGetValue(poolId, out var value) ? value : configured;
    }

    public static IReadOnlyDictionary<string, int> ListAll() => overrides;
}

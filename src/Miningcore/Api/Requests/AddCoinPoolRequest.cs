namespace Miningcore.Api.Requests;

/// <summary>
/// Admin-panel "Add Coin" form payload. Describes both the coins.json entry
/// and the pool entry to add for it - these are always created together
/// since a coin with no pool mining it is useless, and a pool needs a coin
/// template to resolve its algorithm/hashers against.
///
/// This is a UI convenience over what ConfigHotReloadService already does
/// for a hand-edited config.json - it writes the exact same file, then lets
/// the existing hot-reload pick it up, rather than adding any new reload
/// mechanism of its own.
/// </summary>
public class AddCoinPoolRequest
{
    // --- coins.json fields ---
    public string CoinKey { get; set; }
    public string CoinName { get; set; }
    public string CoinSymbol { get; set; }
    public string Algorithm { get; set; }
    public uint[] AlgorithmArgs { get; set; }
    public string Website { get; set; }

    /// <summary>
    /// Block explorer URL template, e.g. https://explorer.example.com/block/{0}.
    /// Blank is fine - block-unlock notifications null-check this.
    /// </summary>
    public string ExplorerBlockLink { get; set; }

    /// <summary>
    /// When true, CoinKey must already exist in coins.json (e.g. one of the
    /// 250+ built-in templates this fork ships with) - no new coin entry is
    /// written, only the pool. CoinName/CoinSymbol/Algorithm/Website/
    /// AlgorithmArgs are ignored in this mode since the existing coin
    /// definition is already authoritative.
    /// </summary>
    public bool UseExistingCoin { get; set; }

    // --- pool fields ---
    public string PoolId { get; set; }
    public string Address { get; set; }
    public string DaemonHost { get; set; }
    public int DaemonPort { get; set; }
    public string DaemonUser { get; set; }
    public string DaemonPassword { get; set; }
    public string DaemonHttpPath { get; set; }

    public int StratumPort { get; set; }
    public double Difficulty { get; set; } = 0.001;

    public double? VarDiffMinDiff { get; set; }
    public double? VarDiffMaxDiff { get; set; }
    public double VarDiffTargetTime { get; set; } = 15;
    public double VarDiffRetargetTime { get; set; } = 90;
    public double VarDiffVariancePercent { get; set; } = 30;

    public bool Solo { get; set; }
    public bool EnablePaymentProcessing { get; set; }
    public decimal MinimumPayment { get; set; } = 0.5m;
    public decimal PplnsFactor { get; set; } = 2.0m;

    /// <summary>
    /// Pool operator fee, taken off the top of each block reward before
    /// miner payouts are calculated (0 = no fee). Requires FeeAddress when
    /// non-zero.
    /// </summary>
    public decimal FeePercent { get; set; }
    public string FeeAddress { get; set; }
}

/// <summary>
/// Admin-panel "Edit Pool" form payload - operational settings only
/// (address, daemon, port, difficulty, payout mode). Changing the
/// underlying coin/algorithm isn't supported here; remove and re-add
/// for that, since it's a structural change rather than a tweak.
/// </summary>
public class EditPoolRequest
{
    public string PoolId { get; set; }
    public string ExplorerBlockLink { get; set; }

    /// <summary>
    /// Updates the coin's display name in coins.json (shared by every pool using
    /// that same coin key - e.g. mateablecoin-balloon and mateablecoin-whirlpool are
    /// two separate coin keys, but if both have "name": "MateableCoin" they'll show
    /// grouped together on the Overview/Pools tabs; give them distinct names here to
    /// separate them, or matching names to group pools that should appear together).
    /// Null/blank leaves it unchanged - same conditional-write pattern as
    /// ExplorerBlockLink below.
    /// </summary>
    public string CoinName { get; set; }
    public string Address { get; set; }
    public string DaemonHost { get; set; }
    public int DaemonPort { get; set; }
    public string DaemonUser { get; set; }
    public string DaemonPassword { get; set; }
    public string DaemonHttpPath { get; set; }
    public double Difficulty { get; set; }
    public double? VarDiffMinDiff { get; set; }
    public double? VarDiffMaxDiff { get; set; }
    public double VarDiffTargetTime { get; set; } = 15;
    public double VarDiffRetargetTime { get; set; } = 90;
    public double VarDiffVariancePercent { get; set; } = 30;
    public bool Solo { get; set; }
    public bool EnablePaymentProcessing { get; set; }
    public decimal MinimumPayment { get; set; } = 0.5m;
    public decimal PplnsFactor { get; set; } = 2.0m;
    public bool Enabled { get; set; } = true;
    public decimal FeePercent { get; set; }
    public string FeeAddress { get; set; }
}

public class RemovePoolRequest
{
    public string PoolId { get; set; }
}

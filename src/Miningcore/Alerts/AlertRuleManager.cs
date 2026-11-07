using System.Collections.Concurrent;
using Miningcore.Admin;
using Newtonsoft.Json;

namespace Miningcore.Alerts;

/// <summary>
/// Pool-oriented alert rules (yiimp parity: CoinTriggers, deliberately scoped
/// down). yiimp's version watches a flat coin-attribute row and can fire
/// arbitrary RPC or shell commands on a match - the same risk class as the
/// admin Console feature, just automatic instead of a button press. This
/// version drops that entirely: a rule only ever shows up as a visible,
/// zero-config alert in the admin panel (and the audit log), plus an email
/// via the existing AdminNotification pipeline - never runs anything.
/// Scoped per-pool rather than per-coin, since this cluster runs several
/// independent pools per coin (5 for MTBC alone), and adds a metric yiimp
/// has no equivalent for: "no new block height observed in N minutes",
/// which is actually relevant to running a pool (a desynced daemon or a
/// dead job pipeline) in a way price/balance watching is not.
///
/// Evaluated from StatsRecorder's own periodic cycle (every real stats
/// update, independent of whether anyone has the admin panel open) rather
/// than from the dashboard's polling - genuinely fires even unattended.
/// </summary>
public static class AlertRuleManager
{
    public class AlertRule
    {
        public string Id { get; set; }
        public string PoolId { get; set; }
        public string Metric { get; set; } // poolHashrate, connectedMiners, networkHashrate, networkDifficulty, minutesSinceBlockHeightChange
        public string Operator { get; set; } // <, >, <=, >=, ==
        public double Threshold { get; set; }
        public string Description { get; set; }
        public bool Enabled { get; set; } = true;
        public DateTime? LastTriggeredAt { get; set; }
    }

    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "alert_rules.json");
    private static readonly ConcurrentDictionary<string, AlertRule> rules = new();
    private static readonly object fileLock = new();

    // In-memory only (not persisted - resets on restart, which is fine, it
    // just means the "minutes since" clock restarts too) tracking for the
    // stale-pool metric: last block height observed per pool, and when it
    // was last seen to actually change.
    private static readonly ConcurrentDictionary<string, (ulong Height, DateTime ChangedAt)> blockHeightTracking = new();

    // Tracks whether each rule was triggered on the previous check, so the
    // audit log / email only record the transition (became triggered /
    // cleared), not every single poll.
    private static readonly ConcurrentDictionary<string, bool> wasTriggered = new();

    static AlertRuleManager()
    {
        Load();
    }

    private static void Load()
    {
        try
        {
            if(!File.Exists(FilePath))
                return;

            var json = File.ReadAllText(FilePath);
            var entries = JsonConvert.DeserializeObject<AlertRule[]>(json) ?? Array.Empty<AlertRule>();

            foreach(var entry in entries)
                rules.TryAdd(entry.Id, entry);
        }

        catch
        {
            // corrupt/missing file - start with no rules rather than crash startup
        }
    }

    private static void Save()
    {
        lock(fileLock)
        {
            var json = JsonConvert.SerializeObject(rules.Values.ToArray(), Formatting.Indented);
            File.WriteAllText(FilePath, json);
        }
    }

    public static AlertRule[] ListRules()
    {
        return rules.Values.OrderBy(x => x.PoolId).ToArray();
    }

    public static AlertRule AddRule(string poolId, string metric, string op, double threshold, string description)
    {
        var rule = new AlertRule
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            PoolId = poolId,
            Metric = metric,
            Operator = op,
            Threshold = threshold,
            Description = description,
            Enabled = true,
        };

        rules[rule.Id] = rule;
        Save();
        return rule;
    }

    public static bool SetEnabled(string id, bool enabled)
    {
        if(!rules.TryGetValue(id, out var rule))
            return false;

        rule.Enabled = enabled;
        Save();
        return true;
    }

    public static bool DeleteRule(string id)
    {
        if(rules.TryRemove(id, out _))
        {
            Save();
            return true;
        }

        return false;
    }

    private static bool Compare(double value, string op, double threshold)
    {
        return op switch
        {
            "<" => value < threshold,
            ">" => value > threshold,
            "<=" => value <= threshold,
            ">=" => value >= threshold,
            "==" or "=" => Math.Abs(value - threshold) < 0.0000001,
            _ => false,
        };
    }

    /// <summary>
    /// Call once per pool from StatsRecorder's own periodic update, to keep
    /// the stale-block tracking fresh.
    /// </summary>
    public static void ObserveBlockHeight(string poolId, ulong height)
    {
        if(height == 0)
            return;

        blockHeightTracking.AddOrUpdate(poolId,
            _ => (height, DateTime.UtcNow),
            (_, existing) => existing.Height == height ? existing : (height, DateTime.UtcNow));
    }

    public class ActiveAlert
    {
        public string Id { get; set; }
        public string PoolId { get; set; }
        public string Metric { get; set; }
        public string Operator { get; set; }
        public double Threshold { get; set; }
        public double CurrentValue { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Evaluates every enabled rule against the given live pool metrics and
    /// returns which ones are currently triggered. Records audit-log
    /// entries and fires the triggered/cleared callbacks only on state
    /// transitions (not every poll).
    /// </summary>
    public static ActiveAlert[] Evaluate(Func<string, string, double?> getMetric, Action<string, string> onTriggered = null, Action<string, string> onCleared = null)
    {
        var active = new List<ActiveAlert>();

        foreach(var rule in rules.Values.Where(x => x.Enabled))
        {
            double? value = rule.Metric == "minutesSinceBlockHeightChange"
                ? (blockHeightTracking.TryGetValue(rule.PoolId, out var tracking)
                    ? (DateTime.UtcNow - tracking.ChangedAt).TotalMinutes
                    : (double?) null)
                : getMetric(rule.PoolId, rule.Metric);

            if(value == null)
                continue;

            var triggered = Compare(value.Value, rule.Operator, rule.Threshold);
            var previouslyTriggered = wasTriggered.GetValueOrDefault(rule.Id);

            if(triggered != previouslyTriggered)
            {
                wasTriggered[rule.Id] = triggered;

                if(triggered)
                {
                    rule.LastTriggeredAt = DateTime.UtcNow;
                    Save();
                }

                var subject = $"[{rule.PoolId}] Alert " + (triggered ? "triggered" : "cleared") + $": {rule.Metric} {rule.Operator} {rule.Threshold}";
                var message = $"Pool: {rule.PoolId}\nMetric: {rule.Metric}\nCondition: {rule.Operator} {rule.Threshold}\nCurrent value: {value.Value:0.###}\nDescription: {rule.Description}";

                AdminAuditLog.Record("system", triggered ? "alert.triggered" : "alert.cleared",
                    $"pool={rule.PoolId} metric={rule.Metric} op={rule.Operator} threshold={rule.Threshold} value={value.Value:0.###} - {rule.Description}");

                if(triggered)
                    onTriggered?.Invoke(subject, message);
                else
                    onCleared?.Invoke(subject, message);
            }

            if(triggered)
            {
                active.Add(new ActiveAlert
                {
                    Id = rule.Id,
                    PoolId = rule.PoolId,
                    Metric = rule.Metric,
                    Operator = rule.Operator,
                    Threshold = rule.Threshold,
                    CurrentValue = value.Value,
                    Description = rule.Description,
                });
            }
        }

        return active.ToArray();
    }
}

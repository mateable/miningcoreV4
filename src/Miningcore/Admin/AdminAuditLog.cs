using System.Collections.Concurrent;

namespace Miningcore.Admin;

public record AuditEntry(DateTime Timestamp, string Ip, string Action, string Detail);

/// <summary>
/// Records admin-panel actions (coin/pool changes, bans, payout triggers,
/// console commands, settings changes). There is only one shared admin
/// password and no other way to see what changed and when, so this is the
/// only audit trail available - in-memory only, lost on restart, by design
/// matching the in-memory log viewer below it.
/// </summary>
public static class AdminAuditLog
{
    private const int MaxEntries = 500;
    private static readonly ConcurrentQueue<AuditEntry> entries = new();

    public static void Record(string ip, string action, string detail)
    {
        entries.Enqueue(new AuditEntry(DateTime.UtcNow, ip ?? "unknown", action, detail ?? ""));

        while(entries.Count > MaxEntries)
            entries.TryDequeue(out _);
    }

    public static IEnumerable<AuditEntry> Recent(int count = 200)
    {
        return entries.Reverse().Take(count);
    }
}

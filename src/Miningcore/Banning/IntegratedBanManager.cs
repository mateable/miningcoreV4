using System.Collections.Concurrent;
using System.Net;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Banning;

public class IntegratedBanManager : IBanManager
{
    // Replaces the previous IMemoryCache-backed ban store: IMemoryCache has
    // no enumeration API, so the admin panel's ban-management view (list
    // active bans, unban on demand) had no way to read what was actually
    // banned. This dictionary is both the enforcement source (IsBanned)
    // and the listing source - one place, no risk of the two disagreeing.
    private static readonly ConcurrentDictionary<string, DateTime> bans = new();

    private static readonly ConcurrentDictionary<string, int> banCounts = new();
    private static readonly ConcurrentDictionary<string, ThrottleWindow> throttles = new();

    private static readonly TimeSpan[] Escalation =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(24),
        TimeSpan.MaxValue, // permanent
    ];

    private struct ThrottleWindow
    {
        public int Count;
        public DateTime ResetAt;
    }

    public bool IsBanned(IPAddress address)
    {
        var key = address.ToString();

        if(!bans.TryGetValue(key, out var until))
            return false;

        if(until <= DateTime.UtcNow)
        {
            bans.TryRemove(key, out _);
            return false;
        }

        return true;
    }

    public void Ban(IPAddress address, TimeSpan duration)
    {
        Contract.RequiresNonNull(address);
        Contract.Requires<ArgumentException>(duration.TotalMilliseconds > 0);

        if(address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback))
            return;

        var key = address.ToString();
        var until = duration == TimeSpan.MaxValue ? DateTime.MaxValue : DateTime.UtcNow.Add(duration);
        bans[key] = until;
    }

    public void EscalateBan(IPAddress address)
    {
        if(address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback))
            return;

        var key = address.ToString();
        var count = banCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
        var idx = Math.Min(count - 1, Escalation.Length - 1);
        var duration = Escalation[idx];

        var until = duration == TimeSpan.MaxValue ? DateTime.MaxValue : DateTime.UtcNow.Add(duration);
        bans[key] = until;
    }

    public IEnumerable<BanEntry> ListBans()
    {
        var now = DateTime.UtcNow;

        foreach(var kv in bans)
        {
            if(kv.Value <= now)
            {
                bans.TryRemove(kv.Key, out _);
                continue;
            }

            yield return new BanEntry(kv.Key, kv.Value);
        }
    }

    public bool Unban(IPAddress address)
    {
        return bans.TryRemove(address.ToString(), out _);
    }

    public bool ThrottleConnect(IPAddress address)
    {
        if(address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback))
            return true;

        var key = address.ToString();
        var now = DateTime.UtcNow;

        var window = throttles.AddOrUpdate(key,
            _ => new ThrottleWindow { Count = 1, ResetAt = now.AddSeconds(1) },
            (_, w) =>
            {
                if(now > w.ResetAt)
                    return new ThrottleWindow { Count = 1, ResetAt = now.AddSeconds(1) };
                return new ThrottleWindow { Count = w.Count + 1, ResetAt = w.ResetAt };
            });

        return window.Count <= 5;
    }
}

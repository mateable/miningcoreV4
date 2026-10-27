using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Banning;

public class IntegratedBanManager : IBanManager
{
    private static readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions
    {
        ExpirationScanFrequency = TimeSpan.FromSeconds(10)
    });

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
        return cache.Get(address.ToString()) != null;
    }

    public void Ban(IPAddress address, TimeSpan duration)
    {
        Contract.RequiresNonNull(address);
        Contract.Requires<ArgumentException>(duration.TotalMilliseconds > 0);

        if(address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback))
            return;

        var key = address.ToString();
        cache.Set(key, string.Empty, duration);
    }

    public void EscalateBan(IPAddress address)
    {
        if(address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback))
            return;

        var key = address.ToString();
        var count = banCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
        var idx = Math.Min(count - 1, Escalation.Length - 1);
        var duration = Escalation[idx];

        cache.Set(key, string.Empty, duration);
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

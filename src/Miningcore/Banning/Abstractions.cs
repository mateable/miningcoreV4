using System.Net;

namespace Miningcore.Banning;

public record BanEntry(string Address, DateTime BannedUntil);

public interface IBanManager
{
    bool IsBanned(IPAddress address);
    void Ban(IPAddress address, TimeSpan duration);
    void EscalateBan(IPAddress address);
    bool ThrottleConnect(IPAddress address);

    /// <summary>
    /// Currently active bans, for the admin panel's ban-management view.
    /// </summary>
    IEnumerable<BanEntry> ListBans();

    /// <summary>
    /// Lifts a ban immediately, regardless of remaining duration.
    /// </summary>
    bool Unban(IPAddress address);
}

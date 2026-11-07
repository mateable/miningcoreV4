namespace Miningcore.Api.Responses;

/// <summary>
/// Snapshot of a single active Stratum connection, for the admin panel's
/// live connected-miners view and ban/botnet tooling.
/// </summary>
public class LiveMinerConnection
{
    public string ConnectionId { get; set; }
    public string RemoteIp { get; set; }
    public string Miner { get; set; }
    public string Worker { get; set; }
    public double Difficulty { get; set; }
    public int ValidShares { get; set; }
    public int InvalidShares { get; set; }
    public bool IsAuthorized { get; set; }
    public DateTime Created { get; set; }
    public DateTime LastActivity { get; set; }
}

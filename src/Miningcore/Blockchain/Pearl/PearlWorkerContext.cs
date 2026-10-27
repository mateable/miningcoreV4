using Miningcore.Mining;

namespace Miningcore.Blockchain.Pearl;

/// <summary>
/// Per-connection state for a Pearl stratum worker.
/// </summary>
public class PearlWorkerContext : WorkerContextBase
{
    /// <summary>Miner wallet address (set on authorize).</summary>
    public override string Miner { get; set; } = string.Empty;

    /// <summary>Arbitrary rig / worker name suffix after the dot.</summary>
    public override string Worker { get; set; } = string.Empty;

    /// <summary>Pool-assigned extra-nonce prefix (hex string).</summary>
    public string ExtraNonce1 { get; set; } = string.Empty;

    // -------------------------------------------------------------------------
    // Active job tracking (newest jobs at back of queue)
    // -------------------------------------------------------------------------
    private readonly Queue<PearlJob> _validJobs = new();

    /// <summary>Enqueue a new job and evict the oldest if over limit.</summary>
    public void AddJob(PearlJob job, int maxActiveJobs)
    {
        if(!_validJobs.Contains(job))
            _validJobs.Enqueue(job);

        while(_validJobs.Count > maxActiveJobs)
            _validJobs.Dequeue();
    }

    /// <summary>Look up a tracked job by its ID, or null if stale/unknown.</summary>
    public PearlJob GetJob(string jobId) =>
        _validJobs.FirstOrDefault(j => j.JobId == jobId);
}

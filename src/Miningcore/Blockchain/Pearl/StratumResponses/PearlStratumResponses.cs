using Newtonsoft.Json;

namespace Miningcore.Blockchain.Pearl.StratumResponses;

/// <summary>
/// Sent in reply to mining.subscribe.
/// Carries the session ID and the two extraNonce components.
/// </summary>
public class PearlSubscribeResponse
{
    /// <summary>Session / subscription ID (connection-scoped).</summary>
    [JsonProperty("id")]
    public string SessionId { get; set; }

    /// <summary>Pool-assigned extraNonce1 prefix (hex).</summary>
    [JsonProperty("extraNonce1")]
    public string ExtraNonce1 { get; set; }

    /// <summary>Number of bytes the miner must supply for extraNonce2.</summary>
    [JsonProperty("extraNonce2Size")]
    public int ExtraNonce2Size { get; set; }
}

/// <summary>
/// Sent as a mining.notify broadcast for each new job.
///
/// params: [jobId, headerHex, target, height, cleanJobs]
///
/// headerHex  – the same headerBytes the pool received from the node
///              (IncompleteBlockHeader wire format, 80 bytes hex).
///              The miner is expected to slot extraNonce1 + extraNonce2
///              into the designated nonce field before hashing.
/// target     – 256-bit target hex (big-endian); share accepted when
///              hash(header) &lt; target.
/// </summary>
public class PearlJobNotification
{
    [JsonProperty("jobId")]
    public string JobId { get; set; }

    [JsonProperty("headerHex")]
    public string HeaderHex { get; set; }

    [JsonProperty("target")]
    public string Target { get; set; }

    [JsonProperty("height")]
    public ulong Height { get; set; }

    [JsonProperty("cleanJobs")]
    public bool CleanJobs { get; set; }
}

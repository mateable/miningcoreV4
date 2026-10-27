using Newtonsoft.Json;

namespace Miningcore.Blockchain.Pearl.StratumRequests;

// NOTE: Pearl reuses the Bitcoin-style stratum wire format. The pool parses
// submit params positionally as string[]; these DTOs document the shapes.

/// <summary>mining.subscribe params: [userAgent]</summary>
public class PearlSubscribeRequest
{
    [JsonProperty("agent")]
    public string UserAgent { get; set; }
}

/// <summary>mining.authorize params: [login, password]</summary>
public class PearlAuthorizeRequest
{
    [JsonProperty("login")]
    public string Login { get; set; }

    [JsonProperty("pass")]
    public string Password { get; set; }
}

/// <summary>
/// mining.submit params: [jobId, plainProofBase64]
///
/// plainProofBase64 is the miner's serialized PlainProof — the matrix
/// proof-of-useful-work solution. The pool feeds it to the Python bridge which
/// verifies it (verify_plain_proof), generates the ZK proof (generate_proof),
/// and assembles the final block for submitblock.
/// </summary>
public class PearlSubmitRequest
{
    [JsonProperty("id")]
    public string JobId { get; set; }

    [JsonProperty("plainProof")]
    public string PlainProofBase64 { get; set; }
}

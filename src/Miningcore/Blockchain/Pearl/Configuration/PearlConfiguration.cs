using Newtonsoft.Json;

namespace Miningcore.Blockchain.Pearl.Configuration;

public class PearlPoolConfigExtra
{
    public int? MaxActiveJobs { get; set; }
    public int? ExtraNonce1Size { get; set; }
    public int? BlockRefreshInterval { get; set; }

    public string GatewayUrl { get; set; }
    public string PythonExecutable { get; set; }
    public string PythonBridgeScript { get; set; }

    public int? MaxStratumRequestSize { get; set; }
}

public class PearlPaymentProcessingConfigExtra
{
    [JsonProperty("walletPassphrase")]
    public string WalletPassphrase { get; set; }

    [JsonProperty("account")]
    public string Account { get; set; } = "default";

    [JsonProperty("feeRatePerKb")]
    public decimal FeeRatePerKb { get; set; } = 0.0001m;

    [JsonProperty("minConf")]
    public int MinConf { get; set; } = 1;

    [JsonProperty("walletUnlockTimeout")]
    public int WalletUnlockTimeout { get; set; } = 300;
}

public class PearlDaemonEndpointConfigExtra
{
    public int? PortWs { get; set; }
    public string HttpPathWs { get; set; }
    public bool SslWs { get; set; }
}
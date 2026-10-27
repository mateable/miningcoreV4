using System.Diagnostics;
using Miningcore.Blockchain.Pearl.DaemonResponses;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Miningcore.Blockchain.Pearl;

/// <summary>
/// Result of submitting a PlainProof through the bridge.
/// </summary>
public sealed class PearlProofResult
{
    public bool   Valid              { get; init; }
    public bool   MeetsNetworkTarget { get; init; }
    public double ShareDifficulty    { get; init; }
    public double NetworkDifficulty  { get; init; }
    public string BlockHex           { get; init; }
    public string BlockHash          { get; init; }
    public string Error              { get; init; }
}

/// <summary>
/// Long-lived sidecar wrapping the pearl_mining module and the pearl-gateway
/// blockchain_utils (PearlBlock / PearlHeader / ZKCertificate). Communicates
/// over line-delimited JSON-RPC on stdin/stdout.
///
/// Methods (C# -> Python):
///   set_template   { jobId, template }            -> { ok, incompleteHeaderHex, target }
///   submit_proof   { jobId, plainProofB64 }        -> { valid, meetsTarget, shareDifficulty,
///                                                       networkDifficulty, blockHex, blockHash, error }
///
/// The Python side owns all Pearl-specific serialization so the produced block
/// stays byte-identical to the upstream gateway.
/// </summary>
public sealed class PearlPythonBridge : IAsyncDisposable
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process _process;
    private StreamWriter _stdin;
    private StreamReader _stdout;
    private string _miningAddress;
    private int _requestId;

    // Cache of incomplete-header hex per jobId (populated by set_template)
    private readonly Dictionary<string, string> _headerHexByJob = new();

    public async Task StartAsync(
        string pythonExe     = "python3",
        string bridgeScript  = "pearl_mining_bridge.py",
        string miningAddress = null,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if(_process != null)
                return;

            _miningAddress = miningAddress;

            var psi = new ProcessStartInfo
            {
                FileName               = pythonExe,
                Arguments              = bridgeScript,
                UseShellExecute        = false,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start pearl_mining_bridge.py");

            _stdin  = _process.StandardInput;
            _stdout = _process.StandardOutput;

            var ready = await _stdout.ReadLineAsync();
            Logger.Info(() => $"[PearlPythonBridge] Sidecar ready: {ready}");

            var readyObj = string.IsNullOrEmpty(ready) ? null : JObject.Parse(ready);
            if(readyObj?["ready"]?.Value<string>() != "ok")
                throw new InvalidOperationException(
                    $"pearl_mining_bridge failed to initialise: {ready}");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Push a block template to the bridge for a job, returning the canonical
    /// incomplete-header hex the miners will work on.
    /// </summary>
    public async Task SetTemplateAsync(string jobId, PearlBlockTemplate template, CancellationToken ct)
    {
        var request = new JObject
        {
            ["method"]        = "set_template",
            ["id"]            = Interlocked.Increment(ref _requestId),
            ["jobId"]         = jobId,
            ["miningAddress"] = _miningAddress,
            ["template"]      = JToken.FromObject(template),
        };

        var response = await CallAsync(request, ct);
        var headerHex = response["incompleteHeaderHex"]?.Value<string>()
            ?? throw new InvalidOperationException("set_template returned no incompleteHeaderHex");

        lock(_headerHexByJob)
            _headerHexByJob[jobId] = headerHex;
    }

    /// <summary>Return the cached incomplete-header hex for a job (set via SetTemplateAsync).</summary>
    public string GetIncompleteHeaderHex(string jobId)
    {
        lock(_headerHexByJob)
            return _headerHexByJob.TryGetValue(jobId, out var hex) ? hex : string.Empty;
    }

    /// <summary>
    /// Submit a miner's serialized PlainProof. The bridge verifies it, computes
    /// the achieved difficulty and, if it meets the network target, generates
    /// the ZK proof and assembles the final block hex.
    /// </summary>
    public async Task<PearlProofResult> SubmitPlainProofAsync(
        string jobId, string plainProofBase64, double workerDifficulty, CancellationToken ct)
    {
        var request = new JObject
        {
            ["method"]         = "submit_proof",
            ["id"]             = Interlocked.Increment(ref _requestId),
            ["jobId"]          = jobId,
            ["plainProofB64"]  = plainProofBase64,
            ["difficulty"]     = workerDifficulty,
        };

        var response = await CallAsync(request, ct);

        return new PearlProofResult
        {
            Valid              = response["valid"]?.Value<bool>() ?? false,
            MeetsNetworkTarget = response["meetsTarget"]?.Value<bool>() ?? false,
            ShareDifficulty    = response["shareDifficulty"]?.Value<double>() ?? 0d,
            NetworkDifficulty  = response["networkDifficulty"]?.Value<double>() ?? 0d,
            BlockHex           = response["blockHex"]?.Value<string>(),
            BlockHash          = response["blockHash"]?.Value<string>(),
            Error              = response["error"]?.Value<string>(),
        };
    }

    private async Task<JObject> CallAsync(JObject request, CancellationToken ct)
    {
        // Only the lock wait honours the caller's token. Once we start writing
        // to the sidecar, the request/response pair must complete uncancelled,
        // otherwise a disconnecting miner would desynchronise the line protocol
        // (request written, response left unread for the next caller).
        await _lock.WaitAsync(ct);
        try
        {
            if(_stdin == null || _stdout == null)
                throw new InvalidOperationException("PearlPythonBridge not started");

            var requestId = request["id"]?.Value<long>() ?? 0;

            var line = JsonConvert.SerializeObject(request);
            await _stdin.WriteLineAsync(line);
            await _stdin.FlushAsync();

            // Read until we see the response matching our id (skips any stale
            // responses left over from an aborted predecessor, just in case).
            while(true)
            {
                var responseLine = await _stdout.ReadLineAsync()
                    ?? throw new InvalidOperationException("Pearl sidecar closed stdout unexpectedly");

                var response = JObject.Parse(responseLine);

                var responseId = response["id"]?.Value<long>() ?? -1;
                if(responseId != requestId)
                {
                    Logger.Warn(() =>
                        $"[PearlPythonBridge] Discarding stale response id={responseId} (expected {requestId})");
                    continue;
                }

                if(response["error"] != null && response["valid"] == null)
                    throw new InvalidOperationException($"pearl_mining_bridge error: {response["error"]}");

                return response;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _stdin?.Close();
            _stdout?.Close();

            if(_process != null && !_process.HasExited)
            {
                _process.Kill();
                await _process.WaitForExitAsync();
            }

            _process?.Dispose();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}

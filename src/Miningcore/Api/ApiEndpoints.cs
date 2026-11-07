using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Autofac;
using AutoMapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Miningcore.Admin;
using Miningcore.Alerts;
using Miningcore.Api.Extensions;
using Miningcore.Api.Responses;
using Miningcore.Banning;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.Logging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Model.Projections;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Targets;

namespace Miningcore.Api;

public static class ApiEndpoints
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    public static void MapMiningcoreApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/help", (EndpointDataSource endpointDataSource) =>
        {
            var endpoints = endpointDataSource.Endpoints.OfType<RouteEndpoint>();
            var tmp = endpoints.Select(e =>
            {
                var method = e.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods.FirstOrDefault() ?? "GET";
                method = $"{method,-5}";
                return $"{method} -> {e.RoutePattern.RawText}";
            });

            var result = string.Join("\n", tmp).Replace("{", "<").Replace("}", ">") + "\n";
            return Results.Text(result);
        });

        api.MapGet("/health-check", () => Results.Text("👍"));

        api.MapGet("/meta", () => Results.Json(new
        {
            generator = "Miningcore Community Edition",
            repo = "soosho/miningcore"
        }));

        MapClusterApi(api);
        MapPoolApi(api);
        MapAdminApi(api);
    }

    private static void MapClusterApi(RouteGroupBuilder api)
    {
        api.MapGet("/blocks", async (
            [FromServices] IConnectionFactory cf,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15,
            [FromQuery] BlockStatus[] state = null) =>
        {
            var ct = httpContext.RequestAborted;
            var blockStates = state is { Length: > 0 } ?
                state :
                new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

            var enabledPools = new HashSet<string>(clusterConfig.Pools.Where(x => x.Enabled).Select(x => x.Id));

            var blocks = (await cf.Run(con => blocksRepo.PageBlocksAsync(con, blockStates, page, pageSize == 0 ? 15 : pageSize, ct)))
                .Select(mapper.Map<Responses.Block>)
                .Where(x => enabledPools.Contains(x.PoolId))
                .ToArray();

            var blocksByPool = blocks.GroupBy(x => x.PoolId);

            foreach (var poolBlocks in blocksByPool)
            {
                var pool = GetPoolNoThrow(clusterConfig, poolBlocks.Key);

                if (pool == null)
                    continue;

                var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

                if (blockInfobaseDict != null)
                {
                    foreach (var block in poolBlocks)
                    {
                        blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                        if (!string.IsNullOrEmpty(blockInfobaseUrl))
                        {
                            if (blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                                block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                            else if (blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                                block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                        }
                    }
                }
            }

            return blocks;
        });
    }

    private static void MapAdminApi(RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin");

        admin.MapGet("/logging/level/{level}", (string level) =>
        {
            if (string.IsNullOrEmpty(level))
                throw new ApiException("Invalid logging level", HttpStatusCode.BadRequest);

            var logLevel = LogLevel.FromString(level);

            if (logLevel == null)
                throw new ApiException("Invalid logging level", HttpStatusCode.BadRequest);

            logger.Error("Admin update Logging Level this is Error");
            logger.Trace("Admin update Logging Level this is Trace");

            foreach (var rule in LogManager.Configuration.LoggingRules)
            {
                rule.EnableLoggingForLevel(logLevel);
                rule.SetLoggingLevels(logLevel, LogLevel.Fatal);
            }

            Target target = LogManager.Configuration.FindTargetByName("console");

            if (target != null)
            {
                var loggingConfig = LogManager.Configuration;
                loggingConfig.AddRule(logLevel, LogLevel.Fatal, target);
                LogManager.Configuration = loggingConfig;
            }

            LogManager.ReconfigExistingLoggers();

            logger.Error("Admin update Logging Level this is Error AFTER");
            logger.Trace("Admin update Logging Level this is Trace AFTER");

            logger.Info($"Logging level set to {level}");
            return "Ok";
        });

        admin.MapGet("/payment/processing/enable", (
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools) =>
        {
            var poolIdsUpdated = new List<string>();
            foreach (var pool in pools.Values)
            {
                if (!pool.Config.Enabled) continue;

                poolIdsUpdated.Add(pool.Config.Id);
                pool.Config.PaymentProcessing.Enabled = true;
            }

            var poolIdsCsv = string.Join(",", poolIdsUpdated);
            logger.Info(() => $"Enabled payment processing for pool {poolIdsCsv}");

            return poolIdsCsv;
        });

        admin.MapGet("/payment/processing/disable", (
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools) =>
        {
            var poolIdsUpdated = new List<string>();
            foreach (var pool in pools.Values)
            {
                if (!pool.Config.Enabled) continue;

                poolIdsUpdated.Add(pool.Config.Id);
                pool.Config.PaymentProcessing.Enabled = false;
            }

            var poolIdsCsv = string.Join(",", poolIdsUpdated);
            logger.Info(() => $"Disabled payment processing for pool {poolIdsCsv}");

            return poolIdsCsv;
        });

        admin.MapGet("/payment/processing/{poolId}/enable", (
            string poolId,
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools) =>
        {
            if (string.IsNullOrEmpty(poolId))
                throw new ApiException("Missing pool ID", HttpStatusCode.BadRequest);

            pools.TryGetValue(poolId, out var poolInstance);
            if (poolInstance == null)
                return "-1";

            poolInstance.Config.PaymentProcessing.Enabled = true;
            logger.Info(() => $"Enabled payment processing for pool {poolId}");
            return "Ok";
        });

        admin.MapGet("/payment/processing/{poolId}/disable", (
            string poolId,
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools) =>
        {
            if (string.IsNullOrEmpty(poolId))
                throw new ApiException("Missing pool ID", HttpStatusCode.BadRequest);

            pools.TryGetValue(poolId, out var poolInstance);
            if (poolInstance == null)
                return "-1";

            poolInstance.Config.PaymentProcessing.Enabled = false;
            logger.Info(() => $"Disabled payment processing for pool {poolId}");
            return "Ok";
        });

        admin.MapGet("/stats/gc", (
            [FromServices] Responses.AdminGcStats gcStats) =>
        {
            gcStats.GcGen0 = GC.CollectionCount(0);
            gcStats.GcGen1 = GC.CollectionCount(1);
            gcStats.GcGen2 = GC.CollectionCount(2);
            gcStats.MemAllocated = FormatUtil.FormatCapacity(GC.GetTotalMemory(false));

            return gcStats;
        });

        admin.MapPost("/forcegc", () =>
        {
            GC.Collect(2, GCCollectionMode.Forced);
            return "Ok";
        });

        // === Live connected miners (admin panel "Miners" tab) ===
        admin.MapGet("/pools/{poolId}/connections", (
            string poolId,
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools) =>
        {
            if(!pools.TryGetValue(poolId, out var pool))
                return Results.NotFound();

            return Results.Json(pool.GetLiveConnections());
        });

        // === Ban management: list / unban ===
        admin.MapGet("/bans", ([FromServices] IComponentContext ctx) =>
        {
            var banManager = ctx.ResolveKeyed<IBanManager>(BanManagerKind.Integrated);
            return banManager.ListBans();
        });

        admin.MapPost("/bans/unban", async (
            HttpContext httpCtx,
            [FromServices] IComponentContext ctx) =>
        {
            var form = await httpCtx.Request.ReadFormAsync();
            var address = form["address"].ToString();

            if(string.IsNullOrEmpty(address) || !IPAddress.TryParse(address, out var ip))
                throw new ApiException("Invalid or missing address", HttpStatusCode.BadRequest);

            var banManager = ctx.ResolveKeyed<IBanManager>(BanManagerKind.Integrated);
            var removed = banManager.Unban(ip);

            if(removed)
            {
                AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "ban.unban", address);
                logger.Info(() => $"Admin unbanned {address}");
            }

            return removed ? "Ok" : "Not banned";
        });

        // === Miner-address banning (pool-level, not IP-level - see
        // Banning.MinerAddressBanManager for current enforcement coverage) ===
        admin.MapGet("/pools/{poolId}/miner-bans", (string poolId) =>
        {
            return Banning.MinerAddressBanManager.ListBans(poolId);
        });

        admin.MapPost("/pools/{poolId}/miner-bans/ban", async (
            string poolId, HttpContext httpCtx) =>
        {
            var form = await httpCtx.Request.ReadFormAsync();
            var address = form["address"].ToString();

            if(string.IsNullOrEmpty(address))
                throw new ApiException("Missing address", HttpStatusCode.BadRequest);

            Banning.MinerAddressBanManager.Ban(poolId, address);
            AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "miner.ban", $"pool={poolId} address={address}");
            logger.Info(() => $"Admin banned miner address {address} from pool {poolId}");

            return "Ok";
        });

        admin.MapPost("/pools/{poolId}/miner-bans/unban", async (
            string poolId, HttpContext httpCtx) =>
        {
            var form = await httpCtx.Request.ReadFormAsync();
            var address = form["address"].ToString();

            var removed = Banning.MinerAddressBanManager.Unban(poolId, address);

            if(removed)
            {
                AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "miner.unban", $"pool={poolId} address={address}");
                logger.Info(() => $"Admin unbanned miner address {address} on pool {poolId}");
            }

            return removed ? "Ok" : "Not banned";
        });

        // === In-memory log viewer ===
        admin.MapGet("/logs", ([FromQuery] int count = 300) =>
        {
            return MemoryLogTarget.Recent(count == 0 ? 300 : count);
        });

        // === Admin action audit log ===
        admin.MapGet("/auditlog", ([FromQuery] int count = 200) =>
        {
            return AdminAuditLog.Recent(count == 0 ? 200 : count);
        });

        // === Run Payout Cycle Now ===
        // payoutManager is resolved by DI as the exact same SingleInstance the
        // PayoutManager BackgroundService loop runs on (see AutofacModule.cs /
        // Program.cs's AddHostedService<PayoutManager> wiring) - not a second,
        // never-started instance, which is the bug this is ported away from.
        admin.MapPost("/payouts/run", async (
            HttpContext httpCtx,
            [FromServices] PayoutManager payoutManager) =>
        {
            AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "payout.run-now", "Manual payout cycle triggered");
            logger.Info(() => "Admin triggered a manual payout cycle");

            await payoutManager.RunNowAsync(httpCtx.RequestAborted);
            return "Ok";
        });

        // === Manual balance adjustment ===
        admin.MapPost("/pools/{poolId}/miners/{address}/balance/adjust", async (
            string poolId, string address,
            HttpContext httpCtx,
            [FromServices] IConnectionFactory cf,
            [FromServices] IBalanceRepository balanceRepo) =>
        {
            var form = await httpCtx.Request.ReadFormAsync();

            if(!decimal.TryParse(form["amount"].ToString(), System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount == 0)
                throw new ApiException("Invalid or missing amount (positive to credit, negative to debit)", HttpStatusCode.BadRequest);

            var reason = form["reason"].ToString();
            if(string.IsNullOrWhiteSpace(reason))
                throw new ApiException("A reason is required for manual balance adjustments", HttpStatusCode.BadRequest);

            await cf.RunTx(async (con, tx) =>
                await balanceRepo.AddAmountAsync(con, tx, poolId, address, amount, $"Admin adjustment: {reason}", "admin-adjustment"));

            var newBalance = await cf.Run(con => balanceRepo.GetBalanceAsync(con, poolId, address));

            AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "balance.adjust",
                $"pool={poolId} address={address} amount={amount} reason=\"{reason}\" newBalance={newBalance}");
            logger.Info(() => $"Admin adjusted balance for {poolId}/{address} by {amount} ({reason}) — new balance {newBalance}");

            return new { newBalance };
        });

        admin.MapGet("/pools/{poolId}/miners/{address}/getbalance", async (
            string poolId, string address,
            [FromServices] IConnectionFactory cf,
            [FromServices] IBalanceRepository balanceRepo) =>
        {
            return await cf.Run(con => balanceRepo.GetBalanceAsync(con, poolId, address));
        });

        admin.MapGet("/pools/{poolId}/miners/{address}/settings", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IMinerRepository minerRepo,
            [FromServices] IMapper mapper) =>
        {
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            var result = await cf.Run(con => minerRepo.GetSettingsAsync(con, null, pool.Id, address));

            if (result == null)
                throw new ApiException("No settings found", HttpStatusCode.NotFound);

            return mapper.Map<Responses.MinerSettings>(result);
        });

        admin.MapPost("/pools/{poolId}/miners/{address}/settings", async (
            string poolId, string address,
            [FromBody] Responses.MinerSettings settings,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IMinerRepository minerRepo,
            [FromServices] IMapper mapper) =>
        {
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (settings == null)
                throw new ApiException("Invalid or missing settings", HttpStatusCode.BadRequest);

            var mapped = mapper.Map<Persistence.Model.MinerSettings>(settings);

            if (pool.PaymentProcessing != null)
                mapped.PaymentThreshold = Math.Max(mapped.PaymentThreshold, pool.PaymentProcessing.MinimumPayment);

            mapped.PoolId = pool.Id;
            mapped.Address = address;

            var result = await cf.RunTx(async (con, tx) =>
            {
                await minerRepo.UpdateSettingsAsync(con, tx, mapped);
                return await minerRepo.GetSettingsAsync(con, tx, mapped.PoolId, mapped.Address);
            });

            logger.Info(() => $"Updated settings for pool {pool.Id}, miner {address}");

            return mapper.Map<Responses.MinerSettings>(result);
        });

        // === Per-pool minimum-confirmations override (display only - see
        // MinConfirmationsOverride's own doc comment for exactly what this
        // does and does not affect) ===
        admin.MapGet("/pools/{poolId}/min-confirmations", (string poolId) =>
        {
            return Payments.MinConfirmationsOverride.ListAll().TryGetValue(poolId, out var value)
                ? new { poolId, overrideValue = (int?) value }
                : new { poolId, overrideValue = (int?) null };
        });

        admin.MapPost("/pools/{poolId}/min-confirmations", async (string poolId, HttpContext httpCtx) =>
        {
            var form = await httpCtx.Request.ReadFormAsync();
            var raw = form["value"].ToString();

            if(string.IsNullOrEmpty(raw))
            {
                Payments.MinConfirmationsOverride.Clear(poolId);
                AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "minconfirmations.reset", poolId);
                return "Ok (reset to configured default)";
            }

            if(!int.TryParse(raw, out var value) || value < 1)
                throw new ApiException("value must be a positive integer", HttpStatusCode.BadRequest);

            Payments.MinConfirmationsOverride.Set(poolId, value);
            AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "minconfirmations.set", $"pool={poolId} value={value}");

            return "Ok";
        });

        // === Pool-oriented alert rules (yiimp parity: CoinTriggers, redesigned - see
        // Alerts/AlertRuleManager.cs for the full design rationale) ===
        admin.MapGet("/alerts/rules", () => AlertRuleManager.ListRules());

        admin.MapPost("/alerts/rules", async (HttpContext httpCtx) =>
        {
            var form = await httpCtx.Request.ReadFormAsync();
            var poolId = form["poolId"].ToString();
            var metric = form["metric"].ToString();
            var op = form["operator"].ToString();
            var description = form["description"].ToString();

            if(string.IsNullOrEmpty(poolId) || string.IsNullOrEmpty(metric) || string.IsNullOrEmpty(op))
                throw new ApiException("poolId, metric, and operator are required", HttpStatusCode.BadRequest);

            if(!double.TryParse(form["threshold"].ToString(), out var threshold))
                throw new ApiException("threshold must be a number", HttpStatusCode.BadRequest);

            var rule = AlertRuleManager.AddRule(poolId, metric, op, threshold, description);
            AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "alertrule.add",
                $"id={rule.Id} pool={poolId} {metric} {op} {threshold}");

            return rule;
        });

        admin.MapPost("/alerts/rules/{id}/enable", (string id, HttpContext httpCtx) =>
        {
            var ok = AlertRuleManager.SetEnabled(id, true);
            if(ok) AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "alertrule.enable", id);
            return ok ? Results.Ok("Ok") : Results.NotFound();
        });

        admin.MapPost("/alerts/rules/{id}/disable", (string id, HttpContext httpCtx) =>
        {
            var ok = AlertRuleManager.SetEnabled(id, false);
            if(ok) AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "alertrule.disable", id);
            return ok ? Results.Ok("Ok") : Results.NotFound();
        });

        admin.MapPost("/alerts/rules/{id}/delete", (string id, HttpContext httpCtx) =>
        {
            var ok = AlertRuleManager.DeleteRule(id);
            if(ok) AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "alertrule.delete", id);
            return ok ? Results.Ok("Ok") : Results.NotFound();
        });

        // Re-evaluates every enabled rule against each pool's current live stats and
        // returns which are triggered right now. Rules are actually fired from
        // StatsRecorder's own periodic cycle (see Mining/StatsRecorder.cs) - calling
        // Evaluate() again here with the same current values is idempotent (no new
        // state transition, so no duplicate emails), it's just a live read for the
        // dashboard's Alerts tab and sidebar badge.
        admin.MapGet("/alerts/active", ([FromServices] ConcurrentDictionary<string, IMiningPool> pools) =>
        {
            return AlertRuleManager.Evaluate((poolId, metric) =>
            {
                if(!pools.TryGetValue(poolId, out var pool))
                    return null;

                return metric switch
                {
                    "poolHashrate" => pool.PoolStats.PoolHashrate,
                    "connectedMiners" => pool.PoolStats.ConnectedMiners,
                    "networkHashrate" => pool.NetworkStats?.NetworkHashrate,
                    "networkDifficulty" => pool.NetworkStats?.NetworkDifficulty,
                    _ => (double?) null,
                };
            });
        });

        // Backs the "Add Coin" form's algorithm dropdown - every algorithm actually
        // registered (and therefore usable) on this build, not a hardcoded list that
        // could drift from what's really available.
        admin.MapGet("/algorithms", () =>
        {
            return typeof(Program).Assembly.GetTypes()
                .Where(t => typeof(IHashAlgorithm).IsAssignableFrom(t) && !t.IsAbstract)
                .SelectMany(t => t.GetCustomAttributes(typeof(IdentifierAttribute), false).Cast<IdentifierAttribute>())
                .Select(a => a.Name)
                .OrderBy(x => x)
                .ToArray();
        });

        // === Raw daemon RPC console (like bitcoin-cli, from the browser) ===
        // Deliberately unrestricted on method name - this IS "run any RPC command against
        // this pool's daemon", same as bitcoin-cli itself. The only gate is the same
        // IP-allowlist/auth that protects the rest of /api/admin - treat that allowlist as
        // the real security boundary here, not anything in this handler.
        admin.MapPost("/pools/{poolId}/rpc", async (
            string poolId, HttpContext httpCtx,
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools) =>
        {
            if(!pools.TryGetValue(poolId, out var pool))
                return Results.NotFound();

            var form = await httpCtx.Request.ReadFormAsync();
            var method = form["method"].ToString();
            var paramsRaw = form["params"].ToString();

            if(string.IsNullOrEmpty(method))
                throw new ApiException("Missing method", HttpStatusCode.BadRequest);

            object[] parameters = null;

            if(!string.IsNullOrWhiteSpace(paramsRaw))
            {
                try
                {
                    // Accept a JSON array ([ "addr", 6 ]) for typed params, same shape the
                    // daemon's own RPC expects - NOT a comma-split string, which can't
                    // distinguish a string "6" from the number 6 and would mis-type calls
                    // like listunspent that care about that.
                    parameters = Newtonsoft.Json.Linq.JArray.Parse(paramsRaw)
                        .Select(t => (object) t).ToArray();
                }
                catch(Newtonsoft.Json.JsonException)
                {
                    throw new ApiException("params must be a JSON array, e.g. [\"some-address\", 6]", HttpStatusCode.BadRequest);
                }
            }

            AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "rpc.execute",
                $"pool={poolId} method={method} params={paramsRaw}");

            try
            {
                var result = await pool.ExecuteDaemonRpcAsync(method, parameters, httpCtx.RequestAborted);

                // Deliberately NOT Results.Json(result): result is a Newtonsoft JToken, and
                // the minimal API's default System.Text.Json serializer doesn't know how to
                // serialize JToken's internal structure - it mangles the content instead of
                // raising an error (same bug gem's own admin panel hit and fixed in commit
                // 9417396). Serialize with Newtonsoft explicitly and return as raw content.
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
                return Results.Content(json, "application/json");
            }

            catch(NotSupportedException ex)
            {
                throw new ApiException(ex.Message, HttpStatusCode.BadRequest);
            }

            catch(Exception ex)
            {
                // Surface the daemon's own error text verbatim - this is a diagnostic tool,
                // hiding the real error behind a generic 500 defeats its purpose.
                throw new ApiException(ex.Message, HttpStatusCode.BadGateway);
            }
        });

        // === Coin/pool add/edit/remove (yiimp parity) ===
        // A UI convenience over hand-editing config.json - writes the exact same file
        // ConfigHotReloadService already watches, then lets that existing mechanism do
        // the actual start/stop/restart (already verified live in this session: editing
        // config.json picks up a new pool within ~500ms, no restart, no interruption to
        // anything already running).
        admin.MapPost("/coins/add", async (
            [FromBody] Requests.AddCoinPoolRequest req, HttpContext httpCtx,
            [FromServices] ClusterConfig clusterConfig) =>
        {
            var idPattern = new Regex("^[a-z0-9][a-z0-9-]{1,40}$");

            if(string.IsNullOrEmpty(req.CoinKey) || !idPattern.IsMatch(req.CoinKey))
                throw new ApiException("Invalid coin key - use lowercase letters, digits and dashes only", HttpStatusCode.BadRequest);

            if(string.IsNullOrEmpty(req.PoolId) || !idPattern.IsMatch(req.PoolId))
                throw new ApiException("Invalid pool id - use lowercase letters, digits and dashes only", HttpStatusCode.BadRequest);

            if(string.IsNullOrWhiteSpace(req.Address))
                throw new ApiException("Payout address is required", HttpStatusCode.BadRequest);

            if(string.IsNullOrWhiteSpace(req.DaemonHost) || req.DaemonPort <= 0 || req.DaemonPort > 65535)
                throw new ApiException("A valid daemon host and port are required", HttpStatusCode.BadRequest);

            if(req.StratumPort <= 0 || req.StratumPort > 65535)
                throw new ApiException("Invalid stratum port", HttpStatusCode.BadRequest);

            if(clusterConfig.Pools.Any(p => p.Id == req.PoolId))
                throw new ApiException($"Pool id '{req.PoolId}' already exists", HttpStatusCode.Conflict);

            var usedPorts = clusterConfig.Pools.SelectMany(p => (IEnumerable<int>) p.Ports?.Keys ?? Enumerable.Empty<int>());

            if(usedPorts.Contains(req.StratumPort))
                throw new ApiException($"Stratum port {req.StratumPort} is already in use by another pool", HttpStatusCode.Conflict);

            var coinsJsonPath = Path.Combine(AppContext.BaseDirectory, "coins.json");
            var coinsJson = JObject.Parse(await File.ReadAllTextAsync(coinsJsonPath));

            if(req.UseExistingCoin)
            {
                if(string.IsNullOrEmpty(req.CoinKey) || !coinsJson.ContainsKey(req.CoinKey))
                    throw new ApiException($"Coin key '{req.CoinKey}' doesn't exist in coins.json - uncheck \"use existing coin\" to define it", HttpStatusCode.BadRequest);

                // The existing entry is authoritative for its own algorithm - test THAT,
                // not whatever the form's algorithm fields say.
                var existingHasher = (JObject) coinsJson[req.CoinKey]["headerHasher"];
                var existingAlgo = existingHasher?["hash"]?.Value<string>();
                var existingArgs = (existingHasher?["args"] as JArray)?.Select(t => Convert.ToUInt32(t)).ToArray();

                var existingAlgoError = TestAlgorithmAvailable(existingAlgo, existingArgs);
                if(existingAlgoError != null)
                    throw new ApiException(existingAlgoError, HttpStatusCode.BadRequest);

                // No coins.json write needed - the coin is already defined.
            }
            else
            {
                if(string.IsNullOrWhiteSpace(req.CoinName) || string.IsNullOrWhiteSpace(req.CoinSymbol))
                    throw new ApiException("Coin name and symbol are required", HttpStatusCode.BadRequest);

                var algoError = TestAlgorithmAvailable(req.Algorithm, req.AlgorithmArgs);
                if(algoError != null)
                    throw new ApiException(algoError, HttpStatusCode.BadRequest);

                if(coinsJson.ContainsKey(req.CoinKey))
                    throw new ApiException($"Coin key '{req.CoinKey}' already exists", HttpStatusCode.Conflict);

                var headerHasher = new JObject { ["hash"] = req.Algorithm };
                if(req.AlgorithmArgs is { Length: > 0 })
                    headerHasher["args"] = new JArray(req.AlgorithmArgs.Cast<object>());

                var coinEntry = new JObject
                {
                    ["name"] = req.CoinName,
                    ["canonicalName"] = $"{req.CoinName} ({req.Algorithm})",
                    ["symbol"] = req.CoinSymbol,
                    ["family"] = "bitcoin",
                    ["website"] = req.Website ?? "",
                    ["coinbaseHasher"] = new JObject { ["hash"] = "sha256d" },
                    ["headerHasher"] = headerHasher,
                    ["blockHasher"] = new JObject
                    {
                        ["hash"] = "reverse",
                        ["args"] = new JArray(new JObject { ["hash"] = "sha256d" }),
                    },
                    ["explorerBlockLink"] = req.ExplorerBlockLink ?? "",
                    ["explorerTxLink"] = "",
                    ["explorerAccountLink"] = "",
                };

                coinsJson[req.CoinKey] = coinEntry;

                // Plain overwrite, not write-temp-then-rename: ConfigHotReloadService's
                // FileSystemWatcher only subscribes to Changed/Created, and on Linux a
                // rename-over-existing-file surfaces as Renamed instead, which it never
                // sees.
                await File.WriteAllTextAsync(coinsJsonPath, coinsJson.ToString(Newtonsoft.Json.Formatting.Indented));
            }

            var configPath = Program.ConfigFilePath;
            var configJson = JObject.Parse(await File.ReadAllTextAsync(configPath));

            var poolEntry = new JObject
            {
                ["id"] = req.PoolId,
                ["enabled"] = true,
                ["coin"] = req.CoinKey,
                ["address"] = req.Address,
                ["rewardRecipients"] = BuildRewardRecipients(req.FeePercent, req.FeeAddress),
                ["blockRefreshInterval"] = 10000,
                ["jobRebroadcastTimeout"] = 10,
                ["clientConnectionTimeout"] = 600,
                ["banning"] = new JObject { ["enabled"] = true, ["time"] = 600, ["invalidPercent"] = 50, ["checkThreshold"] = 50 },
                ["ports"] = new JObject
                {
                    [req.StratumPort.ToString()] = new JObject
                    {
                        ["listenAddress"] = "0.0.0.0",
                        ["difficulty"] = req.Difficulty,
                        ["name"] = req.PoolId,
                        ["varDiff"] = BuildVarDiff(req.Difficulty, req.VarDiffMinDiff, req.VarDiffMaxDiff, req.VarDiffTargetTime, req.VarDiffRetargetTime, req.VarDiffVariancePercent),
                    },
                },
                ["daemons"] = new JArray(new JObject
                {
                    ["host"] = req.DaemonHost,
                    ["port"] = req.DaemonPort,
                    ["user"] = req.DaemonUser ?? "",
                    ["password"] = req.DaemonPassword ?? "",
                    ["httpPath"] = req.DaemonHttpPath ?? "",
                }),
                ["paymentProcessing"] = BuildPaymentProcessing(req.EnablePaymentProcessing, req.Solo, req.MinimumPayment, req.PplnsFactor),
            };

            ((JArray) configJson["pools"]).Add(poolEntry);

            await File.WriteAllTextAsync(configPath, configJson.ToString(Newtonsoft.Json.Formatting.Indented));

            logger.Info(() => $"Admin panel: added coin '{req.CoinKey}' and pool '{req.PoolId}' (algorithm: {req.Algorithm}, solo: {req.Solo})");
            AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "coin.add",
                $"pool={req.PoolId} coin={req.CoinKey} algorithm={req.Algorithm} solo={req.Solo} fee={req.FeePercent}%");

            return new { success = true, message = $"Pool '{req.PoolId}' added - the running cluster will pick it up automatically within a few seconds." };
        });

        // Flat shape for the admin "Coins" tab's edit form - deliberately not the full
        // PoolConfig, since the dashboard only ever edits a handful of operational
        // fields (never the coin/algorithm itself - remove and re-add for that).
        admin.MapGet("/coins/list", ([FromServices] ClusterConfig clusterConfig) =>
        {
            return clusterConfig.Pools.Select(p =>
            {
                var port = p.Ports?.FirstOrDefault();
                var daemon = p.Daemons?.FirstOrDefault();
                var varDiff = port?.Value?.VarDiff;

                return new
                {
                    poolId = p.Id,
                    coinKey = p.Coin,
                    coinName = p.Template?.Name,
                    enabled = p.Enabled,
                    address = p.Address,
                    daemonHost = daemon?.Host,
                    daemonPort = daemon?.Port ?? 0,
                    daemonUser = daemon?.User,
                    daemonHttpPath = daemon?.HttpPath,
                    stratumPort = port?.Key ?? 0,
                    difficulty = port?.Value?.Difficulty ?? 0,
                    varDiffMinDiff = varDiff?.MinDiff,
                    varDiffMaxDiff = varDiff?.MaxDiff,
                    varDiffTargetTime = varDiff?.TargetTime ?? 15,
                    varDiffRetargetTime = varDiff?.RetargetTime ?? 90,
                    varDiffVariancePercent = varDiff?.VariancePercent ?? 30,
                    enablePaymentProcessing = p.PaymentProcessing?.Enabled ?? false,
                    solo = p.PaymentProcessing?.PayoutScheme == PayoutScheme.SOLO,
                    minimumPayment = p.PaymentProcessing?.MinimumPayment ?? 0.5m,
                    feePercent = p.RewardRecipients?.Sum(r => r.Percentage) ?? 0,
                    feeAddress = p.RewardRecipients?.FirstOrDefault()?.Address,
                    explorerBlockLink = p.Template?.ExplorerBlockLink ?? "",
                };
            }).ToArray();
        });

        // Updates an existing pool's operational settings in place (address, daemon,
        // difficulty, payout mode) - the stratum port and coin/algorithm stay fixed;
        // ConfigHotReloadService restarts just this one pool once the file write lands.
        admin.MapPost("/coins/edit", async (
            [FromBody] Requests.EditPoolRequest req, HttpContext httpCtx,
            [FromServices] ClusterConfig clusterConfig) =>
        {
            if(string.IsNullOrEmpty(req.PoolId) || !clusterConfig.Pools.Any(p => p.Id == req.PoolId))
                throw new ApiException($"Pool '{req.PoolId}' doesn't exist", HttpStatusCode.NotFound);

            if(string.IsNullOrWhiteSpace(req.Address))
                throw new ApiException("Payout address is required", HttpStatusCode.BadRequest);

            if(string.IsNullOrWhiteSpace(req.DaemonHost) || req.DaemonPort <= 0 || req.DaemonPort > 65535)
                throw new ApiException("A valid daemon host and port are required", HttpStatusCode.BadRequest);

            if(req.ExplorerBlockLink != null || !string.IsNullOrWhiteSpace(req.CoinName))
            {
                var coinKey = clusterConfig.Pools.First(p => p.Id == req.PoolId).Coin;
                var coinsJsonPath = Path.Combine(AppContext.BaseDirectory, "coins.json");
                var coinsJson = JObject.Parse(await File.ReadAllTextAsync(coinsJsonPath));

                if(coinsJson[coinKey] is JObject coinEntry)
                {
                    if(req.ExplorerBlockLink != null)
                        coinEntry["explorerBlockLink"] = req.ExplorerBlockLink;

                    // Shared by every pool using this same coin key (e.g. mateablecoin-balloon
                    // and mateablecoin-whirlpool are separate coin keys but were both named
                    // "MateableCoin", which is why they appeared grouped together on the
                    // Overview/Pools tabs) - renaming here affects every pool on this coin key,
                    // not just the one being edited.
                    if(!string.IsNullOrWhiteSpace(req.CoinName))
                        coinEntry["name"] = req.CoinName;

                    await File.WriteAllTextAsync(coinsJsonPath, coinsJson.ToString(Newtonsoft.Json.Formatting.Indented));
                }
            }

            var configPath = Program.ConfigFilePath;
            var configJson = JObject.Parse(await File.ReadAllTextAsync(configPath));
            var poolsArray = (JArray) configJson["pools"];
            var poolEntry = poolsArray.Children<JObject>().FirstOrDefault(p => p["id"]?.Value<string>() == req.PoolId);

            if(poolEntry == null)
                throw new ApiException($"Pool '{req.PoolId}' not found in config file", HttpStatusCode.NotFound);

            poolEntry["enabled"] = req.Enabled;
            poolEntry["address"] = req.Address;
            poolEntry["rewardRecipients"] = BuildRewardRecipients(req.FeePercent, req.FeeAddress);

            poolEntry["daemons"] = new JArray(new JObject
            {
                ["host"] = req.DaemonHost,
                ["port"] = req.DaemonPort,
                ["user"] = req.DaemonUser ?? "",
                ["password"] = req.DaemonPassword ?? "",
                ["httpPath"] = req.DaemonHttpPath ?? "",
            });

            var portsObj = (JObject) poolEntry["ports"];
            var portKey = portsObj.Properties().First().Name;
            portsObj[portKey]["difficulty"] = req.Difficulty;
            portsObj[portKey]["varDiff"] = BuildVarDiff(req.Difficulty, req.VarDiffMinDiff, req.VarDiffMaxDiff, req.VarDiffTargetTime, req.VarDiffRetargetTime, req.VarDiffVariancePercent);

            poolEntry["paymentProcessing"] = BuildPaymentProcessing(req.EnablePaymentProcessing, req.Solo, req.MinimumPayment, req.PplnsFactor);

            await File.WriteAllTextAsync(configPath, configJson.ToString(Newtonsoft.Json.Formatting.Indented));

            logger.Info(() => $"Admin panel: edited pool '{req.PoolId}'");
            AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "coin.edit",
                $"pool={req.PoolId} address={req.Address} daemon={req.DaemonHost}:{req.DaemonPort} fee={req.FeePercent}% enabled={req.Enabled}");

            return new { success = true, message = $"Pool '{req.PoolId}' updated - restarting with the new settings." };
        });

        // Removes a pool entirely. The coin's own coins.json entry is left alone (it may
        // be a stock template other pools could reuse later) - only this specific pool's
        // config is deleted; ConfigHotReloadService stops it once the write lands.
        admin.MapPost("/coins/remove", async (
            [FromBody] Requests.RemovePoolRequest req, HttpContext httpCtx,
            [FromServices] ClusterConfig clusterConfig) =>
        {
            var poolId = req.PoolId;

            if(string.IsNullOrEmpty(poolId) || !clusterConfig.Pools.Any(p => p.Id == poolId))
                throw new ApiException($"Pool '{poolId}' doesn't exist", HttpStatusCode.NotFound);

            var configPath = Program.ConfigFilePath;
            var configJson = JObject.Parse(await File.ReadAllTextAsync(configPath));
            var poolsArray = (JArray) configJson["pools"];
            var poolEntry = poolsArray.Children<JObject>().FirstOrDefault(p => p["id"]?.Value<string>() == poolId);

            if(poolEntry != null)
                poolsArray.Remove(poolEntry);

            await File.WriteAllTextAsync(configPath, configJson.ToString(Newtonsoft.Json.Formatting.Indented));

            logger.Info(() => $"Admin panel: removed pool '{poolId}'");
            AdminAuditLog.Record(httpCtx.Connection.RemoteIpAddress?.ToString(), "coin.remove", $"pool={poolId}");

            return new { success = true, message = $"Pool '{poolId}' removed." };
        });
    }

    private static JObject BuildPaymentProcessing(bool enabled, bool solo, decimal minimumPayment, decimal pplnsFactor)
    {
        if(!enabled)
            return new JObject { ["enabled"] = false };

        var paymentProcessing = new JObject
        {
            ["enabled"] = true,
            ["minimumPayment"] = minimumPayment,
            ["payoutScheme"] = solo ? "SOLO" : "PPLNS",
        };

        paymentProcessing["payoutSchemeConfig"] = solo
            ? new JObject()
            : new JObject { ["factor"] = pplnsFactor };

        return paymentProcessing;
    }

    private static JObject BuildVarDiff(double difficulty, double? minDiff, double? maxDiff,
        double targetTime, double retargetTime, double variancePercent)
    {
        var result = new JObject
        {
            ["minDiff"] = minDiff ?? difficulty / 10,
            ["targetTime"] = targetTime,
            ["retargetTime"] = retargetTime,
            ["variancePercent"] = variancePercent,
        };

        if(maxDiff.HasValue)
            result["maxDiff"] = maxDiff.Value;

        return result;
    }

    private static JArray BuildRewardRecipients(decimal feePercent, string feeAddress)
    {
        if(feePercent <= 0 || string.IsNullOrWhiteSpace(feeAddress))
            return new JArray();

        return new JArray(new JObject
        {
            ["address"] = feeAddress,
            ["percentage"] = feePercent,
            ["type"] = "op",
        });
    }

    /// <summary>
    /// Instantiates the named algorithm and runs a real 80-byte digest through it to
    /// confirm it's both registered AND actually working on this build (native library
    /// present, not just the C# wrapper class existing) - called before writing a new
    /// coin into coins.json so a typo'd/broken algorithm name fails the form instead of
    /// silently producing an unusable coin.
    /// </summary>
    private static string TestAlgorithmAvailable(string algorithm, uint[] args = null)
    {
        if(string.IsNullOrWhiteSpace(algorithm))
            return "Algorithm is required";

        var type = typeof(Program).Assembly.GetTypes()
            .FirstOrDefault(t => typeof(IHashAlgorithm).IsAssignableFrom(t) && !t.IsAbstract &&
                t.GetCustomAttributes(typeof(IdentifierAttribute), false)
                    .Cast<IdentifierAttribute>()
                    .Any(a => string.Equals(a.Name, algorithm, StringComparison.OrdinalIgnoreCase)));

        if(type == null)
            return $"Unknown algorithm '{algorithm}'";

        object instance;

        try
        {
            instance = args is { Length: > 0 }
                ? Activator.CreateInstance(type, args.Cast<object>().ToArray())
                : Activator.CreateInstance(type);
        }

        catch(MissingMethodException)
        {
            return args is { Length: > 0 }
                ? $"Algorithm '{algorithm}' doesn't take {args.Length} numeric parameter(s) - check the count/values"
                : $"Algorithm '{algorithm}' needs parameters (see an existing coins.json entry using it for the right values)";
        }

        try
        {
            var hasher = (IHashAlgorithm) instance;
            var input = new byte[80];
            var output = new byte[32];
            hasher.Digest(input, output);
        }

        catch(Exception ex)
        {
            return $"Algorithm '{algorithm}' is registered but not working on this build (native library missing or broken): {ex.Message}";
        }

        return null;
    }

    private static void MapPoolApi(RouteGroupBuilder api)
    {
        var poolsGroup = api.MapGroup("/pools");
        var poolsGroupV2 = api.MapGroup("/v2/pools");

        poolsGroup.MapGet("/", async (
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IStatsRepository statsRepo,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IShareRepository shareRepo,
            [FromServices] IMasterClock clock,
            [FromServices] IMapper mapper,
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools,
            HttpContext httpContext,
            [FromQuery] uint topMinersRange = 24) =>
        {
            var ct = httpContext.RequestAborted;
            topMinersRange = topMinersRange == 0 ? 24 : topMinersRange;

            var response = new GetPoolsResponse
            {
                Pools = await Task.WhenAll(clusterConfig.Pools.Where(x => x.Enabled).Select(async config =>
                {
                    try
                    {
                    var stats = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, config.Id, ct));
                    pools.TryGetValue(config.Id, out var pool);
                    var result = config.ToPoolInfo(mapper, stats, pool, clusterConfig.Proxied);

                    result.TotalPaid = await cf.Run(con => statsRepo.GetTotalPoolPaymentsAsync(con, config.Id, ct));
                    result.TotalBlocks = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, config.Id, ct));
                    result.TotalConfirmedBlocks = await cf.Run(con => blocksRepo.GetTotalConfirmedBlocksAsync(con, config.Id, ct));
                    result.TotalPendingBlocks = await cf.Run(con => blocksRepo.GetTotalPendingBlocksAsync(con, config.Id, ct));
                    result.BlockReward = await cf.Run(con => blocksRepo.GetLastConfirmedBlockRewardAsync(con, config.Id, ct));
                    var lastBlockTime = await cf.Run(con => blocksRepo.GetLastPoolBlockTimeAsync(con, config.Id, ct));
                    result.LastPoolBlockTime = lastBlockTime;

                    var payoutConfig = config.PaymentProcessing;
                    if(result.PaymentProcessing != null)
                    {
                        result.PaymentProcessing.PayoutSchemeConfig = payoutConfig?.PayoutSchemeConfig?.ToObject<ApiPoolPayoutSchemeConfig>();
                        if(payoutConfig?.PayoutScheme != PayoutScheme.PPLNSBF && result.PaymentProcessing.PayoutSchemeConfig != null)
                            result.PaymentProcessing.PayoutSchemeConfig.BlockFinderPercentage = null;
                    }

                    if (lastBlockTime.HasValue && pool != null)
                    {
                        var startTime = lastBlockTime.Value;
                        var poolEffort = await cf.Run(con => shareRepo.GetEffortBetweenCreatedAsync(con, config.Id, pool.ShareMultiplier, startTime, clock.Now, ct));
                        if (poolEffort.HasValue)
                            result.PoolEffort = poolEffort.Value;
                    }

                    var from = clock.Now.AddHours(-topMinersRange);
                    var minersByHashrate = await cf.Run(con => statsRepo.PagePoolMinersByHashrateAsync(con, config.Id, from, 0, 15, ct));

                    result.TopMiners = minersByHashrate.Select(mapper.Map<MinerPerformanceStats>).ToArray();

                    return result;
                    }
                    catch(Exception ex)
                    {
                        logger.Error(() => $"Error fetching data for pool {config.Id}: {ex.Message}");
                        return null;
                    }
                }).Where(x => x != null).ToArray())
            };

            return response;
        });

        poolsGroup.MapGet("/{poolId}", async (
            string poolId,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IStatsRepository statsRepo,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IShareRepository shareRepo,
            [FromServices] IMasterClock clock,
            [FromServices] IMapper mapper,
            [FromServices] ConcurrentDictionary<string, IMiningPool> poolsDict,
            HttpContext httpContext,
            [FromQuery] uint topMinersRange = 24) =>
        {
            var ct = httpContext.RequestAborted;
            topMinersRange = topMinersRange == 0 ? 24 : topMinersRange;
            var pool = GetPool(clusterConfig, poolId);

            var stats = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, pool.Id, ct));
            poolsDict.TryGetValue(pool.Id, out var poolInstance);

            var response = new GetPoolResponse
            {
                Pool = pool.ToPoolInfo(mapper, stats, poolInstance, clusterConfig.Proxied)
            };

            response.Pool.TotalPaid = await cf.Run(con => statsRepo.GetTotalPoolPaymentsAsync(con, pool.Id, ct));
            response.Pool.TotalBlocks = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, pool.Id, ct));
            response.Pool.TotalConfirmedBlocks = await cf.Run(con => blocksRepo.GetTotalConfirmedBlocksAsync(con, pool.Id, ct));
            response.Pool.TotalPendingBlocks = await cf.Run(con => blocksRepo.GetTotalPendingBlocksAsync(con, pool.Id, ct));
            response.Pool.BlockReward = await cf.Run(con => blocksRepo.GetLastConfirmedBlockRewardAsync(con, pool.Id, ct));
            var lastBlockTime = await cf.Run(con => blocksRepo.GetLastPoolBlockTimeAsync(con, pool.Id, ct));
            response.Pool.LastPoolBlockTime = lastBlockTime;

            var payoutConfig = pool.PaymentProcessing;
            if(response.Pool.PaymentProcessing != null)
            {
                response.Pool.PaymentProcessing.PayoutSchemeConfig = payoutConfig?.PayoutSchemeConfig?.ToObject<ApiPoolPayoutSchemeConfig>();
                if(payoutConfig?.PayoutScheme != PayoutScheme.PPLNSBF && response.Pool.PaymentProcessing.PayoutSchemeConfig != null)
                    response.Pool.PaymentProcessing.PayoutSchemeConfig.BlockFinderPercentage = null;
            }

            if (lastBlockTime.HasValue)
            {
                var startTime = lastBlockTime.Value;
                var poolEffort = await cf.Run(con => shareRepo.GetEffortBetweenCreatedAsync(con, pool.Id, poolInstance.ShareMultiplier, startTime, clock.Now, ct));
                if (poolEffort.HasValue)
                    response.Pool.PoolEffort = poolEffort.Value;
            }

            var from = clock.Now.AddHours(-topMinersRange);

            response.Pool.TopMiners = (await cf.Run(con => statsRepo.PagePoolMinersByHashrateAsync(con, pool.Id, from, 0, 15, ct)))
                .Select(mapper.Map<MinerPerformanceStats>)
                .ToArray();

            return response;
        });

        poolsGroup.MapGet("/{poolId}/performance", async (
            string poolId,
            [FromQuery(Name = "r")] SampleRange? range,
            [FromQuery(Name = "i")] SampleInterval? interval,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IStatsRepository statsRepo,
            [FromServices] IMasterClock clock,
            [FromServices] IMapper mapper,
            HttpContext httpContext) =>
        {
            var ct = httpContext.RequestAborted;
            range ??= SampleRange.Day;
            interval ??= SampleInterval.Hour;
            var pool = GetPool(clusterConfig, poolId);

            var end = clock.Now;
            DateTime start;

            switch (range.Value)
            {
                case SampleRange.Day:
                    start = end.AddDays(-1);
                    break;

                case SampleRange.Month:
                    start = end.AddDays(-30);
                    break;

                default:
                    throw new ApiException("invalid interval");
            }

            var stats = await cf.Run(con => statsRepo.GetPoolPerformanceBetweenAsync(con, pool.Id, interval.Value, start, end, ct));

            var response = new GetPoolStatsResponse
            {
                Stats = stats.Select(mapper.Map<AggregatedPoolStats>).ToArray()
            };

            return response;
        });

        poolsGroup.MapGet("/{poolId}/miners", async (
            string poolId,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IStatsRepository statsRepo,
            [FromServices] IMasterClock clock,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15,
            [FromQuery] uint topMinersRange = 24) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            topMinersRange = topMinersRange == 0 ? 24 : topMinersRange;
            var pool = GetPool(clusterConfig, poolId);

            var end = clock.Now;
            var start = end.AddHours(-topMinersRange);

            var miners = (await cf.Run(con => statsRepo.PagePoolMinersByHashrateAsync(con, pool.Id, start, page, pageSize, ct)))
                .Select(mapper.Map<MinerPerformanceStats>)
                .ToArray();

            return miners;
        });

        poolsGroup.MapGet("/{poolId}/blocks", async (
            string poolId,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15,
            [FromQuery] BlockStatus[] state = null) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            var blockStates = state is { Length: > 0 } ?
                state :
                new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

            var blocks = (await cf.Run(con => blocksRepo.PageBlocksAsync(con, pool.Id, blockStates, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Block>)
                .ToArray();

            var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

            foreach (var block in blocks)
            {
                if (blockInfobaseDict != null)
                {
                    blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                    if (!string.IsNullOrEmpty(blockInfobaseUrl))
                    {
                        if (blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                        else if (blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                    }
                }
            }

            return blocks;
        });

        poolsGroupV2.MapGet("/{poolId}/blocks", async (
            string poolId,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15,
            [FromQuery] BlockStatus[] state = null) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            var blockStates = state is { Length: > 0 } ?
                state :
                new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

            uint itemCount = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, poolId, ct));
            uint pageCount = (uint)Math.Floor(itemCount / (double)pageSize);

            var blocks = (await cf.Run(con => blocksRepo.PageBlocksAsync(con, pool.Id, blockStates, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Block>)
                .ToArray();

            var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

            foreach (var block in blocks)
            {
                if (blockInfobaseDict != null)
                {
                    blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                    if (!string.IsNullOrEmpty(blockInfobaseUrl))
                    {
                        if (blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                        else if (blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                    }
                }
            }

            return new PagedResultResponse<Responses.Block[]>(blocks, itemCount, pageCount);
        });

        poolsGroup.MapGet("/{poolId}/payments", async (
            string poolId,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                    con, pool.Id, null, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Payment>)
                .ToArray();

            var txInfobaseUrl = pool.Template.ExplorerTxLink;
            var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

            foreach (var payment in payments)
            {
                if (!string.IsNullOrEmpty(txInfobaseUrl))
                    payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

                if (!string.IsNullOrEmpty(addressInfobaseUrl))
                    payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
            }

            return payments;
        });

        poolsGroupV2.MapGet("/{poolId}/payments", async (
            string poolId,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            uint itemCount = await cf.Run(con => paymentsRepo.GetPaymentsCountAsync(con, poolId, null, ct));
            uint pageCount = (uint)Math.Floor(itemCount / (double)pageSize);

            var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                    con, pool.Id, null, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Payment>)
                .ToArray();

            var txInfobaseUrl = pool.Template.ExplorerTxLink;
            var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

            foreach (var payment in payments)
            {
                if (!string.IsNullOrEmpty(txInfobaseUrl))
                    payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

                if (!string.IsNullOrEmpty(addressInfobaseUrl))
                    payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
            }

            return new PagedResultResponse<Responses.Payment[]>(payments, itemCount, pageCount);
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IStatsRepository statsRepo,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IShareRepository shareRepo,
            [FromServices] IMasterClock clock,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] SampleRange? perfMode = null) =>
        {
            var ct = httpContext.RequestAborted;
            perfMode ??= SampleRange.Day;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var statsResult = await cf.RunTx((con, tx) =>
                statsRepo.GetMinerStatsAsync(con, tx, pool.Id, address, ct), true, IsolationLevel.Serializable);

            Responses.MinerStats stats = null;

            if (statsResult != null)
            {
                stats = mapper.Map<Responses.MinerStats>(statsResult);

                if (pool.Template.Family == CoinFamily.Bitcoin)
                    stats.PendingShares *= pool.Template.As<BitcoinTemplate>().ShareMultiplier;

                if (statsResult.LastPayment != null)
                {
                    stats.LastPayment = statsResult.LastPayment.Created;
                    var baseUrl = pool.Template.ExplorerTxLink;
                    if (!string.IsNullOrEmpty(baseUrl))
                        stats.LastPaymentLink = string.Format(baseUrl, statsResult.LastPayment.TransactionConfirmationData);
                }

                var lastBlockTime = await cf.Run(con => blocksRepo.GetLastPoolBlockTimeAsync(con, pool.Id, ct));
                if (lastBlockTime.HasValue)
                {
                    var startTime = lastBlockTime.Value;
                    var minerEffort = await cf.Run(con => shareRepo.GetMinerEffortBetweenCreatedAsync(con, pool.Id, address, startTime, clock.Now, ct));
                    if (minerEffort.HasValue)
                        stats.MinerEffort = minerEffort.Value;
                }

                stats.PerformanceSamples = await GetMinerPerformanceInternal(cf, statsRepo, clock, mapper, perfMode.Value, pool, address, ct);

                stats.TotalConfirmedBlocks = await cf.Run(con => statsRepo.GetMinerTotalConfirmedBlocksAsync(con, pool.Id, address, ct));
                stats.TotalPendingBlocks = await cf.Run(con => statsRepo.GetMinerTotalPendingBlocksAsync(con, pool.Id, address, ct));
            }

            return stats;
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}/blocks", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15,
            [FromQuery] BlockStatus[] state = null) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var blockStates = state is { Length: > 0 } ?
                state :
                new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

            var blocks = (await cf.Run(con => blocksRepo.PageMinerBlocksAsync(con, pool.Id, address, blockStates, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Block>)
                .ToArray();

            var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

            foreach (var block in blocks)
            {
                if (blockInfobaseDict != null)
                {
                    blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                    if (!string.IsNullOrEmpty(blockInfobaseUrl))
                    {
                        if (blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                        else if (blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                    }
                }
            }

            return blocks;
        });

        poolsGroupV2.MapGet("/{poolId}/miners/{address}/blocks", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15,
            [FromQuery] BlockStatus[] state = null) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var blockStates = state is { Length: > 0 } ?
                state :
                new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

            uint itemCount = await cf.Run(con => blocksRepo.GetMinerBlockCountAsync(con, poolId, address, ct));
            uint pageCount = (uint)Math.Floor(itemCount / (double)pageSize);

            var blocks = (await cf.Run(con => blocksRepo.PageMinerBlocksAsync(con, pool.Id, address, blockStates, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Block>)
                .ToArray();

            var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

            foreach (var block in blocks)
            {
                if (blockInfobaseDict != null)
                {
                    blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                    if (!string.IsNullOrEmpty(blockInfobaseUrl))
                    {
                        if (blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                        else if (blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                    }
                }
            }

            return new PagedResultResponse<Responses.Block[]>(blocks, itemCount, pageCount);
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}/payments", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                    con, pool.Id, address, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Payment>)
                .ToArray();

            var txInfobaseUrl = pool.Template.ExplorerTxLink;
            var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

            foreach (var payment in payments)
            {
                if (!string.IsNullOrEmpty(txInfobaseUrl))
                    payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

                if (!string.IsNullOrEmpty(addressInfobaseUrl))
                    payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
            }

            return payments;
        });

        poolsGroupV2.MapGet("/{poolId}/miners/{address}/payments", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            uint itemCount = await cf.Run(con => paymentsRepo.GetPaymentsCountAsync(con, poolId, address, ct));
            uint pageCount = (uint)Math.Floor(itemCount / (double)pageSize);

            var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                    con, pool.Id, address, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Payment>)
                .ToArray();

            var txInfobaseUrl = pool.Template.ExplorerTxLink;
            var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

            foreach (var payment in payments)
            {
                if (!string.IsNullOrEmpty(txInfobaseUrl))
                    payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

                if (!string.IsNullOrEmpty(addressInfobaseUrl))
                    payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
            }

            return new PagedResultResponse<Responses.Payment[]>(payments, itemCount, pageCount);
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}/balancechanges", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var balanceChanges = (await cf.Run(con => paymentsRepo.PageBalanceChangesAsync(
                    con, pool.Id, address, page, pageSize, ct)))
                .Select(mapper.Map<Responses.BalanceChange>)
                .ToArray();

            return balanceChanges;
        });

        poolsGroupV2.MapGet("/{poolId}/miners/{address}/balancechanges", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            uint itemCount = await cf.Run(con => paymentsRepo.GetBalanceChangesCountAsync(con, poolId, address));
            uint pageCount = (uint)Math.Floor(itemCount / (double)pageSize);

            var balanceChanges = (await cf.Run(con => paymentsRepo.PageBalanceChangesAsync(
                    con, pool.Id, address, page, pageSize, ct)))
                .Select(mapper.Map<Responses.BalanceChange>)
                .ToArray();

            return new PagedResultResponse<Responses.BalanceChange[]>(balanceChanges, itemCount, pageCount);
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}/earnings/daily", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var earnings = (await cf.Run(con => paymentsRepo.PageMinerPaymentsByDayAsync(
                    con, pool.Id, address, page, pageSize, ct)))
                .ToArray();

            return earnings;
        });

        poolsGroupV2.MapGet("/{poolId}/miners/{address}/earnings/daily", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            uint itemCount = await cf.Run(con => paymentsRepo.GetMinerPaymentsByDayCountAsync(con, poolId, address));
            uint pageCount = (uint)Math.Floor(itemCount / (double)pageSize);

            var earnings = (await cf.Run(con => paymentsRepo.PageMinerPaymentsByDayAsync(
                    con, pool.Id, address, page, pageSize, ct)))
                .ToArray();

            return new PagedResultResponse<AmountByDate[]>(earnings, itemCount, pageCount);
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}/performance", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IStatsRepository statsRepo,
            [FromServices] IMasterClock clock,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] SampleRange? mode = null) =>
        {
            var ct = httpContext.RequestAborted;
            mode ??= SampleRange.Day;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var result = await GetMinerPerformanceInternal(cf, statsRepo, clock, mapper, mode.Value, pool, address, ct);
            return result;
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}/settings", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IMinerRepository minerRepo,
            [FromServices] IMapper mapper) =>
        {
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var result = await cf.Run(con => minerRepo.GetSettingsAsync(con, null, pool.Id, address));

            if (result == null)
                throw new ApiException("No settings found", HttpStatusCode.NotFound);

            return mapper.Map<Responses.MinerSettings>(result);
        });

        poolsGroup.MapPost("/{poolId}/miners/{address}/settings", async (
            string poolId, string address,
            [FromBody] Requests.UpdateMinerSettingsRequest request,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IShareRepository shareRepo,
            [FromServices] IMinerRepository minerRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext) =>
        {
            var ct = httpContext.RequestAborted;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            if (request?.Settings == null)
                throw new ApiException("Invalid or missing settings", HttpStatusCode.BadRequest);

            if (!IPAddress.TryParse(request.IpAddress, out var requestIp))
                throw new ApiException("Invalid IP address", HttpStatusCode.BadRequest);

            var ips = await cf.Run(con => shareRepo.GetRecentyUsedIpAddressesAsync(con, null, poolId, address, ct));

            if (ips == null || ips.Length == 0)
                throw new ApiException("Address not recently used for mining", HttpStatusCode.NotFound);

            if (!ips.Any(x => IPAddress.TryParse(x, out var ipAddress) && ipAddress.IsEqual(requestIp)))
                throw new ApiException("None of the recently used IP addresses matches the request", HttpStatusCode.Forbidden);

            var mapped = mapper.Map<Persistence.Model.MinerSettings>(request.Settings);

            if (pool.PaymentProcessing != null)
                mapped.PaymentThreshold = Math.Max(mapped.PaymentThreshold, pool.PaymentProcessing.MinimumPayment);

            mapped.PoolId = pool.Id;
            mapped.Address = address;

            return await cf.RunTx(async (con, tx) =>
            {
                await minerRepo.UpdateSettingsAsync(con, tx, mapped);
                logger.Info(() => $"Updated settings for pool {pool.Id}, miner {address}");
                var result = await minerRepo.GetSettingsAsync(con, tx, mapped.PoolId, mapped.Address);
                return mapper.Map<Responses.MinerSettings>(result);
            });
        });
    }

    private static async Task<Responses.WorkerPerformanceStatsContainer[]> GetMinerPerformanceInternal(
        IConnectionFactory cf, IStatsRepository statsRepo, IMasterClock clock, IMapper mapper,
        SampleRange mode, PoolConfig pool, string address, CancellationToken ct)
    {
        Persistence.Model.Projections.WorkerPerformanceStatsContainer[] stats = null;
        var end = clock.Now;
        DateTime start;

        switch (mode)
        {
            case SampleRange.Hour:
                end = end.AddSeconds(-end.Second);
                start = end.AddHours(-1);
                stats = await cf.Run(con => statsRepo.GetMinerPerformanceBetweenThreeMinutelyAsync(con, pool.Id, address, start, end, ct));
                break;

            case SampleRange.Day:
                if (end.Minute < 30) end = end.AddHours(-1);
                end = end.AddMinutes(-end.Minute);
                end = end.AddSeconds(-end.Second);
                start = end.AddDays(-1);
                stats = await cf.Run(con => statsRepo.GetMinerPerformanceBetweenHourlyAsync(con, pool.Id, address, start, end, ct));
                break;

            case SampleRange.Month:
                if (end.Hour < 12) end = end.AddDays(-1);
                end = end.Date;
                start = end.AddMonths(-1);
                stats = await cf.Run(con => statsRepo.GetMinerPerformanceBetweenDailyAsync(con, pool.Id, address, start, end, ct));
                break;
        }

        var result = mapper.Map<Responses.WorkerPerformanceStatsContainer[]>(stats);
        return result;
    }

    private static PoolConfig GetPoolNoThrow(ClusterConfig clusterConfig, string poolId)
    {
        if (string.IsNullOrEmpty(poolId))
            return null;

        var pool = clusterConfig.Pools.FirstOrDefault(x => x.Id == poolId && x.Enabled);
        return pool;
    }

    private static PoolConfig GetPool(ClusterConfig clusterConfig, string poolId)
    {
        if (string.IsNullOrEmpty(poolId))
            throw new ApiException("Invalid pool id", HttpStatusCode.NotFound);

        var pool = clusterConfig.Pools.FirstOrDefault(x => x.Id == poolId && x.Enabled);

        if (pool == null)
            throw new ApiException($"Unknown pool {poolId}", HttpStatusCode.NotFound);

        return pool;
    }
}

using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Handshake;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using NLog;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Handshake;

[CoinFamily(CoinFamily.Handshake)]
public class HandshakePool : PoolBase
{
    public HandshakePool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMapper mapper,
        IMasterClock clock,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService nicehashService) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, rmsm, nicehashService)
    {
    }

    private object currentJobParams;
    private HandshakeJobManager manager;
    private BitcoinTemplate coin;
    
    protected virtual async Task OnAuthorizeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        var requestParams = request.ParamsAs<string[]>();

        if(requestParams?.Length < 1)
            throw new StratumException(StratumError.Other, "invalid params");

        var workerValue = requestParams?.Length > 0 ? requestParams[0] : null;
        var password = requestParams?.Length > 1 ? requestParams[1] : null;
        var passParts = password?.Split(PasswordControlVarsSeparator);

        // extract worker/miner
        var split = workerValue?.Split('.');
        var minerName = split?.FirstOrDefault()?.Trim();
        var workerName = split?.Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;

        // assumes that minerName is an address
        context.IsAuthorized = await manager.ValidateAddressAsync(minerName, ct);
        context.Miner = minerName;
        context.Worker = workerName;

        if(context.IsAuthorized)
        {
            // respond
            await connection.RespondAsync(context.IsAuthorized, request.Id);

            // log association
            logger.Info(() => $"[{connection.ConnectionId}] Authorized worker {workerValue}");

            // extract control vars from password
            var staticDiff = GetStaticDiffFromPassparts(passParts);

            // Static diff
            if(staticDiff.HasValue &&
               (context.VarDiff != null && staticDiff.Value >= context.VarDiff.Config.MinDiff ||
                   context.VarDiff == null && staticDiff.Value > context.Difficulty))
            {
                context.VarDiff = null; // disable vardiff
                context.SetDifficulty(staticDiff.Value);

                logger.Info(() => $"[{connection.ConnectionId}] Setting static difficulty of {staticDiff.Value}");

                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
            }
        }

        else
        {
            await connection.RespondErrorAsync(StratumError.UnauthorizedWorker, "Authorization failed", request.Id, context.IsAuthorized);

            if(clusterConfig?.Banning?.BanOnLoginFailure is null or true)
            {
                // issue short-time ban if unauthorized to prevent DDos on daemon (validateaddress RPC)
                logger.Info(() => $"[{connection.ConnectionId}] Banning unauthorized worker {minerName} for {loginFailureBanTimeout.TotalSeconds} sec");

                banManager.Ban(connection.RemoteEndpoint.Address, loginFailureBanTimeout);

                Disconnect(connection);
            }
        }
    }

    protected virtual async Task OnSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        
        var requestParams = request.ParamsAs<string[]>();

        if(requestParams?.Length < 1)
            throw new StratumException(StratumError.Other, "invalid params");

        // The following is weird but for some unknown reason all the other pools do the same in order to carry all these Goldshell miners with their own vision of JSON-RPC
        var subscriberData = manager.GetSubscriberData(connection);

        var data = new object[]
        {
            new object[]
            {
                new object[] { BitcoinStratumMethods.MiningNotify, subscriberData[0] },
                new object[] { BitcoinStratumMethods.SetDifficulty, subscriberData[0] }
            }
        }
        .Concat(subscriberData)
        .ToArray();

        await connection.RespondAsync(data, request.Id);

        // setup worker context
        context.IsSubscribed = true;
        context.UserAgent = requestParams.FirstOrDefault()?.Trim();

        // Nicehash support
        var nicehashDiff = await GetNicehashStaticMinDiff(context, coin.Name, coin.GetAlgorithmName());

        if(nicehashDiff.HasValue)
        {
            logger.Info(() => $"[{connection.ConnectionId}] Nicehash detected. Using API supplied difficulty of {nicehashDiff.Value}");

            context.VarDiff = null; // disable vardiff
            context.SetDifficulty(nicehashDiff.Value);
        }

        // send intial update
        await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
        await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
    }

    protected virtual async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        try
        {
            if(request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            // check age of submission (aged submissions are usually caused by high server load)
            var requestAge = clock.Now - tsRequest.Timestamp.UtcDateTime;

            if(requestAge > maxShareAge)
            {
                logger.Warn(() => $"[{connection.ConnectionId}] Dropping stale share submission request (server overloaded?)");
                return;
            }

            // check worker state
            context.LastActivity = clock.Now;

            // validate worker
            if(!context.IsAuthorized)
                throw new StratumException(StratumError.UnauthorizedWorker, "unauthorized worker");
            else if(!context.IsSubscribed)
                throw new StratumException(StratumError.NotSubscribed, "not subscribed");

            var requestParams = request.ParamsAs<string[]>();

            // submit
            var share = await manager.SubmitShareAsync(connection, requestParams, ct);
            await connection.RespondAsync(true, request.Id);

            // publish
            messageBus.SendMessage(share);

            // telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true);

            logger.Info(() => $"[{connection.ConnectionId}] Share accepted: D={Math.Round(share.Difficulty * coin.ShareMultiplier, 3)}");

            // update pool stats
            if(share.IsBlockCandidate)
                poolStats.LastPoolBlockTime = clock.Now;

            // update client stats
            context.Stats.ValidShares++;

            await UpdateVarDiffAsync(connection, false, ct);
        }

        catch(StratumException ex)
        {
            // telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);

            // update client stats
            context.Stats.InvalidShares++;
            logger.Info(() => $"[{connection.ConnectionId}] Share rejected: {ex.Message} [{context.UserAgent}]");

            // banning
            ConsiderBan(connection, context, poolConfig.Banning);

            throw;
        }
    }

    protected virtual async Task OnGetTransactionsAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        try
        {
            if(request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");
            
            // check worker state
            context.LastActivity = clock.Now;

            // validate worker
            if(!context.IsAuthorized)
                throw new StratumException(StratumError.UnauthorizedWorker, "unauthorized worker");
            else if(!context.IsSubscribed)
                throw new StratumException(StratumError.NotSubscribed, "not subscribed");

            var requestParams = request.ParamsAs<string[]>();

            // get transactions
            var transactions = manager.GetTransactions(connection, requestParams);
            await connection.RespondAsync(transactions, request.Id);
        }

        catch(StratumException ex)
        {
            logger.Info(() => $"[{connection.ConnectionId}] GetTransactions failed: {ex.Message} [{context.UserAgent}]");

            throw;
        }
    }

    protected virtual async Task OnNewJobAsync(object jobParams)
    {
        currentJobParams = jobParams;

        logger.Info(() => $"Broadcasting job {((object[]) jobParams)[0]}");

        await Guard(() => ForEachMinerAsync(async (connection, ct) =>
        {
            var context = connection.ContextAs<BitcoinWorkerContext>();

            // varDiff: if the client has a pending difficulty change, apply it now
            if(context.ApplyPendingDifficulty())
                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });

            // send job
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
        }));
    }

    public override double HashrateFromShares(double shares, double interval)
    {
        var multiplier = BitcoinConstants.Pow2x32;
        var result = shares * multiplier / interval;

        if(coin.HashrateMultiplier.HasValue)
            result *= coin.HashrateMultiplier.Value;

        return result;
    }

    public override double ShareMultiplier => coin.ShareMultiplier;

    #region Overrides

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<BitcoinTemplate>();

        base.Configure(pc, cc);
    }

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        manager = ctx.Resolve<HandshakeJobManager>(
            new TypedParameter(typeof(IExtraNonceProvider), new BitcoinExtraNonceProvider(poolConfig.Id, clusterConfig.InstanceId)));

        manager.Configure(poolConfig, clusterConfig);

        await manager.StartAsync(ct);

        if(poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(job => Observable.FromAsync(() =>
                    Guard(()=> OnNewJobAsync(job),
                        ex=> logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"))))
                .Concat()
                .Subscribe(_ => { }, ex =>
                {
                    logger.Debug(ex, nameof(OnNewJobAsync));
                }));

            // start with initial blocktemplate
            await manager.Jobs.Take(1).ToTask(ct);
        }

        else
        {
            // keep updating NetworkStats
            disposables.Add(manager.Jobs.Subscribe());
        }
    }

    protected override async Task InitStatsAsync(CancellationToken ct)
    {
        await base.InitStatsAsync(ct);

        blockchainStats = manager.BlockchainStats;
    }

    protected override WorkerContextBase CreateWorkerContext()
    {
        return new BitcoinWorkerContext();
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;

        try
        {
            switch(request.Method)
            {
                case BitcoinStratumMethods.Authorize:
                    await OnAuthorizeAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.Subscribe:
                    await OnSubscribeAsync(connection, tsRequest);
                    break;

                case BitcoinStratumMethods.SubmitShare:
                    await OnSubmitAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.GetTransactions:
                    await OnGetTransactionsAsync(connection, tsRequest, ct);
                    break;

                default:
                    logger.Debug(() => $"[{connection.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                    await connection.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                    break;
            }
        }

        catch(StratumException ex)
        {
            await connection.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
        }
    }

    protected override async Task OnVarDiffUpdateAsync(StratumConnection connection, double newDiff, CancellationToken ct)
    {
        await base.OnVarDiffUpdateAsync(connection, newDiff, ct);

        if(connection.Context.ApplyPendingDifficulty())
        {
            await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { connection.Context.Difficulty });
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
        }
    }

    #endregion // Overrides
}
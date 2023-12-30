using System;
using static System.Array;
using System.Globalization;
using System.Net.Http;
using System.Numerics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autofac;
using Grpc.Core;
using Grpc.Net.Client;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Blockchain.Kaspa.Custom.Karlsencoin;
using Miningcore.Blockchain.Kaspa.Custom.Pyrin;
using NLog;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;
using kaspaWalletd = Miningcore.Blockchain.Kaspa.KaspaWalletd;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa;

public class KaspaJobManager : JobManagerBase<KaspaJob>
{
    public KaspaJobManager(
        IComponentContext ctx,
        IMessageBus messageBus,
        IMasterClock clock,
        IHttpClientFactory httpClientFactory,
        IExtraNonceProvider extraNonceProvider) :
        base(ctx, messageBus)
    {
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(extraNonceProvider);

        this.clock = clock;
        this.httpClientFactory = httpClientFactory;
        this.extraNonceProvider = extraNonceProvider;
    }
    
    private DaemonEndpointConfig[] daemonEndpoints;
    private DaemonEndpointConfig[] walletDaemonEndpoints;
    private KaspaCoinTemplate coin;
    private IHttpClientFactory httpClientFactory;
    private kaspad.KaspadRPC.KaspadRPCClient rpc;
    private kaspaWalletd.KaspaWalletdRPC.KaspaWalletdRPCClient walletRpc;
    private string network;
    private readonly List<KaspaJob> validJobs = new();
    private readonly IExtraNonceProvider extraNonceProvider;
    private readonly IMasterClock clock;
    private KaspaPoolConfigExtra extraPoolConfig;
    private KaspaPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;
    protected int maxActiveJobs;
    protected string extraData;
    
    protected IObservable<kaspad.RpcBlock> KaspaSubscribeNewBlockTemplate(CancellationToken ct, object payload = null,
        JsonSerializerSettings payloadJsonSerializerSettings = null)
    {
        return Observable.Defer(() => Observable.Create<kaspad.RpcBlock>(obs =>
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            Task.Run(async () =>
            {
                using(cts)
                {
                    retry:
                        try
                        {
                            // we need a stream to communicate with Kaspad
                            var streamNotifyNewBlockTemplate = rpc.MessageStream(null, null, cts.Token);
                            
                            var requestNotifyNewBlockTemplate = new kaspad.KaspadMessage();
                            requestNotifyNewBlockTemplate.NotifyNewBlockTemplateRequest = new kaspad.NotifyNewBlockTemplateRequestMessage();
                            logger.Debug(() => $"Sending NotifyNewBlockTemplateRequest");
                            await streamNotifyNewBlockTemplate.RequestStream.WriteAsync(requestNotifyNewBlockTemplate);
                            while (!cts.IsCancellationRequested && await streamNotifyNewBlockTemplate.ResponseStream.MoveNext())
                            {
                                if(streamNotifyNewBlockTemplate.ResponseStream.Current.NewBlockTemplateNotification != null)
                                {
                                    logger.Debug(() => $"New job received :D");
                                    
                                    var streamBlockTemplate = rpc.MessageStream(null, null, cts.Token);
                                    var requestBlockTemplate = new kaspad.KaspadMessage();
                                    requestBlockTemplate.GetBlockTemplateRequest = new kaspad.GetBlockTemplateRequestMessage
                                    {
                                        PayAddress = poolConfig.Address,
                                        ExtraData = extraData,
                                    };
                                    await streamBlockTemplate.RequestStream.WriteAsync(requestBlockTemplate);
                                    await foreach (var responseBlockTemplate in streamBlockTemplate.ResponseStream.ReadAllAsync())
                                    {
                                        if(string.IsNullOrEmpty(responseBlockTemplate.GetBlockTemplateResponse.Error?.Message))
                                        {
                                            logger.Debug(() => $"DaaScore (BlockHeight): {responseBlockTemplate.GetBlockTemplateResponse.Block.Header.DaaScore}");

                                            // publish
                                            //logger.Debug(() => $"Publishing...");
                                            obs.OnNext(responseBlockTemplate.GetBlockTemplateResponse.Block);
                                        }
                                        else
                                            logger.Warn(() => responseBlockTemplate.GetBlockTemplateResponse.Error?.Message);
                                        
                                        break;
                                    }
                                    await streamBlockTemplate.RequestStream.CompleteAsync();
                                }
                            }
                            logger.Debug(() => $"No more data received. Bye!");
                            await streamNotifyNewBlockTemplate.RequestStream.CompleteAsync();
                        }

                        catch(OperationCanceledException)
                        {
                            // ignored
                        }

                        catch(Exception ex)
                        {
                            logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while streaming kaspad \"NewBlockTemplate\" notifications. Reconnecting in 10s");
                        }
                        
                        if(!cts.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                            goto retry;
                        }
                }
            }, cts.Token);
            
            return Disposable.Create(() => { cts.Cancel(); });
        }));
    }
    
    private void SetupJobUpdates(CancellationToken ct)
    {
        var blockFound = blockFoundSubject.Synchronize();
        var pollTimerRestart = blockFoundSubject.Synchronize();

        var triggers = new List<IObservable<(string Via, kaspad.RpcBlock Data)>>
        {
            blockFound.Select(_ => (JobRefreshBy.BlockFound, (kaspad.RpcBlock) null))
        };

        // Listen to kaspad "NewBlockTemplate" notifications
        var getWorkKaspad = KaspaSubscribeNewBlockTemplate(ct)
            .Publish()
            .RefCount();
            
        triggers.Add(getWorkKaspad
            .Select(blockTemplate => (JobRefreshBy.BlockTemplateStream, blockTemplate))
            .Publish()
            .RefCount());

        // get initial blocktemplate
        triggers.Add(Observable.Interval(TimeSpan.FromMilliseconds(1000))
            .Select(_ => (JobRefreshBy.Initial, (kaspad.RpcBlock) null))
            .TakeWhile(_ => !hasInitialBlockTemplate));

        Jobs = triggers.Merge()
            .Select(x => Observable.FromAsync(() => UpdateJob(ct, x.Via, x.Data)))
            .Concat()
            .Where(x => x)
            .Do(x =>
            {
                if(x)
                    hasInitialBlockTemplate = true;
            })
            .Select(x => GetJobParamsForStratum())
            .Publish()
            .RefCount();
    }
    
    private KaspaJob CreateJob(long blockHeight)
    {
        switch(coin.Symbol)
        {
            case "KLS":
                return new KarlsencoinJob();

            case "PYI":
                if(blockHeight >= PyrinConstants.Blake3ForkHeight)
                    logger.Debug(() => $"blake3HardFork activated");

                return new PyrinJob(blockHeight);
        }

        return new KaspaJob();
    }

    private async Task<bool> UpdateJob(CancellationToken ct, string via = null, kaspad.RpcBlock blockTemplate = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        return await Task.Run(() =>
        {
            using(cts)
            {
                try
                {
                    if(blockTemplate == null)
                        return false;

                    var job = currentJob;

                    var isNew = (job == null || job.BlockTemplate?.Header.DaaScore < blockTemplate.Header.DaaScore);

                    if(isNew)
                        messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Header.DaaScore, poolConfig.Template);

                    if(isNew)
                    {
                        job = CreateJob((long) blockTemplate.Header.DaaScore);

                        job.Init(blockTemplate, NextJobId());

                        lock(jobLock)
                        {
                            validJobs.Insert(0, job);

                            // trim active jobs
                            while(validJobs.Count > maxActiveJobs)
                                validJobs.RemoveAt(validJobs.Count - 1);
                        }
                        
                        logger.Debug(() => $"blockTargetValue: {job.blockTargetValue}");
                        logger.Debug(() => $"Difficulty: {job.Difficulty}");
                        
                        if(via != null)
                            logger.Info(() => $"Detected new block {job.BlockTemplate.Header.DaaScore} [{via}]");
                        else
                            logger.Info(() => $"Detected new block {job.BlockTemplate.Header.DaaScore}");

                        // update stats
                        if (job.BlockTemplate.Header.DaaScore > BlockchainStats.BlockHeight)
                        {
                            // update stats
                            BlockchainStats.LastNetworkBlockTime = clock.Now;
                            BlockchainStats.BlockHeight = job.BlockTemplate.Header.DaaScore;
                            BlockchainStats.NetworkDifficulty = job.Difficulty;
                        }
                        
                        currentJob = job;
                    }
                    else
                    {
                        if(via != null)
                            logger.Debug(() => $"Template update {job.BlockTemplate.Header.DaaScore}");
                        else
                            logger.Debug(() => $"Template update {job.BlockTemplate.Header.DaaScore}");
                    }

                    return isNew;
                }

                catch(OperationCanceledException)
                {
                    // ignored
                }

                catch(Exception ex)
                {
                    logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while updating new job");
                }

                return false;
            }
        }, cts.Token);
    }
    
    private async Task UpdateNetworkStatsAsync(CancellationToken ct)
    {
        try
        {
            // update stats
            // we need a stream to communicate with Kaspad
            var stream = rpc.MessageStream(null, null, ct);

            var request = new kaspad.KaspadMessage();
            request.EstimateNetworkHashesPerSecondRequest = new kaspad.EstimateNetworkHashesPerSecondRequestMessage
            {
                WindowSize = 1000,
            };
            await stream.RequestStream.WriteAsync(request);
            await foreach (var infoHashrate in stream.ResponseStream.ReadAllAsync())
            {
                if(string.IsNullOrEmpty(infoHashrate.EstimateNetworkHashesPerSecondResponse.Error?.Message))
                    BlockchainStats.NetworkHashrate = (double) infoHashrate.EstimateNetworkHashesPerSecondResponse.NetworkHashesPerSecond;
                
                break;
            }
            await stream.RequestStream.CompleteAsync();
            
            // we need a stream to communicate with Kaspad
            stream = rpc.MessageStream(null, null, ct);

            request = new kaspad.KaspadMessage();
            request.GetConnectedPeerInfoRequest = new kaspad.GetConnectedPeerInfoRequestMessage();
            await stream.RequestStream.WriteAsync(request);
            await foreach (var info in stream.ResponseStream.ReadAllAsync())
            {
                if(string.IsNullOrEmpty(info.GetConnectedPeerInfoResponse.Error?.Message))
                    BlockchainStats.ConnectedPeers = info.GetConnectedPeerInfoResponse.Infos.Count;
                
                break;
            }
            await stream.RequestStream.CompleteAsync();
        }

        catch(Exception ex)
        {
            logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while updating network stats");
        }
    }

    private async Task ShowDaemonSyncProgressAsync(CancellationToken ct)
    {
        // we need a stream to communicate with Kaspad
        var stream = rpc.MessageStream(null, null, ct);

        var request = new kaspad.KaspadMessage();
        request.GetInfoRequest = new kaspad.GetInfoRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex=> logger.Debug(ex));
        await foreach (var info in stream.ResponseStream.ReadAllAsync())
        {
            if(!string.IsNullOrEmpty(info.GetInfoResponse.Error?.Message))
                logger.Debug(info.GetInfoResponse.Error?.Message);

            if(info.GetInfoResponse.IsSynced != true && info.GetInfoResponse.IsUtxoIndexed != true)
                logger.Info(() => $"Daemon is downloading headers ...");
            
            break;
        }
        await stream.RequestStream.CompleteAsync();
    }

    private async Task<bool> SubmitBlockAsync(CancellationToken ct, kaspad.RpcBlock block, object payload = null,
        JsonSerializerSettings payloadJsonSerializerSettings = null)
    {
        Contract.RequiresNonNull(block);
        
        bool succeed = false;

        try
        {
            // we need a stream to communicate with Kaspad
            var stream = rpc.MessageStream(null, null, ct);
            
            var request = new kaspad.KaspadMessage();
            request.SubmitBlockRequest = new kaspad.SubmitBlockRequestMessage
            {
                Block = block,
                AllowNonDAABlocks = false,
            };
            await stream.RequestStream.WriteAsync(request);
            await foreach (var response in stream.ResponseStream.ReadAllAsync())
            {
                if(!string.IsNullOrEmpty(response.SubmitBlockResponse.Error?.Message))
                {
                    // We lost that battle
                    logger.Warn(() => $"Block submission failed: {response.SubmitBlockResponse.Error?.Message} [{response.SubmitBlockResponse?.RejectReason.ToString()}]");
                    messageBus.SendMessage(new AdminNotification("Block submission failed", $"Pool {poolConfig.Id}: {response.SubmitBlockResponse.Error?.Message} [{response.SubmitBlockResponse?.RejectReason.ToString()}]"));
                }
                else
                    succeed = true;

                break;
            }
            await stream.RequestStream.CompleteAsync();
        }
        
        catch(Exception ex)
        {
            // We lost that battle
            logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while submitting block");
            messageBus.SendMessage(new AdminNotification("Block submission failed", $"Pool {poolConfig.Id} failed to submit block"));
        }
        
        return succeed;
    }

    #region API-Surface

    public IObservable<object[]> Jobs { get; private set; }
    public BlockchainStats BlockchainStats { get; } = new();
    public string Network => network;

    public KaspaCoinTemplate Coin => coin;

    public object[] GetSubscriberData(StratumConnection worker)
    {
        Contract.RequiresNonNull(worker);

        var context = worker.ContextAs<KaspaWorkerContext>();
        var extraNonce1Size = GetExtraNonce1Size();

        // assign unique ExtraNonce1 to worker (miner)
        context.ExtraNonce1 = extraNonceProvider.Next();

        // setup response data
        var responseData = new object[]
        {
            context.ExtraNonce1,
            KaspaConstants.ExtranoncePlaceHolderLength - extraNonce1Size,
        };

        return responseData;
    }
    
    public int GetExtraNonce1Size()
    {
        return extraPoolConfig?.ExtraNonce1Size ?? 2;
    }

    public virtual async ValueTask<Share> SubmitShareAsync(StratumConnection worker, object submission, CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);
        
        if(submission is not object[] submitParams)
            throw new StratumException(StratumError.Other, "invalid params");

        var context = worker.ContextAs<KaspaWorkerContext>();

        var jobId = submitParams[1] as string;
        var nonce = submitParams[2] as string;

        KaspaJob job;

        lock(jobLock)
        {
            job = validJobs.FirstOrDefault(x => x.JobId == jobId);
        }

        if(job == null)
            throw new StratumException(StratumError.JobNotFound, "job not found");

        // validate & process
        var share = job.ProcessShare(worker, nonce);

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

            var acceptResponse = await SubmitBlockAsync(ct, job.BlockTemplate);

            // is it still a block candidate?
            share.IsBlockCandidate = acceptResponse;
            
            if(share.IsBlockCandidate)
            {
                logger.Info(() => $"Daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {context.Miner}");

                OnBlockFound();

                // persist the nonce to make block unlocking a bit more reliable
                share.TransactionConfirmationData = nonce;
            }

            else
            {
                // clear fields that no longer apply
                share.TransactionConfirmationData = null;
            }
        }

        return share;
    }
    
    public bool ValidateIsLargeJob(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;
        
        if(ValidateIsBzMiner(userAgent))
            return true;
        
        if(ValidateIsIceRiverMiner(userAgent))
            return true;
        
        return false;
    }
    
    public bool ValidateIsBzMiner(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;
        
        // Find matches
        MatchCollection matchesUserAgentBzMiner = KaspaConstants.RegexUserAgentBzMiner.Matches(userAgent);
        return (matchesUserAgentBzMiner.Count > 0);
    }
    
    public bool ValidateIsGodMiner(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;
        
        // Find matches
        MatchCollection matchesUserAgentGodMiner = KaspaConstants.RegexUserAgentGodMiner.Matches(userAgent);
        return (matchesUserAgentGodMiner.Count > 0);
    }
    
    public bool ValidateIsIceRiverMiner(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;
        
        // Find matches
        MatchCollection matchesUserAgentIceRiverMiner = KaspaConstants.RegexUserAgentIceRiverMiner.Matches(userAgent);
        return (matchesUserAgentIceRiverMiner.Count > 0);
    }

    #endregion // API-Surface

    #region Overrides

    protected override async Task PostStartInitAsync(CancellationToken ct)
    {
        // validate pool address
        if(string.IsNullOrEmpty(poolConfig.Address))
            throw new PoolStartupException($"Pool address is not configured", poolConfig.Id);
        
        // we need a stream to communicate with Kaspad
        var stream = rpc.MessageStream(null, null, ct);
        
        var request = new kaspad.KaspadMessage();
        request.GetCurrentNetworkRequest = new kaspad.GetCurrentNetworkRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex=> throw new PoolStartupException($"Error writing a request in the communication stream '{ex.GetType().Name}' : {ex}", poolConfig.Id));
        await foreach (var currentNetwork in stream.ResponseStream.ReadAllAsync())
        {
            if(!string.IsNullOrEmpty(currentNetwork.GetCurrentNetworkResponse.Error?.Message))
                throw new PoolStartupException($"Daemon reports: {currentNetwork.GetCurrentNetworkResponse.Error?.Message}", poolConfig.Id);
            
            network = currentNetwork.GetCurrentNetworkResponse.CurrentNetwork;
            break;
        }
        await stream.RequestStream.CompleteAsync();
        
        var (kaspaAddressUtility, errorKaspaAddressUtility) = KaspaUtils.ValidateAddress(poolConfig.Address, network, coin.Symbol);
        if(errorKaspaAddressUtility != null)
            throw new PoolStartupException($"Pool address: {poolConfig.Address} is invalid for network [{network}]: {errorKaspaAddressUtility}", poolConfig.Id);
        else
            logger.Info(() => $"Pool address: {poolConfig.Address} => {KaspaConstants.KaspaAddressType[kaspaAddressUtility.KaspaAddress.Version()]}");
        
        // update stats
        BlockchainStats.NetworkType = network;
        BlockchainStats.RewardType = "POW";
        
        // we need a stream to communicate with Kaspad
        stream = rpc.MessageStream(null, null, ct);
        
        request = new kaspad.KaspadMessage();
        request.GetInfoRequest = new kaspad.GetInfoRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex=> throw new PoolStartupException($"Error writing a request in the communication stream '{ex.GetType().Name}' : {ex}", poolConfig.Id));
        await foreach (var info in stream.ResponseStream.ReadAllAsync())
        {
            if(!string.IsNullOrEmpty(info.GetInfoResponse.Error?.Message))
                throw new PoolStartupException($"Daemon reports: {info.GetInfoResponse.Error?.Message}", poolConfig.Id);
            
            if(info.GetInfoResponse.IsUtxoIndexed != true)
                throw new PoolStartupException("UTXO index is disabled", poolConfig.Id);
            
            extraData = (string) info.GetInfoResponse.ServerVersion + (!string.IsNullOrEmpty(extraData) ? "." + extraData : "");
            break;
        }
        await stream.RequestStream.CompleteAsync();

        // Payment-processing setup
        if(clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true)
        {
            // we need a call to communicate with kaspadWallet
            var call = walletRpc.ShowAddressesAsync(new kaspaWalletd.ShowAddressesRequest(), null, null, ct);
            
            // check configured address belongs to wallet
            var walletAddresses = await Guard(() => call.ResponseAsync,
                ex=> throw new PoolStartupException($"Error validating pool address '{ex.GetType().Name}' : {ex}", poolConfig.Id));

            if(!walletAddresses.Address.Contains(poolConfig.Address))
                throw new PoolStartupException($"Pool address: {poolConfig.Address} is not controlled by pool wallet", poolConfig.Id);
        }
        
        await UpdateNetworkStatsAsync(ct);

        // Periodically update network stats
        Observable.Interval(TimeSpan.FromMinutes(1))
            .Select(via => Observable.FromAsync(() =>
                Guard(()=> UpdateNetworkStatsAsync(ct),
                    ex=> logger.Error(ex))))
            .Concat()
            .Subscribe();

        SetupJobUpdates(ct);
    }

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<KaspaCoinTemplate>();

        extraPoolConfig = pc.Extra.SafeExtensionDataAs<KaspaPoolConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<KaspaPaymentProcessingConfigExtra>();
        
        maxActiveJobs = extraPoolConfig?.MaxActiveJobs ?? 8;
        extraData = extraPoolConfig?.ExtraData ?? "Miningcore.developers[\"Cedric CRISPIN\"]";
        
        // extract standard daemon endpoints
        daemonEndpoints = pc.Daemons
            .Where(x => string.IsNullOrEmpty(x.Category))
            .ToArray();
        
        if(cc.PaymentProcessing?.Enabled == true && pc.PaymentProcessing?.Enabled == true)
        {
            // extract wallet daemon endpoints
            walletDaemonEndpoints = pc.Daemons
                .Where(x => x.Category?.ToLower() == KaspaConstants.WalletDaemonCategory)
                .ToArray();

            if(walletDaemonEndpoints.Length == 0)
                throw new PoolStartupException("Wallet-RPC daemon is not configured (Daemon configuration for kaspa-pools require an additional entry of category 'wallet' pointing to the wallet daemon)", pc.Id);
        }

        base.Configure(pc, cc);
    }

    protected override void ConfigureDaemons()
    {
        logger.Debug(() => $"ProtobufDaemonRpcServiceName: {extraPoolConfig?.ProtobufDaemonRpcServiceName ?? KaspaConstants.ProtobufDaemonRpcServiceName}");
        
        rpc = KaspaClientFactory.CreateKaspadRPCClient(httpClientFactory, daemonEndpoints, extraPoolConfig?.ProtobufDaemonRpcServiceName ?? KaspaConstants.ProtobufDaemonRpcServiceName);
        
        // Payment-processing setup
        if(clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true)
        {
            logger.Debug(() => $"ProtobufWalletRpcServiceName: {extraPoolConfig?.ProtobufWalletRpcServiceName ?? KaspaConstants.ProtobufWalletRpcServiceName}");

            walletRpc = KaspaClientFactory.CreateKaspaWalletdRPCClient(httpClientFactory, walletDaemonEndpoints, extraPoolConfig?.ProtobufWalletRpcServiceName ?? KaspaConstants.ProtobufWalletRpcServiceName);
        }
    }

    protected override async Task<bool> AreDaemonsHealthyAsync(CancellationToken ct)
    {
        // Payment-processing setup
        if(clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true)
        {
            // we need a call to communicate with kaspadWallet
            var call = walletRpc.ShowAddressesAsync(new kaspaWalletd.ShowAddressesRequest(), null, null, ct);

            // check configured address belongs to wallet
            var walletAddresses = await Guard(() => call.ResponseAsync,
                ex=> logger.Debug(ex));

            if(walletAddresses == null)
                return false;
        }
        
        // we need a stream to communicate with Kaspad
        var stream = rpc.MessageStream(null, null, ct);
        
        var request = new kaspad.KaspadMessage();
        request.GetInfoRequest = new kaspad.GetInfoRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex=> logger.Debug(ex));
        bool areDaemonsHealthy = false;
        await foreach (var info in stream.ResponseStream.ReadAllAsync())
        {
            if(!string.IsNullOrEmpty(info.GetInfoResponse.Error?.Message))
            {
                logger.Debug(info.GetInfoResponse.Error?.Message);
                return false;
            }
            
            if(info.GetInfoResponse.IsUtxoIndexed != true)
                throw new PoolStartupException("UTXO index is disabled", poolConfig.Id);
            
            areDaemonsHealthy = true;
            break;
        }
        await stream.RequestStream.CompleteAsync();

        return areDaemonsHealthy;
    }

    protected override async Task<bool> AreDaemonsConnectedAsync(CancellationToken ct)
    {
        // Payment-processing setup
        if(clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true)
        {
            // we need a call to communicate with kaspadWallet
            var call = walletRpc.ShowAddressesAsync(new kaspaWalletd.ShowAddressesRequest(), null, null, ct);

            // check configured address belongs to wallet
            var walletAddresses = await Guard(() => call.ResponseAsync,
                ex=> logger.Debug(ex));

            if(walletAddresses == null)
                return false;
        }
        
        // we need a stream to communicate with Kaspad
        var stream = rpc.MessageStream(null, null, ct);
        
        var request = new kaspad.KaspadMessage();
        request.GetConnectedPeerInfoRequest = new kaspad.GetConnectedPeerInfoRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex=> logger.Debug(ex));
        int totalPeers = 0;
        await foreach (var info in stream.ResponseStream.ReadAllAsync())
        {
            if(!string.IsNullOrEmpty(info.GetConnectedPeerInfoResponse.Error?.Message))
            {
                logger.Debug(info.GetConnectedPeerInfoResponse.Error?.Message);
                return false;
            }
            else
                totalPeers = info.GetConnectedPeerInfoResponse.Infos.Count;
            
            break;
        }
        await stream.RequestStream.CompleteAsync();
        
        return totalPeers > 0;
    }

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        var syncPendingNotificationShown = false;

        do
        {
            var isSynched = false;
            
            // we need a stream to communicate with Kaspad
            var stream = rpc.MessageStream(null, null, ct);

            var request = new kaspad.KaspadMessage();
            request.GetInfoRequest = new kaspad.GetInfoRequestMessage();
            await Guard(() => stream.RequestStream.WriteAsync(request),
                ex=> logger.Debug(ex));
            await foreach (var info in stream.ResponseStream.ReadAllAsync())
            {
                if(!string.IsNullOrEmpty(info.GetInfoResponse.Error?.Message))
                    logger.Debug(info.GetInfoResponse.Error?.Message);

                isSynched = (info.GetInfoResponse.IsSynced == true && info.GetInfoResponse.IsUtxoIndexed == true);
                break;
            }
            await stream.RequestStream.CompleteAsync();

            if(isSynched)
            {
                logger.Info(() => "Daemon is synced with blockchain");
                break;
            }

            if(!syncPendingNotificationShown)
            {
                logger.Info(() => "Daemon is still syncing with network. Manager will be started once synced.");
                syncPendingNotificationShown = true;
            }

            await ShowDaemonSyncProgressAsync(ct);
        } while(await timer.WaitForNextTickAsync(ct));
    }

    private object[] GetJobParamsForStratum()
    {
        var job = currentJob;
        return job?.GetJobParams();
    }

    #endregion // Overrides
}
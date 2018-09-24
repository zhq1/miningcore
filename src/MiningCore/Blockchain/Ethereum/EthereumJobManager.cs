/*
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using MiningCore.Blockchain.Bitcoin;
using MiningCore.Blockchain.Ethereum.Configuration;
using MiningCore.Blockchain.Ethereum.DaemonResponses;
using MiningCore.Buffers;
using MiningCore.Configuration;
using MiningCore.Crypto.Hashing.Ethash;
using MiningCore.DaemonInterface;
using MiningCore.Extensions;
using MiningCore.JsonRpc;
using MiningCore.Messaging;
using MiningCore.Notifications;
using MiningCore.Notifications.Messages;
using MiningCore.Stratum;
using MiningCore.Time;
using MiningCore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Block = MiningCore.Blockchain.Ethereum.DaemonResponses.Block;
using Contract = MiningCore.Contracts.Contract;
using EC = MiningCore.Blockchain.Ethereum.EthCommands;

namespace MiningCore.Blockchain.Ethereum
{
    public class EthereumJobManager : JobManagerBase<EthereumJob>
    {
        public EthereumJobManager(
            IComponentContext ctx,
            IMasterClock clock,
            IMessageBus messageBus,
            JsonSerializerSettings serializerSettings) :
            base(ctx, messageBus)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(clock, nameof(clock));
            Contract.RequiresNonNull(messageBus, nameof(messageBus));

            this.clock = clock;

            serializer = new JsonSerializer
            {
                ContractResolver = serializerSettings.ContractResolver
            };
        }

        private DaemonEndpointConfig[] daemonEndpoints;
        private DaemonClient daemon;
        private EthereumNetworkType networkType;
        private ParityChainType chainType;
        private EthashFull ethash;
        private readonly IMasterClock clock;
        private readonly EthereumExtraNonceProvider extraNonceProvider = new EthereumExtraNonceProvider();

        private const int MaxBlockBacklog = 3;
        protected readonly Dictionary<string, EthereumJob> validJobs = new Dictionary<string, EthereumJob>();
        private EthereumPoolConfigExtra extraPoolConfig;
        private readonly JsonSerializer serializer;

        protected async Task<bool> UpdateJobAsync()
        {
            logger.LogInvoke();

            try
            {
                return UpdateJob(await GetBlockTemplateAsync());
            }

            catch(Exception ex)
            {
                logger.Error(ex, () => $"Error during {nameof(UpdateJobAsync)}");
            }

            return false;
        }

        protected bool UpdateJob(EthereumBlockTemplate blockTemplate)
        {
            logger.LogInvoke();

            try
            {
                // may happen if daemon is currently not connected to peers
                if (blockTemplate == null || blockTemplate.Header?.Length == 0)
                    return false;

                var job = currentJob;
                var isNew = currentJob == null || job.BlockTemplate.Header != blockTemplate.Header;

                if (isNew)
                {
                    var jobId = NextJobId("x8");

                    // update template
                    job = new EthereumJob(jobId, blockTemplate, logger);

                    lock(jobLock)
                    {
                        // add jobs
                        validJobs[jobId] = job;

                        // remove old ones
                        var obsoleteKeys = validJobs.Keys
                            .Where(key => validJobs[key].BlockTemplate.Height < job.BlockTemplate.Height - MaxBlockBacklog).ToArray();

                        foreach(var key in obsoleteKeys)
                            validJobs.Remove(key);
                    }

                    currentJob = job;

                    // update stats
                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = (long) job.BlockTemplate.Height;
                    BlockchainStats.NetworkDifficulty = job.BlockTemplate.Difficulty;
                }

                return isNew;
            }

            catch(Exception ex)
            {
                logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
            }

            return false;
        }

        private async Task<EthereumBlockTemplate> GetBlockTemplateAsync()
        {
            logger.LogInvoke();

            var response = await daemon.ExecuteCmdAnyAsync<JToken>(EC.GetWork);

            if (response.Error != null)
            {
                logger.Warn(() => $"Error(s) refreshing blocktemplate: {response.Error})");
                return null;
            }

            if (response.Response == null)
            {
                logger.Warn(() => $"Error(s) refreshing blocktemplate: {EC.GetWork} returned null response");
                return null;
            }

            // extract results
            var work = response.Response.ToObject<string[]>();
            var result = AssembleBlockTemplate(work);

            return result;
        }

        private EthereumBlockTemplate AssembleBlockTemplate(string[] work)
        {
            // only parity returns the 4th element (block height)
            if (work.Length < 4)
            {
                logger.Error(() => $"Error(s) refreshing blocktemplate: getWork did not return blockheight. Are you really connected to a Parity daemon?");
                return null;
            }

            // extract values
            var height = work[3].IntegralFromHex<ulong>();
            var targetString = work[2];
            var target = BigInteger.Parse(targetString.Substring(2), NumberStyles.HexNumber);

            var result = new EthereumBlockTemplate
            {
                Header = work[0],
                Seed = work[1],
                Target = targetString,
                Difficulty = (ulong) BigInteger.Divide(EthereumConstants.BigMaxValue, target),
                Height = height,
            };

            return result;
        }

        private async Task ShowDaemonSyncProgressAsync()
        {
            var responses = await daemon.ExecuteCmdAllAsync<object>(EC.GetSyncState);
            var firstValidResponse = responses.FirstOrDefault(x => x.Error == null && x.Response != null)?.Response;

            if (firstValidResponse != null)
            {
                // eth_syncing returns false if not synching
                if (firstValidResponse is bool)
                    return;

                var syncStates = responses.Where(x => x.Error == null && x.Response != null && firstValidResponse is JObject)
                    .Select(x => ((JObject) x.Response).ToObject<SyncState>())
                    .ToArray();

                if (syncStates.Any())
                {
                    // get peer count
                    var response = await daemon.ExecuteCmdAllAsync<string>(EC.GetPeerCount);
                    var validResponses = response.Where(x => x.Error == null && x.Response != null).ToArray();
                    var peerCount = validResponses.Any() ? validResponses.Max(x => x.Response.IntegralFromHex<uint>()) : 0;

                    if (syncStates.Any(x => x.WarpChunksAmount != 0))
                    {
                        var warpChunkAmount = syncStates.Min(x => x.WarpChunksAmount);
                        var warpChunkProcessed = syncStates.Max(x => x.WarpChunksProcessed);
                        var percent = (double) warpChunkProcessed / warpChunkAmount * 100;

                        logger.Info(() => $"Daemons have downloaded {percent:0.00}% of warp-chunks from {peerCount} peers");
                    }

                    else if (syncStates.Any(x => x.HighestBlock != 0))
                    {
                        var lowestHeight = syncStates.Min(x => x.CurrentBlock);
                        var totalBlocks = syncStates.Max(x => x.HighestBlock);
                        var percent = (double) lowestHeight / totalBlocks * 100;

                        logger.Info(() => $"Daemons have downloaded {percent:0.00}% of blockchain from {peerCount} peers");
                    }
                }
            }
        }

        private async Task UpdateNetworkStatsAsync()
        {
            logger.LogInvoke();

            try
            {
                var commands = new[]
                {
                    new DaemonCmd(EC.GetPeerCount),
                };

                var results = await daemon.ExecuteBatchAnyAsync(commands);

                if (results.Any(x => x.Error != null))
                {
                    var errors = results.Where(x => x.Error != null)
                        .ToArray();

                    if (errors.Any())
                        logger.Warn(() => $"Error(s) refreshing network stats: {string.Join(", ", errors.Select(y => y.Error.Message))})");
                }

                // extract results
                var peerCount = results[0].Response.ToObject<string>().IntegralFromHex<int>();

                BlockchainStats.NetworkHashrate = 0; // TODO
                BlockchainStats.ConnectedPeers = peerCount;
            }

            catch(Exception e)
            {
                logger.Error(e);
            }
        }

        private async Task<bool> SubmitBlockAsync(Share share, string fullNonceHex, string headerHash, string mixHash)
        {
            // submit work
            var response = await daemon.ExecuteCmdAnyAsync<object>(EC.SubmitWork, new[]
            {
                fullNonceHex,
                headerHash,
                mixHash
            });

            if (response.Error != null || (bool?) response.Response == false)
            {
                var error = response.Error?.Message ?? response?.Response?.ToString();

                logger.Warn(() => $"Block {share.BlockHeight} submission failed with: {error}");
                messageBus.SendMessage(new AdminNotification("Block submission failed", $"Pool {poolConfig.Id} {(!string.IsNullOrEmpty(share.Source) ? $"[{share.Source.ToUpper()}] " : string.Empty)}failed to submit block {share.BlockHeight}: {error}"));

                return false;
            }

            return true;
        }

        private object[] GetJobParamsForStratum(bool isNew)
        {
            var job = currentJob;

            if (job != null)
            {
                return new object[]
                {
                    job.Id,
                    job.BlockTemplate.Seed,
                    job.BlockTemplate.Header,
                    isNew
                };
            }

            return new object[0];
        }

        private JsonRpcRequest DeserializeRequest(byte[] data)
        {
            using(var stream = new MemoryStream(data))
            {
                using(var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    using(var jreader = new JsonTextReader(reader))
                    {
                        return serializer.Deserialize<JsonRpcRequest>(jreader);
                    }
                }
            }
        }

        #region API-Surface

        public IObservable<object> Jobs { get; private set; }

        public override void Configure(PoolConfig poolConfig, ClusterConfig clusterConfig)
        {
            extraPoolConfig = poolConfig.Extra.SafeExtensionDataAs<EthereumPoolConfigExtra>();

            // extract standard daemon endpoints
            daemonEndpoints = poolConfig.Daemons
                .Where(x => string.IsNullOrEmpty(x.Category))
                .ToArray();

            base.Configure(poolConfig, clusterConfig);

            if (poolConfig.EnableInternalStratum == true)
            {
                // ensure dag location is configured
                var dagDir = !string.IsNullOrEmpty(extraPoolConfig?.DagDir) ? Environment.ExpandEnvironmentVariables(extraPoolConfig.DagDir) : Dag.GetDefaultDagDirectory();

                // create it if necessary
                Directory.CreateDirectory(dagDir);

                // setup ethash
                ethash = new EthashFull(3, dagDir);
            }
        }

        public bool ValidateAddress(string address)
        {
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(address), $"{nameof(address)} must not be empty");

            if (EthereumConstants.ZeroHashPattern.IsMatch(address) ||
                !EthereumConstants.ValidAddressPattern.IsMatch(address))
                return false;

            return true;
        }

        public void PrepareWorker(StratumClient client)
        {
            var context = client.ContextAs<EthereumWorkerContext>();
            context.ExtraNonce1 = extraNonceProvider.Next();
        }

        public async Task<Share> SubmitShareAsync(StratumClient worker,
            string[] request, double stratumDifficulty, double stratumDifficultyBase)
        {
            Contract.RequiresNonNull(worker, nameof(worker));
            Contract.RequiresNonNull(request, nameof(request));

            logger.LogInvoke(new[] { worker.ConnectionId });
            var context = worker.ContextAs<EthereumWorkerContext>();

            // var miner = request[0];
            var jobId = request[1];
            var nonce = request[2];
            EthereumJob job;

            // stale?
            lock(jobLock)
            {
                if (!validJobs.TryGetValue(jobId, out job))
                    throw new StratumException(StratumError.MinusOne, "stale share");
            }

            // validate & process
            var (share, fullNonceHex, headerHash, mixHash) = await job.ProcessShareAsync(worker, nonce, ethash);

            // enrich share with common data
            share.PoolId = poolConfig.Id;
            share.NetworkDifficulty = BlockchainStats.NetworkDifficulty;
            share.Source = clusterConfig.ClusterName;
            share.Created = clock.Now;

            // if block candidate, submit & check if accepted by network
            if (share.IsBlockCandidate)
            {
                logger.Info(() => $"Submitting block {share.BlockHeight}");

                share.IsBlockCandidate = await SubmitBlockAsync(share, fullNonceHex, headerHash, mixHash);

                if (share.IsBlockCandidate)
                {
                    logger.Info(() => $"Daemon accepted block {share.BlockHeight} submitted by {context.MinerName}");
                }
            }

            return share;
        }

        public BlockchainStats BlockchainStats { get; } = new BlockchainStats();

        #endregion // API-Surface

        #region Overrides

        protected override void ConfigureDaemons()
        {
            var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();

            daemon = new DaemonClient(jsonSerializerSettings, messageBus, clusterConfig.ClusterName ?? poolConfig.PoolName, poolConfig.Id);
            daemon.Configure(daemonEndpoints);
        }

        protected override async Task<bool> AreDaemonsHealthyAsync()
        {
            var responses = await daemon.ExecuteCmdAllAsync<Block>(EC.GetBlockByNumber, new[] { (object) "pending", true });

            if (responses.Where(x => x.Error?.InnerException?.GetType() == typeof(DaemonClientException))
                .Select(x => (DaemonClientException) x.Error.InnerException)
                .Any(x => x.Code == HttpStatusCode.Unauthorized))
                logger.ThrowLogPoolStartupException($"Daemon reports invalid credentials");

            return responses.All(x => x.Error == null);
        }

        protected override async Task<bool> AreDaemonsConnectedAsync()
        {
            var response = await daemon.ExecuteCmdAnyAsync<string>(EC.GetPeerCount);

            return response.Error == null && response.Response.IntegralFromHex<uint>() > 0;
        }

        protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
        {
            var syncPendingNotificationShown = false;

            while(true)
            {
                var responses = await daemon.ExecuteCmdAllAsync<object>(EC.GetSyncState);

                var isSynched = responses.All(x => x.Error == null &&
                    x.Response is bool && (bool) x.Response == false);

                if (isSynched)
                {
                    logger.Info(() => $"All daemons synched with blockchain");
                    break;
                }

                if (!syncPendingNotificationShown)
                {
                    logger.Info(() => $"Daemons still syncing with network. Manager will be started once synced");
                    syncPendingNotificationShown = true;
                }

                await ShowDaemonSyncProgressAsync();

                // delay retry by 5s
                await Task.Delay(5000, ct);
            }
        }

        protected override async Task PostStartInitAsync(CancellationToken ct)
        {
            var commands = new[]
            {
                new DaemonCmd(EC.GetNetVersion),
                new DaemonCmd(EC.GetAccounts),
                new DaemonCmd(EC.GetCoinbase),
                new DaemonCmd(EC.ParityVersion),
                new DaemonCmd(EC.ParityChain),
            };

            var results = await daemon.ExecuteBatchAnyAsync(commands);

            if (results.Any(x => x.Error != null))
            {
                if (results[4].Error != null)
                    logger.ThrowLogPoolStartupException($"Looks like you are NOT running 'Parity' as daemon which is not supported - https://parity.io/");

                var errors = results.Where(x => x.Error != null)
                    .ToArray();

                if (errors.Any())
                    logger.ThrowLogPoolStartupException($"Init RPC failed: {string.Join(", ", errors.Select(y => y.Error.Message))}");
            }

            // extract results
            var netVersion = results[0].Response.ToObject<string>();
            var accounts = results[1].Response.ToObject<string[]>();
            var coinbase = results[2].Response.ToObject<string>();
            var parityVersion = results[3].Response.ToObject<JObject>();
            var parityChain = results[4].Response.ToObject<string>();

            // ensure pool owns wallet
            //if (clusterConfig.PaymentProcessing?.Enabled == true && !accounts.Contains(poolConfig.Address) || coinbase != poolConfig.Address)
            //    logger.ThrowLogPoolStartupException($"Daemon does not own pool-address '{poolConfig.Address}'", LogCat);

            EthereumUtils.DetectNetworkAndChain(netVersion, parityChain, out networkType, out chainType);

            if (clusterConfig.PaymentProcessing?.Enabled == true && poolConfig.PaymentProcessing?.Enabled == true)
                ConfigureRewards();

            // update stats
            BlockchainStats.RewardType = "POW";
            BlockchainStats.NetworkType = $"{chainType}-{networkType}";

            await UpdateNetworkStatsAsync();

            // Periodically update network stats
            Observable.Interval(TimeSpan.FromMinutes(10))
                .Select(via => Observable.FromAsync(async ()=>
                {
                    try
                    {
                        await UpdateNetworkStatsAsync();
                    }

                    catch (Exception ex)
                    {
                        logger.Error(ex);
                    }
                }))
                .Concat()
                .Subscribe();

            if (poolConfig.EnableInternalStratum == true)
            {
                // make sure we have a current DAG
                while(true)
                {
                    var blockTemplate = await GetBlockTemplateAsync();

                    if (blockTemplate != null)
                    {
                        logger.Info(() => $"Loading current DAG ...");

                        await ethash.GetDagAsync(blockTemplate.Height, logger);

                        logger.Info(() => $"Loaded current DAG");
                        break;
                    }

                    logger.Info(() => $"Waiting for first valid block template");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }

                SetupJobUpdates();
            }
        }

        private void ConfigureRewards()
        {
            // Donation to MiningCore development
            if (chainType == ParityChainType.Mainnet &&
                DevDonation.Addresses.TryGetValue(poolConfig.Coin.Type, out var address))
            {
                poolConfig.RewardRecipients = poolConfig.RewardRecipients.Concat(new[]
                {
                    new RewardRecipient
                    {
                        Address = address,
                        Percentage = DevDonation.Percent
                    }
                }).ToArray();
            }
        }

        protected virtual void SetupJobUpdates()
        {
            if (poolConfig.EnableInternalStratum == false)
                return;

            var enableStreaming = extraPoolConfig?.EnableDaemonWebsocketStreaming == true;

            if (enableStreaming && !poolConfig.Daemons.Any(x =>
                x.Extra.SafeExtensionDataAs<EthereumDaemonEndpointConfigExtra>()?.PortWs.HasValue == true))
            {
                logger.Warn(() => $"'{nameof(EthereumPoolConfigExtra.EnableDaemonWebsocketStreaming).ToLowerCamelCase()}' enabled but not a single daemon found with a configured websocket port ('{nameof(EthereumDaemonEndpointConfigExtra.PortWs).ToLowerCamelCase()}'). Falling back to polling.");
                enableStreaming = false;
            }

            if (enableStreaming)
            {
                // collect ports
                var wsDaemons = poolConfig.Daemons
                    .Where(x => x.Extra.SafeExtensionDataAs<EthereumDaemonEndpointConfigExtra>()?.PortWs.HasValue == true)
                    .ToDictionary(x => x, x =>
                    {
                        var extra = x.Extra.SafeExtensionDataAs<EthereumDaemonEndpointConfigExtra>();

                        return (extra.PortWs.Value, extra.HttpPathWs, extra.SslWs);
                    });

                logger.Info(() => $"Subscribing to WebSocket push-updates from {string.Join(", ", wsDaemons.Keys.Select(x => x.Host).Distinct())}");

                // stream work updates
                var getWorkObs = daemon.WebsocketSubscribe(wsDaemons, EC.ParitySubscribe, new[] { (object) EC.GetWork })
                    .Select(data =>
                    {
                        try
                        {
                            var psp = DeserializeRequest(data).ParamsAs<PubSubParams<string[]>>();
                            return psp?.Result;
                        }

                        catch(Exception ex)
                        {
                            logger.Info(() => $"Error deserializing pending block: {ex.Message}");
                        }

                        return null;
                    });

                Jobs = getWorkObs.Where(x => x != null)
                    .Select(AssembleBlockTemplate)
                    .Select(UpdateJob)
                    .Do(isNew =>
                    {
                        if (isNew)
                            logger.Info(() => $"New block {currentJob.BlockTemplate.Height} detected");
                    })
                    .Where(isNew => isNew)
                    .Select(_ => GetJobParamsForStratum(true))
                    .Publish()
                    .RefCount();
            }

            else
            {
                Jobs = Observable.Interval(TimeSpan.FromMilliseconds(poolConfig.BlockRefreshInterval))
                    .Select(_ => Observable.FromAsync(UpdateJobAsync))
                    .Concat()
                    .Do(isNew =>
                    {
                        if (isNew)
                            logger.Info(() => $"New block {currentJob.BlockTemplate.Height} detected");
                    })
                    .Where(isNew => isNew)
                    .Select(_ => GetJobParamsForStratum(true))
                    .Publish()
                    .RefCount();
            }
        }

        #endregion // Overrides
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.ChainController;
using AElf.Common.Attributes;
using AElf.Common.ByteArrayHelpers;
using AElf.Configuration;
using AElf.Cryptography.ECDSA;
using AElf.Execution;
using AElf.Execution.Scheduling;
using AElf.Kernel.Consensus;
using AElf.Kernel.Managers;
using AElf.Kernel.Node.Protocol;
using AElf.Kernel.Types;
using AElf.Network;
using AElf.Network.Connection;
using AElf.Network.Data;
using AElf.Network.Peers;
using AElf.SmartContract;
using AElf.Types.CSharp;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

// ReSharper disable once CheckNamespace
namespace AElf.Kernel.Node
{
    [LoggerName("Node")]
    public class MainChainNode : IAElfNode
    {
        private ECKeyPair _nodeKeyPair;
        private readonly ITxPoolService _txPoolService;
        private readonly ITransactionManager _transactionManager;
        private readonly ILogger _logger;
        private readonly INodeConfig _nodeConfig;
        private readonly IMiner _miner;
        private readonly IAccountContextService _accountContextService;
        private readonly IBlockVaildationService _blockVaildationService;
        private readonly IChainContextService _chainContextService;
        private readonly IChainService _chainService;
        private readonly IChainCreationService _chainCreationService;
        private readonly IWorldStateDictator _worldStateDictator;
        private readonly ISmartContractService _smartContractService;
        private readonly IFunctionMetadataService _functionMetadataService;
        private readonly INetworkManager _netManager;
        private readonly IBlockSynchronizer _synchronizer;
        private readonly IBlockExecutor _blockExecutor;
        private readonly AElfDPoSHelper _dPoSHelper;

        public Hash ContractAccountHash => _chainCreationService.GenesisContractHash(_nodeConfig.ChainId, SmartContractType.AElfDPoS);

        /// <summary>
        /// Just used to dispose previous consensus observer.
        /// </summary>
        public IDisposable ConsensusDisposable { get; set; }

        public ulong ConsensusMemory { get; set; }

        private int _flag;

        private bool _incrementIdNeedToAddOne;

        public bool IsMining { get; private set; }

        private readonly Stack<Hash> _consensusData = new Stack<Hash>();

        public int IsMiningInProcess => _flag;

        public BlockProducer BlockProducers
        {
            get
            {
                var dict = MinersConfig.Instance.Producers;
                var blockProducers = new BlockProducer();

                foreach (var bp in dict.Values)
                {
                    var b = bp["address"].RemoveHexPrefix();
                    blockProducers.Nodes.Add(b);
                }
                Globals.BlockProducerNumber = blockProducers.Nodes.Count;
                return blockProducers;
            }
        }

        // ReSharper disable once InconsistentNaming
        private AElfDPoSObserver AElfDPoSObserver => new AElfDPoSObserver(_logger,
            MiningWithInitializingAElfDPoSInformation,
            MiningWithPublishingOutValueAndSignature, PublishInValue, MiningWithUpdatingAElfDPoSInformation);

        private SingleNodeTestObserver SingleNodeTestObserver => new SingleNodeTestObserver(_logger, SingleNodeMining);

        public Hash ChainId => _nodeConfig.ChainId;

        public MainChainNode(ITxPoolService poolService, ITransactionManager txManager,
            ILogger logger,
            INodeConfig nodeConfig, IMiner miner, IAccountContextService accountContextService,
            IBlockVaildationService blockVaildationService,
            IChainContextService chainContextService, IBlockExecutor blockExecutor,
            IChainCreationService chainCreationService, IWorldStateDictator worldStateDictator,
            IChainService chainService, ISmartContractService smartContractService,
            IFunctionMetadataService functionMetadataService, INetworkManager netManager,
            IBlockSynchronizer synchronizer)
        {
            _chainCreationService = chainCreationService;
            _chainService = chainService;
            _worldStateDictator = worldStateDictator;
            _smartContractService = smartContractService;
            _functionMetadataService = functionMetadataService;
            _txPoolService = poolService;
            _transactionManager = txManager;
            _logger = logger;
            _nodeConfig = nodeConfig;
            _miner = miner;
            _accountContextService = accountContextService;
            _blockVaildationService = blockVaildationService;
            _chainContextService = chainContextService;
            _worldStateDictator = worldStateDictator;
            _blockExecutor = blockExecutor;
            _netManager = netManager;
            _synchronizer = synchronizer;

            _dPoSHelper = new AElfDPoSHelper(_worldStateDictator, _nodeKeyPair, ChainId, BlockProducers,
                ContractAccountHash, _logger);
        }
 
        public bool Start(ECKeyPair nodeKeyPair, bool startRpc, int rpcPort, string rpcHost, string initData,
            byte[] tokenContractCode, byte[] consensusContractCode, byte[] basicContractZero)
        {
            if (_nodeConfig == null)
            {
                _logger?.Log(LogLevel.Error, "No node configuration.");
                return false;
            }

            if (_nodeConfig.ChainId == null || _nodeConfig.ChainId.Length <= 0)
            {
                _logger?.Log(LogLevel.Error, "No chain id.");
                return false;
            }

            try
            {
                _logger?.Log(LogLevel.Debug, "Chain Id = \"{0}\"", _nodeConfig.ChainId.ToHex());
                var genesis = GetGenesisContractHash(SmartContractType.BasicContractZero);
                _logger?.Log(LogLevel.Debug, "Genesis contract address = \"{0}\"", genesis.ToHex());
                    
                var tokenContractAddress = GetGenesisContractHash(SmartContractType.TokenContract);
                _logger?.Log(LogLevel.Debug, "Token contract address = \"{0}\"", tokenContractAddress.ToHex());
                    
                var consensusAddress = GetGenesisContractHash(SmartContractType.AElfDPoS);
                _logger?.Log(LogLevel.Debug, "DPoS contract address = \"{0}\"", consensusAddress.ToHex());
                
                var blockchain = _chainService.GetBlockChain(_nodeConfig.ChainId);
                var curHash = blockchain.GetCurrentBlockHashAsync().Result;
                var chainExists = curHash != null && !curHash.Equals(Hash.Genesis);
                if (!chainExists)
                {
                    // Creation of the chain if it doesn't already exist
                    var tokenSCReg = new SmartContractRegistration
                    {
                        Category = 0,
                        ContractBytes = ByteString.CopyFrom(tokenContractCode),
                        ContractHash = tokenContractCode.CalculateHash(),
                        Type = (int)SmartContractType.TokenContract
                    };
                    
                    var consensusCReg = new SmartContractRegistration
                    {
                        Category = 0,
                        ContractBytes = ByteString.CopyFrom(consensusContractCode),
                        ContractHash = consensusContractCode.CalculateHash(),
                        Type = (int)SmartContractType.AElfDPoS
                    };
                    
                    var basicReg = new SmartContractRegistration
                    {
                        Category = 0,
                        ContractBytes = ByteString.CopyFrom(basicContractZero),
                        ContractHash = basicContractZero.CalculateHash(),
                        Type = (int)SmartContractType.BasicContractZero
                    };
                    var res = _chainCreationService.CreateNewChainAsync(_nodeConfig.ChainId,
                        new List<SmartContractRegistration> {basicReg, tokenSCReg, consensusCReg}).Result;

                    _logger?.Log(LogLevel.Debug, "Genesis block hash = \"{0}\"", res.GenesisBlockHash.ToHex());
                    
                }
                else
                {
                    var preBlockHash = GetLastValidBlockHash().Result;
                    _worldStateDictator.SetWorldStateAsync(preBlockHash);

                    _worldStateDictator.PreBlockHash = preBlockHash;
                    _worldStateDictator.RollbackCurrentChangesAsync();
                }
            }
            catch (Exception e)
            {
                _logger?.Log(LogLevel.Error,
                    "Could not create the chain : " + _nodeConfig.ChainId.ToHex());
            }

            if (!string.IsNullOrWhiteSpace(initData))
            {
            }

            // set world state
            _worldStateDictator.SetChainId(_nodeConfig.ChainId);

            _nodeKeyPair = nodeKeyPair;

            _txPoolService.Start();

            Task.Run(() => _netManager.Start());

            _netManager.MessageReceived += ProcessPeerMessage;

            //_protocolDirector.SetCommandContext(this, _nodeConfig.ConsensusInfoGenerater); // If not miner do sync
            if (!_nodeConfig.ConsensusInfoGenerater)
            {
                _synchronizer.SyncFinished += BlockSynchronizerOnSyncFinished;
            }
            else
            {
                StartMining();
            }

            Task.Run(() => _synchronizer.Start(this, !_nodeConfig.ConsensusInfoGenerater));
            // akka env 
            var servicePack = new ServicePack
            {
                ChainContextService = _chainContextService,
                SmartContractService = _smartContractService,
                ResourceDetectionService = new ResourceUsageDetectionService(_functionMetadataService),
                WorldStateDictator = _worldStateDictator
            };
            var grouper = new Grouper(servicePack.ResourceDetectionService, _logger);
            _blockExecutor.Start(grouper);
            if (_nodeConfig.IsMiner)
            {
                _miner.Start(nodeKeyPair, grouper);

                _logger?.Log(LogLevel.Debug, "Coinbase = \"{0}\"", _miner.Coinbase.ToHex());
            }

            _logger?.Log(LogLevel.Debug, "AElf node started.");

            Task.Run(async () => await ProcessLoop()).ConfigureAwait(false);

            return true;
        }

        private BlockingCollection<NetMessageReceivedArgs> _messageQueue = new BlockingCollection<NetMessageReceivedArgs>();

        private async Task ProcessLoop()
        {
            try
            {
                while (true)
                {
                    var args = _messageQueue.Take();

                    var message = args.Message;
                    var msgType = (MessageType) message.Type;
                    
                    _logger?.Trace($"Handling message {message}");

                    if (msgType == MessageType.RequestBlock)
                    {
                        await HandleBlockRequest(message, args.PeerMessage);
                    }
                    else if (msgType == MessageType.TxRequest)
                    {
                        await HandleTxRequest(message, args.PeerMessage);
                    }
                }
            }
            catch (Exception e)
            {
                _logger?.Trace(e, "Error while dequeuing.");
            }
        }

        private void ProcessPeerMessage(object sender, EventArgs e)
        {
            if (sender != null && e is NetMessageReceivedArgs args && args.Message != null)
            {
                _messageQueue.Add(args);
            }
        }

        internal async Task HandleBlockRequest(Message message, PeerMessageReceivedArgs args)
        {
            try
            {
                var breq = BlockRequest.Parser.ParseFrom(message.Payload);
                var block = await GetBlockAtHeight(breq.Height);
                var req = NetRequestFactory.CreateMessage(MessageType.Block, block.ToByteArray());
                
                args.Peer.EnqueueOutgoing(req);

                _logger?.Trace("Send block " + block.GetHash().ToHex() + " to " + args.Peer);
            }
            catch (Exception e)
            {
                _logger?.Trace(e, "Error while during HandleBlockRequest.");
            }
        }

        private async Task HandleTxRequest(Message message, PeerMessageReceivedArgs args)
        {
            string hash = null;

            try
            {
                var breq = TxRequest.Parser.ParseFrom(message.Payload);
                hash = breq.TxHash.ToByteArray().ToHex();
                var tx = await GetTransaction(breq.TxHash);
                if (!(tx is Transaction t))
                {
                    _logger?.Trace("Could not find transaction: ", hash);
                    return;
                }

                var req = NetRequestFactory.CreateMessage(MessageType.Tx, t.ToByteArray());
                args.Peer.EnqueueOutgoing(req);

                _logger?.Trace("Send tx " + t.GetHash().ToHex() + " to " + args.Peer + "(" + t.ToByteArray().Length + " bytes)");
            }
            catch (Exception e)
            {
                _logger?.Trace(e, $"Transaction request failed. Hash : {hash}");
            }
        }

        private void BlockSynchronizerOnSyncFinished(object sender, EventArgs eventArgs)
        {
            StartMining();
        }

        private void StartMining()
        {
            if (IsMiner() && !IsMining)
            {
                StartConsensusProcess();
            }
        }

        public bool IsMiner()
        {
            return _nodeConfig.IsMiner;
        }

        private async Task<bool> InitialDebugSync(string initFileName)
        {
            try
            {
                var fullPath = Path.Combine(_nodeConfig.DataDir, "tests", initFileName);

                using (var file = File.OpenText(fullPath))
                using (var reader = new JsonTextReader(file))
                {
                    var balances = (JObject) JToken.ReadFrom(reader);
                    foreach (var kv in balances)
                    {
                        var address = ByteArrayHelpers.FromHexString(kv.Key);
                        var balance = kv.Value.ToObject<ulong>();

                        var accountDataProvider = await _worldStateDictator.GetAccountDataProvider(address);
                        var dataProvider = accountDataProvider.GetDataProvider();

                        // set balance
                        await dataProvider.SetAsync("Balance".CalculateHash(),
                            new UInt64Value {Value = balance}.ToByteArray());
                        _logger?.Log(LogLevel.Debug, "Initial balance {0} in Address \"{1}\"", balance, kv.Key);
                    }
                }
            }
            catch (Exception e)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// get the tx from tx pool or database
        /// </summary>
        /// <param name="txId"></param>
        /// <returns></returns>
        public async Task<Transaction> GetTransaction(Hash txId)
        {
            if (_txPoolService.TryGetTx(txId, out var tx))
            {
                return tx;
            }

            return await _transactionManager.GetTransaction(txId);
        }

        /// <summary>
        /// This inserts a transaction into the node. Note that it does
        /// not broadcast it to the network and doesn't add it to the
        /// transaction pool. Essentially it just inserts the transaction
        /// in the database.
        /// </summary>
        /// <param name="tx">The transaction to insert</param>
        /// <returns>The hash of the transaction that was inserted</returns>
        public async Task<IHash> InsertTransaction(Transaction tx)
        {
            return await _transactionManager.AddTransactionAsync(tx);
        }

        /// <summary>
        /// This method processes a transaction received from one of the
        /// connected peers.
        /// </summary>
        /// <param name="messagePayload"></param>
        /// <param name="isFromSend"></param>
        /// <returns></returns>
        public async Task ReceiveTransaction(byte[] messagePayload, bool isFromSend)
        {
            try
            {
                var tx = Transaction.Parser.ParseFrom(messagePayload);
                var success = await _txPoolService.AddTxAsync(tx);

                if (isFromSend)
                {
                    _logger?.Trace("Received Transaction: " + "FROM, " + tx.GetHash().ToHex() + ", INCR : " +
                                   tx.IncrementId);
                    //_protocolDirector.AddTransaction(tx);
                }

                if (success != TxValidation.TxInsertionAndBroadcastingError.Success)
                {
                    _logger?.Trace("DID NOT add Transaction to pool: FROM {0} , INCR : {1}, with error {2} ",
                        tx.GetTransactionInfo(),
                        tx.IncrementId, success);
                    return;
                }

                _logger?.Trace("Successfully added tx : " + tx.GetHash().Value.ToByteArray().ToHex());
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Invalid tx - Could not receive transaction from the network", null);
            }
        }

        /// <summary>
        /// return default incrementId for one address
        /// </summary>
        /// <param name="addr"></param>
        /// <returns></returns>
        public async Task<ulong> GetIncrementId(Hash addr)
        {
            try
            {
                bool isDPoS = addr.Equals(_nodeKeyPair.GetAddress()) ||
                             _dPoSHelper.BlockProducer.Nodes.Contains(addr.ToHex().RemoveHexPrefix());
                
                // ReSharper disable once InconsistentNaming
                var idInDB = (await _accountContextService.GetAccountDataContext(addr, _nodeConfig.ChainId)).IncrementId;
                _logger?.Log(LogLevel.Debug, $"Trying to get increment id, {isDPoS}");
                var idInPool = _txPoolService.GetIncrementId(addr, isDPoS);
                _logger?.Log(LogLevel.Debug, $"End Trying to get increment id, {isDPoS}");

                return Math.Max(idInDB, idInPool);
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Failed to get increment id.");
                return 0;
            }
        }

        public async Task<Hash> GetLastValidBlockHash()
        {
            var pointer = ResourcePath.CalculatePointerForLastBlockHash(_nodeConfig.ChainId);
            return await _worldStateDictator.GetDataAsync(pointer);
        }

        /// <summary>
        /// Add a new block received from network by first validating it and then
        /// executing it.
        /// </summary>
        /// <param name="block"></param>
        /// <returns></returns>
        public async Task<BlockExecutionResult> ExecuteAndAddBlock(IBlock block)
        {
            try
            {
                var res = Interlocked.CompareExchange(ref _flag, 1, 0);
                if (res == 1)
                    return new BlockExecutionResult(false, ValidationError.Mining);

                var context = await _chainContextService.GetChainContextAsync(_nodeConfig.ChainId);
                var error = await _blockVaildationService.ValidateBlockAsync(block, context, _nodeKeyPair);

                if (error != ValidationError.Success)
                {
                    var blockchain = _chainService.GetBlockChain(_nodeConfig.ChainId);
                    var localCorrespondingBlock = await blockchain.GetBlockByHeightAsync(block.Header.Index);
                    if (error == ValidationError.OrphanBlock)
                    {
                        //TODO: limit the count of blocks to rollback
                        if (block.Header.Time.ToDateTime() < localCorrespondingBlock.Header.Time.ToDateTime())
                        {
                            _logger?.Trace("Ready to rollback");
                            //Rollback world state
                            var txs = await _worldStateDictator.RollbackToSpecificHeight(block.Header.Index);

                            await _txPoolService.RollBack(txs);
                            _worldStateDictator.PreBlockHash = block.Header.PreviousBlockHash;
                            await _worldStateDictator.RollbackCurrentChangesAsync();

                            var ws = await _worldStateDictator.GetWorldStateAsync(block.GetHash());
                            _logger?.Trace($"Current world state {(await ws.GetWorldStateMerkleTreeRootAsync()).ToHex()}");

                            error = ValidationError.Success;
                        }
                        else
                        {
                            // insert to database 
                            Interlocked.CompareExchange(ref _flag, 0, 1);
                            return new BlockExecutionResult(false, ValidationError.OrphanBlock);
                        }
                    }
                    else
                    {
                        Interlocked.CompareExchange(ref _flag, 0, 1);
                        _logger?.Trace("Invalid block received from network: " + error);
                        return new BlockExecutionResult(false, error);
                    }
                }

                var executed = await _blockExecutor.ExecuteBlock(block);
                Interlocked.CompareExchange(ref _flag, 0, 1);

                Task.WaitAll();
                await CheckUpdatingConsensusProcess();

                return new BlockExecutionResult(executed, error);
                //return new BlockExecutionResult(true, error);
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Block synchronzing failed");
                Interlocked.CompareExchange(ref _flag, 0, 1);
                return new BlockExecutionResult(e);
            }
        }

        public Hash GetGenesisContractHash(SmartContractType contractType)
        {
            return _chainCreationService.GenesisContractHash(_nodeConfig.ChainId, contractType);
        }
        
        /// <summary>
        /// temple mine to generate fake block data with loop
        /// </summary>
        public async void StartConsensusProcess()
        {
            if (IsMining)
                return;

            IsMining = true;

            switch (Globals.ConsensusType)
            {
                case ConsensusType.AElfDPoS:
                    if (!BlockProducers.Nodes.Contains(_nodeKeyPair.GetAddress().ToHex().RemoveHexPrefix()))
                    {
                        break;
                    }
                    
                    if (_nodeConfig.ConsensusInfoGenerater && ! await _dPoSHelper.HasGenerated())
                    {
                        AElfDPoSObserver.Initialization();
                        break;
                    }
                    else
                    {
                        _dPoSHelper.SyncMiningInterval();
                        _logger?.Trace($"Set AElf DPoS mining interval: {Globals.AElfDPoSMiningInterval} ms.");

                    }

                    if (_dPoSHelper.CanRecoverDPoSInformation())
                    {
                        AElfDPoSObserver.RecoverMining();
                    }
                    
                    break;
                
                case ConsensusType.PoTC:
                    await Mine();
                    break;
                
                case ConsensusType.SingleNode:
                    SingleNodeTestProcess();
                    break;
            }
        }

        // ReSharper disable once InconsistentNaming
        public async Task CheckUpdatingConsensusProcess()
        {
            switch (Globals.ConsensusType)
            {
                case ConsensusType.AElfDPoS:
                    await AElfDPoSProcess();
                    break;
                
                case ConsensusType.PoTC:
                    await PoTCProcess();
                    break;
            }
        }

        // ReSharper disable once InconsistentNaming
        private async Task AElfDPoSProcess()
        {
            var blockchain = _chainService.GetBlockChain(_nodeConfig.ChainId);
            var hash = await blockchain.GetCurrentBlockHashAsync();
            var header = (BlockHeader) await blockchain.GetHeaderByHashAsync(hash); 
            //Do DPoS log
            _logger?.Trace(await _dPoSHelper.GetDPoSInfo(header.Index));
            _logger?.Trace("Log dpos information - End");
            
            if (ConsensusMemory == _dPoSHelper.CurrentRoundNumber.Value)
                return;
            //Dispose previous observer.
            if (ConsensusDisposable != null)
            {
                ConsensusDisposable.Dispose();
                _logger?.Trace("Disposed previous consensus observables list.");
            }
            else
            {
                _logger?.Trace("For now the consensus observables list is null.");
            }

            //Update observer.
            var blockProducerInfoOfCurrentRound =
                _dPoSHelper[_nodeKeyPair.GetAddress().ToHex().RemoveHexPrefix()];
            ConsensusDisposable =
                AElfDPoSObserver.SubscribeAElfDPoSMiningProcess(blockProducerInfoOfCurrentRound,
                    _dPoSHelper.ExtraBlockTimeslot);

            //Update current round number.
            ConsensusMemory = _dPoSHelper.CurrentRoundNumber.Value;
        }
        
        // ReSharper disable once InconsistentNaming
        private async Task PoTCProcess()
        {
            while (true)
            {
                var count = await _txPoolService.GetPoolSize();
                if (ConsensusMemory != count)
                {
                    _logger?.Trace($"Current tx pool size: {count} / {Globals.ExpectedTransanctionCount}");
                    ConsensusMemory = count;
                }
                if (count >= Globals.ExpectedTransanctionCount)
                {
                    _logger?.Trace("Will produce one block.");
                    var block = await _miner.Mine();
                    await BroadcastBlock(block);
                }
            }
        }

        private void SingleNodeTestProcess()
        {
            ConsensusDisposable = SingleNodeTestObserver.SubscribeSingleNodeTestProcess();
        }

        private async Task SingleNodeMining()
        {
            _logger.Trace("Single node mining start.");
            var block = await Mine();
            await BroadcastBlock(block);
            _logger.Trace("Single node mining end.");
        }

        public async Task<IBlock> Mine()
        {
            var res = Interlocked.CompareExchange(ref _flag, 1, 0);
            if (res == 1)
                return null;
            try
            {
                _logger?.Trace($"Mine - Entered mining {res}");

                _worldStateDictator.BlockProducerAccountAddress = _nodeKeyPair.GetAddress();

                var task = Task.Run(async () => await _miner.Mine());

                if (!task.Wait(TimeSpan.FromMilliseconds(Globals.AElfDPoSMiningInterval * 0.9)))
                {
                    _logger?.Error("Mining timeout.");
                    return null;
                }

                var b = Interlocked.CompareExchange(ref _flag, 0, 1);

                _synchronizer.IncrementChainHeight();

                _logger?.Trace($"Mine - Leaving mining {b}");
                
                Task.WaitAll();

                //Update DPoS observables.
                //Sometimes failed to update this observables list (which is weird), just ignore this.
                //Which means this node will do nothing in this round.
                try
                {
                    await CheckUpdatingConsensusProcess();
                }
                catch (Exception e)
                {
                    _logger?.Error(e, "Somehow failed to update DPoS observables. Will recover soon.");
                    //In case just config one node to produce blocks.
                    AElfDPoSObserver.RecoverMining();
                }

                return task.Result;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Interlocked.CompareExchange(ref _flag, 0, 1);
                return null;
            }
        }

        public async Task<bool> BroadcastBlock(IBlock block)
        {
            if (!(block is Block b))
            {
                return false;
            }

            var serializedBlock = b.ToByteArray();
            await _netManager.BroadcastBock(block.GetHash().Value.ToByteArray(), serializedBlock);

            var bh = block.GetHash().ToHex();
            _logger?.Trace(
                $"Broadcasted block \"{bh}\" to peers with {block.Body.TransactionsCount} tx(s). Block height: [{block.Header.Index}].");

            return true;
        }

        /// <summary>
        /// Broadcasts a transaction to the network. This method
        /// also places it in the transaction pool.
        /// </summary>
        /// <param name="tx">The tx to broadcast</param>
        public async Task<TxValidation.TxInsertionAndBroadcastingError> BroadcastTransaction(Transaction tx)
        {
            if(tx.From.Equals(_nodeKeyPair.GetAddress()))
                _logger?.Trace("Try to insert DPoS transaction to pool: " + tx.GetHash().ToHex() + ", threadId: " +
                           Thread.CurrentThread.ManagedThreadId);
            TxValidation.TxInsertionAndBroadcastingError res;

            var stopWatch = new Stopwatch();
            try
            {
                stopWatch.Start();
                res = await _txPoolService.AddTxAsync(tx);
                stopWatch.Stop();
                //_logger?.Info($"### Debug _txPoolService.AddTxAsync Time: {stopWatch.ElapsedMilliseconds}");
            }
            catch (Exception e)
            {
                _logger?.Trace("Transaction insertion failed: {0},\n{1}", e.Message, tx.GetTransactionInfo());
                return TxValidation.TxInsertionAndBroadcastingError.Failed;
            }

            if (res == TxValidation.TxInsertionAndBroadcastingError.Success)
            {
                try
                {
                    stopWatch.Start();
                    var transaction = tx.Serialize();
                    await _netManager.BroadcastMessage(MessageType.BroadcastTx, transaction);
                    stopWatch.Stop();
                   // _logger?.Info($"### Debug _netManager.BroadcastMessage Time: {stopWatch.ElapsedMilliseconds}");
                }
                catch (Exception e)
                {
                    _logger?.Trace("Broadcasting transaction failed: {0},\n{1}", e.Message, tx.GetTransactionInfo());
                    return TxValidation.TxInsertionAndBroadcastingError.BroadCastFailed;
                }
                if(tx.From.Equals(_nodeKeyPair.GetAddress()))
                    _logger?.Trace("Broadcasted transaction to peers: " + tx.GetTransactionInfo());
                return TxValidation.TxInsertionAndBroadcastingError.Success;
            }

            _logger?.Trace("Transaction insertion failed:{0}, [{1}]", res, tx.GetTransactionInfo());
            // await _poolService.RemoveAsync(tx.GetHash());
            return res;
        }

        public async Task<Block> GetBlockAtHeight(int height)
        {
            var blockchain = _chainService.GetBlockChain(_nodeConfig.ChainId);
            return (Block) await blockchain.GetBlockByHeightAsync((ulong) height);
        }

        #region Private Methods for DPoS

        // ReSharper disable once InconsistentNaming
        private Transaction GenerateTransaction(string methodName, IReadOnlyList<byte[]> parameters,
            ulong incrementIdOffset = 0)
        {
            var tx = new Transaction
            {
                From = _nodeKeyPair.GetAddress(),
                To = ContractAccountHash,
                IncrementId = GetIncrementId(_nodeKeyPair.GetAddress()).Result + incrementIdOffset,
                MethodName = methodName,
                P = ByteString.CopyFrom(_nodeKeyPair.PublicKey.Q.GetEncoded()),
                Type = TransactionType.DposTransaction
            };

            switch (parameters.Count)
            {
                case 2:
                    tx.Params = ByteString.CopyFrom(ParamsPacker.Pack(parameters[0], parameters[1]));
                    break;
                case 3:
                    tx.Params = ByteString.CopyFrom(ParamsPacker.Pack(parameters[0], parameters[1], parameters[2]));
                    break;
                case 4:
                    tx.Params = ByteString.CopyFrom(ParamsPacker.Pack(parameters[0], parameters[1], parameters[2],
                        parameters[3]));
                    break;
            }

            var signer = new ECSigner();
            var signature = signer.Sign(_nodeKeyPair, tx.GetHash().GetHashBytes());

            // Update the signature
            tx.R = ByteString.CopyFrom(signature.R);
            tx.S = ByteString.CopyFrom(signature.S);

            return tx;
        }

        #region Broadcast Txs

        // ReSharper disable once InconsistentNaming
        public async Task MiningWithInitializingAElfDPoSInformation()
        {
            var parameters = new List<byte[]>
            {
                BlockProducers.ToByteArray(),
                _dPoSHelper.GenerateInfoForFirstTwoRounds().ToByteArray(),
                new Int32Value {Value = Globals.AElfDPoSMiningInterval}.ToByteArray()
            };
            _logger?.Trace($"Set AElf DPoS mining interval: {Globals.AElfDPoSMiningInterval} ms");
            // ReSharper disable once InconsistentNaming
            var txToInitializeAElfDPoS = GenerateTransaction("InitializeAElfDPoS", parameters);
            await BroadcastTransaction(txToInitializeAElfDPoS);

            var block = await Mine();
            await BroadcastBlock(block);
        }

        public async Task MiningWithPublishingOutValueAndSignature()
        {
            var inValue = Hash.Generate();
            if (_consensusData.Count <= 0)
            {
                _consensusData.Push(inValue.CalculateHash());
                _consensusData.Push(inValue);
            }

            var currentRoundNumber = _dPoSHelper.CurrentRoundNumber;
            var signature = Hash.Default;
            if (currentRoundNumber.Value > 1)
            {
                signature = _dPoSHelper.CalculateSignature(inValue);
            }

            var parameters = new List<byte[]>
            {
                _dPoSHelper.CurrentRoundNumber.ToByteArray(),
                new StringValue {Value = _nodeKeyPair.GetAddress().ToHex().RemoveHexPrefix()}.ToByteArray(),
                _consensusData.Pop().ToByteArray(),
                signature.ToByteArray()
            };

            var txToPublishOutValueAndSignature = GenerateTransaction("PublishOutValueAndSignature", parameters);

            await BroadcastTransaction(txToPublishOutValueAndSignature);

            var block = await Mine();
            await BroadcastBlock(block);
        }

        public async Task PublishInValue()
        {
            if (_consensusData.Count <= 0)
            {
                _incrementIdNeedToAddOne = false;
                return;
            }

            _incrementIdNeedToAddOne = true;

            var currentRoundNumber = _dPoSHelper.CurrentRoundNumber;

            var parameters = new List<byte[]>
            {
                currentRoundNumber.ToByteArray(),
                new StringValue {Value = _nodeKeyPair.GetAddress().ToHex().RemoveHexPrefix()}.ToByteArray(),
                _consensusData.Pop().ToByteArray()
            };

            var txToPublishInValue = GenerateTransaction("PublishInValue", parameters);
            await BroadcastTransaction(txToPublishInValue);
        }

        // ReSharper disable once InconsistentNaming
        public async Task MiningWithUpdatingAElfDPoSInformation()
        {
            _logger?.Log(LogLevel.Debug, "MiningWithUpdatingAElf..");
            var extraBlockResult = await _dPoSHelper.ExecuteTxsForExtraBlock();
            _logger?.Log(LogLevel.Debug, "End MiningWithUpdatingAElf..");

            var parameters = new List<byte[]>
            {
                extraBlockResult.Item1.ToByteArray(),
                extraBlockResult.Item2.ToByteArray(),
                extraBlockResult.Item3.ToByteArray()
            };
            _logger?.Log(LogLevel.Debug, "Generating transaction..");

            var txForExtraBlock = GenerateTransaction(
                "UpdateAElfDPoS",
                parameters,
                _incrementIdNeedToAddOne ? (ulong) 1 : 0);
            _logger?.Log(LogLevel.Debug, "End Generating transaction..");

            await BroadcastTransaction(txForExtraBlock);

            var block = await Mine();
            await BroadcastBlock(block);
        }

        #endregion

        #endregion
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using AElf.Types;
using Microsoft.Extensions.Logging;

namespace AElf.Kernel.Blockchain.Application
{
    public interface IBlockValidationProvider
    {
        Task<bool> ValidateBeforeAttachAsync(IBlock block);
        Task<bool> ValidateBlockBeforeExecuteAsync(IBlock block);
        Task<bool> ValidateBlockAfterExecuteAsync(IBlock block);
    }


    [Serializable]
    public class BlockValidationException : Exception
    {
        //
        // For guidelines regarding the creation of new exception types, see
        //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
        // and
        //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
        //

        public BlockValidationException()
        {
        }

        public BlockValidationException(string message) : base(message)
        {
        }

        public BlockValidationException(string message, Exception inner) : base(message, inner)
        {
        }

        protected BlockValidationException(
            SerializationInfo info,
            StreamingContext context) : base(info, context)
        {
        }
    }

    [Serializable]
    public class ValidateNextTimeBlockValidationException : BlockValidationException
    {
        //
        // For guidelines regarding the creation of new exception types, see
        //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
        // and
        //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
        //

        public ValidateNextTimeBlockValidationException()
        {
        }

        public ValidateNextTimeBlockValidationException(string message) : base(message)
        {
        }

        public ValidateNextTimeBlockValidationException(string message, Exception inner) : base(message, inner)
        {
        }

        public ValidateNextTimeBlockValidationException(Hash blockhash) : this(
            $"validate next time, block hash = {blockhash.ToHex()}")
        {
            BlockHash = blockhash;
        }

        protected ValidateNextTimeBlockValidationException(
            SerializationInfo info,
            StreamingContext context) : base(info, context)
        {
        }

        public Hash BlockHash { get; private set; }
    }

    public class BlockValidationProvider : IBlockValidationProvider
    {
        private readonly IBlockchainService _blockchainService;
        private readonly ITransactionBlockIndexService _transactionBlockIndexService;
        public ILogger<BlockValidationProvider> Logger { get; set; }

        public BlockValidationProvider(IBlockchainService blockchainService,
            ITransactionBlockIndexService transactionBlockIndexService)
        {
            _blockchainService = blockchainService;
            _transactionBlockIndexService = transactionBlockIndexService;
        }

        public async Task<bool> ValidateBeforeAttachAsync(IBlock block)
        {
            if (block?.Header == null || block.Body == null)
            {
                Logger.LogWarning("Block header or body is null.");
                return false;
            }

            if (block.Body.TransactionsCount == 0)
            {
                Logger.LogWarning("Block transactions is empty");
                return false;
            }

            var hashSet = new HashSet<Hash>();
            if (block.Body.TransactionIds.Select(item => hashSet.Add(item)).Any(addResult => !addResult))
            {
                Logger.LogWarning("Block contains duplicates transaction");
                return false;
            }

            if (_blockchainService.GetChainId() != block.Header.ChainId)
            {
                Logger.LogWarning($"Block chain id mismatch {block.Header.ChainId}");
                return false;
            }

            if (block.Header.Height != Constants.GenesisBlockHeight && !block.VerifySignature())
            {
                Logger.LogWarning("Block verify signature failed.");
                return false;
            }

            if (block.Body.CalculateMerkleTreeRoot() != block.Header.MerkleTreeRootOfTransactions)
            {
                Logger.LogWarning("Block merkle tree root mismatch.");
                return false;
            }

            if (block.Header.Height != Constants.GenesisBlockHeight &&
                block.Header.Time.ToDateTime() - TimestampHelper.GetUtcNow().ToDateTime() >
                KernelConstants.AllowedFutureBlockTimeSpan.ToTimeSpan())
            {
                Logger.LogWarning($"Future block received {block}, {block.Header.Time.ToDateTime()}");
                return false;
            }

            return true;
        }

        public async Task<bool> ValidateBlockBeforeExecuteAsync(IBlock block)
        {
            if (block?.Header == null || block.Body == null)
                return false;

            if (block.Body.TransactionsCount == 0)
                return false;

            // Verify that the transaction has been packaged in the current branch
            foreach (var transactionId in block.TransactionIds)
            {
                var blockIndex =
                    await _transactionBlockIndexService.GetCachedTransactionBlockIndexAsync(transactionId,
                        block.Header.PreviousBlockHash);
                if (blockIndex != null)
                {
                    Logger.LogWarning($"Transaction: {transactionId} repackaged.");
                    return false;
                }
            }

            return true;
        }

        public async Task<bool> ValidateBlockAfterExecuteAsync(IBlock block)
        {
            return true;
        }
    }
}
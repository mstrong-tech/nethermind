/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Diagnostics.Contracts;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class BlockProcessor : IBlockProcessor
    {
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly ISnapshotableDb _stateDb;
        private readonly ISnapshotableDb _codeDb;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;
        private readonly ITransactionStore _transactionStore;
        private readonly IRewardCalculator _rewardCalculator;

        public BlockProcessor(
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator,
            ITransactionProcessor transactionProcessor,
            ISnapshotableDb stateDb,
            ISnapshotableDb codeDb,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            ITransactionStore transactionStore,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
            _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
            _rewardCalculator = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        }

        private readonly IBlockValidator _blockValidator;

        private TransactionReceipt[] ProcessTransactions(Block block, ITraceListener traceListener)
        {
            TransactionReceipt[] receipts = new TransactionReceipt[block.Transactions.Length];
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                if (_logger.IsTrace) _logger.Trace($"Processing transaction {i}");
                var currentTx = block.Transactions[i];
                bool shouldTrace = traceListener.ShouldTrace(currentTx.Hash);
                
                (TransactionReceipt receipt, TransactionTrace trace) = _transactionProcessor.Execute(i, currentTx, block.Header, shouldTrace);
                if (shouldTrace)
                {
                    traceListener.RecordTrace(currentTx.Hash, trace);
                }

                if (currentTx.Hash == null)
                {
                    throw new InvalidOperationException("Transaction's hash is null when processing");
                }

                receipts[i] = receipt;
            }

            return receipts;
        }

        private void SetReceiptsRootAndBloom(Block block, TransactionReceipt[] receipts)
        {
            PatriciaTree receiptTree = receipts.Length > 0 ? new PatriciaTree(NullDb.Instance, Keccak.EmptyTreeHash, false) : null;
            for (int i = 0; i < receipts.Length; i++)
            {
                Rlp receiptRlp = Rlp.Encode(receipts[i], _specProvider.GetSpec(block.Header.Number).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None);
                receiptTree?.Set(Rlp.Encode(i).Bytes, receiptRlp);
            }

            receiptTree?.UpdateRootHash();

            block.Header.ReceiptsRoot = receiptTree?.RootHash ?? PatriciaTree.EmptyTreeHash;
            block.Header.Bloom = receipts.Length > 0 ? TransactionProcessor.BuildBloom(receipts) : Bloom.Empty;
        }

        public Block[] Process(Keccak branchStateRoot, Block[] suggestedBlocks, ProcessingOptions options, ITraceListener traceListener)
        {
            if (suggestedBlocks.Length == 0)
            {
                return Array.Empty<Block>();
            }

            int stateSnapshot = _stateDb.TakeSnapshot();
            int codeSnapshot = _codeDb.TakeSnapshot();
            Keccak snapshotStateRoot = _stateProvider.StateRoot;

            if (branchStateRoot != null && _stateProvider.StateRoot != branchStateRoot)
            {
                /* discarding the other branch data */
                _storageProvider.Reset();
                _stateProvider.Reset();
                _stateProvider.StateRoot = branchStateRoot;
            }

            Block[] processedBlocks = new Block[suggestedBlocks.Length];
            try
            {
                for (int i = 0; i < suggestedBlocks.Length; i++)
                {
                    processedBlocks[i] = ProcessOne(suggestedBlocks[i], options, traceListener);
                }

                if ((options & ProcessingOptions.ReadOnlyChain) != 0)
                {
                    if (_logger.IsTrace) _logger.Trace($"Reverting blocks after test run - state root {_stateProvider.StateRoot}");
                    _stateDb.Restore(stateSnapshot);
                    _codeDb.Restore(codeSnapshot);
                    _storageProvider.Reset();
                    _stateProvider.Reset();
                    _stateProvider.StateRoot = snapshotStateRoot;
                    if (_logger.IsTrace) _logger.Trace($"Reverted blocks after test run - state root {_stateProvider.StateRoot}");
                }
                else
                {
                    // todo: should be transactional so worth to look at column families
                    _stateDb.Commit();
                    _codeDb.Commit();
                }

                return processedBlocks;
            }
            catch (InvalidBlockException) // TODO: which exception to catch here?
            {
                if (_logger.IsTrace) _logger.Trace($"Reverting blocks after exception - state root {_stateProvider.StateRoot}");
                _stateDb.Restore(stateSnapshot);
                _codeDb.Restore(codeSnapshot);
                _storageProvider.Reset();
                _stateProvider.Reset();
                _stateProvider.StateRoot = snapshotStateRoot;
                if (_logger.IsTrace) _logger.Trace($"Reverted blocks after exception - state root {_stateProvider.StateRoot}");
                throw;
            }
        }

        private void ApplyDaoTransition()
        {
            Address withdrawAccount = DaoData.DaoWithdrawalAccount;
            foreach (Address daoAccount in DaoData.DaoAccounts)
            {
                UInt256 balance = _stateProvider.GetBalance(daoAccount);
                _stateProvider.AddToBalance(withdrawAccount, balance, Dao.Instance);
                _stateProvider.SubtractFromBalance(daoAccount, balance, Dao.Instance);
            }
        }

        private Block ProcessOne(Block suggestedBlock, ProcessingOptions options, ITraceListener traceListener)
        {
            Block processedBlock = suggestedBlock;
            if (!suggestedBlock.IsGenesis)
            {
                processedBlock = ProcessNonGenesis(suggestedBlock, options, traceListener);
            }

            if (_logger.IsTrace) _logger.Trace($"Committing block - state root {_stateProvider.StateRoot}");
            _stateProvider.CommitTree();
            _storageProvider.CommitTrees();

            return processedBlock;
        }

        private Block ProcessNonGenesis(Block suggestedBlock, ProcessingOptions options, ITraceListener traceListener)
        {
            if (_specProvider.DaoBlockNumber.HasValue && _specProvider.DaoBlockNumber.Value == suggestedBlock.Header.Number)
            {
                if (_logger.IsInfo) _logger.Info("Applying DAO transition");
                ApplyDaoTransition();
            }

            if (_logger.IsTrace) _logger.Trace($"Block beneficiary {suggestedBlock.Header.Beneficiary}");
            if (_logger.IsTrace) _logger.Trace($"Block gas limit {suggestedBlock.Header.GasLimit}");
            if (_logger.IsTrace) _logger.Trace($"Block gas used {suggestedBlock.Header.GasUsed}");
            if (_logger.IsTrace) _logger.Trace($"Block difficulty {suggestedBlock.Header.Difficulty}");

            (Block processedBlock, TransactionReceipt[] receipts) = ProcessBlock(
                suggestedBlock.Header.Hash,
                suggestedBlock.Header.ParentHash,
                suggestedBlock.Header.Difficulty,
                suggestedBlock.Header.Number,
                suggestedBlock.Header.Timestamp,
                suggestedBlock.Header.Beneficiary,
                suggestedBlock.Header.GasLimit,
                suggestedBlock.Header.ExtraData,
                suggestedBlock.Transactions,
                suggestedBlock.Header.MixHash,
                suggestedBlock.Header.Nonce,
                suggestedBlock.Header.OmmersHash,
                suggestedBlock.Ommers,
                traceListener);

            processedBlock.Header.TransactionsRoot = suggestedBlock.TransactionsRoot;
            processedBlock.Header.Hash = BlockHeader.CalculateHash(processedBlock.Header);
            
            if ((options & ProcessingOptions.ReadOnlyChain) == 0 &&
                (options & ProcessingOptions.NoValidation) == 0 &&
                !_blockValidator.ValidateProcessedBlock(processedBlock, suggestedBlock))
            {
                if (_logger.IsError) _logger.Error($"Processed block is not valid {suggestedBlock.ToString(Block.Format.HashAndNumber)}");
                throw new InvalidBlockException($"{suggestedBlock.ToString(Block.Format.HashAndNumber)}");
            }
            
            if ((options & ProcessingOptions.StoreReceipts) == ProcessingOptions.StoreReceipts)
            {
                for (int i = 0; i < processedBlock.Transactions.Length; i++)
                {
                    receipts[i].BlockHash = processedBlock.Hash;
                    _transactionStore.StoreProcessedTransaction(processedBlock.Transactions[i].Hash, receipts[i]);    
                }
            }

            return processedBlock;
        }

        private (Block Block, TransactionReceipt[] Receipts) ProcessBlock(
            Keccak hash,
            Keccak parentHash,
            UInt256 difficulty,
            UInt256 number,
            UInt256 timestamp,
            Address beneficiary,
            long gasLimit,
            byte[] extraData,
            Transaction[] transactions,
            Keccak mixHash,
            ulong nonce,
            Keccak ommersHash,
            BlockHeader[] ommers,
            ITraceListener traceListener)
        {
            BlockHeader header = new BlockHeader(parentHash, ommersHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData);
            header.Hash = hash;
            header.MixHash = mixHash;
            header.Nonce = nonce;
            Block block = new Block(header, transactions, ommers);
            TransactionReceipt[] receipts = ProcessTransactions(block, traceListener);
            SetReceiptsRootAndBloom(block, receipts);
            ApplyMinerRewards(block);
            _stateProvider.Commit(_specProvider.GetSpec(block.Number));
            header.StateRoot = _stateProvider.StateRoot;
            return (block, receipts);
        }

        private void ApplyMinerRewards(Block block)
        {
            if (_logger.IsTrace) _logger.Trace("Applying miner rewards:");
            BlockReward[] rewards = _rewardCalculator.CalculateRewards(block);
            for (int i = 0; i < rewards.Length; i++)
            {
                if (_logger.IsTrace) _logger.Trace($"    {((decimal) rewards[i].Value / (decimal) Unit.Ether):N3}{Unit.EthSymbol} for account at {rewards[i].Address}");
                if (!_stateProvider.AccountExists(rewards[i].Address))
                {
                    _stateProvider.CreateAccount(rewards[i].Address, (UInt256) rewards[i].Value);
                }
                else
                {
                    _stateProvider.AddToBalance(rewards[i].Address, (UInt256) rewards[i].Value, _specProvider.GetSpec(block.Number));
                }
            }
        }
    }
}
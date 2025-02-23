//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MathNet.Numerics.LinearAlgebra.Solvers;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Spec;
using Nethermind.Blockchain.Validators;
using Nethermind.Blockchain.Comparers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using NSubstitute;
using NUnit.Framework;
using Org.BouncyCastle.Math;

namespace Nethermind.Blockchain.Test.TxPools
{
    [TestFixture]
    public class TxPoolTests
    {
        private ILogManager _logManager;
        private IEthereumEcdsa _ethereumEcdsa;
        private ISpecProvider _specProvider;
        private ITxPool _txPool;
        private ITxStorage _noTxStorage;
        private ITxStorage _inMemoryTxStorage;
        private ITxStorage _persistentTxStorage;
        private IStateProvider _stateProvider;
        private IStateReader _stateReader;
        private IBlockTree _blockTree;
        
        private IBlockFinder _blockFinder;
        private int _txGasLimit = 1_000_000;

        [SetUp]
        public void Setup()
        {
            _logManager = LimboLogs.Instance;
            _specProvider = RopstenSpecProvider.Instance;
            _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, _logManager);
            _noTxStorage = NullTxStorage.Instance;
            _inMemoryTxStorage = new InMemoryTxStorage();
            _persistentTxStorage = new PersistentTxStorage(new MemDb());
            var trieStore = new TrieStore(new MemDb(), _logManager);
            var codeDb = new MemDb();
            _stateProvider = new StateProvider(trieStore, codeDb, _logManager);
            _stateReader =  new StateReader(trieStore, codeDb, _logManager);
            _blockTree = Substitute.For<IBlockTree>();
            Block block =  Build.A.Block.WithNumber(0).TestObject;
            _blockTree.Head.Returns(block);
            _blockFinder = Substitute.For<IBlockFinder>();
            _blockFinder.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(10000000).TestObject);
        }

        [Test]
        public void should_add_peers()
        {
            _txPool = CreatePool(_noTxStorage);
            var peers = GetPeers();

            foreach ((ITxPoolPeer peer, _) in peers)
            {
                _txPool.AddPeer(peer);
            }
        }

        [Test]
        public void should_delete_peers()
        {
            _txPool = CreatePool(_noTxStorage);
            var peers = GetPeers();

            foreach ((ITxPoolPeer peer, _) in peers)
            {
                _txPool.AddPeer(peer);
            }

            foreach ((ITxPoolPeer peer, _) in peers)
            {
                _txPool.RemovePeer(peer.Id);
            }
        }

        [Test]
        public void should_ignore_transactions_with_different_chain_id()
        {
            _txPool = CreatePool(_noTxStorage);
            EthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Mainnet, _logManager);
            Transaction tx = Build.A.Transaction.SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            AddTxResult result = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AddTxResult.Invalid);
        }
        
        [Test]
        public void should_ignore_transactions_with_insufficient_intrinsic_gas()
        {
            _txPool = CreatePool(_noTxStorage);
            EthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Mainnet, _logManager);
            Transaction tx = Build.A.Transaction
                .WithData(new byte[] 
                {
                    127, 243, 106, 181, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 145, 162, 136, 9, 81, 126, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    0, 128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 188, 120, 128, 96, 158, 141, 79, 126, 233, 131, 209, 47, 215, 166, 85, 190, 220, 187, 180, 115, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    0, 0, 0, 96, 44, 207, 221, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 233, 29, 21, 62, 11, 65, 81, 138, 44, 232, 221, 61, 121,
                    68, 250, 134, 52, 99, 169, 125, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 183, 211, 17, 226, 235, 85, 242, 246, 138, 148, 64, 218, 56, 231, 152, 146, 16, 185, 160, 94, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 22, 226, 139,
                    67, 163, 88, 22, 43, 150, 247, 11, 77, 225, 76, 152, 164, 70, 95, 37
                })
                .SignedAndResolved()
                .TestObject;

            AddTxResult result = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AddTxResult.Invalid);
        }

        [Test]
        public void should_not_ignore_old_scheme_signatures()
        {
            _txPool = CreatePool(_noTxStorage);
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, false).TestObject;
            EnsureSenderBalance(tx);
            AddTxResult result = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(1);
            result.Should().Be(AddTxResult.Added);
        }

        [Test]
        public void should_ignore_already_known()
        {
            _txPool = CreatePool(_noTxStorage);
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AddTxResult result1 = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            AddTxResult result2 = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(1);
            result1.Should().Be(AddTxResult.Added);
            result2.Should().Be(AddTxResult.AlreadyKnown);
        }

        [Test]
        public void should_add_valid_transactions()
        {
            _txPool = CreatePool(_noTxStorage);
            Transaction tx = Build.A.Transaction
                .WithGasLimit(_txGasLimit)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AddTxResult result = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(1);
            result.Should().Be(AddTxResult.Added);
        }
        
        [Test]
        public void should_accept_1559_transactions_only_when_eip1559_enabled([Values(false, true)] bool eip1559Enabled)
        {
            ISpecProvider specProvider = null;
            if (eip1559Enabled)
            {
                specProvider = Substitute.For<ISpecProvider>();
                specProvider.GetSpec(Arg.Any<long>()).Returns(London.Instance);
            }
            var txPool = CreatePool(_noTxStorage, null, specProvider);
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(ChainId.Mainnet)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AddTxResult result = txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            txPool.GetPendingTransactions().Length.Should().Be(eip1559Enabled ? 1 : 0);
            result.Should().Be(eip1559Enabled ? AddTxResult.Added : AddTxResult.Invalid);
        }
        
        [Test]
        public void should_ignore_insufficient_funds_for_eip1559_transactions()
        {
            var specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>()).Returns(London.Instance);
            var txPool = CreatePool(_noTxStorage, null, specProvider);
            Transaction tx = Build.A.Transaction
                .WithType(TxType.EIP1559).WithFeeCap(20)
                .WithChainId(ChainId.Mainnet)
                .WithValue(5).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx.SenderAddress, tx.MaxFeePerGas * (UInt256)tx.GasLimit); // without tx.Value so we should have InsufficientFunds
            AddTxResult result = txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AddTxResult.InsufficientFunds);
        }
        
        [Test]
        public void should_ignore_insufficient_funds_transactions()
        {
            _txPool = CreatePool(_noTxStorage);
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            AddTxResult result = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AddTxResult.InsufficientFunds);
        }
        
        [Test]
        public void should_ignore_old_nonce_transactions()
        {
            _txPool = CreatePool(_noTxStorage);
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _stateProvider.IncrementNonce(tx.SenderAddress);
            AddTxResult result = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AddTxResult.OldNonce);
        }
        
        [Test]
        public void should_ignore_transactions_too_far_into_future()
        {
            _txPool = CreatePool(_noTxStorage);
            Transaction tx = Build.A.Transaction.WithNonce(_txPool.FutureNonceRetention + 1).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AddTxResult result = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AddTxResult.FutureNonce);
        }

        [Test]
        public void should_ignore_overflow_transactions()
        {
            _txPool = CreatePool(_noTxStorage);
            Transaction tx = Build.A.Transaction.WithGasPrice(UInt256.MaxValue / Transaction.BaseTxGasCost)
                .WithGasLimit(Transaction.BaseTxGasCost)
                .WithValue(Transaction.BaseTxGasCost)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AddTxResult result = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AddTxResult.BalanceOverflow);
        }
        
        [Test]
        public void should_ignore_overflow_transactions_gas_premium_and_fee_cap()
        {
            var specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>()).Returns(London.Instance);
            var txPool = CreatePool(_noTxStorage, null, specProvider);
            Transaction tx = Build.A.Transaction.WithGasPrice(UInt256.MaxValue / Transaction.BaseTxGasCost)
                .WithGasLimit(Transaction.BaseTxGasCost)
                .WithValue(Transaction.BaseTxGasCost)
                .WithFeeCap(UInt256.MaxValue - 10)
                .WithMaxPriorityFeePerGas((UInt256)15)
                .WithType(TxType.EIP1559)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx.SenderAddress, UInt256.MaxValue);
            AddTxResult result = txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AddTxResult.BalanceOverflow);
        }
        
        [Test]
        public void should_ignore_block_gas_limit_exceeded()
        {
            _txPool = CreatePool(_noTxStorage);
            Transaction tx = Build.A.Transaction
                .WithGasLimit(Transaction.BaseTxGasCost * 5)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _txPool.BlockGasLimit = Transaction.BaseTxGasCost * 4;
            AddTxResult result = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AddTxResult.GasLimitExceeded);
        }
        
        [Test]
        public void should_ignore_tx_gas_limit_exceeded()
        {
            _txPool = CreatePool(_noTxStorage);
            Transaction tx = Build.A.Transaction
                .WithGasLimit(_txGasLimit + 1)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AddTxResult result = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(0);
            result.Should().Be(AddTxResult.GasLimitExceeded);
        }

        [Test]
        public void should_broadcast_own_transactions_that_were_reorganized_out()
        {
            _txPool = CreatePool(_noTxStorage);
            var transactions = AddOwnTransactionToPool();
            _txPool.RemoveTransaction(transactions[0].Hash);
            _txPool.AddTransaction(transactions[0], TxHandlingOptions.Reorganisation);
            Assert.AreEqual(1, _txPool.GetOwnPendingTransactions().Length);
        }


        [Test]
        public void should_broadcast_own_transactions()
        {
            _txPool = CreatePool(_noTxStorage);
            AddOwnTransactionToPool();
            Assert.AreEqual(1, _txPool.GetOwnPendingTransactions().Length);
        }
        
        [Test]
        public void should_not_broadcast_own_transactions_that_faded_out_and_came_back()
        {
            _txPool = CreatePool(_noTxStorage);
            var transactions = AddOwnTransactionToPool();
            _txPool.RemoveTransaction(transactions[0].Hash);
            _txPool.RemoveTransaction(TestItem.KeccakA);
            _txPool.AddTransaction(transactions[0], TxHandlingOptions.None);
            Assert.AreEqual(0, _txPool.GetOwnPendingTransactions().Length);
        }

        [Test]
        public async Task should_remove_transactions_concurrently()
        {
            var maxTryCount = 5;
            for (int i = 0; i < maxTryCount; ++i)
            {
                _txPool = CreatePool(_noTxStorage);
                int transactionsPerPeer = 5;
                var transactions = AddTransactionsToPool(true, false, transactionsPerPeer);
                Transaction[] transactionsForFirstTask = transactions.Where(t => t.Nonce == 8).ToArray();
                Transaction[] transactionsForSecondTask = transactions.Where(t => t.Nonce == 6).ToArray();
                Transaction[] transactionsForThirdTask = transactions.Where(t => t.Nonce == 7).ToArray();
                transactions.Should().HaveCount(transactionsPerPeer * 10);
                transactionsForFirstTask.Should().HaveCount(transactionsPerPeer);
                var firstTask = Task.Run(() => DeleteTransactionsFromPool(true, transactionsForFirstTask));
                var secondTask = Task.Run(() => DeleteTransactionsFromPool(true, transactionsForSecondTask));
                var thirdTask = Task.Run(() => DeleteTransactionsFromPool(true, transactionsForThirdTask));
                await Task.WhenAll(firstTask, secondTask, thirdTask);
                _txPool.GetPendingTransactions().Should().HaveCount(transactionsPerPeer);
            }
        }

        [TestCase(true, true,10)]
        [TestCase(false, true,100)]
        [TestCase(true, false,100)]
        [TestCase(false, false,100)]
        public void should_add_pending_transactions(bool sameTransactionSenderPerPeer, bool sameNoncePerPeer, int expectedTransactions)
        {
            _txPool = CreatePool(_noTxStorage);
            AddTransactionsToPool(sameTransactionSenderPerPeer, sameNoncePerPeer);
            _txPool.GetPendingTransactions().Length.Should().Be(expectedTransactions);
        }

        [Test]
        public void should_delete_pending_transactions()
        {
            _txPool = CreatePool(_noTxStorage);
            var transactions = AddTransactionsToPool();
            DeleteTransactionsFromPool(false, transactions);
            _txPool.GetPendingTransactions().Should().BeEmpty();
            _txPool.GetOwnPendingTransactions().Should().BeEmpty();
        }
        
        [Test]
        public void should_delete_pending_transactions_and_smaller_nonces()
        {
            _txPool = CreatePool(_noTxStorage);
            int transactionsPerPeer = 5;
            var transactions = AddTransactionsToPool(true, false, transactionsPerPeer);
            Transaction[] transactionsToDelete = transactions.Where(t => t.Nonce == 8).ToArray();
            transactions.Should().HaveCount(transactionsPerPeer * 10);
            transactionsToDelete.Should().HaveCount(transactionsPerPeer);
            DeleteTransactionsFromPool(true, transactionsToDelete);
            _txPool.GetPendingTransactions().Should().HaveCount(transactionsPerPeer);
            _txPool.GetOwnPendingTransactions().Should().HaveCount(transactionsPerPeer);
        }

        [Test]
        public void should_add_transactions_to_in_memory_storage()
        {
            var transactions = AddTransactions(_inMemoryTxStorage);
            transactions.Pending.Count().Should().Be(transactions.Persisted.Count());
        }

        [Test]
        public void should_add_transactions_to_persistent_storage()
        {
            var transactions = AddTransactions(_persistentTxStorage);
            transactions.Pending.Count().Should().Be(transactions.Persisted.Count());
        }

        [Test]
        public void should_increment_own_transaction_nonces_locally_when_requesting_reservations()
        {
            _txPool = CreatePool(_noTxStorage);
            var nonceA1 = _txPool.ReserveOwnTransactionNonce(TestItem.AddressA);
            var nonceA2 = _txPool.ReserveOwnTransactionNonce(TestItem.AddressA);
            var nonceA3 = _txPool.ReserveOwnTransactionNonce(TestItem.AddressA);
            var nonceB1 = _txPool.ReserveOwnTransactionNonce(TestItem.AddressB);
            var nonceB2 = _txPool.ReserveOwnTransactionNonce(TestItem.AddressB);
            var nonceB3 = _txPool.ReserveOwnTransactionNonce(TestItem.AddressB);

            nonceA1.Should().Be(0);
            nonceA2.Should().Be(1);
            nonceA3.Should().Be(2);
            nonceB1.Should().Be(0);
            nonceB2.Should().Be(1);
            nonceB3.Should().Be(2);
        }
        
        [Test]
        public void should_increment_own_transaction_nonces_locally_when_requesting_reservations_in_parallel()
        {
            var address = TestItem.AddressA;
            const int reservationsCount = 1000;
            _txPool = CreatePool(_noTxStorage);
            var result = Parallel.For(0, reservationsCount, i =>
            {
                _txPool.ReserveOwnTransactionNonce(address);
            });

            result.IsCompleted.Should().BeTrue();
            var nonce = _txPool.ReserveOwnTransactionNonce(address);
            nonce.Should().Be(new UInt256(reservationsCount));
        }

        [Test]
        public void should_return_own_nonce_already_used_result_when_trying_to_send_transaction_with_same_nonce_for_same_address()
        {
            _txPool = CreatePool(_noTxStorage);
            var result1 = _txPool.AddTransaction(GetTransaction(TestItem.PrivateKeyA, TestItem.AddressA), TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce);
            result1.Should().Be(AddTxResult.Added);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(1);
            _txPool.GetPendingTransactions().Length.Should().Be(1);
            var result2 = _txPool.AddTransaction(GetTransaction(TestItem.PrivateKeyA, TestItem.AddressB), TxHandlingOptions.PersistentBroadcast | TxHandlingOptions.ManagedNonce);
            result2.Should().Be(AddTxResult.OwnNonceAlreadyUsed);
            _txPool.GetOwnPendingTransactions().Length.Should().Be(1);
            _txPool.GetPendingTransactions().Length.Should().Be(1);
        }

        [Test]
        public void should_add_all_transactions_to_storage_when_using_accept_all_filter()
        {
            var transactions = AddTransactions(_inMemoryTxStorage);
            transactions.Pending.Count().Should().Be(transactions.Persisted.Count());
        }

        [Test]
        public void Should_not_try_to_load_transactions_from_storage()
        {
            var transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            _txPool = CreatePool(_inMemoryTxStorage);
            _inMemoryTxStorage.Add(transaction);
            _txPool.TryGetPendingTransaction(transaction.Hash, out var retrievedTransaction).Should().BeFalse();
        }
        
        [Test]
        public void should_retrieve_added_transaction_correctly()
        {
            var transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            EnsureSenderBalance(transaction);
            _specProvider = Substitute.For<ISpecProvider>();
            _specProvider.ChainId.Returns(transaction.Signature.ChainId.Value);
            _txPool = CreatePool(_inMemoryTxStorage);
            _txPool.AddTransaction(transaction, TxHandlingOptions.PersistentBroadcast).Should().Be(AddTxResult.Added);
            _txPool.TryGetPendingTransaction(transaction.Hash, out var retrievedTransaction).Should().BeTrue();
            retrievedTransaction.Should().BeEquivalentTo(transaction);
        }
        
        [Test]
        public void should_not_retrieve_not_added_transaction()
        {
            var transaction = Build.A.Transaction.SignedAndResolved().TestObject;
            _txPool = CreatePool(_inMemoryTxStorage);
            _txPool.TryGetPendingTransaction(transaction.Hash, out var retrievedTransaction).Should().BeFalse();
            retrievedTransaction.Should().BeNull();
        }
        
        [Test]
        public void should_notify_added_peer_of_own_tx()
        {
            _txPool = CreatePool(_noTxStorage);
            var tx = AddOwnTransactionToPool().First();
            ITxPoolPeer txPoolPeer = Substitute.For<ITxPoolPeer>();
            txPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(txPoolPeer);
            txPoolPeer.Received().SendNewTransaction(tx, false);
        }
        
        [Test]
        public async Task should_notify_peer_only_once()
        {
            _txPool = CreatePool(_noTxStorage);
            ITxPoolPeer txPoolPeer = Substitute.For<ITxPoolPeer>();
            txPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _txPool.AddPeer(txPoolPeer);
            var tx = AddOwnTransactionToPool().First();
            await Task.Delay(1000);
            txPoolPeer.Received(1).SendNewTransaction(tx, true);
        }
        
        [Test]
        public void should_drop_own_transaction_over_limit()
        {
            _txPool = CreatePool(_noTxStorage, new TxPoolConfig() {Size = 10});
            AddTransactionsToPool(transactionsPerPeer: 5); //generates 50 tx with 1..9 gasprice per 5 peers, only 8 up will be kept 
            _txPool.GetOwnPendingTransactions().Min(t => t.GasPrice).Should().Be(8);
        }
        
        [Test]
        public void should_accept_access_list_transactions_only_when_eip2930_enabled([Values(false, true)] bool eip2930Enabled)
        {
            if (!eip2930Enabled)
            {
                _blockFinder.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(RopstenSpecProvider.BerlinBlockNumber - 1).TestObject);
            }
            
            _txPool = CreatePool(_noTxStorage);
            Transaction tx = Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithChainId(ChainId.Mainnet)
                .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            AddTxResult result = _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.GetPendingTransactions().Length.Should().Be(eip2930Enabled ? 1 : 0);
            result.Should().Be(eip2930Enabled ? AddTxResult.Added : AddTxResult.Invalid);
        }

        [Test]
        public void should_return_true_when_asking_for_txHash_existing_in_pool()
        {
            _txPool = CreatePool(_noTxStorage);
            Transaction tx = Build.A.Transaction.SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
            EnsureSenderBalance(tx);
            _txPool.AddTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.IsInHashCache(tx.Hash).Should().Be(true);
            _txPool.RemoveTransaction(tx.Hash).Should().Be(true);
        }
        
        [Test]
        public void should_return_false_when_asking_for_not_known_txHash()
        {
            _txPool = CreatePool(_noTxStorage);
            _txPool.IsInHashCache(TestItem.KeccakA).Should().Be(false);
            _txPool.RemoveTransaction(TestItem.KeccakA).Should().Be(false);
        }

        private Transactions AddTransactions(ITxStorage storage)
        {
            _txPool = CreatePool(storage);

            var pendingTransactions = AddTransactionsToPool();
            var persistedTransactions = GetTransactionsFromStorage(storage, pendingTransactions);

            return new Transactions(pendingTransactions, persistedTransactions);
        }

        private IDictionary<ITxPoolPeer, PrivateKey> GetPeers(int limit = 100)
        {
            var peers = new Dictionary<ITxPoolPeer, PrivateKey>();
            for (var i = 0; i < limit; i++)
            {
                var privateKey = Build.A.PrivateKey.TestObject;
                peers.Add(GetPeer(privateKey.PublicKey), privateKey);
            }

            return peers;
        }

        private TxPool.TxPool CreatePool(ITxStorage txStorage, ITxPoolConfig config = null, ISpecProvider specProvider = null)
        {
            specProvider ??= RopstenSpecProvider.Instance;
            ITransactionComparerProvider transactionComparerProvider =
                new TransactionComparerProvider(specProvider, _blockTree);
            return new TxPool.TxPool(txStorage, _ethereumEcdsa, new ChainHeadInfoProvider(specProvider, _blockFinder, _stateProvider),
                config ?? new TxPoolConfig() { GasLimit = _txGasLimit },
                new TxValidator(_specProvider.ChainId), _logManager, transactionComparerProvider.GetDefaultComparer());
        }

        private ITxPoolPeer GetPeer(PublicKey publicKey)
        {
            ITxPoolPeer peer = Substitute.For<ITxPoolPeer>();
            peer.Id.Returns(publicKey);
            
            return peer;
        }

        private Transaction[] AddTransactionsToPool(bool sameTransactionSenderPerPeer = true, bool sameNoncePerPeer= false, int transactionsPerPeer = 10)
        {
            var transactions = GetTransactions(GetPeers(transactionsPerPeer), sameTransactionSenderPerPeer, sameNoncePerPeer);
            foreach (var transaction in transactions)
            {
                _txPool.AddTransaction(transaction, TxHandlingOptions.PersistentBroadcast);
            }

            return transactions;
        }

        private Transaction[] AddOwnTransactionToPool()
        {
            var transaction = GetTransaction(TestItem.PrivateKeyA, Address.Zero);
            _txPool.AddTransaction(transaction, TxHandlingOptions.PersistentBroadcast);
            return new[] {transaction};
        }

        private void DeleteTransactionsFromPool(bool removeSmallerNonces, params Transaction[] transactions)
        {
            foreach (var transaction in transactions)
            {
                _txPool.RemoveTransaction(transaction.Hash, removeSmallerNonces);
            }
        }

        private static IEnumerable<Transaction> GetTransactionsFromStorage(ITxStorage storage,
            IEnumerable<Transaction> transactions)
            => transactions.Select(t => storage.Get(t.Hash)).Where(t => !(t is null)).ToArray();

        private Transaction[] GetTransactions(IDictionary<ITxPoolPeer, PrivateKey> peers, bool sameTransactionSenderPerPeer = true, bool sameNoncePerPeer = true, int transactionsPerPeer = 10)
        {
            var transactions = new List<Transaction>();
            foreach ((_, PrivateKey privateKey) in peers)
            {
                for (var i = 0; i < transactionsPerPeer; i++)
                {
                    transactions.Add(GetTransaction(sameTransactionSenderPerPeer ? privateKey : Build.A.PrivateKey.TestObject, Address.FromNumber((UInt256)i), sameNoncePerPeer ? UInt256.Zero : (UInt256?) i));
                }
            }

            return transactions.ToArray();
        }

        private Transaction GetTransaction(PrivateKey privateKey, Address to = null, UInt256? nonce = null)
        {
            Transaction transaction = GetTransaction(nonce ?? UInt256.Zero, GasCostOf.Transaction, nonce ?? 1000, to, Array.Empty<byte>(), privateKey);
            EnsureSenderBalance(transaction);
            return transaction;
        }

        private void EnsureSenderBalance(Transaction transaction)
        {
            EnsureSenderBalance(transaction.SenderAddress, transaction.GasPrice * (UInt256)transaction.GasLimit + transaction.Value);
        }
        
        private void EnsureSenderBalance(Address address, UInt256 balance)
        {
            _stateProvider.CreateAccount(address, balance);
        }

        private Transaction GetTransaction(UInt256 nonce, long gasLimit, UInt256 gasPrice, Address to, byte[] data,
            PrivateKey privateKey)
            => Build.A.Transaction
                .WithNonce(nonce)
                .WithGasLimit(gasLimit)
                .WithGasPrice(gasPrice)
                .WithData(data)
                .To(to)
                .DeliveredBy(privateKey.PublicKey)
                .SignedAndResolved(_ethereumEcdsa, privateKey)
                .TestObject;

        private class Transactions
        {
            public IEnumerable<Transaction> Pending { get; }
            public IEnumerable<Transaction> Persisted { get; }

            public Transactions(IEnumerable<Transaction> pending, IEnumerable<Transaction> persisted)
            {
                Pending = pending;
                Persisted = persisted;
            }
        }
    }
}

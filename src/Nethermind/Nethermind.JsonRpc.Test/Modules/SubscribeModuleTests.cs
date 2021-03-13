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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SubscribeModuleTests
    {
        private ISubscribeModule _subscribeModule;
        private ILogManager _logManager;
        private IBlockTree _blockTree;
        private ITxPool _txPool;
        private IReceiptStorage _receiptStorage;
        private IFilterStore _filterStore;
        private ISubscriptionManager _subscriptionManager;
        private IJsonRpcDuplexClient _jsonRpcDuplexClient;
        private IJsonSerializer _jsonSerializer;

        [SetUp]
        public void Setup()
        {
            _logManager = Substitute.For<ILogManager>();
            _blockTree = Substitute.For<IBlockTree>();
            _txPool = Substitute.For<ITxPool>();
            _receiptStorage = Substitute.For<IReceiptStorage>();
            _filterStore = new FilterStore();
            _jsonRpcDuplexClient = Substitute.For<IJsonRpcDuplexClient>();
            _jsonSerializer = new EthereumJsonSerializer();
            
            SubscriptionFactory subscriptionFactory = new SubscriptionFactory(
                _logManager,
                _blockTree,
                _txPool,
                _receiptStorage,
                _filterStore);
            
            _subscriptionManager = new SubscriptionManager(
                subscriptionFactory,
                _logManager);
            
            _subscribeModule = new SubscribeModule(_subscriptionManager);
            _subscribeModule.Context = new JsonRpcContext(RpcEndpoint.WebSocket, _jsonRpcDuplexClient);
            
            // block numbers matching filters in LogsSubscriptions with null arguments will be 33333-77777
            BlockHeader fromBlock = Build.A.BlockHeader.WithNumber(33333).TestObject;
            BlockHeader toBlock = Build.A.BlockHeader.WithNumber(77777).TestObject;
            _blockTree.FindHeader(Arg.Any<BlockParameter>()).Returns(fromBlock);
            _blockTree.FindHeader(Arg.Any<BlockParameter>(), true).Returns(toBlock);
        }

        private JsonRpcResult GetNewHeadBlockResult(BlockEventArgs blockEventArgs, out string subscriptionId)
        {
            NewHeadSubscription newHeadSubscription = new NewHeadSubscription(_jsonRpcDuplexClient, _blockTree, _logManager);
            
            JsonRpcResult jsonRpcResult = new JsonRpcResult();
            
            ManualResetEvent manualResetEvent = new ManualResetEvent(false);
            newHeadSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));
            
            _blockTree.NewHeadBlock += Raise.EventWith(new object(), blockEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(100));

            subscriptionId = newHeadSubscription.Id;
            return jsonRpcResult;
        }
        
        private List<JsonRpcResult> GetLogsSubscriptionResult(Filter filter, ReceiptsEventArgs receiptsEventArgs, out string subscriptionId)
        {
            LogsSubscription logsSubscription = new LogsSubscription(_jsonRpcDuplexClient, _receiptStorage, _filterStore, _blockTree, _logManager, filter);
            
            List<JsonRpcResult> jsonRpcResults = new List<JsonRpcResult>();

            SemaphoreSlim semaphoreSlim = new SemaphoreSlim(0, 1);
            logsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResults.Add(j);
            }));
            
            _receiptStorage.ReceiptsInserted += Raise.EventWith(new object(), receiptsEventArgs);
            semaphoreSlim.Wait(TimeSpan.FromMilliseconds(100));

            subscriptionId = logsSubscription.Id;
            return jsonRpcResults;
        }
        
        private JsonRpcResult GetNewPendingTransactionsResult(TxEventArgs txEventArgs, out string subscriptionId)
        {
            NewPendingTransactionsSubscription newPendingTransactionsSubscription = new NewPendingTransactionsSubscription(_jsonRpcDuplexClient, _txPool, _logManager);
            JsonRpcResult jsonRpcResult = new JsonRpcResult();

            ManualResetEvent manualResetEvent = new ManualResetEvent(false);
            newPendingTransactionsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));
            
            _txPool.NewPending += Raise.EventWith(new object(), txEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(100));

            subscriptionId = newPendingTransactionsSubscription.Id;
            return jsonRpcResult;
        }

        private SyncingSubscription GetSyncingSubscription(int bestSuggested, int head)
        {
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(bestSuggested).TestObject;
            _blockTree.FindBestSuggestedHeader().Returns(blockHeader);

            Block block = Build.A.Block.WithNumber(head).TestObject;
            _blockTree.Head.Returns(block);
            
            SyncingSubscription syncingSubscription = new SyncingSubscription(_jsonRpcDuplexClient, _blockTree, _logManager);
            
            return syncingSubscription;
        }
        
        private JsonRpcResult GetSyncingSubscriptionResult(bool newHead, SyncingSubscription syncingSubscription, BlockEventArgs blockEventArgs)
        {
            JsonRpcResult jsonRpcResult = new JsonRpcResult();

            ManualResetEvent manualResetEvent = new ManualResetEvent(false);
            syncingSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));

            if (newHead) _blockTree.NewHeadBlock += Raise.EventWith(new object(), blockEventArgs);
            else _blockTree.NewBestSuggestedBlock += Raise.EventWith(new object(), blockEventArgs);

            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(100));

            return jsonRpcResult;
        }
        
        [Test]
        public void Wrong_subscription_name()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeModule, "eth_subscribe", "wrongSubscriptionType");
            var expectedResult = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Wrong subscription type: wrongSubscriptionType.\"},\"id\":67}";
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void No_subscription_name()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeModule, "eth_subscribe");
            var expectedResult = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params\",\"data\":\"Incorrect parameters count, expected: 2, actual: 0\"},\"id\":67}";
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void NewHeadSubscription_creating_result()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeModule, "eth_subscribe", "newHeads");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44,34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void NewHeadSubscription_on_NewHeadBlock_event()
        {
            Block block = Build.A.Block.WithDifficulty(1991).WithExtraData(new byte[] {3, 5, 8}).TestObject;
            BlockEventArgs blockEventArgs = new BlockEventArgs(block);

            JsonRpcResult jsonRpcResult = GetNewHeadBlockResult(blockEventArgs, out var subscriptionId);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0x7c7\",\"extraData\":\"0x030508\",\"gasLimit\":\"0x3d0900\",\"gasUsed\":\"0x0\",\"hash\":\"0x2e3c1c2a507dc3071a16300858d4e75390e5f43561515481719a1e0dadf22585\",\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x2ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e2\",\"nonce\":\"0x00000000000003e8\",\"number\":\"0x0\",\"parentHash\":\"0xff483e972a04a9a62bb4b7d04ae403c615604e4090521ecc5bb7af67f71be09c\",\"receiptsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x200\",\"stateRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"totalDifficulty\":\"0x0\",\"timestamp\":\"0xf4240\",\"transactions\":[],\"transactionsRoot\":\"0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\",\"uncles\":[]}}}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void NewHeadSubscription_on_NewHeadBlock_event_with_null_block()
        {
            BlockEventArgs blockEventArgs = new BlockEventArgs(null);
            
            JsonRpcResult jsonRpcResult = GetNewHeadBlockResult(blockEventArgs, out _);

            jsonRpcResult.Response.Should().BeNull();
        }
        
        [Test]
        public void LogsSubscription_with_null_arguments_creating_result()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeModule, "eth_subscribe", "logs");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44,34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void LogsSubscription_with_valid_arguments_creating_result()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeModule, "eth_subscribe", "logs","{\"fromBlock\":\"latest\",\"toBlock\":\"latest\",\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"topics\":\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"}");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44,34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void LogsSubscription_with_invalid_arguments_creating_result()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeModule, "eth_subscribe", "logs","trambabamba");
            var expectedResult = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params\"},\"id\":67}";
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void LogsSubscription_with_null_arguments_on_ReceiptsInserted_event()
        {
            int blockNumber = 55555;
            Filter filter = null;
            
            LogEntry logEntry = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            TxReceipt[] txReceipts = new TxReceipt[] {Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntry).TestObject};
            
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;
            ReceiptsEventArgs receiptsEventArgs = new ReceiptsEventArgs(blockHeader, txReceipts);
            
            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, receiptsEventArgs, out var subscriptionId);

            jsonRpcResults.Count.Should().Be(1);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void LogsSubscription_with_not_matching_block_on_ReceiptsInserted_event()
        {
            int blockNumber = 22222;
            Filter filter = null;

            LogEntry logEntry = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            TxReceipt[] txReceipts = new TxReceipt[] {Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntry).TestObject};
        
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;
            ReceiptsEventArgs receiptsEventArgs = new ReceiptsEventArgs(blockHeader, txReceipts);
        
            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, receiptsEventArgs, out _);

            jsonRpcResults.Count.Should().Be(0);
        }
        
        [Test]
        public void LogsSubscription_with_null_arguments_on_ReceiptsInserted_event_with_one_TxReceipt_with_few_logs()
        {
            int blockNumber = 77777;
            Filter filter = null;
            
            LogEntry logEntryA = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            LogEntry logEntryB = Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakB).TestObject;
            LogEntry logEntryC = Build.A.LogEntry.WithData(TestItem.RandomDataC).TestObject;

            TxReceipt[] txReceipts = new TxReceipt[] {Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryA, logEntryB, logEntryC).TestObject};
            
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;
            ReceiptsEventArgs receiptsEventArgs = new ReceiptsEventArgs(blockHeader, txReceipts);
        
            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, receiptsEventArgs, out var subscriptionId);

            jsonRpcResults.Count.Should().Be(3);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0x12fd1\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
            
            serialized = _jsonSerializer.Serialize(jsonRpcResults[1].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"blockNumber\":\"0x12fd1\",\"data\":\"0x\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x1\"}}}");
            expectedResult.Should().Be(serialized);
            
            serialized = _jsonSerializer.Serialize(jsonRpcResults[2].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0x0000000000000000000000000000000000000000\",\"blockNumber\":\"0x12fd1\",\"data\":\"0x010208090a\",\"logIndex\":\"0x2\",\"removed\":false,\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x2\"}}}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void LogsSubscription_with_null_arguments_on_ReceiptsInserted_event_with_few_TxReceipts_with_few_logs()
        {
            int blockNumber = 55555;
            Filter filter = null;
            
            LogEntry logEntryA = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            LogEntry logEntryB = Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakB).TestObject;
            LogEntry logEntryC = Build.A.LogEntry.WithData(TestItem.RandomDataC).TestObject;

            TxReceipt[] txReceipts = new TxReceipt[]
            {
                Build.A.Receipt.WithBlockNumber(blockNumber).WithIndex(11).WithLogs(logEntryA).TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithIndex(22).WithLogs(logEntryA, logEntryB).TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithIndex(33).WithLogs(logEntryB, logEntryC).TestObject
            };
            
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;
            ReceiptsEventArgs receiptsEventArgs = new ReceiptsEventArgs(blockHeader, txReceipts);
        
            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, receiptsEventArgs, out var subscriptionId);

            jsonRpcResults.Count.Should().Be(5);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0xb\",\"transactionLogIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
            
            serialized = _jsonSerializer.Serialize(jsonRpcResults[1].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x16\",\"transactionLogIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
            
            serialized = _jsonSerializer.Serialize(jsonRpcResults[2].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"blockNumber\":\"0xd903\",\"data\":\"0x\",\"logIndex\":\"0x2\",\"removed\":false,\"topics\":[\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\"],\"transactionIndex\":\"0x16\",\"transactionLogIndex\":\"0x1\"}}}");
            expectedResult.Should().Be(serialized);
            
            serialized = _jsonSerializer.Serialize(jsonRpcResults[3].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"blockNumber\":\"0xd903\",\"data\":\"0x\",\"logIndex\":\"0x3\",\"removed\":false,\"topics\":[\"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111\"],\"transactionIndex\":\"0x21\",\"transactionLogIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
            
            serialized = _jsonSerializer.Serialize(jsonRpcResults[4].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0x0000000000000000000000000000000000000000\",\"blockNumber\":\"0xd903\",\"data\":\"0x010208090a\",\"logIndex\":\"0x4\",\"removed\":false,\"topics\":[\"0x0000000000000000000000000000000000000000000000000000000000000000\"],\"transactionIndex\":\"0x21\",\"transactionLogIndex\":\"0x1\"}}}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void LogsSubscription_on_ReceiptsInserted_event_with_few_TxReceipts_with_few_logs_with_some_address_mismatches()
        {
            int blockNumber = 55555;
            Filter filter = new Filter()
            {
                FromBlock = BlockParameter.Latest,
                ToBlock = BlockParameter.Latest,
                Address = "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                Topics = new []{TestItem.KeccakA}
            };

            LogEntry logEntryA = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            LogEntry logEntryB = Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakB).WithData(TestItem.RandomDataB).TestObject;
            LogEntry logEntryC = Build.A.LogEntry.WithData(TestItem.RandomDataC).TestObject;
            

            TxReceipt[] txReceipts = new TxReceipt[]
            {
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryA, logEntryB, logEntryC).TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs().TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryB).TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryC, logEntryC, logEntryB, logEntryC, logEntryC, logEntryB).TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryA, logEntryC, logEntryB, logEntryA, logEntryC).TestObject,
            };

            Bloom bloom = new Bloom(txReceipts.Select(r => r.Bloom).ToArray());
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).WithBloom(bloom).TestObject;
            ReceiptsEventArgs receiptsEventArgs = new ReceiptsEventArgs(blockHeader, txReceipts);
        
            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, receiptsEventArgs, out var subscriptionId);

            jsonRpcResults.Count.Should().Be(3);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
            
            serialized = _jsonSerializer.Serialize(jsonRpcResults[1].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
            
            serialized = _jsonSerializer.Serialize(jsonRpcResults[2].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x2\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x1\"}}}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void LogsSubscription_on_ReceiptsInserted_event_with_few_TxReceipts_with_few_logs_with_some_topic_mismatches()
        {
            int blockNumber = 55555;

            Filter filter = new Filter()
            {
                FromBlock = BlockParameter.Latest,
                ToBlock = BlockParameter.Latest,
                Address = "0xb7705ae4c6f81b66cdb323c65f4e8133690fc099",
                Topics = new []{TestItem.KeccakA}
            };

            LogEntry logEntryA = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).WithData(TestItem.RandomDataA).TestObject;
            LogEntry logEntryB = Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakB).WithData(TestItem.RandomDataB).TestObject;
            LogEntry logEntryC = Build.A.LogEntry.WithAddress(TestItem.AddressC).WithData(TestItem.RandomDataC).TestObject;
            

            TxReceipt[] txReceipts = new TxReceipt[]
            {
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryA, logEntryB, logEntryC).TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs().TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryB).TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryC, logEntryC, logEntryB, logEntryC, logEntryC, logEntryB).TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryA, logEntryC, logEntryB, logEntryA, logEntryC).TestObject,
            };

            Bloom bloom = new Bloom(txReceipts.Select(r => r.Bloom).ToArray());
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).WithBloom(bloom).TestObject;
            ReceiptsEventArgs receiptsEventArgs = new ReceiptsEventArgs(blockHeader, txReceipts);
        
            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, receiptsEventArgs, out var subscriptionId);

            jsonRpcResults.Count.Should().Be(3);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
            
            serialized = _jsonSerializer.Serialize(jsonRpcResults[1].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
            
            serialized = _jsonSerializer.Serialize(jsonRpcResults[2].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x2\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x1\"}}}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void LogsSubscription_on_ReceiptsInserted_event_with_few_TxReceipts_with_few_logs_with_few_topics_and_some_address_and_topic_mismatches()
        {
            int blockNumber = 55555;
            IEnumerable<object> topics = new List<object>(){TestItem.KeccakA};

            Filter filter = new Filter()
            {
                FromBlock = BlockParameter.Latest,
                ToBlock = BlockParameter.Latest,
                Address = new []{"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099", "0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358"},
                Topics = new []{TestItem.KeccakA, TestItem.KeccakD}
            };

            LogEntry logEntryA = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA, TestItem.KeccakD).WithData(TestItem.RandomDataA).TestObject;
            LogEntry logEntryB = Build.A.LogEntry.WithAddress(TestItem.AddressC).WithTopics(TestItem.KeccakA, TestItem.KeccakD).WithData(TestItem.RandomDataB).TestObject;
            LogEntry logEntryC = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithData(TestItem.RandomDataC).TestObject;
            LogEntry logEntryD = Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics(TestItem.KeccakA, TestItem.KeccakD, TestItem.KeccakE).WithData(TestItem.RandomDataB).TestObject;
            LogEntry logEntryE = Build.A.LogEntry.WithTopics(TestItem.KeccakA, TestItem.KeccakD).WithData(TestItem.RandomDataB).TestObject;

            

            TxReceipt[] txReceipts = new TxReceipt[]
            {
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryA, logEntryB, logEntryC).TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs().TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryB).TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryE, logEntryE, logEntryB, logEntryD, logEntryE, logEntryB).TestObject,
                Build.A.Receipt.WithBlockNumber(blockNumber).WithLogs(logEntryC, logEntryB, logEntryE, logEntryA, logEntryB).TestObject,
            };
            
            Bloom bloom = new Bloom(txReceipts.Select(r => r.Bloom).ToArray());
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).WithBloom(bloom).TestObject;
            ReceiptsEventArgs receiptsEventArgs = new ReceiptsEventArgs(blockHeader, txReceipts);
        
            List<JsonRpcResult> jsonRpcResults = GetLogsSubscriptionResult(filter, receiptsEventArgs, out var subscriptionId);

            jsonRpcResults.Count.Should().Be(3);
            string serialized = _jsonSerializer.Serialize(jsonRpcResults[0].Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x0\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
            
            serialized = _jsonSerializer.Serialize(jsonRpcResults[1].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"blockNumber\":\"0xd903\",\"data\":\"0x04050607\",\"logIndex\":\"0x1\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\",\"0x434b529473163ef4ed9c9341d9b7250ab9183c27e7add004c3bba38c56274e24\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
            
            serialized = _jsonSerializer.Serialize(jsonRpcResults[2].Response);
            expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"blockNumber\":\"0xd903\",\"data\":\"0x010203\",\"logIndex\":\"0x2\",\"removed\":false,\"topics\":[\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\",\"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2\"],\"transactionIndex\":\"0x0\",\"transactionLogIndex\":\"0x0\"}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void NewPendingTransactionsSubscription_creating_result()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeModule, "eth_subscribe", "newPendingTransactions");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44,34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void NewPendingTransactionsSubscription_on_NewPending_event()
        {
            Transaction transaction = Build.A.Transaction.TestObject;
            transaction.Hash = TestItem.KeccakA;
            TxEventArgs txEventArgs = new TxEventArgs(transaction);
            
            JsonRpcResult jsonRpcResult = GetNewPendingTransactionsResult(txEventArgs, out var subscriptionId);
            
            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":\"", TestItem.KeccakA, "\"}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void NewPendingTransactionsSubscription_on_NewPending_event_with_null_transaction()
        {
            TxEventArgs txEventArgs = new TxEventArgs(null);
            
            JsonRpcResult jsonRpcResult = GetNewPendingTransactionsResult(txEventArgs, out _);
            
            jsonRpcResult.Response.Should().BeNull();
        }
        
        [Test]
        public void NewPendingTransactionsSubscription_on_NewPending_event_with_null_transactions_hash()
        {
            Transaction transaction = Build.A.Transaction.TestObject;
            transaction.Hash = null;
            TxEventArgs txEventArgs = new TxEventArgs(transaction);
            
            JsonRpcResult jsonRpcResult = GetNewPendingTransactionsResult(txEventArgs, out var subscriptionId);
            
            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\"}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void SyncingSubscription_creating_result()
        {
            BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
            _blockTree.FindBestSuggestedHeader().Returns(blockHeader);

            Block block = Build.A.Block.TestObject;
            _blockTree.Head.Returns(block);

            string serialized = RpcTest.TestSerializedRequest(_subscribeModule, "eth_subscribe", "syncing");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44,34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void SyncingSubscription_on_NewHeadBlock_event_when_sync_no_change()
        {
            SyncingSubscription syncingSubscription = GetSyncingSubscription(10042, 10024);

            Block blockChanged = Build.A.Block.WithNumber(10030).TestObject;
            BlockEventArgs blockEventArgs = new BlockEventArgs(blockChanged);
            _blockTree.Head.Returns(blockChanged);
            
            JsonRpcResult jsonRpcResult = GetSyncingSubscriptionResult(true, syncingSubscription, blockEventArgs);
            
            jsonRpcResult.Response.Should().BeNull();
        }
        
        [Test]
        public void SyncingSubscription_on_NewBestSuggestedBlock_event_when_sync_no_change()
        {
            SyncingSubscription syncingSubscription = GetSyncingSubscription(10042, 10024);

            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(10045).TestObject;
            Block block = new Block(blockHeader, BlockBody.Empty);
            BlockEventArgs blockEventArgs = new BlockEventArgs(block);
            _blockTree.FindBestSuggestedHeader().Returns(blockHeader);
            
            JsonRpcResult jsonRpcResult = GetSyncingSubscriptionResult(false, syncingSubscription, blockEventArgs);
            
            jsonRpcResult.Response.Should().BeNull();
        }
        
        [Test]
        public void SyncingSubscription_on_NewHeadBlock_event_when_sync_changed_to_false()
        {
            SyncingSubscription syncingSubscription = GetSyncingSubscription(10042, 10024);

            Block blockChanged = Build.A.Block.WithNumber(10040).TestObject;
            BlockEventArgs blockEventArgs = new BlockEventArgs(blockChanged);
            _blockTree.Head.Returns(blockChanged);
            
            JsonRpcResult jsonRpcResult = GetSyncingSubscriptionResult(true, syncingSubscription, blockEventArgs);
            
            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", syncingSubscription.Id, "\",\"result\":false}}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void SyncingSubscription_on_NewBestSuggestedBlock_event_when_sync_changed_to_false()
        {
            SyncingSubscription syncingSubscription = GetSyncingSubscription(10042, 10024);

            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(10030).TestObject;
            Block block = new Block(blockHeader, BlockBody.Empty);
            BlockEventArgs blockEventArgs = new BlockEventArgs(block);
            _blockTree.FindBestSuggestedHeader().Returns(blockHeader);
            
            JsonRpcResult jsonRpcResult = GetSyncingSubscriptionResult(false, syncingSubscription, blockEventArgs);
            
            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", syncingSubscription.Id, "\",\"result\":false}}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void SyncingSubscription_on_NewHeadBlock_event_when_sync_changed_to_true()
        {
            SyncingSubscription syncingSubscription = GetSyncingSubscription(10042, 10040);

            Block blockChanged = Build.A.Block.WithNumber(10024).TestObject;
            BlockEventArgs blockEventArgs = new BlockEventArgs(blockChanged);
            _blockTree.Head.Returns(blockChanged);
            
            JsonRpcResult jsonRpcResult = GetSyncingSubscriptionResult(true, syncingSubscription, blockEventArgs);
            
            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", syncingSubscription.Id, "\",\"result\":{\"isSyncing\":true,\"startingBlock\":\"0x0\",\"currentBlock\":\"0x2728\",\"highestBlock\":\"0x273a\"}}}");
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void SyncingSubscription_on_NewBestSuggestedBlock_event_when_sync_changed_to_true()
        {
            SyncingSubscription syncingSubscription = GetSyncingSubscription(10042, 10040);

            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(10099).TestObject;
            Block block = new Block(blockHeader, BlockBody.Empty);
            BlockEventArgs blockEventArgs = new BlockEventArgs(block);
            _blockTree.FindBestSuggestedHeader().Returns(blockHeader);
            
            JsonRpcResult jsonRpcResult = GetSyncingSubscriptionResult(true, syncingSubscription, blockEventArgs);
            
            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\"", syncingSubscription.Id, "\",\"result\":{\"isSyncing\":true,\"startingBlock\":\"0x0\",\"currentBlock\":\"0x2738\",\"highestBlock\":\"0x2773\"}}}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void Eth_unsubscribe_success()
        {
            string serializedSub = RpcTest.TestSerializedRequest(_subscribeModule, "eth_subscribe", "newHeads");
            string subscriptionId = serializedSub.Substring(serializedSub.Length - 44, 34);
            string expectedSub = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", subscriptionId, "\",\"id\":67}");
            expectedSub.Should().Be(serializedSub);

            string serializedUnsub = RpcTest.TestSerializedRequest(_subscribeModule, "eth_unsubscribe", subscriptionId);
            string expectedUnsub = "{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}";

            expectedUnsub.Should().Be(serializedUnsub);
        }

        [Test]
        public void Subscriptions_remove_after_closing_websockets_client()
        {
            string serializedLogs = RpcTest.TestSerializedRequest(_subscribeModule, "eth_subscribe", "logs");
            string logsId = serializedLogs.Substring(serializedLogs.Length - 44, 34);
            string expectedLogs = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", logsId, "\",\"id\":67}");
            expectedLogs.Should().Be(serializedLogs);


            string serializedNewPendingTx =
                RpcTest.TestSerializedRequest(_subscribeModule, "eth_subscribe", "newPendingTransactions");
            string newPendingTxId = serializedNewPendingTx.Substring(serializedNewPendingTx.Length - 44, 34);
            string expectedNewPendingTx =
                string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", newPendingTxId, "\",\"id\":67}");
            expectedNewPendingTx.Should().Be(serializedNewPendingTx);

            _jsonRpcDuplexClient.Closed += Raise.Event();

            string serializedLogsUnsub = RpcTest.TestSerializedRequest(_subscribeModule, "eth_unsubscribe", logsId);
            string expectedLogsUnsub =
                string.Concat("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Failed to unsubscribe: ",
                    logsId, ".\",\"data\":false},\"id\":67}");
            expectedLogsUnsub.Should().Be(serializedLogsUnsub);

            string serializedNewPendingTxUnsub =
                RpcTest.TestSerializedRequest(_subscribeModule, "eth_unsubscribe", newPendingTxId);
            string expectedNewPendingTxUnsub =
                string.Concat("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Failed to unsubscribe: ",
                    newPendingTxId, ".\",\"data\":false},\"id\":67}");
            expectedNewPendingTxUnsub.Should().Be(serializedNewPendingTxUnsub);
        }
    }
}

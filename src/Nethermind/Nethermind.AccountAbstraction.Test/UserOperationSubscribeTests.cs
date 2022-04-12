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
using System.Threading;
using FluentAssertions;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.AccountAbstraction.Subscribe;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.TxPool;
using Newtonsoft.Json;

namespace Nethermind.AccountAbstraction.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class UserOperationSubscribeTests
    {
        private ISubscribeRpcModule _subscribeRpcModule = null!;
        private ILogManager _logManager = null!;
        private IBlockTree _blockTree = null!;
        private ITxPool _txPool = null!;
        private IReceiptStorage _receiptStorage = null!;
        private IFilterStore _filterStore = null!;
        private ISubscriptionManager _subscriptionManager = null!;
        private IJsonRpcDuplexClient _jsonRpcDuplexClient = null!;
        private IJsonSerializer _jsonSerializer = null!;
        private ISpecProvider _specProvider = null!;
        private IDictionary<Address, IUserOperationPool> _userOperationPools = new Dictionary<Address, IUserOperationPool>();
        //Any test pool and entry point addresses should work for testing.
        private Address _testPoolAddress = Address.Zero;
        private Address _entryPointAddress = new("0x90f3e1105e63c877bf9587de5388c23cdb702c6b");
        
        [SetUp]
        public void Setup()
        {
            _logManager = Substitute.For<ILogManager>();
            _blockTree = Substitute.For<IBlockTree>();
            _txPool = Substitute.For<ITxPool>();
            _receiptStorage = Substitute.For<IReceiptStorage>();
            _specProvider = Substitute.For<ISpecProvider>();
            _userOperationPools[_testPoolAddress] = Substitute.For<IUserOperationPool>();
            _filterStore = new FilterStore();
            _jsonRpcDuplexClient = Substitute.For<IJsonRpcDuplexClient>();
            _jsonSerializer = new EthereumJsonSerializer();

            JsonSerializer jsonSerializer = new();
            jsonSerializer.Converters.AddRange(EthereumJsonSerializer.CommonConverters);
            
            SubscriptionFactory subscriptionFactory = new(
                _logManager,
                _blockTree,
                _txPool,
                _receiptStorage,
                _filterStore,
                new EthSyncingInfo(_blockTree),
                _specProvider,
                jsonSerializer);
            
            subscriptionFactory.RegisterSubscriptionType<UserOperationSubscriptionParam?>(
                "newPendingUserOperations",
                (jsonRpcDuplexClient,entryPoints) => new NewPendingUserOpsSubscription(
                    jsonRpcDuplexClient,
                    _userOperationPools,
                    _logManager,
                    entryPoints)
            );
            subscriptionFactory.RegisterSubscriptionType<UserOperationSubscriptionParam?>(
                "newReceivedUserOperations",
                (jsonRpcDuplexClient,entryPoints) => new NewReceivedUserOpsSubscription(
                    jsonRpcDuplexClient,
                    _userOperationPools,
                    _logManager,
                    entryPoints)
            );
            
            _subscriptionManager = new SubscriptionManager(
                subscriptionFactory,
                _logManager);
            
            _subscribeRpcModule = new SubscribeRpcModule(_subscriptionManager);
            _subscribeRpcModule.Context = new JsonRpcContext(RpcEndpoint.Ws, _jsonRpcDuplexClient);
        }

        private JsonRpcResult GetNewPendingUserOpsResult(
            UserOperationEventArgs userOperationEventArgs,
            out string subscriptionId,
            bool includeUserOperations = false)
        {
            UserOperationSubscriptionParam param = new() {IncludeUserOperations = includeUserOperations};

            NewPendingUserOpsSubscription newPendingUserOpsSubscription =
                new(_jsonRpcDuplexClient, _userOperationPools, _logManager, param);
            JsonRpcResult jsonRpcResult = new();

            ManualResetEvent manualResetEvent = new(false);
            newPendingUserOpsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));

            _userOperationPools[_testPoolAddress].NewPending += Raise.EventWith(new object(), userOperationEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(100));

            subscriptionId = newPendingUserOpsSubscription.Id;
            return jsonRpcResult;
        }
        
        private JsonRpcResult GetNewReceivedUserOpsResult(
            UserOperationEventArgs userOperationEventArgs,
            out string subscriptionId, 
            bool includeUserOperations = false)
        {
            UserOperationSubscriptionParam param = new() {IncludeUserOperations = includeUserOperations};

            NewReceivedUserOpsSubscription newReceivedUserOpsSubscription =
                new(_jsonRpcDuplexClient, _userOperationPools, _logManager, param);
            JsonRpcResult jsonRpcResult = new();

            ManualResetEvent manualResetEvent = new(false);
            newReceivedUserOpsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));

            _userOperationPools[_testPoolAddress].NewReceived += Raise.EventWith(new object(), userOperationEventArgs);
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(100));

            subscriptionId = newReceivedUserOpsSubscription.Id;
            return jsonRpcResult;
        }

        [Test]
        public void NewPendingUserOperationsSubscription_creating_result()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newPendingUserOperations");
            string expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44,34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void NewPendingUserOperationsSubscription_creating_result_with_custom_entryPoints()
        {
            string serialized = RpcTest.TestSerializedRequest(
                _subscribeRpcModule,
                "eth_subscribe",
                "newPendingUserOperations",
                "{\"entryPoints\":[\"" + _entryPointAddress + "\", \"" + Address.Zero + "\"]}");
            string expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44,34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void NewPendingUserOperationsSubscription_creating_result_with_wrong_entryPoints()
        {
            string serialized = RpcTest.TestSerializedRequest(
                _subscribeRpcModule,
                "eth_subscribe",
                "newPendingUserOperations", "{\"entryPoints\":[\"" + _entryPointAddress + "\", \"" + "0x123" + "\"]}");

            string beginningOfExpectedResult = "{\"jsonrpc\":\"2.0\",\"error\":";
            beginningOfExpectedResult.Should().Be(serialized.Substring(0,beginningOfExpectedResult.Length));
        }

        [Test]
        public void NewPendingUserOperationsSubscription_on_NewPending_event()
        {
            UserOperation userOperation = Build.A.UserOperation.TestObject;
            userOperation.CalculateRequestId(_entryPointAddress, 1);
            UserOperationEventArgs userOperationEventArgs = new(userOperation, _entryPointAddress);

            JsonRpcResult jsonRpcResult = GetNewPendingUserOpsResult(userOperationEventArgs, out var subscriptionId, true);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            string expectedResult = Expected_text_response_to_UserOperation_event_with_full_ops(userOperation, subscriptionId);
            serialized.Should().Be(expectedResult);
        }
        
        [Test]
        public void NewPendingUserOperationsSubscription_on_NewPending_event_without_full_user_operations()
        {
            UserOperation userOperation = Build.A.UserOperation.TestObject;
            userOperation.CalculateRequestId(_entryPointAddress, 1);
            UserOperationEventArgs userOperationEventArgs = new(userOperation, _entryPointAddress);

            JsonRpcResult jsonRpcResult = GetNewPendingUserOpsResult(userOperationEventArgs, out var subscriptionId, false);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            string expectedResult = Expected_text_response_to_UserOperation_event_without_full_ops(userOperation, subscriptionId);
            serialized.Should().Be(expectedResult);
        }
        
        [Test]
        public void NewReceivedUserOperationsSubscription_creating_result()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newReceivedUserOperations");
            var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44,34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void NewReceivedUserOperationsSubscription_creating_result_with_custom_entryPoints()
        {
            string serialized = RpcTest.TestSerializedRequest(
                _subscribeRpcModule,
                "eth_subscribe",
                "newReceivedUserOperations",
                "{\"entryPoints\":[\"" + _entryPointAddress + "\", \"" + Address.Zero + "\"]}");
            string expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44,34), "\",\"id\":67}");
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void NewReceivedUserOperationsSubscription_creating_result_with_wrong_entryPoints()
        {
            string serialized = RpcTest.TestSerializedRequest(
                _subscribeRpcModule,
                "eth_subscribe",
                "newPendingUserOperations", "{\"entryPoints\":[\"" + _entryPointAddress + "\", \"" + "0x123" + "\"]}");
            string beginningOfExpectedResult = "{\"jsonrpc\":\"2.0\",\"error\":";
            beginningOfExpectedResult.Should().Be(serialized.Substring(0,beginningOfExpectedResult.Length));
        }

        [Test]
        public void NewReceivedUserOperationsSubscription_on_NewPending_event()
        {
            UserOperation userOperation = Build.A.UserOperation.TestObject;
            userOperation.CalculateRequestId(_entryPointAddress, 1);
            UserOperationEventArgs userOperationEventArgs = new(userOperation, _entryPointAddress);

            JsonRpcResult jsonRpcResult = GetNewReceivedUserOpsResult(userOperationEventArgs, out var subscriptionId, true);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            string expectedResult = Expected_text_response_to_UserOperation_event_with_full_ops(userOperation, subscriptionId);
            expectedResult.Should().Be(serialized);
        }
        
        [Test]
        public void NewReceivedUserOperationsSubscription_on_NewPending_event_without_full_user_operations()
        {
            UserOperation userOperation = Build.A.UserOperation.TestObject;
            userOperation.CalculateRequestId(_entryPointAddress, 1);
            UserOperationEventArgs userOperationEventArgs = new(userOperation, _entryPointAddress);

            JsonRpcResult jsonRpcResult = GetNewReceivedUserOpsResult(userOperationEventArgs, out var subscriptionId, false);

            jsonRpcResult.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
            string expectedResult = Expected_text_response_to_UserOperation_event_without_full_ops(userOperation, subscriptionId);
            expectedResult.Should().Be(serialized);
        }

        [Test]
        public void Eth_unsubscribe_success()
        {
            string serializedSub = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newPendingUserOperations");
            string subscriptionId = serializedSub.Substring(serializedSub.Length - 44, 34);
            string expectedSub = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", subscriptionId, "\",\"id\":67}");
            expectedSub.Should().Be(serializedSub);

            string serializedUnsub = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_unsubscribe", subscriptionId);
            string expectedUnsub = "{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}";

            expectedUnsub.Should().Be(serializedUnsub);
        }

        [Test]
        public void Subscriptions_remove_after_closing_websockets_client()
        {
            string serialized = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_subscribe", "newPendingUserOperations");
            string subscriptionId = serialized.Substring(serialized.Length - 44, 34);
            string expectedId = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", subscriptionId, "\",\"id\":67}");
            expectedId.Should().Be(serialized);

            _jsonRpcDuplexClient.Closed += Raise.Event();

            string serializedLogsUnsub = RpcTest.TestSerializedRequest(_subscribeRpcModule, "eth_unsubscribe", subscriptionId);
            string expectedLogsUnsub =
                string.Concat("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Failed to unsubscribe: ",
                    subscriptionId, ".\",\"data\":false},\"id\":67}");
            expectedLogsUnsub.Should().Be(serializedLogsUnsub);
        }

        private string Expected_text_response_to_UserOperation_event_with_full_ops(UserOperation userOperation, string subscriptionId) => 
            "{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\""
                   + subscriptionId
                   + "\",\"result\":{\"userOperation\":{\"sender\":\""
                   + userOperation.Sender
                   + "\",\"nonce\":\"0x0\",\"callData\":\""
                   + userOperation.CallData.ToHexString(true)
                   + "\",\"initCode\":\""
                   + userOperation.InitCode.ToHexString(true)
                   + "\",\"callGas\":\""
                   + userOperation.CallGas.ToHexString(true)
                   + "\",\"verificationGas\":\""
                   + userOperation.VerificationGas.ToHexString(true)
                   + "\",\"preVerificationGas\":\""
                   + userOperation.PreVerificationGas.ToHexString(true)
                   + "\",\"maxFeePerGas\":\""
                   + userOperation.MaxFeePerGas.ToHexString(true)
                   + "\",\"maxPriorityFeePerGas\":\""
                   + userOperation.MaxPriorityFeePerGas.ToHexString(true) 
                   + "\",\"paymaster\":\""
                   + userOperation.Paymaster
                   + "\",\"signature\":\""
                   + userOperation.Signature.ToHexString(true)
                   + "\",\"paymasterData\":\""
                   + userOperation.PaymasterData.ToHexString(true)
                   + "\"},\"entryPoint\":\""
                   + _entryPointAddress
                   + "\"}}}";
        
        private string Expected_text_response_to_UserOperation_event_without_full_ops(UserOperation userOperation, string subscriptionId) => 
            "{\"jsonrpc\":\"2.0\",\"method\":\"eth_subscription\",\"params\":{\"subscription\":\""
            + subscriptionId
            + "\",\"result\":{\"userOperation\":\""
            + userOperation.RequestId
            + "\",\"entryPoint\":\""
            + _entryPointAddress
            + "\"}}}";
    }
}
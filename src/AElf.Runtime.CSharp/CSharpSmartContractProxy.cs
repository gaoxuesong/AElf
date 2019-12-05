using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using AElf.Kernel;
using AElf.Kernel.SmartContract;
using AElf.Kernel.SmartContract.Sdk;
using AElf.Sdk.CSharp;
using AElf.Types;

namespace AElf.Runtime.CSharp
{
    public class CSharpSmartContractProxy
    {
        private static MethodInfo GetMethodInfo(Type type, string name)
        {
            return type.GetMethod(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
        }

        private object _instance;
        private Type _counterType;

        private Dictionary<string, MethodInfo> _methodInfos = new Dictionary<string, MethodInfo>();

        public CSharpSmartContractProxy(object instance, Type counterType)
        {
            _instance = instance;
            _counterType = counterType;
            InitializeMethodInfos(_instance.GetType());
        }

        private void InitializeMethodInfos(Type instanceType)
        {
            _methodInfos = new[]
            {
                nameof(GetChanges),nameof(Cleanup),nameof(InternalInitialize)
            }.ToDictionary(x => x, x => GetMethodInfo(instanceType, x));
        }

        public void InternalInitialize(ISmartContractBridgeContext context)
        {
            _methodInfos[nameof(InternalInitialize)].Invoke(_instance, new object[] {context});
            
            // Initialize the counter if injected
            _counterType
                ?.GetMethod(nameof(ExecutionObserverProxy.Initialize), new []{ typeof(IExecutionObserver) })
                ?.Invoke(_instance, new object[] { context.ExecutionObserver });
        }

        public TransactionExecutingStateSet GetChanges()
        {
            return (TransactionExecutingStateSet) _methodInfos[nameof(GetChanges)]
                .Invoke(_instance, new object[0]);
        }

        internal void Cleanup()
        {
            _methodInfos[nameof(Cleanup)].Invoke(_instance, new object[0]);
        }
    }
}
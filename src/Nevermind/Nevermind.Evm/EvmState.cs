using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Nevermind.Core;

namespace Nevermind.Evm
{
    [DebuggerDisplay("{ExecutionType} to {Env.ExecutingAccount}, G {GasAvailable} R {Refund} PC {ProgramCounter} OUT {OutputDestination}:{OutputLength}")]
    public class EvmState
    {
        public readonly byte[][] BytesOnStack = new byte[VirtualMachine.MaxStackSize][];
        public readonly bool[] IntPositions = new bool[VirtualMachine.MaxStackSize];
        public readonly BigInteger[] IntsOnStack = new BigInteger[VirtualMachine.MaxStackSize];

        private HashSet<Address> _destroyList = new HashSet<Address>();
        private List<LogEntry> _logs = new List<LogEntry>();
        public int StackHead = 0;

        public EvmState(ulong gasAvailable, ExecutionEnvironment env, ExecutionType executionType, bool isContinuation)
            : this(gasAvailable, env, executionType, -1, -1, BigInteger.Zero, BigInteger.Zero, false, isContinuation)
        {
            GasAvailable = gasAvailable;
            Env = env;
        }

        internal EvmState(
            ulong gasAvailable,
            ExecutionEnvironment env,
            ExecutionType executionType,
            int stateSnapshot,
            int storageSnapshot,
            BigInteger outputDestination,
            BigInteger outputLength,
            bool isStatic,
            bool isContinuation)
        {
            GasAvailable = gasAvailable;
            ExecutionType = executionType;
            StateSnapshot = stateSnapshot;
            StorageSnapshot = storageSnapshot;
            Env = env;
            OutputDestination = outputDestination;
            OutputLength = outputLength;
            IsStatic = isStatic;
            IsContinuation = isContinuation;
        }

        public ExecutionEnvironment Env { get; }
        public ulong GasAvailable { get; set; }
        public BigInteger ProgramCounter { get; set; }

        internal ExecutionType ExecutionType { get; }
        internal BigInteger OutputDestination { get; }
        internal BigInteger OutputLength { get; }
        public bool IsStatic { get; }
        public bool IsContinuation { get; set; }
        public int StateSnapshot { get; }
        public int StorageSnapshot { get; }

        public ulong Refund { get; set; }
        public EvmMemory Memory { get; } = new EvmMemory();

        public HashSet<Address> DestroyList
        {
            get { return LazyInitializer.EnsureInitialized(ref _destroyList, () => new HashSet<Address>()); }
        }

        public List<LogEntry> Logs
        {
            get { return LazyInitializer.EnsureInitialized(ref _logs, () => new List<LogEntry>()); }
        }
    }
}
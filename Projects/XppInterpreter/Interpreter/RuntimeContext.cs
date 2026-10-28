using System;
using System.Collections.Generic;
using System.Linq;
using XppInterpreter.Core;
using XppInterpreter.Interpreter.Bytecode;
using XppInterpreter.Interpreter.Debug;
using XppInterpreter.Interpreter.Proxy;
using XppInterpreter.Interpreter.Query;

namespace XppInterpreter.Interpreter
{
    public sealed class InterpreterLimitExceededException : Exception
    {
        public InterpreterLimitExceededException(string message) : base(message)
        {
        }
    }

    public sealed class RuntimeExecutionGuard
    {
        private readonly int _maxInstructions;
        private readonly int _maxExecutionMilliseconds;
        private readonly System.Diagnostics.Stopwatch _activeTime = new System.Diagnostics.Stopwatch();
        private long _instructionCount;
        private int _executionDepth;

        public long InstructionCount => _instructionCount;

        public RuntimeExecutionGuard(XppInterpreterOptions options)
        {
            _maxInstructions = Math.Max(0, options?.MaxInstructions ?? 0);
            _maxExecutionMilliseconds = Math.Max(0, options?.MaxExecutionMilliseconds ?? 0);
        }

        public void Checkpoint()
        {
            Checkpoint(1);
        }

        /// <summary>
        /// Counts a batch of instructions at once; used by compiled loops at each back edge
        /// </summary>
        public void Checkpoint(int instructions)
        {
            _instructionCount += instructions;

            if (_maxInstructions > 0 && _instructionCount > _maxInstructions)
            {
                throw new InterpreterLimitExceededException($"Interpreter instruction limit of {_maxInstructions} was exceeded.");
            }

            if (_maxExecutionMilliseconds > 0 &&
                _activeTime.ElapsedMilliseconds > _maxExecutionMilliseconds)
            {
                throw new InterpreterLimitExceededException($"Interpreter time limit of {_maxExecutionMilliseconds} ms was exceeded.");
            }
        }

        public void EnterExecution()
        {
            if (_executionDepth++ == 0)
            {
                _activeTime.Start();
            }
        }

        public void ExitExecution()
        {
            if (_executionDepth > 0 && --_executionDepth == 0)
            {
                _activeTime.Stop();
            }
        }
    }

    public class RuntimeContext
    {
        private int _pc = 0;
        public int Counter => _pc;
        public Stack<object> Stack => ScopeHandler.CurrentScope.Stack;
        public Dictionary<Parser.Data.Query, ISearchInstance> Queries = new Dictionary<Parser.Data.Query, ISearchInstance>();
        public Stack<IDisposable> Disposables = new Stack<IDisposable>();
        public readonly ScopeHandler ScopeHandler;
        public readonly XppProxy Proxy;
        public readonly ByteCode ByteCode;
        public Exception LastException;

        public bool Returned;
        public RuntimeContext InnerContext;
        public XppInterpreter Interpreter;
        public DebugAction NextAction;

        /// <summary>
        /// Script class whose method is executing in this context. This is used to
        /// enforce X++ private and protected access during runtime dispatch.
        /// </summary>
        public RuntimeClass ActiveClass;
        public RuntimeClassMethod ActiveMethod;
        public RuntimeExecutionGuard ExecutionGuard;
        private Metadata.AosTypeDescriptorProvider _aosTypes;
        public Metadata.AosTypeDescriptorProvider AosTypes =>
            _aosTypes ?? (_aosTypes = new Metadata.AosTypeDescriptorProvider(Proxy));
        private Metadata.DynamicModelRegistry _models;
        public Metadata.DynamicModelRegistry Models
        {
            get
            {
                if (_models is null) _models = new Metadata.DynamicModelRegistry(this);
                _models.Synchronize(ByteCode.DeclaredClasses);
                return _models;
            }
        }

        public void InheritExecutionState(RuntimeContext parent)
        {
            if (parent is null)
            {
                return;
            }

            ExecutionGuard = parent.ExecutionGuard;
            ActiveClass = parent.ActiveClass;
        }

        public RuntimeContext(XppProxy proxy, ByteCode bytecode)
        {
            Proxy = proxy;
            ScopeHandler = new ScopeHandler(proxy);
            ByteCode = bytecode;
        }

        public RuntimeContext(XppProxy proxy, ByteCode bytecode, ScopeHandler scopeHandler)
        {
            Proxy = proxy;
            ScopeHandler = scopeHandler;
            ByteCode = bytecode;
        }

        public RuntimeContext(XppProxy proxy, ByteCode bytecode, ScopeHandler scopeHandler, int counter) : this(proxy, bytecode, scopeHandler)
        {
            SetCounter(counter);
        }

        public void MoveCounter(int offset)
        {
            if (_pc + offset < 0)
            {
                throw new Exception("Program counter cannot be negative.");
            }

            _pc += offset;
        }

        public void SetCounter(int counter)
        {
            if (counter < 0)
            {
                throw new Exception("Program counter cannot be negative.");
            }

            _pc = counter;
        }

        public void EndScopeRange(int start, int end)
        {
            var rangeToCheck = ByteCode.Instructions.GetRange(start, end);

            // Count the number of scope instructions
            int beginScopeCount = rangeToCheck.Count(instruction => instruction is BeginScope);
            int endScopeCount = rangeToCheck.Count(instruction => instruction is EndScope);

            // The number of scopes to close is the number of orphane begin scope instructions
            ScopeHandler.EndScope(Math.Abs(endScopeCount - beginScopeCount));
        }

    }
}

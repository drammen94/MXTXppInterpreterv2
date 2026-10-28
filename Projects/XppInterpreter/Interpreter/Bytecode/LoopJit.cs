using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using XppInterpreter.Core;
using TType = XppInterpreter.Lexer.TType;

namespace XppInterpreter.Interpreter.Bytecode
{
    /// <summary>
    /// Compiles hot loops to IL at runtime, with no build step.
    /// A loop qualifies when every instruction in it is a constant push, a plain variable load or
    /// assignment, arithmetic, a jump, or a scope that declares nothing. Variables stay in their
    /// VariableEntry objects, so the rest of the interpreter sees every write. Whenever the compiled
    /// code meets a case it cannot prove it handles exactly like the interpreter (an exception, an
    /// overflow, a conversion that fails), it hands control back to the interpreter at that
    /// instruction with the operand stack and scopes rebuilt.
    /// </summary>
    public static class LoopJit
    {
        /// <summary>Turns loop compilation on or off for every interpreter in the process.</summary>
        public static bool Enabled = true;

        /// <summary>Iterations a loop must run in the interpreter before it is compiled.</summary>
        public static int Threshold = 64;

        /// <summary>Compile on the calling thread instead of the thread pool (tests and benchmarks).</summary>
        public static bool CompileSynchronously;

        private static int _compiledCount;

        /// <summary>Number of loops compiled since the process started.</summary>
        public static int CompiledCount => _compiledCount;

        internal static readonly object Deopt = new object();
        private static readonly object BoxedTrue = true;
        private static readonly object BoxedFalse = false;

        private sealed class RegionTable
        {
            public List<IInstruction> Instructions;
            public int Count;
            public LoopRegion[] Regions;
        }

        private static readonly ConditionalWeakTable<ByteCode, RegionTable> _tables = new ConditionalWeakTable<ByteCode, RegionTable>();

        /// <summary>
        /// Loop regions indexed by the program counter of their head, or null when the bytecode has none
        /// </summary>
        internal static LoopRegion[] GetRegions(ByteCode byteCode)
        {
            if (!Enabled || byteCode?.Instructions is null)
            {
                return null;
            }

            RegionTable table = _tables.GetValue(byteCode, _ => new RegionTable());

            lock (table)
            {
                if (!ReferenceEquals(table.Instructions, byteCode.Instructions) || table.Count != byteCode.Instructions.Count)
                {
                    table.Regions = FindRegions(byteCode.Instructions);
                    table.Instructions = byteCode.Instructions;
                    table.Count = byteCode.Instructions.Count;
                }

                return table.Regions;
            }
        }

        private static LoopRegion[] FindRegions(List<IInstruction> instructions)
        {
            var heads = new Dictionary<int, int>();

            for (int pc = 0; pc < instructions.Count; pc++)
            {
                if (IsPlainJump(instructions[pc], out Jump jump) && jump.Offset < 0)
                {
                    int head = pc + jump.Offset;

                    if (head >= 0)
                    {
                        heads[head] = heads.TryGetValue(head, out int end) ? Math.Max(end, pc + 1) : pc + 1;
                    }
                }
            }

            LoopRegion[] regions = null;

            foreach (var loop in heads)
            {
                LoopRegion region = LoopRegion.TryCreate(instructions, loop.Key, loop.Value);

                if (region != null)
                {
                    if (regions is null)
                    {
                        regions = new LoopRegion[instructions.Count];
                    }

                    regions[loop.Key] = region;
                }
            }

            return regions;
        }

        internal static bool IsPlainJump(IInstruction instruction, out Jump jump)
        {
            Type type = instruction?.GetType();
            jump = instruction as Jump;
            return type == typeof(Jump) || type == typeof(JumpIfFalse) || type == typeof(JumpIfTrue);
        }

        /// <summary>
        /// A jump moves the counter by its offset; an offset of zero leaves it in place, so the interpreter steps on
        /// </summary>
        internal static int JumpTarget(int pc, int offset)
        {
            return offset == 0 ? pc + 1 : pc + offset;
        }

        // Helpers called from compiled code. Each one either produces exactly what the interpreter
        // would, or reports that the interpreter must take over; none of them leaves partial state behind.

        internal static object Arith(JitFrame frame, Arithmetic operation, object left, object right)
        {
            if (operation.TryFastPath(left, right, out object result))
            {
                return result;
            }

            try
            {
                return operation.Evaluate(frame.Context, left, right);
            }
            catch (InterpreterLimitExceededException)
            {
                throw;
            }
            catch
            {
                // The interpreter re-executes the instruction and raises the exception itself
                return Deopt;
            }
        }

        internal static bool Assign(JitFrame frame, Store store, VariableEntry entry, object value)
        {
            var casting = frame.Context.Proxy.Casting;

            // Mirrors Scope.SetVar for a value that already has the declared type: no cast, no truncation
            if (value != null && entry.DeclarationType != null && value.GetType() == entry.DeclarationType &&
                !(casting != null && entry.IsStringEdt(casting)))
            {
                entry.SetValue(value);
                return true;
            }

            try
            {
                store.AssignExisting(frame.Context, value);
                return true;
            }
            catch (InterpreterLimitExceededException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Returns 1 for true, 0 for false, and -1 when the interpreter must evaluate the condition.</summary>
        internal static int Truth(JitFrame frame, object value)
        {
            if (value is bool boolean)
            {
                return boolean ? 1 : 0;
            }

            if (value is ScriptObject)
            {
                return 1;
            }

            try
            {
                return frame.Context.Proxy.Casting.ToBoolean(value) ? 1 : 0;
            }
            catch (InterpreterLimitExceededException)
            {
                throw;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Counts a loop iteration against the execution limits. A limit breach is kept on the frame so the
        /// compiled code can write its variables back first; the interpreter then rethrows it.
        /// </summary>
        internal static bool Checkpoint(JitFrame frame, int instructions)
        {
            try
            {
                frame.Context.ExecutionGuard?.Checkpoint(instructions);
                return true;
            }
            catch (InterpreterLimitExceededException exception)
            {
                frame.Pending = exception;
                return false;
            }
        }

        internal static void NoteCompiled()
        {
            Interlocked.Increment(ref _compiledCount);
        }

        internal static object Box(bool value)
        {
            return value ? BoxedTrue : BoxedFalse;
        }
    }

    /// <summary>
    /// State passed between the interpreter and a compiled loop. On return the compiled code leaves
    /// the operand stack and the number of open (elided) scopes for the instruction it resumes at.
    /// </summary>
#pragma warning disable CS0649 // StackCount, ScopeDepth and Pending are assigned by the generated code
    internal sealed class JitFrame
    {
        public RuntimeContext Context;
        public VariableEntry[] Entries;
        public object[] Stack;
        public int StackCount;
        public int ScopeDepth;
        public Exception Pending;
    }
#pragma warning restore CS0649

    internal sealed class LoopRegion
    {
        private const int StateNew = 0, StateCompiling = 1, StateReady = 2, StateFailed = 3;

        private readonly IInstruction[] _code;
        private readonly int _start;
        private readonly int[] _depth;
        private readonly int[] _scopes;
        private readonly int[] _variable;
        private readonly string[] _names;
        private readonly int _maxDepth;

        private int _hits;
        private int _state;
        private Func<JitFrame, int> _generic;
        private Func<JitFrame, int> _typed;
        private Type[] _typedVariables;

        private LoopRegion(IInstruction[] code, int start, int[] depth, int[] scopes, int[] variable, string[] names, int maxDepth)
        {
            _code = code;
            _start = start;
            _depth = depth;
            _scopes = scopes;
            _variable = variable;
            _names = names;
            _maxDepth = maxDepth;
        }

        private int End => _start + _code.Length;

        /// <summary>
        /// Checks every path through [start, end) for supported instructions and works out the
        /// operand-stack depth and scope depth at each instruction. Returns null when the loop does not qualify.
        /// </summary>
        /// <remarks>
        /// The interpreter leaves values it never reads on the stack: a conditional jump that pushes
        /// its condition back for a statement, and whatever sits in a scope's own stack while an inner
        /// scope runs. So where paths meet with different depths, the target keeps the smallest one, and
        /// that is only accepted when it is zero (a statement boundary, where nothing below is read again).
        /// </remarks>
        internal static LoopRegion TryCreate(List<IInstruction> instructions, int start, int end)
        {
            int length = end - start;
            var depth = Enumerable.Repeat(-1, length).ToArray();
            var scopes = new int[length];
            var variable = Enumerable.Repeat(-1, length).ToArray();
            var names = new List<string>();
            var nameIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var next = new List<(int Target, int Depth, int Scopes)>(2);

            int Variable(string name)
            {
                if (!nameIndex.TryGetValue(name, out int index))
                {
                    index = names.Count;
                    names.Add(name);
                    nameIndex.Add(name, index);
                }

                return index;
            }

            // Lists the successors of one instruction; false when it is unsupported or would read below the stack
            bool Step(int pc, int d, int s)
            {
                next.Clear();
                IInstruction instruction = instructions[pc];
                Type type = instruction.GetType();
                int k = pc - start;

                if (type == typeof(Push))
                {
                    next.Add((pc + 1, d + 1, s));
                }
                else if (type == typeof(VariableLoad) && !((VariableLoad)instruction).IsArray)
                {
                    variable[k] = Variable(((VariableLoad)instruction).Name);
                    next.Add((pc + 1, d + 1, s));
                }
                else if (type == typeof(Store) && instruction is Store store && !store.FromStack && !store.IsArray && !store.Top)
                {
                    if (d < 1) return false;
                    variable[k] = Variable(store.Name);
                    next.Add((pc + 1, d - 1, s));
                }
                else if (type == typeof(Arithmetic))
                {
                    if (d < 2) return false;
                    next.Add((pc + 1, d - 1, s));
                }
                else if (type == typeof(BeginScope))
                {
                    next.Add((pc + 1, 0, s + 1)); // the new scope has its own, empty stack
                }
                else if (type == typeof(EndScope))
                {
                    if (s < 1) return false;
                    next.Add((pc + 1, 0, s - 1));
                }
                else if (type == typeof(Jump))
                {
                    next.Add((LoopJit.JumpTarget(pc, ((Jump)instruction).Offset), d, s));
                }
                else if (type == typeof(JumpIfFalse) || type == typeof(JumpIfTrue))
                {
                    if (d < 1) return false;
                    bool returnToStack = instruction is JumpIfFalse jif ? jif.ReturnToStack : ((JumpIfTrue)instruction).ReturnToStack;
                    next.Add((pc + 1, d - 1, s));
                    next.Add((LoopJit.JumpTarget(pc, ((Jump)instruction).Offset), d - 1 + (returnToStack ? 1 : 0), s));
                }
                else
                {
                    return false;
                }

                foreach (var successor in next)
                {
                    if (successor.Target < 0 || successor.Target > instructions.Count)
                    {
                        return false;
                    }
                }

                return true;
            }

            bool Inside(int target) => target >= start && target < end;

            // Fixpoint: depths only ever decrease, so this terminates
            var work = new Stack<int>();
            depth[0] = 0;
            work.Push(start);

            while (work.Count > 0)
            {
                int pc = work.Pop();
                int k = pc - start;

                if (!Step(pc, depth[k], scopes[k]))
                {
                    return null;
                }

                foreach (var successor in next)
                {
                    if (!Inside(successor.Target))
                    {
                        continue; // leaves the region; the interpreter continues there
                    }

                    int t = successor.Target - start;

                    if (depth[t] == -1)
                    {
                        depth[t] = successor.Depth;
                        scopes[t] = successor.Scopes;
                        work.Push(successor.Target);
                    }
                    else if (scopes[t] != successor.Scopes)
                    {
                        return null;
                    }
                    else if (successor.Depth < depth[t])
                    {
                        depth[t] = successor.Depth;
                        work.Push(successor.Target);
                    }
                }
            }

            // Validation with the final depths: every edge either agrees or lands on a statement boundary
            int maxDepth = 0;

            for (int k = 0; k < length; k++)
            {
                if (depth[k] < 0)
                {
                    continue;
                }

                if (!Step(start + k, depth[k], scopes[k]))
                {
                    return null;
                }

                foreach (var successor in next)
                {
                    maxDepth = Math.Max(maxDepth, successor.Depth);

                    if (Inside(successor.Target))
                    {
                        int t = successor.Target - start;

                        if (scopes[t] != successor.Scopes || (depth[t] != successor.Depth && depth[t] != 0))
                        {
                            return null;
                        }
                    }
                }
            }

            return new LoopRegion(instructions.GetRange(start, length).ToArray(), start, depth, scopes, variable, names.ToArray(), maxDepth);
        }

        /// <summary>
        /// Runs the loop compiled when it is ready. Returns false when the interpreter should execute
        /// the head instruction itself; otherwise sets the counter to resume at.
        /// </summary>
        internal bool TryRun(RuntimeContext context, out int resume)
        {
            resume = -1;
            Func<JitFrame, int> generic = Volatile.Read(ref _generic);

            if (generic is null)
            {
                if (Volatile.Read(ref _state) != StateNew ||
                    Interlocked.Increment(ref _hits) < LoopJit.Threshold ||
                    Interlocked.CompareExchange(ref _state, StateCompiling, StateNew) != StateNew)
                {
                    return false;
                }

                // Variable types are read here, on the executing thread; compiling touches no proxy
                Type[] observed = ObserveVariableTypes(context);

                if (LoopJit.CompileSynchronously)
                {
                    Compile(observed);
                }
                else
                {
                    ThreadPool.QueueUserWorkItem(_ => Compile(observed));
                }

                generic = Volatile.Read(ref _generic);

                if (generic is null)
                {
                    return false;
                }
            }

            VariableEntry[] entries = ResolveEntries(context);

            if (entries is null)
            {
                return false; // the interpreter reports the undeclared variable
            }

            if (context.ScopeHandler.AreExceptionsHandled)
            {
                context.ScopeHandler.CurrentExceptionHandler.SetNativeRetryCount(context);
            }

            var frame = new JitFrame
            {
                Context = context,
                Entries = entries,
                Stack = new object[Math.Max(1, _maxDepth)]
            };

            Func<JitFrame, int> typed = Volatile.Read(ref _typed);
            resume = typed != null && MatchesTypes(entries, _typedVariables, context) ? typed(frame) : generic(frame);

            for (int i = 0; i < frame.ScopeDepth; i++)
            {
                context.ScopeHandler.BeginScope();
            }

            for (int i = 0; i < frame.StackCount; i++)
            {
                context.Stack.Push(frame.Stack[i]);
            }

            if (frame.Pending != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(frame.Pending).Throw();
            }

            return true;
        }

        private VariableEntry[] ResolveEntries(RuntimeContext context)
        {
            Scope scope = context.ScopeHandler.CurrentScope;
            var entries = new VariableEntry[_names.Length];

            for (int i = 0; i < entries.Length; i++)
            {
                if (!scope.TryGetEntry(_names[i], out entries[i]))
                {
                    return null;
                }
            }

            return entries;
        }

        private static bool IsUnboxable(Type type)
        {
            return type == typeof(int) || type == typeof(long) || type == typeof(bool);
        }

        /// <summary>
        /// A variable can live unboxed while the loop runs when it is declared int, int64 or boolean, holds
        /// exactly that type, and is not a string EDT. Anything else stays an object.
        /// </summary>
        private static bool IsUnboxed(VariableEntry entry, Type type, RuntimeContext context)
        {
            var casting = context.Proxy.Casting;

            return entry.DeclarationType == type &&
                entry.Value != null && entry.Value.GetType() == type &&
                !(casting != null && entry.IsStringEdt(casting));
        }

        private Type[] ObserveVariableTypes(RuntimeContext context)
        {
            VariableEntry[] entries = ResolveEntries(context);
            var types = new Type[_names.Length];
            bool any = false;

            for (int i = 0; i < types.Length; i++)
            {
                Type type = entries?[i]?.DeclarationType;
                types[i] = type != null && IsUnboxable(type) && IsUnboxed(entries[i], type, context) ? type : typeof(object);
                any |= types[i] != typeof(object);
            }

            return any ? types : null;
        }

        private static bool MatchesTypes(VariableEntry[] entries, Type[] types, RuntimeContext context)
        {
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] != typeof(object) && !IsUnboxed(entries[i], types[i], context))
                {
                    return false;
                }
            }

            return true;
        }

        private void Compile(Type[] observed)
        {
            try
            {
                if (observed != null)
                {
                    Func<JitFrame, int> typed = Build(observed, false);

                    if (typed != null)
                    {
                        _typedVariables = observed;
                        Volatile.Write(ref _typed, typed);
                    }
                }

                Volatile.Write(ref _generic, Build(Enumerable.Repeat(typeof(object), _names.Length).ToArray(), true));
                Volatile.Write(ref _state, StateReady);
                LoopJit.NoteCompiled();
            }
            catch
            {
                // A loop that cannot be compiled simply stays interpreted
                Volatile.Write(ref _state, StateFailed);
            }
        }

        /// <summary>
        /// Type of an arithmetic result when both operand types are known statically, or object when the
        /// operation goes through the helper (and so through the same code the interpreter runs)
        /// </summary>
        private static Type ResultType(TType op, Type left, Type right)
        {
            bool comparison = op == TType.Equal || op == TType.NotEqual || op == TType.Greater ||
                op == TType.GreaterOrEqual || op == TType.Smaller || op == TType.SmallerOrEqual;
            bool integral = op == TType.Plus || op == TType.PlusAssignment || op == TType.Increment ||
                op == TType.Minus || op == TType.MinusAssignment || op == TType.Decrement || op == TType.Star ||
                op == TType.BinaryAnd || op == TType.BinaryOr || op == TType.BinaryXOr;
            bool leftInteger = left == typeof(int) || left == typeof(long);
            bool rightInteger = right == typeof(int) || right == typeof(long);

            // Every comparison, inline or through the proxy, yields a boolean
            if (comparison)
            {
                return typeof(bool);
            }

            if (left == right && (left == typeof(int) || left == typeof(long)) && integral)
            {
                return left;
            }

            return typeof(object);
        }

        private static bool IsInlineComparison(TType op, Type left, Type right)
        {
            bool integers = (left == typeof(int) || left == typeof(long)) && (right == typeof(int) || right == typeof(long));
            bool booleans = left == typeof(bool) && right == typeof(bool) && (op == TType.Equal || op == TType.NotEqual);
            return integers || booleans;
        }

        private static Type ConstantType(object value)
        {
            return value != null && IsUnboxable(value.GetType()) ? value.GetType() : typeof(object);
        }

        /// <summary>
        /// Generates the loop. With <paramref name="allObject"/> every value is an object handled by the
        /// helpers; otherwise values whose type is known (unboxed variables, constants, and the results of
        /// int/int64/boolean operations on them) live in typed locals. Unboxed variables are written back
        /// to their entries whenever control leaves compiled code. Returns null when the stack types do not
        /// agree where paths meet.
        /// </summary>
        private Func<JitFrame, int> Build(Type[] variableTypes, bool allObject)
        {
            int length = _code.Length;
            var stackTypes = new Type[length][];
            var work = new Stack<int>();
            bool consistent = true;

            void Arrive(int target, Type[] types)
            {
                if (target < _start || target >= End)
                {
                    return;
                }

                int t = target - _start;
                Type[] arriving = _depth[t] == 0 ? Type.EmptyTypes : types;

                if (stackTypes[t] is null)
                {
                    stackTypes[t] = arriving;
                    work.Push(t);
                }
                else if (!stackTypes[t].SequenceEqual(arriving))
                {
                    consistent = false;
                }
            }

            stackTypes[0] = Type.EmptyTypes;
            work.Push(0);

            while (work.Count > 0 && consistent)
            {
                int k = work.Pop();

                foreach (var successor in Successors(k, stackTypes[k], variableTypes, allObject))
                {
                    Arrive(successor.Target, successor.Types);
                }
            }

            if (!consistent)
            {
                return null;
            }

            ParameterExpression frame = Expression.Parameter(typeof(JitFrame), "frame");
            ParameterExpression entryArray = Expression.Variable(typeof(VariableEntry[]), "entries");
            ParameterExpression[] entries = _names.Select((name, i) => Expression.Variable(typeof(VariableEntry), "e" + i)).ToArray();
            ParameterExpression[] locals = variableTypes.Select((type, i) => type == typeof(object) ? null : Expression.Variable(type, "v" + i)).ToArray();
            ParameterExpression result = Expression.Variable(typeof(object), "result");
            ParameterExpression truth = Expression.Variable(typeof(int), "truth");
            ParameterExpression wide = Expression.Variable(typeof(long), "wide");
            var slotLocals = new Dictionary<(int, Type), ParameterExpression>();
            LabelTarget exit = Expression.Label(typeof(int), "exit");
            var labels = new LabelTarget[length];

            ParameterExpression Slot(int index, Type type)
            {
                if (!slotLocals.TryGetValue((index, type), out ParameterExpression slot))
                {
                    slot = Expression.Variable(type, "s" + index + "_" + type.Name);
                    slotLocals.Add((index, type), slot);
                }

                return slot;
            }

            Expression Boxed(Expression value)
            {
                return value.Type == typeof(object) ? value : Expression.Convert(value, typeof(object));
            }

            Expression WriteBack()
            {
                var steps = new List<Expression>();

                for (int i = 0; i < locals.Length; i++)
                {
                    if (locals[i] != null)
                    {
                        steps.Add(Expression.Call(entries[i], typeof(VariableEntry).GetMethod(nameof(VariableEntry.SetValue)), Boxed(locals[i])));
                    }
                }

                return steps.Count == 0 ? (Expression)Expression.Empty() : Expression.Block(steps);
            }

            Expression Leave(int target, Type[] types, int s, bool writeBack = true)
            {
                var steps = new List<Expression>();
                MemberExpression stack = Expression.Field(frame, nameof(JitFrame.Stack));

                if (writeBack)
                {
                    steps.Add(WriteBack());
                }

                for (int i = 0; i < types.Length; i++)
                {
                    steps.Add(Expression.Assign(Expression.ArrayAccess(stack, Expression.Constant(i)), Boxed(Slot(i, types[i]))));
                }

                steps.Add(Expression.Assign(Expression.Field(frame, nameof(JitFrame.StackCount)), Expression.Constant(types.Length)));
                steps.Add(Expression.Assign(Expression.Field(frame, nameof(JitFrame.ScopeDepth)), Expression.Constant(s)));
                steps.Add(Expression.Return(exit, Expression.Constant(target)));
                return Expression.Block(steps);
            }

            Expression Transfer(int from, int target, Type[] types, int s)
            {
                if (target < _start || target >= End)
                {
                    return Leave(target, types, s);
                }

                GotoExpression jump = Expression.Goto(labels[target - _start]);

                if (target > from)
                {
                    return jump;
                }

                // Back edges count the loop's instructions against the execution limits
                return Expression.Block(
                    Expression.IfThen(
                        Expression.Not(Expression.Call(Method(nameof(LoopJit.Checkpoint)), frame, Expression.Constant(length))),
                        Leave(target, types, s)),
                    jump);
            }

            Expression Unbox(Expression value, Type type)
            {
                return Expression.Convert(value, type);
            }

            for (int k = 0; k < length; k++)
            {
                if (stackTypes[k] != null)
                {
                    labels[k] = Expression.Label("L" + (_start + k));
                }
            }

            var body = new List<Expression>
            {
                Expression.Assign(entryArray, Expression.Field(frame, nameof(JitFrame.Entries)))
            };

            for (int i = 0; i < entries.Length; i++)
            {
                body.Add(Expression.Assign(entries[i], Expression.ArrayIndex(entryArray, Expression.Constant(i))));

                if (locals[i] != null)
                {
                    body.Add(Expression.Assign(locals[i], Unbox(Expression.Property(entries[i], nameof(VariableEntry.Value)), locals[i].Type)));
                }
            }

            for (int k = 0; k < length; k++)
            {
                Type[] input = stackTypes[k];

                if (input is null)
                {
                    continue; // unreachable from the loop head
                }

                int pc = _start + k;
                int d = input.Length;
                int s = _scopes[k];
                IInstruction instruction = _code[k];
                Type type = instruction.GetType();
                var successors = Successors(k, input, variableTypes, allObject);

                body.Add(Expression.Label(labels[k]));

                if (type == typeof(Push))
                {
                    object value = ((Push)instruction).Value;
                    Type valueType = allObject ? typeof(object) : ConstantType(value);
                    body.Add(Expression.Assign(Slot(d, valueType), Expression.Constant(value, valueType)));
                }
                else if (type == typeof(VariableLoad))
                {
                    int v = _variable[k];
                    body.Add(locals[v] != null
                        ? Expression.Assign(Slot(d, locals[v].Type), locals[v])
                        : Expression.Assign(Slot(d, typeof(object)), Expression.Property(entries[v], nameof(VariableEntry.Value))));
                }
                else if (type == typeof(Store))
                {
                    int v = _variable[k];
                    ParameterExpression value = Slot(d - 1, input[d - 1]);

                    if (locals[v] != null && value.Type == locals[v].Type)
                    {
                        // Same declared type, not a string EDT: Scope.SetVar would store it unchanged
                        body.Add(Expression.Assign(locals[v], value));
                    }
                    else if (locals[v] is null)
                    {
                        body.Add(Expression.IfThen(
                            Expression.Not(Expression.Call(Method(nameof(LoopJit.Assign)), frame, Expression.Constant(instruction, typeof(Store)), entries[v], Boxed(value))),
                            Leave(pc, input, s)));
                    }
                    else
                    {
                        Expression assign = Expression.Call(Method(nameof(LoopJit.Assign)), frame, Expression.Constant(instruction, typeof(Store)), entries[v], Boxed(value));

                        // The interpreter's assignment reads the entries, so they must be current first
                        body.Add(WriteBack());
                        body.Add(Expression.IfThen(Expression.Not(assign), Leave(pc, input, s, writeBack: false)));

                        {
                            // The interpreter's assignment converted the value; keep running only if it kept the type
                            MemberExpression stored = Expression.Property(entries[v], nameof(VariableEntry.Value));

                            body.Add(Expression.IfThenElse(
                                Expression.TypeEqual(stored, locals[v].Type),
                                Expression.Assign(locals[v], Unbox(stored, locals[v].Type)),
                                Leave(pc + 1, successors[0].Types, s, writeBack: false)));
                        }
                    }
                }
                else if (type == typeof(Arithmetic))
                {
                    var operation = (Arithmetic)instruction;
                    ParameterExpression left = Slot(d - 2, input[d - 2]);
                    ParameterExpression right = Slot(d - 1, input[d - 1]);
                    Type resultType = successors[0].Types[d - 2];
                    ParameterExpression target = Slot(d - 2, resultType);
                    Expression deopt = Leave(pc, input, s);

                    if (resultType == typeof(bool) && IsInlineComparison(operation.TokenType, left.Type, right.Type))
                    {
                        body.Add(Expression.Assign(target, Compare(operation.TokenType, left, right)));
                    }
                    else if (resultType == typeof(object) || resultType == typeof(bool))
                    {
                        body.Add(Expression.Assign(result, Expression.Call(Method(nameof(LoopJit.Arith)), frame, Expression.Constant(operation, typeof(Arithmetic)), Boxed(left), Boxed(right))));

                        Expression valid = Expression.ReferenceNotEqual(result, Expression.Constant(LoopJit.Deopt));

                        if (resultType == typeof(bool))
                        {
                            valid = Expression.AndAlso(valid, Expression.TypeEqual(result, typeof(bool)));
                        }

                        body.Add(Expression.IfThenElse(valid, Expression.Assign(target, Unbox(result, resultType)), deopt));
                    }
                    else
                    {
                        body.Add(IntegerOperation(operation.TokenType, left, right, target, wide, deopt));
                    }
                }
                else if (type == typeof(Jump))
                {
                    body.Add(Transfer(pc, successors[0].Target, successors[0].Types, s));
                    continue;
                }
                else if (type == typeof(JumpIfFalse) || type == typeof(JumpIfTrue))
                {
                    bool whenTrue = type == typeof(JumpIfTrue);
                    bool returnToStack = whenTrue ? ((JumpIfTrue)instruction).ReturnToStack : ((JumpIfFalse)instruction).ReturnToStack;
                    ParameterExpression condition = Slot(d - 1, input[d - 1]);
                    var taken = successors[1];
                    Expression jump = Transfer(pc, taken.Target, taken.Types, s);

                    if (returnToStack)
                    {
                        Type pushed = taken.Types[d - 1];
                        jump = Expression.Block(Expression.Assign(Slot(d - 1, pushed), Expression.Constant(whenTrue, pushed)), jump);
                    }

                    if (condition.Type == typeof(bool))
                    {
                        body.Add(Expression.IfThen(whenTrue ? (Expression)condition : Expression.Not(condition), jump));
                    }
                    else
                    {
                        body.Add(Expression.Assign(truth, Expression.Call(Method(nameof(LoopJit.Truth)), frame, Boxed(condition))));
                        body.Add(Expression.IfThen(Expression.Equal(truth, Expression.Constant(-1)), Leave(pc, input, s)));
                        body.Add(Expression.IfThen(Expression.Equal(truth, Expression.Constant(whenTrue ? 1 : 0)), jump));
                    }
                }

                // BeginScope and EndScope generate no code: their scopes declare nothing

                if (pc + 1 == End)
                {
                    body.Add(Leave(End, successors[0].Types, successors[0].Scopes));
                }
            }

            body.Add(Expression.Label(exit, Expression.Constant(End)));

            var variables = new List<ParameterExpression> { entryArray, result, truth, wide };
            variables.AddRange(entries);
            variables.AddRange(locals.Where(local => local != null));
            variables.AddRange(slotLocals.Values);

            return Expression.Lambda<Func<JitFrame, int>>(Expression.Block(variables, body), frame).Compile();
        }

        /// <summary>
        /// Successor program counters with the stack types they receive, fall-through first
        /// </summary>
        private List<(int Target, Type[] Types, int Scopes)> Successors(int k, Type[] input, Type[] variableTypes, bool allObject)
        {
            IInstruction instruction = _code[k];
            Type type = instruction.GetType();
            int pc = _start + k;
            int d = input.Length;
            int s = _scopes[k];
            var successors = new List<(int, Type[], int)>(2);

            Type[] With(int keep, params Type[] pushed)
            {
                var types = new Type[keep + pushed.Length];
                Array.Copy(input, types, keep);
                Array.Copy(pushed, 0, types, keep, pushed.Length);
                return types;
            }

            if (type == typeof(Push))
            {
                successors.Add((pc + 1, With(d, allObject ? typeof(object) : ConstantType(((Push)instruction).Value)), s));
            }
            else if (type == typeof(VariableLoad))
            {
                successors.Add((pc + 1, With(d, variableTypes[_variable[k]]), s));
            }
            else if (type == typeof(Store))
            {
                successors.Add((pc + 1, With(d - 1), s));
            }
            else if (type == typeof(Arithmetic))
            {
                Type resultType = allObject ? typeof(object) : ResultType(((Arithmetic)instruction).TokenType, input[d - 2], input[d - 1]);
                successors.Add((pc + 1, With(d - 2, resultType), s));
            }
            else if (type == typeof(BeginScope))
            {
                successors.Add((pc + 1, Type.EmptyTypes, s + 1));
            }
            else if (type == typeof(EndScope))
            {
                successors.Add((pc + 1, Type.EmptyTypes, s - 1));
            }
            else if (type == typeof(Jump))
            {
                successors.Add((LoopJit.JumpTarget(pc, ((Jump)instruction).Offset), input, s));
            }
            else
            {
                bool returnToStack = instruction is JumpIfFalse jif ? jif.ReturnToStack : ((JumpIfTrue)instruction).ReturnToStack;
                successors.Add((pc + 1, With(d - 1), s));
                successors.Add((LoopJit.JumpTarget(pc, ((Jump)instruction).Offset), returnToStack ? With(d - 1, allObject ? typeof(object) : typeof(bool)) : With(d - 1), s));
            }

            return successors;
        }

        private static Expression Compare(TType op, Expression left, Expression right)
        {
            if (left.Type != right.Type)
            {
                left = Expression.Convert(left, typeof(long));
                right = Expression.Convert(right, typeof(long));
            }

            switch (op)
            {
                case TType.Equal: return Expression.Equal(left, right);
                case TType.NotEqual: return Expression.NotEqual(left, right);
                case TType.Greater: return Expression.GreaterThan(left, right);
                case TType.GreaterOrEqual: return Expression.GreaterThanOrEqual(left, right);
                case TType.Smaller: return Expression.LessThan(left, right);
                default: return Expression.LessThanOrEqual(left, right);
            }
        }

        /// <summary>
        /// int and int64 arithmetic matching Arithmetic.TryInt64: bitwise operations always complete, while
        /// +, - and * hand back to the interpreter (which defers to the native runtime) on overflow.
        /// </summary>
        private static Expression IntegerOperation(TType op, ParameterExpression left, ParameterExpression right, ParameterExpression target, ParameterExpression wide, Expression deopt)
        {
            switch (op)
            {
                case TType.BinaryAnd: return Expression.Assign(target, Expression.And(left, right));
                case TType.BinaryOr: return Expression.Assign(target, Expression.Or(left, right));
                case TType.BinaryXOr: return Expression.Assign(target, Expression.ExclusiveOr(left, right));
            }

            bool add = op == TType.Plus || op == TType.PlusAssignment || op == TType.Increment;
            bool subtract = op == TType.Minus || op == TType.MinusAssignment || op == TType.Decrement;

            if (left.Type == typeof(int))
            {
                Expression l = Expression.Convert(left, typeof(long));
                Expression r = Expression.Convert(right, typeof(long));
                Expression value = add ? Expression.Add(l, r) : subtract ? Expression.Subtract(l, r) : (Expression)Expression.Multiply(l, r);

                return Expression.Block(
                    Expression.Assign(wide, value),
                    Expression.IfThen(
                        Expression.OrElse(
                            Expression.LessThan(wide, Expression.Constant((long)int.MinValue)),
                            Expression.GreaterThan(wide, Expression.Constant((long)int.MaxValue))),
                        deopt),
                    Expression.Assign(target, Expression.Convert(wide, typeof(int))));
            }

            Expression zero = Expression.Constant(0L);

            if (add || subtract)
            {
                Expression value = add ? Expression.Add(left, right) : (Expression)Expression.Subtract(left, right);
                Expression overflow = add
                    ? Expression.And(Expression.ExclusiveOr(left, wide), Expression.ExclusiveOr(right, wide))
                    : Expression.And(Expression.ExclusiveOr(left, right), Expression.ExclusiveOr(left, wide));

                return Expression.Block(
                    Expression.Assign(wide, value),
                    Expression.IfThen(Expression.LessThan(overflow, zero), deopt),
                    Expression.Assign(target, wide));
            }

            // Only products whose operands are within sqrt(long.MaxValue) are taken, as in Arithmetic.FitsInt64Product
            Expression limit = Expression.Constant(3037000499L);
            Expression negativeLimit = Expression.Constant(-3037000499L);

            return Expression.Block(
                Expression.IfThen(
                    Expression.OrElse(
                        Expression.OrElse(Expression.LessThan(left, negativeLimit), Expression.GreaterThan(left, limit)),
                        Expression.OrElse(Expression.LessThan(right, negativeLimit), Expression.GreaterThan(right, limit))),
                    deopt),
                Expression.Assign(target, Expression.Multiply(left, right)));
        }

        private static System.Reflection.MethodInfo Method(string name)
        {
            return typeof(LoopJit).GetMethod(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        }
    }
}

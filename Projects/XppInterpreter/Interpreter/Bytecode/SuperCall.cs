using System.Collections.Generic;

namespace XppInterpreter.Interpreter.Bytecode
{
    /// <summary>
    /// super(...) inside a script class method: calls the nearest override of the
    /// current method above its declaring class, on the current instance
    /// </summary>
    class SuperCall : IInstruction, IInterpretableInstruction
    {
        public string OperationCode => $"SUPER {MethodName} {NArgs}";

        public RuntimeClass DeclaringClass { get; }
        public string MethodName { get; }
        public int NArgs { get; }
        public bool Alloc { get; }
        public InterpreterResult LastResult { get; private set; }

        public SuperCall(RuntimeClass declaringClass, string methodName, int nArgs, bool alloc)
        {
            DeclaringClass = declaringClass;
            MethodName = methodName;
            NArgs = nArgs;
            Alloc = alloc;
        }

        public void Execute(RuntimeContext context)
        {
            // A stale paused result from a previous, aborted execution must not
            // survive into this one
            LastResult = InterpreterResult.Finished;

            object[] arguments = new object[NArgs];

            // Arguments are only on the stack for a fresh call, not on debugger resume
            if (context.InnerContext is null)
            {
                for (int narg = 0; narg < NArgs; narg++)
                {
                    if (context.Stack.Count > 0)
                    {
                        arguments[narg] = context.Stack.Pop();
                    }
                }
            }

            var instance = (ScriptObject)context.ScopeHandler.GetVar("this");

            var invocation = ScriptClassRuntime.CallSuper(context, DeclaringClass, MethodName, instance, arguments);

            LastResult = invocation.Result;

            if ((invocation.Result?.HasFinished ?? true) && Alloc)
            {
                context.Stack.Push(invocation.ReturnValue);
            }
        }
    }

    /// <summary>
    /// Executes the next Chain of Command wrapper, or the original target method
    /// when the current wrapper is innermost.
    /// </summary>
    class NextMethodCall : IInstruction, IInterpretableInstruction
    {
        public string OperationCode => $"NEXT_METHOD {CurrentMethod.Name} {NArgs}";
        public RuntimeClassMethod CurrentMethod { get; }
        public int NArgs { get; }
        public bool Alloc { get; }
        public InterpreterResult LastResult { get; private set; }

        public NextMethodCall(RuntimeClassMethod currentMethod, int nArgs, bool alloc)
        {
            CurrentMethod = currentMethod;
            NArgs = nArgs;
            Alloc = alloc;
        }

        public void Execute(RuntimeContext context)
        {
            LastResult = InterpreterResult.Finished;

            if (CurrentMethod?.NextMethod is null && CurrentMethod?.NextCallsNative != true)
            {
                throw new System.Exception($"No next implementation exists for Chain of Command method {CurrentMethod?.Name}.");
            }

            object[] arguments = new object[NArgs];

            if (context.InnerContext is null)
            {
                for (int narg = 0; narg < NArgs; narg++)
                {
                    arguments[narg] = context.Stack.Pop();
                }
            }

            ScriptObject instance = null;

            if (!CurrentMethod.IsStatic)
            {
                instance = (ScriptObject)context.ScopeHandler.GetVar("this");
            }

            ScriptClassRuntime.Invocation invocation;

            if (CurrentMethod.NextMethod != null)
            {
                invocation = ScriptClassRuntime.CallResumable(
                    context,
                    CurrentMethod.NextMethod,
                    instance,
                    arguments);
            }
            else
            {
                AosCallResult nativeCall = CurrentMethod.IsStatic
                    ? AosCallDispatcher.CallStatic(context, CurrentMethod.NativeTargetName, CurrentMethod.Name, arguments)
                    : AosCallDispatcher.CallInstance(
                        context,
                        ScriptClassRuntime.EnsureBaseInstance(context, instance),
                        CurrentMethod.Name,
                        arguments);
                object returnValue = nativeCall.ReturnValue;

                invocation = new ScriptClassRuntime.Invocation { ReturnValue = returnValue };
            }

            LastResult = invocation.Result;

            if ((invocation.Result?.HasFinished ?? true) && Alloc)
            {
                context.Stack.Push(invocation.ReturnValue);
            }
        }
    }
}

using System.Collections.Generic;

namespace XppInterpreter.Interpreter.Bytecode
{
    class StaticFunctionCall : Call, IInterpretableInstruction
    {
        public string ClassName { get; }

        /// <summary>
        /// Set when the caller resolved at parse time to a .NET type the X++
        /// metadata proxies do not know
        /// </summary>
        public System.Type ClrType { get; }

        /// <summary>
        /// Positions and variable names of byref arguments (written back after CLR calls)
        /// </summary>
        public List<KeyValuePair<int, string>> ByRefArguments { get; }

        public InterpreterResult LastResult { get; private set; }

        public StaticFunctionCall(string name, int nArgs, bool alloc, string className = null, System.Type clrType = null, List<KeyValuePair<int, string>> byRefArguments = null) : base(name, nArgs, alloc)
        {
            ClassName = className;
            ClrType = clrType;
            ByRefArguments = byRefArguments;
        }

        public override void Execute(RuntimeContext context)
        {
            // A stale paused result from a previous, aborted execution must not
            // survive into this one
            LastResult = InterpreterResult.Finished;

            // On debugger resume the arguments were already consumed; the inner
            // context carries the whole call state
            if (context.InnerContext != null)
            {
                var resumed = ScriptClassRuntime.CallResumable(context, null, null, null);
                LastResult = resumed.Result;

                if ((resumed.Result?.HasFinished ?? true) && Alloc)
                {
                    context.Stack.Push(resumed.ReturnValue);
                }

                return;
            }

            var arguments = GetParametersFromStack(context.Stack);

            var scriptClass = string.IsNullOrEmpty(ClassName) ? null : context.ByteCode.FindDeclaredClass(ClassName);

            if (scriptClass != null)
            {
                var invocation = ScriptClassRuntime.CallStatic(context, scriptClass, Name, arguments);
                LastResult = invocation.Result;

                if ((invocation.Result?.HasFinished ?? true) && Alloc)
                {
                    context.Stack.Push(invocation.ReturnValue);
                }

                if (invocation.Result?.HasFinished ?? true)
                {
                    WriteBackByRefArguments(context, invocation.UpdatedArguments);
                }

                return;
            }

            System.Type compiledTargetType = ClrType;

            if (compiledTargetType is null && !string.IsNullOrEmpty(ClassName))
            {
                try
                {
                    compiledTargetType = context.Proxy.Casting.GetSystemTypeFromTypeName(ClassName);
                }
                catch
                {
                }
            }

            RuntimeClassMethod compiledExtension = ScriptClassRuntime.FindCompiledTargetExtension(
                context.ByteCode,
                compiledTargetType,
                Name,
                isStatic: true);

            if (compiledExtension != null)
            {
                var invocation = ScriptClassRuntime.CallResumable(context, compiledExtension, null, arguments);
                LastResult = invocation.Result;

                if ((invocation.Result?.HasFinished ?? true) && Alloc)
                {
                    context.Stack.Push(invocation.ReturnValue);
                }

                if (invocation.Result?.HasFinished ?? true)
                {
                    WriteBackByRefArguments(context, invocation.UpdatedArguments);
                }

                return;
            }

            LastResult = InterpreterResult.Finished;

            // Script instances crossing into native code become their base instances
            arguments = ScriptClassRuntime.MarshalToNative(arguments);

            object returnValue;

            if (string.IsNullOrEmpty(ClassName))
            {
                returnValue = context.Proxy.Reflection.CallGlobalOrPredefinedFunction(context, Name, arguments);
            }
            else if (ClrType != null)
            {
                returnValue = ClrBridge.InvokeStatic(ClrType, Name, arguments, out object[] fittedArguments);

                if (ByRefArguments != null && fittedArguments != null)
                {
                    foreach (var byRefArgument in ByRefArguments)
                    {
                        if (byRefArgument.Key < fittedArguments.Length)
                        {
                            context.ScopeHandler.CurrentScope.SetVar(byRefArgument.Value, fittedArguments[byRefArgument.Key], context.Proxy.Casting);
                        }
                    }
                }
            }
            else
            {
                var dispatched = AosCallDispatcher.CallStatic(context, ClassName, Name, arguments);
                returnValue = dispatched.ReturnValue;
                WriteBackByRefArguments(context, dispatched.UpdatedArguments);
            }

            if (Alloc)
            {
                context.Stack.Push(returnValue);
            }
        }

        public override object MakeCall(RuntimeContext context, object[] arguments)
        {
            arguments = arguments ?? new object[0];
            RuntimeClass scriptClass = string.IsNullOrEmpty(ClassName)
                ? null
                : context.ByteCode.FindDeclaredClass(ClassName);

            if (scriptClass != null)
            {
                RuntimeClassMethod method = scriptClass.FindDispatchMethod(Name, isStatic: true);
                return ScriptClassRuntime.RunSynchronous(context, method, null, arguments).ReturnValue;
            }

            if (string.IsNullOrEmpty(ClassName))
            {
                return context.Proxy.Reflection.CallGlobalOrPredefinedFunction(context, Name, arguments);
            }

            if (ClrType != null)
            {
                object value = ClrBridge.InvokeStatic(ClrType, Name, arguments, out object[] fitted);
                if (fitted != null) System.Array.Copy(fitted, arguments, fitted.Length);
                return value;
            }

            AosCallResult native = AosCallDispatcher.CallStatic(context, ClassName, Name, arguments);
            if (native.UpdatedArguments != null)
            {
                System.Array.Copy(native.UpdatedArguments, arguments, native.UpdatedArguments.Length);
            }
            return native.ReturnValue;
        }

        private void WriteBackByRefArguments(RuntimeContext context, object[] updatedArguments)
        {
            if (ByRefArguments is null || updatedArguments is null) return;

            foreach (var byRefArgument in ByRefArguments)
            {
                if (byRefArgument.Key < updatedArguments.Length)
                {
                    context.ScopeHandler.CurrentScope.SetVar(
                        byRefArgument.Value,
                        updatedArguments[byRefArgument.Key],
                        context.Proxy.Casting);
                }
            }
        }
    }
}

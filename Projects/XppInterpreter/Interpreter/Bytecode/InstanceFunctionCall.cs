using System.Collections.Generic;

namespace XppInterpreter.Interpreter.Bytecode
{
    class InstanceFunctionCall : Call, IInterpretableInstruction
    {
        public InterpreterResult LastResult { get; private set; }

        /// <summary>
        /// Positions and variable names of byref arguments (written back after CLR calls)
        /// </summary>
        public List<KeyValuePair<int, string>> ByRefArguments { get; }

        public InstanceFunctionCall(string funcName, int nArgs, bool alloc, List<KeyValuePair<int, string>> byRefArguments = null) : base(funcName, nArgs, alloc, false)
        {
            ByRefArguments = byRefArguments;
        }

        /// <summary>
        /// How a native receiver type dispatches this call. Metadata cannot change while
        /// the bytecode is alive, so it is resolved once per receiver type (inline cache).
        /// </summary>
        private sealed class CallSiteTarget
        {
            public System.Type Type;
            public ByteCode ByteCode;
            public int DeclaredClassCount;
            public RuntimeClassMethod CompiledExtension;
            public bool ClrOnly;
            public bool ViaExtension;
        }

        private CallSiteTarget _callSite;

        private CallSiteTarget ResolveTarget(RuntimeContext context, object instance)
        {
            System.Type type = instance?.GetType();
            int classCount = context.ByteCode?.DeclaredClasses?.Count ?? 0;
            CallSiteTarget cached = _callSite;

            if (cached != null && type != null && cached.Type == type &&
                ReferenceEquals(cached.ByteCode, context.ByteCode) && cached.DeclaredClassCount == classCount)
            {
                return cached;
            }

            var target = new CallSiteTarget { Type = type, ByteCode = context.ByteCode, DeclaredClassCount = classCount };

            target.CompiledExtension = ScriptClassRuntime.FindCompiledTargetExtension(context.ByteCode, type, Name, isStatic: false);

            if (target.CompiledExtension is null)
            {
                target.ClrOnly = ClrBridge.IsClrOnlyInstance(context, instance);

                if (!target.ClrOnly)
                {
                    // X++ extension methods: when the type has no such instance method,
                    // obj.extMethod() resolves to <TypeName>_Extension::extMethod(obj, ...).
                    // The existence check keeps real errors from an existing method intact.
                    bool hasInstanceMethod = true;

                    try
                    {
                        hasInstanceMethod = context.Proxy.Reflection.TypeHasMethod(type, Name);
                    }
                    catch
                    {
                    }

                    if (!hasInstanceMethod)
                    {
                        try
                        {
                            var extensionType = context.Proxy.Casting.GetSystemTypeFromTypeName(type.Name + "_Extension");
                            target.ViaExtension = extensionType != null && context.Proxy.Reflection.TypeHasMethod(extensionType, Name);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            if (type != null)
            {
                _callSite = target;
            }

            return target;
        }

        public override void Execute(RuntimeContext context)
        {
            // A stale paused result from a previous, aborted execution must not
            // survive into this one
            LastResult = InterpreterResult.Finished;

            // On debugger resume the instance and arguments were already consumed;
            // the inner context carries the whole call state
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

            var instance = context.Stack.Pop();
            var arguments = GetParametersFromStack(context.Stack);

            if (instance is ScriptObject scriptObject)
            {
                if (context.Models.TryInvokeRuntimeRecordMethod(scriptObject, Name, arguments, out object runtimeResult))
                {
                    if (Alloc) context.Stack.Push(runtimeResult);
                    return;
                }

                var invocation = ScriptClassRuntime.CallInstance(context, scriptObject, Name, arguments);
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

            CallSiteTarget target = ResolveTarget(context, instance);
            RuntimeClassMethod compiledExtension = target.CompiledExtension;

            if (compiledExtension != null)
            {
                ScriptObject facade = ScriptClassRuntime.GetCompiledTargetFacade(context, compiledExtension, instance);
                var invocation = ScriptClassRuntime.CallResumable(context, compiledExtension, facade, arguments);
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

            if (target.ClrOnly)
            {
                returnValue = ClrBridge.InvokeInstance(instance, Name, arguments, out object[] fittedArguments);
                WriteBackByRefArguments(context, fittedArguments);
            }
            else
            {
                if (target.ViaExtension)
                {
                    object[] extended = new object[arguments.Length + 1];
                    extended[0] = instance;
                    System.Array.Copy(arguments, 0, extended, 1, arguments.Length);

                    var dispatched = AosCallDispatcher.CallStatic(
                        context,
                        instance.GetType().Name + "_Extension",
                        Name,
                        extended);
                    returnValue = dispatched.ReturnValue;

                    if (dispatched.UpdatedArguments != null && dispatched.UpdatedArguments.Length > 1)
                    {
                        var updated = new object[dispatched.UpdatedArguments.Length - 1];
                        System.Array.Copy(dispatched.UpdatedArguments, 1, updated, 0, updated.Length);
                        WriteBackByRefArguments(context, updated);
                    }
                }
                else
                {
                    var dispatched = AosCallDispatcher.CallInstance(context, instance, Name, arguments);
                    returnValue = dispatched.ReturnValue;
                    WriteBackByRefArguments(context, dispatched.UpdatedArguments);
                }
            }

            if (Alloc)
            {
                context.Stack.Push(returnValue);
            }
        }

        private void WriteBackByRefArguments(RuntimeContext context, object[] fittedArguments)
        {
            if (ByRefArguments is null || fittedArguments is null)
            {
                return;
            }

            foreach (var byRefArgument in ByRefArguments)
            {
                if (byRefArgument.Key < fittedArguments.Length)
                {
                    context.ScopeHandler.CurrentScope.SetVar(byRefArgument.Value, fittedArguments[byRefArgument.Key], context.Proxy.Casting);
                }
            }
        }

        public override object MakeCall(RuntimeContext context, object[] arguments)
        {
            if (arguments is null || arguments.Length == 0)
            {
                throw new System.ArgumentException("An instance call requires the target object as its first argument.", nameof(arguments));
            }

            object instance = arguments[0];
            var methodArguments = new object[arguments.Length - 1];
            System.Array.Copy(arguments, 1, methodArguments, 0, methodArguments.Length);

            if (instance is ScriptObject scriptObject)
            {
                if (context.Models.TryInvokeRuntimeRecordMethod(scriptObject, Name, methodArguments, out object runtimeResult))
                {
                    return runtimeResult;
                }

                return ScriptClassRuntime.RunSynchronous(
                    context,
                    scriptObject.Class.FindDispatchMethod(Name, isStatic: false),
                    scriptObject,
                    methodArguments).ReturnValue;
            }

            RuntimeClassMethod extension = ScriptClassRuntime.FindCompiledTargetExtension(
                context.ByteCode, instance?.GetType(), Name, isStatic: false);
            if (extension != null)
            {
                ScriptObject facade = ScriptClassRuntime.GetCompiledTargetFacade(context, extension, instance);
                return ScriptClassRuntime.RunSynchronous(context, extension, facade, methodArguments).ReturnValue;
            }

            if (ClrBridge.IsClrOnlyInstance(context, instance))
            {
                object value = ClrBridge.InvokeInstance(instance, Name, methodArguments, out object[] fitted);
                if (fitted != null) System.Array.Copy(fitted, 0, arguments, 1, fitted.Length);
                return value;
            }

            AosCallResult native = AosCallDispatcher.CallInstance(context, instance, Name, methodArguments);
            if (native.UpdatedArguments != null)
            {
                System.Array.Copy(native.UpdatedArguments, 0, arguments, 1, native.UpdatedArguments.Length);
            }
            return native.ReturnValue;
        }
    }
}

namespace XppInterpreter.Interpreter.Bytecode
{
    class NewObject : Call, IInterpretableInstruction
    {
        public override string OperationCode => $"NEW_OBJ {Name} {NArgs}";

        /// <summary>
        /// Set when the class name resolved at parse time to a .NET type the X++
        /// metadata proxies do not know
        /// </summary>
        public System.Type ClrType { get; }

        public InterpreterResult LastResult { get; private set; }

        public NewObject(string className, int nArgs, bool alloc, System.Type clrType = null) : base(className, nArgs, alloc)
        {
            ClrType = clrType;
        }

        public override void Execute(RuntimeContext context)
        {
            // A stale paused result from a previous, aborted execution must not
            // survive into this one
            LastResult = InterpreterResult.Finished;

            var scriptClass = context.ByteCode.FindDeclaredClass(Name);

            if (scriptClass is null)
            {
                base.Execute(context);
                return;
            }

            ScriptObject instance;
            ScriptClassRuntime.Invocation invocation;

            if (context.InnerContext is null)
            {
                var arguments = GetParametersFromStack(context.Stack);

                instance = ScriptClassRuntime.BeginInstantiate(context, scriptClass, arguments, out RuntimeClassMethod constructor);

                if (constructor is null)
                {
                    if (Alloc)
                    {
                        context.Stack.Push(instance);
                    }

                    return;
                }

                invocation = ScriptClassRuntime.CallResumable(context, constructor, instance, arguments);
            }
            else
            {
                // Resuming a paused constructor: the instance is the constructor
                // context's own 'this'
                instance = (ScriptObject)context.InnerContext.ScopeHandler.GetVar("this");
                invocation = ScriptClassRuntime.CallResumable(context, scriptClass.FindMethod("new"), instance, null);
            }

            LastResult = invocation.Result ?? InterpreterResult.Finished;

            if (LastResult.HasFinished && Alloc)
            {
                context.Stack.Push(instance);
            }
        }

        public override object MakeCall(RuntimeContext context, object[] arguments)
        {
            // Non-script classes only; script instantiation is handled in Execute

            // Script instances crossing into native code become their base instances
            arguments = ScriptClassRuntime.MarshalToNative(arguments);

            if (ClrType != null)
            {
                return ClrBridge.CreateInstance(ClrType, arguments);
            }

            return context.Proxy.Reflection.CreateInstance(Name, arguments);
        }
    }
}

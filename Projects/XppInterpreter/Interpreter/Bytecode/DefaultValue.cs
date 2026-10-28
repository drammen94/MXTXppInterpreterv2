namespace XppInterpreter.Interpreter.Bytecode
{
    class DefaultValue : IInstruction
    {
        public virtual string OperationCode => "PUSH_DEFAULT";
        public string TypeName { get; }
        public System.Type ClrType { get; }

        public DefaultValue(string typeName, System.Type clrType = null)
        {
            TypeName = typeName;
            ClrType = clrType;
        }

        public virtual void Execute(RuntimeContext context)
        {
            // Runtime table/entity/view declarations behave like X++ table
            // buffers: declaring the variable allocates an empty typed buffer.
            if (context.ByteCode.FindDeclaredClass(TypeName) != null)
            {
                context.Stack.Push(context.Models.IsRuntimeTable(TypeName)
                    ? context.Models.Create(TypeName)
                    : null);
                return;
            }

            if (ClrType != null)
            {
                System.Type proxyType = null;

                try
                {
                    proxyType = context.Proxy.Casting.GetSystemTypeFromTypeName(TypeName);
                }
                catch
                {
                    // Names unknown to X++ metadata (script interfaces, dotted CLR names)
                }

                if (proxyType is null)
                {
                    context.Stack.Push(ClrType.IsValueType && !(ClrType is Core.ScriptClassType)
                        ? System.Activator.CreateInstance(ClrType)
                        : null);
                    return;
                }
            }

            context.Stack.Push(context.Proxy.Casting.GetDefaultValueForType(TypeName));
        }
    }
}

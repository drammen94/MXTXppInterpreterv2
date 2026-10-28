namespace XppInterpreter.Interpreter.Bytecode
{
    public class As : IInstruction
    {
        public string OperationCode => $"AS {TypeName}";
        public string TypeName { get; }
        public System.Type ClrType { get; }

        public As(string typeName, System.Type clrType = null)
        {
            TypeName = typeName;
            ClrType = clrType;
        }

        public void Execute(RuntimeContext context)
        {
            var value = context.Stack.Pop();

            if (value is ScriptObject scriptObject)
            {
                bool matches = scriptObject.Class.IsOrExtends(TypeName);

                // Deeper compiled ancestors are checked through the real base instance
                if (!matches && scriptObject.BaseInstance != null)
                {
                    try
                    {
                        matches = context.Proxy.Casting.Is(scriptObject.BaseInstance, TypeName);
                    }
                    catch
                    {
                    }
                }

                context.Stack.Push(matches ? value : null);
                return;
            }

            if (ClrType != null)
            {
                context.Stack.Push(value != null && ClrType.IsInstanceOfType(value) ? value : null);
                return;
            }

            // CLR-only instances can never be an X++ metadata type
            if (value != null && ClrBridge.IsClrOnlyInstance(context, value))
            {
                context.Stack.Push(null);
                return;
            }

            context.Stack.Push(context.Proxy.Casting.As(value, TypeName));
        }
    }
}

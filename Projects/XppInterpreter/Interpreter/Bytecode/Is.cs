namespace XppInterpreter.Interpreter.Bytecode
{
    public class Is : IInstruction
    {
        public string OperationCode => $"IS {TypeName}";
        public string TypeName { get; }
        public System.Type ClrType { get; }

        public Is(string typeName, System.Type clrType = null)
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

                context.Stack.Push(matches);
                return;
            }

            if (ClrType != null)
            {
                context.Stack.Push(value != null && ClrType.IsInstanceOfType(value));
                return;
            }

            // CLR-only instances can never be an X++ metadata type
            if (value != null && ClrBridge.IsClrOnlyInstance(context, value))
            {
                context.Stack.Push(false);
                return;
            }

            context.Stack.Push(context.Proxy.Casting.Is(value, TypeName));
        }
    }
}

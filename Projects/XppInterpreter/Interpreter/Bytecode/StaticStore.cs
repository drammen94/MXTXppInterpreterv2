namespace XppInterpreter.Interpreter.Bytecode
{
    /// <summary>
    /// Assignment to a static member (ClassName::member = value)
    /// </summary>
    class StaticStore : IInstruction
    {
        public string OperationCode => $"STORE_STATIC {ClassName}::{Name}";

        public string Name { get; }
        public string ClassName { get; }
        public System.Type ClrType { get; }

        public StaticStore(string name, string className, System.Type clrType)
        {
            Name = name;
            ClassName = className;
            ClrType = clrType;
        }

        public void Execute(RuntimeContext context)
        {
            var value = context.Stack.Pop();

            var scriptClass = context.ByteCode.FindDeclaredClass(ClassName);

            if (scriptClass != null)
            {
                RuntimeClassField field = scriptClass.FindField(Name);

                if (field is null)
                {
                    field = scriptClass.FindExtensionField(Name, isStatic: true);
                }

                if (field != null)
                {
                    if (!field.IsStatic)
                    {
                        throw new System.Exception($"Instance field {field.DeclaringClass.Name}.{Name} must be accessed through an object.");
                    }

                    ScriptClassRuntime.EnsureFieldAccessible(field, context.ActiveClass, writing: true);
                }

                RuntimeClass storageClass = field?.DeclaringClass?.ExtensionTarget != null
                    ? field.DeclaringClass
                    : scriptClass;

                ScriptClassRuntime.EnsureStaticsInitialized(context, storageClass);
                storageClass.StaticScope.SetVar(Name, value, context.Proxy.Casting);
                return;
            }

            if (ClrType != null)
            {
                ClrBridge.SetMember(ClrType, null, Name, value);
                return;
            }

            context.Proxy.Reflection.SetStaticProperty(ClassName, Name, value);
        }
    }
}

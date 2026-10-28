namespace XppInterpreter.Interpreter.Bytecode
{
    class StaticLoad : Load
    {
        public string CallerName { get; }

        /// <summary>
        /// Set when the caller resolved at parse time to a .NET type the X++
        /// metadata proxies do not know
        /// </summary>
        public System.Type ClrType { get; }

        public StaticLoad(string name, string callerName, bool isArray, System.Type clrType = null) : base(name, isArray)
        {
            CallerName = callerName;
            ClrType = clrType;
        }

        public override object MakeLoad(RuntimeContext context)
        {
            var scriptClass = context.ByteCode.FindDeclaredClass(CallerName);

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

                    ScriptClassRuntime.EnsureFieldAccessible(field, context.ActiveClass, writing: false);
                }

                RuntimeClass storageClass = field?.DeclaringClass?.ExtensionTarget != null
                    ? field.DeclaringClass
                    : scriptClass;

                ScriptClassRuntime.EnsureStaticsInitialized(context, storageClass);
                return storageClass.StaticScope.GetVar(Name);
            }

            if (ClrType != null)
            {
                return ClrBridge.GetMember(ClrType, null, Name);
            }

            if (context.Proxy.Reflection.IsEnum(CallerName))
            {
                return context.Proxy.Reflection.GetEnumValue(CallerName, Name);
            }
            else
            {
                return context.Proxy.Reflection.GetStaticProperty(CallerName, Name);
            }
        }

        public override object MakeLoadFromArray(RuntimeContext context, int index)
        {
            var array = MakeLoad(context);

            return context.Proxy.Casting.GetArrayIndexValue(array, index);
        }
    }
}

using XppInterpreter.Core;

namespace XppInterpreter.Interpreter.Bytecode
{
    class InstanceLoad : Load
    {
        public InstanceLoad(string name, bool isArray) : base(name, isArray) { }

        public override object MakeLoad(RuntimeContext context)
        {
            object caller = context.Stack.Pop();

            // Script class instance: fields live in the instance field scope
            if (caller is ScriptObject scriptObject)
            {
                RuntimeClassField scriptField = scriptObject.Class.FindField(Name);

                if (scriptField is null && context.ActiveClass?.ExtensionTarget != null &&
                    scriptObject.Class.IsOrExtends(context.ActiveClass.ExtensionTarget.Name))
                {
                    scriptField = context.ActiveClass.FindField(Name, walkBaseChain: false);
                }

                if (scriptField is null)
                {
                    scriptField = scriptObject.Class.FindExtensionField(Name, isStatic: false);
                }

                if (scriptField != null)
                {
                    if (scriptField.IsStatic)
                    {
                        throw new System.Exception($"Static field {scriptField.DeclaringClass.Name}.{Name} must be accessed through its class.");
                    }

                    ScriptClassRuntime.EnsureFieldAccessible(scriptField, context.ActiveClass, writing: false);
                }

                try
                {
                    return ScriptClassRuntime.GetInstanceFieldScope(context, scriptObject, scriptField).GetVar(Name);
                }
                catch when (scriptObject.Class.FindCompiledBaseName() != null)
                {
                    // Forward unknown members to the wrapped compiled base instance
                    object baseInstance = ScriptClassRuntime.EnsureBaseInstance(context, scriptObject);
                    return context.Proxy.Reflection.GetInstanceProperty(baseInstance, Name);
                }
            }

            // Check if it's a delegate
            var field = ReflectionHelper.GetField(caller.GetType(), Name);

            if (field != null && ReflectionHelper.IsDelegateType(field.FieldType))
            {
                return new EventSubscriptionHandle(caller, Name);
            }

            if (ClrBridge.IsClrOnlyInstance(context, caller))
            {
                return ClrBridge.GetMember(caller.GetType(), caller, Name);
            }

            return context.Proxy.Reflection.GetInstanceProperty(caller, Name);
        }

        public override object MakeLoadFromArray(RuntimeContext context, int index)
        {
            var array = MakeLoad(context);

            return context.Proxy.Casting.GetArrayIndexValue(array, index);
        }
    }
}

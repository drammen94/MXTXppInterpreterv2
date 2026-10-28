using System;

namespace XppInterpreter.Interpreter.Bytecode
{
    class Store : IInstruction
    {
        public string Name { get; }
        public string OperationCode
        {
            get
            {
                if (FromStack)
                {
                    return $"STORE_PROP {Name}";
                }
                else
                {
                    return $"STORE {Name}";
                }
            }
        }
        public bool Top { get; }
        public bool FromStack { get; }
        public bool IsArray { get; }
        public string TypeName { get; }
        public Type ClrType { get; }

        public Store(string name, bool fromStack, bool top, bool isArray, string typeName, Type clrType)
        {
            Name = name;
            Top = top;
            FromStack = fromStack;
            IsArray = isArray;
            TypeName = typeName;
            ClrType = clrType;
        }

        internal virtual object GetValue(RuntimeContext context)
        {
            return context.Stack.Pop();
        }

        public void Execute(RuntimeContext context)
        {
            int index = IsArray ? (int)context.Stack.Pop() : 0;

            if (FromStack)
            {
                var caller = context.Stack.Pop();
                var value = GetValue(context);

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
                            throw new Exception($"Static field {scriptField.DeclaringClass.Name}.{Name} must be accessed through its class.");
                        }

                        ScriptClassRuntime.EnsureFieldAccessible(scriptField, context.ActiveClass, writing: true);
                    }

                    global::XppInterpreter.Core.Scope fieldScope = ScriptClassRuntime.GetInstanceFieldScope(context, scriptObject, scriptField);

                    if (IsArray)
                    {
                        var scriptArray = fieldScope.GetVar(Name);
                        context.Proxy.Casting.SetArrayIndexValue(scriptArray, index, value);
                    }
                    else
                    {
                        try
                        {
                            fieldScope.SetVar(Name, value, context.Proxy.Casting);
                        }
                        catch when (scriptObject.Class.FindCompiledBaseName() != null)
                        {
                            // Forward unknown members to the wrapped compiled base instance
                            object baseInstance = ScriptClassRuntime.EnsureBaseInstance(context, scriptObject);
                            context.Proxy.Reflection.SetInstanceProperty(baseInstance, Name, value);
                        }
                    }
                }
                else if (!IsArray && ClrBridge.IsClrOnlyInstance(context, caller))
                {
                    ClrBridge.SetMember(caller.GetType(), caller, Name, ScriptClassRuntime.MarshalToNative(value));
                }
                else if (IsArray)
                {
                    // Get the instance property array and assign the value to the corresponding index
                    var array = context.Proxy.Reflection.GetInstanceProperty(caller, Name);
                    context.Proxy.Casting.SetArrayIndexValue(array, index, value);
                }
                else
                {
                    context.Proxy.Reflection.SetInstanceProperty(caller, Name, ScriptClassRuntime.MarshalToNative(value));
                }
            }
            else
            {
                var value = GetValue(context);

                if (IsArray)
                {
                    var array = context.ScopeHandler.CurrentScope.GetVar(Name);
                    context.Proxy.Casting.SetArrayIndexValue(array, index, value);
                }
                else
                {
                    bool declaration = Top; // for the sake of clarity

                    if (declaration)
                    {
                        if (value is null && context.Proxy.Reflection.IsCommonType(ClrType))
                        {
                            var defaultValue = context.Proxy.Casting.GetDefaultValueForType(TypeName);
                            context.ScopeHandler.CurrentScope.SetVar(Name, defaultValue, context.Proxy.Casting, Top, ClrType, TypeName);
                        }
                        else
                        {
                            context.ScopeHandler.CurrentScope.SetVar(Name, value, context.Proxy.Casting, Top, ClrType, TypeName);
                        }
                    }
                    else
                    {
                        var currentValue = context.ScopeHandler.CurrentScope.GetVar(Name);
                        var type = currentValue?.GetType();

                        if (value is null && context.Proxy.Reflection.IsCommonType(type))
                        {
                            context.Proxy.Reflection.ClearCommon(currentValue);
                        }
                        else
                        {
                            context.ScopeHandler.CurrentScope.SetVar(Name, value, context.Proxy.Casting);
                        }
                    }
                }
            }
        }
    }
}

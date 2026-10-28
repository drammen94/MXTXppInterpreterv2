using System;

namespace XppInterpreter.Interpreter.Bytecode
{
    internal sealed class AosCallResult
    {
        public object ReturnValue;
        public object[] UpdatedArguments;
    }

    /// <summary>Single gateway for interpreted calls into compiled X++.</summary>
    internal static class AosCallDispatcher
    {
        public static AosCallResult CallInstance(RuntimeContext context, object instance, string methodName, object[] arguments)
        {
            string typeName = instance?.GetType().Name;
            var hook = AttributedMethodHook.Before(context, typeName, methodName, instance, arguments);
            object[] nativeArguments = ScriptClassRuntime.MarshalToNative(hook.Arguments);
            object value = context.Proxy.Reflection.CallInstanceFunction(instance, methodName, nativeArguments);
            value = AttributedMethodHook.After(context, typeName, methodName, instance, hook, value);
            return new AosCallResult { ReturnValue = value, UpdatedArguments = nativeArguments };
        }

        public static AosCallResult CallStatic(RuntimeContext context, string typeName, string methodName, object[] arguments)
        {
            var hook = AttributedMethodHook.Before(context, typeName, methodName, null, arguments);
            object[] nativeArguments = ScriptClassRuntime.MarshalToNative(hook.Arguments);
            object value = context.Proxy.Reflection.CallStaticFunction(typeName, methodName, nativeArguments);
            value = AttributedMethodHook.After(context, typeName, methodName, null, hook, value);
            return new AosCallResult { ReturnValue = value, UpdatedArguments = nativeArguments };
        }
    }
}

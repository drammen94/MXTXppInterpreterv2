using System;
using System.Collections.Generic;
using System.Linq;
using XppInterpreter.Interpreter.Metadata;
using XppInterpreter.Parser;

namespace XppInterpreter.Interpreter.Bytecode
{
    /// <summary>
    /// Runtime implementation of PreHandlerFor/PostHandlerFor for calls crossing
    /// the interpreter/AOS boundary. The handler receives a real XppPrePostArgs in
    /// AOS and a compatible managed object in standalone hosts.
    /// </summary>
    internal static class AttributedMethodHook
    {
        internal sealed class Invocation
        {
            public object EventArgs;
            public object[] Arguments;
            public string[] ParameterNames;
            public object ReturnValue;
        }

        public static Invocation Before(
            RuntimeContext context,
            string targetTypeName,
            string methodName,
            object instance,
            object[] arguments)
        {
            RuntimeClassMethod[] handlers = FindHandlers(context.ByteCode, targetTypeName, methodName, before: true).ToArray();
            RuntimeClassMethod[] postHandlers = FindHandlers(context.ByteCode, targetTypeName, methodName, before: false).ToArray();

            var invocation = new Invocation
            {
                Arguments = arguments ?? new object[0],
                ParameterNames = ResolveParameterNames(context, targetTypeName, methodName, arguments?.Length ?? 0),
            };

            if (handlers.Length == 0 && postHandlers.Length == 0)
            {
                return invocation;
            }

            invocation.EventArgs = CreateEventArgs(context, instance, methodName, before: true);
            PopulateArguments(context, invocation);

            foreach (RuntimeClassMethod handler in handlers)
            {
                ValidateHandler(handler);
                ScriptClassRuntime.RunSynchronous(context, handler, null, new[] { invocation.EventArgs });
                ReadArguments(context, invocation);
                PopulateArguments(context, invocation);
            }

            return invocation;
        }

        public static object After(
            RuntimeContext context,
            string targetTypeName,
            string methodName,
            object instance,
            Invocation invocation,
            object returnValue)
        {
            RuntimeClassMethod[] handlers = FindHandlers(context.ByteCode, targetTypeName, methodName, before: false).ToArray();
            if (handlers.Length == 0) return returnValue;

            invocation = invocation ?? Before(context, targetTypeName, methodName, instance, new object[0]);
            invocation.EventArgs = invocation.EventArgs ?? CreateEventArgs(context, instance, methodName, before: false);
            invocation.ReturnValue = returnValue;

            CallIfPresent(context, invocation.EventArgs, "setCalledWhen", ResolveCalledWhen(context, before: false));
            CallIfPresent(context, invocation.EventArgs, "allowSetReturn", true);
            CallIfPresent(context, invocation.EventArgs, "setReturnValue", returnValue);

            foreach (RuntimeClassMethod handler in handlers)
            {
                ValidateHandler(handler);
                ScriptClassRuntime.RunSynchronous(context, handler, null, new[] { invocation.EventArgs });
            }

            object changed = CallIfPresent(context, invocation.EventArgs, "getReturnValue");
            // The args object was seeded with the native return value before the
            // post handlers ran, so null here is also meaningful: a handler may
            // deliberately replace a non-null result with null.
            return changed;
        }

        private static IEnumerable<RuntimeClassMethod> FindHandlers(
            ByteCode byteCode,
            string targetTypeName,
            string methodName,
            bool before)
        {
            if (byteCode?.DeclaredClasses is null) yield break;
            string expected = before ? "PreHandlerFor" : "PostHandlerFor";

            foreach (RuntimeClass runtimeClass in byteCode.DeclaredClasses)
            {
                foreach (RuntimeClassMethod method in runtimeClass.Methods)
                {
                    foreach (XppAttributeDeclaration attribute in method.Attributes)
                    {
                        if (!string.Equals(Normalize(attribute.Name), expected, StringComparison.OrdinalIgnoreCase)) continue;

                        string sourceType = ArgumentAfter(attribute, "classStr") ??
                            ArgumentAfter(attribute, "tableStr") ??
                            ArgumentAfter(attribute, "formStr");
                        string sourceMethod = SecondArgumentAfter(attribute, "methodStr") ??
                            SecondArgumentAfter(attribute, "tableMethodStr") ??
                            SecondArgumentAfter(attribute, "formMethodStr") ??
                            LastArgument(attribute);

                        if (string.Equals(SimpleName(sourceType), SimpleName(targetTypeName), StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(sourceMethod, methodName, StringComparison.OrdinalIgnoreCase))
                        {
                            yield return method;
                        }
                    }
                }
            }
        }

        private static string[] ResolveParameterNames(RuntimeContext context, string typeName, string methodName, int count)
        {
            try
            {
                AosMethodDescriptor method = context.AosTypes.DescribeType(SimpleName(typeName))?.FindMethod(methodName);
                if (method != null && method.Parameters.Count >= count)
                {
                    return method.Parameters.Take(count).Select((parameter, index) =>
                        string.IsNullOrWhiteSpace(parameter.Name) ? "arg" + (index + 1) : parameter.Name).ToArray();
                }
            }
            catch
            {
            }

            return Enumerable.Range(1, count).Select(index => "arg" + index).ToArray();
        }

        private static object CreateEventArgs(RuntimeContext context, object instance, string methodName, bool before)
        {
            Type eventArgsType = null;
            try { eventArgsType = context.Proxy.Casting.GetSystemTypeFromTypeName("XppPrePostArgs"); }
            catch { }
            eventArgsType = eventArgsType ?? Core.ClrTypeResolver.FindType("Microsoft.Dynamics.Ax.Xpp.XppPrePostArgs");

            if (eventArgsType != null)
            {
                object calledWhen = ResolveCalledWhen(context, before);
                try { return Activator.CreateInstance(eventArgsType, instance, methodName, calledWhen); }
                catch
                {
                    try { return Activator.CreateInstance(eventArgsType, instance, methodName); }
                    catch { }
                }
            }

            return new InterpretedPrePostArgs(instance, methodName, before);
        }

        private static object ResolveCalledWhen(RuntimeContext context, bool before)
        {
            Type enumType = null;
            try { enumType = context.Proxy.Casting.GetSystemTypeFromTypeName("XppEventHandlerCalledWhen"); }
            catch { }
            enumType = enumType ?? Core.ClrTypeResolver.FindType("Microsoft.Dynamics.Ax.Xpp.XppEventHandlerCalledWhen");
            return enumType != null ? Enum.Parse(enumType, before ? "Pre" : "Post", true) : (object)(before ? 0 : 1);
        }

        private static void PopulateArguments(RuntimeContext context, Invocation invocation)
        {
            if (invocation.EventArgs is InterpretedPrePostArgs managed)
            {
                managed.ReplaceArguments(invocation.ParameterNames, invocation.Arguments);
                return;
            }

            for (int index = 0; index < invocation.Arguments.Length; index++)
            {
                object exists = CallIfPresent(context, invocation.EventArgs, "existsArg", invocation.ParameterNames[index]);
                if (exists != null && Convert.ToBoolean(exists))
                {
                    CallIfPresent(context, invocation.EventArgs, "setArg", invocation.ParameterNames[index], invocation.Arguments[index]);
                }
                else
                {
                    CallIfPresent(context, invocation.EventArgs, "addArg", invocation.ParameterNames[index], invocation.Arguments[index]);
                }
            }
        }

        private static void ReadArguments(RuntimeContext context, Invocation invocation)
        {
            if (invocation.EventArgs is InterpretedPrePostArgs managed)
            {
                invocation.Arguments = managed.Arguments;
                return;
            }

            for (int index = 0; index < invocation.Arguments.Length; index++)
            {
                object value = CallIfPresent(context, invocation.EventArgs, "getArg", invocation.ParameterNames[index]);
                if (value != null) invocation.Arguments[index] = value;
            }
        }

        private static object CallIfPresent(RuntimeContext context, object instance, string methodName, params object[] arguments)
        {
            if (instance is null) return null;

            try
            {
                return context.Proxy.Reflection.CallInstanceFunction(instance, methodName, arguments ?? new object[0]);
            }
            catch
            {
                try
                {
                    return ClrBridge.InvokeInstance(instance, methodName, arguments ?? new object[0], out _);
                }
                catch
                {
                    return null;
                }
            }
        }

        private static void ValidateHandler(RuntimeClassMethod handler)
        {
            if (!handler.IsStatic || handler.Parameters.Count != 1)
            {
                throw new Exception($"Attributed pre/post handler {handler.DeclaringClass.Name}.{handler.Name} must be static and take one XppPrePostArgs parameter.");
            }
        }

        private static string ArgumentAfter(XppAttributeDeclaration attribute, string functionName)
        {
            int index = attribute.Words.FindIndex(word => string.Equals(word, functionName, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < attribute.Words.Count ? attribute.Words[index + 1] : null;
        }

        private static string SecondArgumentAfter(XppAttributeDeclaration attribute, string functionName)
        {
            int index = attribute.Words.FindIndex(word => string.Equals(word, functionName, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 2 < attribute.Words.Count ? attribute.Words[index + 2] : null;
        }

        private static string LastArgument(XppAttributeDeclaration attribute) =>
            attribute.Words.Count > 1 ? attribute.Words[attribute.Words.Count - 1] : null;

        private static string Normalize(string name) =>
            name != null && name.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - "Attribute".Length)
                : name ?? string.Empty;

        private static string SimpleName(string name)
        {
            if (name is null) return null;
            int separator = Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+'));
            return separator >= 0 ? name.Substring(separator + 1) : name;
        }
    }

    /// <summary>Standalone-host equivalent of the AOS XppPrePostArgs API.</summary>
    public sealed class InterpretedPrePostArgs
    {
        private readonly Dictionary<string, object> _arguments =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private string[] _names = new string[0];
        private object _returnValue;

        public InterpretedPrePostArgs(object instance, string methodName, bool before)
        {
            This = instance;
            MethodName = methodName;
            CalledWhen = before ? 0 : 1;
        }

        public object This { get; }
        public string MethodName { get; }
        public int CalledWhen { get; private set; }
        public object[] Arguments => _names.Select(name => _arguments[name]).ToArray();
        public object getThis() => This;
        public object getArg(string name) => _arguments.TryGetValue(name, out object value) ? value : null;
        public object getArgNum(int index) => index > 0 && index <= _names.Length ? getArg(_names[index - 1]) : null;
        public int existsArg(string name) => _arguments.ContainsKey(name) ? 1 : 0;
        public int addArg(string name, object value) { setArg(name, value); return 1; }
        public int setArg(string name, object value) { _arguments[name] = value; return 1; }
        public int setArgNum(int index, object value) { if (index > 0 && index <= _names.Length) setArg(_names[index - 1], value); return 1; }
        public object getReturnValue() => _returnValue;
        public int setReturnValue(object value) { _returnValue = value; return 1; }
        public int allowSetReturn(bool value) => value ? 1 : 0;
        public int setCalledWhen(object value) { CalledWhen = Convert.ToInt32(value); return CalledWhen; }
        public int getCalledWhen() => CalledWhen;

        internal void ReplaceArguments(string[] names, object[] values)
        {
            _names = names ?? new string[0];
            _arguments.Clear();
            for (int index = 0; index < _names.Length; index++)
            {
                _arguments[_names[index]] = values[index];
            }
        }
    }
}

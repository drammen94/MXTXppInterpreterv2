using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using XppInterpreter.Interpreter.Proxy;

namespace XppInterpreter.Tests
{
    internal static class TestProxyFactory
    {
        public static object LastCaptured { get; private set; }
        public static int TtsBeginCount { get; private set; }
        public static int TtsCommitCount { get; private set; }
        public static int TtsAbortCount { get; private set; }

        public static XppProxy Create()
        {
            AosEventSource.Reset();
            Microsoft.Dynamics.Ax.Xpp.DataEventManager.Reset();
            LastCaptured = null;
            TtsBeginCount = 0;
            TtsCommitCount = 0;
            TtsAbortCount = 0;
            return new XppProxy(
                Proxy<IIntrinsicFunctionProvider>(DefaultResult),
                Proxy<IXppBinaryOperationProxy>(BinaryResult),
                Proxy<IXppCastingProxy>(CastingResult),
                Proxy<IXppUnaryOperationProxy>(UnaryResult),
                Proxy<IXppDataAccessProxy>(DataResult),
                Proxy<IXppReflectionProxy>(ReflectionResult),
                Proxy<IXppExceptionsProxy>(ExceptionResult),
                Proxy<IXppQueryGenerationProxy>(DefaultResult));
        }

        private static object DataResult(MethodInfo method, object[] arguments)
        {
            switch (method.Name)
            {
                case "TtsBegin": TtsBeginCount++; break;
                case "TtsCommit": TtsCommitCount++; break;
                case "TtsAbort": TtsAbortCount++; break;
            }

            return DefaultResult(method, arguments);
        }

        private static T Proxy<T>(Func<MethodInfo, object[], object> handler) where T : class
        {
            return (T)new InterfaceProxy(typeof(T), handler).GetTransparentProxy();
        }

        private static object BinaryResult(MethodInfo method, object[] arguments)
        {
            object left = arguments[0];
            object right = arguments[1];

            switch (method.Name)
            {
                case "Add":
                    if (left is string || right is string) return Convert.ToString(left) + Convert.ToString(right);
                    return Numeric(left, right, (a, b) => a + b);
                case "Substract": return Numeric(left, right, (a, b) => a - b);
                case "Multiply": return Numeric(left, right, (a, b) => a * b);
                case "Divide": return 999m; // Deliberately distinct from IntDivide for its regression test.
                case "IntDivide": return Convert.ToInt64(left) / Convert.ToInt64(right);
                case "Mod": return Convert.ToInt64(left) % Convert.ToInt64(right);
                case "LeftShift": return Convert.ToInt64(left) << Convert.ToInt32(right);
                case "RightShift": return Convert.ToInt64(left) >> Convert.ToInt32(right);
                case "BinaryAnd": return Convert.ToInt64(left) & Convert.ToInt64(right);
                case "BinaryOr": return Convert.ToInt64(left) | Convert.ToInt64(right);
                case "BinaryXOr": return Convert.ToInt64(left) ^ Convert.ToInt64(right);
                case "Greater": return Convert.ToDecimal(left, CultureInfo.InvariantCulture) > Convert.ToDecimal(right, CultureInfo.InvariantCulture);
                case "AreEqual":
                    if (left is string || right is string)
                    {
                        return string.Equals(Convert.ToString(left), Convert.ToString(right), StringComparison.OrdinalIgnoreCase);
                    }
                    return Equals(left, right) || Convert.ToDecimal(left ?? 0, CultureInfo.InvariantCulture) == Convert.ToDecimal(right ?? 0, CultureInfo.InvariantCulture);
                default: return DefaultResult(method, arguments);
            }
        }

        private static object Numeric(object left, object right, Func<decimal, decimal, decimal> operation)
        {
            decimal result = operation(
                Convert.ToDecimal(left, CultureInfo.InvariantCulture),
                Convert.ToDecimal(right, CultureInfo.InvariantCulture));

            if (left is int && right is int) return Decimal.ToInt32(result);
            if (left is long || right is long) return Decimal.ToInt64(result);
            return result;
        }

        private static object CastingResult(MethodInfo method, object[] arguments)
        {
            switch (method.Name)
            {
                case "ToBoolean":
                    if (arguments[0] == null) return false;
                    if (arguments[0] is bool) return arguments[0];
                    if (arguments[0] is string) return !string.IsNullOrEmpty((string)arguments[0]);
                    return Convert.ToDecimal(arguments[0], CultureInfo.InvariantCulture) != 0;
                case "GetDefaultValueForType": return DefaultForName((string)arguments[0]);
                case "GetDefaultValueForSystemType":
                    var defaultType = arguments[0] as Type;
                    return defaultType != null && defaultType.IsValueType ? Activator.CreateInstance(defaultType) : null;
                case "CreateDynamicArray": return new object[0];
                case "CreateFixedArray": return new object[(int)arguments[1]];
                case "GetArrayIndexValue": return ((object[])arguments[0])[(int)arguments[1] - 1];
                case "SetArrayIndexValue":
                    ((object[])arguments[0])[(int)arguments[1] - 1] = arguments[2];
                    return null;
                case "GetSystemTypeFromTypeName": return TypeForName((string)arguments[0]);
                case "Is":
                    var isType = TypeForName((string)arguments[1]);
                    return isType != null && arguments[0] != null && isType.IsInstanceOfType(arguments[0]);
                case "As":
                    var asType = TypeForName((string)arguments[1]);
                    return asType != null && arguments[0] != null && asType.IsInstanceOfType(arguments[0]) ? arguments[0] : null;
                case "ImplicitConversionExists": return CanConvert((Type)arguments[0], (Type)arguments[1]);
                case "IsReferenceType": return arguments[0] is Type && !((Type)arguments[0]).IsValueType;
                case "Cast": return ConvertValue(arguments[0], (Type)arguments[1]);
                case "GetArrayType": return typeof(object[]);
                case "CreateDate": return new DateTime((int)arguments[2], (int)arguments[1], (int)arguments[0]);
                case "IsStringEDT": return false;
                case "TruncateEDTString": return arguments[0];
                default: return DefaultResult(method, arguments);
            }
        }

        private static object ReflectionResult(MethodInfo method, object[] arguments)
        {
            switch (method.Name)
            {
                case "CreateInstance":
                    return Activator.CreateInstance(TypeForName((string)arguments[0]), (object[])arguments[1]);
                case "CallInstanceFunction":
                    return InvokeMethod(arguments[0].GetType(), arguments[0], (string)arguments[1], (object[])arguments[2]);
                case "CallStaticFunction":
                    return InvokeMethod(TypeForName((string)arguments[0]), null, (string)arguments[1], (object[])arguments[2]);
                case "IsInstantiable": return TypeForName((string)arguments[0]) != null;
                case "IsEnum":
                    var enumType = TypeForName((string)arguments[0]);
                    return enumType != null && enumType.IsEnum;
                case "IsCommon": return false;
                case "IsCommonType": return false;
                case "CallGlobalOrPredefinedFunction":
                    if (string.Equals((string)arguments[1], "capture", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals((string)arguments[1], "captureAny", StringComparison.OrdinalIgnoreCase))
                    {
                        LastCaptured = ((object[])arguments[2])[0];
                        return null;
                    }
                    return null;
                case "GetDateType": return typeof(DateTime);
                case "GetUtcDateTimeType": return typeof(DateTime);
                case "TypeHasMethod": return ((Type)arguments[0]).GetMethods().Any(m => string.Equals(m.Name, (string)arguments[1], StringComparison.OrdinalIgnoreCase));
                case "TypeHasProperty": return ((Type)arguments[0]).GetProperties().Any(p => string.Equals(p.Name, (string)arguments[1], StringComparison.OrdinalIgnoreCase));
                case "GetMethodReturnType":
                    return ((Type)arguments[0]).GetMethods().First(m => string.Equals(m.Name, (string)arguments[1], StringComparison.OrdinalIgnoreCase)).ReturnType;
                case "GetMethodSyntax":
                    var syntaxType = TypeForName((string)arguments[0]);
                    var syntaxMethod = syntaxType?.GetMethods().FirstOrDefault(m => string.Equals(m.Name, (string)arguments[1], StringComparison.OrdinalIgnoreCase));
                    return syntaxMethod == null ? string.Empty : MethodSyntax(syntaxMethod);
                case "GetFieldReturnType":
                    var owner = (Type)arguments[0];
                    var name = (string)arguments[1];
                    var property = owner.GetProperties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (property != null) return property.PropertyType;
                    var field = owner.GetFields().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                    return field == null ? null : field.FieldType;
                case "EnumHasMember": return false;
                case "LabelIdToValue": return arguments[0];
                default: return DefaultResult(method, arguments);
            }
        }

        private static object UnaryResult(MethodInfo method, object[] arguments)
        {
            return method.Name == "Negate"
                ? (object)!Convert.ToBoolean(arguments[0], CultureInfo.InvariantCulture)
                : DefaultResult(method, arguments);
        }

        private static object ExceptionResult(MethodInfo method, object[] arguments)
        {
            if (method.Name == "Throw")
            {
                throw new InvalidOperationException(Convert.ToString(arguments[0], CultureInfo.InvariantCulture));
            }

            return method.Name == "IsExceptionMember" || method.Name == "SetRetryCount"
                ? DefaultResult(method, arguments)
                : null;
        }

        private static object DefaultForName(string name)
        {
            Type type = TypeForName(name);
            if (type == typeof(string)) return string.Empty;
            if (type == typeof(object[])) return new object[0];
            return type != null && type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static Type TypeForName(string name)
        {
            switch ((name ?? string.Empty).ToLowerInvariant())
            {
                case "int": return typeof(int);
                case "int64": return typeof(long);
                case "real": return typeof(decimal);
                case "str": return typeof(string);
                case "boolean": return typeof(bool);
                case "guid": return typeof(Guid);
                case "date": return typeof(DateTime);
                case "utcdatetime": return typeof(DateTime);
                case "timeofday": return typeof(int);
                case "container": return typeof(object[]);
                case "anytype": return typeof(object);
                case "global": return typeof(TestGlobal);
                case "aoscompiledtarget": return typeof(AosCompiledTarget);
                case "aoseventsource": return typeof(AosEventSource);
                case "aosmetadatafixture": return typeof(AosMetadataFixture);
                case "iaoscallback": return typeof(IAosCallback);
                case "aosinterfaceconsumer": return typeof(AosInterfaceConsumer);
                case "aosvirtualbase": return typeof(AosVirtualBase);
                case "aosbaseconsumer": return typeof(AosBaseConsumer);
                case "xppprepostargs": return typeof(XppInterpreter.Interpreter.Bytecode.InterpretedPrePostArgs);
                default: return Type.GetType(name, false, true);
            }
        }

        private static object InvokeMethod(Type owner, object instance, string name, object[] arguments)
        {
            MethodInfo method = owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .First(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    candidate.GetParameters().Length == arguments.Length);
            return method.Invoke(instance, arguments);
        }

        private static string MethodSyntax(MethodInfo method)
        {
            string returnType = XppTypeName(method.ReturnType);
            string parameters = string.Join(", ", method.GetParameters().Select(parameter =>
                XppTypeName(parameter.ParameterType) + " " + parameter.Name +
                (parameter.IsOptional ? " = 0" : string.Empty)));
            return (method.IsPublic ? "public " : "protected ") +
                (method.IsStatic ? "static " : string.Empty) +
                returnType + " " + method.Name + "(" + parameters + ")";
        }

        private static string XppTypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(int)) return "int";
            if (type == typeof(long)) return "int64";
            if (type == typeof(string)) return "str";
            if (type == typeof(bool)) return "boolean";
            return type.Name;
        }

        private static bool CanConvert(Type from, Type to)
        {
            if (from == null || to == null) return false;
            if (to.IsAssignableFrom(from)) return true;
            return IsNumeric(from) && IsNumeric(to);
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(int) || type == typeof(long) || type == typeof(decimal) ||
                type == typeof(double) || type == typeof(float) || type == typeof(short) || type == typeof(byte);
        }

        private static object ConvertValue(object value, Type targetType)
        {
            if (value == null) return targetType != null && targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            if (targetType == null || targetType.IsInstanceOfType(value)) return value;
            if (targetType.IsEnum) return Enum.ToObject(targetType, value);
            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        private static object DefaultResult(MethodInfo method, object[] arguments)
        {
            Type returnType = method.ReturnType;
            if (returnType == typeof(void)) return null;
            if (returnType == typeof(string)) return null;
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }

        private sealed class InterfaceProxy : RealProxy
        {
            private readonly Func<MethodInfo, object[], object> _handler;

            public InterfaceProxy(Type interfaceType, Func<MethodInfo, object[], object> handler)
                : base(interfaceType)
            {
                _handler = handler;
            }

            public override IMessage Invoke(IMessage message)
            {
                var call = (IMethodCallMessage)message;

                try
                {
                    object result = _handler((MethodInfo)call.MethodBase, call.Args);
                    return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
                }
                catch (Exception exception)
                {
                    return new ReturnMessage(exception, call);
                }
            }
        }

        private static class TestGlobal
        {
            public static void capture(int value)
            {
            }

            public static void captureAny(object value)
            {
            }
        }
    }
}

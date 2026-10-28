using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace XppInterpreter.Interpreter
{
    /// <summary>
    /// Direct CLR invocation used for .NET types that the X++ metadata proxies do not
    /// know about (System.* and other namespace-qualified types). Performs
    /// case-insensitive member lookup and best-effort overload selection with
    /// X++-friendly numeric conversions and params-array expansion.
    /// </summary>
    public static class ClrBridge
    {
        private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.Instance;

        private static readonly Dictionary<Type, bool> _clrOnlyCache = new Dictionary<Type, bool>();
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// True when the instance's type is not resolvable through the X++ metadata
        /// proxy by its simple name, meaning member access must use direct CLR reflection
        /// </summary>
        public static bool IsClrOnlyInstance(RuntimeContext context, object instance)
        {
            if (instance is null) return false;

            Type type = instance.GetType();

            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type.IsEnum)
            {
                return false;
            }

            lock (_cacheLock)
            {
                if (_clrOnlyCache.TryGetValue(type, out bool cached))
                {
                    return cached;
                }
            }

            bool clrOnly;

            try
            {
                clrOnly = context.Proxy.Casting.GetSystemTypeFromTypeName(type.Name) != type;
            }
            catch
            {
                clrOnly = true;
            }

            lock (_cacheLock)
            {
                _clrOnlyCache[type] = clrOnly;
            }

            return clrOnly;
        }

        public static object CreateInstance(Type type, object[] arguments)
        {
            ConstructorInfo constructor = SelectOverload(
                type.GetConstructors(BindingFlags.Public | BindingFlags.Instance),
                arguments);

            if (constructor is null)
            {
                throw new Exception($"No constructor of {type.FullName} matches {arguments?.Length ?? 0} argument(s).");
            }

            return constructor.Invoke(FitArguments(constructor.GetParameters(), arguments));
        }

        public static object InvokeInstance(object instance, string methodName, object[] arguments)
        {
            return Invoke(instance.GetType(), instance, methodName, arguments, InstanceFlags, out _);
        }

        public static object InvokeInstance(object instance, string methodName, object[] arguments, out object[] fittedArguments)
        {
            return Invoke(instance.GetType(), instance, methodName, arguments, InstanceFlags, out fittedArguments);
        }

        public static object InvokeStatic(Type type, string methodName, object[] arguments)
        {
            return Invoke(type, null, methodName, arguments, StaticFlags, out _);
        }

        public static object InvokeStatic(Type type, string methodName, object[] arguments, out object[] fittedArguments)
        {
            return Invoke(type, null, methodName, arguments, StaticFlags, out fittedArguments);
        }

        private static object Invoke(Type type, object instance, string methodName, object[] arguments, BindingFlags flags, out object[] fittedArguments)
        {
            MethodInfo method = SelectOverload(
                type.GetMethods(flags).Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)).ToArray(),
                arguments);

            if (method is null)
            {
                throw new Exception($"Method {methodName} was not found on {type.FullName} for {arguments?.Length ?? 0} argument(s).");
            }

            ParameterInfo[] parameters = method.GetParameters();
            object[] invocationArguments = FitArguments(parameters, arguments);
            object result = method.Invoke(instance, invocationArguments);

            // The out array is indexed by ORIGINAL argument position (for byref
            // writeback); params-array-packed arguments keep their original value
            int argumentCount = arguments?.Length ?? 0;
            int fixedCount = HasParamsArray(parameters) ? parameters.Length - 1 : parameters.Length;

            fittedArguments = new object[argumentCount];

            for (int i = 0; i < argumentCount; i++)
            {
                fittedArguments[i] = i < fixedCount ? invocationArguments[i] : arguments[i];
            }

            return result;
        }

        public static object GetMember(Type type, object instance, string memberName)
        {
            BindingFlags flags = instance is null ? StaticFlags : InstanceFlags;

            if (type.IsEnum && instance is null)
            {
                return Enum.Parse(type, memberName, true);
            }

            PropertyInfo property = FindProperty(type, memberName, flags);

            if (property != null)
            {
                return property.GetValue(instance);
            }

            FieldInfo field = FindField(type, memberName, flags);

            if (field != null)
            {
                return field.GetValue(instance);
            }

            throw new Exception($"Member {memberName} was not found on {type.FullName}.");
        }

        public static void SetMember(Type type, object instance, string memberName, object value)
        {
            BindingFlags flags = instance is null ? StaticFlags : InstanceFlags;

            PropertyInfo property = FindProperty(type, memberName, flags);

            if (property != null)
            {
                property.SetValue(instance, ConvertArgument(value, property.PropertyType));
                return;
            }

            FieldInfo field = FindField(type, memberName, flags);

            if (field != null)
            {
                field.SetValue(instance, ConvertArgument(value, field.FieldType));
                return;
            }

            throw new Exception($"Member {memberName} was not found on {type.FullName}.");
        }

        public static bool HasMember(Type type, string memberName)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

            return type.GetMethods(flags).Any(m => string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase))
                || FindProperty(type, memberName, flags) != null
                || FindField(type, memberName, flags) != null;
        }

        private static PropertyInfo FindProperty(Type type, string name, BindingFlags flags)
        {
            return type.GetProperties(flags).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static FieldInfo FindField(Type type, string name, BindingFlags flags)
        {
            return type.GetFields(flags).FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasParamsArray(ParameterInfo[] parameters)
        {
            return parameters.Length > 0 &&
                parameters[parameters.Length - 1].IsDefined(typeof(ParamArrayAttribute), false);
        }

        /// <summary>
        /// Picks the overload whose parameters best fit the argument list. Exact and
        /// assignable matches score highest, widening numeric conversions next, and
        /// lossy (narrowing) numeric conversions are heavily penalized so an int
        /// overload never silently truncates a real argument when a floating-point
        /// overload exists. Params arrays are supported with a small penalty.
        /// </summary>
        private static T SelectOverload<T>(T[] candidates, object[] arguments) where T : MethodBase
        {
            int argumentCount = arguments?.Length ?? 0;
            T best = null;
            int bestScore = int.MinValue;

            foreach (T candidate in candidates)
            {
                ParameterInfo[] parameters = candidate.GetParameters();
                bool hasParams = HasParamsArray(parameters);

                int requiredCount = parameters.Count(p => !p.HasDefaultValue) - (hasParams ? 1 : 0);

                if (argumentCount < requiredCount || (argumentCount > parameters.Length && !hasParams))
                {
                    continue;
                }

                int score = 0;
                bool compatible = true;

                for (int i = 0; i < argumentCount; i++)
                {
                    object argument = arguments[i];

                    Type parameterType;
                    bool viaParamsArray = hasParams && i >= parameters.Length - 1;

                    if (viaParamsArray)
                    {
                        // An exact array for the params parameter is matched directly
                        if (i == parameters.Length - 1 && argumentCount == parameters.Length &&
                            argument != null && parameters[i].ParameterType.IsInstanceOfType(argument))
                        {
                            score += 10;
                            continue;
                        }

                        parameterType = parameters[parameters.Length - 1].ParameterType.GetElementType();
                        score -= 1; // prefer fixed-arity overloads when available
                    }
                    else
                    {
                        parameterType = parameters[i].ParameterType;
                    }

                    score += ScoreArgument(argument, parameterType, ref compatible);

                    if (!compatible)
                    {
                        break;
                    }
                }

                if (compatible && score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }

        private static int ScoreArgument(object argument, Type parameterType, ref bool compatible)
        {
            // ref/out parameters are matched against their element type
            if (parameterType.IsByRef)
            {
                parameterType = parameterType.GetElementType();
            }

            if (argument is null)
            {
                return parameterType.IsValueType ? -50 : 5;
            }

            if (parameterType.IsInstanceOfType(argument))
            {
                return parameterType == argument.GetType() ? 20 : 10;
            }

            if (IsNumericType(argument.GetType()) && IsNumericType(parameterType))
            {
                // Lossy narrowing (real -> integer) is a last resort
                return IsLossyNumericConversion(argument.GetType(), parameterType) ? -10 : 7;
            }

            if (parameterType == typeof(object))
            {
                return 1;
            }

            compatible = false;
            return 0;
        }

        internal static bool IsNumericType(Type type)
        {
            return type == typeof(int) || type == typeof(long) || type == typeof(decimal) ||
                   type == typeof(double) || type == typeof(float) || type == typeof(short) ||
                   type == typeof(byte) || type == typeof(uint) || type == typeof(ulong) ||
                   type == typeof(sbyte) || type == typeof(ushort);
        }

        private static bool IsRealType(Type type)
        {
            return type == typeof(decimal) || type == typeof(double) || type == typeof(float);
        }

        private static bool IsLossyNumericConversion(Type from, Type to)
        {
            return IsRealType(from) && !IsRealType(to);
        }

        private static object[] FitArguments(ParameterInfo[] parameters, object[] arguments)
        {
            object[] fitted = new object[parameters.Length];
            int argumentCount = arguments?.Length ?? 0;
            bool hasParams = HasParamsArray(parameters);
            int fixedCount = hasParams ? parameters.Length - 1 : parameters.Length;

            for (int i = 0; i < fixedCount; i++)
            {
                if (i < argumentCount)
                {
                    fitted[i] = ConvertArgument(arguments[i], parameters[i].ParameterType);
                }
                else
                {
                    fitted[i] = parameters[i].HasDefaultValue
                        ? parameters[i].DefaultValue
                        : (parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null);
                }
            }

            if (hasParams)
            {
                Type paramsArrayType = parameters[parameters.Length - 1].ParameterType;
                Type elementType = paramsArrayType.GetElementType();

                // An exact array argument for the params parameter passes through
                if (argumentCount == parameters.Length &&
                    arguments[fixedCount] != null && paramsArrayType.IsInstanceOfType(arguments[fixedCount]))
                {
                    fitted[fixedCount] = arguments[fixedCount];
                }
                else
                {
                    int extraCount = Math.Max(0, argumentCount - fixedCount);
                    Array packed = Array.CreateInstance(elementType, extraCount);

                    for (int i = 0; i < extraCount; i++)
                    {
                        packed.SetValue(ConvertArgument(arguments[fixedCount + i], elementType), i);
                    }

                    fitted[fixedCount] = packed;
                }
            }

            return fitted;
        }

        internal static object ConvertArgument(object value, Type targetType)
        {
            if (targetType.IsByRef)
            {
                targetType = targetType.GetElementType();
            }

            if (value is null || targetType.IsInstanceOfType(value))
            {
                return value;
            }

            if (targetType.IsEnum)
            {
                return Enum.ToObject(targetType, value);
            }

            if (IsNumericType(value.GetType()) && IsNumericType(targetType))
            {
                return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(string))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            try
            {
                return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
            }
            catch
            {
                return value;
            }
        }
    }
}

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

            MethodShape shape = Shape(constructor);
            return constructor.Invoke(FitArguments(shape.Parameters, shape.HasParams, arguments));
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

        /// <summary>
        /// Reflection facts about a method that never change: parameters (GetParameters
        /// copies its array on every call), params-array presence and required arity
        /// </summary>
        private sealed class MethodShape
        {
            public readonly ParameterInfo[] Parameters;
            public readonly bool HasParams;
            public readonly int RequiredCount;
            public readonly bool HasByRef;
            public int Invocations;
            public Func<object, object[], object> CompiledInvoker;

            public MethodShape(MethodBase method)
            {
                Parameters = method.GetParameters();
                HasParams = Parameters.Length > 0 &&
                    Parameters[Parameters.Length - 1].IsDefined(typeof(ParamArrayAttribute), false);
                RequiredCount = Parameters.Count(p => !p.HasDefaultValue) - (HasParams ? 1 : 0);
                HasByRef = Parameters.Any(p => p.ParameterType.IsByRef);
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MethodBase, MethodShape> _shapes =
            new System.Runtime.CompilerServices.ConditionalWeakTable<MethodBase, MethodShape>();

        private static MethodShape Shape(MethodBase method)
        {
            return _shapes.GetValue(method, m => new MethodShape(m));
        }

        /// <summary>
        /// Overload resolution is a pure function of the candidate set and the runtime
        /// argument types (null counts as its own type), so each signature is scored once
        /// </summary>
        private sealed class OverloadSet
        {
            public readonly MethodInfo[] Candidates;
            public readonly System.Collections.Concurrent.ConcurrentDictionary<string, MethodInfo> Selected =
                new System.Collections.Concurrent.ConcurrentDictionary<string, MethodInfo>(StringComparer.Ordinal);

            public OverloadSet(MethodInfo[] candidates)
            {
                Candidates = candidates;
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, System.Collections.Concurrent.ConcurrentDictionary<string, OverloadSet>> _overloads =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Type, System.Collections.Concurrent.ConcurrentDictionary<string, OverloadSet>>();

        // Calls a method this many times through reflection before compiling a direct invoker,
        // so one-off calls do not pay the expression compilation cost
        private const int CompileThreshold = 16;

        private static MethodInfo ResolveMethod(Type type, string methodName, object[] arguments, BindingFlags flags)
        {
            var byName = _overloads.GetValue(type, _ =>
                new System.Collections.Concurrent.ConcurrentDictionary<string, OverloadSet>(StringComparer.OrdinalIgnoreCase));

            OverloadSet set = byName.GetOrAdd(((int)flags).ToString(CultureInfo.InvariantCulture) + "|" + methodName, _ =>
                new OverloadSet(type.GetMethods(flags).Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)).ToArray()));

            if (set.Candidates.Length == 0)
            {
                return null;
            }

            string signature = ArgumentSignature(arguments);

            if (set.Selected.TryGetValue(signature, out MethodInfo selected))
            {
                return selected;
            }

            selected = SelectOverload(set.Candidates, arguments);

            // Unresolvable signatures are not cached so the error path stays identical
            if (selected != null)
            {
                set.Selected[signature] = selected;
            }

            return selected;
        }

        private static string ArgumentSignature(object[] arguments)
        {
            if (arguments is null || arguments.Length == 0)
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder();

            foreach (object argument in arguments)
            {
                builder.Append(argument is null ? "<null>" : argument.GetType().AssemblyQualifiedName).Append('|');
            }

            return builder.ToString();
        }

        private static object InvokeMethod(MethodInfo method, MethodShape shape, object instance, object[] invocationArguments)
        {
            Func<object, object[], object> invoker = shape.CompiledInvoker;

            // Value-type receivers are excluded: a compiled call would mutate an unboxed copy
            if (invoker is null && !shape.HasByRef && !method.ContainsGenericParameters &&
                (method.IsStatic || !method.DeclaringType.IsValueType) &&
                System.Threading.Interlocked.Increment(ref shape.Invocations) == CompileThreshold)
            {
                invoker = shape.CompiledInvoker = CompileInvoker(method, shape);
            }

            // Anything reflection would reject or coerce (nulls for value types, mismatched
            // types, wrong arity) takes the reflection path so errors stay identical
            if (invoker is null || !ArgumentsExactlyAssignable(shape.Parameters, invocationArguments))
            {
                return method.Invoke(instance, invocationArguments);
            }

            try
            {
                return invoker(instance, invocationArguments);
            }
            catch (Exception exception)
            {
                // Keep the same exception shape callers see from MethodInfo.Invoke
                throw new TargetInvocationException(exception);
            }
        }

        private static bool ArgumentsExactlyAssignable(ParameterInfo[] parameters, object[] arguments)
        {
            if (arguments is null || arguments.Length != parameters.Length)
            {
                return false;
            }

            for (int i = 0; i < parameters.Length; i++)
            {
                Type parameterType = parameters[i].ParameterType;
                object argument = arguments[i];

                if (argument is null ? parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null
                                     : !parameterType.IsInstanceOfType(argument))
                {
                    return false;
                }
            }

            return true;
        }

        private static Func<object, object[], object> CompileInvoker(MethodInfo method, MethodShape shape)
        {
            try
            {
                var target = System.Linq.Expressions.Expression.Parameter(typeof(object), "target");
                var args = System.Linq.Expressions.Expression.Parameter(typeof(object[]), "args");

                var callArguments = shape.Parameters.Select((parameter, index) =>
                    (System.Linq.Expressions.Expression)System.Linq.Expressions.Expression.Convert(
                        System.Linq.Expressions.Expression.ArrayIndex(args, System.Linq.Expressions.Expression.Constant(index)),
                        parameter.ParameterType)).ToArray();

                System.Linq.Expressions.Expression call = method.IsStatic
                    ? System.Linq.Expressions.Expression.Call(method, callArguments)
                    : System.Linq.Expressions.Expression.Call(
                        System.Linq.Expressions.Expression.Convert(target, method.DeclaringType), method, callArguments);

                System.Linq.Expressions.Expression body = method.ReturnType == typeof(void)
                    ? (System.Linq.Expressions.Expression)System.Linq.Expressions.Expression.Block(call, System.Linq.Expressions.Expression.Constant(null))
                    : System.Linq.Expressions.Expression.Convert(call, typeof(object));

                return System.Linq.Expressions.Expression.Lambda<Func<object, object[], object>>(body, target, args).Compile();
            }
            catch
            {
                // Methods the expression compiler cannot bind keep using reflection
                return null;
            }
        }

        private static object Invoke(Type type, object instance, string methodName, object[] arguments, BindingFlags flags, out object[] fittedArguments)
        {
            MethodInfo method = ResolveMethod(type, methodName, arguments, flags);

            if (method is null)
            {
                throw new Exception($"Method {methodName} was not found on {type.FullName} for {arguments?.Length ?? 0} argument(s).");
            }

            MethodShape shape = Shape(method);
            ParameterInfo[] parameters = shape.Parameters;
            object[] invocationArguments = FitArguments(parameters, shape.HasParams, arguments);
            object result = InvokeMethod(method, shape, instance, invocationArguments);

            // The out array is indexed by ORIGINAL argument position (for byref
            // writeback); params-array-packed arguments keep their original value
            int argumentCount = arguments?.Length ?? 0;
            int fixedCount = shape.HasParams ? parameters.Length - 1 : parameters.Length;

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

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, System.Collections.Concurrent.ConcurrentDictionary<string, MemberInfo>> _members =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Type, System.Collections.Concurrent.ConcurrentDictionary<string, MemberInfo>>();

        private static T FindMember<T>(Type type, string name, BindingFlags flags, string kind, Func<T> scan) where T : MemberInfo
        {
            var members = _members.GetValue(type, _ =>
                new System.Collections.Concurrent.ConcurrentDictionary<string, MemberInfo>(StringComparer.OrdinalIgnoreCase));

            return (T)members.GetOrAdd(kind + "|" + ((int)flags).ToString(CultureInfo.InvariantCulture) + "|" + name, _ => scan());
        }

        private static PropertyInfo FindProperty(Type type, string name, BindingFlags flags)
        {
            return FindMember(type, name, flags, "P", () =>
                type.GetProperties(flags).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)));
        }

        private static FieldInfo FindField(Type type, string name, BindingFlags flags)
        {
            return FindMember(type, name, flags, "F", () =>
                type.GetFields(flags).FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)));
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
                MethodShape shape = Shape(candidate);
                ParameterInfo[] parameters = shape.Parameters;
                bool hasParams = shape.HasParams;

                int requiredCount = shape.RequiredCount;

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

        private static object[] FitArguments(ParameterInfo[] parameters, bool hasParams, object[] arguments)
        {
            object[] fitted = new object[parameters.Length];
            int argumentCount = arguments?.Length ?? 0;
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

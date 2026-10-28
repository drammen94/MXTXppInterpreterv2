using System;
using System.Collections.Concurrent;

namespace XppInterpreter.Interpreter.Proxy
{
    /// <summary>
    /// Process-wide memo for proxy calls that only read application metadata. Metadata
    /// cannot change without a deployment, which restarts the AOS, so every answer stays
    /// valid for the life of the process. Keys include the wrapped proxy's implementation
    /// type so different hosts never share answers, and lookups involving types created
    /// at runtime (script classes, emitted subclasses) are never cached.
    /// </summary>
    public static class MetadataCache
    {
        private static readonly ConcurrentDictionary<Key, object> Entries = new ConcurrentDictionary<Key, object>();

        /// <summary>
        /// Allows hosts to opt out, e.g. while metadata is being changed in a running process
        /// </summary>
        public static bool Enabled { get; set; } = true;

        public static int Count => Entries.Count;

        public static void Clear()
        {
            Entries.Clear();
        }

        internal static T Get<T>(object proxy, string member, object a, object b, object c, Func<T> compute)
        {
            if (!Enabled || !IsCacheable(a) || !IsCacheable(b) || !IsCacheable(c))
            {
                return compute();
            }

            Type proxyType = ImplementationType(proxy);

            if (proxyType is null)
            {
                return compute();
            }

            var key = new Key(proxyType, member, a, b, c);

            if (Entries.TryGetValue(key, out object cached))
            {
                return (T)cached;
            }

            // Exceptions propagate uncached, so failing lookups behave exactly as before
            T value = compute();

            if (IsCacheable(value))
            {
                Entries.TryAdd(key, value);
            }

            return value;
        }

        private static Type ImplementationType(object proxy)
        {
            // Remoting proxies intercept GetType(), so identify them by their RealProxy instead
            if (System.Runtime.Remoting.RemotingServices.IsTransparentProxy(proxy))
            {
                return System.Runtime.Remoting.RemotingServices.GetRealProxy(proxy)?.GetType();
            }

            return proxy.GetType();
        }

        private static bool IsCacheable(object value)
        {
            if (value is Type type)
            {
                // Only real, non-dynamic CLR types are stable keys; script class types are per compilation
                return type.GetType().FullName == "System.RuntimeType" && !type.Assembly.IsDynamic;
            }

            return true;
        }

        private struct Key : IEquatable<Key>
        {
            private readonly Type _proxyType;
            private readonly string _member;
            private readonly object _a, _b, _c;

            public Key(Type proxyType, string member, object a, object b, object c)
            {
                _proxyType = proxyType;
                _member = member;
                _a = a;
                _b = b;
                _c = c;
            }

            public bool Equals(Key other)
            {
                return _proxyType == other._proxyType &&
                    string.Equals(_member, other._member, StringComparison.Ordinal) &&
                    Equals(_a, other._a) && Equals(_b, other._b) && Equals(_c, other._c);
            }

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _proxyType.GetHashCode();
                    hash = hash * 31 + _member.GetHashCode();
                    hash = hash * 31 + (_a?.GetHashCode() ?? 0);
                    hash = hash * 31 + (_b?.GetHashCode() ?? 0);
                    hash = hash * 31 + (_c?.GetHashCode() ?? 0);
                    return hash;
                }
            }
        }
    }

    /// <summary>
    /// Reflection proxy whose metadata questions are answered from <see cref="MetadataCache"/>;
    /// everything that runs code or touches instances goes straight to the wrapped proxy
    /// </summary>
    internal sealed class MetadataCachingReflectionProxy : IXppReflectionProxy
    {
        private readonly IXppReflectionProxy _inner;

        public MetadataCachingReflectionProxy(IXppReflectionProxy inner)
        {
            _inner = inner;
        }

        public object CreateInstance(string className, object[] parameters) => _inner.CreateInstance(className, parameters);
        public object CallInstanceFunction(object instance, string functionName, object[] parameters) => _inner.CallInstanceFunction(instance, functionName, parameters);
        public object CallStaticFunction(string className, string functionName, object[] parameters) => _inner.CallStaticFunction(className, functionName, parameters);
        public object GetInstanceProperty(object instance, string propertyName) => _inner.GetInstanceProperty(instance, propertyName);
        public void SetInstanceProperty(object instance, string propertyName, object value) => _inner.SetInstanceProperty(instance, propertyName, value);
        public void SetStaticProperty(string className, string propertyName, object value) => _inner.SetStaticProperty(className, propertyName, value);
        public object GetStaticProperty(string className, string functionName) => _inner.GetStaticProperty(className, functionName);
        public object CallGlobalOrPredefinedFunction(RuntimeContext context, string functionName, object[] parameters) => _inner.CallGlobalOrPredefinedFunction(context, functionName, parameters);
        public bool IsCommon(object instance) => _inner.IsCommon(instance);
        public void ClearCommon(object common) => _inner.ClearCommon(common);

        public bool IsInstantiable(string name) => MetadataCache.Get(_inner, nameof(IsInstantiable), name, null, null, () => _inner.IsInstantiable(name));
        public bool IsEnum(string name) => MetadataCache.Get(_inner, nameof(IsEnum), name, null, null, () => _inner.IsEnum(name));
        public bool IsCommonType(Type type) => MetadataCache.Get(_inner, nameof(IsCommonType), type, null, null, () => _inner.IsCommonType(type));
        public object GetEnumValue(string enumType, string enumValue) => MetadataCache.Get(_inner, nameof(GetEnumValue), enumType, enumValue, null, () => _inner.GetEnumValue(enumType, enumValue));
        public string GetMethodSyntax(string typeName, string methodName) => MetadataCache.Get(_inner, nameof(GetMethodSyntax), typeName, methodName, null, () => _inner.GetMethodSyntax(typeName, methodName));
        public string LabelIdToValue(string labelId, string languageId) => MetadataCache.Get(_inner, nameof(LabelIdToValue), labelId, languageId, null, () => _inner.LabelIdToValue(labelId, languageId));
        public Type GetMethodReturnType(Type typeName, string methodName) => MetadataCache.Get(_inner, nameof(GetMethodReturnType), typeName, methodName, null, () => _inner.GetMethodReturnType(typeName, methodName));
        public Type GetFieldReturnType(Type caller, string fieldName, bool includeNonPublic = false) => MetadataCache.Get(_inner, nameof(GetFieldReturnType), caller, fieldName, includeNonPublic, () => _inner.GetFieldReturnType(caller, fieldName, includeNonPublic));
        public bool TypeHasMethod(Type type, string methodName) => MetadataCache.Get(_inner, nameof(TypeHasMethod), type, methodName, null, () => _inner.TypeHasMethod(type, methodName));
        public bool TypeHasProperty(Type type, string propertyName, bool includeNonPublic = false) => MetadataCache.Get(_inner, nameof(TypeHasProperty), type, propertyName, includeNonPublic, () => _inner.TypeHasProperty(type, propertyName, includeNonPublic));
        public bool EnumHasMember(string enumName, string memberName) => MetadataCache.Get(_inner, nameof(EnumHasMember), enumName, memberName, null, () => _inner.EnumHasMember(enumName, memberName));
        public string GetApplicationTypeKind(string name) => MetadataCache.Get(_inner, nameof(GetApplicationTypeKind), name, null, null, () => _inner.GetApplicationTypeKind(name));
        public Type GetDateType() => MetadataCache.Get(_inner, nameof(GetDateType), null, null, null, () => _inner.GetDateType());
        public Type GetUtcDateTimeType() => MetadataCache.Get(_inner, nameof(GetUtcDateTimeType), null, null, null, () => _inner.GetUtcDateTimeType());

        public string[] GetAllEnumValues(string enumType)
        {
            // Callers get their own copy so a cached array can never be modified in place
            string[] values = MetadataCache.Get(_inner, nameof(GetAllEnumValues), enumType, null, null, () => _inner.GetAllEnumValues(enumType));
            return (string[])values?.Clone();
        }
    }

    /// <summary>
    /// Casting proxy whose type-system questions are answered from <see cref="MetadataCache"/>;
    /// conversions and value construction always reach the wrapped proxy
    /// </summary>
    internal sealed class MetadataCachingCastingProxy : IXppCastingProxy
    {
        private readonly IXppCastingProxy _inner;

        public MetadataCachingCastingProxy(IXppCastingProxy inner)
        {
            _inner = inner;
        }

        public bool ToBoolean(object value) => _inner.ToBoolean(value);
        public object GetDefaultValueForType(string typeName) => _inner.GetDefaultValueForType(typeName);
        public object GetDefaultValueForSystemType(Type type) => _inner.GetDefaultValueForSystemType(type);
        public object CreateDynamicArray(string typeName) => _inner.CreateDynamicArray(typeName);
        public object CreateFixedArray(string typeName, int size) => _inner.CreateFixedArray(typeName, size);
        public object GetArrayIndexValue(object array, int index) => _inner.GetArrayIndexValue(array, index);
        public void SetArrayIndexValue(object array, int index, object value) => _inner.SetArrayIndexValue(array, index, value);
        public bool Is(object value, string typeName) => _inner.Is(value, typeName);
        public object As(object value, string typeName) => _inner.As(value, typeName);
        public object Cast(object value, Type toType) => _inner.Cast(value, toType);
        public object CreateDate(int day, int month, int year) => _inner.CreateDate(day, month, year);
        public string TruncateEDTString(string value, string typeName) => _inner.TruncateEDTString(value, typeName);

        public Type GetSystemTypeFromTypeName(string typeName) => MetadataCache.Get(_inner, nameof(GetSystemTypeFromTypeName), typeName, null, null, () => _inner.GetSystemTypeFromTypeName(typeName));
        public bool ImplicitConversionExists(Type from, Type to) => MetadataCache.Get(_inner, nameof(ImplicitConversionExists), from, to, null, () => _inner.ImplicitConversionExists(from, to));
        public bool IsReferenceType(Type type) => MetadataCache.Get(_inner, nameof(IsReferenceType), type, null, null, () => _inner.IsReferenceType(type));
        public Type GetArrayType(Type type) => MetadataCache.Get(_inner, nameof(GetArrayType), type, null, null, () => _inner.GetArrayType(type));
        public bool IsStringEDT(string typeName) => MetadataCache.Get(_inner, nameof(IsStringEDT), typeName, null, null, () => _inner.IsStringEDT(typeName));
    }
}

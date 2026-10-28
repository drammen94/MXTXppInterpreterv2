using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace XppInterpreter.Core
{
    public static class ReflectionHelper
    {
        public static void SetEdtArrayIndexerValue(object indexedObject, int index, object value)
        {
            indexedObject.GetType().GetProperty("Item").SetValue(indexedObject, value, new object[] { index, null });
        }

        public static object GetEdtArrayIndexerValue(object indexedObject, int index)
        {
            return indexedObject.GetType().GetProperty("Item").GetValue(indexedObject, new object[] { index, null });
        }

        public static object MakeGenericInstance(Type type, Type genericType)
        {
            var makeType = type.MakeGenericType(genericType);
            return Activator.CreateInstance(makeType);
        }

        public static object MakeGenericInstance(Type type, Type genericType, object arg1)
        {
            var makeType = type.MakeGenericType(genericType);
            return Activator.CreateInstance(makeType, arg1);
        }

        public static object MakeGenericInstance(Type type, Type genericType, object arg1, object arg2)
        {
            var makeType = type.MakeGenericType(genericType);
            return Activator.CreateInstance(makeType, arg1, arg2);
        }

        /// <summary>
        /// Case-insensitive member lookup tables, built once per type. Types are immutable at
        /// runtime, and a weak table lets emitted script types be collected with their assembly.
        /// The first member in reflection order wins, matching the previous FirstOrDefault scans.
        /// </summary>
        private sealed class MemberIndex
        {
            private readonly Type _type;
            private Dictionary<string, MethodInfo> _methods;
            private readonly Dictionary<string, PropertyInfo>[] _properties = new Dictionary<string, PropertyInfo>[2];
            private readonly Dictionary<string, FieldInfo>[] _fields = new Dictionary<string, FieldInfo>[2];

            public MemberIndex(Type type)
            {
                _type = type;
            }

            public MethodInfo Method(string name)
            {
                var methods = _methods ?? (_methods = Index(_type.GetMethods()));
                return name != null && methods.TryGetValue(name, out MethodInfo method) ? method : null;
            }

            public PropertyInfo Property(string name, bool includeNonPublic)
            {
                int slot = includeNonPublic ? 1 : 0;
                var properties = _properties[slot] ?? (_properties[slot] = Index(_type.GetProperties(GetBindingFlags(includeNonPublic))));
                return name != null && properties.TryGetValue(name, out PropertyInfo property) ? property : null;
            }

            public FieldInfo Field(string name, bool includeNonPublic)
            {
                int slot = includeNonPublic ? 1 : 0;
                var fields = _fields[slot] ?? (_fields[slot] = Index(_type.GetFields(GetBindingFlags(includeNonPublic))));
                return name != null && fields.TryGetValue(name, out FieldInfo field) ? field : null;
            }

            private static Dictionary<string, T> Index<T>(T[] members) where T : MemberInfo
            {
                var index = new Dictionary<string, T>(members.Length, StringComparer.OrdinalIgnoreCase);

                foreach (T member in members)
                {
                    if (member.Name != null && !index.ContainsKey(member.Name))
                    {
                        index.Add(member.Name, member);
                    }
                }

                return index;
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, MemberIndex> _memberIndexes =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Type, MemberIndex>();

        private static MemberIndex Members(Type type)
        {
            // Index construction is idempotent, so a lost race only builds a duplicate table
            return _memberIndexes.GetValue(type, t => new MemberIndex(t));
        }

        public static bool TypeHasMethod(Type type, string methodName)
        {
            if (type is null) return false;

            return Members(type).Method(methodName) != null;
        }

        public static bool TypeHasProperty(Type type, string propertyName, bool includeNonPublic = false)
        {
            if (type is null) return false;
            return Members(type).Property(propertyName, includeNonPublic) != null;
        }

        public static bool TypeHasField(Type type, string fieldName, bool includeNonPublic)
        {
            if (type is null) return false;
            return Members(type).Field(fieldName, includeNonPublic) != null;
        }

        public static string GetMethodInvariantName(Type type, string methodName)
        {
            return type is null ? null : Members(type).Method(methodName)?.Name;
        }

        public static PropertyInfo GetProperty(Type type, string propertyName, bool includeNonPublic = false)
        {
            return type is null ? null : Members(type).Property(propertyName, includeNonPublic);
        }

        public static FieldInfo GetField(Type type, string fieldName, bool includeNonPublic = false)
        {
            return type is null ? null : Members(type).Field(fieldName, includeNonPublic);
        }

        private static BindingFlags GetBindingFlags(bool includeNonPublic)
        {
            BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

            if (includeNonPublic)
            {
                bindingFlags |= BindingFlags.NonPublic;
            }

            return bindingFlags;
        }

        public static MethodInfo GetMethod(Type type, string methodName)
        {
            return type is null ? null : Members(type).Method(methodName);
        }

        public static bool TypeImplementsInterface(Type type, Type interfaceType)
        {
            if (type is null || interfaceType is null) return false;
            
            TypeInfo typeInfo = type as TypeInfo;
            return typeInfo != null && typeInfo.ImplementedInterfaces.Contains(interfaceType);
        }

        public static bool IsDelegateType(Type type)
        {
            if (type is null || type.BaseType is null) return false;

            return typeof(MulticastDelegate).IsAssignableFrom(type.BaseType);
        }
    }
}

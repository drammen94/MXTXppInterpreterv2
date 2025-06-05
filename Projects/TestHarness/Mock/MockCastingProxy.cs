using System;
using XppInterpreter.Interpreter.Proxy;

namespace TestHarness.Mock
{
    class MockCastingProxy : IXppCastingProxy
    {
        public bool ToBoolean(object value) => Convert.ToBoolean(value);
        public object GetDefaultValueForType(string typeName)
        {
            return typeName.ToLower() switch
            {
                "str" => string.Empty,
                "int" => 0,
                "int32" => 0,
                "int64" => 0L,
                "real" => 0.0,
                "boolean" => false,
                _ => null
            };
        }
        public object GetDefaultValueForSystemType(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
        public object CreateDynamicArray(string typeName) => new System.Collections.ArrayList();
        public object CreateFixedArray(string typeName, int size) => new object[size];
        public object GetArrayIndexValue(object array, int index) => ((System.Collections.IList)array)[index];
        public void SetArrayIndexValue(object array, int index, object value) => ((System.Collections.IList)array)[index] = value;
        public Type GetSystemTypeFromTypeName(string typeName)
        {
            return typeName.ToLower() switch
            {
                "str" => typeof(string),
                "int" => typeof(int),
                "int32" => typeof(int),
                "int64" => typeof(long),
                "real" => typeof(double),
                "boolean" => typeof(bool),
                "anytype" => typeof(object),
                _ => null
            };
        }
        public bool Is(object value, string typeName) => value?.GetType() == GetSystemTypeFromTypeName(typeName);
        public object As(object value, string typeName) => Is(value, typeName) ? value : null;
        public bool ImplicitConversionExists(Type from, Type to) => to.IsAssignableFrom(from);
        public bool IsReferenceType(Type type) => !type.IsValueType;
        public object Cast(object value, Type toType) => Convert.ChangeType(value, toType);
        public Type GetArrayType(Type type) => type.GetElementType();
        public object CreateDate(int day, int month, int year) => new DateTime(year, month, day);
    }
}

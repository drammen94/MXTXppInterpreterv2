using System.Collections.Generic;
using XppInterpreter.Interpreter;
using XppInterpreter.Interpreter.Bytecode;
using XppInterpreter.Interpreter.Proxy;

namespace TestHarness.Mock
{
    class MockReflectionProxy : IXppReflectionProxy
    {
        private readonly Dictionary<string, RefFunction> _functions;
        private XppProxy _proxy;

        public MockReflectionProxy(Dictionary<string, RefFunction> functions, XppProxy proxy)
        {
            _functions = functions;
            _proxy = proxy;
        }

        public void SetProxy(XppProxy proxy) => _proxy = proxy;

        public object CreateInstance(string className, object[] parameters)
        {
            return new MockInstance(className);
        }

        public object CallInstanceFunction(object instance, string functionName, object[] parameters)
        {
            if (instance is MockInstance mi)
            {
                var key = $"{mi.ClassName}.{functionName}";
                if (_functions.TryGetValue(key, out var func))
                {
                    return ExecuteFunction(func, parameters);
                }
            }
            return null;
        }

        public object CallStaticFunction(string className, string functionName, object[] parameters)
        {
            var key = $"{className}.{functionName}";
            if (_functions.TryGetValue(key, out var func))
            {
                return ExecuteFunction(func, parameters);
            }
            return null;
        }

        private object ExecuteFunction(RefFunction func, object[] parameters)
        {
            var byteCode = new ByteCode(func.Instructions) { DeclaredFunctions = new List<RefFunction>(_functions.Values) };
            var context = new RuntimeContext(_proxy, byteCode);
            for (int i = parameters.Length - 1; i >= 0; i--)
            {
                context.Stack.Push(parameters[i]);
            }
            var interpreter = new XppInterpreter.Interpreter.XppInterpreter(_proxy);
            var result = interpreter.Interpret(byteCode, context);
            return context.Stack.Count > 0 ? context.Stack.Pop() : null;
        }

        public object GetInstanceProperty(object instance, string propertyName) => null;
        public void SetInstanceProperty(object instance, string propertyName, object value) { }
        public object GetStaticProperty(string className, string functionName) => null;
        public bool IsInstantiable(string name) => true;
        public object CallGlobalOrPredefinedFunction(RuntimeContext context, string functionName, object[] parameters) => null;
        public bool IsEnum(string name) => false;
        public bool IsCommon(object instance) => false;
        public bool IsCommonType(System.Type type) => false;
        public void ClearCommon(object common) { }
        public object GetEnumValue(string enumType, string enumValue) => null;
        public string[] GetAllEnumValues(string enumType) => null;
        public string GetMethodSyntax(string typeName, string methodName) => null;
        public string LabelIdToValue(string labelId, string languageId) => null;
        public System.Type GetMethodReturnType(System.Type typeName, string methodName) => null;
        public System.Type GetFieldReturnType(System.Type caller, string fieldName) => null;
        public bool TypeHasMethod(System.Type type, string methodName) => false;
        public bool TypeHasProperty(System.Type type, string propertyName) => false;
        public bool EnumHasMember(string enumName, string memberName) => false;

        class MockInstance
        {
            public string ClassName { get; }
            public MockInstance(string className) { ClassName = className; }
        }
    }
}

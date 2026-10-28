using System.Linq;

namespace XppInterpreter.Interpreter.Proxy
{
    public static class XppProxyHelper
    {
        public static bool IsIntrinsicFunction(string name)
        {
            return typeof(IIntrinsicFunctionProvider).GetMethods()
                .Where(m => !m.GetCustomAttributes(typeof(IgnoreIntrinsicAttribute), true).Any()) 
                .Any(m => m.Name.ToLowerInvariant() == name.ToLowerInvariant());
        }

        public static object CallIntrinsicFunction(IIntrinsicFunctionProvider provider, string name, object[] parameters)
        {
            var method = typeof(IIntrinsicFunctionProvider).GetMethods()
                .FirstOrDefault(m => string.Equals(m.Name, name, System.StringComparison.OrdinalIgnoreCase));

            if (method is null)
            {
                throw new System.MissingMethodException(typeof(IIntrinsicFunctionProvider).FullName, name);
            }

            return method.Invoke(provider, parameters);
        }
    }
}

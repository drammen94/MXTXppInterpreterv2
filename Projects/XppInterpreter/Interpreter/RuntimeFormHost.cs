using System;
using System.Linq;
using System.Text;
using XppInterpreter.Interpreter.Bytecode;
using XppInterpreter.Parser;

namespace XppInterpreter.Interpreter
{
    /// <summary>
    /// Lists the interpreted classes of a module that the generic form host can open. A class is
    /// listed when it carries [RuntimeForm] or [RuntimeMenuItem]; the label is the first literal of
    /// [RuntimeMenuItem('Label')], or the class name.
    /// </summary>
    public static class RuntimeFormCatalog
    {
        /// <summary>One "ClassName\tLabel" line per form class of the bytecode.</summary>
        public static string Describe(ByteCode byteCode)
        {
            var text = new StringBuilder();

            if (byteCode?.DeclaredClasses is null)
            {
                return string.Empty;
            }

            foreach (RuntimeClass runtimeClass in byteCode.DeclaredClasses)
            {
                if (!IsForm(runtimeClass))
                {
                    continue;
                }

                text.Append(runtimeClass.Name).Append('\t').Append(Label(runtimeClass)).Append('\n');
            }

            return text.ToString();
        }

        internal static bool IsForm(RuntimeClass runtimeClass)
        {
            return runtimeClass.Attributes.Any(a => Is(a, "RuntimeForm") || Is(a, "RuntimeMenuItem"));
        }

        private static string Label(RuntimeClass runtimeClass)
        {
            XppAttributeDeclaration menuItem = runtimeClass.Attributes.FirstOrDefault(a => Is(a, "RuntimeMenuItem"));

            // Words[0] is the attribute name itself
            if (menuItem != null && menuItem.Words.Count > 1 && !string.IsNullOrWhiteSpace(menuItem.Words[1]))
            {
                return menuItem.Words[1].Replace('\t', ' ').Replace('\n', ' ');
            }

            return runtimeClass.Name;
        }

        private static bool Is(XppAttributeDeclaration attribute, string name)
        {
            return string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(attribute.Name, name + "Attribute", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The interpreted controller behind one open runtime form. The native host forwards form and
    /// control events to it by method name; every event runs with fresh execution limits, so a
    /// form that stays open for hours is not stopped by the limits of earlier events.
    /// </summary>
    public sealed class RuntimeFormInstance
    {
        private readonly RuntimeContext _context;
        private readonly RuntimeClass _class;
        private readonly XppInterpreterOptions _options;
        private readonly object _controller;

        public string ClassName => _class.Name;

        private RuntimeFormInstance(RuntimeContext context, RuntimeClass runtimeClass, XppInterpreterOptions options, object controller)
        {
            _context = context;
            _class = runtimeClass;
            _options = options;
            _controller = controller;
        }

        /// <summary>Creates the controller object of an interpreted form class.</summary>
        public static RuntimeFormInstance Create(ByteCode byteCode, Proxy.XppProxy proxy, XppInterpreterOptions options, string className)
        {
            if (byteCode is null) throw new ArgumentNullException(nameof(byteCode));

            RuntimeClass runtimeClass = byteCode.FindDeclaredClass(className)
                ?? throw new ArgumentException($"Interpreted class {className} was not found in the module.", nameof(className));

            var context = new RuntimeContext(proxy, byteCode)
            {
                ExecutionGuard = new RuntimeExecutionGuard(options)
            };

            object controller = ScriptClassRuntime.Instantiate(context, runtimeClass, new object[0]);
            return new RuntimeFormInstance(context, runtimeClass, options, controller);
        }

        /// <summary>True when the controller declares (or inherits) an instance method of this name.</summary>
        public bool Has(string methodName)
        {
            return methodName != null && _class.FindDispatchMethod(methodName, isStatic: false) != null;
        }

        public object Call(string methodName)
        {
            return Invoke(methodName, new object[0]);
        }

        public object Call1(string methodName, object argument)
        {
            return Invoke(methodName, new[] { argument });
        }

        public object Call2(string methodName, object first, object second)
        {
            return Invoke(methodName, new[] { first, second });
        }

        /// <summary>
        /// Calls a handler whose result decides an event (modified, validate). A handler that returns
        /// nothing, or something that is not a boolean, leaves the native result in place.
        /// </summary>
        public bool CallPredicate(string methodName, object argument, bool nativeResult)
        {
            return Invoke(methodName, new[] { argument }) is bool decided ? decided : nativeResult;
        }

        private object Invoke(string methodName, object[] arguments)
        {
            _context.ExecutionGuard = new RuntimeExecutionGuard(_options);
            return _context.Models.InvokeInstance(_controller, methodName, arguments);
        }
    }
}

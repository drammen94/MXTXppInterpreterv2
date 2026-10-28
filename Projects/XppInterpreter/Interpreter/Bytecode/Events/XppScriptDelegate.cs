using System.Collections.Generic;
using System.Linq;

namespace XppInterpreter.Interpreter.Bytecode.Events
{
    /// <summary>
    /// Runtime value of a delegate declared in a script class: an invocation list of
    /// local script functions and script class methods
    /// </summary>
    public class XppScriptDelegate
    {
        internal class Handler
        {
            public RefFunction Function;
            public ScriptObject Target;
            public RuntimeClassMethod Method;

            public bool Matches(Handler other)
            {
                return ReferenceEquals(Function, other.Function) &&
                       ReferenceEquals(Target, other.Target) &&
                       ReferenceEquals(Method, other.Method);
            }
        }

        private readonly List<Handler> _handlers = new List<Handler>();

        public void AddLocalFunction(RefFunction function)
        {
            _handlers.Add(new Handler { Function = function });
        }

        public void AddMethod(ScriptObject target, RuntimeClassMethod method)
        {
            _handlers.Add(new Handler { Target = target, Method = method });
        }

        public void RemoveLocalFunction(RefFunction function)
        {
            RemoveMatching(new Handler { Function = function });
        }

        public void RemoveMethod(ScriptObject target, RuntimeClassMethod method)
        {
            RemoveMatching(new Handler { Target = target, Method = method });
        }

        private void RemoveMatching(Handler pattern)
        {
            var existing = _handlers.LastOrDefault(h => h.Matches(pattern));

            if (existing != null)
            {
                _handlers.Remove(existing);
            }
        }

        /// <summary>
        /// Raises the delegate: every subscribed handler runs synchronously in order
        /// </summary>
        public void Invoke(RuntimeContext context, object[] arguments)
        {
            foreach (Handler handler in _handlers.ToList())
            {
                if (handler.Function != null)
                {
                    InvokeLocalFunction(context, handler.Function, arguments);
                }
                else
                {
                    ScriptClassRuntime.RunSynchronous(context, handler.Method, handler.Target, arguments);
                }
            }
        }

        private static void InvokeLocalFunction(RuntimeContext context, RefFunction function, object[] arguments)
        {
            var byteCode = new ByteCode(function.Instructions)
            {
                DeclaredFunctions = context.ByteCode.DeclaredFunctions,
                DeclaredClasses = context.ByteCode.DeclaredClasses,
            };

            var functionContext = new RuntimeContext(context.Proxy, byteCode);
            functionContext.InheritExecutionState(context);

            int argumentCount = arguments?.Length ?? 0;

            for (int numParam = 0; numParam < function.Declaration.Parameters.Count; numParam++)
            {
                var parameter = function.Declaration.Parameters[numParam];
                object value = numParam < argumentCount ? arguments[numParam] : null;

                functionContext.ScopeHandler.CurrentScope.SetVar(parameter.Name, value, context.Proxy.Casting, true, parameter.DeclarationClrType);
            }

            var interpreter = new XppInterpreter(context.Proxy, context.Interpreter?.Options);
            functionContext.Interpreter = interpreter;

            interpreter.Interpret(byteCode, functionContext, nextAction: global::XppInterpreter.Interpreter.Debug.DebugAction.StopDebugging);
        }
    }
}

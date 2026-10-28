using System;
using XppInterpreter.Lexer;

namespace XppInterpreter.Interpreter.Bytecode
{
    internal class DeclaredFunctionCall : IInstruction, ICall, IInterpretableInstruction
    {
        private RefFunction _ref;
        public string OperationCode => "CALL";
        public string Name => _ref.Declaration.Name;
        public int NArgs { get; set; }
        public bool Alloc => _ref.Declaration.Type.TokenType != TType.Void;
        public bool ProcessParameters => true;
        public InterpreterResult LastResult { get; private set; }
        public System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, string>> ByRefArguments { get; }

        public DeclaredFunctionCall(
            RefFunction refFunction,
            int nArgs,
            System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, string>> byRefArguments = null)
        {
            _ref = refFunction;
            NArgs = nArgs;
            ByRefArguments = byRefArguments;

            if (_ref.Declaration.Parameters.Count != nArgs)
            {
                throw new Exception($"Definied function '{Name}' parameter count missmatch.");
            }
        }

        public void Execute(RuntimeContext context)
        {
            // A stale paused result from a previous, aborted execution must not
            // survive into this one
            LastResult = InterpreterResult.Finished;
            LastResult = Interpret(context);
        }

        public InterpreterResult Interpret(RuntimeContext context)
        {
            RuntimeContext newContext = context.InnerContext;
            XppInterpreter interpreter = newContext?.Interpreter;
            bool firstTime = interpreter is null;

            if (newContext is null)
            {
                // Get parameters from stack
                object[] arguments = new object[NArgs];

                for (int narg = 0; narg < NArgs; narg++)
                {
                    if (context.Stack.Count > 0)
                        arguments[narg] = context.Stack.Pop();
                }

                // Create new runtime context for the function call
                ByteCode newByteCode = new ByteCode(_ref.Instructions)
                {
                    DeclaredFunctions = context.ByteCode.DeclaredFunctions,
                    DeclaredClasses = context.ByteCode.DeclaredClasses
                };

                newContext = new RuntimeContext(context.Proxy, newByteCode);
                newContext.InheritExecutionState(context);

                // Assign callee parameter to function definition parameter's scope
                for (int numParam = 0; numParam < NArgs; numParam++)
                {
                    var funcParameter = _ref.Declaration.Parameters[numParam];
                    System.Type declarationType = _ref.Declaration.Parameters[numParam].DeclarationClrType;

                    System.Diagnostics.Debug.Assert(declarationType != null); // We MUST have a declaration type

                    if (arguments[numParam] is null && context.Proxy.Reflection.IsCommonType(declarationType))
                    {
                        object defaultValue = context.Proxy.Casting.GetDefaultValueForType(funcParameter.DeclarationType.Lexeme);
                        newContext.ScopeHandler.CurrentScope.SetVar(funcParameter.Name, defaultValue, context.Proxy.Casting, true, declarationType);
                    }
                    else
                    {
                        newContext.ScopeHandler.CurrentScope.SetVar(funcParameter.Name, arguments[numParam], context.Proxy.Casting, true, declarationType);
                    }
                }

                interpreter = new XppInterpreter(context.Proxy, context.Interpreter.Options)
                {
                    Debugger = context.Interpreter.Debugger
                };

                newContext.Interpreter = interpreter;
                context.InnerContext = newContext;

            }

            InterpreterResult result;

            // Actual interpretation
            try
            {
                if (firstTime)
                {
                    result = interpreter.Interpret(newContext.ByteCode, newContext, nextAction: context.NextAction);
                }
                else
                {
                    result = interpreter.Continue(newContext.ByteCode, newContext, context.NextAction);
                }
            }
            catch
            {
                // An exception unwinding through this call ends it: a stale inner
                // context must not be resumed by a later re-execution of the caller
                context.InnerContext = null;
                throw;
            }

            // Check if interpreter modified return flag
            if (newContext.Returned && Alloc && context.InnerContext.Stack.Count > 0)
            {
                // Push the inner context last value into current context stack
                context.Stack.Push(context.InnerContext.Stack.Pop());
            }

            if (result?.HasFinished ?? true)
            {
                WriteBackByRefArguments(context, newContext);
            }

            return result;
        }

        private void WriteBackByRefArguments(RuntimeContext caller, RuntimeContext callee)
        {
            if (ByRefArguments is null) return;

            foreach (var byRefArgument in ByRefArguments)
            {
                if (byRefArgument.Key < _ref.Declaration.Parameters.Count)
                {
                    string parameterName = _ref.Declaration.Parameters[byRefArgument.Key].Name;
                    caller.ScopeHandler.CurrentScope.SetVar(
                        byRefArgument.Value,
                        callee.ScopeHandler.GetVar(parameterName),
                        caller.Proxy.Casting);
                }
            }
        }
    }
}

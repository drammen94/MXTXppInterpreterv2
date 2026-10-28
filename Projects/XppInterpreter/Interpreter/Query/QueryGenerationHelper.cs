using System;
using XppInterpreter.Interpreter.Bytecode;

namespace XppInterpreter.Interpreter.Query
{
    public class QueryGenerationHelper
    {
        private readonly RuntimeContext _context;
        public QueryGenerationHelper(RuntimeContext context)
        {
            _context = context;
        }

        private object Interpret(ByteCode byteCode)
        {
            XppInterpreter interpreter = new XppInterpreter(_context.Proxy);
            var expressionContext = new RuntimeContext(_context.Proxy, byteCode, _context.ScopeHandler)
            {
                ActiveClass = _context.ActiveClass,
                ExecutionGuard = _context.ExecutionGuard,
            };

            interpreter.Interpret(byteCode, expressionContext);

            return expressionContext.Stack.Pop();
        }

        public object GetDefaultValueForType(Type type)
        {
            return _context.Proxy.Casting.GetDefaultValueForSystemType(type);
        }

        public object ComputeVariable(Parser.Variable variable)
        {
            // Compile
            var generator = new Bytecode.ByteCodeGenerator();
            generator.VisitVariable(variable);
            ByteCode bytecode = generator.GetProgram();

            return Interpret(bytecode);
        }

        public object ComputeFunctionCall(Parser.FunctionCall functionCall)
        {
            // Compile
            var generator = new Bytecode.ByteCodeGenerator(declaredFunctions: _context.ByteCode.DeclaredFunctions);
            generator.VisitFunctionCall(functionCall);
            ByteCode bytecode = generator.GetProgram();

            return Interpret(bytecode);
        }

        public object ComputeConstant(Parser.Constant constant)
        {
            // Compile
            var generator = new Bytecode.ByteCodeGenerator();
            generator.VisitConstant(constant);
            ByteCode bytecode = generator.GetProgram();

            return Interpret(bytecode);
        }

        public object ComputeExpression(Parser.Expression expression)
        {
            var generator = new Bytecode.ByteCodeGenerator(
                declaredFunctions: _context.ByteCode.DeclaredFunctions,
                declaredClasses: _context.ByteCode.DeclaredClasses);

            expression.Accept(generator);
            return Interpret(generator.GetProgram());
        }

        /// <summary>
        /// True when an expression depends on a table field and therefore must be
        /// translated to SysDa instead of being evaluated once as a value.
        /// </summary>
        public bool ContainsTableReference(Parser.Expression expression)
        {
            if (expression is null)
            {
                return false;
            }

            if (expression is Parser.TableField)
            {
                return true;
            }

            if (expression is Parser.BinaryOperation binary)
            {
                return ContainsTableReference(binary.LeftOperand) || ContainsTableReference(binary.RightOperand);
            }

            if (expression is Parser.UnaryOperation unary)
            {
                return ContainsTableReference(unary.Expression);
            }

            if (expression is Parser.Ternary ternary)
            {
                return ContainsTableReference(ternary.Condition) ||
                       ContainsTableReference(ternary.Left) ||
                       ContainsTableReference(ternary.Right);
            }

            if (expression is Parser.ContainerInitialisation container)
            {
                foreach (Parser.Expression element in container.Elements)
                {
                    if (ContainsTableReference(element))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (expression is Parser.Is isExpression)
            {
                return ContainsTableReference(isExpression.Expression);
            }

            if (expression is Parser.As asExpression)
            {
                return ContainsTableReference(asExpression.Expression);
            }

            if (expression is Parser.Variable variable)
            {
                // A qualified member in a query might be a field on one of the
                // query buffers. Conservatively leave it to the SysDa translator.
                if (variable.Caller != null)
                {
                    return true;
                }

                if (variable is Parser.ArrayAccess array && ContainsTableReference(array.Index))
                {
                    return true;
                }

                if (variable is Parser.FunctionCall functionCall)
                {
                    foreach (Parser.Expression parameter in functionCall.Parameters)
                    {
                        if (ContainsTableReference(parameter))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}

using System.Collections.Generic;
using System.Diagnostics;
using XppInterpreter.Interpreter;
using XppInterpreter.Lexer;
using XppInterpreter.Parser.Metadata;

namespace XppInterpreter.Parser
{
    public class Constructor : FunctionCall
    {
        public string ClassName => (Token as Word).Lexeme;

        /// <summary>
        /// Set when the class name resolved to a .NET type that the X++ metadata
        /// proxies do not know, so instantiation must use direct CLR reflection
        /// </summary>
        public System.Type ResolvedClrType { get; set; }

        public Constructor(Word identifier, List<Expression> parameters, Expression caller, bool staticCall, SourceCodeBinding sourceCodeBinding, SourceCodeBinding debuggeableBinding) :
            base(identifier, parameters, caller, staticCall, false, sourceCodeBinding, debuggeableBinding)
        {
        }

        [DebuggerHidden]
        public override void Accept(IAstVisitor interpreter)
        {
            interpreter.VisitConstructor(this);
        }

        internal override System.Type Accept(ITypeInferExpressionVisitor inferer)
        {
            return inferer.VisitConstructor(this);
        }
    }
}

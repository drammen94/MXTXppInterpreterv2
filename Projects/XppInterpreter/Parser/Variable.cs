using System;
using System.Diagnostics;
using XppInterpreter.Interpreter;
using XppInterpreter.Lexer;
using XppInterpreter.Parser.Metadata;

namespace XppInterpreter.Parser
{
    public class Variable : Expression
    {
        public string Name => (Token as Word).Lexeme;
        public Expression Caller { get; }
        public bool StaticCall { get; }

        /// <summary>
        /// Set when this identifier is a reference to a .NET type unknown to the X++
        /// metadata proxies (used as the caller of :: static member access), so the
        /// access must be routed through direct CLR reflection
        /// </summary>
        public System.Type ClrTypeReference { get; set; }

        public Variable(Word identifier, Expression caller, bool staticCall, SourceCodeBinding sourceCodeBinding) : base(identifier, sourceCodeBinding)
        {
            Caller = caller;
            StaticCall = staticCall;
        }

        [DebuggerHidden]
        public override void Accept(IAstVisitor interpreter)
        {
            interpreter.VisitVariable(this);
        }

        internal override System.Type Accept(ITypeInferExpressionVisitor inferer)
        {
            return inferer.VisitVariable(this);
        }
    }
}

using System.Collections.Generic;
using System.Diagnostics;
using XppInterpreter.Interpreter;

namespace XppInterpreter.Parser
{
    public class Switch : Statement
    {
        public Expression Expression { get; }

        /// <summary>
        /// Case labels in declaration order (order matters for fall-through)
        /// </summary>
        public List<KeyValuePair<Expression, List<Statement>>> Cases { get; }

        public List<Statement> Default { get; }

        public Switch(
            Expression switchExpression,
            List<KeyValuePair<Expression, List<Statement>>> cases,
            List<Statement> defaultStatement,
            SourceCodeBinding sourceCodeBinding, SourceCodeBinding debuggeableCodeBinding) : base(sourceCodeBinding, debuggeableCodeBinding)
        {
            Expression = switchExpression;
            Cases = cases ?? new List<KeyValuePair<Expression, List<Statement>>>();
            Default = defaultStatement ?? new List<Statement>();
        }

        [DebuggerHidden]
        public override void Accept(IAstVisitor interpreter)
        {
            interpreter.VisitSwitch(this);
        }
    }
}

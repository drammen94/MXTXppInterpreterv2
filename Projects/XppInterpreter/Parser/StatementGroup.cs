using System.Collections.Generic;
using XppInterpreter.Interpreter;

namespace XppInterpreter.Parser
{
    /// <summary>
    /// A group of statements produced by a single source construct (e.g. a mixed
    /// declaration list like `int a[100], b = 1;`). Dispatches each contained
    /// statement to the visitor, so no visitor interface changes are needed.
    /// </summary>
    public class StatementGroup : Statement
    {
        public readonly List<Statement> Statements;

        public StatementGroup(List<Statement> statements, SourceCodeBinding sourceCodeBinding) : base(sourceCodeBinding, null)
        {
            Statements = statements;
        }

        public override void Accept(IAstVisitor interpreter)
        {
            foreach (var statement in Statements)
            {
                statement.Accept(interpreter);
            }
        }
    }
}

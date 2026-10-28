using XppInterpreter.Interpreter;

namespace XppInterpreter.Parser
{
    /// <summary>
    /// Represents a stray semicolon (empty statement). Generates no bytecode.
    /// </summary>
    public class EmptyStatement : Statement
    {
        public EmptyStatement(SourceCodeBinding sourceCodeBinding) : base(sourceCodeBinding, null)
        {
        }

        public override void Accept(IAstVisitor interpreter)
        {
        }
    }
}

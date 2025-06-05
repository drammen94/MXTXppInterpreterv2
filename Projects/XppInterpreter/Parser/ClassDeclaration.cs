using System.Collections.Generic;
using System.Diagnostics;
using XppInterpreter.Interpreter;

namespace XppInterpreter.Parser
{
    public class ClassDeclaration : Statement
    {
        public string Name { get; }
        public List<FunctionDeclaration> Methods { get; }

        public ClassDeclaration(string name, List<FunctionDeclaration> methods, SourceCodeBinding binding)
            : base(binding, null)
        {
            Name = name;
            Methods = methods;
        }

        [DebuggerHidden]
        public override void Accept(IAstVisitor interpreter)
        {
            interpreter.VisitClassDeclaration(this);
        }
    }
}

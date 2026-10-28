using System;
using System.Diagnostics;
using XppInterpreter.Interpreter;
using XppInterpreter.Lexer;

namespace XppInterpreter.Parser
{
    public class FunctionDeclarationParameter
    {
        public Word DeclarationType { get; }
        public System.Type DeclarationClrType { get; }
        public string Name { get; }
        public bool IsArray { get; }

        public FunctionDeclarationParameter(Word declarationType, System.Type clrType, string name, SourceCodeBinding sourceCodeBinding, bool isArray = false)
        {
            DeclarationType = declarationType;
            Name = name;
            DeclarationClrType = clrType;
            IsArray = isArray;
        }
    }
}

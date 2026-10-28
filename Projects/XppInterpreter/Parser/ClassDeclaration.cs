using System.Collections.Generic;
using System.Diagnostics;
using XppInterpreter.Interpreter;
using XppInterpreter.Lexer;
using XppInterpreter.Parser.Metadata;

namespace XppInterpreter.Parser
{
    public class XppAttributeDeclaration
    {
        public string Name { get; set; }
        public List<string> Words { get; } = new List<string>();
    }

    public class ClassMemberModifiers
    {
        public AccessModifier? Access;
        public bool IsStatic;
        public bool IsFinal;
        public bool IsAbstract;
        public bool IsConst;
    }

    public class ClassFieldDeclaration
    {
        public Statement Declaration;
        public ClassMemberModifiers Modifiers;
        public List<XppAttributeDeclaration> Attributes = new List<XppAttributeDeclaration>();
    }

    public class ClassMethodDeclaration
    {
        public string Name;
        public Word ReturnType;
        public List<FunctionDeclarationParameter> Parameters = new List<FunctionDeclarationParameter>();
        public List<Expression> ParameterDefaults = new List<Expression>();
        public Block Block;
        public ClassMemberModifiers Modifiers;
        public SourceCodeBinding SourceCodeBinding;
        public List<XppAttributeDeclaration> Attributes = new List<XppAttributeDeclaration>();

        public bool IsConstructor => string.Equals(Name, "new", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A class declared inside a script
    /// </summary>
    public class ClassDeclaration : Statement
    {
        public string Name { get; }
        public string BaseClassName { get; }
        public List<string> ImplementsNames { get; }
        public bool IsAbstract { get; }
        public bool IsFinal { get; }
        public bool IsStatic { get; }
        public string ExtensionTargetName { get; }

        public readonly List<ClassFieldDeclaration> Fields = new List<ClassFieldDeclaration>();
        public readonly List<ClassMethodDeclaration> Methods = new List<ClassMethodDeclaration>();
        public readonly List<ClassDelegateSignature> Delegates = new List<ClassDelegateSignature>();
        public readonly List<XppAttributeDeclaration> Attributes = new List<XppAttributeDeclaration>();

        /// <summary>
        /// Parse-time symbol information registered in the class declaration table
        /// </summary>
        public ClassDeclarationInfo Info { get; set; }

        public ClassDeclaration(
            string name,
            string baseClassName,
            List<string> implementsNames,
            bool isAbstract,
            bool isFinal,
            bool isStatic,
            string extensionTargetName,
            SourceCodeBinding sourceCodeBinding,
            SourceCodeBinding debuggeableBinding) : base(sourceCodeBinding, debuggeableBinding)
        {
            Name = name;
            BaseClassName = baseClassName;
            ImplementsNames = implementsNames ?? new List<string>();
            IsAbstract = isAbstract;
            IsFinal = isFinal;
            IsStatic = isStatic;
            ExtensionTargetName = extensionTargetName;
        }

        [DebuggerHidden]
        public override void Accept(IAstVisitor interpreter)
        {
            interpreter.VisitClassDeclaration(this);
        }
    }

    /// <summary>
    /// super(...) call inside a script class method. Parsed as a function call and
    /// specialized in the bytecode generator and type inferer.
    /// </summary>
    public class SuperCallExpression : FunctionCall
    {
        public ClassDeclarationInfo DeclaringClass { get; }
        public string MethodName { get; }

        public SuperCallExpression(
            ClassDeclarationInfo declaringClass,
            string methodName,
            List<Expression> parameters,
            SourceCodeBinding sourceCodeBinding,
            SourceCodeBinding debuggeableBinding)
            : base(new Word("super", TType.Id), parameters, null, false, false, sourceCodeBinding, debuggeableBinding)
        {
            DeclaringClass = declaringClass;
            MethodName = methodName;
        }
    }

    /// <summary>
    /// A Chain of Command call to the next wrapper or the original method.
    /// </summary>
    public class NextCallExpression : FunctionCall
    {
        public ClassDeclarationInfo ExtensionClass { get; }
        public ClassMethodSignature TargetMethod { get; }

        public NextCallExpression(
            ClassDeclarationInfo extensionClass,
            ClassMethodSignature targetMethod,
            Word identifier,
            List<Expression> parameters,
            SourceCodeBinding sourceCodeBinding)
            : base(identifier, parameters, null, false, false, sourceCodeBinding, null)
        {
            ExtensionClass = extensionClass;
            TargetMethod = targetMethod;
        }
    }
}

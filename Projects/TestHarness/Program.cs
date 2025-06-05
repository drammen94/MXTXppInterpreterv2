using System;
using System.Collections.Generic;
using TestHarness.Mock;
using XppInterpreter.Lexer;
using XppInterpreter.Parser;
using XppInterpreter.Interpreter;
using XppInterpreter.Interpreter.Bytecode;
using XppInterpreter.Interpreter.Proxy;

namespace TestHarness
{
    class Program
    {
        static void Main(string[] args)
        {
            string source = "class Hello { public static str greet() { return \"hi\"; } } return Hello::greet();";

            var (proxy, reflection, functions) = BuildProxy();
            var lexer = new XppLexer(source);
            var parser = new XppParser(lexer, proxy);
            var result = parser.Parse();
            var programAst = result.AST as XppInterpreter.Parser.Program;

            var generator = new ByteCodeGenerator();
            var byteCode = generator.Generate(programAst, false);

            foreach (var f in byteCode.DeclaredFunctions)
                functions[f.Declaration.Name] = f;

            reflection.SetProxy(proxy);

            var interpreter = new XppInterpreter.Interpreter.XppInterpreter(proxy);
            var context = new RuntimeContext(proxy, byteCode);
            interpreter.Interpret(byteCode, context);

            var resultValue = context.Stack.Count > 0 ? context.Stack.Pop() : null;
            Console.WriteLine($"Result: {resultValue}");
        }

        static (XppProxy, MockReflectionProxy, Dictionary<string, RefFunction>) BuildProxy()
        {
            var functions = new Dictionary<string, RefFunction>();
            var intrinsic = new MockIntrinsicFunctionProvider();
            var casting = new MockCastingProxy();
            var binary = new MockBinaryOperationProxy();
            var unary = new MockUnaryOperationProxy();
            var data = new MockDataAccessProxy();
            var reflection = new MockReflectionProxy(functions, null);
            var exceptions = new MockExceptionsProxy();
            var queryGen = new MockQueryGenerationProxy();

            var proxy = new XppProxy(intrinsic, binary, casting, unary, data, reflection, exceptions, queryGen);
            return (proxy, reflection, functions);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using XppInterpreter.Interpreter;
using XppInterpreter.Interpreter.Bytecode;
using XppInterpreter.Interpreter.Query;
using XppInterpreter.Lexer;
using XppInterpreter.Parser;

internal sealed class AosMetadataFixture
{
    public int Optional(int required, int optional = 2) => required + optional;
    public int ArrayInput(object[] values) => values?.Length ?? 0;
}

namespace XppInterpreter.Tests
{
    internal static class Program
    {
        private static readonly List<string> Failures = new List<string>();

        private static int Main()
        {
            // Every loop that qualifies is compiled after two iterations, so the whole suite also runs through the loop JIT
            LoopJit.CompileSynchronously = true;
            LoopJit.Threshold = 2;

            Run("div uses integer division", TestIntegerDivision);
            Run("final method cannot be overridden", () => ExpectParseError(@"
                class Base { final int value() { return 1; } }
                class Child extends Base { int value() { return 2; } }
            ", "final method"));
            Run("override signature must match", () => ExpectParseError(@"
                class Base { int value(int input) { return input; } }
                class Child extends Base { int value(str input) { return 2; } }
            ", "same parameter and return types"));
            Run("concrete class implements abstract methods", () => ExpectParseError(@"
                abstract class Base { abstract int value(); }
                class Child extends Base { }
            ", "does not implement abstract method"));
            Run("interface implementation signature is validated", () => ExpectParseError(@"
                interface IValue { int value(int input); }
                class Value implements IValue { private int value(str input) { return 1; } }
            ", "does not correctly implement"));
            Run("private method is inaccessible outside its class", () => ExpectParseError(@"
                class Secret { private int value() { return 1; } }
                Secret secret = new Secret();
                int result = secret.value();
            ", "not accessible"));
            Run("protected method is accessible from a subclass", () => ExpectParses(@"
                class Base { protected int value() { return 1; } }
                class Child extends Base { int read() { return this.value(); } }
            "));
            Run("private base method is inaccessible from a subclass", () => ExpectParseError(@"
                class Base { private int value() { return 1; } }
                class Child extends Base { int read() { return this.value(); } }
            ", "not accessible"));
            Run("instance method cannot be called statically", () => ExpectParseError(@"
                class Value { int read() { return 1; } }
                int result = Value::read();
            ", "on an instance"));
            Run("override cannot reduce access", () => ExpectParseError(@"
                class Base { public int value() { return 1; } }
                class Child extends Base { protected int value() { return 2; } }
            ", "less accessible"));
            Run("same-named private base method is not an override", () => ExpectParses(@"
                class Base { private int value(int input) { return input; } }
                class Child extends Base { private str value(str input) { return input; } }
            "));
            Run("private constructor is inaccessible externally", () => ExpectParseError(@"
                class Secret { private void new() { } }
                Secret value = new Secret();
            ", "Constructor Secret.new is not accessible"));
            Run("private constructor is accessible to its factory", () => ExpectParses(@"
                class Secret {
                    private void new() { }
                    public static Secret construct() { return new Secret(); }
                }
                Secret value = Secret::construct();
            "));
            Run("abstract class cannot be instantiated", () => ExpectParseError(@"
                abstract class Value { }
                Value value = new Value();
            ", "Cannot instantiate abstract class"));
            Run("constructor argument count is validated", () => ExpectParseError(@"
                class Value { void new(int input) { } }
                Value value = new Value();
            ", "takes 1 to 1 argument"));
            Run("method required argument count is validated", () => ExpectParseError(@"
                class Value { int read(int input, int fallback = 2) { return input; } }
                Value value = new Value();
                int result = value.read();
            ", "takes 1 to 2 argument"));
            Run("required parameters precede optional parameters", () => ExpectParseError(@"
                class Value { int read(int optional = 1, int required) { return required; } }
            ", "Required parameters must be declared before optional"));
            Run("typed assignment casts to the declared type", TestTypedAssignment);
            Run("public field is accessible and executable", TestPublicField);
            Run("private field is inaccessible externally", () => ExpectParseError(@"
                class Secret { private int value; }
                Secret secret = new Secret();
                int result = secret.value;
            ", "Field Secret.value is not accessible"));
            Run("protected field is accessible from a subclass", () => ExpectParses(@"
                class Base { protected int value; }
                class Child extends Base { int read() { return value; } }
            "));
            Run("const instance field cannot be assigned", () => ExpectParseError(@"
                class Value { public const int answer = 42; }
                Value value = new Value();
                value.answer = 1;
            ", "Cannot assign to const"));
            Run("TypeNew runs before first use", TestStaticConstructor);
            Run("TypeNew cannot be called explicitly", () => ExpectParseError(@"
                class Value { static void TypeNew() { } }
                Value::TypeNew();
            ", "cannot be called explicitly"));
            Run("TypeNew signature is validated", () => ExpectParseError(@"
                class Value { void TypeNew(int input) { } }
            ", "must be declared as static void TypeNew()"));
            Run("runtime metadata preserves final and fields", TestRuntimeMetadata);
            Run("runtime metadata preserves type, field, method, and delegate attributes", TestAttributeMetadata);
            Run("class and tier modifiers parse", () => ExpectParses(@"
                [SysObsolete('legacy', false)]
                public final class Tiered {
                    client display int shown() { return 1; }
                    server edit int edited(boolean set, int value) { return value; }
                }
            "));
            Run("modern metadata intrinsic functions parse", () => ExpectParses(@"
                str entityName = dataEntityStr(MyEntity);
                str entityViewName = dataEntityViewStr(MyEntity);
                str viewName = viewStr(MyView);
                str mapName = mapStr(MyMap);
                str serviceName = serviceStr(MyService);
                str operationName = serviceOperationStr(MyService, MyOperation);
            "));
            Run("timeOfDay uses the integer runtime representation", TestTimeOfDay);
            Run("static class executes static methods", TestStaticClass);
            Run("static class cannot be instantiated", () => ExpectParseError(@"
                public static class Utility { public static int value() { return 1; } }
                Utility utility = new Utility();
            ", "Cannot instantiate static class"));
            Run("static class rejects instance members", () => ExpectParseError(@"
                static class Utility { int value() { return 1; } }
            ", "must be static because Utility is a static class"));
            Run("Chain of Command wraps an instance method", TestChainOfCommand);
            Run("Chain of Command extension state initializes once", TestChainOfCommandState);
            Run("Chain of Command wraps static methods", TestStaticChainOfCommand);
            Run("multiple Chain of Command wrappers execute", TestMultipleChainOfCommandWrappers);
            Run("Chain of Command wraps a compiled AOS target", TestCompiledTargetChainOfCommand);
            Run("compiled AOS target augmentation executes", TestCompiledTargetAugmentation);
            Run("Chain of Command wraps a compiled static AOS method", TestCompiledStaticChainOfCommand);
            Run("script subclasses have real compiled base identity", TestCompiledBaseIdentity);
            Run("script classes have real compiled interface identity", TestCompiledInterfaceIdentity);
            Run("compiled interface contracts are validated", () => ExpectParseError(@"
                class BadCallback implements IAosCallback {
                    public int Compute(int input) { return input; }
                }
            ", "does not correctly implement IAosCallback.Mutate"));
            Run("SubscribesTo attribute binds a compiled event", TestAttributedEventHandler);
            Run("AOS data event attributes bind through the runtime manager", TestAosDataEventAttribute);
            Run("pre and post handlers wrap native calls", TestPreAndPostHandlers);
            Run("post handlers can replace native returns with null", TestPostHandlerNullReturn);
            Run("Chain of Command requires a first-level next call", () => ExpectParseError(@"
                class Target { public int value() { return 1; } }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension { public int value() { if (true) { return next value(); } return 0; } }
            ", "first statement level"));
            Run("Chain of Command permits next in try/finally", TestChainOfCommandTryFinally);
            Run("Chain of Command rejects next in logical expressions", () => ExpectParseError(@"
                class Target { public boolean value() { return true; } }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension { public boolean value() { return true && next value(); } }
            ", "logical or conditional expression"));
            Run("Chain of Command counts multiple next calls in one statement", () => ExpectParseError(@"
                class Target { public int value() { return 1; } }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension { public int value() { return next value() + next value(); } }
            ", "exactly once"));
            Run("Chain of Command wrapper omits target default values", TestChainOfCommandDefaultParameter);
            Run("Chain of Command rejects wrapper default values", () => ExpectParseError(@"
                class Target { public int value(int input = 2) { return input; } }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension { public int value(int input = 2) { return next value(input); } }
            ", "must omit default parameter values"));
            Run("Chain of Command requires next unless replaceable", () => ExpectParseError(@"
                class Target { public int value() { return 1; } }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension { public int value() { return 2; } }
            ", "must call next"));
            Run("Replaceable methods may skip next", TestReplaceableChainOfCommand);
            Run("Wrappable false blocks Chain of Command", () => ExpectParseError(@"
                class Target { [Wrappable(false)] public int value() { return 1; } }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension { public int value() { return next value(); } }
            ", "must match a wrappable method"));
            Run("Hookable false blocks Chain of Command", () => ExpectParseError(@"
                class Target { [Hookable(false)] public int value() { return 1; } }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension { public int value() { return next value(); } }
            ", "must match a wrappable method"));
            Run("Wrappable true permits final method wrapping", TestWrappableFinalMethod);
            Run("class augmentation adds instance methods and state", TestAugmentationInstanceMembers);
            Run("class augmentation adds static methods and state", TestAugmentationStaticMembers);
            Run("extension instance methods see extension static state", TestAugmentationInstanceStaticState);
            Run("Chain of Command next must call current method", () => ExpectParseError(@"
                class Target { public int value(int input) { return input; } }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension {
                    public int value(int input) { return next other(input); }
                }
            ", "next must call the current wrapper method"));
            Run("extension class must be final", () => ExpectParseError(@"
                class Target { public int value() { return 1; } }
                [ExtensionOf(classStr(Target))]
                class Target_Extension { public int value() { return next value(); } }
            ", "must be final"));
            Run("query parser handles crossCompany container", TestCrossCompanyQuery);
            Run("query parser distinguishes index selection from index hint", TestQueryIndexModes);
            Run("query parser uses all fields when projection is omitted", TestSelectAllProjection);
            Run("query group/order by accepts optional by and direction", () => ExpectParses(@"
                anytype buffer;
                select buffer group FieldA asc order by FieldB desc;
            "));
            Run("query force hints are mutually exclusive", () => ExpectParseError(@"
                anytype buffer;
                select forceLiterals forcePlaceHolders buffer;
            ", "mutually exclusive"));
            Run("query firstOnly hints are mutually exclusive", () => ExpectParseError(@"
                anytype buffer;
                select firstOnly firstOnly10 buffer;
            ", "Only one firstOnly"));
            Run("query helper computes containers and ternaries", TestQueryExpressionEvaluation);
            Run("query helper detects table-dependent expressions", TestQueryTableReferenceDetection);
            Run("tts statements call the native data proxy", TestTtsStatements);
            Run("global transaction isolation begins and aborts", TestGlobalTtsAbort);
            Run("obsolete pause statement is rejected", () => ExpectParseError("pause;", "Invalid syntax"));
            Run("CLR namespaces, construction, methods, and properties execute", TestClrInstanceInterop);
            Run("CLR overload selection preserves real values", TestClrOverloadSelection);
            Run("CLR optional and params arguments execute", TestClrOptionalAndParamsArguments);
            Run("CLR byref arguments are written back", TestClrByRefWriteBack);
            Run("script method byref arguments are written back", TestScriptMethodByRefWriteBack);
            Run("declared function byref arguments are written back", TestDeclaredFunctionByRefWriteBack);
            Run("script methods accept array parameters", TestScriptArrayParameter);
            Run("runtime model registry exposes script and AOS descriptors", TestRuntimeModelRegistry);
            Run("runtime model registry creates and invokes script models", TestExecutableRuntimeModelRegistry);
            Run("runtime table buffers support row CRUD and select", TestRuntimeTableCrudAndSelect);
            Run("runtime tables support set-based queries, joins, aggregates, and tts rollback", TestRuntimeTableSetBasedQueries);
            Run("runtime tables support select expressions", TestRuntimeTableSelectExpression);
            Run("runtime joined DML and exception rollback preserve relational semantics", TestRuntimeTableJoinedDmlAndExceptionRollback);
            Run("runtime artifacts project forms, entities, views, reports, services, and maps", TestRuntimeArtifactProjection);
            Run("runtime metadata intrinsics validate and fold interpreted artifacts", TestRuntimeArtifactIntrinsics);
            Run("runtime metadata intrinsics reject missing members", () => ExpectParseError(@"
                [RuntimeTable] class RuntimeCustomer { public str Account; }
                str missing = fieldStr(RuntimeCustomer, MissingField);
            ", "does not contain field MissingField"));
            Run("AOS metadata signatures retain optional and array parameters", TestAosMetadataSignatures);
            Run("CLR aliases and static members execute", TestClrAliasAndStaticMember);
            Run("macros expand definitions, conditionals, and parameters", TestMacroExpansion);
            Run("macro libraries resolve through the host hook", TestMacroLibraryResolution);
            Run("macro libraries resolve from PackagesLocalDirectory metadata", TestFileSystemMacroLibraryResolution);
            Run("recursive macro expansion is rejected", TestRecursiveMacroExpansion);
            Run("instruction budget stops execution", () => ExpectRuntimeError(@"
                int value = 1;
                value += 2;
                value += 3;
            ", new XppInterpreterOptions { MaxInstructions = 4 }, "instruction limit"));
            Run("instruction budget is shared by nested method calls", () => ExpectRuntimeError(@"
                class Recursive {
                    int run(int value) {
                        if (value == 0) { return 0; }
                        return this.run(value - 1);
                    }
                }
                Recursive recursive = new Recursive();
                int result = recursive.run(100);
            ", new XppInterpreterOptions { MaxInstructions = 40 }, "instruction limit"));
            Run("execution time budget stops long-running calls", () => ExpectRuntimeError(@"
                using System.Threading;
                Thread::Sleep(30);
                int value = 1;
            ", new XppInterpreterOptions { MaxExecutionMilliseconds = 5 }, "time limit"));
            Run("loop iteration budget stops execution", () => ExpectRuntimeError(@"
                int value = 0;
                while (value < 10) { value++; }
            ", new XppInterpreterOptions { MaxLoopIterations = 3 }, "Max loop counter"));
            Run("execution limits cannot be caught by script code", () => ExpectRuntimeError(@"
                try {
                    while (true) { }
                }
                catch {
                    capture(99);
                }
            ", new XppInterpreterOptions { MaxLoopIterations = 2 }, "Max loop counter"));
            Run("integer fast path matches proxy arithmetic", TestIntegerFastPath);
            Run("int overflow is left to the native runtime", () => ExpectRuntimeError(@"
                int value = 2147483647;
                value = value + 1;
            ", null, "OverflowException"));
            Run("int64 overflow is left to the native runtime", () => ExpectRuntimeError(@"
                int64 value = 9223372036854775807;
                value = value * 2;
            ", null, "OverflowException"));
            Run("hot CLR overloads keep per-signature selection", TestHotClrOverloads);
            Run("hot CLR calls surface exceptions like reflection", TestHotClrExceptions);
            Run("hot CLR calls fall back for null value-type arguments", TestHotClrNullArgument);
            Run("polymorphic CLR call site dispatches per receiver type", TestPolymorphicCallSite);
            Run("single-statement bodies work without braces", TestBracelessBodies);
            Run("compiled loops match the interpreter", TestCompiledLoopsMatchInterpreter);
            Run("compiled loops leave int overflow to the native runtime", () => ExpectRuntimeError(@"
                int value = 2147483000;
                int i;
                for (i = 0; i < 1000; i++) { value += 1; }
            ", null, "OverflowException"));
            Run("compiled loops still enforce the instruction limit", () => ExpectRuntimeError(@"
                int i = 0;
                while (true) { i += 1; }
            ", new XppInterpreterOptions { MaxInstructions = 100000 }, "instruction limit"));
            Run("compiled loops write variables back before an exception is caught", TestCompiledLoopWriteBack);

            if (Failures.Count == 0)
            {
                Console.WriteLine("All {0} conformance tests passed.", TestCount);
                return 0;
            }

            Console.Error.WriteLine("{0} of {1} tests failed:", Failures.Count, TestCount);
            foreach (string failure in Failures)
            {
                Console.Error.WriteLine("- " + failure);
            }

            return 1;
        }

        private static int TestCount;

        private static void Run(string name, Action test)
        {
            TestCount++;
            try
            {
                test();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                Failures.Add(name + ": " + exception.Message);
                Console.WriteLine("FAIL " + name);
            }
        }

        private static void TestIntegerDivision()
        {
            Execute("capture(5 div 2);");
            Equal(2, Convert.ToInt32(TestProxyFactory.LastCaptured), "5 div 2");
        }

        private static void TestTypedAssignment()
        {
            Execute(@"
                int value = 0;
                value = 5.9;
                capture(value);
            ");
            Equal(6, Convert.ToInt32(TestProxyFactory.LastCaptured), "assigned int value");
        }

        private static void TestPublicField()
        {
            Execute(@"
                class Value { public int answer = 42; }
                Value value = new Value();
                capture(value.answer);
            ");
            Equal(42, Convert.ToInt32(TestProxyFactory.LastCaptured), "public field value");
        }

        private static void TestStaticConstructor()
        {
            Execute(@"
                class Counter {
                    private static int value;
                    static void TypeNew() { value += 1; }
                    public static int get() { return value; }
                }
                int first = Counter::get();
                int second = Counter::get();
                capture(first * 10 + second);
            ");
            Equal(11, Convert.ToInt32(TestProxyFactory.LastCaptured), "TypeNew execution count");
        }

        private static void TestRuntimeMetadata()
        {
            ByteCode byteCode = Compile(@"
                class Value {
                    private int state;
                    final int read() { return state; }
                }
            ");

            RuntimeClass runtimeClass = byteCode.FindDeclaredClass("Value");
            if (runtimeClass is null) throw new Exception("Runtime class Value was not emitted.");

            Equal(true, runtimeClass.FindMethod("read").IsFinal, "final metadata");
            Equal(AccessModifier.Private, runtimeClass.FindField("state").Access, "field access metadata");
        }

        private static void TestAttributeMetadata()
        {
            ByteCode byteCode = Compile(@"
                [TypeMarker('type')]
                class AttributedValue {
                    [FieldMarker('field')]
                    public int state;
                    [MethodMarker('method')]
                    public int read() { return state; }
                    [DelegateMarker('delegate')]
                    delegate void changed(int value);
                }
            ");

            RuntimeClass runtimeClass = byteCode.FindDeclaredClass("AttributedValue");
            Equal("TypeMarker", runtimeClass.Attributes.Single().Name, "type attribute");
            Equal("FieldMarker", runtimeClass.FindField("state").Attributes.Single().Name, "field attribute");
            Equal("MethodMarker", runtimeClass.FindMethod("read").Attributes.Single().Name, "method attribute");
            Equal("DelegateMarker", runtimeClass.DelegateDeclarations.Single().Attributes.Single().Name, "delegate attribute");
        }

        private static void TestStaticClass()
        {
            Execute(@"
                public static class Utility {
                    public static int value() { return 7; }
                }
                capture(Utility::value());
            ");
            Equal(7, Convert.ToInt32(TestProxyFactory.LastCaptured), "static class result");
        }

        private static void TestTimeOfDay()
        {
            Execute(@"
                timeOfDay value = 3600;
                value += 30;
                capture(value);
            ");
            Equal(3630, Convert.ToInt32(TestProxyFactory.LastCaptured), "timeOfDay value");
        }

        private static void TestChainOfCommand()
        {
            Execute(@"
                public class Calculator {
                    public int value(int input) { return input + 1; }
                }
                [ExtensionOf(classStr(Calculator))]
                public final class Calculator_Extension {
                    public int value(int input) { return next value(input) * 2; }
                }
                Calculator calculator = new Calculator();
                capture(calculator.value(3));
            ");
            Equal(8, Convert.ToInt32(TestProxyFactory.LastCaptured), "wrapped method result");
        }

        private static void TestChainOfCommandState()
        {
            Execute(@"
                class Counter {
                    public int value() { return 10; }
                }
                [ExtensionOf(classStr(Counter))]
                final class Counter_Extension {
                    private int calls = 0;
                    public int value() {
                        calls += 1;
                        return next value() + calls;
                    }
                }
                Counter counter = new Counter();
                int first = counter.value();
                int second = counter.value();
                capture(first * 100 + second);
            ");
            Equal(1112, Convert.ToInt32(TestProxyFactory.LastCaptured), "extension state result");
        }

        private static void TestChainOfCommandTryFinally()
        {
            Execute(@"
                class Target { public int value() { return 3; } }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension {
                    public int value() {
                        int result;
                        try { result = next value(); }
                        finally { result += 1; }
                        return result;
                    }
                }
                Target target = new Target();
                capture(target.value());
            ");
            Equal(4, Convert.ToInt32(TestProxyFactory.LastCaptured), "try/finally wrapper result");
        }

        private static void TestChainOfCommandDefaultParameter()
        {
            Execute(@"
                class Target { public int value(int input = 2) { return input; } }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension {
                    public int value(int input) { return next value(input) + 1; }
                }
                Target target = new Target();
                capture(target.value());
            ");
            Equal(3, Convert.ToInt32(TestProxyFactory.LastCaptured), "default parameter wrapper result");
        }

        private static void TestStaticChainOfCommand()
        {
            Execute(@"
                class Calculator { public static int value(int input) { return input + 1; } }
                [ExtensionOf(classStr(Calculator))]
                final class Calculator_Extension {
                    public static int value(int input) { return next value(input) * 2; }
                }
                capture(Calculator::value(3));
            ");
            Equal(8, Convert.ToInt32(TestProxyFactory.LastCaptured), "static wrapper result");
        }

        private static void TestMultipleChainOfCommandWrappers()
        {
            Execute(@"
                class Calculator { public int value(int input) { return input; } }
                [ExtensionOf(classStr(Calculator))]
                final class First_Extension {
                    public int value(int input) { return next value(input) + 1; }
                }
                [ExtensionOf(classStr(Calculator))]
                final class Second_Extension {
                    public int value(int input) { return next value(input) * 2; }
                }
                Calculator calculator = new Calculator();
                capture(calculator.value(3));
            ");
            Equal(7, Convert.ToInt32(TestProxyFactory.LastCaptured), "multiple wrapper result");
        }

        private static void TestCompiledTargetChainOfCommand()
        {
            Execute(@"
                [ExtensionOf(classStr(AosCompiledTarget))]
                final class FirstAosTarget_Extension {
                    private int calls;
                    public int value(int input) { calls += 1; return next value(input) + calls; }
                }
                [ExtensionOf(classStr(AosCompiledTarget))]
                final class SecondAosTarget_Extension {
                    public int value(int input) { return next value(input) * 2; }
                }
                AosCompiledTarget target = new AosCompiledTarget();
                int first = target.value(3);
                int second = target.value(3);
                capture(first * 100 + second);
            ");
            Equal(910, Convert.ToInt32(TestProxyFactory.LastCaptured), "compiled target wrapper result");
        }

        private static void TestCompiledTargetAugmentation()
        {
            Execute(@"
                [ExtensionOf(classStr(AosCompiledTarget))]
                final class AosTargetAugmentation_Extension {
                    public int doubled(int input) { return this.value(input) * 2; }
                }
                AosCompiledTarget target = new AosCompiledTarget();
                capture(target.doubled(4));
            ");
            Equal(10, Convert.ToInt32(TestProxyFactory.LastCaptured), "compiled target augmentation result");
        }

        private static void TestCompiledStaticChainOfCommand()
        {
            Execute(@"
                [ExtensionOf(classStr(AosCompiledTarget))]
                final class StaticAosTarget_Extension {
                    public static int staticValue(int input) { return next staticValue(input) * 2; }
                }
                capture(AosCompiledTarget::staticValue(4));
            ");
            Equal(10, Convert.ToInt32(TestProxyFactory.LastCaptured), "compiled static wrapper result");
        }

        private static void TestAttributedEventHandler()
        {
            Execute(@"
                final class InterpretedEventHandlers {
                    [SubscribesTo(classStr(AosEventSource), delegateStr(AosEventSource, Changed))]
                    public static void onChanged(int value) { capture(value + 1); }
                }
                AosEventSource::Raise(41);
            ");
            Equal(42, Convert.ToInt32(TestProxyFactory.LastCaptured), "attributed event result");
        }

        private static void TestAosDataEventAttribute()
        {
            Execute(@"
                final class InterpretedDataEventHandlers {
                    [DataEventHandler(tableStr(DemoTable), DataEventType::Inserted), SysObsolete('test', false)]
                    public static void onInserted(anytype sender, anytype args) { capture(73); }
                }
            ");

            Microsoft.Dynamics.Ax.Xpp.DataEventManager.Raise(
                "DemoTable",
                Dynamics.AX.Application.DataEventType.Inserted);
            Equal(73, Convert.ToInt32(TestProxyFactory.LastCaptured), "data event handler result");
        }

        private static void TestPreAndPostHandlers()
        {
            Execute(@"
                final class InterpretedPrePostHandlers {
                    [PreHandlerFor(classStr(AosCompiledTarget), methodStr(AosCompiledTarget, value))]
                    public static void before(XppPrePostArgs args) { args.setArg('input', 10); }

                    [PostHandlerFor(classStr(AosCompiledTarget), methodStr(AosCompiledTarget, value))]
                    public static void after(XppPrePostArgs args) { args.setReturnValue(args.getReturnValue() * 3); }
                }

                AosCompiledTarget target = new AosCompiledTarget();
                capture(target.value(2));
            ");

            Equal(33, Convert.ToInt32(TestProxyFactory.LastCaptured), "pre/post handler result");
        }

        private static void TestPostHandlerNullReturn()
        {
            Execute(@"
                final class InterpretedNullPostHandler {
                    [PostHandlerFor(classStr(AosCompiledTarget), methodStr(AosCompiledTarget, text))]
                    public static void after(XppPrePostArgs args) { args.setReturnValue(null); }
                }

                AosCompiledTarget target = new AosCompiledTarget();
                anytype result = target.text();
                captureAny(result);
            ");

            Equal<object>(null, TestProxyFactory.LastCaptured, "null post-handler result");
        }

        private static void TestScriptMethodByRefWriteBack()
        {
            Execute(@"
                class Mutator { public void increment(int value) { value += 1; } }
                int value = 4;
                Mutator mutator = new Mutator();
                mutator.increment(byref value);
                capture(value);
            ");
            Equal(5, Convert.ToInt32(TestProxyFactory.LastCaptured), "script method byref result");
        }

        private static void TestDeclaredFunctionByRefWriteBack()
        {
            Execute(@"
                void increment(int value) { value += 2; }
                int value = 4;
                increment(byref value);
                capture(value);
            ");
            Equal(6, Convert.ToInt32(TestProxyFactory.LastCaptured), "declared function byref result");
        }

        private static void TestScriptArrayParameter()
        {
            Execute(@"
                class Calculator {
                    public int sum(int values[]) { return values[1] + values[2]; }
                }
                int values[2];
                values[1] = 3;
                values[2] = 4;
                Calculator calculator = new Calculator();
                capture(calculator.sum(values));
            ");
            Equal(7, Convert.ToInt32(TestProxyFactory.LastCaptured), "script array parameter result");
        }

        private static void TestRuntimeModelRegistry()
        {
            var proxy = TestProxyFactory.Create();
            ByteCode byteCode = Compile(@"
                class RuntimeView { public int value; public int read() { return value; } }
                [ExtensionOf(classStr(AosCompiledTarget))]
                final class RuntimeAosTarget_Extension {
                    public int value(int input) { return next value(input); }
                }
            ", proxy);

            var scriptModel = byteCode.Models.Find("RuntimeView");
            var aosModel = byteCode.Models.Find("AosCompiledTarget");

            Equal(XppInterpreter.Interpreter.Metadata.DynamicModelKind.ScriptClass, scriptModel.Kind, "script model kind");
            Equal(1, scriptModel.Fields.Count, "script model field count");
            Equal(true, aosModel.IsNativeBacked, "AOS model backing");
        }

        private static void TestExecutableRuntimeModelRegistry()
        {
            var proxy = TestProxyFactory.Create();
            ByteCode byteCode = Compile(@"
                class RuntimeObject {
                    public int value = 2;
                    public static int shared = 3;
                    public int add(int input) { value += input; return value; }
                    public static int addShared(int input) { shared += input; return shared; }
                }
            ", proxy);
            var context = new RuntimeContext(proxy, byteCode);
            var models = context.Models;
            object instance = models.Create("RuntimeObject");

            Equal(2, Convert.ToInt32(models.GetField(instance, "value")), "initial instance field");
            models.SetField(instance, "value", 5);
            Equal(9, Convert.ToInt32(models.InvokeInstance(instance, "add", 4)), "instance invocation");
            Equal(3, Convert.ToInt32(models.GetStaticField("RuntimeObject", "shared")), "initial static field");
            Equal(5, Convert.ToInt32(models.InvokeStatic("RuntimeObject", "addShared", 2)), "static invocation");
            models.SetStaticField("RuntimeObject", "shared", 8);
            Equal(8, Convert.ToInt32(models.GetStaticField("RuntimeObject", "shared")), "static field update");
        }

        private static void TestRuntimeTableCrudAndSelect()
        {
            Execute(@"
                [RuntimeTable]
                class RuntimeCustomer {
                    [RuntimeKey] public str Account;
                    public int Balance;
                    public int64 RecId;
                }

                RuntimeCustomer customer;
                customer.Account = 'A';
                customer.Balance = 10;
                customer.insert();
                customer.clear();
                customer.Account = 'B';
                customer.Balance = 30;
                customer.insert();
                customer.clear();

                select firstonly customer order by Balance desc;
                capture(customer.RecId * 100 + customer.Balance);
            ");
            Equal(230, Convert.ToInt32(TestProxyFactory.LastCaptured), "runtime table firstOnly/order by result");
        }

        private static void TestRuntimeTableSetBasedQueries()
        {
            Execute(@"
                [RuntimeTable]
                class RuntimeCustomer {
                    [RuntimeKey] public str Account;
                    public int Balance;
                    public int64 RecId;
                }
                [RuntimeTable]
                class RuntimeArchive {
                    [RuntimeKey] public str Account;
                    public int Balance;
                    public int64 RecId;
                }

                RuntimeCustomer customer;
                RuntimeArchive archive;
                int total = 0;
                int joined = 0;
                int aggregate;

                customer.Account = 'A'; customer.Balance = 10; customer.insert(); customer.clear();
                customer.Account = 'B'; customer.Balance = 30; customer.insert(); customer.clear();

                update_recordset customer setting Balance = customer.Balance + 5
                    where customer.Account == 'A';
                insert_recordset archive (Account, Balance)
                    select Account, Balance from customer;
                select sum(Balance), count(RecId) from archive;
                aggregate = archive.Balance * 10 + archive.RecId;

                delete_from customer where customer.Account == 'B';
                ttsbegin;
                customer.clear(); customer.Account = 'C'; customer.Balance = 99; customer.insert();
                ttsabort;

                while select customer { total += customer.Balance; }
                while select customer join archive
                    where archive.Account == customer.Account
                { joined += archive.Balance; }

                capture(aggregate * 10000 + total * 100 + joined);
            ");
            Equal(4521515, Convert.ToInt32(TestProxyFactory.LastCaptured), "runtime set-based/query/transaction result");
        }

        private static void TestRuntimeTableSelectExpression()
        {
            Execute(@"
                [RuntimeTable]
                class RuntimeCustomer { [RuntimeKey] public str Account; public int Balance; }
                RuntimeCustomer customer;
                customer.Account = 'A'; customer.Balance = 10; customer.insert(); customer.clear();
                customer.Account = 'B'; customer.Balance = 30; customer.insert();
                int balance = (select firstonly Balance from RuntimeCustomer
                    order by Balance desc where RuntimeCustomer.Balance > 10).Balance;
                capture(balance);
            ");
            Equal(30, Convert.ToInt32(TestProxyFactory.LastCaptured), "runtime select expression result");
        }

        private static void TestRuntimeTableJoinedDmlAndExceptionRollback()
        {
            Execute(@"
                [RuntimeTable]
                class RuntimeCustomer { [RuntimeKey] public str Account; public int Balance; }
                [RuntimeTable]
                class RuntimeSelection { [RuntimeKey] public str Account; }
                RuntimeCustomer customer;
                RuntimeSelection selection;
                int total = 0;

                customer.Account = 'A'; customer.Balance = 10; customer.insert(); customer.clear();
                customer.Account = 'B'; customer.Balance = 20; customer.insert(); customer.clear();
                selection.Account = 'A'; selection.insert();

                update_recordset customer setting Balance = customer.Balance + 5
                    join selection where selection.Account == customer.Account;
                delete_from customer notexists join selection
                    where selection.Account == customer.Account;

                try {
                    ttsbegin;
                    customer.clear(); customer.Account = 'C'; customer.Balance = 99; customer.insert();
                    customer.clear(); customer.Account = 'C'; customer.Balance = 100; customer.insert();
                    ttscommit;
                }
                catch { }

                while select customer { total += customer.Balance; }
                capture(total);
            ");
            Equal(15, Convert.ToInt32(TestProxyFactory.LastCaptured), "joined update/delete and exception rollback result");
        }

        private static void TestRuntimeArtifactProjection()
        {
            var proxy = TestProxyFactory.Create();
            ByteCode byteCode = Compile(@"
                [RuntimeTable]
                class RuntimeCustomer { [RuntimeKey] public str Account; public int Balance; }
                [RuntimeDataEntity(RuntimeCustomer)]
                class RuntimeCustomerEntity { [RuntimeKey] public str Account; public int Balance; }
                [RuntimeView(RuntimeCustomer)]
                class RuntimeCustomerView { public str Account; public int Balance; }
                [RuntimeForm(RuntimeCustomer)]
                class RuntimeCustomerForm { public str caption() { return 'Customers'; } }
                [RuntimeReport(RuntimeCustomer)]
                class RuntimeCustomerReport { public int render() { return 7; } }
                [RuntimeService]
                class RuntimeCustomerService { public int execute(int input) { return input + 2; } }
                [RuntimeMap]
                class RuntimeCustomerMap { public str map(str value) { return value; } }
            ", proxy);
            var context = new RuntimeContext(proxy, byteCode);
            var models = context.Models;

            Equal(XppInterpreter.Interpreter.Metadata.DynamicModelKind.Table, models.Find("RuntimeCustomer").Kind, "runtime table kind");
            Equal(XppInterpreter.Interpreter.Metadata.DynamicModelKind.DataEntity, models.Find("RuntimeCustomerEntity").Kind, "runtime entity kind");
            Equal("RuntimeCustomer", models.Find("RuntimeCustomerEntity").BackingModelName, "entity backing model");
            Equal(XppInterpreter.Interpreter.Metadata.DynamicModelKind.View, models.Find("RuntimeCustomerView").Kind, "runtime view kind");
            Equal(XppInterpreter.Interpreter.Metadata.DynamicModelKind.Form, models.Find("RuntimeCustomerForm").Kind, "runtime form kind");
            Equal(XppInterpreter.Interpreter.Metadata.DynamicModelKind.Report, models.Find("RuntimeCustomerReport").Kind, "runtime report kind");
            Equal(XppInterpreter.Interpreter.Metadata.DynamicModelKind.Service, models.Find("RuntimeCustomerService").Kind, "runtime service kind");
            Equal(XppInterpreter.Interpreter.Metadata.DynamicModelKind.Map, models.Find("RuntimeCustomerMap").Kind, "runtime map kind");

            object service = models.Create("RuntimeCustomerService");
            Equal(5, Convert.ToInt32(models.InvokeInstance(service, "execute", 3)), "runtime service dispatch");
            object form = models.Create("RuntimeCustomerForm");
            Equal("Customers", Convert.ToString(models.InvokeInstance(form, "caption")), "runtime form controller dispatch");

            var table = (ScriptObject)models.Create("RuntimeCustomer");
            models.SetField(table, "Account", "A");
            models.SetField(table, "Balance", 42);
            models.InsertRecord(table);

            var entity = (ScriptObject)models.Create("RuntimeCustomerEntity");
            models.SetField(entity, "Account", "A");
            Equal(true, models.RereadRecord(entity), "runtime entity backing lookup");
            Equal(42, Convert.ToInt32(models.GetField(entity, "Balance")), "runtime entity backing value");

            var view = (ScriptObject)models.Create("RuntimeCustomerView");
            models.SetField(view, "Account", "A");
            Equal(true, models.RereadRecord(view), "runtime view backing lookup");
            bool viewRejectedWrite = false;
            try { models.InsertRecord(view); }
            catch (InvalidOperationException) { viewRejectedWrite = true; }
            Equal(true, viewRejectedWrite, "runtime view read-only enforcement");
        }

        private static void TestRuntimeArtifactIntrinsics()
        {
            Execute(@"
                [RuntimeTable, RuntimeIndex(AccountIdx, Account), RuntimeFieldGroup(Overview, Account, Balance)]
                class RuntimeCustomer {
                    [RuntimeKey] public str Account;
                    public int Balance;
                }
                [RuntimeDataEntity(RuntimeCustomer), RuntimeDataSource(Customers, RuntimeCustomer)]
                class RuntimeCustomerEntity { public str Account; public int Balance; }
                [RuntimeForm(RuntimeCustomer), RuntimeDataSource(Customers, RuntimeCustomer), RuntimeControl(BalanceControl)]
                class RuntimeCustomerForm { public str caption() { return 'Customers'; } }
                [RuntimeService]
                class RuntimeCustomerService { public int execute(int input) { return input; } }

                int valid = 1;
                if (tableStr(RuntimeCustomer) != 'RuntimeCustomer') { valid = 0; }
                if (tableNum(RuntimeCustomer) >= 0) { valid = 0; }
                if (fieldStr(RuntimeCustomer, Account) != 'Account') { valid = 0; }
                if (fieldNum(RuntimeCustomer, Balance) >= 0) { valid = 0; }
                if (indexStr(RuntimeCustomer, AccountIdx) != 'AccountIdx') { valid = 0; }
                if (indexNum(RuntimeCustomer, AccountIdx) >= 0) { valid = 0; }
                if (tableFieldGroupStr(RuntimeCustomer, Overview) != 'Overview') { valid = 0; }
                if (tableMethodStr(RuntimeCustomer, insert) != 'insert') { valid = 0; }
                if (dataEntityStr(RuntimeCustomerEntity) != 'RuntimeCustomerEntity') { valid = 0; }
                if (dataEntityDataSourceStr(RuntimeCustomerEntity, Customers) != 'Customers') { valid = 0; }
                if (formStr(RuntimeCustomerForm) != 'RuntimeCustomerForm') { valid = 0; }
                if (formDataSourceStr(RuntimeCustomerForm, Customers) != 'Customers') { valid = 0; }
                if (formControlStr(RuntimeCustomerForm, BalanceControl) != 'BalanceControl') { valid = 0; }
                if (formMethodStr(RuntimeCustomerForm, caption) != 'caption') { valid = 0; }
                if (serviceStr(RuntimeCustomerService) != 'RuntimeCustomerService') { valid = 0; }
                if (serviceOperationStr(RuntimeCustomerService, execute) != 'execute') { valid = 0; }
                capture(valid);
            ");
            Equal(1, Convert.ToInt32(TestProxyFactory.LastCaptured), "runtime intrinsic folding");
        }

        private static void TestAosMetadataSignatures()
        {
            var provider = new XppInterpreter.Interpreter.Metadata.AosTypeDescriptorProvider(TestProxyFactory.Create());
            var type = provider.DescribeType("AosMetadataFixture");
            var optional = type.FindMethod("Optional");
            var array = type.FindMethod("ArrayInput");

            Equal(1, optional.RequiredParameterCount, "optional parameter count");
            Equal(true, optional.Parameters[1].IsOptional, "optional parameter marker");
            Equal(true, array.Parameters[0].IsArray, "array parameter marker");
            Equal("values", array.Parameters[0].Name, "array parameter name");
        }

        private static void TestReplaceableChainOfCommand()
        {
            Execute(@"
                class Target { [Replaceable] public int value() { return 1; } }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension { public int value() { return 9; } }
                Target target = new Target();
                capture(target.value());
            ");
            Equal(9, Convert.ToInt32(TestProxyFactory.LastCaptured), "replaceable wrapper result");
        }

        private static void TestWrappableFinalMethod()
        {
            Execute(@"
                class Target { [Wrappable(true)] final public int value() { return 2; } }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension { public int value() { return next value() + 3; } }
                Target target = new Target();
                capture(target.value());
            ");
            Equal(5, Convert.ToInt32(TestProxyFactory.LastCaptured), "wrappable final result");
        }

        private static void TestAugmentationInstanceMembers()
        {
            Execute(@"
                class Target { }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension {
                    public int state = 4;
                    private void new() { state += 1; }
                    public int increment() { state += 1; return state; }
                }
                Target target = new Target();
                target.state = 10;
                int incremented = target.increment();
                capture(target.state * 100 + incremented);
            ");
            Equal(1111, Convert.ToInt32(TestProxyFactory.LastCaptured), "augmentation instance result");
        }

        private static void TestAugmentationStaticMembers()
        {
            Execute(@"
                class Target { }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension {
                    public static int state;
                    static void TypeNew() { state = 7; }
                    public static int doubleValue(int input) { return input * 2; }
                }
                capture(Target::state * 10 + Target::doubleValue(3));
            ");
            Equal(76, Convert.ToInt32(TestProxyFactory.LastCaptured), "augmentation static result");
        }

        private static void TestAugmentationInstanceStaticState()
        {
            Execute(@"
                class Target { }
                [ExtensionOf(classStr(Target))]
                final class Target_Extension {
                    public static int shared = 5;
                    public int bump() { shared += 1; return shared; }
                }
                Target target = new Target();
                int first = target.bump();
                int second = target.bump();
                capture(first * 10 + second);
            ");
            Equal(67, Convert.ToInt32(TestProxyFactory.LastCaptured), "extension instance/static state result");
        }

        private static void TestCrossCompanyQuery()
        {
            ParseResult parsed = Parse(@"
                anytype buffer;
                container companies;
                select crossCompany : companies buffer;
            ", TestProxyFactory.Create());
            ThrowIfParseErrors(parsed);

            var program = (XppInterpreter.Parser.Program)parsed.AST;
            var select = (Select)program.Statements[2];

            if (select.Query.CrossCompany?.Container is null)
            {
                throw new Exception("crossCompany container was not retained in the query AST.");
            }
        }

        private static void TestQueryIndexModes()
        {
            ParseResult parsed = Parse(@"
                anytype firstBuffer;
                anytype secondBuffer;
                select firstBuffer index MyIndex;
                select secondBuffer index hint MyIndex;
            ", TestProxyFactory.Create());
            ThrowIfParseErrors(parsed);

            var program = (XppInterpreter.Parser.Program)parsed.AST;
            var selectedIndex = ((Select)program.Statements[2]).Query;
            var hintedIndex = ((Select)program.Statements[3]).Query;

            Equal("MyIndex", selectedIndex.Index, "selected index name");
            Equal(false, selectedIndex.IndexIsHint, "selected index mode");
            Equal("MyIndex", hintedIndex.Index, "hinted index name");
            Equal(true, hintedIndex.IndexIsHint, "hinted index mode");
        }

        private static void TestSelectAllProjection()
        {
            ParseResult parsed = Parse(@"
                anytype buffer;
                select buffer;
            ", TestProxyFactory.Create());
            ThrowIfParseErrors(parsed);

            var program = (XppInterpreter.Parser.Program)parsed.AST;
            var select = (Select)program.Statements[1];
            Equal<object>(null, select.Query.SelectionFields, "omitted projection");
        }

        private static void TestQueryExpressionEvaluation()
        {
            var proxy = TestProxyFactory.Create();
            ByteCode byteCode = Compile(string.Empty, proxy);
            var context = new RuntimeContext(proxy, byteCode);
            var helper = new QueryGenerationHelper(context);

            var containerExpression = new ContainerInitialisation(
                new List<Expression> { new Constant(1, null), new Constant(2, null) }, null);
            var container = (object[])helper.ComputeExpression(containerExpression);
            Equal(1, Convert.ToInt32(container[0]), "container first element");
            Equal(2, Convert.ToInt32(container[1]), "container second element");

            var ternary = new Ternary(
                new Token(TType.QuestionMark),
                new Constant(true, null),
                new Constant(7, null),
                new Constant(9, null),
                null);
            Equal(7, Convert.ToInt32(helper.ComputeExpression(ternary)), "ternary value");
        }

        private static void TestQueryTableReferenceDetection()
        {
            var proxy = TestProxyFactory.Create();
            ByteCode byteCode = Compile(string.Empty, proxy);
            var helper = new QueryGenerationHelper(new RuntimeContext(proxy, byteCode));
            var field = new TableField(new Word("buffer", TType.Id), "buffer", "RecId", null);

            Equal(true, helper.ContainsTableReference(field), "direct table field");
            Equal(false, helper.ContainsTableReference(new Constant(1, null)), "constant expression");
        }

        private static void TestTtsStatements()
        {
            Execute("ttsbegin; ttscommit; ttsabort;");
            Equal(1, TestProxyFactory.TtsBeginCount, "ttsbegin count");
            Equal(1, TestProxyFactory.TtsCommitCount, "ttscommit count");
            Equal(1, TestProxyFactory.TtsAbortCount, "ttsabort count");
        }

        private static void TestGlobalTtsAbort()
        {
            var proxy = TestProxyFactory.Create();
            ParseResult parsed = Parse("int value = 1;", proxy);
            ThrowIfParseErrors(parsed);

            var interpreter = new XppInterpreter.Interpreter.XppInterpreter(
                proxy,
                new XppInterpreterOptions { EmitGlobalTtsAbort = true });
            interpreter.Interpret((XppInterpreter.Parser.Program)parsed.AST);

            Equal(1, TestProxyFactory.TtsBeginCount, "global ttsbegin count");
            Equal(1, TestProxyFactory.TtsAbortCount, "global ttsabort count");
        }

        private static void TestClrInstanceInterop()
        {
            Execute(@"
                using System.Text;
                StringBuilder builder = new StringBuilder();
                builder.Append('abc');
                builder.Length = 2;
                capture(builder.Length);
            ");
            Equal(2, Convert.ToInt32(TestProxyFactory.LastCaptured), "CLR property value");
        }

        private static void TestClrOverloadSelection()
        {
            Execute(@"
                using XppInterpreter.Tests;
                capture(ClrInteropFixture::Choose(2.5));
            ");
            Equal("real:2.5", Convert.ToString(TestProxyFactory.LastCaptured), "CLR overload result");
        }

        private static void TestClrOptionalAndParamsArguments()
        {
            Execute(@"
                using XppInterpreter.Tests;
                int optional = ClrInteropFixture::Optional();
                int summed = ClrInteropFixture::Sum(1, 2, 3, 4);
                capture(optional * 100 + summed);
            ");
            Equal(710, Convert.ToInt32(TestProxyFactory.LastCaptured), "CLR optional/params result");
        }

        private static void TestIntegerFastPath()
        {
            Execute(@"
                int a = 7;
                int b = -3;
                int64 c = 5000000000;
                int score = 0;
                if (a > b) { score += 1; }
                if (b < a) { score += 2; }
                if (a >= 7) { score += 4; }
                if (b <= -3) { score += 8; }
                if (a != b) { score += 16; }
                if (!(a == b)) { score += 32; }
                if (c > a) { score += 64; }
                if ((a & 3) == 3 && (a | 8) == 15 && (a ^ 2) == 5) { score += 128; }
                if (true == true && false != true) { score += 256; }
                if (a * b + a - b == -11) { score += 512; }
                if (c + c == 10000000000 && c - 1 == 4999999999) { score += 1024; }
                capture(score);
            ");
            Equal(2047, Convert.ToInt32(TestProxyFactory.LastCaptured), "integer fast path");
        }

        private static void TestCompiledLoopsMatchInterpreter()
        {
            string[] sources =
            {
                // Typed int and int64 variables, nested loops and short-circuit conditions
                @"int total = 0; int i; int j;
                  for (i = 0; i < 200; i++) { for (j = 0; j < 5; j++) { if (i > 10 && j < 3 || i == 150) total += i * j; } }
                  capture(total);",
                @"int64 big = 3000000000; int i = 0;
                  while (i < 300) { big = big + big - big + 3; if (big > 3000000500) { big = big - 600; } i++; }
                  captureAny(big);",
                // Mixed widths, mod and booleans go through the same code the interpreter runs
                @"int64 total = 0; int i = 0;
                  while (i < 300) { if (i mod 3 == 0) { total += i * 2; } else { total -= 1; } i++; }
                  captureAny(total);",
                @"boolean flip = false; int i;
                  for (i = 0; i < 301; i++) { flip = flip == false; }
                  captureAny(flip);",
                // Strings and reals are never unboxed
                @"str text = ''; int i;
                  for (i = 0; i < 40; i++) { text = text + 'a'; }
                  captureAny(text);",
                @"real amount = 0.5; int i;
                  for (i = 0; i < 40; i++) { amount = amount * 2; }
                  captureAny(amount);",
                // break and continue are interpreted and must still agree
                @"int total = 0; int i;
                  for (i = 0; i < 100; i++) { if (i == 3) continue; if (i == 90) break; total += i; }
                  capture(total);"
            };

            foreach (string source in sources)
            {
                LoopJit.Enabled = false;
                Execute(source);
                object interpreted = TestProxyFactory.LastCaptured;

                LoopJit.Enabled = true;
                Execute(source);
                Execute(source);
                object compiled = TestProxyFactory.LastCaptured;

                Equal(Convert.ToString(interpreted), Convert.ToString(compiled), "compiled loop result");
                Equal(interpreted?.GetType(), compiled?.GetType(), "compiled loop result type");
            }
        }

        private static void TestCompiledLoopWriteBack()
        {
            Execute(@"
                int value = 2147483000;
                int i;
                try
                {
                    for (i = 0; i < 1000; i++) { value += 1; }
                }
                catch
                {
                    capture(value);
                }
            ");
            Equal(2147483647, Convert.ToInt32(TestProxyFactory.LastCaptured), "value at overflow");
        }

        private static void TestBracelessBodies()
        {
            Execute(@"
                int score = 0;
                int i;
                int a = 1;
                int b = 0;
                if (a == 1) score += 1;
                if (a == 2) score += 1000; else score += 2;
                if (a == 1) if (b == 1) score += 1000; else score += 4;
                for (i = 0; i < 3; i++) score += 8;
                i = 0;
                while (i < 2) i++;
                if (i == 2) score += 32;
                do i--; while (i > 0);
                if (i == 0) score += 64;
                capture(score);
            ");
            Equal(1 + 2 + 4 + 24 + 32 + 64, Convert.ToInt32(TestProxyFactory.LastCaptured), "braceless bodies");
        }

        private static void TestHotClrOverloads()
        {
            Execute(@"
                using XppInterpreter.Tests;
                str result = '';
                for (int i = 0; i < 40; i++)
                {
                    result = ClrInteropFixture::Choose(i) + ',' + ClrInteropFixture::Choose(2.5);
                }
                capture(result);
            ");
            Equal("int:39,real:2.5", Convert.ToString(TestProxyFactory.LastCaptured), "hot overload selection");
        }

        private static void TestHotClrExceptions()
        {
            Execute(@"
                using XppInterpreter.Tests;
                int caught = 0;
                for (int i = 0; i < 40; i++)
                {
                    try
                    {
                        ClrInteropFixture::FailAt(i, 30);
                    }
                    catch
                    {
                        caught = i;
                    }
                }
                capture(caught);
            ");
            Equal(30, Convert.ToInt32(TestProxyFactory.LastCaptured), "hot call exception");
        }

        private static void TestHotClrNullArgument()
        {
            Execute(@"
                using XppInterpreter.Tests;
                int total = 0;
                for (int i = 0; i < 20; i++)
                {
                    total += ClrInteropFixture::Twice(i);
                }
                capture(total);
            ");
            Equal(380, Convert.ToInt32(TestProxyFactory.LastCaptured), "hot call total");
        }

        private static void TestPolymorphicCallSite()
        {
            Execute(@"
                using System.Text;
                str result = '';
                System.Object value;
                for (int i = 0; i < 40; i++)
                {
                    if (i mod 2 == 0)
                    {
                        value = new StringBuilder('sb');
                    }
                    else
                    {
                        value = new System.Version(1, 2);
                    }
                    result = value.ToString() + result;
                }
                capture(result);
            ");
            string expected = string.Concat(Enumerable.Repeat("1.2sb", 20));
            Equal(expected, Convert.ToString(TestProxyFactory.LastCaptured), "polymorphic dispatch");
        }

        private static void TestClrByRefWriteBack()
        {
            Execute(@"
                using XppInterpreter.Tests;
                int value = 4;
                ClrInteropFixture::Increment(byref value);
                capture(value);
            ");
            Equal(5, Convert.ToInt32(TestProxyFactory.LastCaptured), "CLR byref value");
        }

        private static void TestCompiledInterfaceIdentity()
        {
            Execute(@"
                class Callback implements IAosCallback {
                    public int Compute(int input) { return input * 2; }
                    public void Mutate(int value) { value += 5; }
                }

                class DerivedCallback extends Callback {
                    public int Compute(int input) { return input * 3; }
                }

                DerivedCallback callback = new DerivedCallback();
                boolean matches = callback is IAosCallback;
                IAosCallback interfaceCallback = callback as IAosCallback;
                int computed = AosInterfaceConsumer::Call(interfaceCallback);
                int mutated = AosInterfaceConsumer::Mutate(interfaceCallback, 10);
                capture((matches ? computed : 0) * 100 + mutated);
            ");
            Equal(1215, Convert.ToInt32(TestProxyFactory.LastCaptured), "compiled interface dispatch and byref copy-back");
        }

        private static void TestCompiledBaseIdentity()
        {
            Execute(@"
                class ScriptDerived extends AosVirtualBase {
                    public int Compute(int input) { return super(input) + 10; }
                }

                ScriptDerived derived = new ScriptDerived();
                AosVirtualBase compiledBase = derived;
                capture(AosBaseConsumer::Call(compiledBase));
            ");
            Equal(15, Convert.ToInt32(TestProxyFactory.LastCaptured), "compiled base identity, virtual callback, and super");
        }

        private static void TestClrAliasAndStaticMember()
        {
            Execute(@"
                using Fixture = XppInterpreter.Tests.ClrInteropFixture;
                Fixture::Value = 9;
                capture(Fixture::Value);
            ");
            Equal(9, Convert.ToInt32(TestProxyFactory.LastCaptured), "CLR static property value");
        }

        private static void TestMacroExpansion()
        {
            Execute(@"
                #define.Base(4)
                #localmacro.Add
                    (%1 + %2)
                #endmacro
                int value = #Add(#Base, 3);
                #if.Base(4)
                    value += 10;
                #endif
                #ifnot.Missing
                    value += 20;
                #endif
                capture(value);
            ");
            Equal(37, Convert.ToInt32(TestProxyFactory.LastCaptured), "macro result");
        }

        private static void TestMacroLibraryResolution()
        {
            var preprocessor = new MacroPreprocessor
            {
                MacroLibraryResolver = name => string.Equals(name, "TestMacros", StringComparison.OrdinalIgnoreCase)
                    ? "#define.LibraryValue(42)"
                    : null
            };

            var proxy = TestProxyFactory.Create();
            ParseResult parsed = new XppParser(
                new XppLexer("#macrolib.TestMacros capture(#LibraryValue);", preprocessor),
                proxy).Parse();
            ThrowIfParseErrors(parsed);

            var interpreter = new XppInterpreter.Interpreter.XppInterpreter(proxy);
            ByteCode byteCode = interpreter.Compile((XppInterpreter.Parser.Program)parsed.AST);
            interpreter.Interpret(byteCode, new RuntimeContext(proxy, byteCode));

            Equal(42, Convert.ToInt32(TestProxyFactory.LastCaptured), "macro library value");
        }

        private static void TestFileSystemMacroLibraryResolution()
        {
            string root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "XppInterpreterMacroTest_" + Guid.NewGuid().ToString("N"));
            string dictionaryDirectory = System.IO.Path.Combine(root, "TestPackage", "TestModel", "AxMacroDictionary");

            try
            {
                System.IO.Directory.CreateDirectory(dictionaryDirectory);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dictionaryDirectory, "TestLibrary.xml"),
                    "<AxMacroDictionary><Name>TestLibrary</Name><Source>#define.FileValue(73)</Source><Macros /></AxMacroDictionary>");

                var preprocessor = new MacroPreprocessor();
                preprocessor.UseFileSystemMacroLibraries(root);

                var proxy = TestProxyFactory.Create();
                ParseResult parsed = new XppParser(
                    new XppLexer("#macrolib.TestLibrary capture(#FileValue);", preprocessor),
                    proxy).Parse();
                ThrowIfParseErrors(parsed);

                var interpreter = new XppInterpreter.Interpreter.XppInterpreter(proxy);
                ByteCode byteCode = interpreter.Compile((XppInterpreter.Parser.Program)parsed.AST);
                interpreter.Interpret(byteCode, new RuntimeContext(proxy, byteCode));

                Equal(73, Convert.ToInt32(TestProxyFactory.LastCaptured), "filesystem macro library value");
            }
            finally
            {
                if (System.IO.Directory.Exists(root))
                {
                    System.IO.Directory.Delete(root, recursive: true);
                }
            }
        }

        private static void TestRecursiveMacroExpansion()
        {
            try
            {
                ParseResult parsed = Parse(@"
                    #define.First(#Second)
                    #define.Second(#First)
                    int value = #First;
                ", TestProxyFactory.Create());
                ThrowIfParseErrors(parsed);
            }
            catch (Exception exception)
            {
                if (exception.ToString().IndexOf("maximum depth", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }

                throw;
            }

            throw new Exception("Expected recursive macro expansion to fail.");
        }

        private static void Execute(string source, XppInterpreterOptions options = null)
        {
            var proxy = TestProxyFactory.Create();
            ByteCode byteCode = Compile(source, proxy, options);
            var interpreter = new XppInterpreter.Interpreter.XppInterpreter(proxy, options);
            var context = new RuntimeContext(proxy, byteCode);
            interpreter.Interpret(byteCode, context);
        }

        private static void ExpectRuntimeError(string source, XppInterpreterOptions options, string expectedMessage)
        {
            try
            {
                Execute(source, options);
            }
            catch (Exception exception)
            {
                if (exception.ToString().IndexOf(expectedMessage, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }

                throw new Exception("Expected runtime error containing '" + expectedMessage + "', got: " + exception.Message);
            }

            throw new Exception("Expected runtime error containing '" + expectedMessage + "', but execution completed.");
        }

        private static ByteCode Compile(
            string source,
            XppInterpreter.Interpreter.Proxy.XppProxy proxy = null,
            XppInterpreterOptions options = null)
        {
            proxy = proxy ?? TestProxyFactory.Create();
            ParseResult parsed = Parse(source, proxy);
            ThrowIfParseErrors(parsed);

            var interpreter = new XppInterpreter.Interpreter.XppInterpreter(proxy, options);
            return interpreter.Compile((XppInterpreter.Parser.Program)parsed.AST);
        }

        private static void ExpectParses(string source)
        {
            ParseResult parsed = Parse(source, TestProxyFactory.Create());
            ThrowIfParseErrors(parsed);
        }

        private static void ExpectParseError(string source, string expectedMessage)
        {
            ParseResult parsed = Parse(source, TestProxyFactory.Create());
            string messages = string.Join(" | ", parsed.Errors.Select(error => error.Message));

            if (!parsed.HasErrors || messages.IndexOf(expectedMessage, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new Exception("Expected parse error containing '" + expectedMessage + "', got: " + messages);
            }
        }

        private static ParseResult Parse(string source, XppInterpreter.Interpreter.Proxy.XppProxy proxy)
        {
            return new XppParser(new XppLexer(source), proxy).Parse();
        }

        private static void ThrowIfParseErrors(ParseResult parsed)
        {
            if (parsed.HasErrors)
            {
                throw new Exception(string.Join(" | ", parsed.Errors.Select(error => error.Message)));
            }
        }

        private static void Equal<T>(T expected, T actual, string label)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception(label + ": expected " + expected + ", got " + actual);
            }
        }
    }

    public static class ClrInteropFixture
    {
        public static int Value { get; set; }

        public static string Choose(int value)
        {
            return "int:" + value;
        }

        public static string Choose(double value)
        {
            return "real:" + value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static int Optional(int value = 7)
        {
            return value;
        }

        public static int Sum(params int[] values)
        {
            return values.Sum();
        }

        public static void Increment(ref int value)
        {
            value++;
        }

        public static void FailAt(int value, int failure)
        {
            if (value == failure)
            {
                throw new InvalidOperationException("failed at " + value);
            }
        }

        public static int Twice(int value)
        {
            return value * 2;
        }
    }

    public interface IAosCallback
    {
        int Compute(int input);
        void Mutate(ref int value);
    }

    public static class AosInterfaceConsumer
    {
        public static int Call(IAosCallback callback)
        {
            return callback.Compute(4);
        }

        public static int Mutate(IAosCallback callback, int value)
        {
            callback.Mutate(ref value);
            return value;
        }
    }

    public class AosVirtualBase
    {
        public virtual int Compute(int input)
        {
            return input + 1;
        }

        public int CallVirtual(int input)
        {
            return Compute(input);
        }
    }

    public static class AosBaseConsumer
    {
        public static int Call(AosVirtualBase value)
        {
            return value.CallVirtual(4);
        }
    }

    public class AosCompiledTarget
    {
        public int value(int input)
        {
            return input + 1;
        }

        public static int staticValue(int input)
        {
            return input + 1;
        }

        public string text()
        {
            return "native";
        }
    }

    public static class AosEventSource
    {
        public static event Action<int> Changed;

        public static void Raise(int value)
        {
            Changed?.Invoke(value);
        }

        public static void Reset()
        {
            Changed = null;
        }
    }
}

namespace Dynamics.AX.Application
{
    public enum DataEventType
    {
        Inserted,
        Updated,
        Deleted,
    }

    public sealed class DataEventArgs : EventArgs
    {
    }
}

namespace Microsoft.Dynamics.Ax.Xpp
{
    public delegate void DataEventHandler(object sender, global::Dynamics.AX.Application.DataEventArgs args);

    public static class DataEventManager
    {
        private static readonly Dictionary<string, Dictionary<global::Dynamics.AX.Application.DataEventType, DataEventHandler>> Handlers =
            new Dictionary<string, Dictionary<global::Dynamics.AX.Application.DataEventType, DataEventHandler>>(StringComparer.OrdinalIgnoreCase);

        public static void Subscribe(string tableName, global::Dynamics.AX.Application.DataEventType eventType, DataEventHandler handler)
        {
            if (!Handlers.TryGetValue(tableName, out Dictionary<global::Dynamics.AX.Application.DataEventType, DataEventHandler> byEvent))
            {
                byEvent = new Dictionary<global::Dynamics.AX.Application.DataEventType, DataEventHandler>();
                Handlers[tableName] = byEvent;
            }

            byEvent.TryGetValue(eventType, out DataEventHandler current);
            byEvent[eventType] = (DataEventHandler)Delegate.Combine(current, handler);
        }

        public static void Raise(string tableName, global::Dynamics.AX.Application.DataEventType eventType)
        {
            if (Handlers.TryGetValue(tableName, out Dictionary<global::Dynamics.AX.Application.DataEventType, DataEventHandler> byEvent) &&
                byEvent.TryGetValue(eventType, out DataEventHandler handler))
            {
                handler(new object(), new global::Dynamics.AX.Application.DataEventArgs());
            }
        }

        public static void Reset()
        {
            Handlers.Clear();
        }
    }
}

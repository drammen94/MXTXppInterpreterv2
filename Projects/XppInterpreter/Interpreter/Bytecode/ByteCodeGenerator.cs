using System.Collections.Generic;
using System.Linq;
using System.Text;
using XppInterpreter.Interpreter.Debug;
using XppInterpreter.Lexer;
using XppInterpreter.Parser;
using XppInterpreter.Parser.Data;

namespace XppInterpreter.Interpreter.Bytecode
{
    public class ByteCodeGenerator : IAstVisitor
    {
        private readonly ByteCodeGenerationContext _generationContext = new ByteCodeGenerationContext();
        private readonly Stack<ByteCodeGenerationScope> _ss = new Stack<ByteCodeGenerationScope>();
        private readonly List<RefFunction> _declaredFunctions = new List<RefFunction>();
        private readonly List<RuntimeClass> _declaredClasses = new List<RuntimeClass>();
        private readonly bool _hasMaxIterations;
        private bool _generateDebugInfo;
        private RuntimeClass _currentRuntimeClass;
        private RuntimeClassMethod _currentRuntimeMethod;

        public XppInterpreterOptions Options { get; }

        public ByteCodeGenerator(XppInterpreterOptions options = null, List<RefFunction> declaredFunctions = null, List<RuntimeClass> declaredClasses = null)
        {
            _ss.Push(new ByteCodeGenerationScope());

            Options = options;
            _hasMaxIterations = Options != null && Options.MaxLoopIterations > 0;

            if (declaredFunctions != null)
            {
                _declaredFunctions.AddRange(declaredFunctions);
            }

            if (declaredClasses != null)
            {
                _declaredClasses.AddRange(declaredClasses);
            }
        }

        public ByteCode GeneratedByteCode => new ByteCode(_ss.Peek().Instructions)
        {
            DeclaredFunctions = _declaredFunctions,
            DeclaredClasses = _declaredClasses
        };

        public ByteCode Generate(Program program, bool generateDebugInfo, List<RefFunction> dependencyFunctions = null, List<RuntimeClass> dependencyClasses = null)
        {
            if (dependencyFunctions != null)
            {
                _declaredFunctions.AddRange(dependencyFunctions);
            }

            if (dependencyClasses != null)
            {
                _declaredClasses.AddRange(dependencyClasses);
            }

            if (_ss.Count == 0)
            {
                _ss.Push(new ByteCodeGenerationScope());
            }

            _generateDebugInfo = generateDebugInfo;

            VisitProgram(program);

            return new ByteCode(_ss.Pop().Instructions)
            {
                DeclaredFunctions = _declaredFunctions,
                DeclaredClasses = _declaredClasses
            };

        }

        public ByteCode GetProgram()
        {
            return new ByteCode(_ss.Pop().Instructions)
            {
                DeclaredFunctions = _declaredFunctions,
                DeclaredClasses = _declaredClasses
            };
        }

        public void Emit(IInstruction instruction)
        {
            _ss.Peek().Instructions.Add(instruction);
        }

        public void EmitScope(ByteCodeGenerationScope scope)
        {
            _ss.Peek().Instructions.AddRange(scope.Instructions);
        }

        public void EmitDebugSymbol(IDebuggeable debuggeable, bool always = false)
        {
            if (_generateDebugInfo && debuggeable.DebuggeableBinding != null)
            {
                Emit(new Debug(debuggeable, always));
            }
        }

        private void CreateScope()
        {
            var newScope = new ByteCodeGenerationScope();
            _ss.Push(newScope);
        }

        private ByteCodeGenerationScope ReleaseScope()
        {
            return _ss.Pop();
        }

        public void VisitInsertRecordset(InsertRecordset insertRecordset)
        {
            EmitDebugSymbol(insertRecordset);
            Emit(new InsertQuery(insertRecordset));
        }

        public void VisitDeleteFrom(DeleteFrom deleteFrom)
        {
            EmitDebugSymbol(deleteFrom);
            Emit(new DeleteQuery(deleteFrom));
        }

        public void VisitUpdateRecordset(UpdateRecordset updateRecordset)
        {
            EmitDebugSymbol(updateRecordset);
            Emit(new UpdateQuery(updateRecordset));
        }

        public void VisitThrow(Throw @throw)
        {
            EmitDebugSymbol(@throw);
            @throw.Exception.Accept(this);
            Emit(new ThrowException());
        }

        public void VisitSelect(Parser.Select select)
        {
            EmitDebugSymbol(select);
            Emit(new Select(select.Query, _generationContext.IsWhileSelectBeingGenerated(select)));
        }

        public void VisitSelectExpression(Parser.SelectExpression selectExpression)
        {
            EmitDebugSymbol(selectExpression);
            Emit(new Select(selectExpression.Query, returnField: selectExpression.ReturnField));
        }

        public void VisitNext(Parser.Next next)
        {
            EmitDebugSymbol(next);
            Emit(new VariableLoad(next.TableVariableName, false));
            Emit(new Next());
        }

        public void VisitFlush(Parser.Flush flush)
        {
            EmitDebugSymbol(flush);
            Emit(new Flush(flush.TableName));
        }

        public void VisitWhileSelect(WhileSelect whileSelect)
        {
            _generationContext.AddWhileSelect(whileSelect.Select);

            EmitDebugSymbol(whileSelect);

            CreateScope();
            whileSelect.Select.Accept(this);
            var selectScope = ReleaseScope();

            CreateScope();
            whileSelect.Block.Accept(this);
            var blockScope = ReleaseScope();

            int whileSelectStartIndex = _ss.Peek().Instructions.Count;

            EmitScope(selectScope);
            Emit(new JumpIfFalse(blockScope.Count + 1 + 1 /*Jump instruction*/));
            EmitScope(blockScope);
            Emit(new Jump(-(blockScope.Count + selectScope.Count) - 1));

            _generationContext.RemoveWhileSelect(whileSelect.Select);

            this.RecalculateLoopControlOffsets(selectScope.Instructions.First(), whileSelectStartIndex);
        }

        public void VisitUnchecked(Parser.Unchecked @unchecked)
        {
            @unchecked.Expression.Accept(this);
            Emit(new Unchecked());
            @unchecked.Block.Accept(this);
            Emit(new DisposeHandle());
        }

        public void VisitChangeCompany(Parser.ChangeCompany changeCompany)
        {
            changeCompany.Expression.Accept(this);
            Emit(new ChangeCompany());
            changeCompany.Block.Accept(this);
            Emit(new DisposeHandle());
        }

        public void VisitContainerInitialisation(ContainerInitialisation containerInitialisation)
        {
            for (int nElement = containerInitialisation.Elements.Count - 1; nElement >= 0; nElement--)
            {
                containerInitialisation.Elements[nElement].Accept(this);
            }

            Emit(new Container(containerInitialisation.Elements.Count));
        }

        public void VisitTtsCommit(Parser.TtsCommit ttsCommit)
        {
            EmitDebugSymbol(ttsCommit);
            Emit(new TtsCommit());
        }

        public void VisitTtsBegin(Parser.TtsBegin ttsBegin)
        {
            EmitDebugSymbol(ttsBegin);
            Emit(new TtsBegin());
        }

        public void VisitTtsAbort(Parser.TtsAbort ttsAbort)
        {
            EmitDebugSymbol(ttsAbort);
            Emit(new TtsAbort());
        }

        public void VisitLoopControl(Parser.LoopControl loopControl)
        {
            EmitDebugSymbol(loopControl);
            Emit(new LoopControl(0, loopControl.Token.TokenType, true));
        }

        public void VisitConstructor(Constructor constructor)
        {
            for (var narg = constructor.Parameters.Count - 1; narg >= 0; narg--)
            {
                constructor.Parameters[narg].Accept(this);
            }

            Emit(new NewObject(constructor.ClassName, constructor.Parameters.Count, true, constructor.ResolvedClrType));
        }

        public void VisitAssignment(Assignment assignment)
        {
            EmitDebugSymbol(assignment);

            assignment.Expression.Accept(this);

            // Static member assignment (ClassName::member = value)
            if (assignment.Assignee.StaticCall && assignment.Assignee.Caller is Variable staticCaller)
            {
                Emit(new StaticStore(assignment.Assignee.Name, ((Word)staticCaller.Token).Lexeme, staticCaller.ClrTypeReference));
                return;
            }

            bool fromCaller = false;

            if (assignment.Assignee.Caller != null)
            {
                fromCaller = true;
                assignment.Assignee.Caller.Accept(this);
            }

            bool isArray = false;

            if (assignment.Assignee is ArrayAccess access)
            {
                isArray = true;
                access.Index.Accept(this);
            }

            Emit(new Store(assignment.Assignee.Name, fromCaller, false, isArray, null, null));
        }

        public void VisitContainerAssignment(ContainerAssignment containerAssignment)
        {
            EmitDebugSymbol(containerAssignment);

            containerAssignment.Expression.Accept(this);

            int containerIndex = 1;
            int maxIndex = containerAssignment.Assignees.Count;

            foreach (var assignee in containerAssignment.Assignees)
            {
                bool fromCaller = assignee.Caller != null;
                if (fromCaller)
                {
                    assignee.Caller.Accept(this);
                }

                bool isArray = false;
                if (assignee is ArrayAccess access)
                {
                    isArray = true;
                    access.Index.Accept(this);
                }

                Emit(new ContainerStore(assignee.Name, containerIndex, containerIndex == maxIndex, fromCaller, false, isArray, null, null));

                containerIndex++;
            }
        }

        public void VisitBinaryOperation(BinaryOperation binaryOperation)
        {
            binaryOperation.LeftOperand.Accept(this);
            var tokenType = binaryOperation.Token.TokenType;

            if (Arithmetic.IsArithmetic(tokenType))
            {
                binaryOperation.RightOperand.Accept(this);

                Emit(new Arithmetic(binaryOperation.Token.TokenType));
            }
            else
            {
                // Create scope for left operand
                CreateScope();
                binaryOperation.RightOperand.Accept(this);
                var scope = ReleaseScope();

                if (tokenType == TType.And)
                {
                    Emit(new JumpIfFalse(scope.Count + 1));
                }
                else
                {
                    Emit(new JumpIfTrue(scope.Count + 1));
                }

                EmitScope(scope);
            }
        }

        public void VisitBlock(Block block)
        {
            EmitDebugSymbol(block);
            Emit(new BeginScope());

            CreateScope();
            foreach (var statement in block.Statements)
            {
                statement.Accept(this);
            }
            var blockScope = ReleaseScope();

            EmitScope(blockScope);
            Emit(new EndScope());
        }

        public void VisitConstant(Constant constant)
        {
            object value = null;

            if (constant.Token is BaseType baseType)
            {
                value = baseType.Value;
            }
            else if (constant.Token.TokenType == TType.True)
            {
                value = true;
            }
            else if (constant.Token.TokenType == TType.False)
            {
                value = false;
            }

            Emit(new Push(value));
        }

        public void VisitDo(Do @do)
        {
            EmitDebugSymbol(@do);

            CreateScope();
            @do.Block.Accept(this);
            @do.Expression.Accept(this);
            var scope = ReleaseScope();

            EmitScope(scope);
            Emit(new JumpIfTrue(-(scope.Count)));
        }

        public void VisitElse(Else @else)
        {
            EmitDebugSymbol(@else);

            VisitIf(@else);
        }

        public void VisitFor(For @for)
        {
            EmitDebugSymbol(@for);

            Emit(new BeginScope());

            @for.Initialisation?.Accept(this);

            CreateScope();
            @for.Condition?.Accept(this);
            var forScope = ReleaseScope();

            CreateScope();
            @for.Block.Accept(this);
            var blockScope = ReleaseScope();

            CreateScope();
            @for.LoopStatement.Accept(this);
            var loopScope = ReleaseScope();

            CreateScope();
            if (_hasMaxIterations)
            {
                Emit(new MaxLoopThrow());
            }

            var maxLoopScope = ReleaseScope();

            int forStartIndex = _ss.Peek().Instructions.Count;

            if (_hasMaxIterations)
            {
                Emit(new Push(Options.MaxLoopIterations));
            }

            EmitScope(forScope);
            Emit(new JumpIfFalse(maxLoopScope.Count + blockScope.Count + loopScope.Count + 1 + 1 /* Jump instruction */));
            EmitScope(maxLoopScope);
            EmitScope(blockScope);
            EmitScope(loopScope);
            Emit(new Jump(-(maxLoopScope.Count + blockScope.Count + loopScope.Count + forScope.Count + 1)));

            RecalculateLoopControlOffsets(loopScope.Instructions.First(), forStartIndex);

            Emit(new EndScope());

        }

        public void VisitNoReturnFunctionCall(NoReturnFunctionCall noReturnFunctionCall)
        {
            EmitDebugSymbol(noReturnFunctionCall);

            // A statement-level constructor call (new MyClass();) discards the instance
            if (noReturnFunctionCall.FunctionCall is Constructor constructor)
            {
                for (var narg = constructor.Parameters.Count - 1; narg >= 0; narg--)
                {
                    constructor.Parameters[narg].Accept(this);
                }

                Emit(new NewObject(constructor.ClassName, constructor.Parameters.Count, false, constructor.ResolvedClrType));
                return;
            }

            VisitFunctionCall(noReturnFunctionCall.FunctionCall, false);
        }

        public void VisitProgram(Program program)
        {
            Emit(new BeginScope());

            foreach (var statement in program.Statements)
            {
                statement.Accept(this);
            }

            LinkClassExtensions();

            Emit(new EndScope());
        }

        private void LinkClassExtensions()
        {
            foreach (RuntimeClass runtimeClass in _declaredClasses)
            {
                runtimeClass.ExtensionMethods.Clear();
                runtimeClass.ExtensionFields.Clear();
                runtimeClass.ExtensionTarget = null;
                runtimeClass.HasCompiledExtensionTarget = false;
                runtimeClass.ExternalExtensionRoot = null;

                foreach (RuntimeClassMethod method in runtimeClass.Methods)
                {
                    method.NextMethod = null;
                    method.NextCallsNative = false;
                    method.NativeTargetName = null;
                }
            }

            foreach (RuntimeClass extensionClass in _declaredClasses.Where(runtimeClass =>
                !string.IsNullOrEmpty(runtimeClass.ExtensionTargetName)))
            {
                RuntimeClass target = _declaredClasses.FirstOrDefault(runtimeClass =>
                    string.Equals(runtimeClass.Name, extensionClass.ExtensionTargetName, System.StringComparison.OrdinalIgnoreCase));

                extensionClass.ExtensionTarget = target;

                if (target is null)
                {
                    extensionClass.HasCompiledExtensionTarget = true;
                    continue;
                }

                foreach (RuntimeClassField extensionField in extensionClass.Fields.Where(field =>
                    field.Access == AccessModifier.Public))
                {
                    target.ExtensionFields.Add(extensionField);
                }

                foreach (RuntimeClassMethod extensionMethod in extensionClass.Methods)
                {
                    if (string.Equals(extensionMethod.Name, "new", System.StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extensionMethod.Name, "TypeNew", System.StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    RuntimeClassMethod original = target.FindMethod(extensionMethod.Name);

                    if (original != null && original.IsStatic == extensionMethod.IsStatic)
                    {
                        target.ExtensionMethods.Add(extensionMethod);
                    }
                    else if (original is null && extensionMethod.Access == AccessModifier.Public)
                    {
                        // A public method added by an augmentation is part of the
                        // target's effective class even though it has no next link.
                        target.ExtensionMethods.Add(extensionMethod);
                    }
                }
            }

            foreach (RuntimeClass target in _declaredClasses)
            {
                foreach (var methodGroup in target.ExtensionMethods.GroupBy(method => method.Name, System.StringComparer.OrdinalIgnoreCase))
                {
                    List<RuntimeClassMethod> wrappers = methodGroup.ToList();
                    RuntimeClassMethod original = target.FindMethod(methodGroup.Key);

                    for (int index = 0; index < wrappers.Count; index++)
                    {
                        wrappers[index].NextMethod = original is null
                            ? null
                            : (index + 1 < wrappers.Count ? wrappers[index + 1] : original);
                    }
                }
            }

            // Link extensions whose target is a compiled AOS class/table. The
            // original implementation is not bytecode; the innermost wrapper
            // terminates in a native proxy call.
            foreach (var targetGroup in _declaredClasses
                .Where(runtimeClass => runtimeClass.HasCompiledExtensionTarget)
                .GroupBy(runtimeClass => runtimeClass.ExtensionTargetName, System.StringComparer.OrdinalIgnoreCase))
            {
                RuntimeClass root = targetGroup.First();
                foreach (RuntimeClass extensionClass in targetGroup)
                {
                    extensionClass.ExternalExtensionRoot = root;
                }

                var methods = targetGroup.SelectMany(runtimeClass => runtimeClass.Methods)
                    .Where(method => !string.Equals(method.Name, "new", System.StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(method.Name, "TypeNew", System.StringComparison.OrdinalIgnoreCase));

                foreach (var methodGroup in methods.GroupBy(
                    method => (method.IsStatic ? "S:" : "I:") + method.Name,
                    System.StringComparer.OrdinalIgnoreCase))
                {
                    List<RuntimeClassMethod> wrappers = methodGroup.ToList();
                    bool wrapsNative = wrappers.Any(method => method.WrapsCompiledTarget);

                    for (int index = 0; index < wrappers.Count; index++)
                    {
                        wrappers[index].NextMethod = index + 1 < wrappers.Count ? wrappers[index + 1] : null;
                        wrappers[index].NextCallsNative = wrapsNative && index == wrappers.Count - 1;
                        wrappers[index].NativeTargetName = targetGroup.Key;
                    }
                }
            }
        }

        public void VisitIf(If @if)
        {
            EmitDebugSymbol(@if);

            CreateScope();
            @if.Expression.Accept(this);
            var expressionScope = ReleaseScope();

            CreateScope();
            @if.Statement.Accept(this);
            var statementScope = ReleaseScope();

            ByteCodeGenerationScope elseScope = null;
            if (@if.Else != null)
            {
                CreateScope();
                @if.Else.Accept(this);
                elseScope = ReleaseScope();
            }

            EmitScope(expressionScope);

            if (elseScope != null)
            {
                Emit(new JumpIfFalse(statementScope.Count + 2));
            }
            else
            {
                Emit(new JumpIfFalse(statementScope.Count + 1));
            }

            EmitScope(statementScope);

            if (elseScope != null)
            {
                Emit(new Jump(elseScope.Count + 1));
                EmitScope(elseScope);
            }
        }

        public void VisitWhile(While @while)
        {
            EmitDebugSymbol(@while);

            CreateScope();
            @while.Expression.Accept(this);
            var expressionScope = ReleaseScope();

            CreateScope();
            @while.Block.Accept(this);
            var blockScope = ReleaseScope();

            CreateScope();
            if (_hasMaxIterations)
            {
                Emit(new MaxLoopThrow());
            }

            var loopCheckScope = ReleaseScope();

            int whileStartIndex = _ss.Peek().Instructions.Count;

            if (_hasMaxIterations)
            {
                Emit(new Push(Options.MaxLoopIterations));
            }

            EmitScope(expressionScope);
            Emit(new JumpIfFalse(loopCheckScope.Count + blockScope.Count + 1 + 1 /*Jump instruction*/));
            EmitScope(loopCheckScope);
            EmitScope(blockScope);
            Emit(new Jump(-(loopCheckScope.Count + blockScope.Count + expressionScope.Count) - 1));

            RecalculateLoopControlOffsets(expressionScope.Instructions.First(), whileStartIndex);
        }

        private void RecalculateLoopControlOffsets(IInstruction jumpInstructionForContinue, int fromIndex = 0)
        {
            // Move the pending loop control jumps. Only instructions emitted by the
            // construct being finalized (index >= fromIndex) are stamped, so a break
            // belonging to an enclosing loop that precedes a nested switch/loop in
            // the same scope stays dirty for the enclosing loop's own recalculation.
            var instructions = _ss.Peek().Instructions;

            for (int index = fromIndex; index < instructions.Count; index++)
            {
                if (!(instructions[index] is LoopControl loopControl) || !loopControl.IsDirty)
                {
                    continue;
                }

                if (loopControl.TokenType == TType.Break)
                {
                    loopControl.SetFinalOffset(instructions.Count - index);
                }
                else if (loopControl.TokenType == TType.Continue && jumpInstructionForContinue != null)
                {
                    loopControl.SetFinalOffset(-(index - instructions.IndexOf(jumpInstructionForContinue)));
                }
            }
        }

        public void VisitFunctionCall(FunctionCall functionCall)
        {
            EmitDebugSymbol(functionCall);
            VisitFunctionCall(functionCall, true);
        }

        private void VisitFunctionCall(FunctionCall functionCall, bool allocate)
        {
            for (var narg = functionCall.Parameters.Count - 1; narg >= 0; narg--)
            {
                functionCall.Parameters[narg].Accept(this);
            }

            int nArgs = functionCall.Parameters.Count;

            if (functionCall is SuperCallExpression superCall)
            {
                Emit(new SuperCall(_currentRuntimeClass, superCall.MethodName, nArgs, allocate));
                return;
            }

            if (functionCall is NextCallExpression)
            {
                Emit(new NextMethodCall(_currentRuntimeMethod, nArgs, allocate));
                return;
            }

            if (_declaredFunctions.Exists(f => f.Declaration.Name.ToLowerInvariant() == functionCall.Name.ToLowerInvariant()) && functionCall.Caller is null)
            {
                var declaredFunctionRef = _declaredFunctions.First(f => f.Declaration.Name.ToLowerInvariant() == functionCall.Name.ToLowerInvariant());
                Emit(new DeclaredFunctionCall(declaredFunctionRef, nArgs, functionCall.ByRefArguments));
            }
            else if (functionCall.Intrinsical)
            {
                Emit(new IntrinsicCall(functionCall.Name, nArgs, allocate));
            }
            else if (functionCall.Caller is null)
            {
                Emit(new StaticFunctionCall(functionCall.Name, nArgs, allocate));
            }
            else if (functionCall.StaticCall)
            {
                var staticCaller = functionCall.Caller as Variable;
                string callerName = ((Word)staticCaller.Token).Lexeme;
                Emit(new StaticFunctionCall(functionCall.Name, nArgs, allocate, callerName, staticCaller.ClrTypeReference, functionCall.ByRefArguments));
            }
            else
            {
                functionCall.Caller.Accept(this);
                Emit(new InstanceFunctionCall(functionCall.Name, nArgs, allocate, functionCall.ByRefArguments));
            }
        }

        public void VisitTernary(Ternary ternary)
        {
            EmitDebugSymbol(ternary);
            ternary.Condition.Accept(this);

            CreateScope();
            ternary.Left.Accept(this);
            var leftScope = ReleaseScope();

            CreateScope();
            ternary.Right.Accept(this);
            var rightScope = ReleaseScope();

            Emit(new JumpIfFalse(leftScope.Count + 1 + 1 /*Jump instruction */, false));
            EmitScope(leftScope);
            Emit(new Jump(rightScope.Count + 1));
            EmitScope(rightScope);
        }

        public void VisitUnaryOperation(UnaryOperation unaryOperation)
        {
            unaryOperation.Expression.Accept(this);

            switch (unaryOperation.Token.TokenType)
            {
                case TType.Negation:
                    Emit(new Negation());
                    break;

                case TType.Minus:
                    Emit(new Push(-1));
                    Emit(new Arithmetic(TType.Star));
                    break;

                case TType.BinaryNot:
                    Emit(new BinaryNot());
                    break;
            }
        }

        public void VisitVariable(Variable variable)
        {
            bool isArray = false;

            if (variable is ArrayAccess arrayAccess)
            {
                arrayAccess.Index.Accept(this);
                isArray = true;
            }

            if (variable.Caller is null)
            {
                Emit(new VariableLoad(variable.Name, isArray));
            }
            else if (!variable.StaticCall)
            {
                variable.Caller.Accept(this);
                Emit(new InstanceLoad(variable.Name, isArray));
            }
            else
            {
                string callerName = (variable.Caller.Token as Word).Lexeme;
                Emit(new StaticLoad(variable.Name, callerName, isArray, (variable.Caller as Variable)?.ClrTypeReference));
            }
        }

        public void VisitVariableDeclarations(VariableDeclarations variableDeclarations)
        {
            EmitDebugSymbol(variableDeclarations);
            foreach (var declaration in variableDeclarations.Identifiers)
            {
                if (declaration.Value != null)
                {
                    declaration.Value.Accept(this);
                }
                else if (variableDeclarations is VariableArrayDeclaration arrayDeclaration)
                {
                    arrayDeclaration.Size?.Accept(this);
                    Emit(new DefaultValueArray(variableDeclarations.DeclarationType.Lexeme, arrayDeclaration.Size != null));
                }
                else
                {
                    Emit(new DefaultValue(variableDeclarations.DeclarationType.Lexeme, variableDeclarations.DeclarationClrType));
                }

                Emit(new Store(declaration.Key.Lexeme, false, true, false, variableDeclarations.DeclarationType.Lexeme, variableDeclarations.DeclarationClrType));
            }
        }

        public void VisitBreakpoint(Parser.Breakpoint breakpoint)
        {
            EmitDebugSymbol(breakpoint, true);
        }

        public void VisitSwitch(Switch @switch)
        {
            EmitDebugSymbol(@switch);

            CreateScope();
            @switch.Expression.Accept(this);
            var expressionScope = ReleaseScope();

            var caseExpressionScopes = new List<ByteCodeGenerationScope>();
            var caseStatementScopes = new List<ByteCodeGenerationScope>();

            if (@switch.Cases != null)
            {
                foreach (var @case in @switch.Cases)
                {
                    CreateScope();
                    EmitDebugSymbol(@case.Key);
                    @case.Key.Accept(this);
                    // delete all debug symbols that are created by the expression itself
                    DeleteAllDebugSymbolsInScope(true);
                    caseExpressionScopes.Add(ReleaseScope());

                    CreateScope();
                    foreach (var caseStatement in @case.Value)
                    {
                        caseStatement.Accept(this);
                    }
                    caseStatementScopes.Add(ReleaseScope());
                }
            }

            CreateScope();
            foreach (var defaultStatement in @switch.Default ?? Enumerable.Empty<Statement>())
            {
                defaultStatement.Accept(this);
            }
            var defaultScope = ReleaseScope();

            // Two-phase layout for X++ fall-through semantics: all case labels are
            // tested first (switch value on the stack); a match jumps into a single
            // linear statement region so execution falls through subsequent case
            // bodies until a break
            int switchStartIndex = _ss.Peek().Instructions.Count;

            Emit(new BeginScope());

            EmitScope(expressionScope);

            int caseCount = caseExpressionScopes.Count;
            int dispatchLength = caseExpressionScopes.Sum(s => s.Count + 1) + 2; // + Pop + Jump-to-default
            int totalStatements = caseStatementScopes.Sum(s => s.Count);

            int emittedDispatch = 0;
            int precedingStatements = 0;

            for (int numCase = 0; numCase < caseCount; numCase++)
            {
                EmitScope(caseExpressionScopes[numCase]);
                emittedDispatch += caseExpressionScopes[numCase].Count + 1;

                int jumpPosition = emittedDispatch - 1;
                int targetPosition = dispatchLength + precedingStatements;

                Emit(new CaseJumpIfEqual(targetPosition - jumpPosition));

                precedingStatements += caseStatementScopes[numCase].Count;
            }

            // No case matched: discard the switch value and run the default section
            Emit(new Pop());
            Emit(new Jump(dispatchLength + totalStatements - (dispatchLength - 1)));

            foreach (var statementScope in caseStatementScopes)
            {
                EmitScope(statementScope);
            }

            EmitScope(defaultScope);

            RecalculateLoopControlOffsets(null, switchStartIndex);

            Emit(new EndScope());
        }

        private void DeleteAllDebugSymbolsInScope(bool skipFirst)
        {
            var instructions = _ss.Peek().Instructions.Where(ins => ins is Debug).Reverse();
            int lastIndex = skipFirst ? 1 : 0;

            int total = instructions.Count();
            foreach (var instruction in instructions)
            {
                total--;

                if (skipFirst && total == 0)
                {
                    break;
                }

                _ss.Peek().Instructions.Remove(instruction);
            }
        }

        public void VisitFunctionDeclaration(FunctionDeclaration functionDeclaration)
        {
            // Create dummy function
            RefFunction refFunction = new RefFunction(functionDeclaration);
            _declaredFunctions.Add(refFunction);

            CreateScope();
            functionDeclaration.Block.Accept(this);
            var blockScope = ReleaseScope();

            refFunction.Instructions = blockScope.Instructions;
        }

        public void VisitReturn(Parser.Return @return)
        {
            EmitDebugSymbol(@return);
            @return.Expression?.Accept(this);
            Emit(new Return());
        }

        public void VisitPrint(Print print)
        {
            EmitDebugSymbol(print);

            // Print call is translated into strFmt and info calls
            StringBuilder sb = new StringBuilder();
            for (var narg = print.Parameters.Count - 1; narg >= 0; narg--)
            {
                if (narg != print.Parameters.Count - 1)
                {
                    sb.Append(' ');
                }

                sb.Append($"%{print.Parameters.Count - narg}");
                print.Parameters[narg].Accept(this);
            }

            Emit(new Push(sb.ToString()));
            Emit(new StaticFunctionCall("strFmt", print.Parameters.Count + 1, true));
            Emit(new StaticFunctionCall("info", 1, false));
        }

        public void VisitIs(Parser.Is @is)
        {
            @is.Expression.Accept(this);
            Emit(new Is(@is.TypeName, @is.ResolvedClrType));
        }

        public void VisitAs(Parser.As @as)
        {
            @as.Expression.Accept(this);
            Emit(new As(@as.TypeName, @as.ResolvedClrType));
        }

        public void VisitTry(Try @try)
        {
            EmitTry(@try);
        }

        private void EmitTry(Try @try, IInstruction finallyInstruction = null)
        {
            var catchBlockScopes = new List<System.Tuple<string, ByteCodeGenerationScope>>(@try.Catches.Count);

            int totalCatchInstructions = 0;
            foreach (var @catch in @try.Catches)
            {
                CreateScope();
                @catch.Block.Accept(this);
                ByteCodeGenerationScope catchScope = ReleaseScope();
                catchBlockScopes.Add(new System.Tuple<string, ByteCodeGenerationScope>(@catch.ExceptionMember, catchScope));
                totalCatchInstructions += catchScope.Count;
            }

            int tryOffset = totalCatchInstructions + catchBlockScopes.Count + 1 /* Jump instruction */;
            CreateScope();
            @try.TryBlock.Accept(this);
            var tryScope = ReleaseScope();

            var handler = new BeginHandleException(tryOffset + tryScope.Count + 1);
            Emit(handler);
            EmitScope(tryScope);
            Emit(new Jump(tryOffset));

            int accumulatedCatchOffset = tryScope.Count + 1 + 1 /* Jump instruction*/;
            for (int i = 0; i < catchBlockScopes.Count; i++)
            {
                ByteCodeGenerationScope currentCatchScope = catchBlockScopes[i].Item2;

                totalCatchInstructions -= currentCatchScope.Count;
                int offset = totalCatchInstructions + (catchBlockScopes.Count - i) /* Jump instructions */;

                EmitScope(currentCatchScope);
                Emit(new Jump(offset));

                if (i > 0)
                {
                    accumulatedCatchOffset += catchBlockScopes[i - 1].Item2.Count + 1 /* Jump instruction */;
                }

                handler.CatchReferences.Add(new ExceptionCatchReference(catchBlockScopes[i].Item1, accumulatedCatchOffset));
            }

            Emit(new EndHandleException(@try.HasFinally));

            bool emitFinally = @try.HasFinally || finallyInstruction != null;

            if (@try.HasFinally)
            {
                @try.Finally.Accept(this);
            }
            else if (finallyInstruction != null)
            {
                Emit(finallyInstruction);
            }

            if (emitFinally)
            {
                Emit(new Finally());
            }
        }

        public void VisitUsing(Using @using)
        {
            /* using (var a = value) 
             * {
             *     ...
             * } 
             * is equial to:
             * var a = value;
             * try
             * {
             *    ...
             * }
             * finally { a.Dispose(); } 
             */

            Emit(new BeginScope());
            @using.VariableDeclaration.Accept(this);

            // Construct placeholder try
            Try @try = new Try(@using.Block, @using.Block.SourceCodeBinding);
            EmitTry(@try, new Dispose(@using.VariableName));
            Emit(new EndScope());
        }

        public void VisitRetry(Parser.Retry retry)
        {
            EmitDebugSymbol(retry);
            Emit(new Retry());
        }

        public void VisitTableField(TableField tableField)
        {

        }

        public void VisitEventHandlerSubscription(EventHandlerSubscription subscription)
        {
            EmitDebugSymbol(subscription);
            subscription.Delegate.Accept(this);

            RefFunction refFunction = null;

            if (subscription.EventHandler.Kind == Parser.EventHandlerKind.LocalFunction)
            {
                refFunction = _declaredFunctions.First(f => f.Declaration.Name.ToLowerInvariant() == subscription.EventHandler.FunctionName.ToLowerInvariant());
            }

            Emit(new EventSubscribe(
                refFunction,
                subscription.EventHandler.Kind,
                subscription.EventHandler.FunctionName,
                subscription.EventHandler.CallerName,
                subscription.Unsubscribe));
        }

        public void VisitClassDeclaration(ClassDeclaration classDeclaration)
        {
            // Flatten interface names (including base interfaces of implemented
            // interfaces) so runtime is/as checks see the whole closure
            List<string> implementsClosure = classDeclaration.ImplementsNames;

            if (classDeclaration.Info != null)
            {
                var interfaceNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                classDeclaration.Info.CollectInterfaceNames(interfaceNames);
                implementsClosure = interfaceNames.ToList();
            }

            var runtimeClass = new RuntimeClass
            {
                Name = classDeclaration.Name,
                BaseClassName = classDeclaration.BaseClassName,
                ImplementsNames = implementsClosure,
                CompiledInterfaces = classDeclaration.Info?.CompiledInterfaces.ToList() ?? new List<System.Type>(),
                IsAbstract = classDeclaration.IsAbstract,
                IsFinal = classDeclaration.IsFinal,
                IsStatic = classDeclaration.IsStatic,
                ExtensionTargetName = classDeclaration.ExtensionTargetName,
                DelegateNames = classDeclaration.Delegates.Select(d => d.Name).ToList(),
                DelegateDeclarations = classDeclaration.Delegates.ToList(),
                Attributes = classDeclaration.Attributes.ToList(),
                HasCompiledBase = classDeclaration.Info?.CompiledBaseType != null,
                HasCompiledExtensionTarget = classDeclaration.Info?.ExtensionTarget?.CompiledTypeDescriptor != null,
                CompiledExtensionTargetKind = classDeclaration.Info?.ExtensionTarget?.CompiledTypeDescriptor?.Kind ??
                    Interpreter.Metadata.AosTypeKind.Unknown,
            };

            if (classDeclaration.BaseClassName != null)
            {
                runtimeClass.BaseClass = _declaredClasses.FirstOrDefault(c =>
                    string.Equals(c.Name, classDeclaration.BaseClassName, System.StringComparison.OrdinalIgnoreCase));
            }

            _declaredClasses.Add(runtimeClass);

            if (classDeclaration.Info != null)
            {
                foreach (var field in classDeclaration.Info.Fields)
                {
                    runtimeClass.Fields.Add(new RuntimeClassField
                    {
                        Name = field.Name,
                        TypeName = field.Type?.Lexeme,
                        ClrType = field.ClrType,
                        IsStatic = field.IsStatic,
                        IsConst = field.IsConst,
                        Access = field.Access,
                        DeclaringClass = runtimeClass,
                        Attributes = field.Attributes,
                    });
                }
            }

            // Field initializers are compiled out-of-line and run at instantiation
            // (instance) or on first access (static)
            foreach (var field in classDeclaration.Fields)
            {
                CreateScope();
                field.Declaration.Accept(this);
                var fieldScope = ReleaseScope();

                var target = field.Modifiers.IsStatic ? runtimeClass.StaticFieldInitInstructions : runtimeClass.FieldInitInstructions;
                target.AddRange(fieldScope.Instructions);
            }

            // Method bodies are compiled out-of-line, like declared functions
            foreach (var method in classDeclaration.Methods)
            {
                var runtimeMethod = new RuntimeClassMethod
                {
                    Name = method.Name,
                    IsStatic = method.Modifiers.IsStatic,
                    IsAbstract = method.Modifiers.IsAbstract,
                    IsFinal = method.Modifiers.IsFinal,
                    Access = method.Modifiers.Access ?? (method.IsConstructor ? Parser.AccessModifier.Public : Parser.AccessModifier.Public),
                    ReturnType = method.ReturnType,
                    Parameters = method.Parameters,
                    DeclaringClass = runtimeClass,
                    RequiredParameterCount = method.ParameterDefaults.Count(value => value is null),
                    Attributes = method.Attributes,
                    WrapsCompiledTarget = classDeclaration.Info?.ExtensionTarget?.CompiledTypeDescriptor != null &&
                        classDeclaration.Info.ExtensionTarget.FindMethod(method.Name, walkBaseChain: false) != null,
                };

                foreach (var defaultExpression in method.ParameterDefaults)
                {
                    if (defaultExpression is Constant constant)
                    {
                        object value = null;

                        if (constant.Token is BaseType baseType)
                        {
                            value = baseType.Value;
                        }
                        else if (constant.Token.TokenType == TType.True)
                        {
                            value = true;
                        }
                        else if (constant.Token.TokenType == TType.False)
                        {
                            value = false;
                        }

                        runtimeMethod.ParameterDefaults.Add(value);
                        runtimeMethod.ParameterHasDefault.Add(true);
                    }
                    else
                    {
                        runtimeMethod.ParameterDefaults.Add(null);
                        runtimeMethod.ParameterHasDefault.Add(false);
                    }
                }

                runtimeClass.Methods.Add(runtimeMethod);
            }

            // Bodies are compiled after all methods are registered so calls between
            // methods of the class resolve regardless of declaration order
            for (int numMethod = 0; numMethod < classDeclaration.Methods.Count; numMethod++)
            {
                var method = classDeclaration.Methods[numMethod];
                var runtimeMethod = runtimeClass.Methods[numMethod];

                // Abstract methods have no body to compile
                if (method.Block is null)
                {
                    continue;
                }

                var currentClassSave = _currentRuntimeClass;
                var currentMethodSave = _currentRuntimeMethod;
                _currentRuntimeClass = runtimeClass;
                _currentRuntimeMethod = runtimeMethod;

                CreateScope();
                method.Block.Accept(this);
                runtimeMethod.Instructions = ReleaseScope().Instructions;

                _currentRuntimeClass = currentClassSave;
                _currentRuntimeMethod = currentMethodSave;
            }

            foreach (RuntimeClassMethod attributedMethod in runtimeClass.Methods)
            {
                foreach (XppAttributeDeclaration attribute in attributedMethod.Attributes.Where(candidate =>
                    AttributedEventSubscribe.IsEventAttribute(candidate.Name)))
                {
                    Emit(new AttributedEventSubscribe(attributedMethod, attribute));
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using XppInterpreter.Core;
using XppInterpreter.Lexer;
using XppInterpreter.Parser.Metadata;

namespace XppInterpreter.Parser
{
    public partial class XppParser : IParser
    {
        private Token currentToken;

        private IScanResult lastScanResult;
        private IScanResult currentScanResult;
        private bool hasBeenInitialized = false;
        private int currentPeekOffset = -1;
        private bool validateReturnType = true;

        private ParseContext _parseContext = new ParseContext();
        private readonly ILexer _lexer;
        private readonly Interpreter.Proxy.XppProxy _proxy;
        private readonly ParseErrorCollection _parseErrors = new ParseErrorCollection();

        private bool _forAutoCompletion;
        private bool _forMetadata;
        private AutoCompletionPurpose _autoCompletionPurpose;
        private int _stopAtRow, _stopAtColumn;
        private XppTypeInferer _typeInferer = null;
        private ClrTypeResolver _clrResolver = null;
        private readonly Interpreter.Metadata.AosTypeDescriptorProvider _aosTypes;
        IScanResult AdvancePeek(bool reset = false)
        {
            currentPeekOffset++;
            var ret = _lexer.Peek(currentPeekOffset);
            if (reset)
            {
                ResetPeek();
            }
            return ret;
        }

        void ResetPeek()
        {
            currentPeekOffset = -1;
        }

        public XppParser(ILexer lexer, Interpreter.Proxy.XppProxy xppProxy)
        {
            _lexer = lexer;
            _proxy = xppProxy;
            _aosTypes = new Interpreter.Metadata.AosTypeDescriptorProvider(xppProxy);
        }

        public static ParseResult ParseExpressionWithContext(ILexer lexer, Interpreter.Proxy.XppProxy xppProxy, ParseContext parseContext)
        {
            var parser = new XppParser(lexer, xppProxy);
            parser._parseContext = parseContext;
            parser.validateReturnType = false;
            parser.Initialize();
            Expression expression = null;
            try
            {
                expression = parser.Expression();
            }
            catch (ParseException ex)
            {
                parser._parseErrors.Add(new ParseError(ex.Token, -1, -1, ex.Message));
            }

            return new ParseResult(expression, parser._parseErrors);
        }

        public ParseResult Parse()
        {
            Initialize();

            Program program = null;

            try
            {
                program = Program();
            }
            catch (ParseException ex)
            {
                _parseErrors.Add(new ParseError(ex.Token, ex.Line, ex.Position, ex.Message));
            }

            return new ParseResult(program, _parseErrors);
        }

        public TokenMetadata GetTokenMetadataAt(int row, int column, bool isMethodParameters)
        {
            _forMetadata = true;
            _stopAtRow = row;
            _stopAtColumn = column;

            try
            {
                Parse();
            }
            catch (MetadataInterruption interruption)
            {
                return interruption.TokenData;
            }
            catch
            {
                return null;
            }

            return null;
        }

        public System.Type ParseForAutoCompletion(int row, int column, AutoCompletionPurpose purpose)
        {
            _forAutoCompletion = true;
            _stopAtRow = row;
            _stopAtColumn = column;
            _autoCompletionPurpose = purpose;

            try
            {
                Parse();
            }
            catch (AutoCompletionTypeInterruption interruption)
            {
                return interruption.InferedType;
            }
            catch
            {
                return null;
            }

            return null;
        }

        internal Program Program()
        {
            var startResult = currentScanResult;
            var stmts = new List<Statement>();

            while (currentToken.TokenType != TType.EOF)
            {
                stmts.Add(Statement());
            }

            return new Program(stmts, SourceCodeBinding(startResult, lastScanResult ?? startResult));
        }

        internal Block Block()
        {
            _parseContext.BeginScope();

            var start = Match(TType.LeftBrace);
            var stmts = new List<Statement>();

            while (currentToken.TokenType != TType.RightBrace && currentToken.TokenType != TType.EOF)
            {
                stmts.Add(Statement());
            }

            var end = Match(TType.RightBrace);

            _parseContext.EndScope();

            return new Block(stmts, SourceCodeBinding(start, end), DebuggeableBinding(start));
        }

        internal Constructor Constructor()
        {
            var start = currentScanResult;

            Match(TType.New);
            Word identifier = (Word)Match(TType.Id).Token;

            // Namespace-qualified constructor (new System.Text.StringBuilder())
            if (currentToken.TokenType == TType.Dot)
            {
                var qualifiedName = new System.Text.StringBuilder(identifier.Lexeme);

                while (currentToken.TokenType == TType.Dot)
                {
                    Match(TType.Dot);
                    qualifiedName.Append('.');
                    qualifiedName.Append(((Word)Match(TType.Id).Token).Lexeme);
                }

                identifier = new Word(qualifiedName.ToString(), TType.Id);
            }

            var constructor = new Constructor(
                identifier,
                Parameters(null, identifier.Lexeme, false, true),
                null,
                false,
                SourceCodeBinding(start, lastScanResult),
                SourceCodeBinding(start, lastScanResult));

            constructor.ResolvedClrType = ResolveClrOnlyType(identifier.Lexeme);

            var scriptInfo = _parseContext.DeclaredClasses.Find(identifier.Lexeme);

            if (scriptInfo != null && scriptInfo.IsInterface)
            {
                HandleParseError($"Cannot instantiate interface {identifier.Lexeme}.", stop: false);
            }
            else if (scriptInfo != null)
            {
                if (scriptInfo.IsAbstract)
                {
                    HandleParseError($"Cannot instantiate abstract class {identifier.Lexeme}.", stop: false);
                }

                if (scriptInfo.IsStatic)
                {
                    HandleParseError($"Cannot instantiate static class {identifier.Lexeme}.", stop: false);
                }

                if (!string.IsNullOrEmpty(scriptInfo.ExtensionTargetName))
                {
                    HandleParseError($"Extension class {identifier.Lexeme} cannot be instantiated explicitly.", stop: false);
                }

                var scriptConstructor = scriptInfo.Methods.FirstOrDefault(method =>
                    string.Equals(method.Name, "new", StringComparison.OrdinalIgnoreCase));

                if (scriptConstructor is null)
                {
                    if (constructor.Parameters.Count != 0)
                    {
                        HandleParseError($"The implicit constructor of {identifier.Lexeme} takes no arguments.", stop: false);
                    }
                }
                else
                {
                    if (!CanAccessMember(scriptConstructor.Access, scriptInfo))
                    {
                        HandleParseError($"Constructor {identifier.Lexeme}.new is not accessible from the current context.", stop: false);
                    }

                    ValidateArgumentCount(identifier.Lexeme, scriptConstructor, constructor.Parameters.Count);
                }
            }

            return constructor;
        }

        /// <summary>
        /// Implicit conversion check that understands script-declared class types
        /// before delegating to the X++ casting proxy
        /// </summary>
        internal bool ImplicitConversionExists(System.Type from, System.Type to)
        {
            if (from is Core.ScriptClassType fromScript)
            {
                ClassDeclarationInfo fromInfo = (ClassDeclarationInfo)fromScript.DeclarationInfo;

                if (to is Core.ScriptClassType toScript)
                {
                    return fromInfo.IsOrExtends((ClassDeclarationInfo)toScript.DeclarationInfo);
                }

                return to == typeof(object) || fromInfo.IsAssignableToCompiledType(to);
            }

            if (to is Core.ScriptClassType)
            {
                return from is null || from == typeof(object);
            }

            // CLR numeric conversions (e.g. System.Math::Pow returning double into a
            // real) are handled before consulting X++ metadata
            if (from != null && to != null &&
                Interpreter.ClrBridge.IsNumericType(from) && Interpreter.ClrBridge.IsNumericType(to))
            {
                return true;
            }

            return _proxy.Casting.ImplicitConversionExists(from, to);
        }

        /// <summary>
        /// Resolves a name to a .NET type only when neither the X++ metadata proxy
        /// nor the script class table knows it. Used to route instantiation and
        /// static access of such types through direct CLR reflection.
        /// </summary>
        internal System.Type ResolveClrOnlyType(string name)
        {
            if (_parseContext.DeclaredClasses.Contains(name))
            {
                return null;
            }

            try
            {
                if (_proxy.Casting.GetSystemTypeFromTypeName(name) != null)
                {
                    return null;
                }
            }
            catch
            {
                // Proxy failures are treated as "unknown to X++"
            }

            return _clrResolver?.Resolve(name);
        }

        internal List<string> IntrinsicParameters(string methodName)
        {
            List<string> literalParameters = new List<string>();
            var start = Match(TType.LeftParenthesis);

            int parameterCount = 0;

            HandleMetadataInterruption(start.Line, start.Start, start.Start + 1, null, methodName, parameterCount, true, false, false);

            while (currentToken.TokenType != TType.RightParenthesis)
            {
                var parameter = MatchMultiple(TType.Id, TType.String).Token;

                string literalValue = string.Empty;

                if (parameter is Word word)
                {
                    literalValue = word.Lexeme;
                }
                else if (parameter is Lexer.String str)
                {
                    literalValue = (string)str.Value;
                }

                literalParameters.Add(literalValue);

                if (currentToken.TokenType != TType.RightParenthesis)
                {
                    var comma = Match(TType.Comma);
                    parameterCount ++;
                    HandleMetadataInterruption(comma.Line, comma.Start, comma.Start + 1, null, methodName, parameterCount, true, false, false);
                }
            }

            Match(TType.RightParenthesis);

            return literalParameters;
        }

        internal List<Expression> Parameters(Expression caller, string tokenName, bool isStatic, bool isConstructor)
        {
            return Parameters(caller, tokenName, isStatic, isConstructor, out _);
        }

        internal List<Expression> Parameters(Expression caller, string tokenName, bool isStatic, bool isConstructor, out List<KeyValuePair<int, string>> byRefArguments)
        {
            List<Expression> parameters = new List<Expression>();
            byRefArguments = null;

            IScanResult start = Match(TType.LeftParenthesis);

            int parameterCount = 0;

            HandleMetadataInterruption(start.Line, start.Start, start.Start + 1, caller, tokenName, parameterCount, false, isStatic, isConstructor);

            while (currentToken.TokenType != TType.RightParenthesis)
            {
                // byref marks a .NET ref/out argument; the variable is written back
                // after the call
                bool isByRef = false;

                if (currentToken.TokenType == TType.ByRef)
                {
                    Match(TType.ByRef);
                    isByRef = true;
                }

                Expression argument = Expression();

                if (isByRef)
                {
                    if (argument is Variable byRefVariable && !(argument is FunctionCall) && !(argument is ArrayAccess) && byRefVariable.Caller is null)
                    {
                        if (byRefArguments is null)
                        {
                            byRefArguments = new List<KeyValuePair<int, string>>();
                        }

                        byRefArguments.Add(new KeyValuePair<int, string>(parameters.Count, byRefVariable.Name));
                    }
                    else
                    {
                        HandleParseError("byref arguments must be assignable variables.", stop: false);
                    }
                }

                parameters.Add(argument);

                if (currentToken.TokenType != TType.RightParenthesis)
                {
                    var comma = Match(TType.Comma);

                    parameterCount++;
                    HandleMetadataInterruption(comma.Line, comma.Start, comma.Start + 1, caller, tokenName, parameterCount, false, isStatic, isConstructor);
                }
            }

            Match(TType.RightParenthesis);

            return parameters;
        }

        internal Expression IntrinsicFunction(string functionName)
        {
            var start = lastScanResult;
            HandleMetadataInterruption(start.Line, start.Start, start.End, start.Token, TokenMetadataType.IntrinsicMethod);

            var parameters = IntrinsicParameters(functionName);
            object result;
            Expression ret = null;
            System.Reflection.MethodInfo intrinsicMethod = typeof(Interpreter.Proxy.IIntrinsicFunctionProvider)
                .GetMethods()
                .First(method => string.Equals(method.Name, functionName, StringComparison.OrdinalIgnoreCase));

            try
            {
                if (!_forAutoCompletion && !_forMetadata)
                {
                    // Runtime artifacts occupy a parallel metadata namespace. If
                    // the target is interpreted, validate and fold the intrinsic
                    // here; otherwise retain the native AOT provider path.
                    if (!TryRuntimeArtifactIntrinsic(functionName, parameters, out result))
                    {
                        result = Interpreter.Proxy.XppProxyHelper.CallIntrinsicFunction(_proxy.Intrinsic, functionName, parameters.ToArray<object>());
                    }
                }
                else
                {
                    result = DefaultIntrinsicResult(functionName, intrinsicMethod.ReturnType);
                }
            }
            catch (Exception ex)
            {
                result = DefaultIntrinsicResult(functionName, intrinsicMethod.ReturnType);
                string message = ex.Message;
                if (ex.InnerException != null)
                {
                    message = ex.InnerException.Message;
                }

                HandleParseError(message, false, false);
            }

            if (result is null)
            {
                result = DefaultIntrinsicResult(functionName, intrinsicMethod.ReturnType);
            }

            var binding = SourceCodeBinding(start, lastScanResult);

            if (result is int intValue)
            {
                ret = new Constant(intValue, binding);
            }
            else if (result is string strValue)
            {
                ret = new Constant(strValue, binding);
            }
            else if (result is object[] conValue)
            {
                ret = new Constant(conValue, binding);
            }
            else if (result is long longValue)
            {
                ret = new Constant(longValue, binding);
            }
            else if (result is decimal decimalValue)
            {
                ret = new Constant(decimalValue, binding);
            }
            else if (result is bool boolValue)
            {
                ret = new Constant(boolValue, binding);
            }
            else if (result?.GetType() == _proxy.Reflection.GetDateType())
            {
                ret = new Constant(new BaseType(result, TType.Date), binding);
            }

            if (ret is null)
            {
                ret = new Constant(Word.Null, binding);
            }

            return ret;
        }

        private bool TryRuntimeArtifactIntrinsic(string functionName, IList<string> parameters, out object result)
        {
            result = null;
            if (parameters is null || parameters.Count == 0) return false;

            ClassDeclarationInfo info = _parseContext.DeclaredClasses.Find(parameters[0]);
            if (info is null) return false;

            string function = functionName.ToLowerInvariant();
            Interpreter.Metadata.DynamicModelKind kind = RuntimeArtifactKind(info);

            switch (function)
            {
                case "classstr":
                    result = info.Name;
                    return true;
                case "classnum":
                    result = StableRuntimeId("class", info.Name);
                    return true;
                case "tablestr":
                case "tablepname":
                    RequireRuntimeKind(info, kind, Interpreter.Metadata.DynamicModelKind.Table,
                        Interpreter.Metadata.DynamicModelKind.DataEntity, Interpreter.Metadata.DynamicModelKind.View);
                    result = info.Name;
                    return true;
                case "tablenum":
                    RequireRuntimeKind(info, kind, Interpreter.Metadata.DynamicModelKind.Table,
                        Interpreter.Metadata.DynamicModelKind.DataEntity, Interpreter.Metadata.DynamicModelKind.View);
                    result = StableRuntimeId("table", info.Name);
                    return true;
                case "fieldstr":
                case "fieldpname":
                    RequireRuntimeField(info, parameters);
                    result = parameters[1];
                    return true;
                case "fieldnum":
                    RequireRuntimeField(info, parameters);
                    result = StableRuntimeId("field", info.Name, parameters[1]);
                    return true;
                case "indexstr":
                    RequireRuntimeIndex(info, parameters);
                    result = parameters[1];
                    return true;
                case "indexnum":
                    RequireRuntimeIndex(info, parameters);
                    result = StableRuntimeId("index", info.Name, parameters[1]);
                    return true;
                case "tablefieldgroupstr":
                    RequireRuntimeNamedAttribute(info, parameters, "RuntimeFieldGroup");
                    result = parameters[1];
                    return true;
                case "methodstr":
                case "staticmethodstr":
                case "tablemethodstr":
                case "tablestaticmethodstr":
                case "serviceoperationstr":
                case "formmethodstr":
                    RequireRuntimeMethod(info, parameters);
                    result = parameters[1];
                    return true;
                case "delegatestr":
                case "staticdelegatestr":
                    if (parameters.Count < 2 || info.FindDelegate(parameters[1]) is null)
                        throw new Exception($"Runtime artifact {info.Name} does not contain delegate {Parameter(parameters, 1)}.");
                    result = parameters[1];
                    return true;
                case "formstr":
                    RequireRuntimeKind(info, kind, Interpreter.Metadata.DynamicModelKind.Form);
                    result = info.Name;
                    return true;
                case "mapstr":
                    RequireRuntimeKind(info, kind, Interpreter.Metadata.DynamicModelKind.Map);
                    result = info.Name;
                    return true;
                case "viewstr":
                    RequireRuntimeKind(info, kind, Interpreter.Metadata.DynamicModelKind.View);
                    result = info.Name;
                    return true;
                case "servicestr":
                    RequireRuntimeKind(info, kind, Interpreter.Metadata.DynamicModelKind.Service);
                    result = info.Name;
                    return true;
                case "reportstr":
                case "ssrsreportstr":
                    RequireRuntimeKind(info, kind, Interpreter.Metadata.DynamicModelKind.Report);
                    result = info.Name;
                    return true;
                case "dataentitystr":
                case "dataentityviewstr":
                    RequireRuntimeKind(info, kind, Interpreter.Metadata.DynamicModelKind.DataEntity,
                        Interpreter.Metadata.DynamicModelKind.View);
                    result = info.Name;
                    return true;
                case "dataentitydatasourcestr":
                    RequireRuntimeNamedAttribute(info, parameters, "RuntimeDataSource");
                    result = parameters[1];
                    return true;
                case "formcontrolstr":
                    RequireRuntimeNamedAttribute(info, parameters, "RuntimeControl");
                    result = parameters[1];
                    return true;
                case "formdatasourcestr":
                    RequireRuntimeNamedAttribute(info, parameters, "RuntimeDataSource");
                    result = parameters[1];
                    return true;
                case "formdatafieldstr":
                    RequireRuntimeNamedAttribute(info, parameters, "RuntimeDataSource");
                    if (parameters.Count < 3) throw new Exception($"formDataFieldStr requires form, data source, and field names.");
                    result = parameters[2];
                    return true;
                default:
                    return false;
            }
        }

        private static Interpreter.Metadata.DynamicModelKind RuntimeArtifactKind(ClassDeclarationInfo info)
        {
            foreach (XppAttributeDeclaration attribute in info.Attributes)
            {
                string name = NormalizeRuntimeAttribute(attribute.Name);
                switch (name.ToLowerInvariant())
                {
                    case "runtimetable": return Interpreter.Metadata.DynamicModelKind.Table;
                    case "runtimedataentity": return Interpreter.Metadata.DynamicModelKind.DataEntity;
                    case "runtimeform": return Interpreter.Metadata.DynamicModelKind.Form;
                    case "runtimereport": return Interpreter.Metadata.DynamicModelKind.Report;
                    case "runtimeservice": return Interpreter.Metadata.DynamicModelKind.Service;
                    case "runtimeview": return Interpreter.Metadata.DynamicModelKind.View;
                    case "runtimemap": return Interpreter.Metadata.DynamicModelKind.Map;
                    case "runtimemodel":
                        if (attribute.Words.Count > 1 && Enum.TryParse(attribute.Words[1], true,
                            out Interpreter.Metadata.DynamicModelKind parsed)) return parsed;
                        break;
                }
            }
            return Interpreter.Metadata.DynamicModelKind.ScriptClass;
        }

        private static void RequireRuntimeKind(
            ClassDeclarationInfo info,
            Interpreter.Metadata.DynamicModelKind actual,
            params Interpreter.Metadata.DynamicModelKind[] expected)
        {
            if (!expected.Contains(actual))
            {
                throw new Exception($"Runtime artifact {info.Name} is {actual}, not {string.Join(" or ", expected)}.");
            }
        }

        private static void RequireRuntimeField(ClassDeclarationInfo info, IList<string> parameters)
        {
            string fieldName = Parameter(parameters, 1);
            if (info.FindField(fieldName) is null)
            {
                throw new Exception($"Runtime artifact {info.Name} does not contain field {fieldName}.");
            }
        }

        private static void RequireRuntimeMethod(ClassDeclarationInfo info, IList<string> parameters)
        {
            string methodName = Parameter(parameters, 1);
            if (info.FindMethod(methodName) is null)
            {
                throw new Exception($"Runtime artifact {info.Name} does not contain method {methodName}.");
            }
        }

        private static void RequireRuntimeIndex(ClassDeclarationInfo info, IList<string> parameters)
        {
            RequireRuntimeNamedAttribute(info, parameters, "RuntimeIndex");
        }

        private static void RequireRuntimeNamedAttribute(ClassDeclarationInfo info, IList<string> parameters, string attributeName)
        {
            string memberName = Parameter(parameters, 1);
            bool found = info.Attributes.Any(attribute =>
                string.Equals(NormalizeRuntimeAttribute(attribute.Name), attributeName, StringComparison.OrdinalIgnoreCase) &&
                attribute.Words.Skip(1).Any(word => string.Equals(word, memberName, StringComparison.OrdinalIgnoreCase)));
            if (!found)
            {
                throw new Exception($"Runtime artifact {info.Name} does not define {attributeName} {memberName}.");
            }
        }

        private static string Parameter(IList<string> parameters, int index)
        {
            if (parameters.Count <= index || string.IsNullOrEmpty(parameters[index]))
                throw new Exception($"Runtime metadata intrinsic is missing parameter {index + 1}.");
            return parameters[index];
        }

        private static string NormalizeRuntimeAttribute(string name)
        {
            const string suffix = "Attribute";
            return name != null && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - suffix.Length)
                : name ?? string.Empty;
        }

        private static int StableRuntimeId(params string[] parts)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in string.Join("\u001f", parts).ToUpperInvariant())
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                int value = (int)(hash & 0x7fffffff);
                return value == 0 ? -1 : -value;
            }
        }

        private object DefaultIntrinsicResult(string functionName, System.Type returnType)
        {
            if (returnType == typeof(string)) return string.Empty;
            if (returnType == typeof(int)) return 0;
            if (returnType == typeof(long)) return 0L;
            if (returnType == typeof(bool)) return false;
            if (returnType == typeof(decimal)) return 0m;
            if (string.Equals(functionName, "conNull", StringComparison.OrdinalIgnoreCase)) return new object[0];
            if (string.Equals(functionName, "dateNull", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(functionName, "maxDate", StringComparison.OrdinalIgnoreCase))
            {
                return _proxy.Casting.GetDefaultValueForType("date");
            }
            return null;
        }

        internal Expression Variable(Expression caller = null, bool staticCall = false, bool expectReturn = true)
        {
            IScanResult identifier = caller is null ? MatchIdentifier() : MatchAnyWord();

            Expression ret;
            SourceCodeBinding debuggeableStartBinding = caller?.SourceCodeBinding ?? new SourceCodeBinding(identifier.Line, identifier.Start, 0, 0);
            bool validateVariableName = false;

            if (caller is Interpreter.Debug.IDebuggeable debuggeable)
            {
                debuggeable.DebuggeableBinding = null;
            }

            if (currentToken.TokenType == TType.LeftParenthesis)
            {
                bool isInsideFunctionScope = _parseContext.CallFunctionScope.Empty;

                _parseContext.CallFunctionScope.New();

                string functionName = (identifier.Token as Word).Lexeme;

                // super(...) inside a class method calls the base implementation
                if (caller is null && _parseContext.InClassMethod &&
                    string.Equals(functionName, "super", StringComparison.OrdinalIgnoreCase))
                {
                    // 'this' is only registered for instance methods
                    if (_parseContext.CurrentScope.FindVariableDeclaration("this") is null)
                    {
                        HandleParseError("super() can only be used in instance methods.", stop: false);
                    }
                    else if (!string.Equals(_currentMethodName, "new", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_currentClassInfo?.BaseClass is null && _currentClassInfo?.CompiledBaseType is null)
                        {
                            HandleParseError("super() requires the class to extend another class.", stop: false);
                        }
                        else if (_currentClassInfo.BaseClass != null &&
                                 _currentClassInfo.BaseClass.FindMethod(_currentMethodName) is null &&
                                 _currentClassInfo.BaseClass.FindCompiledBase() is null)
                        {
                            HandleParseError($"super(): no base implementation of {_currentMethodName} exists.", stop: false);
                        }
                    }

                    var superParameters = Parameters(null, "super", false, false);

                    var superCall = new SuperCallExpression(
                        _currentClassInfo,
                        _currentMethodName,
                        superParameters,
                        SourceCodeBinding(identifier, lastScanResult),
                        null);

                    _parseContext.CallFunctionScope.Release();

                    if (currentToken.TokenType == TType.Dot)
                    {
                        Match(TType.Dot);
                        return Variable(superCall, false, expectReturn: expectReturn);
                    }

                    return superCall;
                }

                bool intrinsical = caller is null && Interpreter.Proxy.XppProxyHelper.IsIntrinsicFunction(functionName);
                if (intrinsical)
                {
                    ret = IntrinsicFunction(functionName);
                }
                else
                {
                    var funcName = (Word)identifier.Token;

                    HandleMetadataInterruption(
                        identifier.Line, 
                        identifier.Start, 
                        identifier.End, 
                        identifier.Token,
                        staticCall ? TokenMetadataType.StaticMethod : 
                            caller is null ? TokenMetadataType.GlobalOrDefinedMethod : 
                                             TokenMetadataType.InstanceMethod,
                        caller);

                    var parameters = Parameters(caller, funcName.Lexeme, staticCall, false, out List<KeyValuePair<int, string>> byRefArguments);

                    ret = new FunctionCall(
                        funcName,
                        parameters,
                        caller,
                        staticCall,
                        false,
                        SourceCodeBinding(identifier, lastScanResult),
                        isInsideFunctionScope ? SourceCodeBinding(debuggeableStartBinding, lastScanResult) : null)
                    {
                        ByRefArguments = byRefArguments
                    };
                }

                _parseContext.CallFunctionScope.Release();
            }
            else
            {
                validateVariableName = caller is null;

                HandleMetadataInterruption(
                    identifier.Line,
                    identifier.Start,
                    identifier.End,
                    identifier.Token,
                    TokenMetadataType.Variable,
                    caller);

                if (currentToken.TokenType == TType.LeftBracket)
                {
                    Match(TType.LeftBracket);
                    Expression index = Expression();
                    Match(TType.RightBracket);

                    ret = new ArrayAccess(
                        (Word)identifier.Token,
                        caller,
                        index,
                        staticCall,
                        SourceCodeBinding(identifier, lastScanResult));
                }
                else
                {
                    ret = new Variable(
                        (Word)identifier.Token,
                        caller,
                        staticCall,
                        SourceCodeBinding(identifier, lastScanResult));
                }
            }

            // A single identifier used as a :: caller may be an alias or imported
            // .NET type name unknown to the X++ metadata proxies
            if (currentToken.TokenType == TType.StaticDoubleDot && caller is null &&
                ret != null && ret.GetType() == typeof(Variable))
            {
                var staticCallerVariable = (Variable)ret;
                staticCallerVariable.ClrTypeReference = ResolveClrOnlyType(staticCallerVariable.Name);
            }

            // Static calls cannot be from instances, so skip validation
            if (currentToken.TokenType != TType.StaticDoubleDot && validateVariableName)
            {
                string variableName = ((Word)identifier.Token).Lexeme;
                var declaredVariable = _parseContext.CurrentScope.FindVariableDeclaration(variableName);

                if (declaredVariable is null)
                {
                    HandleParseError(string.Format(MessageProvider.ExceptionVariableNotDeclared, variableName), stop: false);
                }
            }

            System.Type returnType = _typeInferer.InferType(ret, currentToken.TokenType == TType.StaticDoubleDot, _parseContext);

            if (validateReturnType && ret is FunctionCall && expectReturn && (returnType is null || returnType == typeof(void)))
            {
                HandleParseError(MessageProvider.ExceptionTypeCannotBeNull, stop: false);
            }

            if (ret is Variable variable && variable.Caller != null)
            {
                var callerType = _typeInferer.InferType(variable.Caller, variable.StaticCall, _parseContext);
                ValidateVariableMember(callerType, ret);
            }

            if (currentToken.TokenType == TType.Dot || currentToken.TokenType == TType.StaticDoubleDot)
            {
                if (currentToken.TokenType == TType.StaticDoubleDot &&
                    (ret is FunctionCall || ret is ArrayAccess))
                {
                    HandleParseError(string.Format(MessageProvider.ExceptionTokenWasNotExpected, currentToken.TokenType));
                }

                MatchMultiple(TType.Dot, TType.StaticDoubleDot);
                HandleAutocompletion(ret);

                ret = Variable(ret, lastScanResult.Token.TokenType == TType.StaticDoubleDot, expectReturn: expectReturn);
            }

            return ret;
        }

        private void ValidateVariableMember(System.Type callerType, Expression expression)
        {
            if (callerType is null) return;

            // Script-declared classes are validated against the class declaration table
            if (callerType is Core.ScriptClassType scriptClassType)
            {
                var classInfo = (ClassDeclarationInfo)scriptClassType.DeclarationInfo;

                System.Type compiledBase = classInfo.FindCompiledBase();

                if (expression is FunctionCall scriptCall)
                {
                    var scriptMethod = classInfo.FindMethod(scriptCall.Name);
                    var methodOwner = classInfo.FindMethodOwner(scriptCall.Name);
                    var extensionMethod = scriptMethod is null
                        ? _parseContext.DeclaredClasses.FindExtensionMethod(classInfo, scriptCall.Name)
                        : null;

                    if (scriptMethod is null && extensionMethod != null)
                    {
                        ValidateArgumentCount(classInfo.Name, extensionMethod, scriptCall.Parameters.Count);

                        if (scriptCall.StaticCall != extensionMethod.IsStatic)
                        {
                            HandleParseError($"Extension method {scriptCall.Name} must be called {(extensionMethod.IsStatic ? "statically" : "on an instance")}.", stop: false);
                        }
                    }
                    else if (scriptMethod is null)
                    {
                        // Members not declared in the script chain may live on a
                        // wrapped compiled base class
                        bool onCompiledBase = false;

                        try
                        {
                            onCompiledBase = compiledBase != null && _proxy.Reflection.TypeHasMethod(compiledBase, scriptCall.Name);
                        }
                        catch
                        {
                        }

                        if (!onCompiledBase)
                        {
                            HandleParseError(string.Format(MessageProvider.ExceptionInvalidTypeMethod, classInfo.Name, scriptCall.Name), stop: false);
                        }
                    }
                    else if (scriptCall.Parameters.Count > scriptMethod.Parameters.Count)
                    {
                        ValidateArgumentCount(classInfo.Name, scriptMethod, scriptCall.Parameters.Count);
                    }
                    else
                    {
                        ValidateArgumentCount(classInfo.Name, scriptMethod, scriptCall.Parameters.Count);

                        if (scriptCall.StaticCall != scriptMethod.IsStatic)
                        {
                            HandleParseError($"Method {methodOwner.Name}.{scriptMethod.Name} must be called {(scriptMethod.IsStatic ? "statically" : "on an instance")}.", stop: false);
                        }

                        if (!CanAccessMember(scriptMethod.Access, methodOwner))
                        {
                            HandleParseError($"Method {methodOwner.Name}.{scriptMethod.Name} is not accessible from the current context.", stop: false);
                        }

                        if (scriptCall.StaticCall && string.Equals(scriptMethod.Name, "TypeNew", StringComparison.OrdinalIgnoreCase))
                        {
                            HandleParseError($"Static constructor {methodOwner.Name}.TypeNew cannot be called explicitly.", stop: false);
                        }
                    }
                }
                else if (expression is Variable scriptVariable)
                {
                    var scriptField = classInfo.FindField(scriptVariable.Name);
                    var fieldOwner = classInfo.FindFieldOwner(scriptVariable.Name);

                    if (scriptField is null && _currentClassInfo?.ExtensionTarget != null &&
                        _currentClassInfo.ExtensionTarget.IsOrExtends(classInfo))
                    {
                        scriptField = _currentClassInfo.FindField(scriptVariable.Name, walkBaseChain: false);
                        fieldOwner = scriptField is null ? null : _currentClassInfo;
                    }

                    if (scriptField is null)
                    {
                        scriptField = _parseContext.DeclaredClasses.FindExtensionField(classInfo, scriptVariable.Name);
                        fieldOwner = scriptField is null
                            ? null
                            : _parseContext.DeclaredClasses.FindExtensionFieldOwner(classInfo, scriptVariable.Name);
                    }

                    if (scriptField is null && classInfo.FindDelegate(scriptVariable.Name) is null)
                    {
                        bool onCompiledBase = false;

                        try
                        {
                            onCompiledBase = compiledBase != null && _proxy.Reflection.TypeHasProperty(compiledBase, scriptVariable.Name);
                        }
                        catch
                        {
                        }

                        if (!onCompiledBase)
                        {
                            HandleParseError(string.Format(MessageProvider.ExceptionInvalidTypeField, classInfo.Name, scriptVariable.Name), stop: false);
                        }
                    }
                    else if (scriptField != null)
                    {
                        if (scriptVariable.StaticCall != scriptField.IsStatic)
                        {
                            HandleParseError($"Field {fieldOwner.Name}.{scriptField.Name} must be accessed {(scriptField.IsStatic ? "statically" : "on an instance")}.", stop: false);
                        }

                        if (!CanAccessMember(scriptField.Access, fieldOwner))
                        {
                            HandleParseError($"Field {fieldOwner.Name}.{scriptField.Name} is not accessible from the current context.", stop: false);
                        }
                    }
                }

                return;
            }

            // .NET types unknown to the X++ metadata proxies use direct CLR reflection
            if (_typeInferer.IsClrOnlyType(callerType))
            {
                string memberName = expression is FunctionCall clrCall ? clrCall.Name : (expression as Variable)?.Name;

                if (memberName != null && !Interpreter.ClrBridge.HasMember(callerType, memberName))
                {
                    HandleParseError(string.Format(MessageProvider.ExceptionInvalidTypeMethod, callerType.Name, memberName), stop: false);
                }

                return;
            }

            if (callerType.IsEnum)
            {
                if (expression is FunctionCall)
                {
                    HandleParseError(MessageProvider.ExceptionCannotCallEnumMethod, stop: false);
                }
                else if (expression is Variable variable && !_proxy.Reflection.EnumHasMember(callerType.Name, variable.Name))
                {
                    HandleParseError(string.Format(MessageProvider.ExceptionInvalidEnumMember, callerType.Name, variable.Name), stop: false);
                }

                return;
            }
            else if (expression is FunctionCall functionCall)
            {
                ClassMethodSignature interpretedExtension = _parseContext.DeclaredClasses
                    .FindCompiledTargetExtensionMethod(callerType, functionCall.Name, functionCall.StaticCall);

                if (interpretedExtension != null)
                {
                    ValidateArgumentCount(callerType.Name, interpretedExtension, functionCall.Parameters.Count);
                }
                else if (!_proxy.Reflection.TypeHasMethod(callerType, functionCall.Name) &&
                    !ExtensionClassHasMethod(callerType, functionCall.Name))
                {
                    HandleParseError(string.Format(MessageProvider.ExceptionInvalidTypeMethod, callerType.Name, functionCall.Name), stop: false);
                }
            }
            else if (expression is Variable variable)
            {
                if (!_proxy.Reflection.TypeHasProperty(callerType, variable.Name))
                {
                    HandleParseError(string.Format(MessageProvider.ExceptionInvalidTypeField, callerType.Name, variable.Name), stop: false);
                }
            }
        }

        private void ValidateArgumentCount(string className, ClassMethodSignature method, int actualCount)
        {
            if (actualCount < method.RequiredParameterCount || actualCount > method.Parameters.Count)
            {
                HandleParseError(
                    $"Method {method.Name} of {className} takes {method.RequiredParameterCount} to {method.Parameters.Count} argument(s), but {actualCount} were supplied.",
                    stop: false);
            }
        }

        private bool CanAccessMember(AccessModifier access, ClassDeclarationInfo declaringClass)
        {
            if (_currentClassInfo != null && ReferenceEquals(_currentClassInfo.ExtensionTarget, declaringClass))
            {
                return access == AccessModifier.Public || access == AccessModifier.Protected;
            }

            if (access == AccessModifier.Public || access == AccessModifier.Internal)
            {
                // Script declarations are compiled as one unit, so internal members
                // belong to the same effective model.
                return true;
            }

            if (_currentClassInfo is null || declaringClass is null)
            {
                return false;
            }

            if (ReferenceEquals(_currentClassInfo, declaringClass))
            {
                return true;
            }

            return access == AccessModifier.Protected && _currentClassInfo.IsOrExtends(declaringClass);
        }

        /// <summary>
        /// X++ extension methods: obj.extMethod() resolves to TypeName_Extension::extMethod
        /// </summary>
        internal bool ExtensionClassHasMethod(System.Type callerType, string methodName)
        {
            try
            {
                var extensionType = _proxy.Casting.GetSystemTypeFromTypeName(callerType.Name + "_Extension");
                return extensionType != null && _proxy.Reflection.TypeHasMethod(extensionType, methodName);
            }
            catch
            {
                return false;
            }
        }

        internal List<Statement> Default()
        {
            Match(TType.Default);
            var statements = new List<Statement>();
            Match(TType.DoubleDot);

            while (currentToken.TokenType != TType.RightBrace)
            {
                statements.Add(Statement());
            }

            return statements;
        }

        internal List<KeyValuePair<Expression, List<Statement>>> Case()
        {
            var caseResult = Match(TType.Case);
            var expressions = new List<Expression>();

            // A case label allows comma-separated value lists (case 1, 2, 3:)
            do
            {
                if (expressions.Count > 0)
                {
                    Match(TType.Comma);
                }

                expressions.Add(Expression());

            } while (currentToken.TokenType == TType.Comma);

            var statements = new List<Statement>();

            var caseResultEnd = Match(TType.DoubleDot);
            while (currentToken.TokenType != TType.Case &&
                   currentToken.TokenType != TType.Default &&
                   currentToken.TokenType != TType.RightBrace)
            {
                statements.Add(Statement());
            }

            var ret = new List<KeyValuePair<Expression, List<Statement>>>();

            // With fall-through dispatch, all but the last value get an empty label
            // that falls through into the shared statement list
            for (int numExpression = 0; numExpression < expressions.Count; numExpression++)
            {
                var expression = expressions[numExpression];
                expression.DebuggeableBinding = SourceCodeBinding(caseResult, caseResultEnd);

                ret.Add(new KeyValuePair<Expression, List<Statement>>(
                    expression,
                    numExpression == expressions.Count - 1 ? statements : new List<Statement>()));
            }

            return ret;
        }

        internal Switch Switch()
        {
            var switchResult = Match(TType.Switch);
            Match(TType.LeftParenthesis);
            var expression = Expression();
            var switchResultEnd = Match(TType.RightParenthesis);
            Match(TType.LeftBrace);

            List<Statement> defaultStatements = null;
            List<KeyValuePair<Expression, List<Statement>>> cases = null;

            while (currentToken.TokenType == TType.Case || currentToken.TokenType == TType.Default)
            {
                if (defaultStatements != null)
                {
                    if (currentToken.TokenType == TType.Case)
                    {
                        HandleParseError(MessageProvider.ExceptionDefaultSwitchStmt, stop: false);
                    }
                    else if (currentToken.TokenType == TType.Default)
                    {
                        HandleParseError(MessageProvider.ExceptionMultiDefaultSwitchStmt, stop: false);
                    }
                }

                if (currentToken.TokenType == TType.Case)
                {
                    if (cases is null)
                    {
                        cases = new List<KeyValuePair<Expression, List<Statement>>>();
                    }

                    cases.AddRange(Case());
                }
                else if (currentToken.TokenType == TType.Default)
                {
                    defaultStatements = Default();
                }
            }

            Match(TType.RightBrace);

            return new Switch(
                expression,
                cases,
                defaultStatements,
                SourceCodeBinding(switchResult, lastScanResult),
                SourceCodeBinding(switchResult, switchResultEnd));
        }

        internal If If(IScanResult elseScan = null)
        {
            var ifResult = Match(TType.If);
            var debuggeableStartResult = elseScan is null ? ifResult : elseScan;

            Match(TType.LeftParenthesis);
            Expression expression = Expression();

            var endIfResult = Match(TType.RightParenthesis);

            Block block = Block();

            If @else = null;

            if (currentToken.TokenType == TType.Else)
            {
                var elseResult = Match(TType.Else);

                if (currentToken.TokenType == TType.If)
                {
                    @else = If(elseResult);
                }
                else
                {
                    @else = new Else(
                        Statement(),
                        SourceCodeBinding(elseResult, lastScanResult),
                        DebuggeableBinding(elseResult));
                }
            }

            return new If(
                expression,
                block,
                @else,
                SourceCodeBinding(ifResult, block.SourceCodeBinding),
                SourceCodeBinding(debuggeableStartResult, endIfResult));
        }

        internal While While()
        {
            var bindingStart = Match(TType.While);

            Match(TType.LeftParenthesis);
            Expression condition = Expression();

            var bindingEnds = Match(TType.RightParenthesis);

            using (new LoopControlContext(_parseContext))
            { 
                return new While(condition, Block(),
                    SourceCodeBinding(bindingStart, lastScanResult),
                    SourceCodeBinding(bindingStart, bindingEnds));
            }

        }

        internal For For()
        {
            For @for;
            var start = Match(TType.For);
            Match(TType.LeftParenthesis);

            _parseContext.BeginScope();

            Statement initialisation = Statement(false);
            Match(TType.Semicolon);
            Expression expression = Expression();
            Match(TType.Semicolon);
            Statement loopStmt = Statement(false);
            var end = Match(TType.RightParenthesis);

            using (new LoopControlContext(_parseContext))
            { 
                @for = new For(initialisation, expression, loopStmt, Block(), 
                    SourceCodeBinding(start, lastScanResult), 
                    SourceCodeBinding(start, end));
            }

            _parseContext.EndScope();

            return @for;
        }

        internal Do Do()
        {
            var start = Match(TType.Do);

            using (new LoopControlContext(_parseContext))
            { 
                Block block = Block();
                Match(TType.While);
                Match(TType.LeftParenthesis);
                Expression expression = Expression();
                Match(TType.RightParenthesis);
                Match(TType.Semicolon);

                return new Do(expression, block, 
                    SourceCodeBinding(start, lastScanResult), 
                    DebuggeableBinding(start));
            }
        }

        internal Statement VariableDeclaration(bool matchSemicolon = true, bool isUsing = false, bool isConst = false)
        {
            var typeResult = MatchMultiple(
                TType.Id,
                TType.TypeAnytype,
                TType.TypeBoolean,
                TType.TypeContainer,
                TType.TypeInt32,
                TType.TypeInt64,
                TType.TypeGuid,
                TType.TypeReal,
                TType.TypeStr,
                TType.TypeTimeOfDay,
                TType.TypeDate,
                TType.TypeDatetime,
                TType.Var);

            Word typeWord = typeResult.Token as Word;

            // Namespace-qualified type names (System.Text.StringBuilder b;)
            if (typeWord.TokenType == TType.Id && currentToken.TokenType == TType.Dot)
            {
                var qualifiedName = new System.Text.StringBuilder(typeWord.Lexeme);

                while (currentToken.TokenType == TType.Dot)
                {
                    Match(TType.Dot);
                    qualifiedName.Append('.');
                    qualifiedName.Append(((Word)Match(TType.Id).Token).Lexeme);
                }

                typeWord = new Word(qualifiedName.ToString(), TType.Id);
            }

            // Length-limited string declarations (str 40 name;) - the size is parsed
            // and ignored, as the compiler does for in-memory strings
            if (typeWord.TokenType == TType.TypeStr && currentToken.TokenType == TType.Int32)
            {
                Match(TType.Int32);
            }

            if (typeWord.TokenType != TType.Var && !_typeInferer.IsKnownType(typeWord.Lexeme))
            {
                HandleParseError(string.Format(MessageProvider.ExceptionTypeNotFound, typeWord.Lexeme), stop: false);
            }

            // Get type from type declaration if it's a known type
            System.Type declarationType = typeWord.TokenType != TType.Var ? _typeInferer.InferType(typeWord.Lexeme) : null;

            // A declaration list can mix simple and array declarators; each maximal run
            // of simple declarators becomes one VariableDeclarations part
            var parts = new List<Statement>();
            var declarations = new Dictionary<Word, Expression>();

            void FlushSimpleDeclarations()
            {
                if (declarations.Count == 0)
                {
                    return;
                }

                var part = new VariableDeclarations(typeWord, declarationType, declarations, SourceCodeBinding(typeResult, lastScanResult));

                foreach (var identifier in part.Identifiers)
                {
                    var scopeVariable = new ParseContextScopeVariable(identifier.Key.Lexeme, part.DeclarationType, part.DeclarationClrType, false, identifier.Value)
                    {
                        IsConst = isConst
                    };

                    _parseContext.CurrentScope.VariableDeclarations.Add(scopeVariable);
                }

                parts.Add(part);
                declarations = new Dictionary<Word, Expression>();
            }

            bool first = true;

            do
            {
                if (isUsing && !first)
                {
                    HandleParseError(MessageProvider.ExceptionInvalidUsing, stop: false);
                }

                if (!first)
                {
                    Match(TType.Comma);
                }

                first = false;

                Word id = (Word)MatchIdentifier().Token;

                if (!isUsing && currentToken.TokenType == TType.LeftBracket)
                {
                    if (isConst)
                    {
                        HandleParseError("Const declarations cannot be arrays.", stop: false);
                    }

                    if (typeWord.TokenType == TType.Var)
                    {
                        HandleParseError("var cannot be used with array declarators.", stop: false);
                    }

                    // Array declarator: name[size] or name[size, memoryElements]
                    Match(TType.LeftBracket);

                    Expression arraySize = null;

                    if (currentToken.TokenType != TType.RightBracket)
                    {
                        arraySize = Expression();

                        // The optional second value (in-memory element count) is
                        // parsed and ignored, as it only affects paging on disk
                        if (currentToken.TokenType == TType.Comma)
                        {
                            Match(TType.Comma);
                            Expression();
                        }
                    }

                    Match(TType.RightBracket);

                    FlushSimpleDeclarations();

                    System.Type arrayType;

                    try
                    {
                        arrayType = _proxy.Casting.GetArrayType(declarationType);
                    }
                    catch
                    {
                        arrayType = typeof(object);
                    }

                    parts.Add(new VariableArrayDeclaration(typeWord, arrayType, id, arraySize, SourceCodeBinding(typeResult, lastScanResult)));

                    _parseContext.CurrentScope.VariableDeclarations.Add(
                        new ParseContextScopeVariable(id.Lexeme, typeWord, arrayType, true, null));

                    continue;
                }

                Expression initialisation = null;

                if (isUsing || currentToken.TokenType == TType.Assign)
                {
                    Match(TType.Assign);
                    initialisation = Expression();
                }
                else if (isConst)
                {
                    HandleParseError("Const declarations require an initialization value.", stop: false);
                }
                else if (typeWord.TokenType == TType.Var)
                {
                    HandleParseError("var declarations require an initialization value.", stop: false);
                }

                declarations[id] = initialisation;

                // Type checking
                if (initialisation != null)
                {
                    var initializationType = _typeInferer.InferType(initialisation, false, _parseContext);

                    if (declarationType is null)
                    {
                        if (initializationType is null)
                        {
                            HandleParseError(MessageProvider.ExceptionInitializationUnknown, stop: false);
                            declarationType = typeof(object);
                        }
                        else
                        {
                            declarationType = initializationType;
                        }
                    }
                    else if (initializationType != null && !ImplicitConversionExists(initializationType, declarationType))
                    {
                        HandleParseError(string.Format(MessageProvider.ExceptionImplicitConversion, initializationType.Name, declarationType.Name), stop: false);
                    }
                }
            } while (currentToken.TokenType == TType.Comma);

            if (matchSemicolon)
            {
                Match(TType.Semicolon);
            }

            FlushSimpleDeclarations();

            if (parts.Count == 1)
            {
                return parts[0];
            }

            return new StatementGroup(parts, SourceCodeBinding(typeResult, lastScanResult));
        }

        internal LoopControl LoopControl()
        {
            if (!_parseContext.CanScapeLoop())
            {
                HandleParseError(MessageProvider.ExceptionLeaveFinally, stop: false);
            }

            var start = currentScanResult;
            Token loopControlToken = currentToken;

            MatchMultiple(TType.Continue, TType.Break);
            Match(TType.Semicolon);

            return new LoopControl(
                loopControlToken,
                SourceCodeBinding(start, lastScanResult));
        }

        internal TtsAbort TtsAbort()
        {
            var start = currentScanResult;

            Match(TType.TtsAbort);
            Match(TType.Semicolon);

            return new TtsAbort(SourceCodeBinding(start, lastScanResult));
        }
        internal TtsCommit TtsCommit()
        {
            var start = currentScanResult;

            Match(TType.TtsCommit);
            Match(TType.Semicolon);

            return new TtsCommit(SourceCodeBinding(start, lastScanResult));
        }

        internal Next Next()
        {
            var start = currentScanResult;
            Match(TType.Next);
            var id = (Word)Match(TType.Id).Token;
            Match(TType.Semicolon);
            return new Next(id.Lexeme, SourceCodeBinding(start, lastScanResult));
        }

        private bool IsNextMethodCallAhead()
        {
            if (!_parseContext.InClassMethod || _currentClassInfo?.ExtensionTarget is null)
            {
                return false;
            }

            Token methodToken = AdvancePeek(false).Token;
            Token parenthesisToken = AdvancePeek(true).Token;

            return methodToken is Word && parenthesisToken.TokenType == TType.LeftParenthesis;
        }

        internal NextCallExpression NextCall()
        {
            var start = Match(TType.Next);
            Word methodWord = (Word)MatchAnyWord().Token;

            if (_currentClassInfo?.ExtensionTarget is null)
            {
                HandleParseError("next method calls can only be used in a Chain of Command extension.", stop: false);
            }

            if (!string.Equals(methodWord.Lexeme, _currentMethodName, StringComparison.OrdinalIgnoreCase))
            {
                HandleParseError($"next must call the current wrapper method {_currentMethodName}.", stop: false);
            }

            ClassMethodSignature targetMethod = _currentClassInfo?.ExtensionTarget?.FindMethod(_currentMethodName);

            if (targetMethod is null)
            {
                HandleParseError($"No original method {_currentMethodName} exists on extension target {_currentClassInfo?.ExtensionTargetName}.", stop: false);
            }

            List<Expression> parameters = Parameters(null, methodWord.Lexeme, false, false);

            if (targetMethod != null)
            {
                ValidateArgumentCount(_currentClassInfo.ExtensionTarget.Name, targetMethod, parameters.Count);
            }

            return new NextCallExpression(
                _currentClassInfo,
                targetMethod,
                methodWord,
                parameters,
                SourceCodeBinding(start, lastScanResult));
        }

        internal Flush Flush()
        {
            var start = currentScanResult;
            Match(TType.Flush);
            var id = (Word)Match(TType.Id).Token;

            if (!_forAutoCompletion && !_forMetadata)
            {
                try
                {
                    _ = _proxy.Intrinsic.tableNum(id.Lexeme);
                }
                catch (Exception ex)
                {
                    string message = ex.Message;
                    if (ex.InnerException != null)
                    {
                        message = ex.InnerException.Message;
                }

                    HandleParseError(message, false, stop: false);
            }
            }

            Match(TType.Semicolon);

            return new Flush(id.Lexeme, SourceCodeBinding(start, lastScanResult));
        }


        internal Throw Throw()
        {
            var start = currentScanResult;

            Match(TType.Throw);
            var exception = Expression();
            Match(TType.Semicolon);

            return new Throw(exception, SourceCodeBinding(start, lastScanResult));
        }

        internal Breakpoint Breakpoint()
        {
            var start = currentScanResult;

            Match(TType.Breakpoint);
            Match(TType.Semicolon);

            return new Breakpoint(SourceCodeBinding(start, lastScanResult));
        }

        internal TtsBegin TtsBegin()
        {
            var start = currentScanResult;

            Match(TType.TtsBegin);
            Match(TType.Semicolon);

            return new TtsBegin(SourceCodeBinding(start, lastScanResult));
        }

        /// <summary>
        /// Reads a (possibly dotted) name: Id (. Id)*
        /// </summary>
        internal string DottedName()
        {
            var builder = new System.Text.StringBuilder(((Word)Match(TType.Id).Token).Lexeme);

            while (currentToken.TokenType == TType.Dot)
            {
                Match(TType.Dot);
                builder.Append('.');
                builder.Append(((Word)Match(TType.Id).Token).Lexeme);
            }

            return builder.ToString();
        }

        internal Statement UsingOrNamespaceImport()
        {
            // using System.Text;          -> namespace import
            // using SB = System.Text.X;   -> alias
            // using (Type v = expr) { }   -> resource statement
            if (AdvancePeek(true).Token.TokenType != TType.Id)
            {
                return Using();
            }

            var start = Match(TType.Using);

            string firstName = ((Word)Match(TType.Id).Token).Lexeme;

            if (currentToken.TokenType == TType.Assign)
            {
                Match(TType.Assign);
                string target = DottedName();
                _parseContext.ClrNamespaces.AddAlias(firstName, target);
            }
            else
            {
                var builder = new System.Text.StringBuilder(firstName);

                while (currentToken.TokenType == TType.Dot)
                {
                    Match(TType.Dot);
                    builder.Append('.');
                    builder.Append(((Word)Match(TType.Id).Token).Lexeme);
                }

                _parseContext.ClrNamespaces.AddNamespace(builder.ToString());
            }

            var end = Match(TType.Semicolon);

            return new EmptyStatement(SourceCodeBinding(start, end));
        }

        internal Using Using()
        {
            var start = Match(TType.Using);

            _parseContext.BeginScope();

            Match(TType.LeftParenthesis);

            var variable = (VariableDeclarations)VariableDeclaration(false, isUsing: true);

            System.Type inferedType = _typeInferer.InferType(variable.Identifiers.First().Value, false, _parseContext);

            if (!ReflectionHelper.TypeImplementsInterface(inferedType, typeof(IDisposable)))
            {
                HandleParseError(MessageProvider.ExceptionExpressionIDisposable, stop: false);
            }

            Match(TType.RightParenthesis);

            Using @using = new Using(variable, Block(), SourceCodeBinding(start, lastScanResult));

            _parseContext.EndScope();

            return @using;
        }

        internal Return Return()
        {
            if (_parseContext.WithinFinally())
            {
                HandleParseError(MessageProvider.ExceptionLeaveFinally, stop: false);
            }

            var start = currentScanResult;

            Match(TType.Return);

            Expression expression = null;

            // A bare return; is allowed in void functions and at the top level of a script
            if (currentToken.TokenType != TType.Semicolon)
            {
                if (_parseContext.FunctionDeclarationStack.Empty && !_parseContext.InClassMethod)
                {
                    HandleParseError(MessageProvider.ExceptionReturnOutOfFunction, stop: false);
                }

                expression = Expression();
            }

            Match(TType.Semicolon);

            return new Return(expression, SourceCodeBinding(start, lastScanResult));
        }

        internal Print Print()
        {
            var start = currentScanResult;

            Match(TType.Print);
            List<Expression> parameters = new List<Expression>();

            do
            {
                if (currentToken.TokenType == TType.Comma)
                {
                    Match(TType.Comma);
                }

                parameters.Add(Expression());
            } while (currentToken.TokenType == TType.Comma);

            Match(TType.Semicolon);

            return new Print(parameters, SourceCodeBinding(start, lastScanResult), SourceCodeBinding(start, lastScanResult));
        }

        internal Retry Retry()
        {
            if (!_parseContext.WithinCatch())
            {
                HandleParseError(MessageProvider.ExceptionRetryNotInCatch, stop: false);
            }

            var start = currentScanResult;

            Match(TType.Retry);
            Match(TType.Semicolon);

            return new Retry(SourceCodeBinding(start, lastScanResult));
        }

        internal Try Try()
        {
            var start = Match(TType.Try);
            var tryBlock = Block();
            var catches = new List<Catch>();

            var tryOrCatch = MatchMultiple(TType.Catch, TType.Finally);

            if (tryOrCatch.Token.TokenType == TType.Catch)
            {
                bool keepParsingCatch;

                do
                {
                    string enumMember = null;
                    if (currentToken.TokenType == TType.LeftParenthesis)
                    {
                        Match(TType.LeftParenthesis);
                        var exceptionTypeExpression = Variable();

                        if (exceptionTypeExpression is FunctionCall || exceptionTypeExpression is ArrayAccess ||
                            (exceptionTypeExpression is Variable v &&
                            (!v.StaticCall || v.Caller is null || (v.Caller as Variable).Caller != null)))
                        {
                            HandleParseError(MessageProvider.ExceptionInvalidCatchExpr, stop: false);
                        }

                        var exceptionTypeVariable = exceptionTypeExpression as Variable;
                        string exceptionEnum = ((Word)(exceptionTypeVariable.Caller as Variable).Token).Lexeme;

                        // Enum must be Exception
                        if (!CatchExceptionTypeHelper.IsExceptionEnum(exceptionEnum))
                        {
                            HandleParseError(MessageProvider.ExceptionInvalidExceptionEnum, stop: false);
                        }

                        enumMember = ((Word)exceptionTypeExpression.Token).Lexeme;
                        if (!CatchExceptionTypeHelper.IsExceptionMember(enumMember))
                        {
                            HandleParseError(string.Format(MessageProvider.ExceptionNotExceptionMember, enumMember), stop: false);
                        }

                        Match(TType.RightParenthesis);
                    }

                    using (var catchContext = new CatchContext(_parseContext))
                    { 
                        catches.Add(new Catch(enumMember, Block()));
                    }

                    if (currentToken.TokenType == TType.Catch)
                    {
                        Match(TType.Catch);
                        keepParsingCatch = true;
                    }
                    else
                    {
                        keepParsingCatch = false;
                    }

                } while (keepParsingCatch);
            }

            Block finallyBlock = null;
            bool parseFinally = currentToken.TokenType == TType.Finally || tryOrCatch.Token.TokenType == TType.Finally;

            if (parseFinally)
            {
                // Match the finally token if not done before
                if (currentToken.TokenType == TType.Finally)
                {
                    Match(TType.Finally);
                }

                using (new LoopControlContext(_parseContext, false))
                { 
                    finallyBlock = Block();
                }
            }

            return new Try(tryBlock,SourceCodeBinding(start, lastScanResult), catches, finallyBlock);
        }

        internal Statement Statement(bool matchSemicolon = true)
        {
            switch (currentToken.TokenType)
            {
                case TType.Semicolon:
                    {
                        var semicolonResult = Match(TType.Semicolon);
                        return new EmptyStatement(SourceCodeBinding(semicolonResult));
                    }
                case TType.New: return ExpressionStatement();
                case TType.Class:
                case TType.Abstract:
                case TType.Final:
                case TType.Public:
                case TType.Private:
                case TType.Internal:
                case TType.Static:
                case TType.Client:
                case TType.Server:
                    return IsInterfaceDeclarationAhead() ? InterfaceDeclaration() : ClassDeclaration();
                case TType.Interface:
                    return InterfaceDeclaration();
                case TType.LeftBracket:
                    // [AttributeName(...)] decorations before class/interface declarations
                    // are retained on the declared type; anything else is a
                    // container assignment.
                    if (IsAttributeListAhead())
                    {
                        while (currentToken.TokenType == TType.LeftBracket)
                        {
                            List<XppAttributeDeclaration> attributes = CaptureAttributeList();
                            _pendingTypeAttributes.AddRange(attributes);
                            string extensionTarget = GetExtensionTarget(attributes);

                            if (!string.IsNullOrEmpty(extensionTarget))
                            {
                                _pendingExtensionTargetName = extensionTarget;
                            }
                        }

                        return Statement(matchSemicolon);
                    }

                    return ContainerAssignment();
                case TType.Retry: return Retry();
                case TType.Using: return UsingOrNamespaceImport();
                case TType.Try: return Try();
                case TType.Print: return Print();
                case TType.Return: return Return();
                case TType.LeftBrace: return Block();
                case TType.Void:
                case TType.Id:
                    {
                        // Check if the next token is an Id (or a soft keyword used as one)
                        var nextToken = AdvancePeek(false).Token;
                        if (nextToken.TokenType == TType.Id || IsSoftIdentifierToken(nextToken.TokenType))
                        {
                            nextToken = AdvancePeek(true).Token;
                            if (nextToken.TokenType == TType.LeftParenthesis)
                            {
                                return FunctionDeclaration();
                            }
                            else
                            {
                                return VariableDeclaration(matchSemicolon);
                            }
                        }
                        else if (nextToken.TokenType == TType.Dot)
                        {
                            // Namespace-qualified type declaration or function declaration
                            // (System.Text.StringBuilder b; / System.Text.StringBuilder make() {...})
                            switch (PeekDottedDeclarationKind())
                            {
                                case DottedDeclarationKind.Function:
                                    return FunctionDeclaration();
                                case DottedDeclarationKind.Variable:
                                    return VariableDeclaration(matchSemicolon);
                                default:
                                    ResetPeek();
                                    return Assignment(matchSemicolon);
                            }
                        }
                        else
                        {
                            ResetPeek();
                            return Assignment(matchSemicolon);
                        }
                    }
                case TType.Breakpoint: return Breakpoint();
                case TType.Throw: return Throw();
                case TType.TtsBegin: return TtsBegin();
                case TType.TtsAbort: return TtsAbort();
                case TType.TtsCommit: return TtsCommit();
                case TType.If: return If();
                case TType.Next: return IsNextMethodCallAhead() ? ExpressionStatement() : Next();
                case TType.Flush: return Flush();
                case TType.InsertRecordset: return InsertRecordset();
                case TType.UpdateRecordset: return UpdateRecordset();
                case TType.DeleteFrom: return DeleteFrom();
                case TType.Select: return SelectStatement();
                case TType.While:
                    {
                        var nextToken = AdvancePeek(true).Token;
                        if (nextToken.TokenType == TType.Select)
                        {
                            return WhileSelect();
                        }
                        else
                        {
                            return While();
                        }
                    }
                case TType.For: return For();
                case TType.Do: return Do();
                case TType.Break:
                case TType.Continue:
                    return LoopControl();
                case TType.Sum:
                case TType.Avg:
                case TType.Count:
                case TType.MaxOf:
                case TType.MinOf:
                case TType.Index:
                    // Contextual keywords used as identifiers (count = 5;)
                    return Assignment(matchSemicolon);
                case TType.Const:
                case TType.ReadOnly:
                    // Local const declaration (const int Blue = 0xFF;)
                    Match(currentToken.TokenType);
                    return VariableDeclaration(matchSemicolon, isConst: true);
                case TType.Switch: return Switch();
                case TType.ChangeCompany: return ChangeCompany();
                case TType.Unchecked: return Unchecked();
                case TType.Var:
                case TType.TypeStr:
                case TType.TypeDate:
                case TType.TypeDatetime:
                case TType.TypeTimeOfDay:
                case TType.TypeContainer:
                case TType.TypeAnytype:
                case TType.TypeReal:
                case TType.TypeInt32:
                case TType.TypeInt64:
                case TType.TypeBoolean:
                case TType.TypeGuid:
                    {
                        // We advance twice because we want to check for the next token after the Id
                        AdvancePeek(false);
                        var nextToken = AdvancePeek(true).Token;

                        if (nextToken.TokenType == TType.LeftParenthesis)
                        {
                            return FunctionDeclaration();
                        }
                        else
                        {
                            return VariableDeclaration(matchSemicolon);
                        }
                    }
                default:
                    {
                        HandleParseError(MessageProvider.ExceptionInvalidSyntax);
                        return null;
                    }
            }
        }

        private enum DottedDeclarationKind
        {
            None,
            Variable,
            Function,
        }

        /// <summary>
        /// Peeks over Id (. Id)* to check whether the dotted chain is followed by
        /// another Id, which makes it a namespace-qualified declaration - a function
        /// declaration when a parenthesis follows the name, otherwise a variable.
        /// The peek offset already sits after the first "Id ." pair.
        /// </summary>
        private DottedDeclarationKind PeekDottedDeclarationKind()
        {
            var next = AdvancePeek(false).Token;

            while (next.TokenType == TType.Id)
            {
                next = AdvancePeek(false).Token;

                if (next.TokenType == TType.Dot)
                {
                    next = AdvancePeek(false).Token;
                }
                else
                {
                    DottedDeclarationKind kind = DottedDeclarationKind.None;

                    if (next.TokenType == TType.Id)
                    {
                        kind = AdvancePeek(false).Token.TokenType == TType.LeftParenthesis
                            ? DottedDeclarationKind.Function
                            : DottedDeclarationKind.Variable;
                    }

                    ResetPeek();
                    return kind;
                }
            }

            ResetPeek();
            return DottedDeclarationKind.None;
        }

        /// <summary>
        /// Parses an expression used as a statement, discarding its value
        /// (e.g. new Batch().run(); or new MyClass();)
        /// </summary>
        internal Statement ExpressionStatement()
        {
            var start = currentScanResult;

            // The expression value is discarded, so void calls are valid here
            bool validateReturnTypeSave = validateReturnType;
            validateReturnType = false;

            Expression expression;

            try
            {
                expression = Expression();
            }
            finally
            {
                validateReturnType = validateReturnTypeSave;
            }

            var end = Match(TType.Semicolon);

            if (expression is FunctionCall functionCall)
            {
                return new NoReturnFunctionCall(functionCall, SourceCodeBinding(start, end));
            }

            HandleParseError(MessageProvider.ExceptionInvalidSyntax);
            return null;
        }

        #region Class declarations

        private ClassDeclarationInfo _currentClassInfo;
        private string _currentMethodName;
        private string _pendingExtensionTargetName;
        private readonly List<XppAttributeDeclaration> _pendingTypeAttributes = new List<XppAttributeDeclaration>();

        private sealed class ExtensibilityAttributeSettings
        {
            public bool? Hookable;
            public bool? Wrappable;
            public bool? Replaceable;
        }

        private static readonly TType[] MemberTypeTokens = new[]
        {
            TType.Id,
            TType.TypeAnytype,
            TType.TypeBoolean,
            TType.TypeContainer,
            TType.TypeInt32,
            TType.TypeInt64,
            TType.TypeGuid,
            TType.TypeReal,
            TType.TypeStr,
            TType.TypeTimeOfDay,
            TType.TypeDate,
            TType.TypeDatetime,
        };

        private static bool IsMemberTypeToken(TType type)
        {
            return System.Array.IndexOf(MemberTypeTokens, type) >= 0;
        }

        private static bool IsMemberModifierToken(TType type)
        {
            return type == TType.Public || type == TType.Protected || type == TType.Private ||
                   type == TType.Internal || type == TType.Static || type == TType.Final ||
                   type == TType.Abstract || type == TType.Const || type == TType.ReadOnly ||
                   type == TType.Client || type == TType.Server || type == TType.Display ||
                   type == TType.Edit;
        }

        private static bool IsTypeDeclarationModifier(TType type)
        {
            return type == TType.Public || type == TType.Private || type == TType.Internal ||
                   type == TType.Static || type == TType.Final || type == TType.Abstract ||
                   type == TType.Client || type == TType.Server;
        }

        private bool IsInterfaceDeclarationAhead()
        {
            if (currentToken.TokenType == TType.Interface)
            {
                return true;
            }

            int offset = -1;
            Token token = currentToken;

            while (IsTypeDeclarationModifier(token.TokenType))
            {
                offset++;
                token = _lexer.Peek(offset).Token;
            }

            ResetPeek();
            return token.TokenType == TType.Interface;
        }

        /// <summary>
        /// Parses an interface declaration: method signatures only, no bodies.
        /// Interfaces produce no bytecode; they only populate the class table.
        /// </summary>
        internal Statement InterfaceDeclaration()
        {
            var start = currentScanResult;
            List<XppAttributeDeclaration> typeAttributes = _pendingTypeAttributes.ToList();
            _pendingTypeAttributes.Clear();
            _pendingExtensionTargetName = null;

            while (IsTypeDeclarationModifier(currentToken.TokenType))
            {
                Match(currentToken.TokenType);
            }

            Match(TType.Interface);
            string interfaceName = ((Word)Match(TType.Id).Token).Lexeme;

            var info = new ClassDeclarationInfo
            {
                Name = interfaceName,
                IsInterface = true,
            };

            info.Attributes.AddRange(typeAttributes);

            info.Type = new Core.ScriptClassType(interfaceName, info);

            if (currentToken.TokenType == TType.Extends)
            {
                Match(TType.Extends);
                info.BaseClassName = ((Word)Match(TType.Id).Token).Lexeme;
                info.BaseClass = _parseContext.DeclaredClasses.Find(info.BaseClassName);

                if (info.BaseClass != null && !info.BaseClass.IsInterface)
                {
                    HandleParseError($"Interface {interfaceName} can only extend another interface.", stop: false);
                }

                // A base interface that is not script-declared (e.g. compiled) is
                // kept by name so it stays part of implementers' interface closure
                if (info.BaseClass is null)
                {
                    info.ImplementsNames.Add(info.BaseClassName);
                }
            }

            if (_parseContext.DeclaredClasses.Contains(interfaceName))
            {
                HandleParseError($"Type {interfaceName} is already declared.", stop: false);
            }

            _parseContext.DeclaredClasses.Add(info);

            Match(TType.LeftBrace);
            _parseContext.BeginScope();

            while (currentToken.TokenType != TType.RightBrace && currentToken.TokenType != TType.EOF)
            {
                var signature = new ClassMethodSignature
                {
                    Access = AccessModifier.Public,
                };

                var typeResult = MatchMultiple(
                    TType.Id,
                    TType.TypeAnytype,
                    TType.TypeBoolean,
                    TType.TypeContainer,
                    TType.TypeInt32,
                    TType.TypeInt64,
                    TType.TypeGuid,
                    TType.TypeReal,
                    TType.TypeStr,
                    TType.TypeTimeOfDay,
                    TType.TypeDate,
                    TType.TypeDatetime,
                    TType.Void);

                Word returnType = typeResult.Token as Word;

                if (returnType.TokenType == TType.Void)
                {
                    returnType = Word.Void;
                    signature.ReturnClrType = typeof(void);
                }
                else
                {
                    signature.ReturnClrType = _typeInferer.InferType(returnType.Lexeme);
                }

                signature.ReturnType = returnType;
                signature.Name = ((Word)MatchAnyWord().Token).Lexeme;

                Match(TType.LeftParenthesis);

                while (currentToken.TokenType != TType.RightParenthesis && currentToken.TokenType != TType.EOF)
                {
                    signature.Parameters.Add(FunctionDeclarationParameter());

                    if (currentToken.TokenType != TType.RightParenthesis)
                    {
                        Match(TType.Comma);
                    }
                }

                Match(TType.RightParenthesis);
                Match(TType.Semicolon);

                if (info.Methods.Any(m => string.Equals(m.Name, signature.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    HandleParseError($"Method {signature.Name} is already declared in interface {interfaceName}.", stop: false);
                }

                info.Methods.Add(signature);
            }

            _parseContext.EndScope();
            var end = Match(TType.RightBrace);

            return new EmptyStatement(SourceCodeBinding(start, end));
        }

        internal ClassDeclaration ClassDeclaration()
        {
            var start = currentScanResult;
            List<XppAttributeDeclaration> typeAttributes = _pendingTypeAttributes.ToList();
            _pendingTypeAttributes.Clear();

            bool isAbstract = false, isFinal = false, isStatic = false;

            while (IsTypeDeclarationModifier(currentToken.TokenType))
            {
                if (currentToken.TokenType == TType.Abstract) isAbstract = true;
                else if (currentToken.TokenType == TType.Final) isFinal = true;
                else if (currentToken.TokenType == TType.Static) isStatic = true;

                Match(currentToken.TokenType);
            }

            Match(TType.Class);

            var headerEnd = Match(TType.Id);
            string className = ((Word)headerEnd.Token).Lexeme;

            string baseClassName = null;
            var implementsNames = new List<string>();

            if (currentToken.TokenType == TType.Extends)
            {
                Match(TType.Extends);
                baseClassName = ((Word)Match(TType.Id).Token).Lexeme;
                headerEnd = lastScanResult;
            }

            if (currentToken.TokenType == TType.Implements)
            {
                do
                {
                    Match(currentToken.TokenType == TType.Implements ? TType.Implements : TType.Comma);
                    implementsNames.Add(DottedName());
                    headerEnd = lastScanResult;
                } while (currentToken.TokenType == TType.Comma);
            }

            var info = new ClassDeclarationInfo
            {
                Name = className,
                BaseClassName = baseClassName,
                ImplementsNames = implementsNames,
                IsAbstract = isAbstract,
                IsFinal = isFinal,
                IsStatic = isStatic,
                ExtensionTargetName = _pendingExtensionTargetName,
            };

            info.Attributes.AddRange(typeAttributes);

            info.ExtensionTarget = _parseContext.DeclaredClasses.Find(info.ExtensionTargetName);

            if (info.ExtensionTarget is null && !string.IsNullOrEmpty(info.ExtensionTargetName))
            {
                Interpreter.Metadata.AosTypeDescriptor compiledTarget = _aosTypes.DescribeType(info.ExtensionTargetName);

                if (compiledTarget != null)
                {
                    // Represent a real AOS class/table in the same declaration graph
                    // used for script targets. Its members are materialized lazily
                    // from SysDict metadata as wrapper names are encountered.
                    info.ExtensionTarget = new ClassDeclarationInfo
                    {
                        Name = compiledTarget.Name,
                        Type = compiledTarget.ClrType,
                        CompiledTypeDescriptor = compiledTarget,
                    };
                }
                else
                {
                    HandleParseError($"Extension target {info.ExtensionTargetName} was not found in the script or AOS metadata.", stop: false);
                }
            }

            _pendingExtensionTargetName = null;

            info.Type = new Core.ScriptClassType(className, info);

            if (baseClassName != null)
            {
                info.BaseClass = _parseContext.DeclaredClasses.Find(baseClassName);

                if (info.BaseClass is null)
                {
                    // A compiled X++ base class is wrapped at runtime: the script
                    // object holds a base instance and forwards unknown members
                    System.Type compiledBase = null;

                    try
                    {
                        compiledBase = _proxy.Casting.GetSystemTypeFromTypeName(baseClassName);
                    }
                    catch
                    {
                    }

                    if (compiledBase != null)
                    {
                        info.CompiledBaseType = compiledBase;
                    }
                    else
                    {
                        HandleParseError($"Base class {baseClassName} was not found.", stop: false);
                    }
                }
                else if (info.BaseClass.IsInterface)
                {
                    HandleParseError($"Class {className} cannot extend interface {baseClassName}; use implements.", stop: false);
                }
                else if (info.BaseClass.IsFinal)
                {
                    HandleParseError($"Cannot extend final class {baseClassName}.", stop: false);
                }
            }

            // Resolve script-declared interfaces (unknown names may be compiled
            // X++ interfaces and are kept as names only)
            foreach (string implementsName in implementsNames)
            {
                var implemented = _parseContext.DeclaredClasses.Find(implementsName);

                if (implemented != null)
                {
                    if (!implemented.IsInterface)
                    {
                        HandleParseError($"{implementsName} is not an interface.", stop: false);
                    }
                    else
                    {
                        info.ImplementedInterfaces.Add(implemented);
                    }
                }
                else
                {
                    System.Type compiledInterface = null;
                    try { compiledInterface = _proxy.Casting.GetSystemTypeFromTypeName(implementsName); }
                    catch { }

                    if (compiledInterface is null)
                    {
                        HandleParseError($"Interface {implementsName} was not found.", stop: false);
                    }
                    else if (!compiledInterface.IsInterface)
                    {
                        HandleParseError($"{implementsName} is not an interface.", stop: false);
                    }
                    else
                    {
                        info.CompiledInterfaces.Add(compiledInterface);
                    }
                }
            }

            if (_parseContext.DeclaredClasses.Contains(className))
            {
                HandleParseError($"Class {className} is already declared.", stop: false);
            }

            _parseContext.DeclaredClasses.Add(info);

            Match(TType.LeftBrace);

            _parseContext.BeginScope();

            // Pre-scan member signatures so methods can reference members declared
            // after them (mutual recursion, fields at the bottom, ...)
            PreScanClassMembers(info);
            AddRuntimeArtifactMembers(info);
            BindCompiledExtensionTargetMethods(info);

            var declaration = new ClassDeclaration(
                className,
                baseClassName,
                implementsNames,
                isAbstract,
                isFinal,
                isStatic,
                info.ExtensionTargetName,
                null,
                SourceCodeBinding(start, headerEnd))
            {
                Info = info
            };
            declaration.Attributes.AddRange(typeAttributes);

            while (currentToken.TokenType != TType.RightBrace && currentToken.TokenType != TType.EOF)
            {
                ParseClassMember(declaration, info);
            }

            _parseContext.EndScope();

            var end = Match(TType.RightBrace);

            ValidateClassContracts(info);

            return declaration;
        }

        private void ValidateClassContracts(ClassDeclarationInfo info)
        {
            if (!string.IsNullOrEmpty(info.ExtensionTargetName))
            {
                if (!info.IsFinal)
                {
                    HandleParseError($"Extension class {info.Name} must be final.", stop: false);
                }

                if (!info.Name.EndsWith("_Extension", StringComparison.OrdinalIgnoreCase))
                {
                    HandleParseError($"Extension class {info.Name} must have a name ending in _Extension.", stop: false);
                }

                if (info.ExtensionTarget != null)
                {
                    foreach (var extensionField in info.Fields)
                    {
                        if (info.ExtensionTarget.FindField(extensionField.Name) != null)
                        {
                            HandleParseError($"Extension field {info.Name}.{extensionField.Name} conflicts with a field on {info.ExtensionTarget.Name}.", stop: false);
                        }
                    }
                }
            }

            if (info.IsStatic && info.IsAbstract)
            {
                HandleParseError($"Static class {info.Name} cannot be abstract.", stop: false);
            }

            if (info.IsStatic)
            {
                foreach (var field in info.Fields.Where(field => !field.IsStatic))
                {
                    HandleParseError($"Field {info.Name}.{field.Name} must be static because {info.Name} is a static class.", stop: false);
                }
            }

            foreach (var method in info.Methods)
            {
                if (method.IsWrappable == true &&
                    method.Access != AccessModifier.Public && method.Access != AccessModifier.Protected)
                {
                    HandleParseError($"Method {info.Name}.{method.Name} cannot opt in to Chain of Command because it is {method.Access.ToString().ToLowerInvariant()}.", stop: false);
                }

                if (method.IsReplaceable == true && !IsEffectivelyWrappable(method))
                {
                    HandleParseError($"Method {info.Name}.{method.Name} is replaceable but not wrappable.", stop: false);
                }

                if (info.IsStatic && !method.IsStatic)
                {
                    HandleParseError($"Method {info.Name}.{method.Name} must be static because {info.Name} is a static class.", stop: false);
                }

                if (string.Equals(method.Name, "new", StringComparison.OrdinalIgnoreCase))
                {
                    if (method.IsFinal || method.IsStatic || method.IsAbstract)
                    {
                        HandleParseError($"Constructor {info.Name}.new cannot be final, static, or abstract.", stop: false);
                    }

                    if (info.ExtensionTarget != null && (method.Parameters.Count != 0 || method.Access != AccessModifier.Private))
                    {
                        HandleParseError($"Extension constructor {info.Name}.new must be private and parameterless.", stop: false);
                    }

                    continue;
                }

                if (string.Equals(method.Name, "TypeNew", StringComparison.OrdinalIgnoreCase))
                {
                    if (!method.IsStatic || method.Parameters.Count != 0 || method.ReturnType.TokenType != TType.Void)
                    {
                        HandleParseError($"Static constructor {info.Name}.TypeNew must be declared as static void TypeNew() with no parameters.", stop: false);
                    }
                }

                if (method.IsAbstract && (method.IsFinal || method.IsStatic || method.Access == AccessModifier.Private))
                {
                    HandleParseError($"Abstract method {info.Name}.{method.Name} cannot be final, static, or private.", stop: false);
                }

                var baseMethod = info.BaseClass?.FindMethod(method.Name);
                var baseOwner = info.BaseClass?.FindMethodOwner(method.Name);

                if (baseMethod is null)
                {
                    if (info.ExtensionTarget != null)
                    {
                        ClassMethodSignature targetMethod = info.ExtensionTarget.FindMethod(method.Name);

                        if (targetMethod != null &&
                            (!IsEffectivelyWrappable(targetMethod) ||
                             targetMethod.IsStatic != method.IsStatic || !MethodSignaturesMatch(method, targetMethod)))
                        {
                            HandleParseError($"Chain of Command wrapper {info.Name}.{method.Name} must match a wrappable method on {info.ExtensionTarget.Name}.", stop: false);
                        }
                    }

                    continue;
                }

                // Private methods aren't inherited as overridable members. A
                // same-named method in a subclass is a new private implementation.
                if (baseMethod.Access == AccessModifier.Private)
                {
                    continue;
                }

                if (baseMethod.IsStatic || method.IsStatic)
                {
                    HandleParseError($"Static method {info.Name}.{method.Name} cannot override {baseOwner.Name}.{baseMethod.Name}.", stop: false);
                }
                else if (baseMethod.IsFinal)
                {
                    HandleParseError($"Method {info.Name}.{method.Name} cannot override final method {baseOwner.Name}.{baseMethod.Name}.", stop: false);
                }
                else if (!MethodSignaturesMatch(method, baseMethod))
                {
                    HandleParseError($"Override {info.Name}.{method.Name} must have the same parameter and return types as {baseOwner.Name}.{baseMethod.Name}.", stop: false);
                }
                else if (!CanOverrideWithAccess(baseMethod.Access, method.Access))
                {
                    HandleParseError($"Override {info.Name}.{method.Name} cannot be less accessible than {baseOwner.Name}.{baseMethod.Name}.", stop: false);
                }
            }

            if (info.IsAbstract)
            {
                return;
            }

            for (var baseInfo = info.BaseClass; baseInfo != null; baseInfo = baseInfo.BaseClass)
            {
                foreach (var abstractMethod in baseInfo.Methods.Where(m => m.IsAbstract))
                {
                    var implementation = info.FindMethod(abstractMethod.Name);

                    if (implementation is null || implementation.IsAbstract || !MethodSignaturesMatch(implementation, abstractMethod))
                    {
                        HandleParseError($"Class {info.Name} does not implement abstract method {baseInfo.Name}.{abstractMethod.Name}.", stop: false);
                    }
                    else if (!CanOverrideWithAccess(abstractMethod.Access, implementation.Access))
                    {
                        HandleParseError($"Implementation {info.Name}.{implementation.Name} cannot be less accessible than abstract method {baseInfo.Name}.{abstractMethod.Name}.", stop: false);
                    }
                }
            }

            foreach (var implemented in info.ImplementedInterfaces)
            {
                for (var interfaceInfo = implemented; interfaceInfo != null; interfaceInfo = interfaceInfo.BaseClass)
                {
                    foreach (var interfaceMethod in interfaceInfo.Methods)
                    {
                        var implementation = info.FindMethod(interfaceMethod.Name);

                        if (implementation is null || implementation.IsAbstract || implementation.IsStatic ||
                            implementation.Access != AccessModifier.Public || !MethodSignaturesMatch(implementation, interfaceMethod))
                        {
                            HandleParseError($"Class {info.Name} does not correctly implement {interfaceInfo.Name}.{interfaceMethod.Name}.", stop: false);
                        }
                    }
                }
            }

            foreach (System.Type compiledInterface in info.CompiledInterfaces)
            {
                foreach (System.Reflection.MethodInfo interfaceMethod in compiledInterface.GetMethods()
                    .Where(method => !method.IsSpecialName))
                {
                    ClassMethodSignature implementation = info.FindMethod(interfaceMethod.Name);

                    if (implementation is null || implementation.IsAbstract || implementation.IsStatic ||
                        implementation.Access != AccessModifier.Public ||
                        !MethodSignatureMatchesClr(implementation, interfaceMethod))
                    {
                        HandleParseError($"Class {info.Name} does not correctly implement {compiledInterface.Name}.{interfaceMethod.Name}.", stop: false);
                    }
                }
            }
        }

        /// <summary>
        /// Imports only the compiled methods an extension actually mentions. This
        /// keeps parsing cheap while giving CoC exact AOS signatures/default counts.
        /// </summary>
        private void BindCompiledExtensionTargetMethods(ClassDeclarationInfo extensionInfo)
        {
            ClassDeclarationInfo target = extensionInfo?.ExtensionTarget;
            if (target?.CompiledTypeDescriptor is null) return;

            foreach (ClassMethodSignature wrapper in extensionInfo.Methods)
            {
                if (string.Equals(wrapper.Name, "new", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(wrapper.Name, "TypeNew", StringComparison.OrdinalIgnoreCase) ||
                    target.FindMethod(wrapper.Name, walkBaseChain: false) != null)
                {
                    continue;
                }

                Interpreter.Metadata.AosMethodDescriptor method = target.CompiledTypeDescriptor.FindMethod(wrapper.Name);
                if (method is null) continue; // public augmentation, not a wrapper

                Word returnType = string.Equals(method.ReturnTypeName, "void", StringComparison.OrdinalIgnoreCase)
                    ? Word.Void
                    : new Word(method.ReturnTypeName ?? "anytype", TType.Id);

                var signature = new ClassMethodSignature
                {
                    Name = method.Name,
                    ReturnType = returnType,
                    ReturnClrType = method.ReturnClrType,
                    IsStatic = method.IsStatic,
                    IsAbstract = method.IsAbstract,
                    IsFinal = method.IsFinal,
                    IsHookable = method.IsHookable,
                    IsWrappable = method.IsWrappable,
                    IsReplaceable = method.IsReplaceable,
                    Access = method.Access,
                    RequiredParameterCount = method.RequiredParameterCount,
                };

                foreach (Interpreter.Metadata.AosMethodParameterDescriptor parameter in method.Parameters)
                {
                    string parameterTypeName = parameter.TypeName ?? "anytype";
                    signature.Parameters.Add(new FunctionDeclarationParameter(
                        new Word(parameterTypeName, TType.Id),
                        parameter.ClrType,
                        parameter.Name,
                        null));
                }

                target.Methods.Add(signature);
            }
        }

        private static void AddRuntimeArtifactMembers(ClassDeclarationInfo info)
        {
            XppAttributeDeclaration runtimeAttribute = info.Attributes.FirstOrDefault(attribute =>
            {
                string name = attribute.Name ?? string.Empty;
                if (name.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - "Attribute".Length);
                }
                return string.Equals(name, "RuntimeTable", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "RuntimeDataEntity", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "RuntimeView", StringComparison.OrdinalIgnoreCase) ||
                       (string.Equals(name, "RuntimeModel", StringComparison.OrdinalIgnoreCase) &&
                        attribute.Words.Skip(1).Any(word =>
                            string.Equals(word, "Table", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(word, "DataEntity", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(word, "View", StringComparison.OrdinalIgnoreCase)));
            });

            if (runtimeAttribute is null) return;

            void Add(string name, Word returnType, System.Type returnClrType)
            {
                if (info.FindMethod(name, walkBaseChain: false) != null) return;
                info.Methods.Add(new ClassMethodSignature
                {
                    Name = name,
                    ReturnType = returnType,
                    ReturnClrType = returnClrType,
                    Access = AccessModifier.Public,
                });
            }

            Word voidType = Word.Void;
            Word booleanType = new Word("boolean", TType.TypeBoolean);
            Add("clear", voidType, typeof(void));
            Add("reread", booleanType, typeof(bool));

            bool readOnly = runtimeAttribute.Name.IndexOf("View", StringComparison.OrdinalIgnoreCase) >= 0 ||
                runtimeAttribute.Words.Skip(1).Any(word => string.Equals(word, "View", StringComparison.OrdinalIgnoreCase));
            if (!readOnly)
            {
                foreach (string method in new[] { "insert", "doInsert", "update", "doUpdate", "delete", "doDelete", "write" })
                {
                    Add(method, voidType, typeof(void));
                }
            }
        }

        private static bool CanOverrideWithAccess(AccessModifier baseAccess, AccessModifier derivedAccess)
        {
            switch (baseAccess)
            {
                case AccessModifier.Public:
                    return derivedAccess == AccessModifier.Public;
                case AccessModifier.Protected:
                    return derivedAccess == AccessModifier.Protected || derivedAccess == AccessModifier.Public;
                case AccessModifier.Internal:
                    return derivedAccess == AccessModifier.Internal || derivedAccess == AccessModifier.Public;
                default:
                    return false;
            }
        }

        private static bool IsEffectivelyWrappable(ClassMethodSignature method)
        {
            if (method is null ||
                (method.Access != AccessModifier.Public && method.Access != AccessModifier.Protected) ||
                method.IsHookable == false)
            {
                return false;
            }

            if (method.IsWrappable.HasValue)
            {
                return method.IsWrappable.Value;
            }

            return !method.IsFinal;
        }

        private static bool MethodSignaturesMatch(ClassMethodSignature left, ClassMethodSignature right)
        {
            if (!TypeNamesMatch(left.ReturnType, right.ReturnType) || left.Parameters.Count != right.Parameters.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Parameters.Count; index++)
            {
                if (!TypeNamesMatch(left.Parameters[index].DeclarationType, right.Parameters[index].DeclarationType))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MethodSignatureMatchesClr(ClassMethodSignature script, System.Reflection.MethodInfo compiled)
        {
            if (script.ReturnClrType != compiled.ReturnType || script.Parameters.Count != compiled.GetParameters().Length)
            {
                return false;
            }

            System.Reflection.ParameterInfo[] parameters = compiled.GetParameters();
            for (int index = 0; index < parameters.Length; index++)
            {
                System.Type compiledType = parameters[index].ParameterType.IsByRef
                    ? parameters[index].ParameterType.GetElementType()
                    : parameters[index].ParameterType;
                System.Type scriptType = script.Parameters[index].DeclarationClrType;

                if (compiledType != scriptType && !(compiledType?.IsAssignableFrom(scriptType) ?? false))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TypeNamesMatch(Word left, Word right)
        {
            return left != null && right != null && string.Equals(left.Lexeme, right.Lexeme, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Walks the class body with peeks only (no token consumption) and registers
        /// every field and method signature into the class info and the class parse
        /// scope, enabling forward references between members.
        /// </summary>
        private void PreScanClassMembers(ClassDeclarationInfo info)
        {
            // Index -1 denotes the current token; peeks start at 0
            Token TokenAt(int tokenIndex) => tokenIndex < 0 ? currentToken : _lexer.Peek(tokenIndex).Token;

            int index = -1;
            int depth = 1;
            var modifiers = new ClassMemberModifiers();
            var attributes = new ExtensibilityAttributeSettings();

            while (depth > 0)
            {
                Token token = TokenAt(index);

                if (token.TokenType == TType.EOF)
                {
                    break;
                }

                if (token.TokenType == TType.LeftBrace)
                {
                    depth++;
                    index++;
                    continue;
                }

                if (token.TokenType == TType.RightBrace)
                {
                    depth--;
                    index++;
                    continue;
                }

                if (depth != 1)
                {
                    index++;
                    continue;
                }

                if (token.TokenType == TType.LeftBracket)
                {
                    var attributeTokens = new List<Token>();
                    int attributeDepth = 0;

                    do
                    {
                        Token attributeToken = TokenAt(index);
                        attributeTokens.Add(attributeToken);

                        if (attributeToken.TokenType == TType.LeftBracket) attributeDepth++;
                        else if (attributeToken.TokenType == TType.RightBracket) attributeDepth--;

                        index++;
                    } while (attributeDepth > 0 && TokenAt(index).TokenType != TType.EOF);

                    ApplyExtensibilityAttributes(attributeTokens, attributes);
                    continue;
                }

                if (IsMemberModifierToken(token.TokenType))
                {
                    if (token.TokenType == TType.Static) modifiers.IsStatic = true;
                    else if (token.TokenType == TType.Public) modifiers.Access = AccessModifier.Public;
                    else if (token.TokenType == TType.Protected) modifiers.Access = AccessModifier.Protected;
                    else if (token.TokenType == TType.Private) modifiers.Access = AccessModifier.Private;
                    else if (token.TokenType == TType.Internal) modifiers.Access = AccessModifier.Internal;
                    else if (token.TokenType == TType.Const || token.TokenType == TType.ReadOnly) modifiers.IsConst = true;
                    else if (token.TokenType == TType.Abstract) modifiers.IsAbstract = true;
                    else if (token.TokenType == TType.Final) modifiers.IsFinal = true;

                    index++;
                    continue;
                }

                if (token.TokenType == TType.Void || token.TokenType == TType.Var || IsMemberTypeToken(token.TokenType))
                {
                    Word typeWord = token as Word;

                    // Dotted type names
                    while (TokenAt(index + 1).TokenType == TType.Dot && TokenAt(index + 2).TokenType == TType.Id)
                    {
                        typeWord = new Word(typeWord.Lexeme + "." + (TokenAt(index + 2) as Word).Lexeme, TType.Id);
                        index += 2;
                    }

                    // Length-limited strings (str 40 name;)
                    if (token.TokenType == TType.TypeStr && TokenAt(index + 1).TokenType == TType.Int32)
                    {
                        index++;
                    }

                    Token nameToken = TokenAt(index + 1);

                    // Method and field names may be soft keywords (sum, count, index, ...)
                    if (nameToken is Word || nameToken.TokenType == TType.New)
                    {
                        string memberName = nameToken is Word nameWord ? nameWord.Lexeme : "new";

                        if (TokenAt(index + 2).TokenType == TType.LeftParenthesis)
                        {
                            // Method: collect the parameter signature
                            var signature = new ClassMethodSignature
                            {
                                Name = memberName,
                                ReturnType = token.TokenType == TType.Void ? Word.Void : typeWord,
                                IsStatic = modifiers.IsStatic,
                                IsAbstract = modifiers.IsAbstract,
                                IsFinal = modifiers.IsFinal,
                                IsHookable = attributes.Hookable,
                                IsWrappable = attributes.Wrappable,
                                IsReplaceable = attributes.Replaceable,
                                Access = modifiers.Access ?? AccessModifier.Public,
                            };

                            signature.ReturnClrType = token.TokenType == TType.Void ? typeof(void) : _typeInferer.InferType(typeWord.Lexeme);

                            index += 3; // at first token inside parentheses

                            int parenthesisDepth = 1;
                            bool optionalParameterSeen = false;

                            while (parenthesisDepth > 0)
                            {
                                Token parameterToken = TokenAt(index);

                                if (parameterToken.TokenType == TType.EOF) break;
                                if (parameterToken.TokenType == TType.LeftParenthesis) { parenthesisDepth++; index++; continue; }
                                if (parameterToken.TokenType == TType.RightParenthesis) { parenthesisDepth--; index++; continue; }

                                // A parameter is Type Name [= default]
                                if (parenthesisDepth == 1 && (IsMemberTypeToken(parameterToken.TokenType)) &&
                                    TokenAt(index + 1).TokenType == TType.Id)
                                {
                                    Word parameterType = parameterToken as Word;
                                    Word parameterName = TokenAt(index + 1) as Word;

                                    signature.Parameters.Add(new FunctionDeclarationParameter(
                                        parameterType,
                                        _typeInferer.InferType(parameterType.Lexeme),
                                        parameterName.Lexeme,
                                        null));

                                    if (TokenAt(index + 2).TokenType == TType.Assign)
                                    {
                                        optionalParameterSeen = true;
                                    }
                                    else if (!optionalParameterSeen)
                                    {
                                        signature.RequiredParameterCount++;
                                    }

                                    index += 2;
                                    continue;
                                }

                                index++;
                            }

                            if (info.Methods.Any(m => string.Equals(m.Name, signature.Name, StringComparison.OrdinalIgnoreCase)))
                            {
                                HandleParseError($"Method {signature.Name} is already declared in class {info.Name} (overloads are not supported in X++).", stop: false);
                            }

                            info.Methods.Add(signature);
                            modifiers = new ClassMemberModifiers();
                            attributes = new ExtensibilityAttributeSettings();
                            continue;
                        }
                        else
                        {
                            // Field(s): Type a, b = expr, c;
                            int fieldIndex = index + 1;

                            while (true)
                            {
                                Token fieldToken = TokenAt(fieldIndex);

                                if (fieldToken.TokenType == TType.Id || IsSoftIdentifierToken(fieldToken.TokenType))
                                {
                                    string fieldName = (fieldToken as Word).Lexeme;
                                    System.Type fieldClrType = _typeInferer.InferType(typeWord.Lexeme);

                                    info.Fields.Add(new ClassFieldSignature
                                    {
                                        Name = fieldName,
                                        Type = typeWord,
                                        ClrType = fieldClrType,
                                        IsStatic = modifiers.IsStatic,
                                        IsConst = modifiers.IsConst,
                                        Access = modifiers.Access ?? AccessModifier.Protected,
                                    });

                                    // Only static fields are visible at class scope; instance
                                    // fields are registered per instance-method scope
                                    if (modifiers.IsStatic)
                                    {
                                        _parseContext.CurrentScope.VariableDeclarations.Add(
                                            new ParseContextScopeVariable(fieldName, typeWord, fieldClrType, false)
                                            {
                                                IsConst = modifiers.IsConst
                                            });
                                    }

                                    if (info.Fields.Count(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase)) > 1)
                                    {
                                        HandleParseError($"Field {fieldName} is already declared in class {info.Name}.", stop: false);
                                    }

                                    fieldIndex++;
                                }

                                // Skip to the next comma or the terminating semicolon
                                int expressionDepth = 0;

                                while (true)
                                {
                                    Token skipToken = TokenAt(fieldIndex);

                                    if (skipToken.TokenType == TType.EOF || skipToken.TokenType == TType.Semicolon && expressionDepth == 0)
                                    {
                                        break;
                                    }

                                    if (skipToken.TokenType == TType.LeftParenthesis || skipToken.TokenType == TType.LeftBracket) expressionDepth++;
                                    if (skipToken.TokenType == TType.RightParenthesis || skipToken.TokenType == TType.RightBracket) expressionDepth--;

                                    if (skipToken.TokenType == TType.Comma && expressionDepth == 0)
                                    {
                                        fieldIndex++;
                                        break;
                                    }

                                    fieldIndex++;
                                }

                                if (TokenAt(fieldIndex).TokenType != TType.Id || TokenAt(fieldIndex - 1).TokenType == TType.Semicolon)
                                {
                                    break;
                                }
                            }

                            index = fieldIndex;
                            modifiers = new ClassMemberModifiers();
                            attributes = new ExtensibilityAttributeSettings();
                            continue;
                        }
                    }
                }

                index++;
            }

            ResetPeek();
        }

        private static void ApplyExtensibilityAttributes(
            List<Token> tokens,
            ExtensibilityAttributeSettings settings)
        {
            for (int index = 0; index < tokens.Count; index++)
            {
                if (!(tokens[index] is Word attributeWord))
                {
                    continue;
                }

                string name = attributeWord.Lexeme;
                bool isHookable = string.Equals(name, "Hookable", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "HookableAttribute", StringComparison.OrdinalIgnoreCase);
                bool isWrappable = string.Equals(name, "Wrappable", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "WrappableAttribute", StringComparison.OrdinalIgnoreCase);
                bool isReplaceable = string.Equals(name, "Replaceable", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "ReplaceableAttribute", StringComparison.OrdinalIgnoreCase);

                if (!isHookable && !isWrappable && !isReplaceable)
                {
                    continue;
                }

                bool? argument = null;

                for (int argumentIndex = index + 1; argumentIndex < tokens.Count; argumentIndex++)
                {
                    Token argumentToken = tokens[argumentIndex];

                    if (argumentToken.TokenType == TType.Comma || argumentToken.TokenType == TType.RightBracket)
                    {
                        break;
                    }

                    if (argumentToken.TokenType == TType.True)
                    {
                        argument = true;
                        break;
                    }

                    if (argumentToken.TokenType == TType.False)
                    {
                        argument = false;
                        break;
                    }
                }

                if (isHookable) settings.Hookable = argument;
                else if (isWrappable) settings.Wrappable = argument;
                else settings.Replaceable = argument ?? true;
            }
        }

        private List<XppAttributeDeclaration> CaptureAttributeList()
        {
            int squareDepth = 0;
            int parenthesisDepth = 0;
            var attributes = new List<XppAttributeDeclaration>();
            XppAttributeDeclaration attribute = null;

            do
            {
                Token token = currentToken;

                if (token.TokenType == TType.LeftBracket)
                {
                    squareDepth++;
                }
                else if (token.TokenType == TType.RightBracket)
                {
                    squareDepth--;
                }
                else if (token.TokenType == TType.LeftParenthesis)
                {
                    parenthesisDepth++;
                }
                else if (token.TokenType == TType.RightParenthesis)
                {
                    parenthesisDepth--;
                }

                // X++ permits [AttributeOne(...), AttributeTwo(...)]. A comma
                // separates attributes only at the outer bracket level; commas
                // inside an attribute constructor belong to its arguments.
                if (token.TokenType == TType.Comma && squareDepth == 1 && parenthesisDepth == 0)
                {
                    attribute = null;
                    Move();
                    continue;
                }

                if (token is Word word && squareDepth > 0)
                {
                    if (attribute is null)
                    {
                        attribute = new XppAttributeDeclaration();
                        attributes.Add(attribute);
                    }

                    if (attribute.Name is null) attribute.Name = word.Lexeme;
                    attribute.Words.Add(word.Lexeme);
                }
                else if (token is BaseType literal && squareDepth > 0 && attribute != null)
                {
                    attribute.Words.Add(System.Convert.ToString(literal.Value, System.Globalization.CultureInfo.InvariantCulture));
                }

                Move();
            } while (squareDepth > 0 && currentToken.TokenType != TType.EOF);

            return attributes;
        }

        private static string GetExtensionTarget(IEnumerable<XppAttributeDeclaration> attributes)
        {
            XppAttributeDeclaration extension = attributes?.FirstOrDefault(attribute =>
                string.Equals(attribute.Name, "ExtensionOf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attribute.Name, "ExtensionOfAttribute", StringComparison.OrdinalIgnoreCase));

            if (extension != null && extension.Words.Count > 2)
            {
                return extension.Words[2];
            }

            return null;
        }

        /// <summary>
        /// Peeks past a bracketed list to check whether a declaration follows,
        /// which makes the brackets an attribute decoration
        /// </summary>
        private bool IsAttributeListAhead()
        {
            int depth = 1;
            int offset = -1;

            while (depth > 0)
            {
                offset++;
                var token = _lexer.Peek(offset).Token;

                if (token.TokenType == TType.EOF)
                {
                    ResetPeek();
                    return false;
                }

                if (token.TokenType == TType.LeftBracket) depth++;
                else if (token.TokenType == TType.RightBracket) depth--;
            }

            var next = _lexer.Peek(offset + 1).Token;
            ResetPeek();

            return next.TokenType == TType.Class || next.TokenType == TType.Abstract ||
                   next.TokenType == TType.Final || next.TokenType == TType.Static ||
                   next.TokenType == TType.Public || next.TokenType == TType.Private ||
                   next.TokenType == TType.Internal || next.TokenType == TType.Client ||
                   next.TokenType == TType.Server || next.TokenType == TType.Interface ||
                   next.TokenType == TType.LeftBracket;
        }

        private ClassMemberModifiers ParseClassMemberModifiers()
        {
            var modifiers = new ClassMemberModifiers();

            while (IsMemberModifierToken(currentToken.TokenType))
            {
                switch (currentToken.TokenType)
                {
                    case TType.Public: modifiers.Access = AccessModifier.Public; break;
                    case TType.Protected: modifiers.Access = AccessModifier.Protected; break;
                    case TType.Private: modifiers.Access = AccessModifier.Private; break;
                    case TType.Internal: modifiers.Access = AccessModifier.Internal; break;
                    case TType.Static: modifiers.IsStatic = true; break;
                    case TType.Final: modifiers.IsFinal = true; break;
                    case TType.Abstract: modifiers.IsAbstract = true; break;
                    case TType.Const:
                    case TType.ReadOnly: modifiers.IsConst = true; break;
                }

                Match(currentToken.TokenType);
            }

            return modifiers;
        }

        private void ParseClassMember(ClassDeclaration declaration, ClassDeclarationInfo info)
        {
            var memberAttributes = new List<XppAttributeDeclaration>();

            while (currentToken.TokenType == TType.LeftBracket)
            {
                memberAttributes.AddRange(CaptureAttributeList());
            }

            // delegate void name(params); - no body, raised by calling it inside the class
            if (currentToken.TokenType == TType.Delegate)
            {
                Match(TType.Delegate);
                Match(TType.Void);

                var delegateSignature = new ClassDelegateSignature
                {
                    Name = ((Word)MatchAnyWord().Token).Lexeme,
                    Attributes = memberAttributes,
                };

                Match(TType.LeftParenthesis);

                _parseContext.BeginScope();

                while (currentToken.TokenType != TType.RightParenthesis && currentToken.TokenType != TType.EOF)
                {
                    delegateSignature.Parameters.Add(FunctionDeclarationParameter());

                    if (currentToken.TokenType != TType.RightParenthesis)
                    {
                        Match(TType.Comma);
                    }
                }

                _parseContext.EndScope();

                Match(TType.RightParenthesis);
                Match(TType.Semicolon);

                if (info.FindDelegate(delegateSignature.Name) != null)
                {
                    HandleParseError($"Delegate {delegateSignature.Name} is already declared in class {info.Name} or a base class.", stop: false);
                }

                info.Delegates.Add(delegateSignature);

                // Register as a void method signature so invocations type-check
                info.Methods.Add(new ClassMethodSignature
                {
                    Name = delegateSignature.Name,
                    ReturnType = Word.Void,
                    ReturnClrType = typeof(void),
                    Parameters = delegateSignature.Parameters,
                    Access = AccessModifier.Public,
                });

                declaration.Delegates.Add(delegateSignature);
                return;
            }

            var modifiers = ParseClassMemberModifiers();

            // Method: [type|void] [name|new] '(' - otherwise a field declaration
            bool isMethod = false;

            if (currentToken.TokenType == TType.Void || IsMemberTypeToken(currentToken.TokenType))
            {
                int offset = 0;
                var next = AdvancePeek(false).Token;

                // Skip over dotted type names
                while (next.TokenType == TType.Dot)
                {
                    AdvancePeek(false); // Id
                    next = AdvancePeek(false).Token;
                }

                if (next is Word || next.TokenType == TType.New)
                {
                    isMethod = AdvancePeek(false).Token.TokenType == TType.LeftParenthesis;
                }

                ResetPeek();
                _ = offset;
            }

            if (isMethod)
            {
                declaration.Methods.Add(ParseClassMethod(info, modifiers, memberAttributes));
            }
            else
            {
                int scopeVariablesBefore = _parseContext.CurrentScope.VariableDeclarations.Count;

                var fieldDeclaration = VariableDeclaration(matchSemicolon: true);
                declaration.Fields.Add(new ClassFieldDeclaration
                {
                    Declaration = fieldDeclaration,
                    Modifiers = modifiers,
                    Attributes = memberAttributes,
                });

                if (fieldDeclaration is VariableDeclarations fieldVariables)
                {
                    foreach (Word fieldName in fieldVariables.Identifiers.Keys)
                    {
                        ClassFieldSignature signature = info.Fields.FirstOrDefault(field =>
                            string.Equals(field.Name, fieldName.Lexeme, StringComparison.OrdinalIgnoreCase));
                        if (signature != null)
                        {
                            signature.Attributes = memberAttributes;
                        }
                    }
                }

                // Instance fields must not be visible at class scope (static method
                // bodies parse there); they are re-registered per instance method
                if (!modifiers.IsStatic)
                {
                    int added = _parseContext.CurrentScope.VariableDeclarations.Count - scopeVariablesBefore;

                    if (added > 0)
                    {
                        _parseContext.CurrentScope.VariableDeclarations.RemoveRange(scopeVariablesBefore, added);
                    }
                }
            }
        }

        private ClassMethodDeclaration ParseClassMethod(
            ClassDeclarationInfo info,
            ClassMemberModifiers modifiers,
            List<XppAttributeDeclaration> attributes)
        {
            var start = MatchMultiple(
                TType.Id,
                TType.TypeAnytype,
                TType.TypeBoolean,
                TType.TypeContainer,
                TType.TypeInt32,
                TType.TypeInt64,
                TType.TypeGuid,
                TType.TypeReal,
                TType.TypeStr,
                TType.TypeTimeOfDay,
                TType.TypeDate,
                TType.TypeDatetime,
                TType.Void);

            Word returnType = start.Token as Word;

            if (returnType.TokenType == TType.Id && currentToken.TokenType == TType.Dot)
            {
                var qualifiedName = new System.Text.StringBuilder(returnType.Lexeme);

                while (currentToken.TokenType == TType.Dot)
                {
                    Match(TType.Dot);
                    qualifiedName.Append('.');
                    qualifiedName.Append(((Word)Match(TType.Id).Token).Lexeme);
                }

                returnType = new Word(qualifiedName.ToString(), TType.Id);
            }

            if (returnType.TokenType == TType.Void)
            {
                returnType = Word.Void;
            }
            else if (!_typeInferer.IsKnownType(returnType.Lexeme))
            {
                HandleParseError(string.Format(MessageProvider.ExceptionTypeNotFound, returnType.Lexeme), stop: false);
            }

            string methodName;

            if (currentToken.TokenType == TType.New)
            {
                Match(TType.New);
                methodName = "new";

                if (returnType.TokenType != TType.Void)
                {
                    HandleParseError("Constructors must be declared void.", stop: false);
                }
            }
            else
            {
                // Method names may be soft keywords (sum, count, index, ...)
                methodName = ((Word)MatchAnyWord().Token).Lexeme;
            }

            Match(TType.LeftParenthesis);

            var method = new ClassMethodDeclaration
            {
                Name = methodName,
                ReturnType = returnType,
                Modifiers = modifiers,
                Attributes = attributes ?? new List<XppAttributeDeclaration>(),
            };

            bool optionalParameterSeen = false;

            _parseContext.FunctionDeclarationStack.New();
            _parseContext.BeginScope();

            bool inClassMethodSave = _parseContext.InClassMethod;
            var currentClassSave = _currentClassInfo;
            var currentMethodSave = _currentMethodName;

            _parseContext.InClassMethod = true;
            _currentClassInfo = info;
            _currentMethodName = methodName;

            try
            {
                if (!modifiers.IsStatic)
                {
                    ClassDeclarationInfo thisType = info.ExtensionTarget ?? info;
                    _parseContext.CurrentScope.VariableDeclarations.Add(
                        new ParseContextScopeVariable("this", new Word(thisType.Name, TType.Id), thisType.Type, false));
                }

                // Register the fields visible to this method: instance fields only for
                // instance methods, static fields always, walking the base chain
                for (ClassDeclarationInfo fieldSource = info; fieldSource != null; fieldSource = fieldSource.BaseClass)
                {
                    foreach (var field in fieldSource.Fields)
                    {
                        if (field.IsStatic && ReferenceEquals(fieldSource, info))
                        {
                            continue; // already visible through the class scope
                        }

                        if (field.IsStatic || !modifiers.IsStatic)
                        {
                            if (!CanAccessMember(field.Access, fieldSource))
                            {
                                continue;
                            }

                            _parseContext.CurrentScope.VariableDeclarations.Add(
                                new ParseContextScopeVariable(field.Name, field.Type, field.ClrType, false)
                                {
                                    IsConst = field.IsConst
                                });
                        }
                    }
                }

                // A class extension sees the target's public/protected instance
                // state as part of its effective class.
                if (!modifiers.IsStatic && info.ExtensionTarget != null)
                {
                    for (ClassDeclarationInfo fieldSource = info.ExtensionTarget; fieldSource != null; fieldSource = fieldSource.BaseClass)
                    {
                        foreach (var field in fieldSource.Fields.Where(field => !field.IsStatic))
                        {
                            if (CanAccessMember(field.Access, fieldSource))
                            {
                                _parseContext.CurrentScope.VariableDeclarations.Add(
                                    new ParseContextScopeVariable(field.Name, field.Type, field.ClrType, false)
                                    {
                                        IsConst = field.IsConst
                                    });
                            }
                        }
                    }
                }

                while (currentToken.TokenType != TType.RightParenthesis && currentToken.TokenType != TType.EOF)
                {
                    method.Parameters.Add(FunctionDeclarationParameter());

                    // Optional parameter default value (compile-time literal)
                    if (currentToken.TokenType == TType.Assign)
                    {
                        Match(TType.Assign);
                        Expression defaultValue = Expression();

                        if (!(defaultValue is Constant))
                        {
                            HandleParseError("Parameter default values must be literals.", stop: false);
                        }

                        method.ParameterDefaults.Add(defaultValue);
                        optionalParameterSeen = true;
                    }
                    else
                    {
                        if (optionalParameterSeen)
                        {
                            HandleParseError("Required parameters must be declared before optional parameters.", stop: false);
                        }

                        method.ParameterDefaults.Add(null);
                    }

                    if (currentToken.TokenType != TType.RightParenthesis)
                    {
                        Match(TType.Comma);
                    }
                }

                Match(TType.RightParenthesis);

                if (modifiers.IsAbstract)
                {
                    if (!info.IsAbstract)
                    {
                        HandleParseError($"Abstract methods can only be declared in abstract classes.", stop: false);
                    }

                    // Abstract methods have no body
                    Match(TType.Semicolon);
                    method.Block = null;
                }
                else
                {
                    method.Block = Block();
                }

                method.SourceCodeBinding = SourceCodeBinding(start, lastScanResult);

                var signature = info.Methods.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase));

                if (signature != null)
                {
                    signature.Parameters = method.Parameters;
                    signature.RequiredParameterCount = method.ParameterDefaults.Count(value => value is null);
                    signature.Attributes = method.Attributes;

                    ClassMethodSignature targetMethod = info.ExtensionTarget?.FindMethod(methodName);

                    if (targetMethod != null)
                    {
                        ValidateChainOfCommandBody(info, method, targetMethod);
                    }
                }
            }
            finally
            {
                _parseContext.InClassMethod = inClassMethodSave;
                _currentClassInfo = currentClassSave;
                _currentMethodName = currentMethodSave;

                _parseContext.EndScope();
                _parseContext.FunctionDeclarationStack.Release();
            }

            return method;
        }

        private void ValidateChainOfCommandBody(
            ClassDeclarationInfo extensionClass,
            ClassMethodDeclaration wrapper,
            ClassMethodSignature targetMethod)
        {
            if (targetMethod.RequiredParameterCount < targetMethod.Parameters.Count &&
                wrapper.ParameterDefaults.Any(defaultValue => defaultValue != null))
            {
                HandleParseError(
                    $"Chain of Command wrapper {extensionClass.Name}.{wrapper.Name} must omit default parameter values from the wrapped method signature.",
                    stop: false);
            }

            if (wrapper.Block is null)
            {
                return;
            }

            if (ContainsConditionallyEvaluatedNextCall(wrapper.Block, false, new HashSet<object>()))
            {
                HandleParseError(
                    $"Chain of Command wrapper {extensionClass.Name}.{wrapper.Name} cannot call next from a logical or conditional expression.",
                    stop: false);
            }

            if (targetMethod.IsReplaceable == true)
            {
                return;
            }

            int directNextCount = wrapper.Block.Statements.Sum(CountFirstLevelNextCalls);

            if (directNextCount == 0)
            {
                string placement = ContainsNextCall(wrapper.Block, includeNestedStatements: true)
                    ? " at the first statement level, not conditionally or inside a loop"
                    : string.Empty;

                HandleParseError(
                    $"Chain of Command wrapper {extensionClass.Name}.{wrapper.Name} must call next {wrapper.Name}(){placement}.",
                    stop: false);
                return;
            }

            if (directNextCount > 1)
            {
                HandleParseError(
                    $"Chain of Command wrapper {extensionClass.Name}.{wrapper.Name} must call next exactly once.",
                    stop: false);
            }

            int firstNext = wrapper.Block.Statements.FindIndex(statement => CountFirstLevelNextCalls(statement) > 0);

            if (wrapper.Block.Statements.Take(firstNext).Any(statement => statement is Return))
            {
                HandleParseError(
                    $"A return statement cannot precede next {wrapper.Name}() in Chain of Command wrapper {extensionClass.Name}.{wrapper.Name}.",
                    stop: false);
            }
        }

        private static int CountFirstLevelNextCalls(Statement statement)
        {
            if (statement is Try tryStatement)
            {
                int count = tryStatement.TryBlock.Statements.Sum(CountFirstLevelNextCalls);
                count += tryStatement.Catches.Sum(catchBlock => catchBlock.Block.Statements.Sum(CountFirstLevelNextCalls));
                count += tryStatement.Finally?.Statements.Sum(CountFirstLevelNextCalls) ?? 0;
                return count;
            }

            if (statement is StatementGroup group)
            {
                return group.Statements.Sum(CountFirstLevelNextCalls);
            }

            // These statement forms make execution of their contents conditional.
            if (statement is If || statement is Loop || statement is Switch || statement is WhileSelect)
            {
                return 0;
            }

            return CountNextCallValue(
                statement,
                includeNestedStatements: false,
                isRootStatement: true,
                new HashSet<object>());
        }

        private static bool ContainsNextCall(Statement statement, bool includeNestedStatements)
        {
            return CountNextCallValue(
                statement,
                includeNestedStatements,
                isRootStatement: true,
                new HashSet<object>()) > 0;
        }

        private static int CountNextCallValue(
            object value,
            bool includeNestedStatements,
            bool isRootStatement,
            HashSet<object> visited)
        {
            if (value is null)
            {
                return 0;
            }

            if (value is NextCallExpression)
            {
                return 1;
            }

            if (value is Statement && !isRootStatement && !includeNestedStatements)
            {
                return 0;
            }

            if (value is StatementGroup statementGroup)
            {
                return statementGroup.Statements.Sum(statement =>
                    CountNextCallValue(statement, includeNestedStatements, isRootStatement: false, visited));
            }

            if (value is Catch catchBlock)
            {
                return CountNextCallValue(catchBlock.Block, includeNestedStatements, false, visited);
            }

            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                int count = 0;

                foreach (object item in enumerable)
                {
                    count += CountNextCallValue(item, includeNestedStatements, false, visited);
                }

                return count;
            }

            System.Type type = value.GetType();
            bool isAstNode = value is Statement || value is Expression;
            bool isKeyValuePair = type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);

            if (!isAstNode && !isKeyValuePair)
            {
                return 0;
            }

            if (!type.IsValueType && !visited.Add(value))
            {
                return 0;
            }

            int result = 0;

            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                object child;

                try
                {
                    child = property.GetValue(value, null);
                }
                catch
                {
                    continue;
                }

                result += CountNextCallValue(child, includeNestedStatements, false, visited);
            }

            return result;
        }

        private static bool ContainsConditionallyEvaluatedNextCall(
            object value,
            bool conditionallyEvaluated,
            HashSet<object> visited)
        {
            if (value is null)
            {
                return false;
            }

            if (value is NextCallExpression)
            {
                return conditionallyEvaluated;
            }

            if (value is BinaryOperation binary)
            {
                bool conditional = conditionallyEvaluated ||
                    binary.Token.TokenType == TType.And ||
                    binary.Token.TokenType == TType.Or;

                return ContainsConditionallyEvaluatedNextCall(binary.LeftOperand, conditional, visited) ||
                    ContainsConditionallyEvaluatedNextCall(binary.RightOperand, conditional, visited);
            }

            if (value is Ternary ternary)
            {
                return ContainsConditionallyEvaluatedNextCall(ternary.Condition, true, visited) ||
                    ContainsConditionallyEvaluatedNextCall(ternary.Left, true, visited) ||
                    ContainsConditionallyEvaluatedNextCall(ternary.Right, true, visited);
            }

            if (value is StatementGroup statementGroup)
            {
                return statementGroup.Statements.Any(statement =>
                    ContainsConditionallyEvaluatedNextCall(statement, conditionallyEvaluated, visited));
            }

            if (value is Catch catchBlock)
            {
                return ContainsConditionallyEvaluatedNextCall(catchBlock.Block, conditionallyEvaluated, visited);
            }

            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                foreach (object item in enumerable)
                {
                    if (ContainsConditionallyEvaluatedNextCall(item, conditionallyEvaluated, visited))
                    {
                        return true;
                    }
                }

                return false;
            }

            System.Type type = value.GetType();
            bool isAstNode = value is Statement || value is Expression;
            bool isKeyValuePair = type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);

            if (!isAstNode && !isKeyValuePair)
            {
                return false;
            }

            if (!type.IsValueType && !visited.Add(value))
            {
                return false;
            }

            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                object child;

                try
                {
                    child = property.GetValue(value, null);
                }
                catch
                {
                    continue;
                }

                if (ContainsConditionallyEvaluatedNextCall(child, conditionallyEvaluated, visited))
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        internal FunctionDeclaration FunctionDeclaration()
        {
            var start = MatchMultiple(
                TType.Id,
                TType.TypeAnytype,
                TType.TypeBoolean,
                TType.TypeContainer,
                TType.TypeInt32,
                TType.TypeInt64,
                TType.TypeGuid,
                TType.TypeReal,
                TType.TypeStr,
                TType.TypeTimeOfDay,
                TType.TypeDate,
                TType.TypeDatetime,
                TType.Void);

            Word type = start.Token as Word;

            // Namespace-qualified return types
            if (type.TokenType == TType.Id && currentToken.TokenType == TType.Dot)
            {
                var qualifiedName = new System.Text.StringBuilder(type.Lexeme);

                while (currentToken.TokenType == TType.Dot)
                {
                    Match(TType.Dot);
                    qualifiedName.Append('.');
                    qualifiedName.Append(((Word)Match(TType.Id).Token).Lexeme);
                }

                type = new Word(qualifiedName.ToString(), TType.Id);
            }

            // Length-limited string return types (str 40 fn())
            if (type.TokenType == TType.TypeStr && currentToken.TokenType == TType.Int32)
            {
                Match(TType.Int32);
            }

            if (type.TokenType != TType.Void && !_typeInferer.IsKnownType(type.Lexeme))
            {
                HandleParseError(string.Format(MessageProvider.ExceptionTypeNotFound, type.Lexeme), stop: false);
            }

            Word funcNameToken = MatchIdentifier().Token as Word;

            Match(TType.LeftParenthesis);

            _parseContext.FunctionDeclarationStack.New();
            _parseContext.CurrentScope.FunctionReferences.Add(new FunctionDeclarationReference((funcNameToken).Lexeme, type));
            _parseContext.CurrentScope.Begin();

            var parameters = new List<FunctionDeclarationParameter>();
            while (currentToken.TokenType != TType.RightParenthesis)
            {
                parameters.Add(FunctionDeclarationParameter());

                if (currentToken.TokenType != TType.RightParenthesis)
                {
                    Match(TType.Comma);
                }
            }

            Match(TType.RightParenthesis);
            var block = Block();

            _parseContext.FunctionDeclarationStack.Release();
            _parseContext.CurrentScope.End();

            var ret = new FunctionDeclaration(
                ((Word)funcNameToken).Lexeme,
                start.Token,
                parameters,
                block,
                SourceCodeBinding(start, lastScanResult));

            _parseContext.CurrentScope.FunctionDeclarations.Add(ret);

            return ret;
        }

        FunctionDeclarationParameter FunctionDeclarationParameter()
        {
            var typeResult = MatchMultiple(
                TType.Id,
                TType.TypeAnytype,
                TType.TypeBoolean,
                TType.TypeContainer,
                TType.TypeInt32,
                TType.TypeInt64,
                TType.TypeGuid,
                TType.TypeReal,
                TType.TypeStr,
                TType.TypeTimeOfDay,
                TType.TypeDate,
                TType.TypeDatetime);

            Word type = typeResult.Token as Word;

            // Namespace-qualified parameter types
            if (type.TokenType == TType.Id && currentToken.TokenType == TType.Dot)
            {
                var qualifiedName = new System.Text.StringBuilder(type.Lexeme);

                while (currentToken.TokenType == TType.Dot)
                {
                    Match(TType.Dot);
                    qualifiedName.Append('.');
                    qualifiedName.Append(((Word)Match(TType.Id).Token).Lexeme);
                }

                type = new Word(qualifiedName.ToString(), TType.Id);
            }

            // Length-limited string parameters (str 20 _name)
            if (type.TokenType == TType.TypeStr && currentToken.TokenType == TType.Int32)
            {
                Match(TType.Int32);
            }

            if (!_typeInferer.IsKnownType(type.Lexeme))
            {
                HandleParseError(string.Format(MessageProvider.ExceptionTypeNotFound, type.Lexeme), stop: false);
            }

            System.Type inferedType = _typeInferer.InferType(type.Lexeme);

            Word id = MatchIdentifier().Token as Word;

            // Array parameters (int values[])
            bool isArrayParameter = false;

            if (currentToken.TokenType == TType.LeftBracket)
            {
                Match(TType.LeftBracket);
                Match(TType.RightBracket);
                isArrayParameter = true;

                try
                {
                    inferedType = _proxy.Casting.GetArrayType(inferedType);
                }
                catch
                {
                    inferedType = typeof(object);
                }
            }

            _parseContext.CurrentScope.VariableDeclarations.Add(
                new ParseContextScopeVariable(id.Lexeme, type, inferedType, isArrayParameter));

            return new FunctionDeclarationParameter(type, inferedType, id.Lexeme, SourceCodeBinding(typeResult, lastScanResult), isArrayParameter);
        }

        internal Unchecked Unchecked()
        {
            var start = currentScanResult;

            Match(TType.Unchecked);
            Match(TType.LeftParenthesis);

            Expression expression = Expression();

            var end = Match(TType.RightParenthesis);

            Block block = Block();

            return new Unchecked(expression, block, SourceCodeBinding(start, lastScanResult), SourceCodeBinding(start, end));
        }

        internal ChangeCompany ChangeCompany()
        {
            var start = currentScanResult;

            Match(TType.ChangeCompany);
            Match(TType.LeftParenthesis);

            Expression expression = Expression();

            var end = Match(TType.RightParenthesis);

            Block block = Block();

            return new ChangeCompany(expression, block, SourceCodeBinding(start, lastScanResult), SourceCodeBinding(start, end));
        }

        internal ContainerInitialisation ContainerInitialisation()
        {
            var start = currentScanResult;

            Match(TType.LeftBracket);

            List<Expression> elements = new List<Expression>();

            while (currentToken.TokenType != TType.RightBracket)
            {
                var expression = Expression();

                // Check type
                var elementType = _typeInferer.InferType(expression, _parseContext);

                if (elementType is Core.ScriptClassType || _proxy.Casting.IsReferenceType(elementType))
                {
                    HandleParseError(string.Format(MessageProvider.ExceptionRefTypeContainer, elementType.Name), stop: false);
                }

                elements.Add(expression);

                if (currentToken.TokenType != TType.RightBracket)
                {
                    Match(TType.Comma);
                }
            }

            Match(TType.RightBracket);

            return new ContainerInitialisation(elements, SourceCodeBinding(start, lastScanResult));
        }

        internal Expression Primary()
        {
            Token token = currentToken;

            switch (currentToken.TokenType)
            {
                case TType.LeftBracket:
                    return ContainerInitialisation();

                case TType.LeftParenthesis:
                    if (AdvancePeek(true).Token.TokenType == TType.Select)
                    {
                        return SelectExpression();
                    }
                    else
                    {
                        Match(TType.LeftParenthesis);
                        Expression node = Expression();
                        Match(TType.RightParenthesis);
                        return node;
                    }

                case TType.Plus:
                case TType.Minus:
                    var mpResult = Match(currentToken.TokenType);
                    return new UnaryOperation(mpResult.Token, As(), SourceCodeBinding(mpResult, lastScanResult));

                case TType.Negation:
                    var negResult = Match(TType.Negation);
                    return new UnaryOperation(negResult.Token, Primary(), SourceCodeBinding(negResult, lastScanResult));

                case TType.BinaryNot:
                    var bnResult = Match(TType.BinaryNot);
                    return new UnaryOperation(bnResult.Token, Primary(), SourceCodeBinding(bnResult, lastScanResult));

                case TType.Int32:
                    var integerScan = Match(TType.Int32);
                    return new Constant((int)(integerScan.Token as Lexer.BaseType).Value, SourceCodeBinding(integerScan));

                case TType.Int64:
                    var longScan = Match(TType.Int64);
                    return new Constant((long)(longScan.Token as Lexer.BaseType).Value, SourceCodeBinding(longScan));

                case TType.Real:
                    var doubleScan = Match(TType.Real);
                    return new Constant((decimal)(doubleScan.Token as BaseType).Value, SourceCodeBinding(doubleScan));

                case TType.String:
                    var stringScan = Match(TType.String);
                    HandleMetadataInterruption(stringScan.Line, stringScan.Start, stringScan.End, stringScan.Token, TokenMetadataType.Label);
                    return new Constant((string)(stringScan.Token as BaseType).Value, SourceCodeBinding(stringScan));

                case TType.Date:
                    var dateScan = Match(TType.Date);
                    var date = (DateLiteral)(dateScan.Token as Date).Value;
                    return new Constant(new BaseType(_proxy.Casting.CreateDate(date.Day, date.Month, date.Year), TType.Date), SourceCodeBinding(dateScan));

                case TType.DateTime:
                    var dateTimeScan = Match(TType.DateTime);
                    return new Constant(new BaseType((dateTimeScan.Token as BaseType).Value, TType.DateTime), SourceCodeBinding(dateTimeScan));

                case TType.True:
                case TType.False:
                    var boolScan = Match(currentToken.TokenType);
                    return new Constant(boolScan.Token.TokenType == TType.True, SourceCodeBinding(boolScan));

                case TType.Null:
                    var nullScan = Match(TType.Null);
                    return new Constant(Word.Null, SourceCodeBinding(nullScan));

                case TType.Id:
                    if (currentToken is Word w && this.IsIdentifierMatchQueryExpressionTable(w.Lexeme))
                    {
                        return TableField();
                    }

                    // Namespace-qualified static member access (System.Math::Max(...))
                    if (TryParseClrStaticTypeReference(out Variable clrTypeReference))
                    {
                        Match(TType.StaticDoubleDot);
                        HandleAutocompletion(clrTypeReference);
                        return Variable(clrTypeReference, true);
                    }

                    return Variable();

                case TType.Sum:
                case TType.Avg:
                case TType.Count:
                case TType.MaxOf:
                case TType.MinOf:
                case TType.Index:
                    // Contextual keywords used as identifiers in expressions
                    return Variable();

                case TType.New:
                    return Constructor();

                case TType.Next:
                    return NextCall();

                default:
                    HandleParseError(string.Format(MessageProvider.ExceptionTokenWasNotExpected, token));
                    return null;
            }
        }

        /// <summary>
        /// Peeks over Id (. Id)+ :: to detect a namespace-qualified static type
        /// reference. Only commits (consuming the tokens) when the dotted name
        /// resolves to a .NET type the X++ metadata proxies do not know.
        /// </summary>
        private bool TryParseClrStaticTypeReference(out Variable typeReference)
        {
            typeReference = null;

            if (!(currentToken is Word firstWord) || firstWord.TokenType != TType.Id)
            {
                return false;
            }

            int segments = 0;
            bool endsWithStaticAccess = false;
            var qualifiedName = new System.Text.StringBuilder(firstWord.Lexeme);

            var next = AdvancePeek(false).Token;

            while (next.TokenType == TType.Dot)
            {
                var segment = AdvancePeek(false).Token;

                if (segment.TokenType != TType.Id)
                {
                    break;
                }

                qualifiedName.Append('.');
                qualifiedName.Append((segment as Word).Lexeme);
                segments++;

                next = AdvancePeek(false).Token;

                if (next.TokenType == TType.StaticDoubleDot)
                {
                    endsWithStaticAccess = true;
                    break;
                }
            }

            ResetPeek();

            if (!endsWithStaticAccess || segments == 0)
            {
                return false;
            }

            System.Type clrType = ResolveClrOnlyType(qualifiedName.ToString());

            if (clrType is null)
            {
                return false;
            }

            var start = Match(TType.Id);

            for (int segment = 0; segment < segments; segment++)
            {
                Match(TType.Dot);
                Match(TType.Id);
            }

            typeReference = new Variable(
                new Word(qualifiedName.ToString(), TType.Id),
                null,
                false,
                SourceCodeBinding(start, lastScanResult));

            typeReference.ClrTypeReference = clrType;
            typeReference.SetReturnType(clrType);

            return true;
        }

        internal Expression As()
        {
            Expression expr = Primary();

            // Postfix member access on constructor and parenthesized expressions
            // (new X().m(), (expr).m()) binds tighter than any binary operator
            while (currentToken.TokenType == TType.Dot || currentToken.TokenType == TType.StaticDoubleDot)
            {
                if (currentToken.TokenType == TType.StaticDoubleDot &&
                    (expr is FunctionCall || expr is ArrayAccess))
                {
                    HandleParseError(string.Format(MessageProvider.ExceptionTokenWasNotExpected, currentToken.TokenType));
                }

                MatchMultiple(TType.Dot, TType.StaticDoubleDot);
                HandleAutocompletion(expr);

                expr = Variable(expr, lastScanResult.Token.TokenType == TType.StaticDoubleDot);
            }

            if (currentToken.TokenType == TType.As)
            {
                Match(TType.As);
                string typeName = DottedName();

                var asExpression = new As(expr, typeName, SourceCodeBinding(expr.SourceCodeBinding, lastScanResult));
                asExpression.ResolvedClrType = ResolveClrOnlyType(typeName);

                expr = asExpression;
            }

            return expr;
        }

        internal Expression Factor()
        {
            Expression expr = As();

            while (currentToken.TokenType == TType.Star
                || currentToken.TokenType == TType.Division
                || currentToken.TokenType == TType.Mod
                || currentToken.TokenType == TType.IntegerDivision
                || currentToken.TokenType == TType.LeftShift
                || currentToken.TokenType == TType.RightShift
                || currentToken.TokenType == TType.BinaryAnd
                || currentToken.TokenType == TType.BinaryXOr)
            {
                var result = Match(currentToken.TokenType);
                expr = new BinaryOperation(
                    expr,
                    As(),
                    result.Token,
                    SourceCodeBinding(expr.SourceCodeBinding, lastScanResult));
            }

            return expr;
        }

        internal Expression Bool()
        {
            Expression expr = Equality();

            while (currentToken.TokenType == TType.Or || currentToken.TokenType == TType.And)
            {
                var result = Match(currentToken.TokenType);
                expr = new BinaryOperation(
                    expr,
                    Equality(),
                    result.Token,
                    SourceCodeBinding(expr.SourceCodeBinding, lastScanResult));
            }

            return expr;
        }

        internal Expression Term()
        {
            Expression node = Factor();

            while (currentToken.TokenType == TType.Plus 
                || currentToken.TokenType == TType.Minus
                || currentToken.TokenType == TType.BinaryOr)
            {
                var result = Match(currentToken.TokenType);
                node = new BinaryOperation(
                    node,
                    Factor(),
                    result.Token,
                    SourceCodeBinding(node.SourceCodeBinding, lastScanResult));
            }

            return node;
        }

        internal Expression Comparison()
        {
            Expression expr = Term();

            while (currentToken.TokenType == TType.Greater || currentToken.TokenType == TType.GreaterOrEqual
                || currentToken.TokenType == TType.Smaller || currentToken.TokenType == TType.SmallerOrEqual)
            {
                var result = Match(currentToken.TokenType);
                expr = new BinaryOperation(
                    expr,
                    Term(),
                    result.Token,
                    SourceCodeBinding(expr.SourceCodeBinding, lastScanResult),
                    SourceCodeBinding(expr.SourceCodeBinding, lastScanResult));
            }

            return expr;
        }

        internal Expression Equality()
        {
            var start = currentScanResult;
            Expression expr = Comparison();

            while (currentToken.TokenType == TType.Equal
                || currentToken.TokenType == TType.NotEqual
                || currentToken.TokenType == TType.In
                || currentToken.TokenType == TType.Like
                || currentToken.TokenType == TType.Is)
            {
                if (currentToken.TokenType == TType.In && !isParsingWhereStatement)
                {
                    HandleParseError(MessageProvider.ExceptionInLikeNotInQuery, stop: false);
                }
                else if (currentToken.TokenType == TType.Is &&
                    isParsingWhereStatement)
                {
                    HandleParseError(MessageProvider.ExceptionIsInQuery, stop: false);
                }

                var result = Match(currentToken.TokenType);

                if (lastScanResult.Token.TokenType == TType.Is)
                {
                    string typeName = DottedName();
                    var isExpression = new Is(
                        expr,
                        typeName,
                        SourceCodeBinding(start, lastScanResult));
                    isExpression.ResolvedClrType = ResolveClrOnlyType(typeName);
                    expr = isExpression;
                }
                else
                { 
                    var binaryExpr = new BinaryOperation(
                        expr,
                        Comparison(),
                        result.Token,
                        SourceCodeBinding(start, lastScanResult));

                    expr = binaryExpr;

                    if (currentToken.TokenType == TType.In &&
                       (binaryExpr.LeftOperand.GetType() != typeof(Variable) ||
                        binaryExpr.RightOperand.GetType() != typeof(Variable)))
                    {
                        HandleParseError(MessageProvider.ExceptionInNotContainer, stop: false);
                    }
                }
            }

            return expr;
        }

        internal Expression Ternary()
        {
            Expression expr = Bool();

            while (currentToken.TokenType == TType.QuestionMark)
            {
                var result = Match(TType.QuestionMark);
                Expression left = Expression();
                Match(TType.DoubleDot);
                Expression right = Expression();

                expr = new Ternary(
                    result.Token,
                    expr,
                    left,
                    right,
                    SourceCodeBinding(result, right.SourceCodeBinding));
            }

            return expr;
        }

        internal Expression Expression()
        {
            Expression expr = Ternary();

            if (currentToken.TokenType == TType.Dot || currentToken.TokenType == TType.StaticDoubleDot)
            {
                MatchMultiple(TType.Dot, TType.StaticDoubleDot);
                expr = Variable(expr);
            }

            return expr;
        }

        internal ContainerAssignment ContainerAssignment()
        {
            var start = currentScanResult;
            Match(TType.LeftBracket);
            List<Variable> assignees = new List<Variable>();
            do
            {
                if (currentToken.TokenType == TType.Comma)
                {
                    Match(TType.Comma);
                }

                Expression assignee = Variable();
                
                if (assignee is FunctionCall || !(assignee is Variable))
                {
                    HandleParseError(MessageProvider.ExceptionInvalidSyntax);
                }

                assignees.Add((Variable)assignee);

            } while (currentToken.TokenType == TType.Comma);

            Match(TType.RightBracket);
            Match(TType.Assign);

            Expression expr = Expression();
            var exprType = _typeInferer.InferType(expr, _parseContext);

            if (exprType != _proxy.Casting.GetSystemTypeFromTypeName("container"))
            {
                HandleParseError(MessageProvider.ExceptionAssignmentNotContainer, stop: false);
            }

            Match(TType.Semicolon);

            return new ContainerAssignment(assignees, expr, SourceCodeBinding(start, lastScanResult));
        }

        internal EventHandler EventHandler()
        {
            var start = Match(TType.EventHandler);

            Match(TType.LeftParenthesis);

            var firstName = Match(TType.Id).Token as Word;

            EventHandler handler;

            if (currentToken.TokenType == TType.StaticDoubleDot)
            {
                // eventhandler(MyClass::myStaticMethod)
                Match(TType.StaticDoubleDot);
                var staticMethod = (Word)MatchAnyWord().Token;
                handler = new EventHandler(staticMethod.Lexeme, EventHandlerKind.StaticMethod, firstName.Lexeme);
            }
            else if (currentToken.TokenType == TType.Dot)
            {
                // eventhandler(myObject.myMethod)
                Match(TType.Dot);
                var instanceMethod = (Word)MatchAnyWord().Token;

                if (_parseContext.CurrentScope.FindVariableDeclaration(firstName.Lexeme) is null)
                {
                    HandleParseError(string.Format(MessageProvider.ExceptionVariableNotDeclared, firstName.Lexeme), stop: false);
                }

                handler = new EventHandler(instanceMethod.Lexeme, EventHandlerKind.InstanceMethod, firstName.Lexeme);
            }
            else
            {
                // eventhandler(localFunction)
                var functionDeclaration = _parseContext.CurrentScope.FindFunctionDeclarationReference(firstName.Lexeme);

                if (functionDeclaration is null)
                {
                    HandleParseError($"Function {firstName.Lexeme} does not exist in the current context.", stop: false);
                }
                else if (functionDeclaration.ReturnType != Word.Void)
                {
                    HandleParseError($"Function {firstName.Lexeme} can only return void.", stop: false);
                }

                handler = new EventHandler(firstName.Lexeme);
            }

            var end = Match(TType.RightParenthesis);

            return handler;
        }

        internal Statement Assignment(bool matchSemicolon = true)
        {
            var start = currentScanResult;
            Expression assignee;

            // Statement-level namespace-qualified static access
            // (System.Math::Max(1, 2); or System.X.Y::Property = value;)
            if (TryParseClrStaticTypeReference(out Variable clrStaticCaller))
            {
                Match(TType.StaticDoubleDot);
                assignee = Variable(clrStaticCaller, true, expectReturn: false);
            }
            else
            {
                assignee = Variable(expectReturn: false);
            }

            // Const enforcement: local const variables and const class fields
            bool isMutation = currentToken.TokenType == TType.Assign ||
                currentToken.TokenType == TType.Increment || currentToken.TokenType == TType.Decrement ||
                currentToken.TokenType == TType.PlusAssignment || currentToken.TokenType == TType.MinusAssignment;

            if (isMutation && assignee is Variable mutationTarget)
            {
                if (mutationTarget.Caller is null)
                {
                    var declaration = _parseContext.CurrentScope.FindVariableDeclaration(mutationTarget.Name);

                    if (declaration?.IsConst == true)
                    {
                        HandleParseError($"Cannot assign to const {mutationTarget.Name}.", stop: false);
                    }
                }
                else
                {
                    ClassDeclarationInfo constClass = null;

                    if (mutationTarget.StaticCall && mutationTarget.Caller is Variable staticConstCaller)
                    {
                        constClass = _parseContext.DeclaredClasses.Find(staticConstCaller.Name);
                    }
                    else
                    {
                        var callerType = _typeInferer.InferType(mutationTarget.Caller, false, _parseContext) as Core.ScriptClassType;
                        constClass = callerType?.DeclarationInfo as ClassDeclarationInfo;
                    }

                    if (constClass?.FindField(mutationTarget.Name)?.IsConst == true)
                    {
                        HandleParseError($"Cannot assign to const {mutationTarget.Name}.", stop: false);
                    }
                }
            }

            Token operand = currentToken;
            Statement ret = null;
            Expression assignedExpression = null;

            switch (currentToken.TokenType)
            {
                case TType.Assign:
                    {

                        Match(TType.Assign);
                        assignedExpression = Expression();
                        ret = new Assignment((Variable)assignee, assignedExpression, SourceCodeBinding(start, lastScanResult));
                    }
                    break;

                case TType.Increment:
                case TType.Decrement:
                    {
                        Match(currentToken.TokenType);
                        assignedExpression = new BinaryOperation(assignee, new Constant(1, null), operand, null);
                        ret = new Assignment((Variable)assignee, assignedExpression , SourceCodeBinding(start, lastScanResult));
                    }
                    break;

                case TType.PlusAssignment:
                case TType.MinusAssignment:
                    {
                        bool isUnsubscribe = operand.TokenType == TType.MinusAssignment;

                        Match(currentToken.TokenType);
                        var binding = SourceCodeBinding(start, currentScanResult);

                        if (EventHandlerHelper.IsEventHandler(assignee, _parseContext, _typeInferer))
                        {
                            ret = new EventHandlerSubscription(assignee as Variable, EventHandler(), SourceCodeBinding(start, lastScanResult), isUnsubscribe);
                        }
                        else
                        {
                            assignedExpression = new BinaryOperation(assignee, Expression(), operand, SourceCodeBinding(start, currentScanResult));
                            ret = new Assignment(
                                (Variable)assignee,
                                assignedExpression,
                                binding);
                        }
                    }
                    break;

                default:
                    {
                        if (assignee is FunctionCall fc)
                        {
                            ret = new NoReturnFunctionCall(fc, SourceCodeBinding(start, lastScanResult));
                        }
                        else
                        {
                            HandleParseError(MessageProvider.ExceptionInvalidSyntax);
                        }
                    }
                    break;
            }

            if (assignee is Variable assigneeVar && assignedExpression != null)
            {
                var assigneeType = _typeInferer.InferType(assignee, false, _parseContext);

                if (assigneeType != null)
                {
                    var assignedType = _typeInferer.InferType(assignedExpression, false, _parseContext);

                    if (assignedType != null && !ImplicitConversionExists(assignedType, assigneeType))
                    {
                        HandleParseError(string.Format(MessageProvider.ExceptionImplicitConversion, assignedType.Name, assigneeVar.ReturnType.Name), stop: false);
                    }
                }
            }
            else if (ret is EventHandlerSubscription subscription)
            {
                var assigneeType = _typeInferer.InferType(assignee, false, _parseContext);

                EventHandlerHelper.ValidateSubscription(this, _parseContext, _typeInferer, assignee as Variable, subscription.EventHandler, assigneeType);
            }

            if (matchSemicolon)
            {
                var end = Match(TType.Semicolon);

                if (ret.DebuggeableBinding != null)
                {
                    var newBinding = new SourceCodeBinding(
                        ret.DebuggeableBinding.FromLine,
                        ret.DebuggeableBinding.FromPosition,
                        end.Line,
                        end.End);

                    ret.DebuggeableBinding = newBinding;
                }
            }

            return ret;
        }

        internal void HandleParseError(Exception exception, bool showLine = false, bool stop = true)
        {
            string message = exception.Message;

            if (exception.InnerException != null)
            {
                message = exception.InnerException.Message;
            }

            HandleParseError(message, showLine, stop);
        }

        internal void HandleParseError(string s, bool showLine = false, bool stop = true)
        {
            if (stop)
            {
                throw new ParseException(
                    s,
                    currentToken,
                    currentScanResult.Line,
                    currentScanResult.End,
                    showLine);
            }
            else
            {
                _parseErrors.Add(new ParseError(currentToken, currentScanResult.Line, currentScanResult.End, s));
            }
        }

        IScanResult MatchMultiple(params TType[] ttypes)
        {
            if (ttypes.Contains(currentScanResult.Token.TokenType))
            {
                Move();
            }
            else
            {
                HandleParseError(string.Format(MessageProvider.ExceptionTokenWasNotExpected, string.Join(", ", ttypes)));
            }

            // Move function sets the last scan results
            return lastScanResult;
        }

        /// <summary>
        /// Query keywords that X++ treats as contextual and are legal identifiers
        /// (variables named count, sum, index... are common in real scripts)
        /// </summary>
        internal static bool IsSoftIdentifierToken(TType type)
        {
            return type == TType.Sum || type == TType.Avg || type == TType.Count ||
                   type == TType.MaxOf || type == TType.MinOf || type == TType.Index;
        }

        /// <summary>
        /// Matches an identifier, also accepting contextual (soft) keywords
        /// </summary>
        internal IScanResult MatchIdentifier()
        {
            if (currentToken.TokenType == TType.Id || IsSoftIdentifierToken(currentToken.TokenType))
            {
                Move();
            }
            else
            {
                HandleParseError(string.Format(MessageProvider.ExceptionTokenWasNotExpected, currentToken.TokenType));
            }

            return lastScanResult;
        }

        internal IScanResult MatchAnyWord()
        {
            if (currentToken is Word)
            {
                Move();
            }
            else
            {
                HandleParseError(string.Format(MessageProvider.ExceptionTokenWasNotExpected, TType.Id));
            }

            // Move function sets the last scan results
            return lastScanResult;
        }

        internal IScanResult Match(TType ttype)
        {
            if (currentToken.TokenType == ttype)
            {
                Move();
            }
            else
            {
                HandleParseError(string.Format(MessageProvider.ExceptionTokenWasNotExpected, currentToken.TokenType));
            }

            // Move function sets the last scan results
            return lastScanResult;
        }

        void Initialize()
        {
            if (!hasBeenInitialized)
            {
                _clrResolver = new ClrTypeResolver(_parseContext.ClrNamespaces);
                _typeInferer = new XppTypeInferer(_proxy, () => _parseContext, () => _clrResolver);

                hasBeenInitialized = true;
                Move();
            }
        }

        void Move()
        {
            lastScanResult = currentScanResult;
            currentScanResult = _lexer.GetNextToken();
            currentToken = currentScanResult.Token;
        }

        internal void HandleMetadataInterruption(int line, int start, int end, Expression caller, string methodName, int parameterPosition, bool isIntrinsic, bool isStatic, bool isConstructor)
        {
            if (!_forMetadata) return;

            // Search for declaration of the variable
            if (line == _stopAtRow &&
                start <= _stopAtColumn &&
                end >= _stopAtColumn)
            {
                System.Type callerType = null;

                if (caller != null)
                {
                    callerType = _typeInferer.InferType(caller, isStatic, _parseContext);
                }

                throw new MetadataInterruption(TokenMetadataProviderHelper.GetMetadataForMethodParameters(callerType, 
                    methodName, 
                    isIntrinsic, 
                    isStatic, 
                    isConstructor, 
                    parameterPosition,
                    _proxy,
                    _parseContext));
            }
        }

        internal void HandleMetadataInterruption(int line, int start, int end, Token token, TokenMetadataType type, Expression caller = null)
        {
            if (!_forMetadata) return;

            // Search for declaration of the variable
            if (line == _stopAtRow &&
                start <= _stopAtColumn &&
                end >= _stopAtColumn)
            {
                if (type == TokenMetadataType.IntrinsicMethod ||
                    type == TokenMetadataType.GlobalOrDefinedMethod ||
                    type == TokenMetadataType.StaticMethod ||
                    type == TokenMetadataType.InstanceMethod ||
                    type == TokenMetadataType.Constructor)
                {
                    var methodName = (token as Word).Lexeme;
                    System.Type callerType = null;

                    if (caller != null)
                    {
                        callerType = _typeInferer.InferType(caller, type == TokenMetadataType.StaticMethod, _parseContext);

                        if (callerType is null) throw new MetadataInterruption(null);
                    }

                    throw new MetadataInterruption(TokenMetadataProviderHelper.GetMethodMetadata(
                        callerType, 
                        methodName,
                        type == TokenMetadataType.IntrinsicMethod,
                        type == TokenMetadataType.StaticMethod,
                        type == TokenMetadataType.Constructor,
                        _proxy, 
                        _parseContext));
                }
                else if (type == TokenMetadataType.Label)
                {
                    throw new MetadataInterruption(TokenMetadataProviderHelper.GetLabelMetadata((token as Lexer.String).Value.ToString(), _proxy));
                }
                else if (type == TokenMetadataType.Variable)
                {
                    var varName = (token as Word).Lexeme;

                    throw new MetadataInterruption(TokenMetadataProviderHelper.GetLocalVariableMetadata(varName, _proxy, _parseContext));
                }
            }
        }

        internal void HandleAutocompletionForType(string typeName)
        {
            if (!_forAutoCompletion) return;

            throw new AutoCompletionTypeInterruption(_typeInferer.InferType(typeName));
        }

        internal void HandleAutocompletion(Expression expression)
        {
            if (!_forAutoCompletion) return;

            if (lastScanResult.Line == _stopAtRow &&
                lastScanResult.Start <= _stopAtColumn &&
                lastScanResult.End >= _stopAtColumn)
            {
                throw new AutoCompletionTypeInterruption(_typeInferer.InferType(expression, lastScanResult.Token.TokenType == TType.StaticDoubleDot, _parseContext));
            }
        }

        internal SourceCodeBinding DebuggeableBinding(IScanResult scanResult)
        {
            return SourceCodeBinding(scanResult, scanResult);
        }

        internal SourceCodeBinding SourceCodeBinding(IScanResult fromScan, IScanResult toScan)
        {
            return new SourceCodeBinding(fromScan.Line, fromScan.Start, toScan.Line, toScan.End);
        }

        internal SourceCodeBinding SourceCodeBinding(SourceCodeBinding fromSourceCodeBinding, IScanResult toScan)
        {
            return new SourceCodeBinding(fromSourceCodeBinding.FromLine, fromSourceCodeBinding.FromPosition, toScan.Line, toScan.End);
        }

        internal SourceCodeBinding SourceCodeBinding(IScanResult fromScan, SourceCodeBinding toSourceCodeBinding)
        {
            return new SourceCodeBinding(fromScan.Line, fromScan.Start, toSourceCodeBinding.ToLine, toSourceCodeBinding.ToPosition);
        }

        internal SourceCodeBinding SourceCodeBinding(IScanResult scanResult)
        {
            return new SourceCodeBinding(scanResult.Line, scanResult.Start, scanResult.Line, scanResult.End);
        }
    }
}

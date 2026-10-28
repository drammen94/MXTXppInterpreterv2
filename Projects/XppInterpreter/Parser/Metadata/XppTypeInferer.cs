using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel.Security;
using System.Text;
using System.Threading.Tasks;
using XppInterpreter.Interpreter;
using XppInterpreter.Interpreter.Proxy;
using XppInterpreter.Lexer;

namespace XppInterpreter.Parser.Metadata
{
    internal class XppTypeInferer : ITypeInferExpressionVisitor
    {
        private System.Type _globalType, _predefinedType, _customPredefinedType;
        private readonly XppProxy _proxy;
        private readonly System.Func<ParseContext> _parseContextAccessor;
        private readonly System.Func<Core.ClrTypeResolver> _clrResolverAccessor;
        private bool _calledStatically;
        private ParseContext _context;

        public XppTypeInferer(XppProxy proxy, System.Func<ParseContext> parseContextAccessor = null, System.Func<Core.ClrTypeResolver> clrResolverAccessor = null)
        {
            _proxy = proxy;
            _parseContextAccessor = parseContextAccessor;
            _clrResolverAccessor = clrResolverAccessor;

            // initialize types
            _globalType = _proxy.Casting.GetSystemTypeFromTypeName("Global");
            _predefinedType = _proxy.Casting.GetSystemTypeFromTypeName("PredefinedFunctions");
            _customPredefinedType = _proxy.Intrinsic.GetCustomPredefinedFunctionProvider();
        }

        /// <summary>
        /// Resolves a type name through the X++ metadata proxy first, then
        /// script-declared classes, then .NET types honoring using directives
        /// </summary>
        public System.Type InferType(string typeName)
        {
            System.Type proxyType = null;

            try
            {
                proxyType = _proxy.Casting.GetSystemTypeFromTypeName(typeName);
            }
            catch
            {
                // Names unknown to the X++ metadata (dotted CLR names) may throw
            }

            if (proxyType != null)
            {
                return proxyType;
            }

            ParseContext context = _context ?? _parseContextAccessor?.Invoke();
            ClassDeclarationInfo scriptClass = context?.DeclaredClasses.Find(typeName);

            if (scriptClass != null)
            {
                return scriptClass.Type;
            }

            return _clrResolverAccessor?.Invoke()?.Resolve(typeName);
        }

        public System.Type InferType(Expression expression, ParseContext context)
        {
            bool calledStatic = false;

            if (expression is Variable variable)
            {
                calledStatic = variable.StaticCall;
            }

            return InferType(expression, calledStatic, context ?? _context);
        }

        public System.Type InferType(Expression expression, bool calledStatic, ParseContext context)
        {
            if (expression.ReturnType != null)
            {
                return expression.ReturnType;
            }

            _calledStatically = calledStatic;
            _context = context;

            var inferedType = expression.Accept(this);
            expression.SetReturnType(inferedType);

            return inferedType;
        }

        private bool IsGlobalFunction(string methodName)
        {
            return Core.ReflectionHelper.TypeHasMethod(_globalType, methodName)
                || Core.ReflectionHelper.TypeHasMethod(_predefinedType, methodName);
        }

        private bool IsCustomPredefinedFunction(string methodName)
        {
            if (_customPredefinedType is null) return false;

            return Core.ReflectionHelper.TypeHasMethod(_customPredefinedType, methodName);
        }

        public System.Type VisitBinaryOperation(BinaryOperation binaryOperation)
        {
            var leftType = binaryOperation.LeftOperand.Accept(this);
            var rightType = binaryOperation.RightOperand.Accept(this);

            if (leftType == typeof(decimal) || rightType == typeof(decimal))
            { 
                return typeof(decimal);
            }

            return leftType ?? rightType;
        }

        public System.Type VisitConstant(Constant constant)
        {
            if (constant.Token is BaseType baseType)
            {
                return baseType.Value?.GetType();
            }
            else if (constant.Token == Word.True || constant.Token == Word.False)
            {
                return typeof(bool);
            }

            return null;
        }

        public System.Type VisitConstructor(Constructor constructor)
        {
            return InferType(constructor.ClassName);
        }

        /// <summary>
        /// True for types only reachable through direct CLR reflection (not X++ metadata)
        /// </summary>
        internal bool IsClrOnlyType(System.Type type)
        {
            if (type is null || type is Core.ScriptClassType) return false;

            try
            {
                return _proxy.Casting.GetSystemTypeFromTypeName(type.Name) != type;
            }
            catch
            {
                return true;
            }
        }

        private System.Type GetScriptClassMethodReturnType(Core.ScriptClassType scriptClassType, string methodName)
        {
            var info = (ClassDeclarationInfo)scriptClassType.DeclarationInfo;
            var method = info.FindMethod(methodName);

            if (method is null)
            {
                method = _context.DeclaredClasses.FindExtensionMethod(info, methodName);
            }

            if (method is null)
            {
                // Members not declared in the script chain may live on a compiled base
                var compiledBase = info.FindCompiledBase();

                if (compiledBase != null)
                {
                    try
                    {
                        return _proxy.Reflection.GetMethodReturnType(compiledBase, methodName);
                    }
                    catch
                    {
                    }
                }

                return null;
            }

            if (method.ReturnType.TokenType == Lexer.TType.Void) return typeof(void);

            return method.ReturnClrType ?? InferType(method.ReturnType.Lexeme);
        }

        private System.Type GetScriptClassFieldType(Core.ScriptClassType scriptClassType, string fieldName)
        {
            var info = (ClassDeclarationInfo)scriptClassType.DeclarationInfo;
            var field = info.FindField(fieldName);

            if (field is null)
            {
                field = _context.DeclaredClasses.FindExtensionField(info, fieldName);
            }

            if (field is null)
            {
                var compiledBase = info.FindCompiledBase();

                if (compiledBase != null)
                {
                    try
                    {
                        return _proxy.Reflection.GetFieldReturnType(compiledBase, fieldName);
                    }
                    catch
                    {
                    }
                }

                return null;
            }

            return field.ClrType ?? InferType(field.Type.Lexeme);
        }

        public System.Type VisitContainerInitialisation(ContainerInitialisation containerInitialisation)
        {
            return typeof(object[]);
        }

        public System.Type VisitFunctionCall(FunctionCall functionCall)
        {
            if (functionCall is NextCallExpression nextCall)
            {
                if (nextCall.TargetMethod is null || nextCall.TargetMethod.ReturnType.TokenType == TType.Void)
                {
                    return typeof(void);
                }

                return nextCall.TargetMethod.ReturnClrType ?? InferType(nextCall.TargetMethod.ReturnType.Lexeme);
            }

            if (functionCall is SuperCallExpression superCall)
            {
                var baseMethod = superCall.DeclaringClass?.BaseClass?.FindMethod(superCall.MethodName);

                if (baseMethod is null || baseMethod.ReturnType.TokenType == TType.Void)
                {
                    return typeof(void);
                }

                return baseMethod.ReturnClrType ?? InferType(baseMethod.ReturnType.Lexeme);
            }

            if (functionCall.Caller != null)
            {
                bool staticSave = _calledStatically;
                _calledStatically = functionCall.StaticCall;

                System.Type callerType = functionCall.Caller.Accept(this);

                _calledStatically = staticSave;

                if (callerType is Core.ScriptClassType scriptClassType)
                {
                    return GetScriptClassMethodReturnType(scriptClassType, functionCall.Name);
                }

                if (callerType != null)
                {
                    ClassMethodSignature interpretedExtension = _context?.DeclaredClasses
                        .FindCompiledTargetExtensionMethod(callerType, functionCall.Name, functionCall.StaticCall);

                    if (interpretedExtension != null)
                    {
                        return interpretedExtension.ReturnClrType ?? InferType(interpretedExtension.ReturnType.Lexeme);
                    }

                    if (IsClrOnlyType(callerType))
                    {
                        return Core.ReflectionHelper.GetMethod(callerType, functionCall.Name)?.ReturnType;
                    }

                    System.Type methodReturnType = _proxy.Reflection.GetMethodReturnType(callerType, functionCall.Name);

                    // X++ extension methods: TypeName_Extension::method(obj, ...)
                    if (methodReturnType is null)
                    {
                        try
                        {
                            var extensionType = _proxy.Casting.GetSystemTypeFromTypeName(callerType.Name + "_Extension");

                            if (extensionType != null)
                            {
                                methodReturnType = _proxy.Reflection.GetMethodReturnType(extensionType, functionCall.Name);
                            }
                        }
                        catch
                        {
                        }
                    }

                    return methodReturnType;
                }
            }
            else if (IsGlobalFunction(functionCall.Name))
            {
                var globalMethod = 
                    Core.ReflectionHelper.GetMethod(_globalType, functionCall.Name) 
                    ?? Core.ReflectionHelper.GetMethod(_predefinedType, functionCall.Name);

                if (globalMethod != null)
                {
                    return globalMethod.ReturnType;
                }
            }
            else if (IsCustomPredefinedFunction(functionCall.Name))
            {
                return Core.ReflectionHelper.GetMethod(_customPredefinedType, functionCall.Name)?.ReturnType;
            }
            else
            {
                return GetTypeFromUserFunctionDeclaration(functionCall.Name);
            }

            return null;
        }

        public System.Type VisitTernary(Ternary ternary)
        {
            return ternary.Left.Accept(this) ?? ternary.Right.Accept(this);
        }

        public System.Type VisitUnaryOperation(UnaryOperation unaryOperation)
        {
            switch (unaryOperation.Token.TokenType)
            {
                case TType.Negation:
                    return typeof(bool);
                case TType.Minus:
                    return unaryOperation.Expression.Accept(this);
                default:
                    return null;
            }
        }

        public bool IsKnownType(string typeName)
        {
            return InferType(typeName) != null;
        }

        public System.Type VisitVariable(Variable variable)
        {
            if (variable.Caller != null)
            {
                bool staticSave = _calledStatically;
                _calledStatically = variable.StaticCall;

                System.Type callerType = variable.Caller.Accept(this);

                _calledStatically = staticSave;

                if (callerType is null) return null;

                if (callerType is Core.ScriptClassType scriptClassType)
                {
                    return GetScriptClassFieldType(scriptClassType, variable.Name);
                }

                if (IsClrOnlyType(callerType))
                {
                    if (callerType.IsEnum)
                    {
                        return callerType;
                    }

                    return Core.ReflectionHelper.GetProperty(callerType, variable.Name)?.PropertyType
                        ?? Core.ReflectionHelper.GetField(callerType, variable.Name)?.FieldType;
                }

                if (callerType.IsEnum)
                {
                    return _proxy.Reflection.GetEnumValue(callerType.Name, variable.Name).GetType();
                }
                else
                {
                    return _proxy.Reflection.GetFieldReturnType(callerType, variable.Name);
                }
            }
            else
            {
                if (_calledStatically)
                {
                    return GetTypeFromWord(variable.Token as Word);
                }
                // Infer from variables
                else
                {
                    var declaration = _context.CurrentScope.FindVariableDeclaration(variable.Name);

                    if (declaration != null)
                    {
                        if (declaration.Type.TokenType == TType.Var)
                        {
                            if (declaration.Initialization is null) return null;
                            return InferType(declaration.Initialization, false, _context);
                        }

                        return GetTypeFromWord((Word)declaration.Type);
                    }
                }
            }

            return null;
        }

        private System.Type GetTypeFromWord(Word word)
        {
            if (word is null) return null;
            return InferType(word.Lexeme);
        }

        public System.Type GetTypeFromUserFunctionDeclaration(string functionName, ParseContext context = null)
        {
            var declarationReference = (context ?? _context).CurrentScope.FindFunctionDeclarationReference(functionName);

            if (declarationReference != null)
            {
                if (declarationReference.ReturnType.TokenType == TType.Void)
                {
                    return typeof(void);
                }

                return InferType(declarationReference.ReturnType.Lexeme);
            }

            return null;
        }

        public System.Type VisitIs(Is @is)
        {
            return typeof(bool);
        }

        public System.Type VisitAs(As @as)
        {
            return InferType(@as.TypeName);
        }

        public System.Type VisitSelectExpression(SelectExpression selectExpression)
        {
            var bufferType = _proxy.Casting.GetSystemTypeFromTypeName(selectExpression.Query.TableVariableName);

            if (bufferType != null)
            {
                return _proxy.Reflection.GetFieldReturnType(bufferType, selectExpression.ReturnField);
            }

            return null;
        }

        public System.Type VisitTableField(TableField tableField)
        {

            var bufferType = _proxy.Casting.GetSystemTypeFromTypeName(tableField.TableName);

            if (bufferType != null)
            {
                return _proxy.Reflection.GetFieldReturnType(bufferType, tableField.FieldName);
            }

            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using XppInterpreter.Core;
using XppInterpreter.Interpreter.Debug;
using XppInterpreter.Parser;

namespace XppInterpreter.Interpreter.Bytecode
{
    /// <summary>
    /// Runtime representation of a method declared in a script class
    /// </summary>
    public class RuntimeClassMethod
    {
        public string Name;
        public bool IsStatic;
        public bool IsAbstract;
        public bool IsFinal;
        public AccessModifier Access;
        public Lexer.Word ReturnType;
        public List<FunctionDeclarationParameter> Parameters = new List<FunctionDeclarationParameter>();
        public List<object> ParameterDefaults = new List<object>();
        public List<bool> ParameterHasDefault = new List<bool>();
        public List<IInstruction> Instructions = new List<IInstruction>();
        public RuntimeClass DeclaringClass;
        public int RequiredParameterCount;
        public RuntimeClassMethod NextMethod;
        public List<XppAttributeDeclaration> Attributes = new List<XppAttributeDeclaration>();
        public bool WrapsCompiledTarget;
        public bool NextCallsNative;
        public string NativeTargetName;

        public bool IsVoid => ReturnType.TokenType == Lexer.TType.Void;
    }

    /// <summary>
    /// Runtime representation of a field declared in a script class.
    /// </summary>
    public class RuntimeClassField
    {
        public string Name;
        public string TypeName;
        public Type ClrType;
        public bool IsStatic;
        public bool IsConst;
        public AccessModifier Access;
        public RuntimeClass DeclaringClass;
        public List<XppAttributeDeclaration> Attributes = new List<XppAttributeDeclaration>();
    }

    /// <summary>
    /// Runtime representation of a script-declared class
    /// </summary>
    public class RuntimeClass
    {
        public string Name;
        public string BaseClassName;
        public RuntimeClass BaseClass;
        public RuntimeClass ExtensionTarget;
        public List<string> ImplementsNames = new List<string>();
        public List<Type> CompiledInterfaces = new List<Type>();
        public bool IsAbstract;
        public bool IsFinal;
        public bool IsStatic;
        public string ExtensionTargetName;
        public bool HasCompiledExtensionTarget;
        public Metadata.AosTypeKind CompiledExtensionTargetKind;
        public RuntimeClass ExternalExtensionRoot;
        public List<XppAttributeDeclaration> Attributes = new List<XppAttributeDeclaration>();

        internal readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ScriptObject> ExternalTargetInstances =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, ScriptObject>();

        /// <summary>
        /// True when BaseClassName refers to a compiled X++ class; the runtime wraps
        /// an instance of it and forwards unknown members to it
        /// </summary>
        public bool HasCompiledBase;

        /// <summary>
        /// Nearest compiled base class name up the script chain, or null
        /// </summary>
        public string FindCompiledBaseName()
        {
            for (RuntimeClass current = this; current != null; current = current.BaseClass)
            {
                if (current.HasCompiledBase)
                {
                    return current.BaseClassName;
                }

                if (current.HasCompiledExtensionTarget)
                {
                    return current.ExtensionTargetName;
                }
            }

            return null;
        }

        public bool HasCompiledInterfaces()
        {
            for (RuntimeClass current = this; current != null; current = current.BaseClass)
            {
                if (current.CompiledInterfaces.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        public List<RuntimeClassMethod> Methods = new List<RuntimeClassMethod>();
        public List<RuntimeClassMethod> ExtensionMethods = new List<RuntimeClassMethod>();
        public List<RuntimeClassField> Fields = new List<RuntimeClassField>();
        public List<RuntimeClassField> ExtensionFields = new List<RuntimeClassField>();
        public List<string> DelegateNames = new List<string>();
        public List<ClassDelegateSignature> DelegateDeclarations = new List<ClassDelegateSignature>();
        public List<IInstruction> FieldInitInstructions = new List<IInstruction>();
        public List<IInstruction> StaticFieldInitInstructions = new List<IInstruction>();

        public Scope StaticScope = new Scope();
        public bool StaticsInitialized;

        public RuntimeClassMethod FindMethod(string name, bool walkBaseChain = true)
        {
            RuntimeClass current = this;

            do
            {
                var method = current.Methods.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

                if (method != null)
                {
                    return method;
                }

                current = current.BaseClass;
            } while (walkBaseChain && current != null);

            return null;
        }

        public RuntimeClassMethod FindDispatchMethod(string name, bool isStatic)
        {
            for (RuntimeClass current = this; current != null; current = current.BaseClass)
            {
                var extensionMethod = current.ExtensionMethods.FirstOrDefault(method =>
                    method.IsStatic == isStatic && string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase));

                if (extensionMethod != null)
                {
                    return extensionMethod;
                }

                var declaredMethod = current.Methods.FirstOrDefault(method =>
                    method.IsStatic == isStatic && string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase));

                if (declaredMethod != null)
                {
                    return declaredMethod;
                }
            }

            return null;
        }

        public RuntimeClassField FindField(string name, bool walkBaseChain = true)
        {
            RuntimeClass current = this;

            do
            {
                var field = current.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

                if (field != null)
                {
                    return field;
                }

                current = current.BaseClass;
            } while (walkBaseChain && current != null);

            return null;
        }

        public RuntimeClassField FindExtensionField(string name, bool isStatic)
        {
            for (RuntimeClass current = this; current != null; current = current.BaseClass)
            {
                RuntimeClassField field = current.ExtensionFields.FirstOrDefault(candidate =>
                    candidate.IsStatic == isStatic &&
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

                if (field != null)
                {
                    return field;
                }
            }

            return null;
        }

        public bool IsOrExtends(string typeName)
        {
            RuntimeClass current = this;

            while (current != null)
            {
                if (string.Equals(current.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                    current.ImplementsNames.Any(i => string.Equals(i, typeName, StringComparison.OrdinalIgnoreCase)) ||
                    (current.HasCompiledBase && string.Equals(current.BaseClassName, typeName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                current = current.BaseClass;
            }

            return false;
        }
    }

    /// <summary>
    /// An instance of a script-declared class. Fields (own and inherited) live in a
    /// single flat scope whose parent chain points at the class static scopes, so
    /// unqualified field and static access inside methods resolves naturally.
    /// </summary>
    public class ScriptObject
    {
        public readonly RuntimeClass Class;
        public readonly Scope FieldScope = new Scope();

        /// <summary>
        /// Wrapped instance of the compiled X++ base class, when the script class
        /// extends a compiled class. Unknown members are forwarded to it.
        /// </summary>
        public object BaseInstance;
        internal readonly HashSet<string> InitializedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, Scope> ExtensionScopes =
            new Dictionary<string, Scope>(StringComparer.OrdinalIgnoreCase);

        public ScriptObject(RuntimeClass runtimeClass)
        {
            Class = runtimeClass;
        }

        public override string ToString() => Class.Name;
    }

    /// <summary>
    /// Instantiation and method dispatch for script-declared classes. Method bodies
    /// run in a nested interpreter following the same InnerContext resume pattern as
    /// DeclaredFunctionCall, so breakpoints inside methods propagate to the debugger.
    /// </summary>
    public static class ScriptClassRuntime
    {
        public class Invocation
        {
            public InterpreterResult Result = InterpreterResult.Finished;
            public object ReturnValue;
            public object[] UpdatedArguments;
        }

        public static object Instantiate(RuntimeContext context, RuntimeClass runtimeClass, object[] arguments)
        {
            var instance = BeginInstantiate(context, runtimeClass, arguments, out RuntimeClassMethod constructor);

            if (constructor != null)
            {
                RunSynchronous(context, constructor, instance, arguments);
            }

            return instance;
        }

        /// <summary>
        /// Runs every instantiation stage except the script constructor (field
        /// initializers, per-instance delegates, base-instance creation) and hands
        /// the constructor back so the caller can run it resumably - this is what
        /// lets breakpoints inside constructors pause execution
        /// </summary>
        public static ScriptObject BeginInstantiate(RuntimeContext context, RuntimeClass runtimeClass, object[] arguments, out RuntimeClassMethod constructor)
        {
            if (runtimeClass.IsAbstract)
            {
                throw new Exception($"Cannot instantiate abstract class {runtimeClass.Name}.");
            }

            if (runtimeClass.IsStatic)
            {
                throw new Exception($"Cannot instantiate static class {runtimeClass.Name}.");
            }
            if (!string.IsNullOrEmpty(runtimeClass.ExtensionTargetName))
            {
                throw new Exception($"Extension class {runtimeClass.Name} cannot be instantiated explicitly.");
            }

            var instance = new ScriptObject(runtimeClass);

            // Field initializers run base-first so derived initializers can override
            instance.FieldScope.Parent = EnsureStaticsInitialized(context, runtimeClass);
            RunFieldInitializers(context, runtimeClass, instance);

            // Delegates are per-instance invocation lists
            for (RuntimeClass delegateSource = runtimeClass; delegateSource != null; delegateSource = delegateSource.BaseClass)
            {
                foreach (string delegateName in delegateSource.DelegateNames)
                {
                    instance.FieldScope.SetVar(delegateName, new Events.XppScriptDelegate(), context.Proxy.Casting, true, typeof(Events.XppScriptDelegate));
                }
            }

            // Constructors are declared per class. A class without an explicit new
            // method has its own implicit, public, parameterless constructor.
            constructor = runtimeClass.FindMethod("new", walkBaseChain: false);

            if (constructor != null)
            {
                EnsureMethodAccessible(constructor, context.ActiveClass);
            }
            string compiledBase = runtimeClass.FindCompiledBaseName();

            if (compiledBase != null || runtimeClass.HasCompiledInterfaces())
            {
                // The base instance is created up front (preferably as an emitted
                // subclass/proxy) so compiled-side virtual and interface calls
                // reach script methods.
                // Without a script constructor, base constructor args are inherited.
                object[] baseArguments = constructor is null ? (arguments ?? new object[0]) : new object[0];
                CreateBaseInstance(context, instance, compiledBase, baseArguments, allowUninitialized: constructor != null);
            }

            if (constructor is null && arguments != null && arguments.Length > 0 && compiledBase is null)
            {
                throw new Exception($"Class {runtimeClass.Name} has no constructor taking {arguments.Length} argument(s).");
            }

            return instance;
        }

        /// <summary>
        /// Replaces script instances with their emitted/wrapped base instances when
        /// values cross into compiled code or the CLR
        /// </summary>
        public static object MarshalToNative(object value)
        {
            return value is ScriptObject scriptObject && scriptObject.BaseInstance != null
                ? scriptObject.BaseInstance
                : value;
        }

        public static object[] MarshalToNative(object[] arguments)
        {
            if (arguments != null)
            {
                for (int i = 0; i < arguments.Length; i++)
                {
                    arguments[i] = MarshalToNative(arguments[i]);
                }
            }

            return arguments;
        }

        /// <summary>
        /// Creates the compiled-base instance for a script object: an emitted CLR
        /// subclass whose virtual overrides dispatch into the script methods when
        /// the base type is subclassable, otherwise a plain wrapped instance
        /// </summary>
        internal static void CreateBaseInstance(RuntimeContext context, ScriptObject instance, string compiledBaseName, object[] arguments, bool allowUninitialized)
        {
            arguments = MarshalToNative(arguments ?? new object[0]);

            Type baseType = null;

            if (!string.IsNullOrEmpty(compiledBaseName))
            {
                try
                {
                    baseType = context.Proxy.Casting.GetSystemTypeFromTypeName(compiledBaseName);
                }
                catch
                {
                }
            }

            // An interpreted class can implement a compiled interface without
            // extending a compiled class. Emit an object-derived proxy in that case
            // so the value has real CLR/AOS interface identity at native call sites.
            baseType = baseType ?? typeof(object);

            Type emittedType = ScriptSubclassEmitter.TryGetEmittedType(instance.Class, baseType);

            object created = null;

            if (emittedType != null)
            {
                try
                {
                    created = arguments.Length > 0
                        ? Activator.CreateInstance(emittedType, arguments)
                        : Activator.CreateInstance(emittedType);
                }
                catch when (allowUninitialized)
                {
                    // The script constructor is expected to run super(args), which
                    // constructs the base on this allocated-but-uninitialized object
                    created = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(emittedType);
                }

                if (created != null)
                {
                    ScriptSubclassEmitter.AttachDispatcher(created, new ScriptMethodDispatcher(context, instance));
                }
            }

            if (created is null)
            {
                if (instance.Class.HasCompiledInterfaces())
                {
                    throw new Exception($"Class {instance.Class.Name} could not be emitted as compiled interface proxy over {baseType.FullName}.");
                }

                created = context.Proxy.Reflection.CreateInstance(compiledBaseName, arguments);
            }

            instance.BaseInstance = created;
        }

        private static void RunFieldInitializers(RuntimeContext context, RuntimeClass runtimeClass, ScriptObject instance)
        {
            if (runtimeClass.BaseClass != null)
            {
                RunFieldInitializers(context, runtimeClass.BaseClass, instance);
            }

            if (runtimeClass.FieldInitInstructions.Count == 0)
            {
                return;
            }

            RunInstructionsInScope(context, runtimeClass.FieldInitInstructions, instance.FieldScope);
        }

        /// <summary>
        /// Initializes static fields of the class chain (base first) and links the
        /// static scope chain. Returns the class's static scope.
        /// </summary>
        public static Scope EnsureStaticsInitialized(RuntimeContext context, RuntimeClass runtimeClass)
        {
            if (runtimeClass is null)
            {
                return null;
            }

            if (!runtimeClass.StaticsInitialized)
            {
                // The flag guards re-entry during initialization; a throwing
                // initializer resets it so a later access can retry
                runtimeClass.StaticsInitialized = true;

                try
                {
                    runtimeClass.StaticScope.Parent = EnsureStaticsInitialized(context, runtimeClass.BaseClass);

                    if (runtimeClass.StaticFieldInitInstructions.Count > 0)
                    {
                        RunInstructionsInScope(context, runtimeClass.StaticFieldInitInstructions, runtimeClass.StaticScope);
                    }

                    RuntimeClassMethod staticConstructor = runtimeClass.FindMethod("TypeNew", walkBaseChain: false);

                    if (staticConstructor != null)
                    {
                        RunSynchronous(context, staticConstructor, null, new object[0]);
                    }
                }
                catch
                {
                    runtimeClass.StaticsInitialized = false;
                    throw;
                }
            }

            return runtimeClass.StaticScope;
        }

        private static void RunInstructionsInScope(RuntimeContext context, List<IInstruction> instructions, Scope scope)
        {
            var byteCode = new ByteCode(instructions)
            {
                DeclaredFunctions = context.ByteCode.DeclaredFunctions,
                DeclaredClasses = context.ByteCode.DeclaredClasses,
            };

            var scopeHandler = new ScopeHandler(context.Proxy, scope);
            var initContext = new RuntimeContext(context.Proxy, byteCode, scopeHandler);
            initContext.InheritExecutionState(context);

            var interpreter = new XppInterpreter(context.Proxy, context.Interpreter?.Options);
            initContext.Interpreter = interpreter;

            interpreter.Interpret(byteCode, initContext, nextAction: DebugAction.StopDebugging);
        }

        /// <summary>
        /// Calls an instance method with virtual dispatch starting at the instance's
        /// runtime class, following the InnerContext resume pattern
        /// </summary>
        /// <summary>
        /// Creates the wrapped compiled base instance on first use (a super(args)
        /// call in the constructor replaces it with an argument-constructed one)
        /// </summary>
        public static object EnsureBaseInstance(RuntimeContext context, ScriptObject instance)
        {
            if (instance.BaseInstance is null)
            {
                string compiledBaseName = instance.Class.FindCompiledBaseName();

                if (compiledBaseName != null)
                {
                    CreateBaseInstance(context, instance, compiledBaseName, new object[0], allowUninitialized: false);
                }
            }

            return instance.BaseInstance;
        }

        public static Invocation CallInstance(RuntimeContext context, ScriptObject instance, string methodName, object[] arguments)
        {
            RuntimeClassMethod method = instance.Class.FindDispatchMethod(methodName, isStatic: false);

            if (method is null || method.IsStatic)
            {
                // Raising a delegate: obj.myDelegate(args) invokes its handlers
                object delegateValue = null;

                try
                {
                    delegateValue = instance.FieldScope.GetVar(methodName);
                }
                catch
                {
                }

                if (delegateValue is Events.XppScriptDelegate scriptDelegate)
                {
                    scriptDelegate.Invoke(context, arguments);
                    return new Invocation();
                }

                // Forward to the wrapped compiled base instance
                object baseInstance = EnsureBaseInstance(context, instance);

                if (baseInstance != null)
                {
                    AosCallResult nativeCall = AosCallDispatcher.CallInstance(context, baseInstance, methodName, arguments);
                    return new Invocation
                    {
                        ReturnValue = nativeCall.ReturnValue,
                        UpdatedArguments = nativeCall.UpdatedArguments,
                    };
                }

                throw new Exception($"Method {methodName} was not found on {instance.Class.Name}.");
            }

            EnsureMethodAccessible(method, context.ActiveClass);
            return CallResumable(context, method, instance, arguments);
        }

        /// <summary>
        /// Finds an interpreted extension for a real AOS object. Calls entering the
        /// interpreter then use the same CoC chain as script-declared targets, with
        /// the real AOS method as the terminal implementation.
        /// </summary>
        public static RuntimeClassMethod FindCompiledTargetExtension(
            ByteCode byteCode,
            Type targetType,
            string methodName,
            bool isStatic)
        {
            if (byteCode?.DeclaredClasses is null || targetType is null) return null;

            for (Type currentType = targetType; currentType != null; currentType = currentType.BaseType)
            {
                RuntimeClassMethod method = byteCode.DeclaredClasses
                    .Where(runtimeClass => runtimeClass.HasCompiledExtensionTarget &&
                        string.Equals(runtimeClass.ExtensionTargetName, currentType.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(runtimeClass => runtimeClass.FindMethod(methodName, walkBaseChain: false))
                    .FirstOrDefault(candidate => candidate != null && candidate.IsStatic == isStatic &&
                        (candidate.WrapsCompiledTarget || candidate.Access == AccessModifier.Public));

                if (method != null) return method;
            }

            return null;
        }

        public static ScriptObject GetCompiledTargetFacade(RuntimeContext context, RuntimeClassMethod method, object target)
        {
            RuntimeClass extensionClass = method.DeclaringClass;
            RuntimeClass root = extensionClass.ExternalExtensionRoot ?? extensionClass;

            return root.ExternalTargetInstances.GetValue(target, nativeTarget =>
            {
                var facade = new ScriptObject(root) { BaseInstance = nativeTarget };
                facade.FieldScope.Parent = EnsureStaticsInitialized(context, root);
                return facade;
            });
        }

        public static Invocation CallStatic(RuntimeContext context, RuntimeClass runtimeClass, string methodName, object[] arguments)
        {
            RuntimeClassMethod method = runtimeClass.FindDispatchMethod(methodName, isStatic: true);

            if (method is null || !method.IsStatic)
            {
                throw new Exception($"Static method {methodName} was not found on {runtimeClass.Name}.");
            }

            EnsureMethodAccessible(method, context.ActiveClass);
            return CallResumable(context, method, null, arguments);
        }

        /// <summary>
        /// Calls the nearest override of a method above the declaring class (super())
        /// </summary>
        public static Invocation CallSuper(RuntimeContext context, RuntimeClass declaringClass, string methodName, ScriptObject instance, object[] arguments)
        {
            RuntimeClassMethod method = declaringClass.BaseClass?.FindMethod(methodName);

            if (method is null)
            {
                // super() against a compiled base class (directly or anywhere above
                // the script chain) forwards to the wrapped instance
                string compiledBaseName = declaringClass.BaseClass != null
                    ? declaringClass.BaseClass.FindCompiledBaseName()
                    : (declaringClass.HasCompiledBase ? declaringClass.BaseClassName : null);

                if (compiledBaseName != null)
                {
                    arguments = MarshalToNative(arguments ?? new object[0]);

                    if (string.Equals(methodName, "new", StringComparison.OrdinalIgnoreCase))
                    {
                        // On an emitted subclass the base constructor runs on the
                        // existing object; on a plain wrap the instance is replaced
                        if (instance.BaseInstance is IScriptEmittedInstance)
                        {
                            Type constructedBase = instance.BaseInstance.GetType().BaseType;

                            var baseConstructor = constructedBase
                                .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                .FirstOrDefault(c => c.GetParameters().Length == arguments.Length);

                            if (baseConstructor is null)
                            {
                                throw new Exception($"Base class {compiledBaseName} has no constructor taking {arguments.Length} argument(s).");
                            }

                            var constructorParameters = baseConstructor.GetParameters();
                            object[] fitted = new object[arguments.Length];

                            for (int i = 0; i < arguments.Length; i++)
                            {
                                fitted[i] = ClrBridge.ConvertArgument(arguments[i], constructorParameters[i].ParameterType);
                            }

                            baseConstructor.Invoke(instance.BaseInstance, fitted);
                        }
                        else
                        {
                            instance.BaseInstance = context.Proxy.Reflection.CreateInstance(compiledBaseName, arguments);
                        }

                        return new Invocation();
                    }

                    object forwardTarget = EnsureBaseInstance(context, instance);

                    // super() on an emitted subclass must run the base implementation
                    // non-virtually or it would re-enter the script override
                    if (forwardTarget is IScriptEmittedInstance)
                    {
                        var superMethod = ScriptSubclassEmitter.FindSuperMethod(forwardTarget, methodName);

                        if (superMethod != null)
                        {
                            var superParameters = superMethod.GetParameters();
                            object[] fitted = new object[superParameters.Length];

                            for (int i = 0; i < superParameters.Length; i++)
                            {
                                fitted[i] = i < arguments.Length
                                    ? ClrBridge.ConvertArgument(arguments[i], superParameters[i].ParameterType)
                                    : (superParameters[i].ParameterType.IsValueType ? Activator.CreateInstance(superParameters[i].ParameterType) : null);
                            }

                            return new Invocation { ReturnValue = superMethod.Invoke(forwardTarget, fitted) };
                        }
                    }

                    AosCallResult nativeCall = AosCallDispatcher.CallInstance(context, forwardTarget, methodName, arguments);
                    return new Invocation
                    {
                        ReturnValue = nativeCall.ReturnValue,
                        UpdatedArguments = nativeCall.UpdatedArguments,
                    };
                }

                if (string.Equals(methodName, "new", StringComparison.OrdinalIgnoreCase))
                {
                    // No base constructor: super() is a no-op like extending Object
                    return new Invocation();
                }

                throw new Exception($"super(): method {methodName} was not found above {declaringClass.Name}.");
            }

            EnsureMethodAccessible(method, context.ActiveClass);
            return CallResumable(context, method, instance, arguments);
        }

        /// <summary>
        /// Runs a method through a nested interpreter with InnerContext save/resume so
        /// that breakpoints inside the body propagate to the debugger
        /// </summary>
        public static Invocation CallResumable(RuntimeContext context, RuntimeClassMethod method, ScriptObject instance, object[] arguments)
        {
            if (method != null && method.IsAbstract)
            {
                throw new Exception($"Abstract method {method.Name} of {method.DeclaringClass.Name} has no implementation.");
            }

            arguments = ApplyChainDefaults(method, arguments);

            int argumentCount = arguments?.Length ?? 0;

            if (method != null && (argumentCount < method.RequiredParameterCount || argumentCount > method.Parameters.Count))
            {
                throw new Exception($"Method {method.Name} of {method.DeclaringClass.Name} takes {method.RequiredParameterCount} to {method.Parameters.Count} argument(s), but {argumentCount} were supplied.");
            }

            if ((method?.DeclaringClass?.ExtensionTarget != null || method?.DeclaringClass?.HasCompiledExtensionTarget == true) &&
                !method.IsStatic && instance != null &&
                !string.Equals(method.Name, "new", StringComparison.OrdinalIgnoreCase))
            {
                EnsureExtensionInitialized(context, instance, method.DeclaringClass);
            }

            var invocation = new Invocation();

            RuntimeContext methodContext = context.InnerContext;
            XppInterpreter interpreter = methodContext?.Interpreter;
            bool firstTime = interpreter is null;

            if (methodContext is null)
            {
                methodContext = CreateMethodContext(context, method, instance, arguments);

                interpreter = new XppInterpreter(context.Proxy, context.Interpreter?.Options)
                {
                    Debugger = context.Interpreter?.Debugger
                };

                methodContext.Interpreter = interpreter;
                context.InnerContext = methodContext;
            }

            InterpreterResult result;

            try
            {
                result = firstTime
                    ? interpreter.Interpret(methodContext.ByteCode, methodContext, nextAction: context.NextAction)
                    : interpreter.Continue(methodContext.ByteCode, methodContext, context.NextAction);
            }
            catch
            {
                // An exception unwinding through this call ends it: a stale inner
                // context must not be resumed by a later re-execution of the caller
                context.InnerContext = null;
                throw;
            }

            invocation.Result = result;

            RuntimeClassMethod invokedMethod = method ?? methodContext.ActiveMethod;

            if ((result?.HasFinished ?? true) && invokedMethod != null)
            {
                invocation.UpdatedArguments = invokedMethod.Parameters
                    .Select(parameter => methodContext.ScopeHandler.GetVar(parameter.Name))
                    .ToArray();
            }

            // method is null on debugger resume; the stack state carries the return value
            if ((result?.HasFinished ?? true) && methodContext.Returned && methodContext.Stack.Count > 0 &&
                (method is null || !method.IsVoid))
            {
                invocation.ReturnValue = methodContext.Stack.Pop();
            }

            return invocation;
        }

        private static object[] ApplyChainDefaults(RuntimeClassMethod method, object[] arguments)
        {
            if (method?.NextMethod is null)
            {
                return arguments;
            }

            RuntimeClassMethod original = method;

            while (original.NextMethod != null)
            {
                original = original.NextMethod;
            }

            int supplied = arguments?.Length ?? 0;

            if (ReferenceEquals(original, method) || supplied >= method.Parameters.Count ||
                original.Parameters.Count != method.Parameters.Count)
            {
                return arguments;
            }

            var expanded = new object[method.Parameters.Count];

            if (supplied > 0)
            {
                Array.Copy(arguments, expanded, supplied);
            }

            for (int index = supplied; index < expanded.Length; index++)
            {
                if (index >= original.ParameterHasDefault.Count || !original.ParameterHasDefault[index])
                {
                    // Leave validation to the wrapper so the caller gets its normal
                    // required-argument diagnostic.
                    return arguments;
                }

                expanded[index] = original.ParameterDefaults[index];
            }

            return expanded;
        }

        /// <summary>
        /// Runs a method to completion without pausing on breakpoints
        /// (used for constructors and field/static initializers)
        /// </summary>
        public static Invocation RunSynchronous(RuntimeContext context, RuntimeClassMethod method, ScriptObject instance, object[] arguments)
        {
            var invocation = new Invocation();

            RuntimeContext methodContext = CreateMethodContext(context, method, instance, arguments);

            var interpreter = new XppInterpreter(context.Proxy, context.Interpreter?.Options);
            methodContext.Interpreter = interpreter;

            interpreter.Interpret(methodContext.ByteCode, methodContext, nextAction: DebugAction.StopDebugging);

            if (methodContext.Returned && !method.IsVoid && methodContext.Stack.Count > 0)
            {
                invocation.ReturnValue = methodContext.Stack.Pop();
            }

            invocation.UpdatedArguments = method.Parameters
                .Select(parameter => methodContext.ScopeHandler.GetVar(parameter.Name))
                .ToArray();

            return invocation;
        }

        private static RuntimeContext CreateMethodContext(RuntimeContext context, RuntimeClassMethod method, ScriptObject instance, object[] arguments)
        {
            var byteCode = new ByteCode(method.Instructions)
            {
                DeclaredFunctions = context.ByteCode.DeclaredFunctions,
                DeclaredClasses = context.ByteCode.DeclaredClasses,
            };

            Scope rootScope;

            if (method.IsStatic)
            {
                rootScope = EnsureStaticsInitialized(context, method.DeclaringClass);
            }
            else if (method.DeclaringClass.ExtensionTarget != null || method.DeclaringClass.HasCompiledExtensionTarget)
            {
                rootScope = GetExtensionScope(instance, method.DeclaringClass);
            }
            else
            {
                rootScope = instance.FieldScope;
            }

            var scopeHandler = new ScopeHandler(context.Proxy, rootScope);
            scopeHandler.BeginScope(); // method locals live in a child of the field/static scope

            var methodContext = new RuntimeContext(context.Proxy, byteCode, scopeHandler)
            {
                ActiveClass = method.DeclaringClass,
                ActiveMethod = method,
                ExecutionGuard = context.ExecutionGuard,
            };

            if (!method.IsStatic)
            {
                scopeHandler.CurrentScope.SetVar("this", instance, context.Proxy.Casting, true, typeof(ScriptObject));
            }

            int argumentCount = arguments?.Length ?? 0;

            for (int numParam = 0; numParam < method.Parameters.Count; numParam++)
            {
                FunctionDeclarationParameter parameter = method.Parameters[numParam];
                System.Type declarationType = parameter.DeclarationClrType;

                object value;

                if (numParam < argumentCount)
                {
                    value = arguments[numParam];
                }
                else if (numParam < method.ParameterHasDefault.Count && method.ParameterHasDefault[numParam])
                {
                    value = method.ParameterDefaults[numParam];
                }
                else
                {
                    value = GetDefaultParameterValue(context, parameter);
                }

                if (value is null && declarationType != null && context.Proxy.Reflection.IsCommonType(declarationType))
                {
                    value = context.Proxy.Casting.GetDefaultValueForType(parameter.DeclarationType.Lexeme);
                }

                scopeHandler.CurrentScope.SetVar(parameter.Name, value, context.Proxy.Casting, true, declarationType);
            }

            return methodContext;
        }

        private static void EnsureMethodAccessible(RuntimeClassMethod method, RuntimeClass callerClass)
        {
            if (method.Access == AccessModifier.Public || method.Access == AccessModifier.Internal)
            {
                return;
            }

            if (callerClass != null && ReferenceEquals(callerClass, method.DeclaringClass))
            {
                return;
            }

            if (method.Access == AccessModifier.Protected && callerClass != null)
            {
                if (ReferenceEquals(callerClass.ExtensionTarget, method.DeclaringClass))
                {
                    return;
                }

                for (RuntimeClass current = callerClass; current != null; current = current.BaseClass)
                {
                    if (ReferenceEquals(current, method.DeclaringClass))
                    {
                        return;
                    }
                }
            }

            throw new Exception($"Method {method.DeclaringClass.Name}.{method.Name} is not accessible from the current context.");
        }

        internal static void EnsureFieldAccessible(RuntimeClassField field, RuntimeClass callerClass, bool writing)
        {
            if (field is null)
            {
                return;
            }

            if (writing && field.IsConst)
            {
                throw new Exception($"Cannot assign to const field {field.DeclaringClass.Name}.{field.Name}.");
            }

            if (field.Access == AccessModifier.Public || field.Access == AccessModifier.Internal)
            {
                return;
            }

            if (callerClass != null && ReferenceEquals(callerClass, field.DeclaringClass))
            {
                return;
            }

            if (field.Access == AccessModifier.Protected && callerClass != null)
            {
                if (ReferenceEquals(callerClass.ExtensionTarget, field.DeclaringClass))
                {
                    return;
                }

                for (RuntimeClass current = callerClass; current != null; current = current.BaseClass)
                {
                    if (ReferenceEquals(current, field.DeclaringClass))
                    {
                        return;
                    }
                }
            }

            throw new Exception($"Field {field.DeclaringClass.Name}.{field.Name} is not accessible from the current context.");
        }

        private static void EnsureExtensionInitialized(RuntimeContext context, ScriptObject instance, RuntimeClass extensionClass)
        {
            if (!instance.InitializedExtensions.Add(extensionClass.Name))
            {
                return;
            }

            try
            {
                Scope extensionScope = GetExtensionScope(instance, extensionClass);
                extensionScope.SecondaryParent = EnsureStaticsInitialized(context, extensionClass);

                if (extensionClass.FieldInitInstructions.Count > 0)
                {
                    RunInstructionsInScope(context, extensionClass.FieldInitInstructions, extensionScope);
                }

                RuntimeClassMethod constructor = extensionClass.FindMethod("new", walkBaseChain: false);

                if (constructor != null)
                {
                    RunSynchronous(context, constructor, instance, new object[0]);
                }
            }
            catch
            {
                instance.InitializedExtensions.Remove(extensionClass.Name);
                throw;
            }
        }

        internal static Scope GetExtensionScope(ScriptObject instance, RuntimeClass extensionClass)
        {
            if (!instance.ExtensionScopes.TryGetValue(extensionClass.Name, out Scope scope))
            {
                scope = new Scope { Parent = instance.FieldScope };
                instance.ExtensionScopes[extensionClass.Name] = scope;
            }

            return scope;
        }

        internal static Scope GetInstanceFieldScope(
            RuntimeContext context,
            ScriptObject instance,
            RuntimeClassField field)
        {
            if (field?.DeclaringClass?.ExtensionTarget != null || field?.DeclaringClass?.HasCompiledExtensionTarget == true)
            {
                EnsureExtensionInitialized(context, instance, field.DeclaringClass);
                return GetExtensionScope(instance, field.DeclaringClass);
            }

            return instance.FieldScope;
        }

        private static object GetDefaultParameterValue(RuntimeContext context, FunctionDeclarationParameter parameter)
        {
            try
            {
                if (parameter.DeclarationClrType != null && parameter.DeclarationClrType is Core.ScriptClassType)
                {
                    return null;
                }

                return context.Proxy.Casting.GetDefaultValueForSystemType(parameter.DeclarationClrType);
            }
            catch
            {
                return null;
            }
        }
    }
}

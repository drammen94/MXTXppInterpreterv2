using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace XppInterpreter.Interpreter.Bytecode
{
    /// <summary>
    /// Marker implemented by every runtime-emitted script subclass
    /// </summary>
    public interface IScriptEmittedInstance
    {
    }

    /// <summary>
    /// Callback from emitted method overrides into the interpreter
    /// </summary>
    public interface IScriptMethodDispatcher
    {
        object Dispatch(string methodName, object[] arguments);
    }

    /// <summary>
    /// Static helper called from emitted IL: routes the call to the dispatcher and
    /// coerces the interpreted return value to the CLR return type so the emitted
    /// unbox/cast is always safe
    /// </summary>
    public static class ScriptDispatchHelper
    {
        public static object Invoke(IScriptMethodDispatcher dispatcher, string methodName, object[] arguments, Type returnType)
        {
            object result = dispatcher.Dispatch(methodName, arguments);

            if (returnType == typeof(void))
            {
                return null;
            }

            result = ClrBridge.ConvertArgument(result, returnType);

            if (result is null || !returnType.IsInstanceOfType(result))
            {
                return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
            }

            return result;
        }

        public static readonly MethodInfo InvokeMethod = typeof(ScriptDispatchHelper).GetMethod(nameof(Invoke));
    }

    /// <summary>
    /// Emits, at runtime, a genuine CLR subclass of a compiled base class whose
    /// virtual-method overrides dispatch into the interpreted script methods.
    /// This makes compiled-side virtual calls (template methods, callbacks, any
    /// API taking the base type) reach script overrides - the script instance IS
    /// an instance of the compiled base class.
    /// </summary>
    public static class ScriptSubclassEmitter
    {
        public const string SuperMethodPrefix = "__super_";
        private const string DispatcherFieldName = "__dispatcher";

        private static readonly object _lock = new object();
        private static ModuleBuilder _moduleBuilder;
        private static int _typeCounter;

        private static readonly ConditionalWeakTable<RuntimeClass, Type> _emittedTypes =
            new ConditionalWeakTable<RuntimeClass, Type>();

        private static ModuleBuilder Module
        {
            get
            {
                if (_moduleBuilder is null)
                {
                    var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
                        new AssemblyName("XppInterpreter.ScriptProxies"),
                        AssemblyBuilderAccess.Run);

                    _moduleBuilder = assemblyBuilder.DefineDynamicModule("ScriptProxies");
                }

                return _moduleBuilder;
            }
        }

        /// <summary>
        /// Returns the emitted subclass for the script class over the given compiled
        /// base, or null when the base cannot be subclassed (sealed, no reachable
        /// constructor, non-public) - callers fall back to plain wrapping.
        /// </summary>
        public static Type TryGetEmittedType(RuntimeClass runtimeClass, Type compiledBase)
        {
            if (compiledBase is null || compiledBase.IsSealed || compiledBase.IsInterface ||
                compiledBase.IsValueType || !compiledBase.IsVisible)
            {
                return null;
            }

            lock (_lock)
            {
                if (_emittedTypes.TryGetValue(runtimeClass, out Type cached))
                {
                    return cached;
                }

                Type emitted;

                try
                {
                    emitted = Emit(runtimeClass, compiledBase);
                }
                catch
                {
                    emitted = null;
                }

                if (emitted != null)
                {
                    _emittedTypes.Add(runtimeClass, emitted);
                }

                return emitted;
            }
        }

        public static void AttachDispatcher(object emittedInstance, IScriptMethodDispatcher dispatcher)
        {
            emittedInstance.GetType()
                .GetField(DispatcherFieldName, BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(emittedInstance, dispatcher);
        }

        public static MethodInfo FindSuperMethod(object emittedInstance, string methodName)
        {
            return emittedInstance.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => string.Equals(m.Name, SuperMethodPrefix + methodName, StringComparison.OrdinalIgnoreCase));
        }

        private static Type Emit(RuntimeClass runtimeClass, Type compiledBase)
        {
            int typeNumber = System.Threading.Interlocked.Increment(ref _typeCounter);

            TypeBuilder typeBuilder = Module.DefineType(
                $"{runtimeClass.Name}_ScriptProxy_{typeNumber}",
                TypeAttributes.Public | TypeAttributes.Class,
                compiledBase,
                new[] { typeof(IScriptEmittedInstance) }
                    .Concat(GetCompiledInterfaces(runtimeClass))
                    .Distinct()
                    .ToArray());

            FieldBuilder dispatcherField = typeBuilder.DefineField(
                DispatcherFieldName,
                typeof(IScriptMethodDispatcher),
                FieldAttributes.Public);

            EmitConstructors(typeBuilder, compiledBase);

            // One override per unique script method name that matches an overridable
            // base method (derived-first walk so overrides shadow correctly)
            var overriddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (RuntimeClass current = runtimeClass; current != null; current = current.BaseClass)
            {
                foreach (RuntimeClassMethod scriptMethod in current.Methods)
                {
                    if (scriptMethod.IsStatic || scriptMethod.IsAbstract ||
                        string.Equals(scriptMethod.Name, "new", StringComparison.OrdinalIgnoreCase) ||
                        !overriddenNames.Add(scriptMethod.Name))
                    {
                        continue;
                    }

                    MethodInfo baseMethod = FindOverridableBaseMethod(compiledBase, scriptMethod);

                    if (baseMethod is null)
                    {
                        continue;
                    }

                    EmitOverride(typeBuilder, dispatcherField, baseMethod);
                    EmitSuperPassThrough(typeBuilder, baseMethod);
                }
            }

            foreach (Type compiledInterface in GetCompiledInterfaces(runtimeClass))
            {
                foreach (MethodInfo interfaceMethod in compiledInterface.GetMethods().Where(method => !method.IsSpecialName))
                {
                    EmitInterfaceImplementation(typeBuilder, dispatcherField, interfaceMethod);
                }
            }

            return typeBuilder.CreateType();
        }

        private static MethodInfo FindOverridableBaseMethod(Type compiledBase, RuntimeClassMethod scriptMethod)
        {
            return compiledBase
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.IsVirtual && !m.IsFinal && !m.IsSpecialName && !m.IsPrivate &&
                    string.Equals(m.Name, scriptMethod.Name, StringComparison.OrdinalIgnoreCase) &&
                    MethodSignatureMatches(m, scriptMethod))
                .FirstOrDefault();
        }

        private static IEnumerable<Type> GetCompiledInterfaces(RuntimeClass runtimeClass)
        {
            var seen = new HashSet<Type>();

            for (RuntimeClass current = runtimeClass; current != null; current = current.BaseClass)
            {
                foreach (Type compiledInterface in current.CompiledInterfaces)
                {
                    if (seen.Add(compiledInterface))
                    {
                        yield return compiledInterface;
                    }
                }
            }
        }

        private static bool MethodSignatureMatches(MethodInfo compiledMethod, RuntimeClassMethod scriptMethod)
        {
            ParameterInfo[] compiledParameters = compiledMethod.GetParameters();
            if (compiledParameters.Length != scriptMethod.Parameters.Count)
            {
                return false;
            }

            for (int index = 0; index < compiledParameters.Length; index++)
            {
                Type compiledType = compiledParameters[index].ParameterType;
                if (compiledType.IsByRef)
                {
                    compiledType = compiledType.GetElementType();
                }

                Type scriptType = scriptMethod.Parameters[index].DeclarationClrType;
                if (compiledType != scriptType && !(compiledType?.IsAssignableFrom(scriptType) ?? false))
                {
                    return false;
                }
            }

            return true;
        }

        private static void EmitConstructors(TypeBuilder typeBuilder, Type compiledBase)
        {
            foreach (ConstructorInfo baseConstructor in compiledBase.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (baseConstructor.IsPrivate)
                {
                    continue;
                }

                Type[] parameterTypes = baseConstructor.GetParameters().Select(p => p.ParameterType).ToArray();

                ConstructorBuilder constructorBuilder = typeBuilder.DefineConstructor(
                    MethodAttributes.Public | MethodAttributes.HideBySig,
                    CallingConventions.Standard,
                    parameterTypes);

                ILGenerator il = constructorBuilder.GetILGenerator();

                il.Emit(OpCodes.Ldarg_0);

                for (int i = 0; i < parameterTypes.Length; i++)
                {
                    il.Emit(OpCodes.Ldarg, i + 1);
                }

                il.Emit(OpCodes.Call, baseConstructor);
                il.Emit(OpCodes.Ret);
            }
        }

        private static void EmitOverride(TypeBuilder typeBuilder, FieldBuilder dispatcherField, MethodInfo baseMethod)
        {
            ParameterInfo[] parameters = baseMethod.GetParameters();
            Type[] parameterTypes = parameters.Select(p => p.ParameterType).ToArray();

            MethodAttributes attributes =
                (baseMethod.IsPublic ? MethodAttributes.Public : MethodAttributes.Family) |
                MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.ReuseSlot;

            MethodBuilder methodBuilder = typeBuilder.DefineMethod(
                baseMethod.Name,
                attributes,
                baseMethod.ReturnType,
                parameterTypes);

            ILGenerator il = methodBuilder.GetILGenerator();
            System.Reflection.Emit.Label callBase = il.DefineLabel();

            // During base construction the dispatcher is not attached yet: run base
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, dispatcherField);
            il.Emit(OpCodes.Brfalse, callBase);

            EmitDispatcherCall(il, dispatcherField, baseMethod.Name, baseMethod.ReturnType, parameterTypes);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(callBase);
            il.Emit(OpCodes.Ldarg_0);

            for (int i = 0; i < parameterTypes.Length; i++)
            {
                il.Emit(OpCodes.Ldarg, i + 1);
            }

            il.Emit(OpCodes.Call, baseMethod); // non-virtual: the base implementation
            il.Emit(OpCodes.Ret);

            typeBuilder.DefineMethodOverride(methodBuilder, baseMethod);
        }

        private static void EmitInterfaceImplementation(
            TypeBuilder typeBuilder,
            FieldBuilder dispatcherField,
            MethodInfo interfaceMethod)
        {
            Type[] parameterTypes = interfaceMethod.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
            string implementationName = interfaceMethod.DeclaringType.FullName + "." + interfaceMethod.Name;
            MethodBuilder methodBuilder = typeBuilder.DefineMethod(
                implementationName,
                MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final |
                    MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                interfaceMethod.ReturnType,
                parameterTypes);

            ILGenerator il = methodBuilder.GetILGenerator();
            EmitDispatcherCall(il, dispatcherField, interfaceMethod.Name, interfaceMethod.ReturnType, parameterTypes);
            il.Emit(OpCodes.Ret);
            typeBuilder.DefineMethodOverride(methodBuilder, interfaceMethod);
        }

        private static void EmitDispatcherCall(
            ILGenerator il,
            FieldBuilder dispatcherField,
            string methodName,
            Type returnType,
            Type[] parameterTypes)
        {
            LocalBuilder arguments = il.DeclareLocal(typeof(object[]));
            LocalBuilder result = il.DeclareLocal(typeof(object));

            il.Emit(OpCodes.Ldc_I4, parameterTypes.Length);
            il.Emit(OpCodes.Newarr, typeof(object));
            il.Emit(OpCodes.Stloc, arguments);

            for (int i = 0; i < parameterTypes.Length; i++)
            {
                Type parameterType = parameterTypes[i];
                Type valueType = parameterType.IsByRef ? parameterType.GetElementType() : parameterType;

                il.Emit(OpCodes.Ldloc, arguments);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldarg, i + 1);

                if (parameterType.IsByRef)
                {
                    il.Emit(OpCodes.Ldobj, valueType);
                }

                if (valueType.IsValueType)
                {
                    il.Emit(OpCodes.Box, valueType);
                }

                il.Emit(OpCodes.Stelem_Ref);
            }

            // ScriptDispatchHelper.Invoke(__dispatcher, "name", new object[]{...}, typeof(TReturn))
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, dispatcherField);
            il.Emit(OpCodes.Ldstr, methodName);
            il.Emit(OpCodes.Ldloc, arguments);
            il.Emit(OpCodes.Ldtoken, returnType);
            il.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle)));
            il.Emit(OpCodes.Call, ScriptDispatchHelper.InvokeMethod);
            il.Emit(OpCodes.Stloc, result);

            for (int i = 0; i < parameterTypes.Length; i++)
            {
                if (!parameterTypes[i].IsByRef) continue;

                Type valueType = parameterTypes[i].GetElementType();
                il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Ldloc, arguments);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);

                if (valueType.IsValueType)
                {
                    il.Emit(OpCodes.Unbox_Any, valueType);
                }
                else
                {
                    il.Emit(OpCodes.Castclass, valueType);
                }

                il.Emit(OpCodes.Stobj, valueType);
            }

            if (returnType == typeof(void))
            {
                return;
            }

            il.Emit(OpCodes.Ldloc, result);
            if (returnType.IsValueType)
            {
                il.Emit(OpCodes.Unbox_Any, returnType);
            }
            else
            {
                il.Emit(OpCodes.Castclass, returnType);
            }
        }

        /// <summary>
        /// __super_name(...) calls the base implementation non-virtually, so script
        /// super() never re-enters the emitted override
        /// </summary>
        private static void EmitSuperPassThrough(TypeBuilder typeBuilder, MethodInfo baseMethod)
        {
            Type[] parameterTypes = baseMethod.GetParameters().Select(p => p.ParameterType).ToArray();

            MethodBuilder methodBuilder = typeBuilder.DefineMethod(
                SuperMethodPrefix + baseMethod.Name,
                MethodAttributes.Public | MethodAttributes.HideBySig,
                baseMethod.ReturnType,
                parameterTypes);

            ILGenerator il = methodBuilder.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);

            for (int i = 0; i < parameterTypes.Length; i++)
            {
                il.Emit(OpCodes.Ldarg, i + 1);
            }

            il.Emit(OpCodes.Call, baseMethod);
            il.Emit(OpCodes.Ret);
        }
    }

    /// <summary>
    /// Routes emitted-override calls into the interpreted script methods
    /// </summary>
    public class ScriptMethodDispatcher : IScriptMethodDispatcher
    {
        private readonly RuntimeContext _context;
        private readonly ScriptObject _scriptObject;

        public ScriptMethodDispatcher(RuntimeContext context, ScriptObject scriptObject)
        {
            _context = context;
            _scriptObject = scriptObject;
        }

        public object Dispatch(string methodName, object[] arguments)
        {
            RuntimeClassMethod method = _scriptObject.Class.FindMethod(methodName);

            if (method is null || method.IsStatic)
            {
                // No script override after all: run the base implementation
                MethodInfo superMethod = ScriptSubclassEmitter.FindSuperMethod(_scriptObject.BaseInstance, methodName);
                return superMethod?.Invoke(_scriptObject.BaseInstance, arguments);
            }

            ScriptClassRuntime.Invocation invocation =
                ScriptClassRuntime.RunSynchronous(_context, method, _scriptObject, arguments);

            if (invocation.UpdatedArguments != null && arguments != null)
            {
                Array.Copy(invocation.UpdatedArguments, arguments,
                    Math.Min(invocation.UpdatedArguments.Length, arguments.Length));
            }

            return invocation.ReturnValue;
        }
    }
}

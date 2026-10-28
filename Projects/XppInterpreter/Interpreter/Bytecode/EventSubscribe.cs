using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using XppInterpreter.Core;
using XppInterpreter.Interpreter.Bytecode.Events;
using XppInterpreter.Parser;

namespace XppInterpreter.Interpreter.Bytecode
{
    /// <summary>
    /// Subscribes (+=) or unsubscribes (-=) an event handler on a delegate. The
    /// delegate reference on the stack is either an XppScriptDelegate (declared in a
    /// script class) or an EventSubscriptionHandle (a CLR delegate field on a
    /// compiled object). Handlers can be local script functions, static methods or
    /// instance methods.
    /// </summary>
    class EventSubscribe : IInstruction
    {
        public string OperationCode => Unsubscribe ? "EVENT_UNSUBSCRIBE" : "EVENT_SUBSCRIBE";

        public RefFunction LocalFunction { get; }
        public EventHandlerKind Kind { get; }
        public string MethodName { get; }
        public string CallerName { get; }
        public bool Unsubscribe { get; }

        /// <summary>
        /// Tracks CLR delegates created for subscriptions so -= can remove them.
        /// Keyed weakly by the subscribed instance so entries die with it.
        /// </summary>
        private class ClrSubscriptionRegistry
        {
            private class Entry
            {
                public string FieldName;
                public object HandlerKey;
                public Delegate Created;
            }

            private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, List<Entry>> _entries =
                new System.Runtime.CompilerServices.ConditionalWeakTable<object, List<Entry>>();

            private readonly object _lock = new object();

            public void Add(object instance, string fieldName, object handlerKey, Delegate created)
            {
                lock (_lock)
                {
                    var list = _entries.GetOrCreateValue(instance);
                    list.Add(new Entry { FieldName = fieldName, HandlerKey = handlerKey, Created = created });
                }
            }

            public Delegate Take(object instance, string fieldName, object handlerKey)
            {
                lock (_lock)
                {
                    if (!_entries.TryGetValue(instance, out List<Entry> list))
                    {
                        return null;
                    }

                    var entry = list.LastOrDefault(e =>
                        string.Equals(e.FieldName, fieldName, StringComparison.OrdinalIgnoreCase) &&
                        Equals(e.HandlerKey, handlerKey));

                    if (entry != null)
                    {
                        list.Remove(entry);
                        return entry.Created;
                    }

                    return null;
                }
            }
        }

        private static readonly ClrSubscriptionRegistry _registry = new ClrSubscriptionRegistry();

        public EventSubscribe(RefFunction localFunction, EventHandlerKind kind, string methodName, string callerName, bool unsubscribe)
        {
            LocalFunction = localFunction;
            Kind = kind;
            MethodName = methodName;
            CallerName = callerName;
            Unsubscribe = unsubscribe;
        }

        public void Execute(RuntimeContext context)
        {
            object target = context.Stack.Pop();

            if (target is XppScriptDelegate scriptDelegate)
            {
                ExecuteOnScriptDelegate(context, scriptDelegate);
                return;
            }

            if (target is EventSubscriptionHandle clrHandle)
            {
                ExecuteOnClrDelegate(context, clrHandle);
                return;
            }

            throw new Exception("The expression is not a delegate.");
        }

        private void ExecuteOnScriptDelegate(RuntimeContext context, XppScriptDelegate scriptDelegate)
        {
            switch (Kind)
            {
                case EventHandlerKind.LocalFunction:
                    if (Unsubscribe) scriptDelegate.RemoveLocalFunction(LocalFunction);
                    else scriptDelegate.AddLocalFunction(LocalFunction);
                    break;

                case EventHandlerKind.StaticMethod:
                    {
                        var handlerClass = context.ByteCode.FindDeclaredClass(CallerName);

                        if (handlerClass?.FindMethod(MethodName) is RuntimeClassMethod staticMethod && staticMethod.IsStatic)
                        {
                            if (Unsubscribe) scriptDelegate.RemoveMethod(null, staticMethod);
                            else scriptDelegate.AddMethod(null, staticMethod);
                            break;
                        }

                        throw new Exception($"Static method {CallerName}::{MethodName} was not found.");
                    }

                case EventHandlerKind.InstanceMethod:
                    {
                        if (context.ScopeHandler.GetVar(CallerName) is ScriptObject handlerTarget &&
                            handlerTarget.Class.FindMethod(MethodName) is RuntimeClassMethod instanceMethod && !instanceMethod.IsStatic)
                        {
                            if (Unsubscribe) scriptDelegate.RemoveMethod(handlerTarget, instanceMethod);
                            else scriptDelegate.AddMethod(handlerTarget, instanceMethod);
                            break;
                        }

                        throw new Exception($"Instance method {CallerName}.{MethodName} was not found.");
                    }
            }
        }

        private void ExecuteOnClrDelegate(RuntimeContext context, EventSubscriptionHandle handle)
        {
            FieldInfo delegateField = ReflectionHelper.GetField(handle.Instance.GetType(), handle.MethodName);
            Type delegateType = delegateField.FieldType;
            var currentDelegate = (MulticastDelegate)delegateField.GetValue(handle.Instance);

            object handlerKey = GetClrHandlerKey(context);

            if (Unsubscribe)
            {
                Delegate created = _registry.Take(handle.Instance, handle.MethodName, handlerKey);

                if (created != null)
                {
                    delegateField.SetValue(handle.Instance, Delegate.Remove(currentDelegate, created));
                }

                return;
            }

            Delegate handlerDelegate = CreateClrHandler(context, delegateType);

            _registry.Add(handle.Instance, handle.MethodName, handlerKey, handlerDelegate);
            delegateField.SetValue(handle.Instance, Delegate.Combine(currentDelegate, handlerDelegate));
        }

        private object GetClrHandlerKey(RuntimeContext context)
        {
            switch (Kind)
            {
                case EventHandlerKind.LocalFunction:
                    return LocalFunction;
                case EventHandlerKind.StaticMethod:
                    return $"{CallerName}::{MethodName}".ToLowerInvariant();
                default:
                    // Keyed by the current value of the handler variable AND the
                    // method name, so two methods on one object stay distinct
                    return Tuple.Create(context.ScopeHandler.GetVar(CallerName), MethodName.ToLowerInvariant());
            }
        }

        private Delegate CreateClrHandler(RuntimeContext context, Type delegateType)
        {
            Type[] parameterTypes = delegateType.GetMethod("Invoke").GetParameters().Select(p => p.ParameterType).ToArray();

            switch (Kind)
            {
                case EventHandlerKind.LocalFunction:
                    {
                        var executor = new DelegateEventExecutor(context, LocalFunction);
                        return Delegate.CreateDelegate(delegateType, executor, DelegateHelper.GetMethodToExecute(executor, parameterTypes));
                    }

                case EventHandlerKind.StaticMethod:
                    {
                        // Script class static method
                        var handlerClass = context.ByteCode.FindDeclaredClass(CallerName);

                        if (handlerClass?.FindMethod(MethodName) is RuntimeClassMethod scriptStatic && scriptStatic.IsStatic)
                        {
                            var executor = new DelegateEventExecutor(context, arguments =>
                                ScriptClassRuntime.RunSynchronous(context, scriptStatic, null, arguments));

                            return Delegate.CreateDelegate(delegateType, executor, DelegateHelper.GetMethodToExecute(executor, parameterTypes));
                        }

                        // Compiled class static method
                        Type callerType = ResolveClrCallerType(context);
                        MethodInfo staticMethod = ReflectionHelper.GetMethod(callerType, MethodName);

                        if (staticMethod is null)
                        {
                            throw new Exception($"Static method {CallerName}::{MethodName} was not found.");
                        }

                        return Delegate.CreateDelegate(delegateType, null, staticMethod);
                    }

                default:
                    {
                        object handlerTarget = context.ScopeHandler.GetVar(CallerName);

                        if (handlerTarget is ScriptObject scriptTarget)
                        {
                            if (!(scriptTarget.Class.FindMethod(MethodName) is RuntimeClassMethod scriptMethod) || scriptMethod.IsStatic)
                            {
                                throw new Exception($"Instance method {CallerName}.{MethodName} was not found.");
                            }

                            var executor = new DelegateEventExecutor(context, arguments =>
                                ScriptClassRuntime.RunSynchronous(context, scriptMethod, scriptTarget, arguments));

                            return Delegate.CreateDelegate(delegateType, executor, DelegateHelper.GetMethodToExecute(executor, parameterTypes));
                        }

                        MethodInfo instanceMethod = ReflectionHelper.GetMethod(handlerTarget.GetType(), MethodName);

                        if (instanceMethod is null)
                        {
                            throw new Exception($"Instance method {CallerName}.{MethodName} was not found.");
                        }

                        return Delegate.CreateDelegate(delegateType, handlerTarget, instanceMethod);
                    }
            }
        }

        private Type ResolveClrCallerType(RuntimeContext context)
        {
            Type callerType = null;

            try
            {
                callerType = context.Proxy.Casting.GetSystemTypeFromTypeName(CallerName);
            }
            catch
            {
            }

            if (callerType is null)
            {
                callerType = ClrTypeResolver.FindType(CallerName);
            }

            if (callerType is null)
            {
                throw new Exception($"Type {CallerName} was not found.");
            }

            return callerType;
        }
    }
}

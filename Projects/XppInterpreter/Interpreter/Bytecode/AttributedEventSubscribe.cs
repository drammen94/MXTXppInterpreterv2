using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using XppInterpreter.Interpreter.Bytecode.Events;
using XppInterpreter.Parser;

namespace XppInterpreter.Interpreter.Bytecode
{
    /// <summary>
    /// Binds [SubscribesTo(...)] on an interpreted static method to a generated
    /// AOS/CLR event or delegate field when the script starts executing.
    /// </summary>
    internal sealed class AttributedEventSubscribe : IInstruction
    {
        private static readonly HashSet<string> EventAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SubscribesTo",
            "DataEventHandler",
            "FormEventHandler",
            "FormControlEventHandler",
            "FormDataSourceEventHandler",
            "FormDataFieldEventHandler",
        };

        public string OperationCode => $"ATTRIBUTE_SUBSCRIBE {AttributeName} {SourceTypeName}::{EventName}";
        public RuntimeClassMethod Handler { get; }
        public string AttributeName { get; }
        public string SourceTypeName { get; }
        public string ElementName { get; }
        public string FieldName { get; }
        public string EventName { get; }

        public AttributedEventSubscribe(RuntimeClassMethod handler, XppAttributeDeclaration attribute)
        {
            Handler = handler;
            AttributeName = NormalizeAttributeName(attribute?.Name);

            switch (AttributeName.ToLowerInvariant())
            {
                case "formcontroleventhandler":
                    SourceTypeName = ArgumentAfter(attribute, "formControlStr");
                    ElementName = SecondArgumentAfter(attribute, "formControlStr");
                    break;
                case "formdatasourceeventhandler":
                    SourceTypeName = ArgumentAfter(attribute, "formDataSourceStr");
                    ElementName = SecondArgumentAfter(attribute, "formDataSourceStr");
                    break;
                case "formdatafieldeventhandler":
                    SourceTypeName = ArgumentAfter(attribute, "formDataFieldStr");
                    ElementName = SecondArgumentAfter(attribute, "formDataFieldStr");
                    FieldName = ThirdArgumentAfter(attribute, "formDataFieldStr");
                    break;
                case "formeventhandler":
                    SourceTypeName = ArgumentAfter(attribute, "formStr");
                    break;
                case "dataeventhandler":
                    SourceTypeName = ArgumentAfter(attribute, "tableStr") ??
                        ArgumentAfter(attribute, "dataEntityViewStr") ??
                        ArgumentAfter(attribute, "viewStr");
                    break;
                default:
                    SourceTypeName = ArgumentAfter(attribute, "classStr") ?? ArgumentAfter(attribute, "tableStr");
                    break;
            }

            EventName = SecondArgumentAfter(attribute, "delegateStr") ?? LastArgument(attribute);
        }

        public static bool IsEventAttribute(string attributeName)
        {
            return EventAttributes.Contains(NormalizeAttributeName(attributeName));
        }

        public void Execute(RuntimeContext context)
        {
            if (!Handler.IsStatic)
            {
                throw new Exception($"Attributed event handler {Handler.DeclaringClass.Name}.{Handler.Name} must be static.");
            }

            if (!string.Equals(AttributeName, "SubscribesTo", StringComparison.OrdinalIgnoreCase))
            {
                SubscribeThroughAosEventManager(context);
                return;
            }

            SubscribeToDelegate(context);
        }

        private void SubscribeToDelegate(RuntimeContext context)
        {
            Type sourceType = null;
            try { sourceType = context.Proxy.Casting.GetSystemTypeFromTypeName(SourceTypeName); }
            catch { }
            sourceType = sourceType ?? Core.ClrTypeResolver.FindType(SourceTypeName);

            if (sourceType is null)
            {
                throw new Exception($"Event source type {SourceTypeName} was not found.");
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            EventInfo eventInfo = sourceType.GetEvents(flags)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, EventName, StringComparison.OrdinalIgnoreCase));
            FieldInfo delegateField = sourceType.GetFields(flags)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, EventName, StringComparison.OrdinalIgnoreCase) &&
                    typeof(Delegate).IsAssignableFrom(candidate.FieldType));

            Type delegateType = eventInfo?.EventHandlerType ?? delegateField?.FieldType;
            if (delegateType is null)
            {
                throw new Exception($"Delegate/event {SourceTypeName}::{EventName} was not found.");
            }

            Type[] parameterTypes = delegateType.GetMethod("Invoke").GetParameters()
                .Select(parameter => parameter.ParameterType).ToArray();

            if (Handler.Parameters.Count != parameterTypes.Length)
            {
                throw new Exception($"Attributed handler {Handler.DeclaringClass.Name}.{Handler.Name} takes {Handler.Parameters.Count} parameter(s), but {SourceTypeName}::{EventName} declares {parameterTypes.Length}.");
            }

            var executor = new DelegateEventExecutor(context, arguments =>
                ScriptClassRuntime.RunSynchronous(context, Handler, null, arguments));
            Delegate callback = Delegate.CreateDelegate(
                delegateType,
                executor,
                DelegateHelper.GetMethodToExecute(executor, parameterTypes));

            if (eventInfo != null)
            {
                eventInfo.AddEventHandler(null, callback);
            }
            else
            {
                var current = (Delegate)delegateField.GetValue(null);
                delegateField.SetValue(null, Delegate.Combine(current, callback));
            }
        }

        private void SubscribeThroughAosEventManager(RuntimeContext context)
        {
            string managerTypeName;
            string delegateTypeName;
            string enumTypeName;
            object[] leadingArguments;

            switch (AttributeName.ToLowerInvariant())
            {
                case "dataeventhandler":
                    managerTypeName = "Microsoft.Dynamics.Ax.Xpp.DataEventManager";
                    delegateTypeName = "Microsoft.Dynamics.Ax.Xpp.DataEventHandler";
                    enumTypeName = "Dynamics.AX.Application.DataEventType";
                    leadingArguments = new object[] { SourceTypeName };
                    break;
                case "formeventhandler":
                    managerTypeName = "Microsoft.Dynamics.Ax.Xpp.FormEventManager";
                    delegateTypeName = "Microsoft.Dynamics.Ax.Xpp.FormEventHandler";
                    enumTypeName = "Dynamics.AX.Application.FormEventType";
                    leadingArguments = new object[] { SourceTypeName };
                    break;
                case "formcontroleventhandler":
                    managerTypeName = "Microsoft.Dynamics.Ax.Xpp.FormControlEventManager";
                    delegateTypeName = "Microsoft.Dynamics.Ax.Xpp.FormControlEventHandler";
                    enumTypeName = "Dynamics.AX.Application.FormControlEventType";
                    leadingArguments = new object[] { SourceTypeName, ElementName };
                    break;
                case "formdatasourceeventhandler":
                    managerTypeName = "Microsoft.Dynamics.Ax.Xpp.FormDataSourceEventManager";
                    delegateTypeName = "Microsoft.Dynamics.Ax.Xpp.FormDataSourceEventHandler";
                    enumTypeName = "Dynamics.AX.Application.FormDataSourceEventType";
                    leadingArguments = new object[] { SourceTypeName, ElementName };
                    break;
                case "formdatafieldeventhandler":
                    managerTypeName = "Microsoft.Dynamics.Ax.Xpp.FormDataFieldEventManager";
                    delegateTypeName = "Microsoft.Dynamics.Ax.Xpp.FormDataFieldEventHandler";
                    enumTypeName = "Dynamics.AX.Application.FormDataFieldEventType";
                    leadingArguments = new object[] { SourceTypeName, ElementName, FieldName };
                    break;
                default:
                    throw new Exception($"Attributed event adapter {AttributeName} is not recognized.");
            }

            if (string.IsNullOrWhiteSpace(SourceTypeName) || string.IsNullOrWhiteSpace(EventName))
            {
                throw new Exception($"Attribute {AttributeName} on {Handler.DeclaringClass.Name}.{Handler.Name} does not identify a valid AOS event target.");
            }

            Type managerType = ResolveRuntimeType(managerTypeName);
            Type delegateType = ResolveRuntimeType(delegateTypeName);
            Type enumType = ResolveRuntimeType(enumTypeName);

            if (managerType is null || delegateType is null || enumType is null)
            {
                throw new Exception($"The AOS event runtime required by {AttributeName} is not loaded.");
            }

            Type[] parameterTypes = delegateType.GetMethod("Invoke").GetParameters()
                .Select(parameter => parameter.ParameterType).ToArray();

            if (Handler.Parameters.Count != parameterTypes.Length)
            {
                throw new Exception($"Attributed handler {Handler.DeclaringClass.Name}.{Handler.Name} takes {Handler.Parameters.Count} parameter(s), but {delegateType.Name} declares {parameterTypes.Length}.");
            }

            var executor = new DelegateEventExecutor(context, arguments =>
                ScriptClassRuntime.RunSynchronous(context, Handler, null, arguments));
            Delegate callback = Delegate.CreateDelegate(
                delegateType,
                executor,
                DelegateHelper.GetMethodToExecute(executor, parameterTypes));
            object eventValue = Enum.Parse(enumType, EventName, true);

            object[] subscribeArguments = new object[leadingArguments.Length + 2];
            Array.Copy(leadingArguments, subscribeArguments, leadingArguments.Length);
            subscribeArguments[leadingArguments.Length] = eventValue;
            subscribeArguments[leadingArguments.Length + 1] = callback;

            MethodInfo subscribe = managerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => string.Equals(method.Name, "Subscribe", StringComparison.Ordinal))
                .FirstOrDefault(method => method.GetParameters().Length == subscribeArguments.Length);

            if (subscribe is null)
            {
                throw new Exception($"{managerTypeName}.Subscribe with {subscribeArguments.Length} parameters was not found.");
            }

            subscribe.Invoke(null, subscribeArguments);
        }

        private static Type ResolveRuntimeType(string name)
        {
            return Core.ClrTypeResolver.FindType(name) ?? Type.GetType(name, false, true);
        }

        private static string ArgumentAfter(XppAttributeDeclaration attribute, string functionName)
        {
            int index = attribute.Words.FindIndex(word => string.Equals(word, functionName, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < attribute.Words.Count ? attribute.Words[index + 1] : null;
        }

        private static string SecondArgumentAfter(XppAttributeDeclaration attribute, string functionName)
        {
            int index = attribute.Words.FindIndex(word => string.Equals(word, functionName, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 2 < attribute.Words.Count ? attribute.Words[index + 2] : null;
        }

        private static string ThirdArgumentAfter(XppAttributeDeclaration attribute, string functionName)
        {
            int index = attribute.Words.FindIndex(word => string.Equals(word, functionName, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 3 < attribute.Words.Count ? attribute.Words[index + 3] : null;
        }

        private static string LastArgument(XppAttributeDeclaration attribute)
        {
            return attribute.Words.Count > 1 ? attribute.Words[attribute.Words.Count - 1] : null;
        }

        private static string NormalizeAttributeName(string attributeName)
        {
            if (attributeName != null && attributeName.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase))
            {
                return attributeName.Substring(0, attributeName.Length - "Attribute".Length);
            }

            return attributeName ?? string.Empty;
        }
    }
}

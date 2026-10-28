using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using XppInterpreter.Core;
using XppInterpreter.Parser.Metadata;

namespace XppInterpreter.Parser
{
    internal static class EventHandlerHelper
    {
        internal static void ValidateEventHandler(XppParser parser, ParseContext context, Type delegateType, EventHandler eventHandler)
        {
            ValidateSubscription(parser, context, null, null, eventHandler, delegateType);
        }

        /// <summary>
        /// Structural validation of a += / -= subscription: the handler's parameter
        /// count must match the delegate's, for script and CLR delegates alike;
        /// CLR delegates with local-function handlers are additionally type-checked
        /// </summary>
        internal static void ValidateSubscription(XppParser parser, ParseContext context, XppTypeInferer typeInferer, Variable assignee, EventHandler eventHandler, Type assigneeClrType)
        {
            int? expectedCount = null;
            System.Reflection.ParameterInfo[] clrDelegateParameters = null;

            // Script-class delegate signature
            if (assignee?.Caller != null && typeInferer != null &&
                typeInferer.InferType(assignee.Caller, context) is Core.ScriptClassType scriptClassType &&
                ((ClassDeclarationInfo)scriptClassType.DeclarationInfo).FindDelegate(assignee.Name) is ClassDelegateSignature scriptDelegate)
            {
                expectedCount = scriptDelegate.Parameters.Count;
            }
            else if (assigneeClrType != null && ReflectionHelper.IsDelegateType(assigneeClrType))
            {
                clrDelegateParameters = assigneeClrType.GetMethod("Invoke").GetParameters();
                expectedCount = clrDelegateParameters.Length;
            }

            if (expectedCount is null)
            {
                return;
            }

            // Handler signature
            int? handlerCount = null;
            FunctionDeclaration localFunction = null;

            switch (eventHandler.Kind)
            {
                case EventHandlerKind.LocalFunction:
                    localFunction = context.CurrentScope.FunctionDeclarations.FirstOrDefault(
                        f => string.Equals(f.Name, eventHandler.FunctionName, StringComparison.InvariantCultureIgnoreCase));
                    handlerCount = localFunction?.Parameters.Count;
                    break;

                case EventHandlerKind.StaticMethod:
                    handlerCount = context.DeclaredClasses.Find(eventHandler.CallerName)
                        ?.FindMethod(eventHandler.FunctionName)?.Parameters.Count;
                    break;

                case EventHandlerKind.InstanceMethod:
                    var handlerVariable = context.CurrentScope.FindVariableDeclaration(eventHandler.CallerName);

                    if (handlerVariable?.ClrType is Core.ScriptClassType handlerClassType)
                    {
                        handlerCount = ((ClassDeclarationInfo)handlerClassType.DeclarationInfo)
                            .FindMethod(eventHandler.FunctionName)?.Parameters.Count;
                    }

                    break;
            }

            if (handlerCount is null)
            {
                return;
            }

            if (handlerCount != expectedCount)
            {
                parser.HandleParseError($"Handler {eventHandler.FunctionName} takes {handlerCount} parameter(s) but the delegate declares {expectedCount}.", stop: false);
                return;
            }

            // CLR delegates with local-function handlers are type-checked as well
            if (clrDelegateParameters != null && localFunction != null)
            {
                for (int i = 0; i < localFunction.Parameters.Count; i++)
                {
                    if (localFunction.Parameters[i].DeclarationClrType?.FullName != clrDelegateParameters[i].ParameterType.FullName)
                    {
                        parser.HandleParseError("Parameter types does not match with the delegate parameter types.", stop: false);
                    }
                }
            }
        }

        internal static bool IsEventHandler(Expression expression, ParseContext context, XppTypeInferer typeInferer)
        {
            if (!(expression is Variable variable) || expression is FunctionCall)
            {
                return false;
            }

            // Delegate declared in a script class (obj.myDelegate += eventhandler(...))
            if (variable.Caller != null)
            {
                var callerType = typeInferer.InferType(variable.Caller, context);

                if (callerType is Core.ScriptClassType scriptClassType &&
                    ((ClassDeclarationInfo)scriptClassType.DeclarationInfo).FindDelegate(variable.Name) != null)
                {
                    return true;
                }
            }

            return ReflectionHelper.IsDelegateType(typeInferer.InferType(expression, context));
        }
    }
}

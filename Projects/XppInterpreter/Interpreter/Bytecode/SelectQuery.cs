using XppInterpreter.Interpreter.Query;

namespace XppInterpreter.Interpreter.Bytecode
{
    class Select : IInstruction
    {
        public string OperationCode => "SELECT_QUERY";
        public Parser.Data.Query Query { get; }
        public bool IsWhileSelect { get; }
        public string ReturnField { get; }
        public Select(Parser.Data.Query query, bool isWhileSelect = false, string returnField = "")
        {
            Query = query;
            IsWhileSelect = isWhileSelect;
            ReturnField = returnField;
        }

        public void Execute(RuntimeContext context)
        {
            ISearchInstance searchInstance = null;
            bool found = false;

            if (!string.IsNullOrEmpty(ReturnField))
            {
                // A select expression operates on its declared table buffer. If
                // the syntax names the table type directly, fall back to creating
                // that buffer by metadata name.
                object buffer;
                try
                {
                    buffer = context.ScopeHandler.GetVar(Query.TableVariableName);
                }
                catch
                {
                    buffer = context.Models.IsRuntimeTable(Query.TableVariableName)
                        ? context.Models.Create(Query.TableVariableName)
                        : context.Proxy.Casting.GetDefaultValueForType(Query.TableVariableName);
                }

                if (buffer is null)
                {
                    throw new System.Exception($"Table buffer {Query.TableVariableName} could not be resolved for the select expression.");
                }

                searchInstance = QueryGeneratorRouter.ForQuery(context, Query, buffer).NewSearchInstance(Query, context, true, buffer);
                bool expressionFound = searchInstance.Next();

                object returnValue;
                if (buffer is ScriptObject runtimeBuffer)
                {
                    RuntimeClassField field = runtimeBuffer.Class.FindField(ReturnField);
                    returnValue = expressionFound
                        ? context.Models.GetField(runtimeBuffer, ReturnField)
                        : context.Proxy.Casting.GetDefaultValueForSystemType(field?.ClrType);
                }
                else
                {
                    returnValue = expressionFound
                        ? context.Proxy.Reflection.GetInstanceProperty(buffer, ReturnField)
                        : context.Proxy.Casting.GetDefaultValueForSystemType(
                            context.Proxy.Reflection.GetFieldReturnType(buffer.GetType(), ReturnField));
                }
                context.Stack.Push(returnValue);
            }
            else if (IsWhileSelect && context.Queries.TryGetValue(Query, out searchInstance))
            {
                found = searchInstance.Next();
            }
            else
            {
                searchInstance = QueryGeneratorRouter.ForQuery(context, Query).NewSearchInstance(Query, context, false, null);
                found = searchInstance.Next();
            }

            if (IsWhileSelect)
            {
                if (!found)
                {
                    context.Queries.Remove(Query);
                }
                else
                {
                    context.Queries[Query] = searchInstance;
                }

                context.Stack.Push(found);
            }
        }
    }
}

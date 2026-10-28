using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Linq;
using XppInterpreter.Interpreter.Debug;
using XppInterpreter.Interpreter.Proxy;

namespace XppInterpreter.Core
{
    public class Scope
    {
        internal readonly VariableCollection VariableCollection = new VariableCollection();
        internal readonly List<object> disposables = new List<object>();

        public readonly Stack<object> Stack = new Stack<object>();
        public Scope Parent { get; set; }
        public Scope SecondaryParent { get; set; }

        public Scope Begin()
        {
            return new Scope() { Parent = this };
        }

        public bool IsGlobal()
        {
            return Parent == null;
        }

        public Scope End()
        {
            foreach (var obj in disposables)
            {
                IDisposable disposable = obj as IDisposable;

                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }

            return Parent;
        }

        public object GetVar(string varName)
        {
            if (FindScopeContaining(varName, out VariableEntry entry) != null)
            {
                return entry.Value;
            }

            throw new Exception($"Variable {varName} was not declared.");
        }

        private Scope FindScopeContaining(string varName, out VariableEntry entry)
        {
            // Walk the parent chain iteratively; only branch into secondary parents when they exist
            for (Scope scope = this; scope != null; scope = scope.Parent)
            {
                if (scope.VariableCollection.TryGet(varName, out entry))
                {
                    return scope;
                }

                if (scope.SecondaryParent != null)
                {
                    Scope found = scope.Parent?.FindScopeContaining(varName, out entry);

                    return found ?? scope.SecondaryParent.FindScopeContaining(varName, out entry);
                }
            }

            entry = null;
            return null;
        }

        // Primitive type keywords are reserved words, so they can never name a string EDT
        private static readonly HashSet<string> _primitiveTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "int", "int64", "real", "boolean", "str", "date", "utcdatetime", "timeofday", "guid", "container", "anytype", "var", "void"
        };

        internal static bool IsStringEdtTypeName(string typeName, Interpreter.Proxy.IXppCastingProxy castingProxy)
        {
            return castingProxy != null &&
                !string.IsNullOrEmpty(typeName) &&
                !_primitiveTypeNames.Contains(typeName) &&
                castingProxy.IsStringEDT(typeName);
        }

        public void SetVar(string varName, object varValue, Interpreter.Proxy.IXppCastingProxy castingProxy, bool forceOnTop = false, Type declarationType = null, string typeName = null)
        {
            VariableEntry existing = null;
            Scope current = forceOnTop ? this : FindScopeContaining(varName, out existing);

            if (forceOnTop)
            {
                VariableCollection.TryGet(varName, out existing);
            }

            if (current != null)
            {
                if (existing is null)
                {
                    if (declarationType != null)
                    {
                        object value;

                        if (varValue != null && varValue.GetType() != declarationType &&
                            !(declarationType is ScriptClassType) && !declarationType.IsInstanceOfType(varValue) &&
                            !IsCompatibleScriptProxy(varValue, declarationType))
                        {
                            if (declarationType.IsEnum)
                            {
                                value = Enum.ToObject(declarationType, varValue);
                            }
                            else
                            {
                                value = castingProxy.Cast(varValue, declarationType);
                            }
                        }
                        else
                        {
                            value = varValue;
                        }

                        if (IsStringEdtTypeName(typeName, castingProxy))
                        {
                            value = castingProxy.TruncateEDTString(value as string, typeName);
                        }

                        current.VariableCollection.Add(new VariableEntry(varName, declarationType, value, typeName));
                    }
                    else
                    {
                        throw new Exception($"Variable {varName} is not declared.");
                    }
                }
                else
                {
                    var entry = existing;
                    var entryTypeName = entry.TypeName;

                    object value = varValue;

                    if (value != null && entry.DeclarationType != null &&
                        !(entry.DeclarationType is ScriptClassType) &&
                        !entry.DeclarationType.IsInstanceOfType(value) &&
                        !IsCompatibleScriptProxy(value, entry.DeclarationType))
                    {
                        if (entry.DeclarationType.IsEnum)
                        {
                            value = Enum.ToObject(entry.DeclarationType, value);
                        }
                        else if (castingProxy != null)
                        {
                            value = castingProxy.Cast(value, entry.DeclarationType);
                        }
                    }

                    if (castingProxy != null && entry.IsStringEdt(castingProxy))
                    {
                        value = castingProxy.TruncateEDTString(value as string, entryTypeName);
                    }

                    entry.SetValue(value);
                }

                return;
            }

            throw new Exception($"Variable {varName} is not declared.");
        }

        private static bool IsCompatibleScriptProxy(object value, Type declarationType)
        {
            var scriptObject = value as Interpreter.Bytecode.ScriptObject;
            return scriptObject?.BaseInstance != null &&
                declarationType.IsInstanceOfType(scriptObject.BaseInstance);
        }

        public NormalizedScopeEntry[] GetNormalizedScopeEntries(XppProxy proxy = null)
        {
            List<NormalizedScopeEntry> ret = new List<NormalizedScopeEntry>();

            foreach (VariableEntry variable in VariableCollection)
            {
                NormalizedScopeEntry entry = new NormalizedScopeEntry()
                {
                    VariableName = variable.Name,
                    Value = variable.Value,
                    TypeName = variable.DeclarationType is null ? variable.Value?.GetType().FullName ?? "" : variable.DeclarationType.FullName,
                };

                if (proxy != null && proxy.Reflection.IsEnum(entry.TypeName))
                {
                    entry.EnumValues = proxy.Reflection.GetAllEnumValues(entry.TypeName).Cast<string>().ToArray();
                }

                ret.Add(entry);
            }

            return ret.ToArray();
        }
    }
}

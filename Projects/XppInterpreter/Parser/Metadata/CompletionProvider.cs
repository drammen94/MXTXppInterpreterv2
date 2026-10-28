using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using XppInterpreter.Interpreter.Proxy;
using XppInterpreter.Lexer;

namespace XppInterpreter.Parser.Metadata
{
    public class CompletionProvider
    {
        private readonly XppProxy _proxy;

        public CompletionProvider(XppProxy proxy)
        {
            _proxy = proxy;
        }

        public CompletionCollection GetCompletions(ICompletionProvider nativeProvider, string sourceCode, int row, int column, AutoCompletionPurpose purpose)
        {
            XppLexer lexer = new XppLexer(sourceCode);
            XppParser parser = new XppParser(lexer, _proxy);

            System.Type inferedType = parser.ParseForAutoCompletion(row, column, purpose);

            if (inferedType is null) return new CompletionCollection();

            CompletionCollection completions = new CompletionCollection();

            if (purpose == AutoCompletionPurpose.InstanceMembers ||
                purpose == AutoCompletionPurpose.StaticMembers)
            {
                bool @static = purpose == AutoCompletionPurpose.StaticMembers;

                // Script-declared classes and interfaces complete from the class table
                if (inferedType is Core.ScriptClassType scriptClassType)
                {
                    var info = (ClassDeclarationInfo)scriptClassType.DeclarationInfo;

                    for (var current = info; current != null; current = current.BaseClass)
                    {
                        foreach (var method in current.Methods.Where(m => m.IsStatic == @static && !string.Equals(m.Name, "new", StringComparison.OrdinalIgnoreCase)))
                        {
                            completions.Add(new Completion(method.Name, method.Name, @static ? CompletionEntryType.StaticMethod : CompletionEntryType.Method));
                        }

                        foreach (var field in current.Fields.Where(f => f.IsStatic == @static))
                        {
                            completions.Add(new Completion(field.Name, field.Name, CompletionEntryType.ClassProperty));
                        }

                        if (!@static)
                        {
                            foreach (var declaredDelegate in current.Delegates)
                            {
                                completions.Add(new Completion(declaredDelegate.Name, declaredDelegate.Name, CompletionEntryType.Method));
                            }
                        }
                    }

                    return completions;
                }

                // .NET types unknown to X++ metadata complete through CLR reflection
                bool clrOnly;

                try
                {
                    clrOnly = _proxy.Casting.GetSystemTypeFromTypeName(inferedType.Name) != inferedType;
                }
                catch
                {
                    clrOnly = true;
                }

                if (clrOnly && !inferedType.IsPrimitive && inferedType != typeof(string))
                {
                    if (@static && inferedType.IsEnum)
                    {
                        foreach (string enumValue in Enum.GetNames(inferedType))
                        {
                            completions.Add(new Completion(enumValue, enumValue, CompletionEntryType.EnumValue));
                        }
                    }
                    else
                    {
                        foreach (var method in GetMethods(inferedType, @static))
                        {
                            completions.Add(new Completion(method.Name, method.Name, @static ? CompletionEntryType.StaticMethod : CompletionEntryType.Method));
                        }

                        foreach (var property in GetProperties(inferedType, @static))
                        {
                            completions.Add(new Completion(property.Name, property.Name, CompletionEntryType.ClassProperty));
                        }

                        foreach (var field in GetFields(inferedType, @static))
                        {
                            completions.Add(new Completion(field.Name, field.Name, CompletionEntryType.ClassProperty));
                        }
                    }

                    return completions;
                }

                if (_proxy.Reflection.IsCommonType(inferedType))
                {
                    completions.Union(nativeProvider.GetTableMethodCompletions(inferedType.Name, @static));

                    if (!@static)
                    {
                        completions.Union(nativeProvider.GetTableFieldsCompletions(inferedType.Name));
                    }
                }
                else
                {
                    if (@static && _proxy.Reflection.IsEnum(inferedType.Name))
                    {
                        completions.Union(nativeProvider.GetEnumCompletions(inferedType.Name));
                    }
                    else
                    {
                        completions.Union(nativeProvider.GetClassMethodCompletions(inferedType.Name, @static));
                        completions.Union(nativeProvider.GetClassFieldCompletions(inferedType.Name, @static));
                    }
                }
            }
            else if (purpose == AutoCompletionPurpose.TableIndexes)
            {
                if (_proxy.Reflection.IsCommonType(inferedType))
                { 
                    completions.Union(nativeProvider.GetIndexCompletions(inferedType.Name));
                }
            }

            return completions;
        }

        private IEnumerable<System.Reflection.MethodInfo> GetMethods(System.Type type, bool @static)
        {
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | (@static ? System.Reflection.BindingFlags.Static : System.Reflection.BindingFlags.Instance);

            return type.GetMethods(flags)
                .Where(m => 
                !m.IsSpecialName 
                && (!m.IsHideBySig || (m.Attributes & System.Reflection.MethodAttributes.VtableLayoutMask) == System.Reflection.MethodAttributes.NewSlot)
                && !m.Name.StartsWith("_") 
                && !m.Name.StartsWith("`") 
                && !m.Name.StartsWith("$"));
        }

        private IEnumerable<System.Reflection.FieldInfo> GetFields(System.Type type, bool @static)
        {
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | (@static ? System.Reflection.BindingFlags.Static : System.Reflection.BindingFlags.Instance);

            return type.GetFields(flags).Where(m => !m.IsSpecialName && !m.Name.StartsWith("_") && !m.Name.StartsWith("`") && !m.Name.StartsWith("$"));
        }

        private IEnumerable<System.Reflection.PropertyInfo> GetProperties(System.Type type, bool @static)
        {
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | (@static ? System.Reflection.BindingFlags.Static : System.Reflection.BindingFlags.Instance);

            return type.GetProperties(flags).Where(m => !m.IsSpecialName && !m.Name.StartsWith("_") && !m.Name.StartsWith("`") && !m.Name.StartsWith("$"));
        }
    }
}

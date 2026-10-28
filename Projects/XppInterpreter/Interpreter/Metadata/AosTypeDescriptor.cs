using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using XppInterpreter.Interpreter.Proxy;

namespace XppInterpreter.Interpreter.Metadata
{
    public enum AosTypeKind
    {
        Unknown,
        Class,
        Table,
        Enum,
        Form,
        DataEntity,
        Report,
        Service,
    }

    /// <summary>
    /// Unified description of a compiled application type. In D365FO the proxy
    /// obtains X++ signatures from SysDict; ordinary CLR reflection is the fallback.
    /// </summary>
    public sealed class AosTypeDescriptor
    {
        private readonly AosTypeDescriptorProvider _provider;

        internal AosTypeDescriptor(AosTypeDescriptorProvider provider, string name, Type clrType, AosTypeKind kind)
        {
            _provider = provider;
            Name = name;
            ClrType = clrType;
            Kind = kind;
        }

        public string Name { get; }
        public Type ClrType { get; }
        public AosTypeKind Kind { get; }
        public bool IsTable => Kind == AosTypeKind.Table;
        public AosMethodDescriptor FindMethod(string name) => _provider.DescribeMethod(this, name);
        public AosFieldDescriptor FindField(string name) => _provider.DescribeField(this, name);
    }

    public sealed class AosFieldDescriptor
    {
        public string Name { get; internal set; }
        public Type ClrType { get; internal set; }
        public bool IsStatic { get; internal set; }
        public bool IsPublic { get; internal set; }
    }

    public sealed class AosMethodParameterDescriptor
    {
        public string Name { get; internal set; }
        public string TypeName { get; internal set; }
        public Type ClrType { get; internal set; }
        public bool IsOptional { get; internal set; }
        public bool IsByRef { get; internal set; }
        public bool IsArray { get; internal set; }
    }

    public sealed class AosMethodDescriptor
    {
        public string Name { get; internal set; }
        public string ReturnTypeName { get; internal set; }
        public Type ReturnClrType { get; internal set; }
        public bool IsStatic { get; internal set; }
        public bool IsAbstract { get; internal set; }
        public bool IsFinal { get; internal set; }
        public bool IsHookable { get; internal set; } = true;
        public bool IsWrappable { get; internal set; } = true;
        public bool IsReplaceable { get; internal set; }
        public Parser.AccessModifier Access { get; internal set; } = Parser.AccessModifier.Public;
        public IReadOnlyList<AosMethodParameterDescriptor> Parameters { get; internal set; } =
            new List<AosMethodParameterDescriptor>();
        public int RequiredParameterCount => Parameters.Count(parameter => !parameter.IsOptional);
    }

    public sealed class AosTypeDescriptorProvider
    {
        private readonly XppProxy _proxy;
        private readonly Dictionary<string, AosTypeDescriptor> _types =
            new Dictionary<string, AosTypeDescriptor>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AosMethodDescriptor> _methods =
            new Dictionary<string, AosMethodDescriptor>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AosFieldDescriptor> _fields =
            new Dictionary<string, AosFieldDescriptor>(StringComparer.OrdinalIgnoreCase);

        public AosTypeDescriptorProvider(XppProxy proxy)
        {
            _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        }

        public AosTypeDescriptor DescribeType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            if (_types.TryGetValue(typeName, out AosTypeDescriptor cached)) return cached;

            Type clrType = null;
            try { clrType = _proxy.Casting.GetSystemTypeFromTypeName(typeName); }
            catch { }

            AosTypeKind kind = AosTypeKind.Unknown;
            try
            {
                if (Enum.TryParse(_proxy.Reflection.GetApplicationTypeKind(typeName), true, out AosTypeKind metadataKind))
                {
                    kind = metadataKind;
                }
            }
            catch
            {
            }

            try
            {
                if (clrType != null && _proxy.Reflection.IsCommonType(clrType)) kind = AosTypeKind.Table;
                else if ((clrType?.IsEnum ?? false) || _proxy.Reflection.IsEnum(typeName)) kind = AosTypeKind.Enum;
                else if (clrType != null && kind == AosTypeKind.Unknown) kind = AosTypeKind.Class;
            }
            catch
            {
                if (clrType?.IsEnum ?? false) kind = AosTypeKind.Enum;
            }

            if (clrType is null && kind == AosTypeKind.Unknown) return null;

            var descriptor = new AosTypeDescriptor(this, typeName, clrType, kind);
            _types[typeName] = descriptor;
            return descriptor;
        }

        internal AosMethodDescriptor DescribeMethod(AosTypeDescriptor owner, string methodName)
        {
            if (owner is null || string.IsNullOrWhiteSpace(methodName)) return null;
            string key = owner.Name + "::" + methodName;
            if (_methods.TryGetValue(key, out AosMethodDescriptor cached)) return cached;

            bool exists = false;
            try { exists = _proxy.Reflection.TypeHasMethod(owner.ClrType, methodName); }
            catch { }

            MethodInfo clrMethod = FindClrMethod(owner.ClrType, methodName);
            string syntax = null;
            try { syntax = _proxy.Reflection.GetMethodSyntax(owner.Name, methodName); }
            catch { }

            AosMethodDescriptor descriptor = !string.IsNullOrWhiteSpace(syntax)
                ? ParseMetadataSyntax(owner, methodName, syntax)
                : FromClrMethod(clrMethod);

            if (descriptor is null && exists)
            {
                descriptor = new AosMethodDescriptor
                {
                    Name = methodName,
                    ReturnTypeName = "anytype",
                    ReturnClrType = TryGetReturnType(owner.ClrType, methodName),
                };
            }

            if (descriptor is null) return null;
            ApplyClrExtensibility(descriptor, clrMethod);
            _methods[key] = descriptor;
            return descriptor;
        }

        internal AosFieldDescriptor DescribeField(AosTypeDescriptor owner, string fieldName)
        {
            if (owner is null || string.IsNullOrWhiteSpace(fieldName)) return null;
            string key = owner.Name + "::" + fieldName;
            if (_fields.TryGetValue(key, out AosFieldDescriptor cached)) return cached;

            bool exists = false;
            try { exists = _proxy.Reflection.TypeHasProperty(owner.ClrType, fieldName, true); }
            catch { }
            if (!exists) return null;

            Type fieldType = null;
            try { fieldType = _proxy.Reflection.GetFieldReturnType(owner.ClrType, fieldName, true); }
            catch { }

            FieldInfo field = owner.ClrType?.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, fieldName, StringComparison.OrdinalIgnoreCase));
            PropertyInfo property = owner.ClrType?.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, fieldName, StringComparison.OrdinalIgnoreCase));

            var descriptor = new AosFieldDescriptor
            {
                Name = fieldName,
                ClrType = fieldType ?? field?.FieldType ?? property?.PropertyType,
                IsStatic = field?.IsStatic ?? ((property?.GetGetMethod(true) ?? property?.GetSetMethod(true))?.IsStatic ?? false),
                IsPublic = field?.IsPublic ?? ((property?.GetGetMethod(true) ?? property?.GetSetMethod(true))?.IsPublic ?? true),
            };

            _fields[key] = descriptor;
            return descriptor;
        }

        private AosMethodDescriptor ParseMetadataSyntax(AosTypeDescriptor owner, string methodName, string syntax)
        {
            int open = syntax.IndexOf('(');
            int close = syntax.LastIndexOf(')');
            if (open < 0 || close < open) return null;

            string header = syntax.Substring(0, open).Trim();
            string parameterList = syntax.Substring(open + 1, close - open - 1);
            int methodIndex = header.LastIndexOf(methodName, StringComparison.OrdinalIgnoreCase);
            if (methodIndex < 0) return null;

            var words = header.Substring(0, methodIndex).Trim()
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var descriptor = new AosMethodDescriptor
            {
                Name = methodName,
                Access = ParseAccess(words),
                IsStatic = Contains(words, "static"),
                IsAbstract = Contains(words, "abstract"),
                ReturnTypeName = words.LastOrDefault(word => !IsMethodModifier(word)) ?? "void",
            };
            descriptor.ReturnClrType = TryGetReturnType(owner.ClrType, methodName) ?? ResolveType(descriptor.ReturnTypeName);

            var parameters = new List<AosMethodParameterDescriptor>();
            foreach (string rawParameter in SplitParameters(parameterList))
            {
                string text = rawParameter.Trim();
                if (text.Length == 0) continue;

                bool optional = text[0] == '[' && text[text.Length - 1] == ']';
                if (optional) text = text.Substring(1, text.Length - 2).Trim();
                int defaultValue = FindTopLevelAssignment(text);
                if (defaultValue >= 0)
                {
                    optional = true;
                    text = text.Substring(0, defaultValue).Trim();
                }
                bool byRef = text.StartsWith("byref ", StringComparison.OrdinalIgnoreCase);
                if (byRef) text = text.Substring(6).Trim();

                int lastSpace = text.LastIndexOf(' ');
                string typeName = lastSpace > 0 ? text.Substring(0, lastSpace).Trim() : "anytype";
                string name = lastSpace > 0 ? text.Substring(lastSpace + 1).Trim() : text;
                bool isArray = typeName.EndsWith("[]", StringComparison.Ordinal) ||
                    name.EndsWith("[]", StringComparison.Ordinal);
                if (typeName.EndsWith("[]", StringComparison.Ordinal)) typeName = typeName.Substring(0, typeName.Length - 2);
                if (name.EndsWith("[]", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 2);

                parameters.Add(new AosMethodParameterDescriptor
                {
                    Name = name,
                    TypeName = typeName,
                    ClrType = ResolveType(typeName),
                    IsOptional = optional,
                    IsByRef = byRef,
                    IsArray = isArray,
                });
            }
            descriptor.Parameters = parameters;
            return descriptor;
        }

        private AosMethodDescriptor FromClrMethod(MethodInfo method)
        {
            if (method is null) return null;
            return new AosMethodDescriptor
            {
                Name = method.Name,
                ReturnTypeName = ToXppTypeName(method.ReturnType),
                ReturnClrType = method.ReturnType,
                IsStatic = method.IsStatic,
                IsAbstract = method.IsAbstract,
                IsFinal = method.IsFinal,
                Access = method.IsPublic ? Parser.AccessModifier.Public :
                    (method.IsFamily ? Parser.AccessModifier.Protected : Parser.AccessModifier.Private),
                Parameters = method.GetParameters().Select(parameter => new AosMethodParameterDescriptor
                {
                    Name = parameter.Name,
                    TypeName = ToXppTypeName(parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType),
                    ClrType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType,
                    IsOptional = parameter.IsOptional,
                    IsByRef = parameter.ParameterType.IsByRef,
                    IsArray = parameter.ParameterType.IsArray,
                }).ToList(),
            };
        }

        private void ApplyClrExtensibility(AosMethodDescriptor descriptor, MethodInfo method)
        {
            descriptor.IsWrappable = !descriptor.IsFinal &&
                (descriptor.Access == Parser.AccessModifier.Public || descriptor.Access == Parser.AccessModifier.Protected);
            if (method is null) return;

            foreach (object attribute in method.GetCustomAttributes(true))
            {
                string name = attribute.GetType().Name;
                bool? value = ReadBooleanAttributeValue(attribute);
                if (name.IndexOf("Hookable", StringComparison.OrdinalIgnoreCase) >= 0) descriptor.IsHookable = value ?? true;
                else if (name.IndexOf("Wrappable", StringComparison.OrdinalIgnoreCase) >= 0) descriptor.IsWrappable = value ?? true;
                else if (name.IndexOf("Replaceable", StringComparison.OrdinalIgnoreCase) >= 0) descriptor.IsReplaceable = value ?? true;
            }
        }

        private static bool? ReadBooleanAttributeValue(object attribute)
        {
            foreach (string propertyName in new[] { "Value", "Enabled", "IsEnabled" })
            {
                PropertyInfo property = attribute.GetType().GetProperty(propertyName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property?.PropertyType == typeof(bool)) return (bool)property.GetValue(attribute, null);
            }
            return null;
        }

        private Type TryGetReturnType(Type owner, string methodName)
        {
            try { return _proxy.Reflection.GetMethodReturnType(owner, methodName); }
            catch { return null; }
        }

        private Type ResolveType(string typeName)
        {
            try { return _proxy.Casting.GetSystemTypeFromTypeName(typeName); }
            catch { return null; }
        }

        private static MethodInfo FindClrMethod(Type type, string methodName)
        {
            return type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(method => string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase));
        }

        private static Parser.AccessModifier ParseAccess(IEnumerable<string> words)
        {
            if (Contains(words, "private")) return Parser.AccessModifier.Private;
            if (Contains(words, "protected")) return Parser.AccessModifier.Protected;
            if (Contains(words, "internal")) return Parser.AccessModifier.Internal;
            return Parser.AccessModifier.Public;
        }

        private static bool Contains(IEnumerable<string> words, string value) =>
            words.Any(word => string.Equals(word, value, StringComparison.OrdinalIgnoreCase));

        private static bool IsMethodModifier(string word) =>
            new[] { "public", "protected", "private", "internal", "static", "abstract", "final", "display", "edit", "client", "server" }
                .Any(modifier => string.Equals(modifier, word, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<string> SplitParameters(string value)
        {
            int squareDepth = 0, genericDepth = 0, parenthesisDepth = 0, start = 0;
            char quote = '\0';
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (quote != '\0')
                {
                    if (current == quote && (index == 0 || value[index - 1] != '\\')) quote = '\0';
                    continue;
                }

                if (current == '\'' || current == '"') quote = current;
                else if (current == '[') squareDepth++;
                else if (current == ']') squareDepth--;
                else if (current == '<') genericDepth++;
                else if (current == '>') genericDepth--;
                else if (current == '(') parenthesisDepth++;
                else if (current == ')') parenthesisDepth--;
                else if (current == ',' && squareDepth == 0 && genericDepth == 0 && parenthesisDepth == 0)
                {
                    yield return value.Substring(start, index - start);
                    start = index + 1;
                }
            }
            yield return value.Substring(start);
        }

        private static int FindTopLevelAssignment(string value)
        {
            int squareDepth = 0, parenthesisDepth = 0, genericDepth = 0;
            char quote = '\0';
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (quote != '\0')
                {
                    if (current == quote && (index == 0 || value[index - 1] != '\\')) quote = '\0';
                    continue;
                }

                if (current == '\'' || current == '"') quote = current;
                else if (current == '[') squareDepth++;
                else if (current == ']') squareDepth--;
                else if (current == '(') parenthesisDepth++;
                else if (current == ')') parenthesisDepth--;
                else if (current == '<') genericDepth++;
                else if (current == '>') genericDepth--;
                else if (current == '=' && squareDepth == 0 && parenthesisDepth == 0 && genericDepth == 0) return index;
            }
            return -1;
        }

        private static string ToXppTypeName(Type type)
        {
            if (type is null) return "anytype";
            if (type == typeof(void)) return "void";
            if (type == typeof(string)) return "str";
            if (type == typeof(bool)) return "boolean";
            if (type == typeof(int)) return "int";
            if (type == typeof(long)) return "int64";
            if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)) return "real";
            if (type == typeof(Guid)) return "guid";
            if (type == typeof(object)) return "anytype";
            return type.Name;
        }
    }
}

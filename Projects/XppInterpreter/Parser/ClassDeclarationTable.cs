using System;
using System.Collections.Generic;
using System.Linq;
using XppInterpreter.Lexer;

namespace XppInterpreter.Parser
{
    public enum AccessModifier
    {
        Public,
        Protected,
        Private,
        Internal,
    }

    /// <summary>
    /// Parse-time signature of a script-declared class method
    /// </summary>
    public class ClassMethodSignature
    {
        public string Name;
        public Word ReturnType;
        public System.Type ReturnClrType;
        public bool IsStatic;
        public bool IsAbstract;
        public bool IsFinal;
        public bool? IsHookable;
        public bool? IsWrappable;
        public bool? IsReplaceable;
        public AccessModifier Access = AccessModifier.Public;
        public List<FunctionDeclarationParameter> Parameters = new List<FunctionDeclarationParameter>();
        public List<XppAttributeDeclaration> Attributes = new List<XppAttributeDeclaration>();
        public int RequiredParameterCount;
    }

    /// <summary>
    /// Parse-time signature of a script-declared class field
    /// </summary>
    public class ClassFieldSignature
    {
        public string Name;
        public Word Type;
        public System.Type ClrType;
        public bool IsStatic;
        public bool IsConst;
        public AccessModifier Access = AccessModifier.Protected;
        public List<XppAttributeDeclaration> Attributes = new List<XppAttributeDeclaration>();
    }

    /// <summary>
    /// Parse-time signature of a delegate declared in a script class
    /// </summary>
    public class ClassDelegateSignature
    {
        public string Name;
        public List<FunctionDeclarationParameter> Parameters = new List<FunctionDeclarationParameter>();
        public List<XppAttributeDeclaration> Attributes = new List<XppAttributeDeclaration>();
    }

    /// <summary>
    /// Parse-time symbol information for a script-declared class or interface
    /// </summary>
    public class ClassDeclarationInfo
    {
        public string Name;
        public string BaseClassName;
        public ClassDeclarationInfo BaseClass;
        public List<string> ImplementsNames = new List<string>();
        public bool IsAbstract;
        public bool IsFinal;
        public bool IsStatic;
        public bool IsInterface;
        public string ExtensionTargetName;
        public ClassDeclarationInfo ExtensionTarget;

        /// <summary>
        /// Set when the base class is a compiled X++ class (not script-declared).
        /// The runtime wraps an instance of it and forwards unknown members.
        /// </summary>
        public System.Type CompiledBaseType;

        /// <summary>
        /// Interfaces resolved from ImplementsNames that are script-declared
        /// </summary>
        public readonly List<ClassDeclarationInfo> ImplementedInterfaces = new List<ClassDeclarationInfo>();
        public readonly List<System.Type> CompiledInterfaces = new List<System.Type>();

        public readonly List<ClassMethodSignature> Methods = new List<ClassMethodSignature>();
        public readonly List<ClassFieldSignature> Fields = new List<ClassFieldSignature>();
        public readonly List<ClassDelegateSignature> Delegates = new List<ClassDelegateSignature>();
        public readonly List<XppAttributeDeclaration> Attributes = new List<XppAttributeDeclaration>();

        /// <summary>
        /// Synthetic CLR type used by the type-inference layer to represent this class
        /// </summary>
        public System.Type Type;

        /// <summary>
        /// Present on a synthetic declaration that represents a compiled AOS
        /// class/table targeted by an interpreted extension.
        /// </summary>
        public Interpreter.Metadata.AosTypeDescriptor CompiledTypeDescriptor;

        /// <summary>
        /// Nearest compiled base type up the script chain, or null
        /// </summary>
        public System.Type FindCompiledBase()
        {
            for (ClassDeclarationInfo current = this; current != null; current = current.BaseClass)
            {
                if (current.CompiledBaseType != null)
                {
                    return current.CompiledBaseType;
                }
            }

            return null;
        }

        /// <summary>
        /// True when instances of this script class are emitted/wrapped as a
        /// compiled base or interface assignable to the requested type.
        /// </summary>
        public bool IsAssignableToCompiledType(System.Type targetType)
        {
            if (targetType is null)
            {
                return false;
            }

            System.Type compiledBase = FindCompiledBase();
            if (compiledBase != null && targetType.IsAssignableFrom(compiledBase))
            {
                return true;
            }

            for (ClassDeclarationInfo current = this; current != null; current = current.BaseClass)
            {
                if (current.CompiledInterfaces.Any(compiledInterface =>
                    targetType.IsAssignableFrom(compiledInterface)))
                {
                    return true;
                }
            }

            return false;
        }

        public ClassMethodSignature FindMethod(string name, bool walkBaseChain = true)
        {
            ClassDeclarationInfo current = this;

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

        public ClassDeclarationInfo FindMethodOwner(string name, bool walkBaseChain = true)
        {
            ClassDeclarationInfo current = this;

            do
            {
                if (current.Methods.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return current;
                }

                current = current.BaseClass;
            } while (walkBaseChain && current != null);

            return null;
        }

        public ClassFieldSignature FindField(string name, bool walkBaseChain = true)
        {
            ClassDeclarationInfo current = this;

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

        public ClassDeclarationInfo FindFieldOwner(string name, bool walkBaseChain = true)
        {
            ClassDeclarationInfo current = this;

            do
            {
                if (current.Fields.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return current;
                }

                current = current.BaseClass;
            } while (walkBaseChain && current != null);

            return null;
        }

        public ClassDelegateSignature FindDelegate(string name, bool walkBaseChain = true)
        {
            ClassDeclarationInfo current = this;

            do
            {
                var declaredDelegate = current.Delegates.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

                if (declaredDelegate != null)
                {
                    return declaredDelegate;
                }

                current = current.BaseClass;
            } while (walkBaseChain && current != null);

            return null;
        }

        public bool IsOrExtends(ClassDeclarationInfo other)
        {
            ClassDeclarationInfo current = this;

            while (current != null)
            {
                if (ReferenceEquals(current, other))
                {
                    return true;
                }

                current = current.BaseClass;
            }

            if (other != null && other.IsInterface)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectInterfaceNames(names);
                return names.Contains(other.Name);
            }

            return false;
        }

        /// <summary>
        /// Collects every interface name this type satisfies: direct implements,
        /// base-class implements, and base interfaces of implemented interfaces
        /// </summary>
        public void CollectInterfaceNames(HashSet<string> names)
        {
            for (ClassDeclarationInfo current = this; current != null; current = current.BaseClass)
            {
                if (current.IsInterface && !ReferenceEquals(current, this))
                {
                    names.Add(current.Name);
                }

                foreach (string name in current.ImplementsNames)
                {
                    names.Add(name);
                }

                foreach (ClassDeclarationInfo implemented in current.ImplementedInterfaces)
                {
                    names.Add(implemented.Name);
                    implemented.CollectInterfaceNames(names);
                }
            }
        }
    }

    /// <summary>
    /// Case-insensitive symbol table for script-declared classes
    /// </summary>
    public class ClassDeclarationTable
    {
        private readonly Dictionary<string, ClassDeclarationInfo> _classes =
            new Dictionary<string, ClassDeclarationInfo>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<ClassDeclarationInfo> All => _classes.Values;

        public void Add(ClassDeclarationInfo info)
        {
            _classes[info.Name] = info;
        }

        public bool Contains(string name)
        {
            return name != null && _classes.ContainsKey(name);
        }

        public ClassDeclarationInfo Find(string name)
        {
            if (name is null) return null;
            return _classes.TryGetValue(name, out ClassDeclarationInfo info) ? info : null;
        }

        public ClassMethodSignature FindExtensionMethod(
            ClassDeclarationInfo target,
            string methodName,
            bool publicOnly = true)
        {
            for (ClassDeclarationInfo currentTarget = target; currentTarget != null; currentTarget = currentTarget.BaseClass)
            {
                foreach (ClassDeclarationInfo extension in _classes.Values.Where(candidate =>
                    ReferenceEquals(candidate.ExtensionTarget, currentTarget)))
                {
                    ClassMethodSignature method = extension.FindMethod(methodName, walkBaseChain: false);

                    if (method != null && (!publicOnly || method.Access == AccessModifier.Public))
                    {
                        return method;
                    }
                }
            }

            return null;
        }

        public ClassFieldSignature FindExtensionField(
            ClassDeclarationInfo target,
            string fieldName,
            bool publicOnly = true)
        {
            for (ClassDeclarationInfo currentTarget = target; currentTarget != null; currentTarget = currentTarget.BaseClass)
            {
                foreach (ClassDeclarationInfo extension in _classes.Values.Where(candidate =>
                    ReferenceEquals(candidate.ExtensionTarget, currentTarget)))
                {
                    ClassFieldSignature field = extension.FindField(fieldName, walkBaseChain: false);

                    if (field != null && (!publicOnly || field.Access == AccessModifier.Public))
                    {
                        return field;
                    }
                }
            }

            return null;
        }

        public ClassDeclarationInfo FindExtensionFieldOwner(ClassDeclarationInfo target, string fieldName)
        {
            for (ClassDeclarationInfo currentTarget = target; currentTarget != null; currentTarget = currentTarget.BaseClass)
            {
                ClassDeclarationInfo owner = _classes.Values.FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.ExtensionTarget, currentTarget) &&
                    candidate.Fields.Any(field => field.Access == AccessModifier.Public &&
                        string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase)));

                if (owner != null)
                {
                    return owner;
                }
            }

            return null;
        }

        public ClassMethodSignature FindCompiledTargetExtensionMethod(
            System.Type targetType,
            string methodName,
            bool isStatic)
        {
            if (targetType is null) return null;

            return _classes.Values
                .Where(candidate => candidate.ExtensionTarget?.CompiledTypeDescriptor?.ClrType != null &&
                    candidate.ExtensionTarget.CompiledTypeDescriptor.ClrType.IsAssignableFrom(targetType))
                .Select(candidate => candidate.FindMethod(methodName, walkBaseChain: false))
                .FirstOrDefault(method => method != null && method.IsStatic == isStatic &&
                    method.Access == AccessModifier.Public);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace XppInterpreter.Core
{
    /// <summary>
    /// Namespace imports and aliases declared with using directives in a script
    /// </summary>
    public class ClrNamespaceContext
    {
        public readonly List<string> Namespaces = new List<string>();
        public readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void AddNamespace(string ns)
        {
            if (!Namespaces.Contains(ns, StringComparer.OrdinalIgnoreCase))
            {
                Namespaces.Add(ns);
            }
        }

        public void AddAlias(string alias, string target)
        {
            Aliases[alias] = target;
        }
    }

    /// <summary>
    /// Resolves .NET type names (plain, dotted or aliased) against the loaded
    /// assemblies, honoring the script's using directives
    /// </summary>
    public class ClrTypeResolver
    {
        private static readonly Dictionary<string, Type> _cache = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();

        public ClrNamespaceContext NamespaceContext { get; }

        public ClrTypeResolver(ClrNamespaceContext namespaceContext = null)
        {
            NamespaceContext = namespaceContext ?? new ClrNamespaceContext();
        }

        /// <summary>
        /// Resolves a type name using aliases, the literal name, and every imported namespace
        /// </summary>
        public Type Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // Alias for the full name (using SB = System.Text.StringBuilder;)
            if (NamespaceContext.Aliases.TryGetValue(name, out string aliasTarget))
            {
                Type aliasType = FindType(aliasTarget);

                if (aliasType != null)
                {
                    return aliasType;
                }
            }

            // Alias for the first segment (using IO = System.IO; IO.StreamReader)
            int firstDot = name.IndexOf('.');
            if (firstDot > 0)
            {
                string firstSegment = name.Substring(0, firstDot);

                if (NamespaceContext.Aliases.TryGetValue(firstSegment, out string prefixTarget))
                {
                    Type prefixed = FindType(prefixTarget + name.Substring(firstDot));

                    if (prefixed != null)
                    {
                        return prefixed;
                    }
                }
            }

            Type direct = FindType(name);

            if (direct != null)
            {
                return direct;
            }

            foreach (string ns in NamespaceContext.Namespaces)
            {
                Type namespaced = FindType(ns + "." + name);

                if (namespaced != null)
                {
                    return namespaced;
                }
            }

            return null;
        }

        /// <summary>
        /// Case-insensitive type lookup by full name over all loaded assemblies
        /// </summary>
        public static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return null;
            }

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(fullName, out Type cached))
                {
                    return cached;
                }
            }

            Type found = System.Type.GetType(fullName, false, true);

            if (found is null)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic) continue;

                    try
                    {
                        found = assembly.GetType(fullName, false, true);
                    }
                    catch
                    {
                        // Reflection-only or broken assemblies are skipped
                    }

                    if (found != null)
                    {
                        break;
                    }
                }
            }

            if (found != null)
            {
                lock (_cacheLock)
                {
                    _cache[fullName] = found;
                }
            }

            return found;
        }
    }
}

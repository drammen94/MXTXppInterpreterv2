using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace XppInterpreter.Lexer
{
    /// <summary>
    /// Holds the macro table for the X++ preprocessor (#define, #localmacro, #if...).
    /// Shared between the main lexer and the sub-lexers used to expand macro bodies,
    /// so that nested definitions and expansions see a single, consistent state.
    /// </summary>
    public class MacroPreprocessor
    {
        public class MacroDefinition
        {
            public string Value;
            public bool IsLocalMacro;
        }

        private readonly Dictionary<string, MacroDefinition> _macros =
            new Dictionary<string, MacroDefinition>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Maximum nesting of macro expansions before a recursive definition is assumed
        /// </summary>
        internal const int MaxExpansionDepth = 100;

        /// <summary>
        /// Current expansion nesting, shared across the main lexer and all sub-lexers
        /// so that recursive macro definitions surface as a parse error instead of an
        /// uncatchable stack overflow
        /// </summary>
        internal int ExpansionDepth;

        /// <summary>
        /// Optional hook that returns the source text of an AOT macro library
        /// (a set of #define/#localmacro directives) given its name.
        /// </summary>
        public Func<string, string> MacroLibraryResolver { get; set; }

        private readonly Dictionary<string, string> _macroLibraryCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Configures #macrolib resolution against a D365FO PackagesLocalDirectory.
        /// Macro dictionaries are read from model AxMacroDictionary folders and
        /// cached for the lifetime of this preprocessor.
        /// </summary>
        public void UseFileSystemMacroLibraries(string packagesLocalDirectory)
        {
            if (string.IsNullOrWhiteSpace(packagesLocalDirectory))
            {
                throw new ArgumentException("A PackagesLocalDirectory path is required.", nameof(packagesLocalDirectory));
            }

            string root = Path.GetFullPath(packagesLocalDirectory);
            MacroLibraryResolver = name => ResolveFileSystemMacroLibrary(root, name);
        }

        private string ResolveFileSystemMacroLibrary(string packagesLocalDirectory, string libraryName)
        {
            if (string.IsNullOrWhiteSpace(libraryName) ||
                libraryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                libraryName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                libraryName.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                return null;
            }

            if (_macroLibraryCache.TryGetValue(libraryName, out string cached))
            {
                return cached;
            }

            if (!Directory.Exists(packagesLocalDirectory))
            {
                return null;
            }

            foreach (string packageDirectory in Directory.EnumerateDirectories(packagesLocalDirectory))
            {
                foreach (string modelContainer in new[]
                {
                    packageDirectory,
                    Path.Combine(packageDirectory, "XppMetadata")
                })
                {
                    if (!Directory.Exists(modelContainer))
                    {
                        continue;
                    }

                    foreach (string modelDirectory in Directory.EnumerateDirectories(modelContainer))
                    {
                        string candidate = Path.Combine(
                            modelDirectory,
                            "AxMacroDictionary",
                            libraryName + ".xml");

                        if (!File.Exists(candidate))
                        {
                            continue;
                        }

                        XDocument document = XDocument.Load(candidate, LoadOptions.PreserveWhitespace);
                        string source = document.Root?.Elements()
                            .FirstOrDefault(element => string.Equals(
                                element.Name.LocalName,
                                "Source",
                                StringComparison.OrdinalIgnoreCase))?.Value;

                        if (source != null)
                        {
                            _macroLibraryCache[libraryName] = source;
                            return source;
                        }
                    }
                }
            }

            return null;
        }

        public bool IsDefined(string name) => _macros.ContainsKey(name);

        public bool TryGetMacro(string name, out MacroDefinition macro) => _macros.TryGetValue(name, out macro);

        public string GetValue(string name) => _macros.TryGetValue(name, out MacroDefinition macro) ? macro.Value : null;

        public void Define(string name, string value)
        {
            // A redefinition always overrides, discarding any #localmacro with the same name
            _macros[name] = new MacroDefinition { Value = value ?? string.Empty, IsLocalMacro = false };
        }

        public void DefineLocalMacro(string name, string body)
        {
            _macros[name] = new MacroDefinition { Value = body ?? string.Empty, IsLocalMacro = true };
        }

        public void Undefine(string name)
        {
            if (_macros.TryGetValue(name, out MacroDefinition macro) && !macro.IsLocalMacro)
            {
                _macros.Remove(name);
            }
        }

        /// <summary>
        /// #defInc / #defDec: interprets the macro value as an int (anything that does
        /// not parse cleanly is coerced to 0 first) and increments/decrements it.
        /// </summary>
        public void IncrementValue(string name, int delta)
        {
            long current = 0;

            string value = GetValue(name);

            if (value != null && long.TryParse(value.Trim(), out long parsed))
            {
                current = parsed;
            }

            unchecked
            {
                current = (int)current + delta;
            }

            Define(name, current.ToString());
        }

        /// <summary>
        /// Substitutes %1..%n parameters in a #localmacro body. Omitted parameters
        /// expand to the empty string; extra arguments are ignored.
        /// </summary>
        public static string SubstituteParameters(string body, List<string> arguments)
        {
            if (string.IsNullOrEmpty(body))
            {
                return body;
            }

            var builder = new StringBuilder();

            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] == '%' && i + 1 < body.Length && char.IsDigit(body[i + 1]))
                {
                    int numberStart = i + 1;
                    int numberEnd = numberStart;

                    while (numberEnd < body.Length && char.IsDigit(body[numberEnd]))
                    {
                        numberEnd++;
                    }

                    if (!int.TryParse(body.Substring(numberStart, numberEnd - numberStart), out int parameterNumber))
                    {
                        // Absurdly long digit sequences are kept verbatim
                        builder.Append(body, i, numberEnd - i);
                        i = numberEnd - 1;
                        continue;
                    }

                    if (arguments != null && parameterNumber >= 1 && parameterNumber <= arguments.Count)
                    {
                        builder.Append(arguments[parameterNumber - 1]);
                    }

                    i = numberEnd - 1;
                }
                else
                {
                    builder.Append(body[i]);
                }
            }

            return builder.ToString();
        }
    }
}

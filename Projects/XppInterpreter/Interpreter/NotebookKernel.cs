using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using XppInterpreter.Core;
using XppInterpreter.Interpreter.Bytecode;
using XppInterpreter.Lexer;
using XppInterpreter.Parser;

namespace XppInterpreter.Interpreter
{
    /// <summary>
    /// Resident notebook sessions: each one keeps its variables, script classes, functions and using
    /// imports in the AOS process between cells, the way a Jupyter kernel does. Nothing is compiled
    /// to metadata; a cell is parsed against the session, turned into bytecode, and run in the
    /// session's global scope.
    /// </summary>
    public static class NotebookKernel
    {
        /// <summary>Sessions idle for longer than this are dropped when another session is opened.</summary>
        public static TimeSpan IdleTimeout = TimeSpan.FromMinutes(60);

        /// <summary>Most sessions kept per AOS process; the least recently used one is dropped beyond it.</summary>
        public static int MaxSessions = 64;

        private static readonly ConcurrentDictionary<string, NotebookSession> _sessions =
            new ConcurrentDictionary<string, NotebookSession>(StringComparer.OrdinalIgnoreCase);

        public static int Count => _sessions.Count;

        /// <summary>Returns the session with this id, creating it when it does not exist.</summary>
        public static NotebookSession Open(string sessionId, Proxy.XppProxy proxy)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("A notebook session id is required.", nameof(sessionId));
            }

            NotebookSession session = _sessions.GetOrAdd(sessionId, id => new NotebookSession(id, proxy));
            session.Touch();
            Evict(session);
            return session;
        }

        /// <summary>Drops a session and everything it holds. Returns false when it did not exist.</summary>
        public static bool Close(string sessionId)
        {
            return sessionId != null && _sessions.TryRemove(sessionId, out _);
        }

        private static void Evict(NotebookSession keep)
        {
            DateTime cutoff = DateTime.UtcNow - IdleTimeout;

            foreach (var pair in _sessions)
            {
                if (!ReferenceEquals(pair.Value, keep) && pair.Value.LastUsedUtc < cutoff)
                {
                    _sessions.TryRemove(pair.Key, out _);
                }
            }

            while (_sessions.Count > Math.Max(1, MaxSessions))
            {
                var oldest = _sessions.Values.Where(s => !ReferenceEquals(s, keep)).OrderBy(s => s.LastUsedUtc).FirstOrDefault();

                if (oldest is null || !_sessions.TryRemove(oldest.Id, out _))
                {
                    break;
                }
            }
        }
    }

    public sealed class NotebookSession
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly ScopeHandler _scopes;
        private readonly List<string> _acceptedCells = new List<string>();
        private readonly Dictionary<string, (int Version, ByteCode ByteCode)> _compiledCells = new Dictionary<string, (int, ByteCode)>(StringComparer.Ordinal);
        private ParseContext _parseContext;
        private List<RefFunction> _functions = new List<RefFunction>();
        private List<RuntimeClass> _classes = new List<RuntimeClass>();
        private int _definitionsVersion;
        private long _lastUsedTicks;

        public string Id { get; }
        public DateTime LastUsedUtc => new DateTime(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);
        public int CellCount { get; private set; }

        /// <summary>Macro dictionaries for every lexer this session creates; set by the host.</summary>
        public MacroPreprocessor Preprocessor { get; set; }

        /// <summary>Parse errors of the last cell, one per line, or null.</summary>
        public string LastError { get; private set; }

        public long LastParseMicroseconds { get; private set; }
        public long LastCompileMicroseconds { get; private set; }

        /// <summary>True when the last cell's bytecode came from the session cache (same source, same definitions).</summary>
        public bool LastCompileCached { get; private set; }

        internal NotebookSession(string id, Proxy.XppProxy proxy)
        {
            Id = id;
            _scopes = new ScopeHandler(proxy);
            _parseContext = NewParseContext();
            Touch();
        }

        internal void Touch()
        {
            Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>Cells of one session run one at a time. Returns false when the session stayed busy.</summary>
        public bool Enter(int timeoutMilliseconds)
        {
            return _gate.Wait(timeoutMilliseconds);
        }

        public void Exit()
        {
            _gate.Release();
        }

        private static ParseContext NewParseContext()
        {
            return new ParseContext { ReplaceGlobalDeclarations = true };
        }

        /// <summary>
        /// Parses and compiles one cell against the session. Returns null and sets LastError when the
        /// cell does not parse; the session is left as it was before the cell.
        /// </summary>
        public ByteCode CompileCell(string source, Proxy.XppProxy proxy, XppInterpreterOptions options)
        {
            Touch();
            LastError = null;
            LastParseMicroseconds = 0;
            LastCompileMicroseconds = 0;

            // Running the same cell again reuses its bytecode, and with it any loops already compiled to IL
            if (_compiledCells.TryGetValue(source, out var cached) && cached.Version == _definitionsVersion)
            {
                LastCompileCached = true;
                CellCount++;
                return cached.ByteCode;
            }

            LastCompileCached = false;
            Stopwatch watch = Stopwatch.StartNew();
            ParseResult parsed = XppParser.ParseWithContext(new XppLexer(source, Preprocessor), proxy, _parseContext);
            LastParseMicroseconds = Microseconds(watch);

            if (!parsed.IsCorrect)
            {
                LastError = string.Join("\n", parsed.Errors.Cast<ParseError>().Select(e => e.Line > 0 ? $"Line {e.Line}: {e.Message}" : e.Message));
                RebuildParseContext(proxy);
                return null;
            }

            watch.Restart();
            int classCount = _classes.Count;
            int functionCount = _functions.Count;
            ByteCode generated = new ByteCodeGenerator(options).Generate((Program)parsed.AST, false, _functions, _classes);

            // A cell's statements run in the session's global scope, so its declarations outlive it
            List<IInstruction> instructions = generated.Instructions;

            if (instructions.Count >= 2 && instructions[0].GetType() == typeof(BeginScope) && instructions[instructions.Count - 1].GetType() == typeof(EndScope))
            {
                instructions = instructions.GetRange(1, instructions.Count - 2);
            }

            bool definesTypes = generated.DeclaredClasses.Count != classCount || generated.DeclaredFunctions.Count != functionCount;

            // A redefinition replaces the earlier class or function of the same name
            _classes = LastByName(generated.DeclaredClasses, c => c.Name);
            _functions = LastByName(generated.DeclaredFunctions, f => f.Declaration?.Name);

            foreach (RuntimeClass declaredClass in _classes)
            {
                if (declaredClass.BaseClassName != null)
                {
                    declaredClass.BaseClass = _classes.FirstOrDefault(c => string.Equals(c.Name, declaredClass.BaseClassName, StringComparison.OrdinalIgnoreCase)) ?? declaredClass.BaseClass;
                }
            }

            var byteCode = new ByteCode(instructions)
            {
                DeclaredFunctions = _functions,
                DeclaredClasses = _classes
            };

            if (definesTypes)
            {
                // Earlier cells' bytecode refers to the replaced definitions
                _definitionsVersion++;
                _compiledCells.Clear();
            }

            _compiledCells[source] = (_definitionsVersion, byteCode);
            _acceptedCells.Add(source);
            CellCount++;
            LastCompileMicroseconds = Microseconds(watch);
            return byteCode;
        }

        /// <summary>A runtime context for one cell: the session's scopes with fresh execution limits.</summary>
        public RuntimeContext CreateContext(ByteCode byteCode, Proxy.XppProxy proxy)
        {
            return new RuntimeContext(proxy, byteCode, _scopes);
        }

        /// <summary>Session variables as "name: type = value" lines, in declaration order.</summary>
        public string DescribeVariables()
        {
            var text = new StringBuilder();

            foreach (VariableEntry variable in _scopes.GlobalScope.VariableCollection)
            {
                string type = variable.TypeName ?? variable.DeclarationType?.Name ?? variable.Value?.GetType().Name ?? "anytype";
                string value;

                try
                {
                    value = variable.Value is null ? "null" : variable.Value.ToString();
                }
                catch (Exception exception)
                {
                    value = "<" + exception.GetType().Name + ">";
                }

                if (value.Length > 200)
                {
                    value = value.Substring(0, 200) + "...";
                }

                text.Append(variable.Name).Append(": ").Append(type).Append(" = ").Append(value).Append('\n');
            }

            return text.ToString();
        }

        /// <summary>
        /// A cell that fails to parse may already have added declarations to the parse context; the
        /// context is rebuilt from the cells that were accepted, so the failed cell leaves no trace.
        /// </summary>
        private void RebuildParseContext(Proxy.XppProxy proxy)
        {
            ParseContext context = NewParseContext();

            foreach (string cell in _acceptedCells)
            {
                XppParser.ParseWithContext(new XppLexer(cell, Preprocessor), proxy, context);
            }

            _parseContext = context;
        }

        private static List<T> LastByName<T>(List<T> items, Func<T, string> name)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<T>();

            for (int i = items.Count - 1; i >= 0; i--)
            {
                string key = name(items[i]);

                if (key is null || seen.Add(key))
                {
                    kept.Add(items[i]);
                }
            }

            kept.Reverse();
            return kept;
        }

        private static long Microseconds(Stopwatch watch)
        {
            return watch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
        }
    }
}

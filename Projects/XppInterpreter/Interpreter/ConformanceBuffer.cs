using System;
using System.Collections.Generic;

namespace XppInterpreter.Interpreter
{
    /// <summary>
    /// Collects the values a conformance case emits, so the same case run natively and
    /// through the interpreter can be compared. Thread-static because each AOS request
    /// runs on its own thread and concurrent conformance runs must not mix results.
    /// </summary>
    public static class ConformanceBuffer
    {
        [ThreadStatic]
        private static List<string> _values;

        public static void Reset()
        {
            _values = new List<string>();
        }

        public static void Emit(string value)
        {
            if (_values is null)
            {
                _values = new List<string>();
            }

            _values.Add(value ?? "<null>");
        }

        public static string Snapshot()
        {
            return _values is null ? string.Empty : string.Join(" | ", _values);
        }
    }
}

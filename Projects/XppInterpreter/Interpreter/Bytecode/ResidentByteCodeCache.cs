using System;
using System.Collections.Concurrent;

namespace XppInterpreter.Interpreter.Bytecode
{
    /// <summary>
    /// Process-resident cache shared by all X++ custom-service sessions in an AOS
    /// worker. Module versions are immutable, so entries never change in place.
    /// </summary>
    public static class ResidentByteCodeCache
    {
        private static readonly ConcurrentDictionary<string, ByteCode> Entries =
            new ConcurrentDictionary<string, ByteCode>(StringComparer.OrdinalIgnoreCase);

        public static ByteCode Get(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            Entries.TryGetValue(key, out ByteCode byteCode);
            return byteCode;
        }

        public static void Put(string key, ByteCode byteCode)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A bytecode cache key is required.", nameof(key));
            if (byteCode == null)
                throw new ArgumentNullException(nameof(byteCode));
            Entries[key] = byteCode;
        }

        public static int Count => Entries.Count;

        public static void Clear()
        {
            Entries.Clear();
        }
    }
}

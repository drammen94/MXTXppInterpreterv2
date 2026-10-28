using System;
using System.Collections;
using System.Collections.Generic;

namespace XppInterpreter.Core
{
    public class VariableCollection : IEnumerable
    {
        // X++ identifiers are case-insensitive; ordinal comparison avoids culture lookups on every variable access
        private Dictionary<string, VariableEntry> _var = new Dictionary<string, VariableEntry>(StringComparer.OrdinalIgnoreCase);

        public VariableEntry this[string key] { get => _var[key]; set => _var[key] = value; }

        public bool TryGet(string name, out VariableEntry entry)
        {
            return _var.TryGetValue(name, out entry);
        }

        public void Remove(string key)
        {
            _var.Remove(key);
        }

        public void Add(VariableEntry entry)
        {
            _var.Add(entry.Name, entry);
        }

        public bool Exists(string name)
        {
            return _var.ContainsKey(name);
        }

        public IEnumerator GetEnumerator()
        {
            return _var.Values.GetEnumerator();
        }
    }
}

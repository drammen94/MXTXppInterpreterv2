using System;
using System.Collections.Generic;
using System.Linq;

namespace XppInterpreter.Interpreter.Bytecode
{
    public class ByteCode
    {
        public List<IInstruction> Instructions;
        public List<RefFunction> DeclaredFunctions;
        public List<RuntimeClass> DeclaredClasses;
        private Metadata.DynamicModelRegistry _models;

        public Metadata.DynamicModelRegistry Models
        {
            get
            {
                if (_models is null) _models = new Metadata.DynamicModelRegistry();
                _models.Synchronize(DeclaredClasses);
                return _models;
            }
        }

        internal ByteCode()
        {
            Instructions = new List<IInstruction>();
            DeclaredFunctions = new List<RefFunction>();
            DeclaredClasses = new List<RuntimeClass>();
        }

        public ByteCode(List<IInstruction> instructions)
        {
            Instructions = instructions;
            DeclaredClasses = new List<RuntimeClass>();
        }

        public RuntimeClass FindDeclaredClass(string name)
        {
            if (name is null || DeclaredClasses is null) return null;

            return DeclaredClasses.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}

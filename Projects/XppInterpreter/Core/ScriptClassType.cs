using System;
using System.Reflection;

namespace XppInterpreter.Core
{
    /// <summary>
    /// Synthetic <see cref="System.Type"/> representing a script-declared class in the
    /// type-inference and validation layers. Member lookups must be guarded at proxy
    /// call sites and routed to the parse-time class declaration table instead.
    /// </summary>
    public sealed class ScriptClassType : TypeDelegator
    {
        private readonly string _name;

        /// <summary>
        /// Parse-time declaration info. Typed as object to avoid a circular
        /// dependency; consumers cast to Parser.ClassDeclarationInfo.
        /// </summary>
        public object DeclarationInfo { get; }

        public ScriptClassType(string name, object declarationInfo) : base(typeof(object))
        {
            _name = name;
            DeclarationInfo = declarationInfo;
        }

        public override string Name => _name;
        public override string FullName => _name;
        public override string ToString() => _name;
    }
}

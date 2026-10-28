namespace XppInterpreter.Interpreter.Bytecode
{
    /// <summary>
    /// Switch dispatch: pops the case value and compares it against the switch value
    /// (kept on the stack). On a match the switch value is popped and control jumps
    /// into the linear case-statement region, enabling X++ fall-through semantics.
    /// </summary>
    class CaseJumpIfEqual : Jump
    {
        public override string OperationCode => $"CASE_JUMP_EQ {Offset}";

        public CaseJumpIfEqual(int offset) : base(offset) { }

        public override void Execute(RuntimeContext context)
        {
            var caseValue = context.Stack.Pop();
            var switchValue = context.Stack.Peek();

            bool equal = (switchValue is ScriptObject || caseValue is ScriptObject)
                ? ReferenceEquals(switchValue, caseValue)
                : context.Proxy.Binary.AreEqual(switchValue, caseValue);

            if (equal)
            {
                context.Stack.Pop();
                base.Execute(context);
            }
        }
    }
}

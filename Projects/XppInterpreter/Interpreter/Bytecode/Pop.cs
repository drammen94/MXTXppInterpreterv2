namespace XppInterpreter.Interpreter.Bytecode
{
    /// <summary>
    /// Discards the top of the operand stack
    /// </summary>
    class Pop : IInstruction
    {
        public string OperationCode => "POP";

        public void Execute(RuntimeContext context)
        {
            if (context.Stack.Count > 0)
            {
                context.Stack.Pop();
            }
        }
    }
}

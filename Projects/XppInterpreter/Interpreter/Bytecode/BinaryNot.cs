namespace XppInterpreter.Interpreter.Bytecode
{
    /// <summary>
    /// Unary bitwise complement operator (~)
    /// </summary>
    class BinaryNot : IInstruction
    {
        public string OperationCode => "BNOT";

        public void Execute(RuntimeContext context)
        {
            var value = context.Stack.Pop();

            if (value is long longValue)
            {
                context.Stack.Push(~longValue);
            }
            else
            {
                context.Stack.Push(~System.Convert.ToInt32(value));
            }
        }
    }
}

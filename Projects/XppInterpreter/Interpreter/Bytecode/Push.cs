namespace XppInterpreter.Interpreter.Bytecode
{
    class Push : IInstruction
    {
        internal object Value { get; }
        public string OperationCode
        {
            get
            {
                if (Value is string)
                {
                    return $"PUSH \"{Value}\"";
                }
                else
                {
                    return $"PUSH {Value}";
                }
            }
        }

        public Push(object value)
        {
            Value = value;
        }

        public void Execute(RuntimeContext context)
        {
            context.Stack.Push(Value);
        }
    }
}

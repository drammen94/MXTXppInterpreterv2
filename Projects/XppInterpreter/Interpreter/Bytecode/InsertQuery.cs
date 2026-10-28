using XppInterpreter.Interpreter.Query;

namespace XppInterpreter.Interpreter.Bytecode
{
    class InsertQuery : IInstruction
    {
        public string OperationCode => "INSERT_QUERY";
        public Parser.InsertRecordset InsertRecordset { get; }

        public InsertQuery(Parser.InsertRecordset insertRecordset)
        {
            InsertRecordset = insertRecordset;
        }

        public void Execute(RuntimeContext context)
        {
            QueryGeneratorRouter.ForInsert(context, InsertRecordset).ExecuteInsertRecordset(InsertRecordset, context);
        }
    }
}

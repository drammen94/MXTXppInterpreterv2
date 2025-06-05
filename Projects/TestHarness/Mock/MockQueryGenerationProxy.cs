using XppInterpreter.Interpreter;
using XppInterpreter.Interpreter.Proxy;
using XppInterpreter.Interpreter.Query;
using XppInterpreter.Parser;
using XppInterpreter.Parser.Data;

namespace TestHarness.Mock
{
    class MockQueryGenerationProxy : IXppQueryGenerationProxy
    {
        class DummyQueryGenerator : IQueryGenerator
        {
            public void ExecuteInsertRecordset(InsertRecordset insertRecordset, RuntimeContext runtimeContext) { }
            public void ExecuteDeleteFrom(DeleteFrom deleteFrom, RuntimeContext runtimeContext) { }
            public void ExecuteUpdateRecordset(UpdateRecordset updateRecordset, RuntimeContext runtimeContext) { }
            public ISearchInstance NewSearchInstance(Query query, RuntimeContext runtimeContext) => null;
        }

        public IQueryGenerator NewQueryGenerator() => new DummyQueryGenerator();
    }
}

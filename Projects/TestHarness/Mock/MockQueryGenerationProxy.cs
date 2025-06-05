using XppInterpreter.Interpreter.Proxy;

namespace TestHarness.Mock
{
    class MockQueryGenerationProxy : IXppQueryGenerationProxy
    {
        public IQueryGenerator NewQueryGenerator() => null;
    }
}

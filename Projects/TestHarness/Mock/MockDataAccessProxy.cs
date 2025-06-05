using XppInterpreter.Interpreter.Proxy;

namespace TestHarness.Mock
{
    class MockDataAccessProxy : IXppDataAccessProxy
    {
        public void TtsBegin() { }
        public void TtsCommit() { }
        public void TtsAbort() { }
    }
}

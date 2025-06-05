using System;
using XppInterpreter.Interpreter.Proxy;

namespace TestHarness.Mock
{
    class MockDataAccessProxy : IXppDataAccessProxy
    {
        public void TtsBegin() { }
        public void TtsCommit() { }
        public void TtsAbort() { }

        public IDisposable CreateChangeCompanyHandler(string datAreaId)
        {
            return null;
        }

        public void Next(object common) { }
    }
}

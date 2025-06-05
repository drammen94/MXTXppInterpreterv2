using System;
using XppInterpreter.Interpreter.Proxy;

namespace TestHarness.Mock
{
    class MockExceptionsProxy : IXppExceptionsProxy
    {
        public void Throw(string message) => throw new Exception(message);
    }
}

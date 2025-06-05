using System;
using XppInterpreter.Interpreter.Proxy;

namespace TestHarness.Mock
{
    class MockExceptionsProxy : IXppExceptionsProxy
    {
        public void Throw(object obj) => throw new Exception(obj?.ToString());

        public bool IsExceptionMember(Exception exception, string exceptionMember) => false;

        public void SetRetryCount(int retryCount) { }
    }
}

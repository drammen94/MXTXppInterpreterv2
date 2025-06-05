using XppInterpreter.Interpreter.Proxy;

namespace TestHarness.Mock
{
    class MockUnaryOperationProxy : IXppUnaryOperationProxy
    {
        public object BitwiseNot(object value) => ~(dynamic)value;
        public object LogicalNot(object value) => !(dynamic)value;
        public object Negate(object value) => -(dynamic)value;
        public object Positive(object value) => +(dynamic)value;
    }
}

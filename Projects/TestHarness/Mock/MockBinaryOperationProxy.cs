using XppInterpreter.Interpreter.Proxy;

namespace TestHarness.Mock
{
    class MockBinaryOperationProxy : IXppBinaryOperationProxy
    {
        public object Add(object left, object right) => (dynamic)left + (dynamic)right;
        public object Substract(object left, object right) => (dynamic)left - (dynamic)right;
        public object Multiply(object left, object right) => (dynamic)left * (dynamic)right;
        public object Divide(object left, object right) => (dynamic)left / (dynamic)right;
        public object IntDivide(object left, object right) => (dynamic)left / (dynamic)right;
        public object LeftShift(object left, object right) => null;
        public object RightShift(object left, object right) => null;
        public object BinaryAnd(object left, object right) => null;
        public object BinaryOr(object left, object right) => null;
        public object BinaryXOr(object left, object right) => null;
        public object Mod(object left, object right) => (dynamic)left % (dynamic)right;
        public bool Greater(object left, object right) => (dynamic)left > (dynamic)right;
        public bool AreEqual(object left, object right) => Equals(left, right);
    }
}

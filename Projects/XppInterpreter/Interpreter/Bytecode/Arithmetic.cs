using System;
using System.Collections.Generic;
using XppInterpreter.Lexer;

namespace XppInterpreter.Interpreter.Bytecode
{
    public class Arithmetic : IInstruction
    {
        public string OperationCode => _arithmeticOpCodes[TokenType];

        public TType TokenType { get; private set; }

        public Arithmetic(TType tokenType)
        {
            if (!_arithmeticOpCodes.ContainsKey(tokenType))
            {
                throw new ArgumentException($"{tokenType} is invalid");
            }

            TokenType = tokenType;

        }

        public void Execute(RuntimeContext context)
        {
            object right = context.Stack.Pop();
            object left = context.Stack.Pop();

            context.Stack.Push(Evaluate(context, left, right));
        }

        internal object Evaluate(RuntimeContext context, object left, object right)
        {
            if (TryFastPath(left, right, out object result))
            {
                return result;
            }

            switch (TokenType)
            {
                case TType.Plus:
                case TType.PlusAssignment:
                case TType.Increment:
                    result = context.Proxy.Binary.Add(left, right);
                    break;

                case TType.Minus:
                case TType.MinusAssignment:
                case TType.Decrement:
                    result = context.Proxy.Binary.Substract(left, right);
                    break;

                case TType.Star:
                    result = context.Proxy.Binary.Multiply(left, right);
                    break;

                case TType.Division:
                    result = context.Proxy.Binary.Divide(left, right);
                    break;

                case TType.IntegerDivision:
                    result = context.Proxy.Binary.IntDivide(left, right);
                    break;

                case TType.LeftShift:
                    result = context.Proxy.Binary.LeftShift(left, right);
                    break;

                case TType.RightShift:
                    result = context.Proxy.Binary.RightShift(left, right);
                    break;

                case TType.BinaryAnd:
                    result = context.Proxy.Binary.BinaryAnd(left, right);
                    break;

                case TType.BinaryOr:
                    result = context.Proxy.Binary.BinaryOr(left, right);
                    break;

                case TType.BinaryXOr:
                    result = context.Proxy.Binary.BinaryXOr(left, right);
                    break;

                case TType.Mod:
                    result = context.Proxy.Binary.Mod(left, right);
                    break;

                case TType.Equal:
                    result = AreEqual(context, left, right);
                    break;

                case TType.NotEqual:
                    result = !AreEqual(context, left, right);
                    break;

                case TType.Greater:
                    result = context.Proxy.Binary.Greater(left, right);
                    break;

                case TType.GreaterOrEqual:
                    result = context.Proxy.Binary.Greater(left, right) || context.Proxy.Binary.AreEqual(left, right);
                    break;

                case TType.Smaller:
                    result = !context.Proxy.Binary.Greater(left, right) && !context.Proxy.Binary.AreEqual(left, right);
                    break;

                case TType.SmallerOrEqual:
                    result = !context.Proxy.Binary.Greater(left, right);
                    break;

                case TType.Like:
                    result = LikeMatch(left, right);
                    break;
            }

            return result;
        }

        private static readonly object True = true;
        private static readonly object False = false;

        private static object Box(bool value) => value ? True : False;

        /// <summary>
        /// Evaluates operations whose result cannot differ from native X++ without a proxy round trip.
        /// Only same-type int/int64/bool operands qualify, plus comparisons between int and int64 (exact
        /// in either width); anything that could overflow, round, or depend on runtime conversion rules
        /// (real, str, other mixed types, div, mod, shifts) goes to the proxy.
        /// </summary>
        internal bool TryFastPath(object left, object right, out object result)
        {
            if (left is int l32 && right is int r32)
            {
                return TryInt64(l32, r32, out result, int.MinValue, int.MaxValue, true);
            }

            if (left is long l64 && right is long r64)
            {
                return TryInt64(l64, r64, out result, long.MinValue, long.MaxValue, false);
            }

            if (left is bool lb && right is bool rb)
            {
                switch (TokenType)
                {
                    case TType.Equal: result = Box(lb == rb); return true;
                    case TType.NotEqual: result = Box(lb != rb); return true;
                }
            }

            if ((left is int || left is long) && (right is int || right is long) && TryCompare(Convert.ToInt64(left), Convert.ToInt64(right), out result))
            {
                return true;
            }

            result = null;
            return false;
        }

        private bool TryInt64(long l, long r, out object result, long min, long max, bool narrow)
        {
            long value;

            switch (TokenType)
            {
                case TType.Equal:
                case TType.NotEqual:
                case TType.Greater:
                case TType.GreaterOrEqual:
                case TType.Smaller:
                case TType.SmallerOrEqual:
                    return TryCompare(l, r, out result);

                case TType.BinaryAnd: value = l & r; break;
                case TType.BinaryOr: value = l | r; break;
                case TType.BinaryXOr: value = l ^ r; break;

                // Overflow is left to the native runtime so its wrap/throw behaviour stays authoritative
                case TType.Plus:
                case TType.PlusAssignment:
                case TType.Increment:
                    value = unchecked(l + r);
                    if (narrow ? (value < min || value > max) : (((l ^ value) & (r ^ value)) < 0)) { result = null; return false; }
                    break;

                case TType.Minus:
                case TType.MinusAssignment:
                case TType.Decrement:
                    value = unchecked(l - r);
                    if (narrow ? (value < min || value > max) : (((l ^ r) & (l ^ value)) < 0)) { result = null; return false; }
                    break;

                case TType.Star:
                    // Two int32 operands always fit in int64; int64 products are only taken when provably in range
                    if (!narrow && !FitsInt64Product(l, r)) { result = null; return false; }
                    value = unchecked(l * r);
                    if (value < min || value > max) { result = null; return false; }
                    break;

                default:
                    result = null;
                    return false;
            }

            result = narrow ? (object)(int)value : value;
            return true;
        }

        private bool TryCompare(long l, long r, out object result)
        {
            switch (TokenType)
            {
                case TType.Equal: result = Box(l == r); return true;
                case TType.NotEqual: result = Box(l != r); return true;
                case TType.Greater: result = Box(l > r); return true;
                case TType.GreaterOrEqual: result = Box(l >= r); return true;
                case TType.Smaller: result = Box(l < r); return true;
                case TType.SmallerOrEqual: result = Box(l <= r); return true;
            }

            result = null;
            return false;
        }

        private static bool FitsInt64Product(long l, long r)
        {
            const long limit = 3037000499; // floor(sqrt(long.MaxValue))
            return l >= -limit && l <= limit && r >= -limit && r <= limit;
        }

        /// <summary>
        /// Script class instances compare by reference, everything else through the proxy
        /// </summary>
        private static bool AreEqual(RuntimeContext context, object left, object right)
        {
            if (left is ScriptObject || right is ScriptObject)
            {
                return ReferenceEquals(left, right);
            }

            return context.Proxy.Binary.AreEqual(left, right);
        }

        /// <summary>
        /// X++ like operator: * matches any character sequence, ? matches exactly one character
        /// </summary>
        internal static bool LikeMatch(object value, object pattern)
        {
            string text = value?.ToString() ?? "";
            string wildcard = pattern?.ToString() ?? "";

            string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(wildcard)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            return System.Text.RegularExpressions.Regex.IsMatch(
                text,
                regexPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        }

        public static bool IsArithmetic(TType tokenType)
        {
            return _arithmeticOpCodes.ContainsKey(tokenType);
        }

        private readonly static Dictionary<TType, string> _arithmeticOpCodes = new Dictionary<TType, string>()
        {
            { TType.Plus, "ADD" },
            { TType.PlusAssignment, "ADD" },
            { TType.Increment, "ADD" },
            { TType.Minus, "SUB" },
            { TType.MinusAssignment, "SUB" },
            { TType.Decrement, "SUB" },
            { TType.Star, "MUL" },
            { TType.Division, "DIV" },
            { TType.IntegerDivision, "INTDIV" },
            { TType.Mod, "MOD" },
            { TType.Equal, "EQ" },
            { TType.NotEqual, "NE" },
            { TType.Greater, "GR" },
            { TType.GreaterOrEqual, "GE" },
            { TType.Smaller, "SM" },
            { TType.SmallerOrEqual, "SME" },
            { TType.LeftShift, "LS" },
            { TType.RightShift, "RS" },
            { TType.BinaryOr, "BOR" },
            { TType.BinaryAnd, "BAND" },
            { TType.BinaryXOr, "BXOR" },
            { TType.Like, "LIKE" },
        };
    }
}

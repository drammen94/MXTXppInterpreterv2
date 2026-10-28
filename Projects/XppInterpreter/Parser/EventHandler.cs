using XppInterpreter.Interpreter;

namespace XppInterpreter.Parser
{
    public enum EventHandlerKind
    {
        /// <summary>A function declared in the script (eventhandler(myFunction))</summary>
        LocalFunction,

        /// <summary>A static method (eventhandler(MyClass::myStaticMethod))</summary>
        StaticMethod,

        /// <summary>An instance method on a variable (eventhandler(myObject.myMethod))</summary>
        InstanceMethod,
    }

    public class EventHandler
    {
        public string FunctionName { get; }
        public EventHandlerKind Kind { get; }

        /// <summary>
        /// The class name (static handler) or variable name (instance handler)
        /// </summary>
        public string CallerName { get; }

        public EventHandler(string functionName, EventHandlerKind kind = EventHandlerKind.LocalFunction, string callerName = null)
        {
            FunctionName = functionName;
            Kind = kind;
            CallerName = callerName;
        }
    }
}

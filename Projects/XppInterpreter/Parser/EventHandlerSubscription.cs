using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using XppInterpreter.Interpreter;

namespace XppInterpreter.Parser
{
    public class EventHandlerSubscription : Statement
    {
        public Variable Delegate { get; }
        public EventHandler EventHandler { get; }

        /// <summary>
        /// True for -= (removes the handler instead of adding it)
        /// </summary>
        public bool Unsubscribe { get; }

        public EventHandlerSubscription(Variable @delegate, EventHandler eventHandler, SourceCodeBinding sourceCodeBinding, bool unsubscribe = false) : base(sourceCodeBinding, sourceCodeBinding)
        {
            Delegate = @delegate;
            EventHandler = eventHandler;
            Unsubscribe = unsubscribe;
        }

        [DebuggerHidden]
        public override void Accept(IAstVisitor interpreter)
        {
            interpreter.VisitEventHandlerSubscription(this);
        }
    }
}

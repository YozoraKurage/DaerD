using UnityEditor.Animations;

namespace Yozolab.DaerD
{
    /// <summary>What a <see cref="TransitionEnd"/> stands for. None is an end nothing can connect to.</summary>
    enum TransitionEndKind
    {
        None,
        State,
        SubStateMachine,
        Entry,
        Exit,
        AnyState
    }

    /// <summary>
    /// One end of a transition, named by animator object instead of by graph node. This is what
    /// lets <see cref="EdgeCommands"/> run without a graph view: GraphSync converts the node the
    /// user clicked into one of these, and everything downstream works off the model alone.
    /// </summary>
    readonly struct TransitionEnd
    {
        public readonly TransitionEndKind Kind;
        public readonly AnimatorState State;
        public readonly AnimatorStateMachine StateMachine;

        TransitionEnd(TransitionEndKind kind, AnimatorState state, AnimatorStateMachine stateMachine)
        {
            Kind = kind;
            State = state;
            StateMachine = stateMachine;
        }

        public static TransitionEnd Of(AnimatorState state) =>
            new TransitionEnd(TransitionEndKind.State, state, null);

        public static TransitionEnd Of(AnimatorStateMachine stateMachine) =>
            new TransitionEnd(TransitionEndKind.SubStateMachine, null, stateMachine);

        public static readonly TransitionEnd Entry = new TransitionEnd(TransitionEndKind.Entry, null, null);
        public static readonly TransitionEnd Exit = new TransitionEnd(TransitionEndKind.Exit, null, null);
        public static readonly TransitionEnd AnyState = new TransitionEnd(TransitionEndKind.AnyState, null, null);

        /// <summary>
        /// How this end reads in a menu, a sorted list or a transition row. Entry, Exit and Any
        /// State are spelled the way their nodes are labelled in the graph rather than
        /// translated, so a row and the node it points at say the same word in every language.
        /// </summary>
        public string Label
        {
            get
            {
                switch (Kind)
                {
                    case TransitionEndKind.State: return State != null ? State.name : "(state)";
                    case TransitionEndKind.SubStateMachine:
                        return StateMachine != null ? StateMachine.name : "(sub-state machine)";
                    case TransitionEndKind.Entry: return "Entry";
                    case TransitionEndKind.Exit: return "Exit";
                    case TransitionEndKind.AnyState: return "Any State";
                    default: return "?";
                }
            }
        }

        /// <summary>
        /// Where a transition currently goes, read off the transition itself rather than off the
        /// node its edge lands on. The two differ exactly where it matters: a transition into a
        /// state nested in a sub-state machine is drawn to the machine's node, so asking the graph
        /// would answer "the machine" for something that names a state inside it.
        /// </summary>
        public static TransitionEnd DestinationOf(AnimatorTransitionBase transition)
        {
            if (transition == null) return None;
            if (transition.isExit) return Exit;
            if (transition.destinationState != null) return Of(transition.destinationState);
            if (transition.destinationStateMachine != null) return Of(transition.destinationStateMachine);
            return None;
        }

        /// <summary>An end no transition can start from or land on — what an unknown node maps to.</summary>
        public static readonly TransitionEnd None = new TransitionEnd(TransitionEndKind.None, null, null);

        /// <summary>
        /// Two ends are the same when they name the same animator object, which is what comparing
        /// two graph nodes by reference used to mean: the graph holds one node per state, per
        /// sub-state machine and per special kind.
        /// </summary>
        public bool SameAs(TransitionEnd other) =>
            Kind == other.Kind && State == other.State && StateMachine == other.StateMachine;

        /// <summary>
        /// The one copy of the connect rule: states and sub-state machines may reach a state, a
        /// sub-state machine or Exit; Entry and Any State may reach a state or a sub-state machine
        /// but cannot transition straight to Exit. The graph view asks the same question while
        /// dragging an edge — <see cref="TransitionConnect.CanConnect"/> turns its two nodes into
        /// ends and lands here.
        /// </summary>
        public static bool CanConnect(TransitionEnd source, TransitionEnd destination)
        {
            bool destState = destination.Kind == TransitionEndKind.State;
            bool destSsm = destination.Kind == TransitionEndKind.SubStateMachine;
            bool destExit = destination.Kind == TransitionEndKind.Exit;
            switch (source.Kind)
            {
                case TransitionEndKind.State:
                case TransitionEndKind.SubStateMachine:
                    return destState || destSsm || destExit;
                case TransitionEndKind.AnyState:
                case TransitionEndKind.Entry:
                    // Entry / Any State cannot transition straight to Exit.
                    return destState || destSsm;
                default:
                    return false;
            }
        }
    }
}

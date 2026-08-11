using System.Collections.Generic;
using UnityEditor.Animations;

namespace Yozolab.DaerD
{
    /// <summary>
    /// One selected edge, reduced to model data: where its transitions start, whether it is the
    /// default-state link, and the transitions it carries.
    /// </summary>
    /// <remarks>
    /// The source is what an edge alone cannot answer. An edge knows only the transitions that
    /// share its two endpoints, but the Animator evaluates a source's transitions as one ordered
    /// list — the first whose conditions hold wins — and that list also holds the transitions
    /// going to every other destination. Naming the source lets the inspector ask for the whole
    /// list, so the numbers it shows are the real priority instead of a position within one edge.
    /// </remarks>
    readonly struct TransitionGroup
    {
        public readonly TransitionEnd Source;
        public readonly bool IsDefault;
        public readonly IList<AnimatorTransitionBase> Transitions;

        public TransitionGroup(TransitionEnd source, bool isDefault, IList<AnimatorTransitionBase> transitions)
        {
            Source = source;
            IsDefault = isDefault;
            Transitions = transitions;
        }
    }
}

using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.IR;

namespace Yozolab.DaerD.Authoring
{
    // ---- transitions ----------------------------------------------------------------

    public sealed class TransitionBuilder
    {
        readonly ControllerBuilder _root;
        internal readonly ControllerIR.Transition Transition;

        internal TransitionBuilder(ControllerBuilder root, ControllerIR.Transition transition)
        {
            _root = root;
            Transition = transition;
        }

        /// <summary>Adds a condition. Conditions AND together; When and And are synonyms so
        /// chains read naturally (.When(a.IsTrue()).And(b.IsGreaterThan(0.5f))).</summary>
        public TransitionBuilder When(Condition condition) => Append(condition, "When");

        public TransitionBuilder And(Condition condition) => Append(condition, "And");

        TransitionBuilder Append(Condition condition, string method)
        {
            if (condition == null) return this;
            Transition.conditions.Add(new ControllerIR.Condition
            {
                mode = condition.Mode,
                parameter = condition.Parameter,
                threshold = condition.Threshold,
            });
            if (condition.Source != null)
                _root.Script?.Call(this, $"{method}({condition.Source})");
            return this;
        }

        /// <summary>Exit time 1: leave when the animation completes a loop.</summary>
        public TransitionBuilder AfterAnimationFinishes()
        {
            Transition.hasExitTime = true;
            Transition.exitTime = 1f;
            _root.Script?.Call(this, "AfterAnimationFinishes()");
            return this;
        }

        public TransitionBuilder AfterAnimationIsAtLeastAtNormalized(float exitTimeNormalized)
        {
            Transition.hasExitTime = true;
            Transition.exitTime = exitTimeNormalized;
            _root.Script?.Call(this,
                $"AfterAnimationIsAtLeastAtNormalized({RecipeScript.F(exitTimeNormalized)})");
            return this;
        }

        public TransitionBuilder WithTransitionDurationSeconds(float seconds)
        {
            Transition.hasFixedDuration = true;
            Transition.duration = seconds;
            _root.Script?.Call(this, $"WithTransitionDurationSeconds({RecipeScript.F(seconds)})");
            return this;
        }

        public TransitionBuilder WithTransitionDurationNormalized(float fraction)
        {
            Transition.hasFixedDuration = false;
            Transition.duration = fraction;
            _root.Script?.Call(this, $"WithTransitionDurationNormalized({RecipeScript.F(fraction)})");
            return this;
        }

        public TransitionBuilder WithOffset(float offset)
        {
            Transition.offset = offset;
            _root.Script?.Call(this, $"WithOffset({RecipeScript.F(offset)})");
            return this;
        }

        public TransitionBuilder WithInterruption(TransitionInterruptionSource source)
        {
            Transition.interruptionSource = source;
            _root.Script?.Call(this, $"WithInterruption({RecipeScript.E(source)})");
            return this;
        }

        public TransitionBuilder WithOrderedInterruption()
        {
            Transition.orderedInterruption = true;
            _root.Script?.Call(this, "WithOrderedInterruption()");
            return this;
        }

        public TransitionBuilder WithNoOrderedInterruption()
        {
            Transition.orderedInterruption = false;
            _root.Script?.Call(this, "WithNoOrderedInterruption()");
            return this;
        }

        public TransitionBuilder WithTransitionToSelf()
        {
            Transition.canTransitionToSelf = true;
            _root.Script?.Call(this, "WithTransitionToSelf()");
            return this;
        }

        public TransitionBuilder WithNoTransitionToSelf()
        {
            Transition.canTransitionToSelf = false;
            _root.Script?.Call(this, "WithNoTransitionToSelf()");
            return this;
        }

        public TransitionBuilder Solo(bool on = true)
        {
            Transition.solo = on;
            _root.Script?.Call(this, on ? "Solo()" : "Solo(false)");
            return this;
        }

        public TransitionBuilder Mute(bool on = true)
        {
            Transition.mute = on;
            _root.Script?.Call(this, on ? "Mute()" : "Mute(false)");
            return this;
        }
    }
}

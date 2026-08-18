using System.Collections.Generic;
using System.Text;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Bridge
{
    /// <summary>
    /// What one layer of a running Animator is doing this frame, and the one piece of
    /// bookkeeping needed to match it back to the graph: a state's full path hash.
    ///
    /// Short names are not enough to match on. <c>AnimatorStateInfo.shortNameHash</c> is the
    /// hash of the state's own name, and nothing stops two sub-state machines from each
    /// holding an "Idle" — matching on it lights both. The full path is what Unity itself
    /// identifies a state by, and <see cref="FullPathHash"/> rebuilds it from the graph side.
    /// </summary>
    static class AnimatorPlayback
    {
        public struct LayerPlayback
        {
            /// <summary>False when there was nothing to read — no animator, not initialized,
            /// or the layer index does not exist on it.</summary>
            public bool valid;
            /// <summary>Full path hash of the state currently playing.</summary>
            public int stateHash;
            /// <summary>How far through its clip that state is, 0 to 1.</summary>
            public float progress;
            public bool inTransition;
            /// <summary>Full path hash of the state being transitioned to.</summary>
            public int nextStateHash;
            /// <summary>How far through the transition, 0 to 1.</summary>
            public float transitionProgress;
            /// <summary>The transition started from Any State, so the edge that is running
            /// leaves the Any State node rather than the current state.</summary>
            public bool fromAnyState;
            public float weight;
        }

        public static LayerPlayback Read(Animator animator, int layer)
        {
            var playback = new LayerPlayback();
            if (animator == null || !animator.isInitialized) return playback;
            if (layer < 0 || layer >= animator.layerCount) return playback;

            var current = animator.GetCurrentAnimatorStateInfo(layer);
            playback.valid = true;
            playback.stateHash = current.fullPathHash;
            playback.progress = Position(current);
            playback.weight = Weight(animator, layer);

            if (!animator.IsInTransition(layer)) return playback;
            var transition = animator.GetAnimatorTransitionInfo(layer);
            playback.inTransition = true;
            playback.nextStateHash = animator.GetNextAnimatorStateInfo(layer).fullPathHash;
            playback.transitionProgress = Mathf.Clamp01(transition.normalizedTime);
            playback.fromAnyState = transition.anyState;
            return playback;
        }

        /// <summary>What a layer is actually mixed in at. The base layer is forced to full
        /// weight at runtime whatever its field says, which is why it is not simply read.</summary>
        public static float Weight(Animator animator, int layer)
        {
            if (animator == null || layer < 0 || layer >= animator.layerCount) return 0f;
            return layer == 0 ? 1f : animator.GetLayerWeight(layer);
        }

        /// <summary>Where the state is inside its clip. normalizedTime keeps counting past 1,
        /// so a looping state wraps and one that plays once stops at the end instead of
        /// starting over.</summary>
        static float Position(AnimatorStateInfo info) =>
            info.loop
                ? Mathf.Repeat(info.normalizedTime, 1f)
                : Mathf.Clamp01(info.normalizedTime);

        /// <summary>
        /// The hash Unity reports as <c>AnimatorStateInfo.fullPathHash</c>: every state machine
        /// from the layer's root down to the one holding the state, then the state, joined with
        /// dots.
        ///
        /// The root contributes its OWN name, not the layer's. The two look interchangeable
        /// because Unity names a new layer's root machine after the layer — but renaming one
        /// does not rename the other, and it is the machine's name that ends up in the path.
        /// Pinned by <c>AnimatorPlaybackTests</c>, which renames the machine and asks Unity.
        /// </summary>
        /// <param name="machinePath">Root state machine first, the one holding the state last —
        /// the shape <see cref="DaerDContext.StateMachinePath"/> keeps.</param>
        public static int FullPathHash(IList<AnimatorStateMachine> machinePath, string stateName)
        {
            var path = new StringBuilder();
            for (int i = 0; i < (machinePath?.Count ?? 0); i++)
            {
                if (machinePath[i] == null) continue;
                if (path.Length > 0) path.Append('.');
                path.Append(machinePath[i].name);
            }
            if (path.Length > 0) path.Append('.');
            return Animator.StringToHash(path.Append(stateName).ToString());
        }
    }
}

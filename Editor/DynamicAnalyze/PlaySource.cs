using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// The reading face of something that is running a controller right now — one shape of
    /// question, whatever is answering it.
    ///
    /// There are two answerers and they have no type in common. A plain
    /// <see cref="UnityEngine.Animator"/> answers for an avatar that is simply playing, and an
    /// <see cref="AnimatorControllerPlayable"/> answers for one driven by a PlayableGraph —
    /// which is every avatar GestureManager or Av3Emulator has hold of, because both build the
    /// VRChat layer stack as a graph and the Animator component is only the graph's output.
    ///
    /// The two spell every method identically — GetLayerName, GetLayerWeight,
    /// GetCurrentAnimatorStateInfo, GetParameter — and Unity never made that into an interface.
    /// Measured rather than assumed: <c>AnimatorControllerPlayable</c> in 2022.3 implements
    /// <c>IPlayable</c> and <c>IEquatable</c> and nothing else, so there is no
    /// <c>IAnimatorControllerPlayable</c> to reach for and the resemblance cannot be used
    /// directly. This is that interface, written out — two forwarders of a dozen lines each,
    /// which is the cost of the recorder above not caring which of the two it has.
    ///
    /// Read-only on purpose. A recording is evidence, and a source that could also be written
    /// through would make it evidence of a run this window had been interfering with. Poking a
    /// running avatar is what a live SESSION is for, where the thing being poked is a copy of
    /// the avatar this window made and nobody else is looking at.
    /// </summary>
    abstract class PlaySource
    {
        /// <summary>Whether there is still anything behind this to read. A graph is destroyed
        /// when the tool that built it lets go of the avatar, and reading a destroyed one is
        /// not a thing the recorder is allowed to try.</summary>
        public abstract bool Alive { get; }

        public abstract int ParameterCount { get; }

        public abstract AnimatorControllerParameter ParameterAt(int index);

        public abstract int LayerCount { get; }

        public abstract string LayerName(int layer);

        public abstract float LayerWeight(int layer);

        /// <summary>The FULL path hash of the state this layer is in — the same number
        /// <see cref="StateTables"/> keys its labels by, and the reason a recording can name a
        /// state at all.</summary>
        public abstract int StateHash(int layer);

        public abstract bool InTransition(int layer);

        /// <summary>The full path hash of the transition this layer is blending through.
        /// Meaningless unless <see cref="InTransition"/> says there is one — measured, a settled
        /// layer answers 0.</summary>
        public abstract int TransitionHash(int layer);

        public abstract bool ReadBool(string parameter);

        public abstract float ReadFloat(string parameter);

        public abstract int ReadInt(string parameter);

        /// <summary>
        /// Every parameter as a number, whatever it is underneath — the same flattening
        /// <see cref="SimClient.Read"/> does, so a recorded row and a simulated row of the same
        /// name are the same kind of number and can be laid over each other.
        ///
        /// A Trigger is read as a Bool, which is all a poller can do: it is up between the frame
        /// something set it and the frame a transition consumes it, and this only ever looks
        /// once per frame. See <see cref="PlayRecorder"/> for what that costs.
        /// </summary>
        public float Read(string parameter, AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    return ReadBool(parameter) ? 1f : 0f;
                case AnimatorControllerParameterType.Int:
                    return ReadInt(parameter);
                default:
                    return ReadFloat(parameter);
            }
        }

        public static PlaySource Of(AnimatorControllerPlayable playable) =>
            new FromPlayable(playable);

        public static PlaySource Of(Animator animator) => new FromAnimator(animator);

        /// <summary>One layer of somebody's PlayableGraph — a controller running inside a stack
        /// of them, which is what an avatar under GestureManager or Av3Emulator is made of. The
        /// Animator component those graphs output to holds no controller of its own, so this is
        /// the only place the values exist.</summary>
        sealed class FromPlayable : PlaySource
        {
            readonly AnimatorControllerPlayable _playable;

            public FromPlayable(AnimatorControllerPlayable playable) { _playable = playable; }

            public override bool Alive => _playable.IsValid();

            public override int ParameterCount => _playable.GetParameterCount();

            public override AnimatorControllerParameter ParameterAt(int index) =>
                _playable.GetParameter(index);

            public override int LayerCount => _playable.GetLayerCount();

            public override string LayerName(int layer) => _playable.GetLayerName(layer);

            public override float LayerWeight(int layer) => _playable.GetLayerWeight(layer);

            public override int StateHash(int layer) =>
                _playable.GetCurrentAnimatorStateInfo(layer).fullPathHash;

            public override bool InTransition(int layer) => _playable.IsInTransition(layer);

            public override int TransitionHash(int layer) =>
                _playable.GetAnimatorTransitionInfo(layer).fullPathHash;

            public override bool ReadBool(string parameter) => _playable.GetBool(parameter);

            public override float ReadFloat(string parameter) => _playable.GetFloat(parameter);

            public override int ReadInt(string parameter) => _playable.GetInteger(parameter);
        }

        /// <summary>An Animator running a controller the ordinary way. The fallback, for an
        /// avatar nobody has wrapped in a graph — a prefab dropped into a scene and played, or
        /// a rig somebody is stepping by hand.</summary>
        sealed class FromAnimator : PlaySource
        {
            readonly Animator _animator;

            public FromAnimator(Animator animator) { _animator = animator; }

            public override bool Alive =>
                _animator != null && _animator.runtimeAnimatorController != null;

            public override int ParameterCount => _animator.parameterCount;

            public override AnimatorControllerParameter ParameterAt(int index) =>
                _animator.GetParameter(index);

            public override int LayerCount => _animator.layerCount;

            public override string LayerName(int layer) => _animator.GetLayerName(layer);

            public override float LayerWeight(int layer) => _animator.GetLayerWeight(layer);

            public override int StateHash(int layer) =>
                _animator.GetCurrentAnimatorStateInfo(layer).fullPathHash;

            public override bool InTransition(int layer) => _animator.IsInTransition(layer);

            public override int TransitionHash(int layer) =>
                _animator.GetAnimatorTransitionInfo(layer).fullPathHash;

            public override bool ReadBool(string parameter) => _animator.GetBool(parameter);

            public override float ReadFloat(string parameter) => _animator.GetFloat(parameter);

            public override int ReadInt(string parameter) => _animator.GetInteger(parameter);
        }
    }
}

using System;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// A generated controller on a real Animator, stepped by hand — the difference between
    /// reading what DaerD built and running it.
    ///
    /// Frames are the unit everything here is measured in. Mecanim evaluates a frame from the
    /// values it started with and writes the results at the end, so a blend tree child reading
    /// a parameter another child writes is always one frame behind it: a gadget's stages fill
    /// one frame at a time, and one built on feedback (the smoothings) has no settled value at
    /// all — its output is a function of the frame count. Tests therefore either step a fixed
    /// number of frames and expect an exact number, or step past the longest chain and expect
    /// one that has stopped moving.
    ///
    /// The host object is created outside the scene's save path and destroyed with the rig, so
    /// a test that throws leaves nothing behind for the next one.
    /// </summary>
    sealed class AnimatorRig : IDisposable
    {
        /// <summary>The frame length every step uses. Only the gadgets that read the clock care
        /// what it is; for the rest it is just "one frame".</summary>
        public const float Dt = 1f / 60f;

        readonly GameObject _host;
        readonly Animator _animator;

        /// <summary>The Animator's own GameObject — the root the animated paths are relative
        /// to, and where a test that animates a hierarchy hangs its objects.</summary>
        public GameObject Root => _host;

        /// <param name="host">An existing hierarchy to drive, or null for a bare object. Either
        /// way the rig owns it and destroys it on <see cref="Dispose"/>.</param>
        public AnimatorRig(AnimatorController controller, GameObject host = null)
        {
            _host = host != null ? host : new GameObject("DaerD Runtime Rig");
            _host.hideFlags = HideFlags.DontSave;
            _animator = _host.AddComponent<Animator>();
            _animator.applyRootMotion = false;
            // Nothing is rendering in batch mode, so an animator that only animates when it is
            // on screen would never animate at all.
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _animator.runtimeAnimatorController = controller;
            // Parameters start at the defaults the controller declares — which is where the
            // Direct trees' constant "One" comes from.
            _animator.Rebind();
        }

        public AnimatorRig Set(string parameter, float value)
        {
            _animator.SetFloat(parameter, value);
            return this;
        }

        public AnimatorRig Set(string parameter, bool value)
        {
            _animator.SetBool(parameter, value);
            return this;
        }

        public float Get(string parameter) => _animator.GetFloat(parameter);

        public bool GetBool(string parameter) => _animator.GetBool(parameter);

        public AnimatorRig Step(int frames = 1)
        {
            return Step(frames, Dt);
        }

        /// <summary>Steps frames of a length of your choosing. Only a gadget reading the clock
        /// can tell the difference — which is exactly what makes it worth being able to ask.</summary>
        public AnimatorRig Step(int frames, float dt)
        {
            for (int i = 0; i < frames; i++) _animator.Update(dt);
            return this;
        }

        /// <summary>Whether the layer is mid-blend. With <see cref="Transition"/>, the only way
        /// to ask Mecanim what it calls the transition it is running — which is a question with
        /// no documented answer, so the tests that care measure it rather than assume it.</summary>
        public bool InTransition(int layer) => _animator.IsInTransition(layer);

        public AnimatorTransitionInfo Transition(int layer) =>
            _animator.GetAnimatorTransitionInfo(layer);

        /// <summary>The name of the state the layer is currently in — what a transition test is
        /// really asking about.</summary>
        public string CurrentState(int layer, params string[] candidates)
        {
            var info = _animator.GetCurrentAnimatorStateInfo(layer);
            foreach (var name in candidates)
                if (info.IsName(name)) return name;
            return null;
        }

        /// <summary>Sets the inputs, steps <paramref name="frames"/> frames, and reads one
        /// output back. For gadgets whose value stops moving once their stages have filled.</summary>
        public float Evaluate(string output, int frames, params (string name, float value)[] inputs)
        {
            foreach (var input in inputs) Set(input.name, input.value);
            Step(frames);
            return Get(output);
        }

        public void Dispose()
        {
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
        }
    }
}

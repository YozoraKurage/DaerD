using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Drives a frame-0 scene preview of the selected clip state. It uses the public
    /// <see cref="AnimationMode"/> API — the same one the Animation window's Preview is built on —
    /// so no reflection or patching is needed: <see cref="AnimationMode.SampleAnimationClip"/>
    /// poses the GameObject and <see cref="AnimationMode.StopAnimationMode"/> restores it.
    ///
    /// Previewing is active only while the toolbar Preview toggle is on AND the scene selection is
    /// a GameObject whose Animator runs the controller open in this window AND the window's
    /// selection is a state whose motion is an AnimationClip.
    /// </summary>
    class StatePreview
    {
        readonly DaerDContext _context;
        bool _enabled;

        /// <summary>True while this previewer (rather than another tool) holds animation mode.</summary>
        bool _owns;

        public StatePreview(DaerDContext context) => _context = context;

        public void Start()
        {
            Selection.selectionChanged += Evaluate;
            _context.SelectionChanged += Evaluate;
            _context.GraphRebuilt += Evaluate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public void Stop()
        {
            Selection.selectionChanged -= Evaluate;
            _context.SelectionChanged -= Evaluate;
            _context.GraphRebuilt -= Evaluate;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            StopPreview();
        }

        /// <summary>Turns previewing on or off; wired to the toolbar Preview toggle.</summary>
        public void SetEnabled(bool on)
        {
            _enabled = on;
            Evaluate();
        }

        void OnPlayModeChanged(PlayModeStateChange change) => Evaluate();

        /// <summary>Re-checks the preview conditions and starts, refreshes or stops accordingly.</summary>
        void Evaluate()
        {
            if (!_enabled
                || EditorApplication.isPlayingOrWillChangePlaymode
                || !TryGetTarget(out var go, out var clip))
            {
                StopPreview();
                return;
            }
            Sample(go, clip);
        }

        /// <summary>
        /// True when the scene selection is a GameObject whose Animator runs this window's
        /// controller and the window's selection is a state with an AnimationClip motion.
        /// </summary>
        bool TryGetTarget(out GameObject go, out AnimationClip clip)
        {
            go = null;
            clip = null;

            var controller = _context.Controller;
            if (controller == null) return false;

            var selected = Selection.activeGameObject;
            // Exclude project-asset prefabs; allow scene instances and the prefab stage.
            if (selected == null || !selected.scene.IsValid()) return false;

            var animator = selected.GetComponent<Animator>();
            if (animator == null || !ControllerMatches(animator.runtimeAnimatorController, controller))
                return false;

            if (!(_context.Selection is AnimatorState state)) return false;
            if (!(state.motion is AnimationClip motionClip)) return false;

            go = selected;
            clip = motionClip;
            return true;
        }

        void Sample(GameObject go, AnimationClip clip)
        {
            // Another tool (typically the Animation window) already owns animation mode: leave it be.
            if (AnimationMode.InAnimationMode() && !_owns)
                return;

            if (!AnimationMode.InAnimationMode())
            {
                AnimationMode.StartAnimationMode();
                _owns = true;
            }

            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(go, clip, 0f);
            AnimationMode.EndSampling();
            SceneView.RepaintAll();
        }

        void StopPreview()
        {
            if (_owns && AnimationMode.InAnimationMode())
                AnimationMode.StopAnimationMode();
            _owns = false;
        }

        static bool ControllerMatches(RuntimeAnimatorController runtime, AnimatorController controller)
        {
            if (runtime == controller) return true;
            return runtime is AnimatorOverrideController over && over.runtimeAnimatorController == controller;
        }
    }
}

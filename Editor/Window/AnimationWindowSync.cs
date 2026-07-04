using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Pushes the AnimationClip of the currently selected State into Unity's Animation window
    /// so the timeline immediately shows that clip. Driven from the toolbar "Select Sync" toggle —
    /// the user opts in because changing the Animation window's clip side-effects whatever they
    /// were doing in it. Reflection internals live in <see cref="AnimationWindowAccess"/>.
    /// </summary>
    class AnimationWindowSync
    {
        readonly DaerDContext _context;
        bool _enabled;

        public AnimationWindowSync(DaerDContext context) => _context = context;

        public void Start()
        {
            _context.SelectionChanged += Sync;
            _context.GraphRebuilt += Sync;
            _context.ControllerChanged += Sync;
        }

        public void Stop()
        {
            _context.SelectionChanged -= Sync;
            _context.GraphRebuilt -= Sync;
            _context.ControllerChanged -= Sync;
        }

        public void SetEnabled(bool on)
        {
            _enabled = on;
            if (!on) return;
            // Open the Animation window now so the user sees the sync land on the very first
            // State they click. A freshly-created window hasn't run its OnEnable cycle yet,
            // so defer the first Sync() by a frame in that case.
            bool wasOpen = AnimationWindowAccess.FindOpen() != null;
            AnimationWindowAccess.EnsureOpen();
            if (wasOpen) Sync();
            else EditorApplication.delayCall += () => { if (_enabled) Sync(); };
        }

        void Sync()
        {
            if (!_enabled) return;
            if (!(_context.Selection is AnimatorState state)) return;
            if (!(state.motion is AnimationClip clip)) return;
            // Only push to an already-open window — opening one mid-selection would steal
            // focus and surprise the user. SetEnabled opens it once on opt-in instead.
            var window = AnimationWindowAccess.FindOpen();
            if (window == null) return;
            AnimationWindowAccess.TrySetClip(window, clip);
        }
    }
}

using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Drives Unity's Animation-window Preview from the DaerD State selection: every time the
    /// user picks a different clip-state in DaerD, the Animation window's Preview is briefly
    /// toggled off and on so it re-acquires against the new clip and starts driving the scene
    /// pose. Wired to the toolbar "Preview" toggle — opt-in because the AnimationWindow's
    /// previewing mode side-effects whatever the user had it doing.
    ///
    /// Premise: Anim Sync is enabled. Pushing the new clip into the AnimationWindow is
    /// Anim Sync's job — Preview just re-toggles previewing once the clip is in place. The
    /// toolbar's Preview switch auto-enables Anim Sync to keep this invariant true.
    /// </summary>
    class StatePreview
    {
        readonly DaerDContext _context;
        bool _enabled;

        /// <summary>True while we started the Animation window's preview; used so toggling
        /// our switch OFF only stops a preview we ourselves turned on.</summary>
        bool _owns;

        public StatePreview(DaerDContext context) => _context = context;

        public void Start()
        {
            _context.SelectionChanged += Evaluate;
            _context.GraphRebuilt += Evaluate;
            _context.ControllerChanged += Evaluate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public void Stop()
        {
            _context.SelectionChanged -= Evaluate;
            _context.GraphRebuilt -= Evaluate;
            _context.ControllerChanged -= Evaluate;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            StopOurPreview();
        }

        /// <summary>Turns the auto-Preview-toggle on or off; wired to the toolbar Preview toggle.</summary>
        public void SetEnabled(bool on)
        {
            _enabled = on;
            if (!on)
            {
                StopOurPreview();
                return;
            }
            // Auto-open the Animation window so the very first State click lands somewhere
            // visible — mirrors the Anim Sync toggle.
            AnimationWindowAccess.EnsureOpen();
            Evaluate();
        }

        void OnPlayModeChanged(PlayModeStateChange change) => Evaluate();

        void Evaluate()
        {
            if (!_enabled || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                StopOurPreview();
                return;
            }
            if (!(_context.Selection is AnimatorState state)
                || !(state.motion is AnimationClip clip))
            {
                StopOurPreview();
                return;
            }

            // Anim Sync (registered before us on the same SelectionChanged event) has already
            // pushed `clip` into the AnimationWindow. The previewing toggle only sees the new
            // clip once the window has run an OnGUI pass and rebuilt its controlInterface
            // against the new selection — which is one Repaint plus one more editor tick
            // after the clip push. Chain two delayCall hops so we land after that.
            EditorApplication.delayCall += () =>
            {
                EditorApplication.delayCall += () => TogglePreviewFor(clip);
            };
        }

        void TogglePreviewFor(AnimationClip clip)
        {
            if (!_enabled) return;
            // Bail if the user has already moved on — the next selection's own delayCall
            // will handle the right clip.
            if (!(_context.Selection is AnimatorState state) || state.motion != clip) return;

            var window = AnimationWindowAccess.FindOpen();
            if (window == null) return;

            // OFF then ON so an already-previewing window re-acquires against the new clip;
            // the setter is idempotent (false-when-already-false is a no-op) so the first
            // call only does work when something was previewing before.
            AnimationWindowAccess.TrySetPreviewing(window, false);
            AnimationWindowAccess.TrySetPreviewing(window, true);
            _owns = true;
        }

        void StopOurPreview()
        {
            if (!_owns) return;
            var window = AnimationWindowAccess.FindOpen();
            if (window != null) AnimationWindowAccess.TrySetPreviewing(window, false);
            _owns = false;
        }
    }
}

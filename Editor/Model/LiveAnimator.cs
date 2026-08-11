using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The Animator in the scene that is actually running the controller being edited, and the
    /// parameter values it holds right now. The controller asset knows only what a parameter
    /// starts at; everything a gadget computes lives here and nowhere else.
    ///
    /// Reading is guarded by the names the running controller really declares. Asking an
    /// Animator for a parameter it does not have is not a silent miss — Unity logs an error per
    /// call, which at repaint rate fills the console. The gap is normal rather than exotic:
    /// avatar tooling swaps in built or merged controllers, so the thing running is often not
    /// the asset in the project, and <see cref="Runs"/> is what decides whether it counts as
    /// the same controller at all.
    /// </summary>
    class LiveAnimator
    {
        /// <summary>An Animator the user chose by hand. Beats every other candidate for as long
        /// as it lives and still runs the controller — the scene may hold several, and the one
        /// being debugged is not always the one selected.</summary>
        public Animator Pinned
        {
            get => _pinned;
            set { _pinned = value; _resolvedFor = null; }
        }

        Animator _pinned;

        // What the standing answer was worked out from. Resolving means a scene-wide search for
        // Animators, and the poll runs ten times a second for as long as the editor plays, so
        // the search happens when one of these stops being true rather than on every tick.
        AnimatorController _resolvedFor;
        GameObject _resolvedFrom;
        double _lastSearch;

        /// <summary>How long to leave a "nothing in this scene runs it" answer standing. An
        /// avatar can still be spawned mid-session, so the search is repeated — a second apart
        /// rather than ten times a second.</summary>
        const double SearchAgainAfter = 1.0;

        /// <summary>The Animator being read, or null when none was found.</summary>
        public Animator Current { get; private set; }

        /// <summary>More than one Animator runs this controller and nothing said which. Reading
        /// an arbitrary one would be worse than reading none, so nothing is read.</summary>
        public bool Ambiguous { get; private set; }

        /// <summary>What the running controller declares — the filter every read goes through.</summary>
        readonly Dictionary<string, AnimatorControllerParameterType> _present =
            new Dictionary<string, AnimatorControllerParameterType>();

        /// <summary>An Animator that has been bound and initialized: reads return real values.</summary>
        public bool IsLive => Current != null && Current.isInitialized;

        /// <summary>Re-resolves against the scene when the standing answer will not do. Called
        /// on a timer while the editor plays; outside play mode there is nothing to read.</summary>
        /// <param name="now">Editor time, passed in rather than read, so the one decision that
        /// depends on it can be tested.</param>
        public void Poll(AnimatorController controller, double now)
        {
            if (!EditorApplication.isPlaying) { Clear(); return; }
            Refresh(controller, Selection.activeGameObject, now);
        }

        /// <summary>The half of the poll that has nothing to do with the editor's state:
        /// searches when the standing answer will not do, and adopts what it finds. Separate
        /// because the tests never run inside play mode, and this is the part worth testing.</summary>
        public void Refresh(AnimatorController controller, GameObject selected, double now)
        {
            if (!NeedsSearch(controller, selected, now)) return;

            _resolvedFor = controller;
            _resolvedFrom = selected;
            _lastSearch = now;
            var resolution = Resolve(controller, _pinned, selected);
            // A pin that stopped matching (destroyed with the play session, or pointed at a
            // controller the user has since switched away from) is dropped rather than kept
            // around to override every later resolution. Assigned to the field, not the
            // property, whose setter would ask for yet another search.
            if (_pinned != null && resolution.animator != _pinned) _pinned = null;
            Bind(resolution.animator, resolution.ambiguous);
        }

        /// <summary>
        /// Whether the standing answer still holds. It does not when it was worked out for a
        /// different controller or a different selection, or when the Animator it named has
        /// been destroyed or now runs something else. An answer of "nothing" holds only
        /// briefly — see <see cref="SearchAgainAfter"/>.
        /// </summary>
        public bool NeedsSearch(AnimatorController controller, GameObject selected, double now)
        {
            if (!ReferenceEquals(controller, _resolvedFor)) return true;
            if (!ReferenceEquals(selected, _resolvedFrom)) return true;
            if (Current != null) return !Runs(Current, controller);
            return now - _lastSearch >= SearchAgainAfter;
        }

        public void Clear()
        {
            Current = null;
            Ambiguous = false;
            _present.Clear();
            _resolvedFor = null;
            _resolvedFrom = null;
        }

        /// <summary>Points the reader at an Animator and re-reads what it declares.</summary>
        public void Bind(Animator animator, bool ambiguous = false)
        {
            Ambiguous = ambiguous;
            // animator.parameters builds a fresh array every time it is asked; rebuilding the
            // same map from it on a timer is pure waste.
            if (ReferenceEquals(animator, Current) && animator != null
                && _present.Count == animator.parameterCount)
                return;

            Current = animator;
            _present.Clear();
            if (animator == null || animator.runtimeAnimatorController == null) return;
            foreach (var parameter in animator.parameters)
                _present[parameter.name] = parameter.type;
        }

        public struct Resolution
        {
            public Animator animator;
            public bool ambiguous;
        }

        /// <summary>
        /// Which Animator to read, in order: the pinned one, the one the user has selected (or
        /// that owns what they selected — clicking a mesh under an avatar should still count),
        /// then the scene's own answer if it is unambiguous.
        /// </summary>
        public static Resolution Resolve(AnimatorController controller, Animator pinned, GameObject selected)
        {
            var result = new Resolution();
            if (controller == null) return result;

            if (Runs(pinned, controller)) { result.animator = pinned; return result; }

            var owner = selected != null ? selected.GetComponentInParent<Animator>(true) : null;
            if (Runs(owner, controller)) { result.animator = owner; return result; }

            var all = FindAll(controller);
            if (all.Count == 1) result.animator = all[0];
            else result.ambiguous = all.Count > 1;
            return result;
        }

        /// <summary>True when this Animator plays <paramref name="controller"/>, directly or
        /// through however many override controllers are stacked on it.</summary>
        public static bool Runs(Animator animator, AnimatorController controller)
        {
            if (animator == null || controller == null) return false;
            var runtime = animator.runtimeAnimatorController;
            while (runtime is AnimatorOverrideController over) runtime = over.runtimeAnimatorController;
            return runtime == controller;
        }

        public static List<Animator> FindAll(AnimatorController controller)
        {
            var found = new List<Animator>();
            if (controller == null) return found;
            foreach (var animator in Object.FindObjectsOfType<Animator>(true))
                if (Runs(animator, controller)) found.Add(animator);
            return found;
        }

        // ---- reading -----------------------------------------------------------

        /// <summary>True when the running controller declares this exact name and type. Every
        /// read and write below is gated on it.</summary>
        public bool Has(string name, AnimatorControllerParameterType type) =>
            IsLive && name != null
            && _present.TryGetValue(name, out var actual) && actual == type;

        /// <summary>The animation system is driving this parameter right now — the runtime's
        /// own answer to what an AAP is, as opposed to DaerD's guess from reading the clips.</summary>
        public bool IsCurveDriven(string name) =>
            IsLive && name != null && _present.ContainsKey(name)
            && Current.IsParameterControlledByCurve(name);

        public float GetFloat(string name) =>
            Has(name, AnimatorControllerParameterType.Float) ? Current.GetFloat(name) : 0f;

        public int GetInt(string name) =>
            Has(name, AnimatorControllerParameterType.Int) ? Current.GetInteger(name) : 0;

        public bool GetBool(string name) =>
            Has(name, AnimatorControllerParameterType.Bool) && Current.GetBool(name);

        public void SetFloat(string name, float value)
        {
            if (Has(name, AnimatorControllerParameterType.Float)) Current.SetFloat(name, value);
        }

        public void SetInt(string name, int value)
        {
            if (Has(name, AnimatorControllerParameterType.Int)) Current.SetInteger(name, value);
        }

        public void SetBool(string name, bool value)
        {
            if (Has(name, AnimatorControllerParameterType.Bool)) Current.SetBool(name, value);
        }

        /// <summary>Triggers are momentary and consumed by the transition that reads them, so
        /// they are fired rather than shown — there is no steady value to display.</summary>
        public void FireTrigger(string name)
        {
            if (Has(name, AnimatorControllerParameterType.Trigger)) Current.SetTrigger(name);
        }
    }
}

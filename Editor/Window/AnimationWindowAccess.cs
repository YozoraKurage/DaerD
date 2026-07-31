using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Reflection-based accessors for Unity's internal <c>UnityEditor.AnimationWindow</c> and
    /// its inner AnimEditor / AnimationWindowState / AnimationWindowControl.
    ///
    /// Unity 2022.3 keeps the <c>previewing</c> properties read-only on every layer — the
    /// real toggle is via <c>StartPreview()</c> / <c>StopPreview()</c> methods on the
    /// AnimationWindowState (or its inner AnimationWindowControl), which is what the
    /// toolbar Preview button calls into. We walk AnimationWindow → m_AnimEditor → state
    /// and invoke those methods.
    ///
    /// All reflection failures are swallowed so a Unity-version drift can disable the
    /// feature but never crash DaerD. Set <see cref="VerboseLogging"/> true to trace the
    /// dispatch when the toggle silently fails.
    /// </summary>
    static class AnimationWindowAccess
    {
        public static bool VerboseLogging;

        const BindingFlags AllInstance =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        static Type s_animationWindowType;
        static PropertyInfo s_clipProp;
        static FieldInfo s_animEditorField;
        static bool s_clipResolved;

        public static Type AnimationWindowType
        {
            get
            {
                if (s_animationWindowType != null) return s_animationWindowType;
                s_animationWindowType = typeof(Editor).Assembly.GetType("UnityEditor.AnimationWindow");
                return s_animationWindowType;
            }
        }

        public static EditorWindow FindOpen()
        {
            var type = AnimationWindowType;
            if (type == null) return null;
            var open = Resources.FindObjectsOfTypeAll(type);
            return open.Length > 0 ? (EditorWindow)open[0] : null;
        }

        public static EditorWindow EnsureOpen()
        {
            var window = FindOpen();
            if (window != null) return window;
            var type = AnimationWindowType;
            if (type == null) return null;
            return EditorWindow.GetWindow(type, false, "Animation", true);
        }

        public static bool TrySetClip(EditorWindow window, AnimationClip clip)
        {
            if (window == null || clip == null) return false;

            // First try the public animationClip property. Its setter silently refuses when
            // the clip is not among the selected GameObject's clips — e.g. the user edits a
            // controller that is not on the selected Animator (a VRChat FX layer), or nothing
            // is selected at all. So verify via the getter instead of trusting the call, and
            // fall back to the window's internal state when it didn't stick.
            if (ResolveClipAccess())
            {
                try
                {
                    s_clipProp.SetValue(window, clip);
                    if ((AnimationClip)s_clipProp.GetValue(window) == clip)
                    {
                        window.Repaint();
                        return true;
                    }
                    if (VerboseLogging)
                        Debug.Log("[DaerD] animationClip setter refused (clip not on the selection); trying state fallback");
                }
                catch (Exception ex)
                {
                    if (VerboseLogging) Debug.LogWarning("[DaerD] TrySetClip threw: " + ex.Message);
                }
            }
            return TrySetClipViaState(window, clip);
        }

        /// <summary>
        /// Pushes the clip through AnimationWindowState instead of the public property.
        /// <c>state.activeAnimationClip</c> is what the window's own clip popup assigns —
        /// it only requires a root GameObject, not clip membership, so it accepts clips from
        /// controllers that aren't on the selected Animator. If even that refuses (no
        /// selection item yet), write the selection item's clip directly and let the state
        /// rebuild, mirroring what the state setter does after its own check.
        /// </summary>
        static bool TrySetClipViaState(EditorWindow window, AnimationClip clip)
        {
            var animEditor = ResolveAnimEditor(window);
            if (animEditor == null)
            {
                if (VerboseLogging) Debug.LogWarning("[DaerD] TrySetClipViaState: AnimEditor instance not reachable");
                return false;
            }
            var state = ResolveMember(animEditor, "state", "m_State");
            if (state == null)
            {
                if (VerboseLogging) Debug.LogWarning("[DaerD] TrySetClipViaState: AnimationWindowState not reachable");
                return false;
            }

            try
            {
                var stateClip = state.GetType().GetProperty("activeAnimationClip", AllInstance);
                if (stateClip != null && stateClip.CanWrite)
                {
                    stateClip.SetValue(state, clip);
                    if ((AnimationClip)stateClip.GetValue(state) == clip)
                    {
                        if (VerboseLogging) Debug.Log("[DaerD] clip set via AnimationWindowState.activeAnimationClip");
                        window.Repaint();
                        return true;
                    }
                }

                var selection = ResolveMember(state, "selection", "m_Selection");
                if (selection != null)
                {
                    var itemClip = selection.GetType().GetProperty("animationClip", AllInstance);
                    if (itemClip != null && itemClip.CanWrite)
                    {
                        itemClip.SetValue(selection, clip);
                        var refresh = state.GetType().GetMethod(
                            "OnSelectionChanged", AllInstance, null, Type.EmptyTypes, null);
                        refresh?.Invoke(state, null);
                        window.Repaint();
                        bool stuck = stateClip == null || (AnimationClip)stateClip.GetValue(state) == clip;
                        if (VerboseLogging)
                            Debug.Log("[DaerD] clip set via selection item (stuck=" + stuck + ")");
                        return stuck;
                    }
                }
            }
            catch (Exception ex)
            {
                if (VerboseLogging) Debug.LogWarning("[DaerD] TrySetClipViaState threw: " + ex.Message);
            }
            if (VerboseLogging) Debug.LogWarning("[DaerD] TrySetClip: every strategy failed " +
                "(is a GameObject selected for the Animation window?)");
            return false;
        }

        /// <summary>
        /// Starts or stops Unity's Animation-window Preview. The <c>previewing</c> properties
        /// on AnimEditor / AnimationWindowState / AnimationWindowControl are all read-only in
        /// 2022.3, so we drive the toggle via the <c>StartPreview()</c> / <c>StopPreview()</c>
        /// methods on the state (preferred — that's what the toolbar button calls) or fall
        /// back to controlInterface if the state methods are gone.
        /// </summary>
        public static bool TrySetPreviewing(EditorWindow window, bool on)
        {
            if (window == null)
            {
                if (VerboseLogging) Debug.LogWarning("[DaerD] TrySetPreviewing: window null");
                return false;
            }

            var animEditor = ResolveAnimEditor(window);
            if (animEditor == null)
            {
                if (VerboseLogging) Debug.LogWarning("[DaerD] TrySetPreviewing: AnimEditor instance not reachable");
                return false;
            }

            // Highest-impact targets first: the AnimationWindowState's StartPreview is what
            // the toolbar Preview button ends up calling. If that's gone in a future Unity
            // release, fall back through AnimEditor → controlInterface.
            var state = ResolveMember(animEditor, "state", "m_State");
            if (InvokePreviewMethod(state, on, "AnimationWindowState"))
            {
                window.Repaint();
                return true;
            }
            if (InvokePreviewMethod(animEditor, on, "AnimEditor"))
            {
                window.Repaint();
                return true;
            }
            var control = ResolveMember(animEditor, "controlInterface", null)
                          ?? (state != null ? ResolveMember(state, "controlInterface", "m_ControlInterface") : null);
            if (InvokePreviewMethod(control, on, "controlInterface"))
            {
                window.Repaint();
                return true;
            }

            if (VerboseLogging) Debug.LogWarning("[DaerD] TrySetPreviewing: no Start/StopPreview method found");
            return false;
        }

        static bool InvokePreviewMethod(object target, bool on, string label)
        {
            if (target == null) return false;
            var type = target.GetType();
            var name = on ? "StartPreview" : "StopPreview";
            var method = type.GetMethod(name, AllInstance, null, Type.EmptyTypes, null);
            if (method == null) return false;
            try
            {
                method.Invoke(target, null);
                if (VerboseLogging) Debug.Log($"[DaerD] previewing toggled via {label}.{name}() (on={on})");
                return true;
            }
            catch (Exception ex)
            {
                if (VerboseLogging) Debug.LogWarning($"[DaerD] {label}.{name}() threw: {ex.Message}");
                return false;
            }
        }

        static object ResolveAnimEditor(EditorWindow window)
        {
            if (s_animEditorField == null)
            {
                var type = AnimationWindowType;
                if (type != null) s_animEditorField = type.GetField("m_AnimEditor", AllInstance);
            }
            if (s_animEditorField == null) return null;
            try { return s_animEditorField.GetValue(window); }
            catch (Exception ex)
            {
                if (VerboseLogging) Debug.LogWarning("[DaerD] ResolveAnimEditor field read failed: " + ex.Message);
                return null;
            }
        }

        static object ResolveMember(object target, string propertyName, string fieldName)
        {
            if (target == null) return null;
            var type = target.GetType();
            if (propertyName != null)
            {
                var prop = type.GetProperty(propertyName, AllInstance);
                if (prop != null)
                {
                    try { var v = prop.GetValue(target); if (v != null) return v; }
                    catch { /* fall through */ }
                }
            }
            if (fieldName != null)
            {
                var field = type.GetField(fieldName, AllInstance);
                if (field != null)
                {
                    try { return field.GetValue(target); }
                    catch { /* fall through */ }
                }
            }
            return null;
        }

        static bool ResolveClipAccess()
        {
            if (s_clipResolved) return s_clipProp != null;
            s_clipResolved = true;
            var type = AnimationWindowType;
            if (type == null) return false;
            s_clipProp = type.GetProperty("animationClip", AllInstance);
            return s_clipProp != null;
        }
    }
}

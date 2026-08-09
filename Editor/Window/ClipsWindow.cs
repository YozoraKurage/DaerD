using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Standalone clip index window: a shell around <see cref="ClipsForm"/>, which the home
    /// screen embeds as well. Lives outside the main DaerD window so the list stays visible
    /// while the user edits the graph — which is also what decides where its Jump goes: the
    /// window has no context of its own, so it opens (or raises) the main window on the usage.
    /// </summary>
    class ClipsWindow : EditorWindow
    {
        [SerializeField] AnimatorController _controller;

        readonly ClipsForm _form = new ClipsForm();
        Vector2 _scroll;

        public static ClipsWindow Open(AnimatorController controller)
        {
            var window = GetWindow<ClipsWindow>();
            window.minSize = new Vector2(380, 220);
            if (controller != null)
                window.SetController(controller);
            window.Show();
            window.Focus();
            return window;
        }

        void SetController(AnimatorController controller)
        {
            _controller = controller;
            _form.SetController(controller);
        }

        void OnEnable()
        {
            ApplyTitle();
            _form.JumpRequested = usage =>
                DaerDWindow.Open(_controller).TryNavigateTo(usage.layerIndex, usage.stateMachinePath, usage.state);
            // Node labels in the graph show motion names — let DaerD pick a bulk replace up.
            _form.ControllerModified = () =>
            {
                foreach (var window in Resources.FindObjectsOfTypeAll<DaerDWindow>())
                    window.OnControllerModifiedExternally(_controller);
            };
            Undo.undoRedoPerformed += OnControllerPossiblyChanged;
            L.LanguageChanged += OnLanguageChanged;
            // The usage list doesn't survive a domain reload — rebuild it for the
            // remembered controller.
            _form.SetController(_controller);
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnControllerPossiblyChanged;
            L.LanguageChanged -= OnLanguageChanged;
        }

        /// <summary>Edits made elsewhere (main window, undo) invalidate the index; re-collect.</summary>
        void OnControllerPossiblyChanged()
        {
            _form.RefreshEntries();
            Repaint();
        }

        // Cheap enough to re-collect whenever the user comes back from editing in another
        // window, so the list never shows clips that were swapped out meanwhile.
        void OnFocus() => _form.RefreshEntries();

        void OnLanguageChanged()
        {
            ApplyTitle();
            Repaint();
        }

        void ApplyTitle() => titleContent = new GUIContent(L.Tr("DaerD Clips"));

        void OnGUI()
        {
            _form.DrawHeader(withControllerSlot: true);
            // The controller slot inside the header can pick another one; keep the serialized
            // field (and so the Jump target) on whatever the form is showing.
            _controller = _form.Controller;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            _form.DrawList();
            EditorGUILayout.EndScrollView();
        }
    }
}

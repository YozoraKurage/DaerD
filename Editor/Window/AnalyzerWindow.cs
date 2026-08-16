using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Standalone analysis window: a shell around <see cref="AnalyzerForm"/>, which the home
    /// screen embeds as well. Lives outside the main DaerD window so the report stays visible
    /// while the user edits the graph — which is also what decides where its Ping goes: the
    /// window has no context of its own, so it opens (or raises) the main window on the issue.
    /// </summary>
    class AnalyzerWindow : EditorWindow
    {
        [SerializeField] AnimatorController _controller;

        readonly AnalyzerForm _form = new AnalyzerForm();
        Vector2 _scroll;

        public static AnalyzerWindow Open(AnimatorController controller)
        {
            var window = GetWindow<AnalyzerWindow>();
            window.minSize = new Vector2(420, 240);
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
            _form.FocusRequested = issue => DaerDWindow.Open(_controller).TryFocusIssue(issue);
            // A fix may have deleted a parameter or a transition — let any DaerD window
            // showing this controller pick that up.
            _form.ControllerModified = () =>
            {
                foreach (var window in Resources.FindObjectsOfTypeAll<DaerDWindow>())
                    window.OnControllerModifiedExternally(_controller);
                Repaint();
            };
            Undo.undoRedoPerformed += OnControllerPossiblyChanged;
            L.LanguageChanged += OnLanguageChanged;
            // The issue list (and the fix delegates captured in it) doesn't survive a domain
            // reload — rebuild it for the remembered controller.
            _form.SetController(_controller);
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnControllerPossiblyChanged;
            L.LanguageChanged -= OnLanguageChanged;
        }

        /// <summary>Edits made elsewhere (main window, undo) invalidate the report; re-run.</summary>
        void OnControllerPossiblyChanged()
        {
            _form.Reanalyze();
            Repaint();
        }

        // Analysis is cheap enough to re-run whenever the user comes back from editing in
        // another window, so the report never shows already-fixed issues.
        void OnFocus() => _form.Reanalyze();

        void OnLanguageChanged()
        {
            ApplyTitle();
            _form.Reanalyze();   // issue messages are baked at analysis time
            Repaint();
        }

        void ApplyTitle() => titleContent = new GUIContent(L.Tr("DD StaticAnalyze"));

        void OnGUI()
        {
            _form.DrawHeader(withControllerSlot: true);
            // The controller slot inside the header can pick another one; keep the serialized
            // field (and so the Ping target) on whatever the form is showing.
            _controller = _form.Controller;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            _form.DrawReport();
            EditorGUILayout.EndScrollView();
        }
    }
}

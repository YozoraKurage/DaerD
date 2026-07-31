using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Standalone analysis window: audits a controller with <see cref="ControllerAnalyzer"/>
    /// and lists the issues with Ping / one-click-fix buttons. Lives outside the main DaerD
    /// window so the report stays visible while the user edits the graph.
    /// </summary>
    class AnalyzerWindow : EditorWindow
    {
        [SerializeField] AnimatorController _controller;

        List<ControllerAnalyzer.Issue> _issues;
        Vector2 _scroll;
        // Ping / Fix are recorded during the row draw and executed after the layout pass
        // ends — both rebuild state under the current IMGUI layout otherwise.
        ControllerAnalyzer.Issue _pendingPing;
        ControllerAnalyzer.Issue _pendingFix;

        // Severity filter. Session-static: shared by every window, reset on domain reload —
        // a per-user display preference isn't worth an EditorPref.
        static bool s_showErrors = true, s_showWarnings = true, s_showInfo = true;

        static readonly ControllerAnalyzer.Severity[] SeverityOrder =
        {
            ControllerAnalyzer.Severity.Error,
            ControllerAnalyzer.Severity.Warning,
            ControllerAnalyzer.Severity.Info,
        };

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
            Reanalyze();
        }

        void Reanalyze()
        {
            _issues = _controller != null ? ControllerAnalyzer.Analyze(_controller) : null;
        }

        void OnEnable()
        {
            ApplyTitle();
            Undo.undoRedoPerformed += OnControllerPossiblyChanged;
            L.LanguageChanged += OnLanguageChanged;
            // The issue list (and the fix delegates captured in it) doesn't survive a domain
            // reload — rebuild it for the remembered controller.
            Reanalyze();
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnControllerPossiblyChanged;
            L.LanguageChanged -= OnLanguageChanged;
        }

        /// <summary>Edits made elsewhere (main window, undo) invalidate the report; re-run.</summary>
        void OnControllerPossiblyChanged()
        {
            Reanalyze();
            Repaint();
        }

        // Analysis is cheap enough to re-run whenever the user comes back from editing in
        // another window, so the report never shows already-fixed issues.
        void OnFocus() => Reanalyze();

        void OnLanguageChanged()
        {
            ApplyTitle();
            Reanalyze();   // issue messages are baked at analysis time
            Repaint();
        }

        void ApplyTitle() => titleContent = new GUIContent(L.Tr("DaerD Analyzer"));

        void OnGUI()
        {
            DrawHeader();

            if (_controller == null)
            {
                EditorGUILayout.HelpBox(
                    L.Tr("Assign an Animator Controller to analyze."), MessageType.Info);
                return;
            }
            if (_issues == null) return;

            if (_issues.Count == 0)
            {
                EditorGUILayout.HelpBox(L.Tr("No issues found."), MessageType.Info);
                return;
            }

            DrawFilter();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            bool anyShown = false;
            // Errors first — the filter toggles double as a legend, so keep the same order here.
            foreach (var severity in SeverityOrder)
            {
                if (!IsSeverityShown(severity)) continue;
                foreach (var issue in _issues)
                {
                    if (issue.severity != severity) continue;
                    anyShown = true;
                    DrawIssueRow(issue);
                }
            }
            if (!anyShown)
                EditorGUILayout.HelpBox(L.Tr("All {0} issue(s) are hidden by the filter above.", _issues.Count),
                    MessageType.None);
            EditorGUILayout.EndScrollView();

            RunPendingActions();
        }

        void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            var picked = (AnimatorController)EditorGUILayout.ObjectField(
                _controller, typeof(AnimatorController), false);
            if (picked != _controller)
                SetController(picked);
            using (new EditorGUI.DisabledScope(_controller == null))
            {
                if (GUILayout.Button(new GUIContent(L.Tr("Analyze"),
                        L.Tr("Audit this controller for unused parameters, broken conditions, unreachable states and more.")),
                        GUILayout.Width(80)))
                    Reanalyze();
            }
            EditorGUILayout.EndHorizontal();
        }

        static bool IsSeverityShown(ControllerAnalyzer.Severity severity) =>
            severity == ControllerAnalyzer.Severity.Error ? s_showErrors
            : severity == ControllerAnalyzer.Severity.Warning ? s_showWarnings
            : s_showInfo;

        /// <summary>Toggle row with per-severity counts, plus the copy-report button.</summary>
        void DrawFilter()
        {
            int errors = 0, warnings = 0, infos = 0;
            foreach (var issue in _issues)
            {
                if (issue.severity == ControllerAnalyzer.Severity.Error) errors++;
                else if (issue.severity == ControllerAnalyzer.Severity.Warning) warnings++;
                else infos++;
            }

            EditorGUILayout.BeginHorizontal();
            s_showErrors = GUILayout.Toggle(s_showErrors, L.Tr("{0} error(s)", errors), EditorStyles.miniButtonLeft);
            s_showWarnings = GUILayout.Toggle(s_showWarnings, L.Tr("{0} warning(s)", warnings), EditorStyles.miniButtonMid);
            s_showInfo = GUILayout.Toggle(s_showInfo, L.Tr("{0} info", infos), EditorStyles.miniButtonRight);
            if (GUILayout.Button(new GUIContent(L.Tr("Copy"), L.Tr("Copy the full report to the clipboard")),
                    EditorStyles.miniButton, GUILayout.Width(60)))
                CopyIssueReport();
            EditorGUILayout.EndHorizontal();
        }

        void DrawIssueRow(ControllerAnalyzer.Issue issue)
        {
            var messageType = issue.severity == ControllerAnalyzer.Severity.Error ? MessageType.Error
                : issue.severity == ControllerAnalyzer.Severity.Warning ? MessageType.Warning
                : MessageType.None;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.HelpBox(
                "[" + ControllerAnalyzer.CategoryLabel(issue.kind) + "] " + issue.message, messageType);
            var buttons = new GUILayoutOption[] { GUILayout.Width(46), GUILayout.Height(issue.fix != null ? 19 : 38) };
            if (issue.fix != null || issue.context != null)
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(46));
                if (issue.context != null && GUILayout.Button(
                        new GUIContent(L.Tr("Ping"), L.Tr("Highlight this object in the Project / graph")), buttons))
                    _pendingPing = issue;
                if (issue.fix != null && GUILayout.Button(
                        new GUIContent(issue.fixLabel, issue.fixTooltip), buttons))
                    _pendingFix = issue;
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }

        void RunPendingActions()
        {
            if (_pendingPing != null)
            {
                var issue = _pendingPing;
                _pendingPing = null;
                PingIssue(issue);
            }
            if (_pendingFix != null)
            {
                var issue = _pendingFix;
                _pendingFix = null;
                ApplyIssueFix(issue);
            }
        }

        /// <summary>
        /// Opens (or focuses) the DaerD window on this controller and navigates its graph to
        /// the issue's object; anything unlocatable falls back to the Project-window ping.
        /// </summary>
        void PingIssue(ControllerAnalyzer.Issue issue)
        {
            var window = DaerDWindow.Open(_controller);
            if (!window.TryFocusIssue(issue))
                EditorGUIUtility.PingObject(issue.context);
        }

        void ApplyIssueFix(ControllerAnalyzer.Issue issue)
        {
            issue.fix();
            Reanalyze();
            // A fix may have deleted a parameter or a transition — let any DaerD window
            // showing this controller pick that up.
            foreach (var window in Resources.FindObjectsOfTypeAll<DaerDWindow>())
                window.OnControllerModifiedExternally(_controller);
            Repaint();
        }

        /// <summary>Puts a plain-text version of every issue (ignoring the filter) on the clipboard.</summary>
        void CopyIssueReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(_controller.name + " (" + _issues.Count + ")");
            foreach (var severity in SeverityOrder)
                foreach (var issue in _issues)
                    if (issue.severity == severity)
                        sb.AppendLine(
                            $"[{issue.severity}] [{ControllerAnalyzer.CategoryLabel(issue.kind)}] {issue.message}");
            EditorGUIUtility.systemCopyBuffer = sb.ToString();
        }
    }
}

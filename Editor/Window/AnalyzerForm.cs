using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The analysis report itself, shared between the standalone window
    /// (<see cref="AnalyzerWindow"/>) and the home screen's inline card: the severity filter,
    /// the issue rows and their Ping / one-click-fix buttons. Same split as
    /// <see cref="AsyncSyncForm"/> — the host owns the window chrome and decides where a Ping
    /// lands, the form owns the report and the filter.
    /// </summary>
    class AnalyzerForm
    {
        AnimatorController _controller;
        List<AnalyzerIssue> _issues;
        // Ping / Fix are recorded during the row draw and executed after the layout pass
        // ends — both rebuild state under the current IMGUI layout otherwise.
        AnalyzerIssue _pendingPing;
        AnalyzerIssue _pendingFix;

        // Severity filter. Session-static: shared by every window, reset on domain reload —
        // a per-user display preference isn't worth an EditorPref.
        static bool s_showErrors = true, s_showWarnings = true, s_showInfo = true;

        static readonly IssueSeverity[] SeverityOrder =
        {
            IssueSeverity.Error,
            IssueSeverity.Warning,
            IssueSeverity.Info,
        };

        /// <summary>Where a Ping goes. The window opens the main window on the issue; the home
        /// screen is already inside one and navigates its own context instead. Returns false
        /// when the issue could not be located, and the form falls back to a Project ping.
        /// </summary>
        public Func<AnalyzerIssue, bool> FocusRequested;

        /// <summary>Raised after a fix rewrote the controller, for the host to tell whoever is
        /// showing that controller — a fix can delete a parameter or a transition.</summary>
        public Action ControllerModified;

        public AnimatorController Controller => _controller;

        public void SetController(AnimatorController controller)
        {
            _controller = controller;
            Reanalyze();
        }

        public void Reanalyze()
        {
            _issues = _controller != null ? ControllerAnalyzer.Analyze(_controller) : null;
        }

        /// <summary>The controller slot and the Analyze button. Separate from
        /// <see cref="DrawReport"/> because a host with its own scroll view keeps this above it;
        /// <paramref name="withControllerSlot"/> is off for a host that is already bound to a
        /// controller — see <see cref="ClipsForm.DrawHeader"/> for the same reasoning.</summary>
        public void DrawHeader(bool withControllerSlot)
        {
            EditorGUILayout.BeginHorizontal();
            if (withControllerSlot)
            {
                var picked = (AnimatorController)EditorGUILayout.ObjectField(
                    _controller, typeof(AnimatorController), false);
                if (picked != _controller)
                    SetController(picked);
            }
            using (new EditorGUI.DisabledScope(_controller == null))
            {
                if (GUILayout.Button(new GUIContent(L.Tr("Analyze"),
                        L.Tr("Audit this controller for unused parameters, broken conditions, unreachable states and more.")),
                        GUILayout.Width(80)))
                    Reanalyze();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>The filter, the issue rows and the deferred actions they queued.</summary>
        public void DrawReport()
        {
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

            RunPendingActions();
        }

        static bool IsSeverityShown(IssueSeverity severity) =>
            severity == IssueSeverity.Error ? s_showErrors
            : severity == IssueSeverity.Warning ? s_showWarnings
            : s_showInfo;

        /// <summary>Toggle row with per-severity counts, plus the copy-report button.</summary>
        void DrawFilter()
        {
            int errors = 0, warnings = 0, infos = 0;
            foreach (var issue in _issues)
            {
                if (issue.severity == IssueSeverity.Error) errors++;
                else if (issue.severity == IssueSeverity.Warning) warnings++;
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

        void DrawIssueRow(AnalyzerIssue issue)
        {
            var messageType = issue.severity == IssueSeverity.Error ? MessageType.Error
                : issue.severity == IssueSeverity.Warning ? MessageType.Warning
                : MessageType.None;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.HelpBox(
                "[" + ControllerAnalyzer.CategoryLabel(issue.kind) + "] " + issue.message, messageType);
            var buttons = new GUILayoutOption[] { GUILayout.Width(DaerDLayout.RowAction), GUILayout.Height(issue.fix != null ? 19 : 38) };
            if (issue.fix != null || issue.context != null)
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(DaerDLayout.RowAction));
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

        /// <summary>Navigates the host's graph to the issue's object; anything unlocatable falls
        /// back to the Project-window ping.</summary>
        void PingIssue(AnalyzerIssue issue)
        {
            if (FocusRequested == null || !FocusRequested(issue))
                EditorGUIUtility.PingObject(issue.context);
        }

        void ApplyIssueFix(AnalyzerIssue issue)
        {
            issue.fix();
            Reanalyze();
            ControllerModified?.Invoke();
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

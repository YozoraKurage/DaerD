using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Parameter-mapping step shown before a layer template import: each template parameter
    /// can be created under its own (editable) name or wired to an existing parameter of the
    /// same type. Also used by blend tree template imports (they share the snapshot format).
    /// </summary>
    class LayerTemplateImportWindow : EditorWindow
    {
        class Row
        {
            public LayerClipboard.ParameterSnapshot parameter;
            /// <summary>0 = create new (name editable); 1.. = _candidates[index-1].</summary>
            public int choice;
            public string newName;
            public string[] candidates;
        }

        AnimatorController _controller;
        string _title;
        List<LayerClipboard.ParameterSnapshot> _parameters;
        Action<Dictionary<string, string>> _onApplied;
        readonly List<Row> _rows = new List<Row>();
        Vector2 _scroll;

        /// <summary>onApplied receives templateName → chosenName for every parameter.</summary>
        public static void Open(AnimatorController controller, string title,
            List<LayerClipboard.ParameterSnapshot> parameters,
            Action<Dictionary<string, string>> onApplied)
        {
            if (parameters == null || parameters.Count == 0)
            {
                // Nothing to remap — import straight away.
                onApplied?.Invoke(new Dictionary<string, string>());
                return;
            }
            var window = CreateInstance<LayerTemplateImportWindow>();
            window.titleContent = new GUIContent(L.Tr("Import Template"));
            window.minSize = new Vector2(420, 220);
            window._controller = controller;
            window._title = title;
            window._parameters = parameters;
            window._onApplied = onApplied;
            window.BuildRows();
            window.ShowUtility();
        }

        void BuildRows()
        {
            _rows.Clear();
            foreach (var parameter in _parameters)
            {
                var candidates = new List<string>();
                foreach (var existing in _controller.parameters)
                    if (existing.type == parameter.type)
                        candidates.Add(existing.name);
                var row = new Row
                {
                    parameter = parameter,
                    newName = parameter.name,
                    candidates = candidates.ToArray(),
                };
                // A same-name same-type parameter is almost always the intended wiring.
                int index = candidates.IndexOf(parameter.name);
                row.choice = index >= 0 ? index + 1 : 0;
                _rows.Add(row);
            }
        }

        void OnGUI()
        {
            if (_controller == null)
            {
                Close();
                return;
            }

            EditorGUILayout.LabelField(_title, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                L.Tr("Wire each template parameter to an existing parameter or create it under a new name."),
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var row in _rows)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(row.parameter.name + "  (" + row.parameter.type + ")",
                    GUILayout.MinWidth(120));
                var labels = new string[row.candidates.Length + 1];
                labels[0] = L.Tr("Create new");
                for (int i = 0; i < row.candidates.Length; i++)
                    labels[i + 1] = row.candidates[i].Replace('/', '∕');
                row.choice = EditorGUILayout.Popup(Mathf.Clamp(row.choice, 0, labels.Length - 1), labels,
                    GUILayout.Width(140));
                if (row.choice == 0)
                    row.newName = EditorGUILayout.TextField(row.newName, GUILayout.MinWidth(80));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(DaerDLayout.DialogButton)))
                Close();
            if (GUILayout.Button(L.Tr("Import"), GUILayout.Width(DaerDLayout.DialogButton)))
            {
                var map = new Dictionary<string, string>();
                foreach (var row in _rows)
                    map[row.parameter.name] = row.choice == 0
                        ? (string.IsNullOrEmpty(row.newName) ? row.parameter.name : row.newName.Trim())
                        : row.candidates[row.choice - 1];
                _onApplied?.Invoke(map);
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}

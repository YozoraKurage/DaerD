using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Remaps the parameters a blend tree subtree uses: every referenced parameter gets a
    /// dropdown of the controller's Float parameters (blend and Direct weights are always
    /// Float). Only the subtree is touched — the rest of the controller keeps its wiring.
    /// </summary>
    class BlendTreeRemapWindow : EditorWindow
    {
        class Row
        {
            public string source;
            /// <summary>0 = keep; 1.. = _floats[choice-1].</summary>
            public int choice;
        }

        AnimatorController _controller;
        BlendTree _tree;
        Action _onApplied;
        string[] _floats = Array.Empty<string>();
        readonly List<Row> _rows = new List<Row>();
        Vector2 _scroll;

        public static void Open(AnimatorController controller, BlendTree tree, Action onApplied)
        {
            var window = CreateInstance<BlendTreeRemapWindow>();
            window.titleContent = new GUIContent(L.Tr("Remap Parameters"));
            window.minSize = new Vector2(380, 200);
            window._controller = controller;
            window._tree = tree;
            window._onApplied = onApplied;
            window.BuildRows();
            window.ShowUtility();
        }

        void BuildRows()
        {
            var floats = new List<string>();
            if (_controller != null)
                foreach (var parameter in _controller.parameters)
                    if (parameter.type == AnimatorControllerParameterType.Float)
                        floats.Add(parameter.name);
            _floats = floats.ToArray();

            _rows.Clear();
            var used = new List<string>(LayerClipboard.CollectBlendTreeParameterNames(_tree));
            used.Sort(StringComparer.Ordinal);
            foreach (var name in used)
                _rows.Add(new Row { source = name });
        }

        void OnGUI()
        {
            if (_controller == null || _tree == null)
            {
                Close();
                return;
            }
            if (_rows.Count == 0)
            {
                EditorGUILayout.HelpBox(L.Tr("This subtree references no parameters."), MessageType.Info);
                if (GUILayout.Button(L.Tr("Cancel"))) Close();
                return;
            }

            EditorGUILayout.LabelField(_tree.name, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                L.Tr("Pick a replacement for each parameter this subtree uses. 'Keep' leaves it unchanged."),
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var row in _rows)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(row.source, GUILayout.MinWidth(100));
                var labels = new string[_floats.Length + 1];
                labels[0] = L.Tr("Keep");
                for (int i = 0; i < _floats.Length; i++)
                    labels[i + 1] = _floats[i].Replace('/', '∕');
                row.choice = EditorGUILayout.Popup(Mathf.Clamp(row.choice, 0, labels.Length - 1), labels);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(DaerDLayout.DialogButton)))
                Close();
            if (GUILayout.Button(L.Tr("Apply"), GUILayout.Width(DaerDLayout.DialogButton)))
            {
                var map = new Dictionary<string, string>();
                foreach (var row in _rows)
                    if (row.choice > 0 && _floats[row.choice - 1] != row.source)
                        map[row.source] = _floats[row.choice - 1];
                if (map.Count > 0)
                    LayerParameterRemapper.RemapTree(_tree, map);
                _onApplied?.Invoke();
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}

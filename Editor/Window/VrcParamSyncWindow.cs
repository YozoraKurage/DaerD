using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Diff preview for "Sync VRC Parameters Asset": lists the entries that would be added
    /// (controller parameters missing from the asset, Triggers excluded) and removed (asset
    /// entries with no controller parameter), each with a checkbox, then rewrites the asset
    /// aligned to the controller's parameter order. Unchecked rows are left untouched.
    /// </summary>
    class VrcParamSyncWindow : EditorWindow
    {
        class Item
        {
            public string name;
            public VrcExpressionParameters.ValueType type;
            public float defaultValue;
            public bool selected = true;
        }

        AnimatorController _controller;
        UnityEngine.Object _asset;
        Action _onApplied;
        readonly List<Item> _adds = new List<Item>();
        readonly List<Item> _removes = new List<Item>();
        Vector2 _scroll;

        public static void Open(AnimatorController controller, UnityEngine.Object asset, Action onApplied)
        {
            var window = CreateInstance<VrcParamSyncWindow>();
            window.titleContent = new GUIContent(L.Tr("Sync VRC Parameters"));
            window.minSize = new Vector2(420, 300);
            window._controller = controller;
            window._asset = asset;
            window._onApplied = onApplied;
            window.BuildDiff();
            window.ShowUtility();
        }

        void BuildDiff()
        {
            _adds.Clear();
            _removes.Clear();
            var entries = VrcExpressionParameters.Read(_asset);
            var entryNames = new HashSet<string>();
            foreach (var entry in entries) entryNames.Add(entry.name);

            var controllerNames = new HashSet<string>();
            foreach (var parameter in _controller.parameters)
            {
                controllerNames.Add(parameter.name);
                var mapped = VrcExpressionParameters.MapType(parameter.type);
                if (mapped == null || entryNames.Contains(parameter.name)) continue;
                _adds.Add(new Item
                {
                    name = parameter.name,
                    type = mapped.Value,
                    defaultValue = parameter.type == AnimatorControllerParameterType.Float
                        ? parameter.defaultFloat
                        : parameter.type == AnimatorControllerParameterType.Int
                            ? parameter.defaultInt
                            : parameter.defaultBool ? 1f : 0f,
                });
            }
            foreach (var entry in entries)
                if (!controllerNames.Contains(entry.name))
                    _removes.Add(new Item { name = entry.name, type = entry.valueType });
        }

        void OnGUI()
        {
            if (_controller == null || _asset == null)
            {
                Close();
                return;
            }

            EditorGUILayout.HelpBox(
                L.Tr("Aligns the VRC expression parameters asset to this controller's parameter list and order. Unchecked rows are left untouched."),
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawSection(L.Tr("Add ({0})", _adds.Count), _adds, "+");
            DrawSection(L.Tr("Remove ({0})", _removes.Count), _removes, "−");
            if (_adds.Count == 0 && _removes.Count == 0)
                EditorGUILayout.HelpBox(L.Tr("Already in sync — applying only reorders entries."), MessageType.None);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(100)))
                Close();
            if (GUILayout.Button(L.Tr("Apply"), GUILayout.Width(100)))
            {
                Apply();
                _onApplied?.Invoke();
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }

        void DrawSection(string title, List<Item> items, string sign)
        {
            if (items.Count == 0) return;
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            foreach (var item in items)
            {
                EditorGUILayout.BeginHorizontal();
                item.selected = EditorGUILayout.ToggleLeft(
                    sign + " " + item.name + "  (" + item.type + ")", item.selected);
                EditorGUILayout.EndHorizontal();
            }
        }

        void Apply()
        {
            var entries = VrcExpressionParameters.Read(_asset);
            var byName = new Dictionary<string, VrcExpressionParameters.Entry>();
            foreach (var entry in entries) byName[entry.name] = entry;

            foreach (var item in _adds)
                if (item.selected && !byName.ContainsKey(item.name))
                    byName[item.name] = new VrcExpressionParameters.Entry
                    {
                        name = item.name,
                        valueType = item.type,
                        defaultValue = item.defaultValue,
                    };
            foreach (var item in _removes)
                if (item.selected)
                    byName.Remove(item.name);

            // Controller order first, then anything kept that the controller doesn't know.
            var ordered = new List<VrcExpressionParameters.Entry>();
            foreach (var parameter in _controller.parameters)
                if (byName.TryGetValue(parameter.name, out var entry))
                {
                    ordered.Add(entry);
                    byName.Remove(parameter.name);
                }
            foreach (var entry in entries)
                if (byName.Remove(entry.name))
                    ordered.Add(entry);

            Undo.RegisterCompleteObjectUndo(_asset, "Sync VRC Parameters");
            VrcExpressionParameters.WriteAll(_asset, ordered);
        }
    }
}

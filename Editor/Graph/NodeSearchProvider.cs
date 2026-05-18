using System;
using System.Collections.Generic;
using UnityEditor.Experimental.GraphView;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Search-window content for adding a node at the cursor position.</summary>
    class NodeSearchProvider : ScriptableObject, ISearchWindowProvider
    {
        Action<string, Vector2> _onSelect;
        Texture2D _blankIcon;

        public void Init(Action<string, Vector2> onSelect) => _onSelect = onSelect;

        public List<SearchTreeEntry> CreateSearchTree(SearchWindowContext context)
        {
            if (_blankIcon == null)
            {
                _blankIcon = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _blankIcon.SetPixel(0, 0, Color.clear);
                _blankIcon.Apply();
            }

            return new List<SearchTreeEntry>
            {
                new SearchTreeGroupEntry(new GUIContent("Create Node"), 0),
                new SearchTreeEntry(new GUIContent("State", _blankIcon)) { level = 1, userData = "state" },
                new SearchTreeEntry(new GUIContent("State With Selected Clip", _blankIcon)) { level = 1, userData = "state-clip" },
                new SearchTreeEntry(new GUIContent("Blend Tree State", _blankIcon)) { level = 1, userData = "state-blendtree" },
                new SearchTreeEntry(new GUIContent("Sub-State Machine", _blankIcon)) { level = 1, userData = "ssm" },
                new SearchTreeEntry(new GUIContent("Paste State(s)", _blankIcon)) { level = 1, userData = "paste" },
            };
        }

        public bool OnSelectEntry(SearchTreeEntry entry, SearchWindowContext context)
        {
            if (entry is SearchTreeGroupEntry) return false;
            _onSelect?.Invoke((string)entry.userData, context.screenMousePosition);
            return true;
        }
    }
}

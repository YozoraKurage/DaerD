using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    // ---- controller tabs -------------------------------------------------

    /// <summary>
    /// The row of open-controller tabs. The two lists behind it stay serialized on the window —
    /// they have to survive domain reloads — so this class holds them by reference and is the
    /// only thing that edits them, keeping the remembered layer of each tab aligned by index.
    /// </summary>
    class TabStrip
    {
        readonly List<AnimatorController> _controllers;
        readonly List<int> _layers;
        readonly Action<AnimatorController> _activate;
        readonly Action<AnimatorController> _close;

        /// <summary>The strip itself; the window parents it under the toolbar.</summary>
        public readonly VisualElement Bar = new VisualElement();

        public TabStrip(List<AnimatorController> controllers, List<int> layers,
            Action<AnimatorController> activate, Action<AnimatorController> close)
        {
            _controllers = controllers;
            _layers = layers;
            _activate = activate;
            _close = close;
            Bar.AddToClassList("dd-tabbar");
        }

        public bool Contains(AnimatorController controller) => _controllers.Contains(controller);

        /// <summary>Opens a tab for <paramref name="controller"/>, remembering its first layer.</summary>
        public void Add(AnimatorController controller)
        {
            _controllers.Add(controller);
            _layers.Add(0);
        }

        /// <summary>Closes the tab and returns the index it held, or -1 when it wasn't open.</summary>
        public int Remove(AnimatorController controller)
        {
            int index = _controllers.IndexOf(controller);
            if (index < 0) return -1;
            _controllers.RemoveAt(index);
            if (index < _layers.Count) _layers.RemoveAt(index);
            return index;
        }

        /// <summary>The tab to fall back to once the one at <paramref name="index"/> is gone.</summary>
        public AnimatorController NextAfter(int index) =>
            _controllers.Count > 0
                ? _controllers[Mathf.Clamp(index, 0, _controllers.Count - 1)]
                : null;

        /// <summary>Writes the active layer index back to the per-tab memory.</summary>
        public void Remember(AnimatorController controller, int layerIndex)
        {
            if (controller == null) return;
            int index = _controllers.IndexOf(controller);
            if (index < 0) return;
            while (_layers.Count <= index)
                _layers.Add(0);
            _layers[index] = layerIndex;
        }

        /// <summary>The layer this tab was last left on.</summary>
        public int Lookup(AnimatorController controller)
        {
            int index = _controllers.IndexOf(controller);
            if (index < 0 || index >= _layers.Count) return 0;
            return _layers[index];
        }

        /// <summary>Rebuilds the tab strip from the open-controller list, highlighting the active one.</summary>
        public void Refresh(AnimatorController active, int activeLayer)
        {
            // Drop the parallel layer entry for any controller that's been removed (deleted asset
            // or null reference) so the two lists stay aligned by index.
            for (int i = _controllers.Count - 1; i >= 0; i--)
            {
                if (_controllers[i] != null) continue;
                _controllers.RemoveAt(i);
                if (i < _layers.Count) _layers.RemoveAt(i);
            }
            if (active != null && !_controllers.Contains(active))
            {
                _controllers.Add(active);
                _layers.Add(activeLayer);
            }
            while (_layers.Count < _controllers.Count)
                _layers.Add(0);

            Bar.Clear();
            Bar.style.display = _controllers.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (var controller in _controllers)
            {
                var captured = controller;
                var tab = new VisualElement();
                tab.AddToClassList("dd-tab");
                if (controller == active) tab.AddToClassList("dd-tab--active");

                AddPrefabIcon(tab, controller);

                var label = new Label(controller.name) { tooltip = AssetDatabase.GetAssetPath(controller) };
                label.AddToClassList("dd-tab__label");
                tab.Add(label);

                var close = new Label("×") { tooltip = L.Tr("Close tab") };   // U+00D7, widely available
                close.AddToClassList("dd-tab__close");
                tab.Add(close);

                tab.RegisterCallback<MouseDownEvent>(evt =>
                {
                    if (evt.button == 0)
                    {
                        // The first click activates the tab (and rebuilds this bar); the second
                        // still arrives with clickCount 2 because UI Toolkit tracks click count
                        // per pointer, not per element.
                        if (evt.clickCount == 2) EditorGUIUtility.PingObject(captured);
                        else _activate(captured);
                        evt.StopPropagation();
                    }
                    else if (evt.button == 2) { _close(captured); evt.StopPropagation(); }   // middle-click
                });
                close.RegisterCallback<MouseDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    _close(captured);
                    evt.StopPropagation();   // don't also activate the tab
                });

                Bar.Add(tab);
            }
        }

        /// <summary>
        /// A prefab badge in front of the tab's name, for a controller whose pin resolves
        /// cleanly. Only for a healthy pin: the mark says "this one has a home and I can see
        /// it", and a badge that also appeared for a pin pointing at a deleted prefab would be
        /// saying the opposite of the truth in the place least able to explain itself. The
        /// broken states are named on the home screen, which has room for the sentence.
        ///
        /// The state is asked for on every rebuild of the strip, which is what
        /// <see cref="PrefabLinks.Status"/> is kept cheap for — reference resolution and one
        /// field read, never a sweep.
        /// </summary>
        static void AddPrefabIcon(VisualElement tab, AnimatorController controller)
        {
            var status = PrefabLinks.Status(controller);
            if (!status.IsHealthy) return;
            var icon = EditorGUIUtility.IconContent("Prefab Icon")?.image;
            if (icon == null) return;

            var image = new Image
            {
                image = icon,
                tooltip = L.Tr("Linked to the prefab '{0}' ({1})",
                    status.prefab.name, AssetDatabase.GetAssetPath(status.prefab)),
            };
            image.AddToClassList("dd-tab__icon");
            tab.Add(image);
        }
    }
}

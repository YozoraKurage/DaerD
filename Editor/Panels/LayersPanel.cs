using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Layer list plus settings for the selected layer.</summary>
    class LayersPanel : PanelBase
    {
        readonly ListReorder _reorder = new ListReorder();
        GUIContent _settingsIcon;

        /// <summary>The gear glyph used by the per-row settings button (lazy so the editor skin is ready).</summary>
        GUIContent SettingsIcon =>
            _settingsIcon ??= new GUIContent(EditorGUIUtility.IconContent("_Popup")) { tooltip = "Layer settings" };

        public LayersPanel(DaerDContext context) : base(context, "Layers")
        {
            context.ControllerChanged += Refresh;
            context.LayerChanged += Refresh;
            context.LayersChanged += Refresh;
        }

        protected override void DrawContent()
        {
            var controller = Context.Controller;
            var layers = controller.layers;

            _reorder.Begin();
            for (int i = 0; i < layers.Length; i++)
            {
                var rowRect = EditorGUILayout.BeginHorizontal();
                _reorder.DrawHandle();

                bool isCurrent = i == Context.LayerIndex;
                var prev = GUI.backgroundColor;
                if (isCurrent) GUI.backgroundColor = new Color(0.40f, 0.60f, 0.90f);
                if (GUILayout.Button(layers[i].name, EditorStyles.miniButton))
                    Context.SetLayer(i);
                GUI.backgroundColor = prev;

                // Direct-blend-tree-only layers (the WD-ON DBT idiom) get a small badge so
                // they read as "machinery, not motion" at a glance.
                if (ControllerAnalyzer.IsDirectBlendTreeOnlyLayer(layers[i]))
                    GUILayout.Label(new GUIContent("DBT",
                        L.Tr("Every state in this layer is a Direct blend tree")),
                        EditorStyles.centeredGreyMiniLabel, GUILayout.Width(28));

                // Gear button on the right edge opens this layer's settings popup, so the
                // settings always belong unambiguously to the clicked layer.
                if (GUILayout.Button(SettingsIcon, EditorStyles.miniButton, GUILayout.Width(26)))
                    PopupWindow.Show(GUILayoutUtility.GetLastRect(), new LayerSettingsPopup(this, i));

                EditorGUILayout.EndHorizontal();
                _reorder.Row(rowRect);
            }
            _reorder.End(MoveLayer);

            EditorGUILayout.Space(4);
            if (GUILayout.Button("+ Add Layer"))
                AddLayer();
        }

        /// <summary>Editable settings for one layer, anchored to its row's gear button.</summary>
        class LayerSettingsPopup : PopupWindowContent
        {
            readonly LayersPanel _panel;
            readonly int _index;

            public LayerSettingsPopup(LayersPanel panel, int index)
            {
                _panel = panel;
                _index = index;
            }

            public override Vector2 GetWindowSize() => new Vector2(300f, 184f);

            public override void OnGUI(Rect rect)
            {
                var controller = _panel.Context.Controller;
                var layers = controller != null ? controller.layers : null;
                if (layers == null || _index < 0 || _index >= layers.Length)
                {
                    editorWindow.Close();
                    return;
                }

                EditorGUILayout.LabelField("Layer Settings", EditorStyles.boldLabel);

                EditorGUI.BeginChangeCheck();
                string name = EditorGUILayout.DelayedTextField("Name", layers[_index].name);
                // The base layer always runs at weight 1; show the slider locked rather than hiding it.
                float weight;
                using (new EditorGUI.DisabledScope(_index == 0))
                    weight = EditorGUILayout.Slider("Weight",
                        _index == 0 ? 1f : layers[_index].defaultWeight, 0f, 1f);
                if (_index == 0) weight = 1f;
                var blending = (AnimatorLayerBlendingMode)EditorGUILayout.EnumPopup("Blending", layers[_index].blendingMode);
                var mask = (AvatarMask)EditorGUILayout.ObjectField("Mask", layers[_index].avatarMask, typeof(AvatarMask), false);
                bool ikPass = EditorGUILayout.Toggle("IK Pass", layers[_index].iKPass);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RegisterCompleteObjectUndo(controller, "Edit Layer");
                    layers[_index].name = string.IsNullOrEmpty(name) ? layers[_index].name : name;
                    layers[_index].defaultWeight = weight;
                    layers[_index].blendingMode = blending;
                    layers[_index].avatarMask = mask;
                    layers[_index].iKPass = ikPass;
                    controller.layers = layers;
                    EditorUtility.SetDirty(controller);
                    _panel.Context.NotifyLayersChanged();
                }

                EditorGUILayout.Space(6);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Duplicate"))
                {
                    var panel = _panel;
                    int index = _index;
                    editorWindow.Close();
                    // Deferred: the duplicate rebuilds the layer list and the popup is gone by then.
                    EditorApplication.delayCall += () => panel.DuplicateLayer(index);
                    GUIUtility.ExitGUI();
                }
                using (new EditorGUI.DisabledScope(layers.Length <= 1))
                {
                    if (GUILayout.Button("Delete"))
                    {
                        var panel = _panel;
                        int index = _index;
                        // Close before the confirmation dialog: the dialog steals focus, which
                        // would dismiss the popup mid-OnGUI and leave IMGUI in a broken state.
                        editorWindow.Close();
                        EditorApplication.delayCall += () => panel.DeleteLayer(index);
                        GUIUtility.ExitGUI();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        void AddLayer()
        {
            var controller = Context.Controller;
            Undo.RegisterCompleteObjectUndo(controller, "Add Layer");
            controller.AddLayer(MakeUniqueLayerName(controller, "New Layer"));
            var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1f;
            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            Context.NotifyLayersChanged();
            Context.SetLayer(controller.layers.Length - 1);
        }

        void DeleteLayer(int idx)
        {
            var controller = Context.Controller;
            if (controller == null || idx < 0 || idx >= controller.layers.Length) return;
            if (!EditorUtility.DisplayDialog("Delete Layer",
                "Delete layer '" + controller.layers[idx].name + "' and all of its states?", "Delete", "Cancel"))
                return;
            int current = Context.LayerIndex;
            Undo.RegisterCompleteObjectUndo(controller, "Delete Layer");
            controller.RemoveLayer(idx);
            EditorUtility.SetDirty(controller);
            Context.NotifyLayersChanged();
            // Keep showing the same layer the user was on; only fall back when it was the one deleted.
            int next = current == idx ? idx : current > idx ? current - 1 : current;
            Context.SetLayer(Mathf.Clamp(next, 0, controller.layers.Length - 1));
        }

        void MoveLayer(int from, int to)
        {
            var controller = Context.Controller;
            var layers = controller.layers;
            if (from < 0 || from >= layers.Length || to < 0 || to >= layers.Length || from == to)
                return;
            Undo.RegisterCompleteObjectUndo(controller, "Reorder Layers");
            var moved = layers[from];
            if (from < to)
                Array.Copy(layers, from + 1, layers, from, to - from);
            else
                Array.Copy(layers, to, layers, to + 1, from - to);
            layers[to] = moved;
            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            Context.NotifyLayersChanged();
            Context.SetLayer(to);
        }

        void DuplicateLayer(int idx)
        {
            var controller = Context.Controller;
            if (controller == null || idx < 0 || idx >= controller.layers.Length) return;
            using (new UndoScope("Duplicate Layer"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "Duplicate Layer");
                var src = controller.layers[idx];
                controller.AddLayer(MakeUniqueLayerName(controller, src.name + " Copy"));
                var layers = controller.layers;
                int newIdx = layers.Length - 1;
                layers[newIdx].defaultWeight = src.defaultWeight;
                layers[newIdx].blendingMode = src.blendingMode;
                layers[newIdx].avatarMask = src.avatarMask;
                layers[newIdx].iKPass = src.iKPass;
                controller.layers = layers;
                StateMachineCloner.Clone(src.stateMachine, controller.layers[newIdx].stateMachine,
                    out _, out var machineMap);
                // Frames / notes live in GraphFrameData keyed by state machine, separate from the
                // controller asset, so StateMachineCloner can't reach them. Mirror them now using
                // the source→copy state-machine map so every nested SM keeps its annotations.
                FrameInheritance.CarryOver(Context.Controller, machineMap);
                EditorUtility.SetDirty(controller);
            }
            Context.NotifyLayersChanged();
            Context.SetLayer(controller.layers.Length - 1);
        }

        static string MakeUniqueLayerName(AnimatorController controller, string baseName)
        {
            bool Taken(string n)
            {
                foreach (var l in controller.layers)
                    if (l.name == n) return true;
                return false;
            }
            if (!Taken(baseName)) return baseName;
            int i = 1;
            while (Taken(baseName + " " + i)) i++;
            return baseName + " " + i;
        }
    }
}

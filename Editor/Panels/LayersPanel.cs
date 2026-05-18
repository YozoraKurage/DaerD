using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Layer list plus settings for the selected layer.</summary>
    class LayersPanel : PanelBase
    {
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

            for (int i = 0; i < layers.Length; i++)
            {
                EditorGUILayout.BeginHorizontal();
                bool isCurrent = i == Context.LayerIndex;
                var prev = GUI.backgroundColor;
                if (isCurrent) GUI.backgroundColor = new Color(0.40f, 0.60f, 0.90f);
                if (GUILayout.Button(layers[i].name, EditorStyles.miniButton))
                    Context.SetLayer(i);
                GUI.backgroundColor = prev;

                using (new EditorGUI.DisabledScope(i == 0))
                {
                    if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(22)))
                        MoveLayer(i, i - 1);
                }
                using (new EditorGUI.DisabledScope(i == layers.Length - 1))
                {
                    if (GUILayout.Button("▼", EditorStyles.miniButtonRight, GUILayout.Width(22)))
                        MoveLayer(i, i + 1);
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("+ Add Layer"))
                AddLayer();

            EditorGUILayout.Space(8);
            DrawLayerSettings(controller, layers);
        }

        void DrawLayerSettings(AnimatorController controller, AnimatorControllerLayer[] layers)
        {
            int idx = Context.LayerIndex;
            if (idx < 0 || idx >= layers.Length) return;

            EditorGUILayout.LabelField("Layer Settings", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            string name = EditorGUILayout.DelayedTextField("Name", layers[idx].name);
            float weight = idx == 0 ? 1f : EditorGUILayout.Slider("Weight", layers[idx].defaultWeight, 0f, 1f);
            var blending = (AnimatorLayerBlendingMode)EditorGUILayout.EnumPopup("Blending", layers[idx].blendingMode);
            var mask = (AvatarMask)EditorGUILayout.ObjectField("Mask", layers[idx].avatarMask, typeof(AvatarMask), false);
            bool ikPass = EditorGUILayout.Toggle("IK Pass", layers[idx].iKPass);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RegisterCompleteObjectUndo(controller, "Edit Layer");
                layers[idx].name = string.IsNullOrEmpty(name) ? layers[idx].name : name;
                layers[idx].defaultWeight = weight;
                layers[idx].blendingMode = blending;
                layers[idx].avatarMask = mask;
                layers[idx].iKPass = ikPass;
                controller.layers = layers;
                EditorUtility.SetDirty(controller);
                Context.NotifyLayersChanged();
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Duplicate"))
                DuplicateLayer(idx);
            using (new EditorGUI.DisabledScope(layers.Length <= 1))
            {
                if (GUILayout.Button("Delete"))
                    DeleteLayer(idx);
            }
            EditorGUILayout.EndHorizontal();
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
            if (!EditorUtility.DisplayDialog("Delete Layer",
                "Delete layer '" + controller.layers[idx].name + "' and all of its states?", "Delete", "Cancel"))
                return;
            Undo.RegisterCompleteObjectUndo(controller, "Delete Layer");
            controller.RemoveLayer(idx);
            EditorUtility.SetDirty(controller);
            Context.NotifyLayersChanged();
            Context.SetLayer(Mathf.Clamp(idx, 0, controller.layers.Length - 1));
        }

        void MoveLayer(int from, int to)
        {
            var controller = Context.Controller;
            var layers = controller.layers;
            if (to < 0 || to >= layers.Length) return;
            Undo.RegisterCompleteObjectUndo(controller, "Reorder Layers");
            var moved = layers[from];
            layers[from] = layers[to];
            layers[to] = moved;
            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            Context.NotifyLayersChanged();
            Context.SetLayer(to);
        }

        void DuplicateLayer(int idx)
        {
            var controller = Context.Controller;
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
                StateMachineCloner.Clone(src.stateMachine, controller.layers[newIdx].stateMachine);
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

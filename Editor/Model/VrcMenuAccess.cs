using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Reads and edits a VRCExpressionsMenu asset in place. Matched by type name and accessed
    /// via SerializedObject so DaerD needs no VRCSDK reference. Controls are edited per
    /// property path (never rewritten wholesale) so fields DaerD doesn't model — label icons,
    /// future SDK additions — survive untouched.
    /// </summary>
    static class VrcMenuAccess
    {
        public const string AssetTypeName = "VRCExpressionsMenu";
        public const string DescriptorTypeName = "VRCAvatarDescriptor";
        public const int FallbackMaxControls = 8;

        /// <summary>VRC SDK control type ids.</summary>
        public enum ControlType
        {
            Button = 101,
            Toggle = 102,
            SubMenu = 103,
            TwoAxisPuppet = 201,
            FourAxisPuppet = 202,
            RadialPuppet = 203,
        }

        /// <summary>Read-only snapshot of one control for list / inspector rendering.</summary>
        public class Control
        {
            public string name;
            public Texture2D icon;
            public ControlType type;
            public string parameter;
            public float value;
            public Object subMenu;
            public List<string> subParameters = new List<string>();
            public List<string> labels = new List<string>();
        }

        public static bool Is(Object asset) =>
            asset != null && asset.GetType().Name == AssetTypeName;

        /// <summary>SubParameter slots a control type uses (horizontal/vertical, 4-way, radial).</summary>
        public static int SubParameterCount(ControlType type)
        {
            switch (type)
            {
                case ControlType.TwoAxisPuppet: return 2;
                case ControlType.FourAxisPuppet: return 4;
                case ControlType.RadialPuppet: return 1;
                default: return 0;
            }
        }

        /// <summary>Axis label slots a control type uses (up/right/down/left).</summary>
        public static int LabelCount(ControlType type) =>
            type == ControlType.TwoAxisPuppet || type == ControlType.FourAxisPuppet ? 4 : 0;

        public static int MaxControls(Object asset)
        {
            if (asset == null) return FallbackMaxControls;
            var field = asset.GetType().GetField("MAX_CONTROLS",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (field != null && field.FieldType == typeof(int))
                return (int)field.GetRawConstantValue();
            return FallbackMaxControls;
        }

        /// <summary>The menu asset of the avatar running this controller (same resolution
        /// rules as <see cref="VrcExpressionParameters.FindAssetFor"/>).</summary>
        public static Object FindMenuFor(AnimatorController controller)
        {
            if (controller == null) return null;
            System.Type descriptorType = null;
            foreach (var type in TypeCache.GetTypesDerivedFrom<MonoBehaviour>())
                if (!type.IsAbstract && type.Name == DescriptorTypeName) { descriptorType = type; break; }
            if (descriptorType == null) return null;

            Object fallback = null;
            int descriptorsWithMenu = 0;
            foreach (var descriptor in Object.FindObjectsOfType(descriptorType, true))
            {
                var so = new SerializedObject(descriptor);
                var menu = so.FindProperty("expressionsMenu")?.objectReferenceValue;
                if (menu == null) continue;
                descriptorsWithMenu++;
                fallback = menu;
                foreach (var arrayName in new[] { "baseAnimationLayers", "specialAnimationLayers" })
                {
                    var layers = so.FindProperty(arrayName);
                    if (layers == null || !layers.isArray) continue;
                    for (int i = 0; i < layers.arraySize; i++)
                        if (layers.GetArrayElementAtIndex(i)
                                .FindPropertyRelative("animatorController")?.objectReferenceValue == controller)
                            return menu;
                }
            }
            return descriptorsWithMenu == 1 ? fallback : null;
        }

        // ---- reading ---------------------------------------------------------

        public static List<Control> Read(Object asset)
        {
            var controls = new List<Control>();
            if (!Is(asset)) return controls;
            var list = ControlsProperty(new SerializedObject(asset));
            if (list == null) return controls;
            for (int i = 0; i < list.arraySize; i++)
            {
                var element = list.GetArrayElementAtIndex(i);
                var control = new Control
                {
                    name = element.FindPropertyRelative("name")?.stringValue ?? string.Empty,
                    icon = element.FindPropertyRelative("icon")?.objectReferenceValue as Texture2D,
                    type = (ControlType)(element.FindPropertyRelative("type")?.intValue ?? (int)ControlType.Button),
                    parameter = ParameterName(element, "parameter"),
                    value = element.FindPropertyRelative("value")?.floatValue ?? 0f,
                    subMenu = element.FindPropertyRelative("subMenu")?.objectReferenceValue,
                };
                var subParameters = element.FindPropertyRelative("subParameters");
                if (subParameters != null && subParameters.isArray)
                    for (int s = 0; s < subParameters.arraySize; s++)
                        control.subParameters.Add(
                            subParameters.GetArrayElementAtIndex(s).FindPropertyRelative("name")?.stringValue
                            ?? string.Empty);
                var labels = element.FindPropertyRelative("labels");
                if (labels != null && labels.isArray)
                    for (int s = 0; s < labels.arraySize; s++)
                        control.labels.Add(
                            labels.GetArrayElementAtIndex(s).FindPropertyRelative("name")?.stringValue
                            ?? string.Empty);
                controls.Add(control);
            }
            return controls;
        }

        static string ParameterName(SerializedProperty element, string property)
        {
            var parameter = element.FindPropertyRelative(property);
            return parameter?.FindPropertyRelative("name")?.stringValue ?? string.Empty;
        }

        static SerializedProperty ControlsProperty(SerializedObject so) =>
            so.FindProperty("controls");

        // ---- editing ---------------------------------------------------------

        /// <summary>Applies <paramref name="edit"/> to the control element at
        /// <paramref name="index"/> (with undo); false when out of range.</summary>
        public static bool EditControl(Object asset, int index, System.Action<SerializedProperty> edit)
        {
            if (!Is(asset)) return false;
            var so = new SerializedObject(asset);
            var list = ControlsProperty(so);
            if (list == null || index < 0 || index >= list.arraySize) return false;
            Undo.RegisterCompleteObjectUndo(asset, "Edit Expressions Menu");
            edit(list.GetArrayElementAtIndex(index));
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            return true;
        }

        public static void SetName(Object asset, int index, string value) =>
            EditControl(asset, index, e => e.FindPropertyRelative("name").stringValue = value);

        public static void SetIcon(Object asset, int index, Texture2D icon) =>
            EditControl(asset, index, e => e.FindPropertyRelative("icon").objectReferenceValue = icon);

        public static void SetType(Object asset, int index, ControlType type) =>
            EditControl(asset, index, e =>
            {
                e.FindPropertyRelative("type").intValue = (int)type;
                EnsureAuxArrays(e, type);
            });

        public static void SetParameter(Object asset, int index, string parameterName) =>
            EditControl(asset, index, e =>
                e.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = parameterName);

        public static void SetValue(Object asset, int index, float value) =>
            EditControl(asset, index, e => e.FindPropertyRelative("value").floatValue = value);

        public static void SetSubMenu(Object asset, int index, Object subMenu) =>
            EditControl(asset, index, e => e.FindPropertyRelative("subMenu").objectReferenceValue = subMenu);

        public static void SetSubParameter(Object asset, int index, int slot, string parameterName) =>
            EditControl(asset, index, e =>
            {
                var subParameters = e.FindPropertyRelative("subParameters");
                if (subParameters == null || slot < 0) return;
                if (subParameters.arraySize <= slot) subParameters.arraySize = slot + 1;
                subParameters.GetArrayElementAtIndex(slot)
                    .FindPropertyRelative("name").stringValue = parameterName;
            });

        public static void SetLabel(Object asset, int index, int slot, string label) =>
            EditControl(asset, index, e =>
            {
                var labels = e.FindPropertyRelative("labels");
                if (labels == null || slot < 0) return;
                if (labels.arraySize <= slot) labels.arraySize = slot + 1;
                labels.GetArrayElementAtIndex(slot).FindPropertyRelative("name").stringValue = label;
            });

        /// <summary>Grows subParameters / labels to the sizes the control type expects.</summary>
        static void EnsureAuxArrays(SerializedProperty element, ControlType type)
        {
            var subParameters = element.FindPropertyRelative("subParameters");
            if (subParameters != null && subParameters.arraySize < SubParameterCount(type))
                subParameters.arraySize = SubParameterCount(type);
            var labels = element.FindPropertyRelative("labels");
            if (labels != null && labels.arraySize < LabelCount(type))
                labels.arraySize = LabelCount(type);
        }

        public static int AddControl(Object asset)
        {
            if (!Is(asset)) return -1;
            var so = new SerializedObject(asset);
            var list = ControlsProperty(so);
            if (list == null || list.arraySize >= MaxControls(asset)) return -1;
            Undo.RegisterCompleteObjectUndo(asset, "Add Menu Control");
            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);
            var element = list.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("name").stringValue = "New Control";
            element.FindPropertyRelative("type").intValue = (int)ControlType.Toggle;
            var parameter = element.FindPropertyRelative("parameter")?.FindPropertyRelative("name");
            if (parameter != null) parameter.stringValue = string.Empty;
            var icon = element.FindPropertyRelative("icon");
            if (icon != null) icon.objectReferenceValue = null;
            var subMenu = element.FindPropertyRelative("subMenu");
            if (subMenu != null) subMenu.objectReferenceValue = null;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            return index;
        }

        public static bool RemoveControl(Object asset, int index)
        {
            if (!Is(asset)) return false;
            var so = new SerializedObject(asset);
            var list = ControlsProperty(so);
            if (list == null || index < 0 || index >= list.arraySize) return false;
            Undo.RegisterCompleteObjectUndo(asset, "Remove Menu Control");
            list.DeleteArrayElementAtIndex(index);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            return true;
        }

        public static bool MoveControl(Object asset, int from, int to)
        {
            if (!Is(asset)) return false;
            var so = new SerializedObject(asset);
            var list = ControlsProperty(so);
            if (list == null || from < 0 || from >= list.arraySize || to < 0 || to >= list.arraySize)
                return false;
            Undo.RegisterCompleteObjectUndo(asset, "Reorder Menu Controls");
            list.MoveArrayElement(from, to);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            return true;
        }

        /// <summary>New menu asset of the same type, saved next to <paramref name="parent"/>.</summary>
        public static Object CreateSubMenuAsset(Object parent, string controlName)
        {
            if (!Is(parent)) return null;
            string parentPath = AssetDatabase.GetAssetPath(parent);
            if (string.IsNullOrEmpty(parentPath)) return null;
            var menu = ScriptableObject.CreateInstance(parent.GetType());
            string safe = string.IsNullOrEmpty(controlName) ? "SubMenu" : controlName;
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');
            string path = AssetDatabase.GenerateUniqueAssetPath(
                System.IO.Path.GetDirectoryName(parentPath) + "/" + parent.name + " " + safe + ".asset");
            AssetDatabase.CreateAsset(menu, path);
            return menu;
        }

        /// <summary>Renames parameter references in this menu and every reachable submenu
        /// (cycle-safe). Returns how many controls were touched.</summary>
        public static int RenameParameterReferences(Object asset, string oldName, string newName)
        {
            int touched = 0;
            var visited = new HashSet<Object>();
            var queue = new Queue<Object>();
            queue.Enqueue(asset);
            while (queue.Count > 0)
            {
                var menu = queue.Dequeue();
                if (menu == null || !visited.Add(menu) || !Is(menu)) continue;
                var controls = Read(menu);
                for (int i = 0; i < controls.Count; i++)
                {
                    var control = controls[i];
                    if (control.subMenu != null) queue.Enqueue(control.subMenu);
                    if (control.parameter == oldName)
                    {
                        SetParameter(menu, i, newName);
                        touched++;
                    }
                    for (int s = 0; s < control.subParameters.Count; s++)
                        if (control.subParameters[s] == oldName)
                        {
                            SetSubParameter(menu, i, s, newName);
                            touched++;
                        }
                }
            }
            return touched;
        }
    }
}

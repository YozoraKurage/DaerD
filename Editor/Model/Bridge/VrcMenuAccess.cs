using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD.Bridge
{
    /// <summary>
    /// Reads a VRCExpressionsMenu asset, and renames parameter references inside one. Matched
    /// by type name and accessed via SerializedObject so DaerD needs no VRCSDK reference.
    ///
    /// <para>WHY THERE IS NO AUTHORING HALF.</para>
    /// Menu editing left DaerD's scope on 2026-08-18 (user decision — MA Menu's territory),
    /// and the editor window went with it. The authoring surface this class used to carry
    /// (add / remove / move / set-everything / create-submenu) had no caller left and was
    /// deleted rather than kept as an unexercised path. What stays is what live features
    /// reach for: the async-sync puppet warning reads controls, and a parameter rename must
    /// not silently orphan the menu references to the old name — so the rename cascade keeps
    /// its two write helpers, private, as its own machinery rather than an API.
    /// Controls are edited per property path (never rewritten wholesale) so fields DaerD
    /// doesn't model survive untouched.
    /// </summary>
    static class VrcMenuAccess
    {
        public const string AssetTypeName = "VRCExpressionsMenu";

        /// <summary>VRC SDK control type ids.</summary>
        internal enum ControlType
        {
            Button = 101,
            Toggle = 102,
            SubMenu = 103,
            TwoAxisPuppet = 201,
            FourAxisPuppet = 202,
            RadialPuppet = 203,
        }

        /// <summary>Read-only snapshot of one control for list / inspector rendering.</summary>
        internal class Control
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

        // ---- the rename cascade ---------------------------------------------

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

        /// <summary>Applies <paramref name="edit"/> to the control element at
        /// <paramref name="index"/> (with undo); false when out of range.</summary>
        static bool EditControl(Object asset, int index, System.Action<SerializedProperty> edit)
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

        static void SetParameter(Object asset, int index, string parameterName) =>
            EditControl(asset, index, e =>
                e.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = parameterName);

        static void SetSubParameter(Object asset, int index, int slot, string parameterName) =>
            EditControl(asset, index, e =>
            {
                var subParameters = e.FindPropertyRelative("subParameters");
                if (subParameters == null || slot < 0) return;
                if (subParameters.arraySize <= slot) subParameters.arraySize = slot + 1;
                subParameters.GetArrayElementAtIndex(slot)
                    .FindPropertyRelative("name").stringValue = parameterName;
            });
    }
}

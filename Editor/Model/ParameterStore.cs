using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Uniform access to "the thing that declares this controller's expression parameters":
    /// a VRCExpressionParameters asset (avatar workflow) or a Modular Avatar "MA Parameters"
    /// component (NDMF gimmick workflow). The association is explicit — stored per controller
    /// in <see cref="GraphFrameData"/> and assigned by the user; <see cref="DetectFor"/> only
    /// runs on an explicit user action and only returns EXACT matches (never "the only
    /// avatar in the scene").
    /// </summary>
    abstract class ParameterStore
    {
        public abstract Object Target { get; }
        /// <summary>Short label shown next to the slot ("VRC Params" / "MA Params").</summary>
        public abstract string Kind { get; }
        /// <summary>Synced-bit capacity, or -1 when the store has no own budget (an MA
        /// component contributes to the avatar's total, which DaerD can't see).</summary>
        public abstract int Capacity();
        public abstract List<VrcExpressionParameters.Entry> Read();
        /// <summary>Aligns the store to the given entries (used by the sync command). Order
        /// is honoured where the store is ordered; MA applies it as a diff.</summary>
        public abstract void WriteAll(IList<VrcExpressionParameters.Entry> entries);
        public abstract void Add(VrcExpressionParameters.Entry entry);
        public abstract bool Remove(string name);
        public abstract bool Edit(string name, System.Action<VrcExpressionParameters.Entry> edit);

        public bool Rename(string oldName, string newName) =>
            Edit(oldName, entry => entry.name = newName);

        public VrcExpressionParameters.Entry Find(string name)
        {
            foreach (var entry in Read())
                if (entry.name == name)
                    return entry;
            return null;
        }

        public int UsedBits()
        {
            int bits = 0;
            foreach (var entry in Read())
                if (entry.synced)
                    bits += VrcExpressionParameters.BitCost(entry.valueType);
            return bits;
        }

        /// <summary>Wraps a user-assigned object; null when the type isn't a known store.</summary>
        public static ParameterStore TryWrap(Object target)
        {
            if (target == null) return null;
            if (VrcExpressionParameters.Is(target))
                return new VrcStore(target);
            if (target is GameObject gameObject)
                target = FindComponent(gameObject, MaStore.TypeName);
            if (target is Component component && component.GetType().Name == MaStore.TypeName)
                return new MaStore(component);
            return null;
        }

        /// <summary>The store explicitly associated with the controller, wrapped; null when
        /// none is assigned or the assigned object went missing.</summary>
        public static ParameterStore Of(AnimatorController controller) =>
            TryWrap(GraphFrameData.GetParameterStore(controller));

        /// <summary>
        /// Explicit detection (user-triggered only). Exact matches:
        /// an avatar descriptor whose playable layers run this controller, or an MA Merge
        /// Animator referencing it with an MA Parameters component on itself or a parent.
        /// </summary>
        public static Object DetectFor(AnimatorController controller)
        {
            var vrc = VrcExpressionParameters.FindAssetFor(controller);
            if (vrc != null) return vrc;
            return MaStore.FindFor(controller);
        }

        /// <summary>Store-vs-controller checks, appended to the analyzer's issue list.</summary>
        public void Analyze(AnimatorController controller, List<ControllerAnalyzer.Issue> issues)
        {
            if (controller == null || Target == null) return;
            var entries = Read();

            int capacity = Capacity();
            if (capacity >= 0)
            {
                int used = 0;
                foreach (var entry in entries)
                    if (entry.synced)
                        used += VrcExpressionParameters.BitCost(entry.valueType);
                if (used > capacity)
                    issues.Add(new ControllerAnalyzer.Issue
                    {
                        kind = ControllerAnalyzer.Kind.VrcParameters,
                        severity = ControllerAnalyzer.Severity.Error,
                        message = L.Tr("Expression parameters use {0} of {1} synced bits.", used, capacity),
                        context = Target,
                    });
            }

            foreach (var entry in entries)
            {
                var controllerParameter = DbtBuilder.FindParameter(controller, entry.name);
                if (controllerParameter == null)
                {
                    if (entry.synced)
                        issues.Add(new ControllerAnalyzer.Issue
                        {
                            kind = ControllerAnalyzer.Kind.VrcParameters,
                            severity = ControllerAnalyzer.Severity.Info,
                            message = L.Tr("Expression parameter '{0}' has no matching controller parameter.", entry.name),
                            context = Target,
                        });
                    continue;
                }
                // Differing types are NOT an error: VRChat converts between every
                // combination ("parameter mismatching", e.g. a 1-bit synced Bool driving an
                // animator Float — https://vrc.school/docs/Other/Parameter-Mismatching).
                // Surface it as info so accidental mismatches stay visible.
                if (!entry.typed) continue;
                var mapped = VrcExpressionParameters.MapType(controllerParameter.type);
                if (mapped != null && mapped.Value != entry.valueType)
                    issues.Add(new ControllerAnalyzer.Issue
                    {
                        kind = ControllerAnalyzer.Kind.VrcParameters,
                        severity = ControllerAnalyzer.Severity.Info,
                        message = L.Tr("Expression parameter '{0}' is {1} while the controller parameter is {2} — VRChat converts between them (parameter mismatching); make sure it's intentional.",
                            entry.name, entry.valueType, controllerParameter.type),
                        context = Target,
                    });
            }
        }

        internal static Component FindComponent(GameObject gameObject, string typeName)
        {
            foreach (var component in gameObject.GetComponents<Component>())
                if (component != null && component.GetType().Name == typeName)
                    return component;
            return null;
        }

        // ---- VRCExpressionParameters backend ---------------------------------

        class VrcStore : ParameterStore
        {
            readonly Object _asset;
            public VrcStore(Object asset) => _asset = asset;

            public override Object Target => _asset;
            public override string Kind => "VRC Params";
            public override int Capacity() => VrcExpressionParameters.Capacity(_asset);
            public override List<VrcExpressionParameters.Entry> Read() =>
                VrcExpressionParameters.Read(_asset);
            public override void WriteAll(IList<VrcExpressionParameters.Entry> entries)
            {
                Undo.RegisterCompleteObjectUndo(_asset, "Sync Parameters");
                VrcExpressionParameters.WriteAll(_asset, entries);
            }
            public override void Add(VrcExpressionParameters.Entry entry) =>
                VrcExpressionParameters.Add(_asset, entry);
            public override bool Remove(string name) =>
                VrcExpressionParameters.Remove(_asset, name);
            public override bool Edit(string name, System.Action<VrcExpressionParameters.Entry> edit) =>
                VrcExpressionParameters.Edit(_asset, name, edit);
        }

        // ---- Modular Avatar "MA Parameters" backend ---------------------------

        /// <summary>
        /// Accesses nadena.dev Modular Avatar's ModularAvatarParameters component via
        /// SerializedObject (no MA reference). Entries live in the `parameters` array:
        /// nameOrPrefix / isPrefix / syncType (NotSynced=0, Int=1, Float=2, Bool=3) /
        /// localOnly / saved / defaultValue. Prefix rows (PhysBone families) are preserved
        /// but not exposed for editing.
        /// </summary>
        class MaStore : ParameterStore
        {
            public const string TypeName = "ModularAvatarParameters";
            public const string MergeAnimatorTypeName = "ModularAvatarMergeAnimator";

            readonly Component _component;
            public MaStore(Component component) => _component = component;

            public override Object Target => _component;
            public override string Kind => "MA Params";
            public override int Capacity() => -1;   // contributes to the avatar's budget

            /// <summary>The MA Parameters component belonging to a scene MA Merge Animator
            /// that references this controller (on the same object or a parent).</summary>
            public static Object FindFor(AnimatorController controller)
            {
                if (controller == null) return null;
                System.Type mergeType = null;
                foreach (var type in TypeCache.GetTypesDerivedFrom<MonoBehaviour>())
                    if (!type.IsAbstract && type.Name == MergeAnimatorTypeName) { mergeType = type; break; }
                if (mergeType == null) return null;

                foreach (var merge in Object.FindObjectsOfType(mergeType, true))
                {
                    var component = merge as Component;
                    if (component == null) continue;
                    var so = new SerializedObject(component);
                    if (so.FindProperty("animator")?.objectReferenceValue != controller) continue;
                    for (var transform = component.transform; transform != null; transform = transform.parent)
                    {
                        var parameters = FindComponent(transform.gameObject, TypeName);
                        if (parameters != null) return parameters;
                    }
                }
                return null;
            }

            public override List<VrcExpressionParameters.Entry> Read()
            {
                var entries = new List<VrcExpressionParameters.Entry>();
                var list = List(out _);
                if (list == null) return entries;
                for (int i = 0; i < list.arraySize; i++)
                {
                    var element = list.GetArrayElementAtIndex(i);
                    if (element.FindPropertyRelative("isPrefix")?.boolValue == true) continue;
                    int syncType = element.FindPropertyRelative("syncType")?.intValue ?? 0;
                    entries.Add(new VrcExpressionParameters.Entry
                    {
                        name = element.FindPropertyRelative("nameOrPrefix")?.stringValue ?? string.Empty,
                        valueType = MapSyncType(syncType),
                        typed = syncType != 0,
                        synced = syncType != 0
                            && element.FindPropertyRelative("localOnly")?.boolValue != true,
                        saved = element.FindPropertyRelative("saved")?.boolValue ?? false,
                        defaultValue = element.FindPropertyRelative("defaultValue")?.floatValue ?? 0f,
                    });
                }
                return entries;
            }

            static VrcExpressionParameters.ValueType MapSyncType(int syncType)
            {
                switch (syncType)
                {
                    case 1: return VrcExpressionParameters.ValueType.Int;
                    case 3: return VrcExpressionParameters.ValueType.Bool;
                    default: return VrcExpressionParameters.ValueType.Float;
                }
            }

            static int MapValueType(VrcExpressionParameters.ValueType type)
            {
                switch (type)
                {
                    case VrcExpressionParameters.ValueType.Int: return 1;
                    case VrcExpressionParameters.ValueType.Bool: return 3;
                    default: return 2;
                }
            }

            SerializedProperty List(out SerializedObject so)
            {
                so = new SerializedObject(_component);
                var list = so.FindProperty("parameters");
                return list != null && list.isArray ? list : null;
            }

            /// <summary>MA entries are matched by name (order carries no meaning there), so
            /// "write all" applies as a diff and leaves prefix rows untouched.</summary>
            public override void WriteAll(IList<VrcExpressionParameters.Entry> entries)
            {
                var wanted = new Dictionary<string, VrcExpressionParameters.Entry>();
                foreach (var entry in entries) wanted[entry.name] = entry;
                foreach (var existing in Read())
                    if (!wanted.ContainsKey(existing.name))
                        Remove(existing.name);
                foreach (var entry in entries)
                {
                    if (Find(entry.name) != null)
                        Edit(entry.name, e => CopyInto(entry, e));
                    else
                        Add(entry);
                }
            }

            static void CopyInto(VrcExpressionParameters.Entry from, VrcExpressionParameters.Entry to)
            {
                to.name = from.name;
                to.valueType = from.valueType;
                to.typed = from.typed;
                to.synced = from.synced;
                to.saved = from.saved;
                to.defaultValue = from.defaultValue;
            }

            public override void Add(VrcExpressionParameters.Entry entry)
            {
                if (entry == null || Find(entry.name) != null) return;
                var list = List(out var so);
                if (list == null) return;
                Undo.RegisterCompleteObjectUndo(_component, "Add MA Parameter");
                int index = list.arraySize;
                list.InsertArrayElementAtIndex(index);
                var element = list.GetArrayElementAtIndex(index);
                // InsertArrayElementAtIndex clones the previous row — reset everything.
                SetString(element, "nameOrPrefix", entry.name);
                SetString(element, "remapTo", string.Empty);
                SetBool(element, "isPrefix", false);
                SetBool(element, "internalParameter", false);
                WriteEntry(element, entry);
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(_component);
            }

            public override bool Remove(string name)
            {
                var list = List(out var so);
                if (list == null) return false;
                for (int i = 0; i < list.arraySize; i++)
                {
                    var element = list.GetArrayElementAtIndex(i);
                    if (element.FindPropertyRelative("isPrefix")?.boolValue == true) continue;
                    if (element.FindPropertyRelative("nameOrPrefix")?.stringValue != name) continue;
                    Undo.RegisterCompleteObjectUndo(_component, "Remove MA Parameter");
                    list.DeleteArrayElementAtIndex(i);
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(_component);
                    return true;
                }
                return false;
            }

            public override bool Edit(string name, System.Action<VrcExpressionParameters.Entry> edit)
            {
                var list = List(out var so);
                if (list == null) return false;
                for (int i = 0; i < list.arraySize; i++)
                {
                    var element = list.GetArrayElementAtIndex(i);
                    if (element.FindPropertyRelative("isPrefix")?.boolValue == true) continue;
                    if (element.FindPropertyRelative("nameOrPrefix")?.stringValue != name) continue;

                    int syncType = element.FindPropertyRelative("syncType")?.intValue ?? 0;
                    var entry = new VrcExpressionParameters.Entry
                    {
                        name = name,
                        valueType = MapSyncType(syncType),
                        typed = syncType != 0,
                        synced = syncType != 0
                            && element.FindPropertyRelative("localOnly")?.boolValue != true,
                        saved = element.FindPropertyRelative("saved")?.boolValue ?? false,
                        defaultValue = element.FindPropertyRelative("defaultValue")?.floatValue ?? 0f,
                    };
                    Undo.RegisterCompleteObjectUndo(_component, "Edit MA Parameter");
                    edit(entry);
                    SetString(element, "nameOrPrefix", entry.name);
                    WriteEntry(element, entry);
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(_component);
                    return true;
                }
                return false;
            }

            /// <summary>Maps the shared entry shape back onto MA's fields. Synced entries get
            /// a concrete syncType; unsynced typed entries keep their type with localOnly.</summary>
            static void WriteEntry(SerializedProperty element, VrcExpressionParameters.Entry entry)
            {
                var syncType = element.FindPropertyRelative("syncType");
                if (syncType != null && entry.typed)
                    syncType.intValue = MapValueType(entry.valueType);
                SetBool(element, "localOnly", !entry.synced);
                SetBool(element, "saved", entry.saved);
                var defaultValue = element.FindPropertyRelative("defaultValue");
                if (defaultValue != null) defaultValue.floatValue = entry.defaultValue;
                SetBool(element, "hasExplicitDefaultValue", true);
            }

            static void SetString(SerializedProperty element, string property, string value)
            {
                var prop = element.FindPropertyRelative(property);
                if (prop != null) prop.stringValue = value;
            }

            static void SetBool(SerializedProperty element, string property, bool value)
            {
                var prop = element.FindPropertyRelative(property);
                if (prop != null) prop.boolValue = value;
            }
        }
    }
}

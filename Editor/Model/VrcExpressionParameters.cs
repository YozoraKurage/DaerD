using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Reads and rewrites a VRCExpressionParameters asset. Matched by type name and accessed
    /// via SerializedObject so DaerD needs no VRCSDK reference. Entries live in the asset's
    /// `parameters` array: name / valueType (Int=0, Float=1, Bool=2) / saved / defaultValue /
    /// networkSynced (absent on very old SDKs — treated as always synced).
    /// </summary>
    static class VrcExpressionParameters
    {
        public const string AssetTypeName = "VRCExpressionParameters";
        public const string DescriptorTypeName = "VRCAvatarDescriptor";
        public const int FallbackCapacity = 256;

        public enum ValueType { Int = 0, Float = 1, Bool = 2 }

        public class Entry
        {
            public string name;
            public ValueType valueType;
            public bool saved = true;
            public bool synced = true;
            public float defaultValue;
        }

        public static bool Is(Object asset) =>
            asset != null && asset.GetType().Name == AssetTypeName;

        /// <summary>Synced bits an entry occupies: Bool = 1, Int / Float = 8.</summary>
        public static int BitCost(ValueType type) => type == ValueType.Bool ? 1 : 8;

        /// <summary>The expression-parameter type an animator parameter maps to; null for Trigger.</summary>
        public static ValueType? MapType(AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Int: return ValueType.Int;
                case AnimatorControllerParameterType.Float: return ValueType.Float;
                case AnimatorControllerParameterType.Bool: return ValueType.Bool;
                default: return null;
            }
        }

        /// <summary>
        /// The expression parameters asset belonging to this controller: a scene avatar
        /// descriptor whose playable layers reference the controller. Falls back to the only
        /// descriptor in the scene when exactly one carries an asset.
        /// </summary>
        public static Object FindAssetFor(AnimatorController controller)
        {
            if (controller == null) return null;
            var descriptorType = FindDescriptorType();
            if (descriptorType == null) return null;

            Object fallback = null;
            int descriptorsWithAsset = 0;
            foreach (var descriptor in Object.FindObjectsOfType(descriptorType, true))
            {
                var so = new SerializedObject(descriptor);
                var asset = so.FindProperty("expressionParameters")?.objectReferenceValue;
                if (asset == null) continue;
                descriptorsWithAsset++;
                fallback = asset;
                if (ReferencesController(so, controller))
                    return asset;
            }
            return descriptorsWithAsset == 1 ? fallback : null;
        }

        static System.Type FindDescriptorType()
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<MonoBehaviour>())
                if (!type.IsAbstract && type.Name == DescriptorTypeName)
                    return type;
            return null;
        }

        static bool ReferencesController(SerializedObject descriptor, AnimatorController controller)
        {
            foreach (var arrayName in new[] { "baseAnimationLayers", "specialAnimationLayers" })
            {
                var layers = descriptor.FindProperty(arrayName);
                if (layers == null || !layers.isArray) continue;
                for (int i = 0; i < layers.arraySize; i++)
                {
                    var element = layers.GetArrayElementAtIndex(i);
                    var reference = element.FindPropertyRelative("animatorController");
                    if (reference != null && reference.objectReferenceValue == controller)
                        return true;
                }
            }
            return false;
        }

        // ---- reading ---------------------------------------------------------

        public static List<Entry> Read(Object asset)
        {
            var entries = new List<Entry>();
            if (!Is(asset)) return entries;
            var so = new SerializedObject(asset);
            var list = so.FindProperty("parameters");
            if (list == null || !list.isArray) return entries;
            for (int i = 0; i < list.arraySize; i++)
                entries.Add(ReadEntry(list.GetArrayElementAtIndex(i)));
            return entries;
        }

        static Entry ReadEntry(SerializedProperty element)
        {
            var synced = element.FindPropertyRelative("networkSynced");
            return new Entry
            {
                name = element.FindPropertyRelative("name")?.stringValue ?? string.Empty,
                valueType = (ValueType)(element.FindPropertyRelative("valueType")?.intValue ?? 0),
                saved = element.FindPropertyRelative("saved")?.boolValue ?? true,
                synced = synced == null || synced.boolValue,
                defaultValue = element.FindPropertyRelative("defaultValue")?.floatValue ?? 0f,
            };
        }

        public static Entry Find(Object asset, string name)
        {
            foreach (var entry in Read(asset))
                if (entry.name == name)
                    return entry;
            return null;
        }

        /// <summary>Synced bits currently used by the asset.</summary>
        public static int UsedBits(Object asset)
        {
            int bits = 0;
            foreach (var entry in Read(asset))
                if (entry.synced)
                    bits += BitCost(entry.valueType);
            return bits;
        }

        /// <summary>MAX_PARAMETER_COST from the SDK, or 256 when it can't be read.</summary>
        public static int Capacity(Object asset)
        {
            if (asset == null) return FallbackCapacity;
            var field = asset.GetType().GetField("MAX_PARAMETER_COST",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (field != null && field.FieldType == typeof(int))
                return (int)field.GetRawConstantValue();
            return FallbackCapacity;
        }

        // ---- writing ---------------------------------------------------------

        /// <summary>Replaces the whole entry list (used by the sync command to align order).</summary>
        public static void WriteAll(Object asset, IList<Entry> entries)
        {
            if (!Is(asset)) return;
            var so = new SerializedObject(asset);
            var list = so.FindProperty("parameters");
            if (list == null || !list.isArray) return;
            list.arraySize = entries.Count;
            for (int i = 0; i < entries.Count; i++)
                WriteEntry(list.GetArrayElementAtIndex(i), entries[i]);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
        }

        static void WriteEntry(SerializedProperty element, Entry entry)
        {
            var name = element.FindPropertyRelative("name");
            if (name != null) name.stringValue = entry.name;
            var valueType = element.FindPropertyRelative("valueType");
            if (valueType != null) valueType.intValue = (int)entry.valueType;
            var saved = element.FindPropertyRelative("saved");
            if (saved != null) saved.boolValue = entry.saved;
            var synced = element.FindPropertyRelative("networkSynced");
            if (synced != null) synced.boolValue = entry.synced;
            var defaultValue = element.FindPropertyRelative("defaultValue");
            if (defaultValue != null) defaultValue.floatValue = entry.defaultValue;
        }

        /// <summary>Applies <paramref name="edit"/> to the named entry; false when absent.</summary>
        public static bool Edit(Object asset, string name, System.Action<Entry> edit)
        {
            var entries = Read(asset);
            foreach (var entry in entries)
            {
                if (entry.name != name) continue;
                Undo.RegisterCompleteObjectUndo(asset, "Edit VRC Parameter");
                edit(entry);
                WriteAll(asset, entries);
                return true;
            }
            return false;
        }

        public static void Add(Object asset, Entry entry)
        {
            if (!Is(asset) || entry == null || Find(asset, entry.name) != null) return;
            Undo.RegisterCompleteObjectUndo(asset, "Add VRC Parameter");
            var entries = Read(asset);
            entries.Add(entry);
            WriteAll(asset, entries);
        }

        public static bool Remove(Object asset, string name)
        {
            var entries = Read(asset);
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].name != name) continue;
                Undo.RegisterCompleteObjectUndo(asset, "Remove VRC Parameter");
                entries.RemoveAt(i);
                WriteAll(asset, entries);
                return true;
            }
            return false;
        }

        public static bool Rename(Object asset, string oldName, string newName) =>
            Edit(asset, oldName, entry => entry.name = newName);

        // ---- analysis --------------------------------------------------------

        /// <summary>Expression-parameter checks, appended to the analyzer's issue list.</summary>
        public static void Analyze(AnimatorController controller, Object asset,
            List<ControllerAnalyzer.Issue> issues)
        {
            if (controller == null || !Is(asset)) return;
            var entries = Read(asset);

            int used = 0;
            foreach (var entry in entries)
                if (entry.synced)
                    used += BitCost(entry.valueType);
            int capacity = Capacity(asset);
            if (used > capacity)
                issues.Add(new ControllerAnalyzer.Issue
                {
                    kind = ControllerAnalyzer.Kind.VrcParameters,
                    severity = ControllerAnalyzer.Severity.Error,
                    message = L.Tr("Expression parameters use {0} of {1} synced bits.", used, capacity),
                    context = asset,
                });

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
                            context = asset,
                        });
                    continue;
                }
                var mapped = MapType(controllerParameter.type);
                if (mapped != null && mapped.Value != entry.valueType)
                    issues.Add(new ControllerAnalyzer.Issue
                    {
                        kind = ControllerAnalyzer.Kind.VrcParameters,
                        severity = ControllerAnalyzer.Severity.Error,
                        message = L.Tr("Expression parameter '{0}' is {1} but the controller parameter is {2}.",
                            entry.name, entry.valueType, controllerParameter.type),
                        context = asset,
                    });
            }
        }
    }
}

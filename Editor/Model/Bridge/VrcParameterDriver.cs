using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Reads and rewrites the parameter references inside a VRCAvatarParameterDriver
    /// StateMachineBehaviour. Matched by type name and accessed via SerializedObject so
    /// DaerD needs no VRCSDK reference. Each driver entry references parameters through its
    /// `name` field (Set / Add / Random destination, Copy destination) and, for Copy, the
    /// `source` field.
    /// </summary>
    static class VrcParameterDriver
    {
        public const string TypeName = "VRCAvatarParameterDriver";

        public static bool Is(StateMachineBehaviour behaviour) =>
            behaviour != null && behaviour.GetType().Name == TypeName;

        /// <summary>The driver's concrete type, or null when the VRChat SDK is absent.</summary>
        public static System.Type FindType()
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<StateMachineBehaviour>())
                if (!type.IsAbstract && type.Name == TypeName)
                    return type;
            return null;
        }

        public static bool SdkAvailable => FindType() != null;

        /// <summary>The first driver already on the state, or null.</summary>
        public static StateMachineBehaviour FindOn(AnimatorState state)
        {
            if (state == null) return null;
            foreach (var behaviour in state.behaviours)
                if (Is(behaviour)) return behaviour;
            return null;
        }

        /// <summary>Adds a fresh driver to the state; null when the SDK is absent.</summary>
        public static StateMachineBehaviour AddTo(AnimatorState state, string instanceName = null)
        {
            var type = FindType();
            if (type == null || state == null) return null;
            Undo.RegisterCompleteObjectUndo(state, "Add Parameter Driver");
            var behaviour = state.AddStateMachineBehaviour(type);
            if (behaviour != null && !string.IsNullOrEmpty(instanceName))
                behaviour.name = instanceName;
            EditorUtility.SetDirty(state);
            return behaviour;
        }

        /// <summary>Appends a Set entry (type 0) writing <paramref name="value"/> to the parameter.</summary>
        public static void AddSetEntry(StateMachineBehaviour driver, string parameter, float value)
        {
            if (!Is(driver)) return;
            var so = new SerializedObject(driver);
            var list = so.FindProperty("parameters");
            if (list == null || !list.isArray) return;
            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);
            var entry = list.GetArrayElementAtIndex(index);
            // InsertArrayElementAtIndex clones the previous entry — overwrite every field we
            // rely on so the new row is a clean Set.
            var type = entry.FindPropertyRelative("type");
            if (type != null) type.intValue = 0;   // ChangeType.Set
            var name = entry.FindPropertyRelative("name");
            if (name != null) name.stringValue = parameter;
            var val = entry.FindPropertyRelative("value");
            if (val != null) val.floatValue = value;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(driver);
        }

        /// <summary>Appends a Copy entry (type 3) reading <paramref name="source"/> into
        /// <paramref name="destination"/> (no range conversion).</summary>
        public static void AddCopyEntry(StateMachineBehaviour driver, string source, string destination)
        {
            if (!Is(driver)) return;
            var so = new SerializedObject(driver);
            var list = so.FindProperty("parameters");
            if (list == null || !list.isArray) return;
            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);
            var entry = list.GetArrayElementAtIndex(index);
            // InsertArrayElementAtIndex clones the previous entry — overwrite every field we
            // rely on so the new row is a clean Copy.
            var type = entry.FindPropertyRelative("type");
            if (type != null) type.intValue = 3;   // ChangeType.Copy
            var sourceProp = entry.FindPropertyRelative("source");
            if (sourceProp != null) sourceProp.stringValue = source;
            var name = entry.FindPropertyRelative("name");
            if (name != null) name.stringValue = destination;
            var convert = entry.FindPropertyRelative("convertRange");
            if (convert != null) convert.boolValue = false;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(driver);
        }

        /// <summary>Appends an Add entry (type 1) adding <paramref name="value"/> to the parameter.</summary>
        public static void AddAddEntry(StateMachineBehaviour driver, string parameter, float value)
        {
            AppendEntry(driver, entry =>
            {
                SetInt(entry, "type", 1);   // ChangeType.Add
                SetString(entry, "name", parameter);
                SetFloat(entry, "value", value);
            });
        }

        /// <summary>Appends a Random entry (type 2) rolling between min and max with the given
        /// chance (Bools use chance alone; the SDK reads the fields it needs per type).</summary>
        public static void AddRandomEntry(StateMachineBehaviour driver, string parameter,
            float min, float max, float chance, bool preventRepeats = false)
        {
            AppendEntry(driver, entry =>
            {
                SetInt(entry, "type", 2);   // ChangeType.Random
                SetString(entry, "name", parameter);
                SetFloat(entry, "valueMin", min);
                SetFloat(entry, "valueMax", max);
                SetFloat(entry, "chance", chance);
                var repeats = entry.FindPropertyRelative("preventRepeats");
                if (repeats != null) repeats.boolValue = preventRepeats;
            });
        }

        /// <summary>Copy entry with the full range-conversion block.</summary>
        public static void AddCopyEntry(StateMachineBehaviour driver, string source, string destination,
            bool convertRange, float sourceMin, float sourceMax, float destMin, float destMax)
        {
            AppendEntry(driver, entry =>
            {
                SetInt(entry, "type", 3);   // ChangeType.Copy
                SetString(entry, "source", source);
                SetString(entry, "name", destination);
                var convert = entry.FindPropertyRelative("convertRange");
                if (convert != null) convert.boolValue = convertRange;
                SetFloat(entry, "sourceMin", sourceMin);
                SetFloat(entry, "sourceMax", sourceMax);
                SetFloat(entry, "destMin", destMin);
                SetFloat(entry, "destMax", destMax);
            });
        }

        /// <summary>Shared append: insert a row, hand it to <paramref name="fill"/>, apply.
        /// InsertArrayElementAtIndex clones the previous entry, so fill must overwrite every
        /// field it relies on.</summary>
        static void AppendEntry(StateMachineBehaviour driver, System.Action<SerializedProperty> fill)
        {
            if (!Is(driver)) return;
            var so = new SerializedObject(driver);
            var list = so.FindProperty("parameters");
            if (list == null || !list.isArray) return;
            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);
            fill(list.GetArrayElementAtIndex(index));
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(driver);
        }

        static void SetInt(SerializedProperty entry, string name, int value)
        {
            var property = entry.FindPropertyRelative(name);
            if (property != null) property.intValue = value;
        }

        static void SetFloat(SerializedProperty entry, string name, float value)
        {
            var property = entry.FindPropertyRelative(name);
            if (property != null) property.floatValue = value;
        }

        static void SetString(SerializedProperty entry, string name, string value)
        {
            var property = entry.FindPropertyRelative(name);
            if (property != null) property.stringValue = value ?? string.Empty;
        }

        /// <summary>Reads the driver into the IR's typed spec — what the C# exporter emits
        /// as .Set/.Add/.Random/.Copy calls instead of a JSON blob.</summary>
        public static ControllerIR.DriverSpec ReadSpec(StateMachineBehaviour driver)
        {
            var spec = new ControllerIR.DriverSpec();
            if (!Is(driver)) return spec;
            var so = new SerializedObject(driver);
            var localOnly = so.FindProperty("localOnly");
            spec.localOnly = localOnly != null && localOnly.boolValue;
            var list = so.FindProperty("parameters");
            if (list == null || !list.isArray) return spec;

            for (int i = 0; i < list.arraySize; i++)
            {
                var row = list.GetArrayElementAtIndex(i);
                var entry = new ControllerIR.DriverEntry
                {
                    kind = GetInt(row, "type"),
                    name = GetString(row, "name"),
                    source = GetString(row, "source"),
                    value = GetFloat(row, "value"),
                    min = GetFloat(row, "valueMin"),
                    max = GetFloat(row, "valueMax"),
                    chance = GetFloat(row, "chance"),
                    sourceMin = GetFloat(row, "sourceMin"),
                    sourceMax = GetFloat(row, "sourceMax"),
                    destMin = GetFloat(row, "destMin"),
                    destMax = GetFloat(row, "destMax"),
                };
                var convert = row.FindPropertyRelative("convertRange");
                entry.convertRange = convert != null && convert.boolValue;
                var repeats = row.FindPropertyRelative("preventRepeats");
                entry.preventRepeats = repeats != null && repeats.boolValue;
                spec.entries.Add(entry);
            }
            return spec;
        }

        /// <summary>Writes a typed spec onto a fresh driver instance (authoring path).</summary>
        public static void ApplySpec(StateMachineBehaviour driver, ControllerIR.DriverSpec spec)
        {
            if (!Is(driver) || spec == null) return;
            SetLocalOnly(driver, spec.localOnly);
            foreach (var entry in spec.entries)
                switch (entry.kind)
                {
                    case 1: AddAddEntry(driver, entry.name, entry.value); break;
                    case 2: AddRandomEntry(driver, entry.name, entry.min, entry.max, entry.chance,
                        entry.preventRepeats); break;
                    case 3:
                        AddCopyEntry(driver, entry.source, entry.name, entry.convertRange,
                            entry.sourceMin, entry.sourceMax, entry.destMin, entry.destMax);
                        break;
                    default: AddSetEntry(driver, entry.name, entry.value); break;
                }
        }

        static int GetInt(SerializedProperty entry, string name)
        {
            var property = entry.FindPropertyRelative(name);
            return property != null ? property.intValue : 0;
        }

        static float GetFloat(SerializedProperty entry, string name)
        {
            var property = entry.FindPropertyRelative(name);
            return property != null ? property.floatValue : 0f;
        }

        static string GetString(SerializedProperty entry, string name)
        {
            var property = entry.FindPropertyRelative(name);
            return property != null ? property.stringValue ?? string.Empty : string.Empty;
        }

        /// <summary>Removes every entry that writes the parameter (or Copy-reads it as its
        /// source). Used by Delete-and-Clean.</summary>
        public static bool RemoveEntriesReferencing(StateMachineBehaviour behaviour, string parameterName)
        {
            if (!Is(behaviour)) return false;
            var so = new SerializedObject(behaviour);
            var list = so.FindProperty("parameters");
            if (list == null || !list.isArray) return false;
            bool modified = false;
            for (int i = list.arraySize - 1; i >= 0; i--)
            {
                var entry = list.GetArrayElementAtIndex(i);
                var name = entry.FindPropertyRelative("name");
                bool matches = name != null && name.stringValue == parameterName;
                if (!matches)
                {
                    // `source` only means something on Copy entries (type 3); other types
                    // may carry stale clone values there.
                    var source = entry.FindPropertyRelative("source");
                    var type = entry.FindPropertyRelative("type");
                    matches = source != null && source.stringValue == parameterName
                        && type != null && type.intValue == 3;
                }
                if (!matches) continue;
                list.DeleteArrayElementAtIndex(i);
                modified = true;
            }
            if (modified)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(behaviour);
            }
            return modified;
        }

        /// <summary>Sets the driver's localOnly flag (drivers behind an IsLocal fence should
        /// not also run on remote clients).</summary>
        public static void SetLocalOnly(StateMachineBehaviour driver, bool localOnly)
        {
            if (!Is(driver)) return;
            var so = new SerializedObject(driver);
            var prop = so.FindProperty("localOnly");
            if (prop == null) return;
            prop.boolValue = localOnly;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(driver);
        }

        /// <summary>Adds every parameter name the driver references to <paramref name="into"/>.</summary>
        public static void CollectReferencedParameters(StateMachineBehaviour behaviour, HashSet<string> into)
        {
            ForEachReference(behaviour, prop =>
            {
                if (!string.IsNullOrEmpty(prop.stringValue)) into.Add(prop.stringValue);
                return false;
            });
        }

        public static bool References(StateMachineBehaviour behaviour, string parameterName)
        {
            bool found = false;
            ForEachReference(behaviour, prop =>
            {
                found |= prop.stringValue == parameterName;
                return false;
            });
            return found;
        }

        /// <summary>Rewrites every reference to <paramref name="oldName"/>; the write goes
        /// through ApplyModifiedProperties, which registers its own undo entry.</summary>
        public static bool RenameReferences(StateMachineBehaviour behaviour, string oldName, string newName)
        {
            return ForEachReference(behaviour, prop =>
            {
                if (prop.stringValue != oldName) return false;
                prop.stringValue = newName;
                return true;
            });
        }

        /// <summary>
        /// Runs <paramref name="visit"/> over each entry's `name` and `source` string property.
        /// A visit returning true marks the object modified; changes are applied (with undo)
        /// once at the end. Returns whether anything was modified.
        /// </summary>
        static bool ForEachReference(StateMachineBehaviour behaviour,
            System.Func<SerializedProperty, bool> visit)
        {
            if (!Is(behaviour)) return false;
            var so = new SerializedObject(behaviour);
            var parameters = so.FindProperty("parameters");
            if (parameters == null || !parameters.isArray) return false;

            bool modified = false;
            for (int i = 0; i < parameters.arraySize; i++)
            {
                var entry = parameters.GetArrayElementAtIndex(i);
                modified |= VisitString(entry.FindPropertyRelative("name"), visit);
                modified |= VisitString(entry.FindPropertyRelative("source"), visit);
            }
            if (modified)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(behaviour);
            }
            return modified;
        }

        static bool VisitString(SerializedProperty prop, System.Func<SerializedProperty, bool> visit)
        {
            if (prop == null || prop.propertyType != SerializedPropertyType.String) return false;
            return visit(prop);
        }
    }
}

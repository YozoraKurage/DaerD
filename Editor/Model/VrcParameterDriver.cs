using System.Collections.Generic;
using UnityEditor;
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

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

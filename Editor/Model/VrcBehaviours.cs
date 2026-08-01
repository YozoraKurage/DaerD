using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Registry and clipboard for the VRC SDK StateMachineBehaviour types DaerD understands.
    /// Types are matched by name (no SDK reference); a type resolves to null when the SDK is
    /// absent. Tracking / Locomotion / PoseSpace are singletons — one instance per state —
    /// while the others may repeat (each repeat is an "instance", distinguished by its
    /// object name).
    /// </summary>
    static class VrcBehaviours
    {
        public const string ParameterDriver = "VRCAvatarParameterDriver";
        public const string PlayAudio = "VRCAnimatorPlayAudio";
        public const string TrackingControl = "VRCAnimatorTrackingControl";
        public const string LocomotionControl = "VRCAnimatorLocomotionControl";
        public const string LayerControl = "VRCAnimatorLayerControl";
        public const string PlayableLayerControl = "VRCPlayableLayerControl";
        public const string TemporaryPoseSpace = "VRCAnimatorTemporaryPoseSpace";

        /// <summary>Menu order mirrors how often the types are reached for.</summary>
        public static readonly string[] All =
        {
            ParameterDriver, PlayAudio, TrackingControl, LocomotionControl,
            LayerControl, PlayableLayerControl, TemporaryPoseSpace,
        };

        /// <summary>Types where a second instance on one state makes no sense.</summary>
        public static bool IsSingleton(string typeName) =>
            typeName == TrackingControl || typeName == LocomotionControl
            || typeName == TemporaryPoseSpace;

        public static bool IsVrcType(string typeName)
        {
            foreach (var name in All)
                if (name == typeName) return true;
            return false;
        }

        public static System.Type Find(string typeName)
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<StateMachineBehaviour>())
                if (!type.IsAbstract && type.Name == typeName)
                    return type;
            return null;
        }

        public static bool SdkAvailable => Find(ParameterDriver) != null;

        public static bool Has(AnimatorState state, string typeName)
        {
            if (state == null) return false;
            foreach (var behaviour in state.behaviours)
                if (behaviour != null && behaviour.GetType().Name == typeName)
                    return true;
            return false;
        }

        public static StateMachineBehaviour Add(AnimatorState state, string typeName)
        {
            var type = Find(typeName);
            if (type == null || state == null) return null;
            Undo.RegisterCompleteObjectUndo(state, "Add Behaviour");
            var behaviour = state.AddStateMachineBehaviour(type);
            EditorUtility.SetDirty(state);
            return behaviour;
        }

        // ---- clipboard --------------------------------------------------------

        // Deep copies detached from any state, so pasting works after the source state (or
        // its behaviours) are gone. Session-scoped; cleared by domain reload.
        static readonly List<StateMachineBehaviour> Clipboard = new List<StateMachineBehaviour>();

        public static int ClipboardCount => Prune();

        public static void Copy(IEnumerable<StateMachineBehaviour> behaviours)
        {
            ClearClipboard();
            foreach (var behaviour in behaviours)
            {
                if (behaviour == null) continue;
                var copy = Object.Instantiate(behaviour);
                copy.name = behaviour.name;
                copy.hideFlags = HideFlags.HideAndDontSave;
                Clipboard.Add(copy);
            }
        }

        public static void ClearClipboard()
        {
            foreach (var copy in Clipboard)
                if (copy != null)
                    Object.DestroyImmediate(copy);
            Clipboard.Clear();
        }

        /// <summary>Drops copies destroyed by a domain reload; returns the live count.</summary>
        static int Prune()
        {
            Clipboard.RemoveAll(copy => copy == null);
            return Clipboard.Count;
        }

        /// <summary>Pastes the clipboard onto a state. Replace destroys the state's existing
        /// behaviours first; append keeps them and adds the copies after.</summary>
        public static void Paste(AnimatorState state, bool replace)
        {
            if (state == null || Prune() == 0) return;
            Undo.RegisterCompleteObjectUndo(state, "Paste Behaviours");
            if (replace)
                foreach (var behaviour in state.behaviours)
                    if (behaviour != null)
                        RemoveFrom(state, behaviour);
            foreach (var copy in Clipboard)
            {
                var added = state.AddStateMachineBehaviour(copy.GetType());
                if (added == null) continue;
                string name = copy.name;
                EditorUtility.CopySerialized(copy, added);
                added.name = name;
                added.hideFlags = HideFlags.None;
            }
            EditorUtility.SetDirty(state);
        }

        /// <summary>Removes one behaviour instance from the state's list and destroys it.</summary>
        public static void RemoveFrom(AnimatorState state, StateMachineBehaviour behaviour)
        {
            var serialized = new SerializedObject(state);
            var array = serialized.FindProperty("m_StateMachineBehaviours");
            if (array != null && array.isArray)
            {
                for (int i = 0; i < array.arraySize; i++)
                {
                    if (array.GetArrayElementAtIndex(i).objectReferenceValue != behaviour) continue;
                    array.DeleteArrayElementAtIndex(i);
                    // Older Unity versions null the slot first; delete again to drop it.
                    if (i < array.arraySize && array.GetArrayElementAtIndex(i).objectReferenceValue == behaviour)
                        array.DeleteArrayElementAtIndex(i);
                    break;
                }
                serialized.ApplyModifiedProperties();
            }
            Undo.DestroyObjectImmediate(behaviour);
        }

        /// <summary>Swaps the behaviour at <paramref name="index"/> with its neighbour.</summary>
        public static void Move(AnimatorState state, int index, int direction)
        {
            var serialized = new SerializedObject(state);
            var array = serialized.FindProperty("m_StateMachineBehaviours");
            if (array == null || !array.isArray) return;
            int target = index + direction;
            if (index < 0 || index >= array.arraySize || target < 0 || target >= array.arraySize)
                return;
            array.MoveArrayElement(index, target);
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(state);
        }
    }
}

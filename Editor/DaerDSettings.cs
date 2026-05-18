using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>User preferences for daerD, backed by EditorPrefs.</summary>
    static class DaerDSettings
    {
        const string Prefix = "Yozolab.DaerD.";

        // --- new transition defaults ---

        public static bool ApplyTransitionDefaults
        {
            get => EditorPrefs.GetBool(Prefix + "ApplyTransitionDefaults", true);
            set => EditorPrefs.SetBool(Prefix + "ApplyTransitionDefaults", value);
        }

        public static bool TransitionHasExitTime
        {
            get => EditorPrefs.GetBool(Prefix + "TransitionHasExitTime", false);
            set => EditorPrefs.SetBool(Prefix + "TransitionHasExitTime", value);
        }

        public static float TransitionExitTime
        {
            get => EditorPrefs.GetFloat(Prefix + "TransitionExitTime", 0.75f);
            set => EditorPrefs.SetFloat(Prefix + "TransitionExitTime", value);
        }

        public static bool TransitionHasFixedDuration
        {
            get => EditorPrefs.GetBool(Prefix + "TransitionHasFixedDuration", true);
            set => EditorPrefs.SetBool(Prefix + "TransitionHasFixedDuration", value);
        }

        public static float TransitionDuration
        {
            get => EditorPrefs.GetFloat(Prefix + "TransitionDuration", 0.25f);
            set => EditorPrefs.SetFloat(Prefix + "TransitionDuration", value);
        }

        public static float TransitionOffset
        {
            get => EditorPrefs.GetFloat(Prefix + "TransitionOffset", 0f);
            set => EditorPrefs.SetFloat(Prefix + "TransitionOffset", value);
        }

        public static TransitionInterruptionSource TransitionInterruption
        {
            get => (TransitionInterruptionSource)EditorPrefs.GetInt(Prefix + "TransitionInterruption",
                (int)TransitionInterruptionSource.None);
            set => EditorPrefs.SetInt(Prefix + "TransitionInterruption", (int)value);
        }

        public static bool TransitionOrderedInterruption
        {
            get => EditorPrefs.GetBool(Prefix + "TransitionOrderedInterruption", true);
            set => EditorPrefs.SetBool(Prefix + "TransitionOrderedInterruption", value);
        }

        public static bool TransitionCanTransitionToSelf
        {
            get => EditorPrefs.GetBool(Prefix + "TransitionCanTransitionToSelf", false);
            set => EditorPrefs.SetBool(Prefix + "TransitionCanTransitionToSelf", value);
        }

        // --- new state defaults ---

        public static bool ApplyStateDefaults
        {
            get => EditorPrefs.GetBool(Prefix + "ApplyStateDefaults", true);
            set => EditorPrefs.SetBool(Prefix + "ApplyStateDefaults", value);
        }

        public static bool StateWriteDefaults
        {
            get => EditorPrefs.GetBool(Prefix + "StateWriteDefaults", true);
            set => EditorPrefs.SetBool(Prefix + "StateWriteDefaults", value);
        }

        public static float StateSpeed
        {
            get => EditorPrefs.GetFloat(Prefix + "StateSpeed", 1f);
            set => EditorPrefs.SetFloat(Prefix + "StateSpeed", value);
        }

        // --- behavior ---

        public static bool InterceptDoubleClick
        {
            get => EditorPrefs.GetBool(Prefix + "InterceptDoubleClick", false);
            set => EditorPrefs.SetBool(Prefix + "InterceptDoubleClick", value);
        }

        public static void ApplyTransitionDefaultsTo(AnimatorStateTransition transition)
        {
            if (transition == null || !ApplyTransitionDefaults) return;
            transition.hasExitTime = TransitionHasExitTime;
            transition.exitTime = TransitionExitTime;
            transition.hasFixedDuration = TransitionHasFixedDuration;
            transition.duration = TransitionDuration;
            transition.offset = TransitionOffset;
            transition.interruptionSource = TransitionInterruption;
            transition.orderedInterruption = TransitionOrderedInterruption;
            transition.canTransitionToSelf = TransitionCanTransitionToSelf;
        }

        public static void ApplyStateDefaultsTo(AnimatorState state)
        {
            if (state == null || !ApplyStateDefaults) return;
            state.writeDefaultValues = StateWriteDefaults;
            state.speed = StateSpeed;
        }

        public static void ResetAll()
        {
            string[] keys =
            {
                "ApplyTransitionDefaults", "TransitionHasExitTime", "TransitionExitTime",
                "TransitionHasFixedDuration", "TransitionDuration", "TransitionOffset",
                "TransitionInterruption", "TransitionOrderedInterruption", "TransitionCanTransitionToSelf",
                "ApplyStateDefaults", "StateWriteDefaults", "StateSpeed", "InterceptDoubleClick",
            };
            foreach (var key in keys)
                EditorPrefs.DeleteKey(Prefix + key);
        }
    }

    /// <summary>Exposes <see cref="DaerDSettings"/> in Edit &gt; Preferences.</summary>
    static class DaerDSettingsProvider
    {
        public const string Path = "Preferences/Yozolab/daerD";

        [SettingsProvider]
        static SettingsProvider Create()
        {
            return new SettingsProvider(Path, SettingsScope.User)
            {
                label = "daerD",
                guiHandler = _ => DrawGui(),
                keywords = new HashSet<string>(new[]
                {
                    "animator", "controller", "transition", "exit time", "duration",
                    "fixed duration", "write defaults", "interruption",
                }),
            };
        }

        static void DrawGui()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("New Transition Defaults", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            DaerDSettings.ApplyTransitionDefaults =
                EditorGUILayout.Toggle("Apply To New Transitions", DaerDSettings.ApplyTransitionDefaults);
            using (new EditorGUI.DisabledScope(!DaerDSettings.ApplyTransitionDefaults))
            {
                DaerDSettings.TransitionHasExitTime =
                    EditorGUILayout.Toggle("Has Exit Time", DaerDSettings.TransitionHasExitTime);
                DaerDSettings.TransitionExitTime =
                    EditorGUILayout.FloatField("Exit Time", DaerDSettings.TransitionExitTime);
                DaerDSettings.TransitionHasFixedDuration =
                    EditorGUILayout.Toggle("Fixed Duration", DaerDSettings.TransitionHasFixedDuration);
                DaerDSettings.TransitionDuration =
                    EditorGUILayout.FloatField("Duration", DaerDSettings.TransitionDuration);
                DaerDSettings.TransitionOffset =
                    EditorGUILayout.FloatField("Offset", DaerDSettings.TransitionOffset);
                DaerDSettings.TransitionInterruption =
                    (TransitionInterruptionSource)EditorGUILayout.EnumPopup("Interruption",
                        DaerDSettings.TransitionInterruption);
                DaerDSettings.TransitionOrderedInterruption =
                    EditorGUILayout.Toggle("Ordered Interruption", DaerDSettings.TransitionOrderedInterruption);
                DaerDSettings.TransitionCanTransitionToSelf =
                    EditorGUILayout.Toggle("Can Transition To Self", DaerDSettings.TransitionCanTransitionToSelf);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("New State Defaults", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            DaerDSettings.ApplyStateDefaults =
                EditorGUILayout.Toggle("Apply To New States", DaerDSettings.ApplyStateDefaults);
            using (new EditorGUI.DisabledScope(!DaerDSettings.ApplyStateDefaults))
            {
                DaerDSettings.StateWriteDefaults =
                    EditorGUILayout.Toggle("Write Defaults", DaerDSettings.StateWriteDefaults);
                DaerDSettings.StateSpeed =
                    EditorGUILayout.FloatField("Speed", DaerDSettings.StateSpeed);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Behavior", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            DaerDSettings.InterceptDoubleClick = EditorGUILayout.Toggle(
                new GUIContent("Intercept .controller Double-Click",
                    "When on, double-clicking an Animator Controller opens this editor instead of Unity's window."),
                DaerDSettings.InterceptDoubleClick);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(10);
            if (GUILayout.Button("Reset To Defaults", GUILayout.Width(160)))
                DaerDSettings.ResetAll();
        }
    }
}

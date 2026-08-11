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

        // --- graph display ---

        // These two are asked once per node and once per edge, every time a layer's graph is
        // built — eight hundred native preference reads to draw a two-hundred-state layer. The
        // rest of the settings on this page are read once per action and stay direct. Held in
        // memory rather than re-read; DaerD's own settings UI is the only writer, and a domain
        // reload starts them over.
        static bool? s_showTransitionConditions;
        static bool? s_showStateBadges;

        /// <summary>Draw a one-line condition summary on single-transition edges.</summary>
        public static bool ShowTransitionConditions
        {
            get => s_showTransitionConditions ??= EditorPrefs.GetBool(Prefix + "ShowTransitionConditions", true);
            set
            {
                s_showTransitionConditions = value;
                EditorPrefs.SetBool(Prefix + "ShowTransitionConditions", value);
            }
        }

        /// <summary>Draw WD / B badges on state nodes (Write Defaults on, has behaviours).</summary>
        public static bool ShowStateBadges
        {
            get => s_showStateBadges ??= EditorPrefs.GetBool(Prefix + "ShowStateBadges", true);
            set
            {
                s_showStateBadges = value;
                EditorPrefs.SetBool(Prefix + "ShowStateBadges", value);
            }
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
                "ShowTransitionConditions", "ShowStateBadges",
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
                    "language", "japanese", "日本語",
                }),
            };
        }

        static void DrawGui()
        {
            EditorGUILayout.Space(4);
            L.Language = (DaerDLanguage)EditorGUILayout.Popup(
                new GUIContent(L.Tr("Language"),
                    L.Tr("Display language for daerD windows and analysis results.")),
                (int)L.Language,
                new[]
                {
                    new GUIContent(L.Tr("Auto (System Language)")),
                    new GUIContent("English"),
                    new GUIContent("日本語"),
                });

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(L.Tr("New Transition Defaults"), EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            DaerDSettings.ApplyTransitionDefaults =
                EditorGUILayout.Toggle(L.Tr("Apply To New Transitions"), DaerDSettings.ApplyTransitionDefaults);
            using (new EditorGUI.DisabledScope(!DaerDSettings.ApplyTransitionDefaults))
            {
                DaerDSettings.TransitionHasExitTime =
                    EditorGUILayout.Toggle(L.Tr("Has Exit Time"), DaerDSettings.TransitionHasExitTime);
                DaerDSettings.TransitionExitTime =
                    EditorGUILayout.FloatField(L.Tr("Exit Time"), DaerDSettings.TransitionExitTime);
                DaerDSettings.TransitionHasFixedDuration =
                    EditorGUILayout.Toggle(L.Tr("Fixed Duration"), DaerDSettings.TransitionHasFixedDuration);
                DaerDSettings.TransitionDuration =
                    EditorGUILayout.FloatField(L.Tr("Duration"), DaerDSettings.TransitionDuration);
                DaerDSettings.TransitionOffset =
                    EditorGUILayout.FloatField(L.Tr("Offset"), DaerDSettings.TransitionOffset);
                DaerDSettings.TransitionInterruption =
                    (TransitionInterruptionSource)EditorGUILayout.EnumPopup(L.Tr("Interruption"),
                        DaerDSettings.TransitionInterruption);
                DaerDSettings.TransitionOrderedInterruption =
                    EditorGUILayout.Toggle(L.Tr("Ordered Interruption"), DaerDSettings.TransitionOrderedInterruption);
                DaerDSettings.TransitionCanTransitionToSelf =
                    EditorGUILayout.Toggle(L.Tr("Can Transition To Self"), DaerDSettings.TransitionCanTransitionToSelf);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(L.Tr("New State Defaults"), EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            DaerDSettings.ApplyStateDefaults =
                EditorGUILayout.Toggle(L.Tr("Apply To New States"), DaerDSettings.ApplyStateDefaults);
            using (new EditorGUI.DisabledScope(!DaerDSettings.ApplyStateDefaults))
            {
                DaerDSettings.StateWriteDefaults =
                    EditorGUILayout.Toggle(L.Tr("Write Defaults"), DaerDSettings.StateWriteDefaults);
                DaerDSettings.StateSpeed =
                    EditorGUILayout.FloatField(L.Tr("Speed"), DaerDSettings.StateSpeed);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(L.Tr("Graph Display"), EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            DaerDSettings.ShowTransitionConditions = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Condition Labels On Edges"),
                    L.Tr("Show a one-line condition summary on transition edges. Takes effect on the next graph rebuild.")),
                DaerDSettings.ShowTransitionConditions);
            DaerDSettings.ShowStateBadges = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("State Badges (WD / B)"),
                    L.Tr("Mark states with Write Defaults ON and states carrying StateMachineBehaviours.")),
                DaerDSettings.ShowStateBadges);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(L.Tr("Behavior"), EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            DaerDSettings.InterceptDoubleClick = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Intercept .controller Double-Click"),
                    L.Tr("When on, double-clicking an Animator Controller opens this editor instead of Unity's window.")),
                DaerDSettings.InterceptDoubleClick);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(10);
            if (GUILayout.Button(L.Tr("Reset To Defaults"), GUILayout.Width(160)))
                DaerDSettings.ResetAll();
        }
    }
}

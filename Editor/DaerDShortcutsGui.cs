using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The shortcut editor on DaerD's preferences page: one row per command, a switch to turn it
    /// off, and a button that listens for the next keystroke.
    ///
    /// Rebinding is a modal moment rather than a text field, because a shortcut is the keystroke
    /// itself and typing its name out is a spelling exercise. The listening row shows what it is
    /// waiting for, Escape gets out, and a keystroke another command already holds is refused by
    /// name instead of quietly stealing it.
    /// </summary>
    static class DaerDShortcutsGui
    {
        // Which row is waiting for a keystroke. None means nothing is listening.
        static DaerDCommand s_listening = DaerDCommand.None;
        static ShortcutScope s_listeningScope;
        static string s_message;

        public static void Draw()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(L.Tr("Keyboard Shortcuts"), EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            EditorGUILayout.LabelField(
                L.Tr("Click a key to change it, or switch a command off to leave the key alone."),
                EditorStyles.wordWrappedMiniLabel);

            DrawScope(L.Tr("Graph"), ShortcutScope.Graph);
            DrawScope(L.Tr("Inspector"), ShortcutScope.Inspector);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(" ", L.Tr("Shift + wheel switches layer, anywhere in the window."),
                EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(s_message))
                EditorGUILayout.HelpBox(s_message, MessageType.Warning);

            EditorGUILayout.Space(4);
            if (GUILayout.Button(L.Tr("Reset Shortcuts"), GUILayout.Width(160)))
            {
                DaerDShortcuts.ResetAll();
                StopListening();
            }
            EditorGUI.indentLevel--;
        }

        static void DrawScope(string title, ShortcutScope scope)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);

            foreach (var shortcut in DaerDShortcuts.In(scope))
            {
                EditorGUILayout.BeginHorizontal();

                // Applied straight away rather than through ExitGUI: the row list being walked
                // is the array Current handed out at the start of the loop, so replacing it
                // underneath is safe, and the repaint that follows the click reads the new one.
                bool enabled = EditorGUILayout.Toggle(shortcut.Binding.Enabled, GUILayout.Width(18));
                if (enabled != shortcut.Binding.Enabled)
                    Apply(scope, shortcut.Command, shortcut.Binding.Switched(enabled));

                using (new EditorGUI.DisabledScope(!enabled))
                    EditorGUILayout.LabelField(L.Tr(shortcut.Description));

                bool listening = s_listening == shortcut.Command && s_listeningScope == scope;
                string label = listening ? L.Tr("Press a key…") : shortcut.Binding.Keys;
                var previous = GUI.backgroundColor;
                if (listening) GUI.backgroundColor = DaerDColors.SelectedRow;
                if (GUILayout.Button(label, EditorStyles.miniButton, GUILayout.Width(110)))
                {
                    if (listening) StopListening();
                    else StartListening(scope, shortcut.Command);
                }
                GUI.backgroundColor = previous;

                EditorGUILayout.EndHorizontal();

                if (listening) Listen(scope, shortcut);
            }
        }

        static void StartListening(ShortcutScope scope, DaerDCommand command)
        {
            s_listeningScope = scope;
            s_listening = command;
            s_message = null;
        }

        static void StopListening()
        {
            s_listening = DaerDCommand.None;
        }

        /// <summary>
        /// Takes the next keystroke as the new binding. Modifier keys on their own are skipped —
        /// holding Ctrl before pressing the letter arrives as a key press of its own, and taking
        /// it would end the capture before the user got to the key they meant.
        /// </summary>
        static void Listen(ShortcutScope scope, DaerDShortcut shortcut)
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;
            if (e.keyCode == KeyCode.None || IsModifier(e.keyCode)) return;

            e.Use();
            if (e.keyCode == KeyCode.Escape)
            {
                StopListening();
                return;
            }

            var binding = new ShortcutBinding(e.keyCode, e.control || e.command, e.shift);
            var clash = DaerDShortcuts.Conflict(scope, shortcut.Command, binding);
            if (clash != DaerDCommand.None)
            {
                s_message = L.Tr("{0} is already {1}.", binding.Keys, L.Tr(DescriptionOf(scope, clash)));
                StopListening();
                return;
            }

            Apply(scope, shortcut.Command, binding);
        }

        static void Apply(ShortcutScope scope, DaerDCommand command, ShortcutBinding binding)
        {
            DaerDShortcuts.Rebind(scope, command, binding);
            StopListening();
            s_message = null;
        }

        static string DescriptionOf(ShortcutScope scope, DaerDCommand command)
        {
            foreach (var shortcut in DaerDShortcuts.In(scope))
                if (shortcut.Command == command) return shortcut.Description;
            return command.ToString();
        }

        static bool IsModifier(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:
                case KeyCode.LeftCommand:
                case KeyCode.RightCommand:
                case KeyCode.LeftWindows:
                case KeyCode.RightWindows:
                    return true;
                default:
                    return false;
            }
        }
    }
}

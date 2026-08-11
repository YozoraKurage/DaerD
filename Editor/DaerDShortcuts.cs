using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>Where a shortcut applies. The same keys mean different things in the graph and
    /// in the inspector, which is why a scope is part of resolving one.</summary>
    enum ShortcutScope
    {
        Graph,
        Inspector,
    }

    /// <summary>Every command a key can reach. Handlers switch on these, never on key codes.</summary>
    enum DaerDCommand
    {
        None = 0,

        Rename,
        RenameClip,
        SelectIncoming,
        SelectOutgoing,
        SelectConnected,
        FrameSelection,
        FrameAll,
        MarkSources,
        Connect,
        MoveUp,
        MoveDown,
        MoveLeft,
        MoveRight,
        Copy,
        Paste,
        PasteAsNew,
        Duplicate,
        SelectAllNodes,
        SelectAllTransitions,
        FocusSearch,
    }

    /// <summary>One keystroke, or the absence of one.</summary>
    readonly struct ShortcutBinding
    {
        public readonly KeyCode Key;
        public readonly bool Ctrl;
        public readonly bool Shift;
        /// <summary>False means the command has been switched off and no key reaches it.</summary>
        public readonly bool Enabled;

        public ShortcutBinding(KeyCode key, bool ctrl = false, bool shift = false, bool enabled = true)
        {
            Key = key;
            Ctrl = ctrl;
            Shift = shift;
            Enabled = enabled;
        }

        public ShortcutBinding Switched(bool enabled) => new ShortcutBinding(Key, Ctrl, Shift, enabled);

        public bool Matches(KeyCode key, bool ctrl, bool shift) =>
            Enabled && Key == key && Ctrl == ctrl && Shift == shift;

        public bool SameKeys(ShortcutBinding other) =>
            Key == other.Key && Ctrl == other.Ctrl && Shift == other.Shift;

        /// <summary>"Ctrl+Shift+A" — Cmd rather than Ctrl on a Mac, where that is the key used.</summary>
        public string Keys
        {
            get
            {
                string text = string.Empty;
                if (Ctrl) text += Application.platform == RuntimePlatform.OSXEditor ? "Cmd+" : "Ctrl+";
                if (Shift) text += "Shift+";
                return text + KeyName(Key);
            }
        }

        static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.UpArrow: return "↑";
                case KeyCode.DownArrow: return "↓";
                case KeyCode.LeftArrow: return "←";
                case KeyCode.RightArrow: return "→";
                default: return key.ToString();
            }
        }

        /// <summary>
        /// How the binding is stored. Readable rather than packed — a preference somebody may
        /// have to look at, or clear by hand, is worth being able to read.
        /// </summary>
        public string Serialize()
        {
            string modifiers = (Ctrl ? "C" : string.Empty) + (Shift ? "S" : string.Empty);
            return (Enabled ? string.Empty : "-") + Key + ":" + modifiers;
        }

        public static bool TryParse(string text, out ShortcutBinding binding)
        {
            binding = default;
            if (string.IsNullOrEmpty(text)) return false;

            bool enabled = true;
            if (text[0] == '-')
            {
                enabled = false;
                text = text.Substring(1);
            }

            int split = text.IndexOf(':');
            if (split < 0) return false;
            if (!System.Enum.TryParse(text.Substring(0, split), out KeyCode key)) return false;

            string modifiers = text.Substring(split + 1);
            binding = new ShortcutBinding(key, modifiers.Contains("C"), modifiers.Contains("S"), enabled);
            return true;
        }
    }

    readonly struct DaerDShortcut
    {
        public readonly ShortcutScope Scope;
        public readonly DaerDCommand Command;
        public readonly ShortcutBinding Binding;
        /// <summary>English source text, translated where it is shown.</summary>
        public readonly string Description;

        public DaerDShortcut(ShortcutScope scope, DaerDCommand command, ShortcutBinding binding,
            string description)
        {
            Scope = scope;
            Command = command;
            Binding = binding;
            Description = description;
        }

        public DaerDShortcut Rebound(ShortcutBinding binding) =>
            new DaerDShortcut(Scope, Command, binding, Description);
    }

    /// <summary>
    /// The one list of what every key does, and the only thing that changes it. Handlers ask what
    /// a keystroke means and switch on the answer, so a key code appears once — which is also
    /// what lets the settings page edit them all and a test refuse two commands on one keystroke.
    ///
    /// Not here: keys that belong to a control rather than to a command. Escape and Return while
    /// renaming a node, or the arrows inside the search box, are part of how that widget works
    /// and mean nothing outside it. Shift+wheel (switch layer) is not a key press at all; it is
    /// claimed on the window root, above every panel.
    /// </summary>
    static class DaerDShortcuts
    {
        const string PrefPrefix = "Yozolab.DaerD.Shortcut.";

        public static readonly DaerDShortcut[] Defaults =
        {
            Graph(DaerDCommand.Rename, KeyCode.F2, "Rename the selected state, frame or note"),
            Graph(DaerDCommand.RenameClip, KeyCode.F2, "Rename the selected state's clip", ctrl: true),
            Graph(DaerDCommand.SelectIncoming, KeyCode.I, "Select the incoming transitions"),
            Graph(DaerDCommand.SelectOutgoing, KeyCode.O, "Select the outgoing transitions"),
            Graph(DaerDCommand.SelectConnected, KeyCode.P, "Select every connected transition"),
            Graph(DaerDCommand.FrameSelection, KeyCode.F, "Fit the selection in view"),
            Graph(DaerDCommand.FrameAll, KeyCode.A, "Fit the whole graph in view"),
            Graph(DaerDCommand.MarkSources, KeyCode.M, "Mark the selection as transition sources"),
            Graph(DaerDCommand.Connect, KeyCode.T,
                "Connect: marked sources to the selection, or the selection in click order"),
            Graph(DaerDCommand.MoveUp, KeyCode.UpArrow, "Move the selection to the node above"),
            Graph(DaerDCommand.MoveDown, KeyCode.DownArrow, "Move the selection to the node below"),
            Graph(DaerDCommand.MoveLeft, KeyCode.LeftArrow, "Move the selection to the node on the left"),
            Graph(DaerDCommand.MoveRight, KeyCode.RightArrow, "Move the selection to the node on the right"),
            Graph(DaerDCommand.Copy, KeyCode.C, "Copy the selected states", ctrl: true),
            Graph(DaerDCommand.Paste, KeyCode.V, "Paste states", ctrl: true),
            Graph(DaerDCommand.PasteAsNew, KeyCode.V, "Paste the copied transition as a new one",
                ctrl: true, shift: true),
            Graph(DaerDCommand.Duplicate, KeyCode.D, "Duplicate the selected states", ctrl: true),
            Graph(DaerDCommand.SelectAllNodes, KeyCode.A, "Select every node", ctrl: true),
            Graph(DaerDCommand.SelectAllTransitions, KeyCode.A, "Select every transition",
                ctrl: true, shift: true),
            Graph(DaerDCommand.FocusSearch, KeyCode.F, "Jump to the search box", ctrl: true),

            Inspector(DaerDCommand.Copy, KeyCode.C, "Copy the selected transitions or behaviours", ctrl: true),
            Inspector(DaerDCommand.Paste, KeyCode.V, "Paste onto the selected transitions or behaviours",
                ctrl: true),
            Inspector(DaerDCommand.PasteAsNew, KeyCode.V, "Paste as new transitions alongside the selected ones",
                ctrl: true, shift: true),
        };

        static DaerDShortcut Graph(DaerDCommand command, KeyCode key, string description,
            bool ctrl = false, bool shift = false) =>
            new DaerDShortcut(ShortcutScope.Graph, command, new ShortcutBinding(key, ctrl, shift), description);

        static DaerDShortcut Inspector(DaerDCommand command, KeyCode key, string description,
            bool ctrl = false, bool shift = false) =>
            new DaerDShortcut(ShortcutScope.Inspector, command, new ShortcutBinding(key, ctrl, shift), description);

        // Rebuilt from the preferences on first use and after every change; a key event asks this
        // many times a second and EditorPrefs is not free.
        static DaerDShortcut[] s_current;

        /// <summary>Every shortcut as it stands now: the defaults with the user's changes applied.</summary>
        public static DaerDShortcut[] Current
        {
            get
            {
                if (s_current != null) return s_current;
                s_current = new DaerDShortcut[Defaults.Length];
                for (int i = 0; i < Defaults.Length; i++)
                {
                    var stored = EditorPrefs.GetString(PrefKey(Defaults[i]), string.Empty);
                    s_current[i] = ShortcutBinding.TryParse(stored, out var binding)
                        ? Defaults[i].Rebound(binding)
                        : Defaults[i];
                }
                return s_current;
            }
        }

        static string PrefKey(DaerDShortcut shortcut) =>
            PrefPrefix + shortcut.Scope + "." + shortcut.Command;

        /// <summary>Changes one binding. The caller has already decided what to do about any
        /// clash; see <see cref="Conflict"/>.</summary>
        public static void Rebind(ShortcutScope scope, DaerDCommand command, ShortcutBinding binding)
        {
            foreach (var shortcut in Defaults)
            {
                if (shortcut.Scope != scope || shortcut.Command != command) continue;
                if (binding.SameKeys(shortcut.Binding) && binding.Enabled == shortcut.Binding.Enabled)
                    EditorPrefs.DeleteKey(PrefKey(shortcut));   // back to the default: store nothing
                else
                    EditorPrefs.SetString(PrefKey(shortcut), binding.Serialize());
                s_current = null;
                return;
            }
        }

        public static void ResetAll()
        {
            foreach (var shortcut in Defaults)
                EditorPrefs.DeleteKey(PrefKey(shortcut));
            s_current = null;
        }

        /// <summary>
        /// The command already bound to <paramref name="binding"/> in the same scope, or None.
        /// Disabled commands hold no keys, so they never clash.
        /// </summary>
        public static DaerDCommand Conflict(IList<DaerDShortcut> table, ShortcutScope scope,
            DaerDCommand command, ShortcutBinding binding)
        {
            if (!binding.Enabled) return DaerDCommand.None;
            foreach (var shortcut in table)
            {
                if (shortcut.Scope != scope || shortcut.Command == command) continue;
                if (shortcut.Binding.Enabled && shortcut.Binding.SameKeys(binding)) return shortcut.Command;
            }
            return DaerDCommand.None;
        }

        public static DaerDCommand Conflict(ShortcutScope scope, DaerDCommand command, ShortcutBinding binding) =>
            Conflict(Current, scope, command, binding);

        /// <summary>
        /// What a keystroke means in <paramref name="scope"/>, or None. <paramref name="ctrl"/> is
        /// the caller's own reading of Control-or-Command: the two are one key to a user, and
        /// which one it physically was is the caller's business, not this table's.
        /// </summary>
        public static DaerDCommand Resolve(IList<DaerDShortcut> table, ShortcutScope scope,
            KeyCode key, bool ctrl, bool shift)
        {
            foreach (var shortcut in table)
                if (shortcut.Scope == scope && shortcut.Binding.Matches(key, ctrl, shift))
                    return shortcut.Command;
            return DaerDCommand.None;
        }

        public static DaerDCommand Resolve(ShortcutScope scope, KeyCode key, bool ctrl, bool shift) =>
            Resolve(Current, scope, key, ctrl, shift);

        public static DaerDCommand Resolve(ShortcutScope scope, KeyDownEvent evt) =>
            Resolve(scope, evt.keyCode, evt.ctrlKey || evt.commandKey, evt.shiftKey);

        /// <summary>The IMGUI reading of the same question. Null and non-key events are None, so
        /// callers can ask before checking anything else.</summary>
        public static DaerDCommand Resolve(ShortcutScope scope, Event imguiEvent)
        {
            if (imguiEvent == null || imguiEvent.type != EventType.KeyDown) return DaerDCommand.None;
            return Resolve(scope, imguiEvent.keyCode,
                imguiEvent.control || imguiEvent.command, imguiEvent.shift);
        }

        /// <summary>The shortcuts of one scope, in table order, for showing to the user.</summary>
        public static IEnumerable<DaerDShortcut> In(ShortcutScope scope)
        {
            foreach (var shortcut in Current)
                if (shortcut.Scope == scope) yield return shortcut;
        }
    }
}

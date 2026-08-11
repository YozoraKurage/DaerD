using System.Collections.Generic;
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

        // graph
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

    readonly struct DaerDShortcut
    {
        public readonly ShortcutScope Scope;
        public readonly DaerDCommand Command;
        public readonly KeyCode Key;
        public readonly bool Ctrl;
        public readonly bool Shift;
        /// <summary>English source text, translated where it is shown.</summary>
        public readonly string Description;

        public DaerDShortcut(ShortcutScope scope, DaerDCommand command, KeyCode key, bool ctrl, bool shift,
            string description)
        {
            Scope = scope;
            Command = command;
            Key = key;
            Ctrl = ctrl;
            Shift = shift;
            Description = description;
        }

        /// <summary>"Ctrl+Shift+V" — Cmd rather than Ctrl on a Mac, where that is the key used.</summary>
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
    }

    /// <summary>
    /// The one list of what every key does. Handlers ask this what a keystroke means and switch
    /// on the answer, so a binding is stated once instead of being spelled out at the place that
    /// happens to implement it — which is also what lets the settings page list them all and a
    /// test refuse two commands on the same keystroke.
    ///
    /// Not here: keys that belong to a control rather than to a command. Escape and Return while
    /// renaming a node, or the arrows inside the search box, are part of how that widget works
    /// and mean nothing outside it. Shift+wheel (switch layer) is not a key press at all; it is
    /// claimed on the window root, above every panel.
    /// </summary>
    static class DaerDShortcuts
    {
        public static readonly DaerDShortcut[] All =
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
            new DaerDShortcut(ShortcutScope.Graph, command, key, ctrl, shift, description);

        static DaerDShortcut Inspector(DaerDCommand command, KeyCode key, string description,
            bool ctrl = false, bool shift = false) =>
            new DaerDShortcut(ShortcutScope.Inspector, command, key, ctrl, shift, description);

        /// <summary>
        /// What a keystroke means in <paramref name="scope"/>, or None. <paramref name="ctrl"/> is
        /// the caller's own reading of Control-or-Command: the two are one key to a user, and
        /// which one it physically was is the caller's business, not this table's.
        /// </summary>
        public static DaerDCommand Resolve(ShortcutScope scope, KeyCode key, bool ctrl, bool shift)
        {
            foreach (var shortcut in All)
                if (shortcut.Scope == scope && shortcut.Key == key
                    && shortcut.Ctrl == ctrl && shortcut.Shift == shift)
                    return shortcut.Command;
            return DaerDCommand.None;
        }

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
            foreach (var shortcut in All)
                if (shortcut.Scope == scope) yield return shortcut;
        }
    }
}

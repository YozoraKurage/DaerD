using System.Collections.Generic;
using UnityEditor.ShortcutManagement;
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
        /// <summary>
        /// The id this command is registered under with Unity's shortcut manager, or null when
        /// it is handled here instead. Registered means the user can rebind it in Edit &gt;
        /// Shortcuts, and that the keys below are only the default.
        /// </summary>
        public readonly string Id;

        public bool Rebindable => Id != null;

        public DaerDShortcut(ShortcutScope scope, DaerDCommand command, KeyCode key, bool ctrl, bool shift,
            string description, string id = null)
        {
            Scope = scope;
            Command = command;
            Key = key;
            Ctrl = ctrl;
            Shift = shift;
            Description = description;
            Id = id;
        }

        /// <summary>
        /// What is bound right now: the user's binding for a registered command, the default for
        /// the rest. Asked of the shortcut manager rather than remembered, because the answer is
        /// theirs to change and a remembered copy would be a second, wrong one.
        /// </summary>
        public string CurrentKeys
        {
            get
            {
                if (!Rebindable) return Keys;
                try
                {
                    string bound = ShortcutManager.instance.GetShortcutBinding(Id).ToString();
                    return string.IsNullOrEmpty(bound) ? L.Tr("(unbound)") : bound;
                }
                catch (System.Exception)
                {
                    // The id is only known once the assembly has been scanned for it; before
                    // that (and in a batch-mode run) fall back to what it was declared with.
                    return Keys;
                }
            }
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
            Graph(DaerDCommand.Rename, KeyCode.F2, "Rename the selected state, frame or note",
                id: Ids.Rename),
            Graph(DaerDCommand.RenameClip, KeyCode.F2, "Rename the selected state's clip",
                ctrl: true, id: Ids.RenameClip),
            Graph(DaerDCommand.SelectIncoming, KeyCode.I, "Select the incoming transitions",
                id: Ids.SelectIncoming),
            Graph(DaerDCommand.SelectOutgoing, KeyCode.O, "Select the outgoing transitions",
                id: Ids.SelectOutgoing),
            Graph(DaerDCommand.SelectConnected, KeyCode.P, "Select every connected transition",
                id: Ids.SelectConnected),
            Graph(DaerDCommand.FrameSelection, KeyCode.F, "Fit the selection in view",
                id: Ids.FrameSelection),
            Graph(DaerDCommand.FrameAll, KeyCode.A, "Fit the whole graph in view", id: Ids.FrameAll),
            Graph(DaerDCommand.MarkSources, KeyCode.M, "Mark the selection as transition sources",
                id: Ids.MarkSources),
            Graph(DaerDCommand.Connect, KeyCode.T,
                "Connect: marked sources to the selection, or the selection in click order",
                id: Ids.Connect),
            Graph(DaerDCommand.MoveUp, KeyCode.UpArrow, "Move the selection to the node above",
                id: Ids.MoveUp),
            Graph(DaerDCommand.MoveDown, KeyCode.DownArrow, "Move the selection to the node below",
                id: Ids.MoveDown),
            Graph(DaerDCommand.MoveLeft, KeyCode.LeftArrow, "Move the selection to the node on the left",
                id: Ids.MoveLeft),
            Graph(DaerDCommand.MoveRight, KeyCode.RightArrow, "Move the selection to the node on the right",
                id: Ids.MoveRight),
            Graph(DaerDCommand.Duplicate, KeyCode.D, "Duplicate the selected states",
                ctrl: true, id: Ids.Duplicate),
            Graph(DaerDCommand.SelectAllNodes, KeyCode.A, "Select every node",
                ctrl: true, id: Ids.SelectAllNodes),
            Graph(DaerDCommand.SelectAllTransitions, KeyCode.A, "Select every transition",
                ctrl: true, shift: true, id: Ids.SelectAllTransitions),
            Graph(DaerDCommand.FocusSearch, KeyCode.F, "Jump to the search box",
                ctrl: true, id: Ids.FocusSearch),

            // The copy / paste family is deliberately NOT registered. A registered shortcut
            // belongs to the whole window, and these three are the keys whose meaning depends on
            // which pane is focused: Ctrl+C copies states in the graph and the selected
            // transitions or behaviours in the inspector. Handing them to the window would pick
            // one of those and silence the other.
            Graph(DaerDCommand.Copy, KeyCode.C, "Copy the selected states", ctrl: true),
            Graph(DaerDCommand.Paste, KeyCode.V, "Paste states", ctrl: true),
            Graph(DaerDCommand.PasteAsNew, KeyCode.V, "Paste the copied transition as a new one",
                ctrl: true, shift: true),

            Inspector(DaerDCommand.Copy, KeyCode.C, "Copy the selected transitions or behaviours", ctrl: true),
            Inspector(DaerDCommand.Paste, KeyCode.V, "Paste onto the selected transitions or behaviours",
                ctrl: true),
            Inspector(DaerDCommand.PasteAsNew, KeyCode.V, "Paste as new transitions alongside the selected ones",
                ctrl: true, shift: true),
        };

        /// <summary>
        /// The ids Unity stores the user's bindings under. Spelled out as constants because a
        /// binding is remembered by id: renaming one silently throws away everybody's
        /// customisation of it, so they are worth being deliberate about.
        /// </summary>
        public static class Ids
        {
            const string Prefix = "DaerD/";
            public const string Rename = Prefix + "Rename";
            public const string RenameClip = Prefix + "Rename Clip";
            public const string SelectIncoming = Prefix + "Select Incoming Transitions";
            public const string SelectOutgoing = Prefix + "Select Outgoing Transitions";
            public const string SelectConnected = Prefix + "Select Connected Transitions";
            public const string FrameSelection = Prefix + "Frame Selection";
            public const string FrameAll = Prefix + "Frame All";
            public const string MarkSources = Prefix + "Mark Transition Sources";
            public const string Connect = Prefix + "Connect";
            public const string MoveUp = Prefix + "Move Selection Up";
            public const string MoveDown = Prefix + "Move Selection Down";
            public const string MoveLeft = Prefix + "Move Selection Left";
            public const string MoveRight = Prefix + "Move Selection Right";
            public const string Duplicate = Prefix + "Duplicate States";
            public const string SelectAllNodes = Prefix + "Select All Nodes";
            public const string SelectAllTransitions = Prefix + "Select All Transitions";
            public const string FocusSearch = Prefix + "Focus Search";
        }

        static DaerDShortcut Graph(DaerDCommand command, KeyCode key, string description,
            bool ctrl = false, bool shift = false, string id = null) =>
            new DaerDShortcut(ShortcutScope.Graph, command, key, ctrl, shift, description, id);

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
            {
                // A registered command arrives through Unity's shortcut manager, at whatever key
                // the user has bound it to. Answering for its default here as well would run it
                // twice on a stock install, and on the old key after a rebind.
                if (shortcut.Rebindable) continue;
                if (shortcut.Scope == scope && shortcut.Key == key
                    && shortcut.Ctrl == ctrl && shortcut.Shift == shift)
                    return shortcut.Command;
            }
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

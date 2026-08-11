using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Registers DaerD's graph commands with Unity's shortcut manager, so they show up in
    /// Edit &gt; Shortcuts and can be rebound there like any other editor shortcut — including
    /// having their conflicts pointed out, which is a thing this package cannot do for itself.
    ///
    /// Every entry is the same three lines: an id from <see cref="DaerDShortcuts.Ids"/>, the
    /// default keys, and the command to run. The default has to be repeated in the attribute
    /// because an attribute argument must be a constant; a test compares the two lists so they
    /// cannot drift apart.
    ///
    /// The context is <see cref="DaerDWindow"/>, so these are live only while that window has
    /// focus and never while a text field is being typed into.
    /// </summary>
    static class DaerDShortcutBindings
    {
        static void Run(ShortcutArguments args, DaerDCommand command) =>
            (args.context as DaerDWindow)?.RunShortcut(command);

        [Shortcut(DaerDShortcuts.Ids.Rename, typeof(DaerDWindow), KeyCode.F2)]
        static void Rename(ShortcutArguments args) => Run(args, DaerDCommand.Rename);

        [Shortcut(DaerDShortcuts.Ids.RenameClip, typeof(DaerDWindow), KeyCode.F2, ShortcutModifiers.Action)]
        static void RenameClip(ShortcutArguments args) => Run(args, DaerDCommand.RenameClip);

        [Shortcut(DaerDShortcuts.Ids.SelectIncoming, typeof(DaerDWindow), KeyCode.I)]
        static void SelectIncoming(ShortcutArguments args) => Run(args, DaerDCommand.SelectIncoming);

        [Shortcut(DaerDShortcuts.Ids.SelectOutgoing, typeof(DaerDWindow), KeyCode.O)]
        static void SelectOutgoing(ShortcutArguments args) => Run(args, DaerDCommand.SelectOutgoing);

        [Shortcut(DaerDShortcuts.Ids.SelectConnected, typeof(DaerDWindow), KeyCode.P)]
        static void SelectConnected(ShortcutArguments args) => Run(args, DaerDCommand.SelectConnected);

        [Shortcut(DaerDShortcuts.Ids.FrameSelection, typeof(DaerDWindow), KeyCode.F)]
        static void FrameSelection(ShortcutArguments args) => Run(args, DaerDCommand.FrameSelection);

        [Shortcut(DaerDShortcuts.Ids.FrameAll, typeof(DaerDWindow), KeyCode.A)]
        static void FrameAll(ShortcutArguments args) => Run(args, DaerDCommand.FrameAll);

        [Shortcut(DaerDShortcuts.Ids.MarkSources, typeof(DaerDWindow), KeyCode.M)]
        static void MarkSources(ShortcutArguments args) => Run(args, DaerDCommand.MarkSources);

        [Shortcut(DaerDShortcuts.Ids.Connect, typeof(DaerDWindow), KeyCode.T)]
        static void Connect(ShortcutArguments args) => Run(args, DaerDCommand.Connect);

        [Shortcut(DaerDShortcuts.Ids.MoveUp, typeof(DaerDWindow), KeyCode.UpArrow)]
        static void MoveUp(ShortcutArguments args) => Run(args, DaerDCommand.MoveUp);

        [Shortcut(DaerDShortcuts.Ids.MoveDown, typeof(DaerDWindow), KeyCode.DownArrow)]
        static void MoveDown(ShortcutArguments args) => Run(args, DaerDCommand.MoveDown);

        [Shortcut(DaerDShortcuts.Ids.MoveLeft, typeof(DaerDWindow), KeyCode.LeftArrow)]
        static void MoveLeft(ShortcutArguments args) => Run(args, DaerDCommand.MoveLeft);

        [Shortcut(DaerDShortcuts.Ids.MoveRight, typeof(DaerDWindow), KeyCode.RightArrow)]
        static void MoveRight(ShortcutArguments args) => Run(args, DaerDCommand.MoveRight);

        [Shortcut(DaerDShortcuts.Ids.Duplicate, typeof(DaerDWindow), KeyCode.D, ShortcutModifiers.Action)]
        static void Duplicate(ShortcutArguments args) => Run(args, DaerDCommand.Duplicate);

        [Shortcut(DaerDShortcuts.Ids.SelectAllNodes, typeof(DaerDWindow), KeyCode.A, ShortcutModifiers.Action)]
        static void SelectAllNodes(ShortcutArguments args) => Run(args, DaerDCommand.SelectAllNodes);

        [Shortcut(DaerDShortcuts.Ids.SelectAllTransitions, typeof(DaerDWindow), KeyCode.A,
            ShortcutModifiers.Action | ShortcutModifiers.Shift)]
        static void SelectAllTransitions(ShortcutArguments args) => Run(args, DaerDCommand.SelectAllTransitions);

        [Shortcut(DaerDShortcuts.Ids.FocusSearch, typeof(DaerDWindow), KeyCode.F, ShortcutModifiers.Action)]
        static void FocusSearch(ShortcutArguments args) => Run(args, DaerDCommand.FocusSearch);
    }
}

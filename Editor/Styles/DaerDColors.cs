using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Every colour DaerD paints from code, named for what it MEANS rather than what it looks
    /// like. The names are the point: "selected", "playing", "found by the query" survive a
    /// change of palette, while "blue" and "green" become lies the first time one is adjusted.
    ///
    /// Before this file the same colour was written out in up to four places — the selection
    /// blue in the edge, the frame, the note and the reorder line — and two of those had
    /// already drifted a notch apart while meaning the same thing. What a colour is FOR is now
    /// stated once, and a test refuses to let a new literal be scattered back out.
    ///
    /// What is NOT here: the graph background, the panel chrome, the row stripes. Those live in
    /// <c>Editor/Styles/DaerD.uss</c> and are unreachable from C# — UIElements resolves them
    /// itself. The two halves are listed together at the bottom so the whole vocabulary can be
    /// found from one place even though it cannot be defined in one.
    ///
    /// Everything here except <see cref="SyncMark"/> and its neighbours assumes the dark editor
    /// theme, which is a decision nobody has made on purpose. See the light-theme note below.
    /// </summary>
    static class DaerDColors
    {
        // ---- what is happening to a thing ------------------------------------

        /// <summary>Selected — in the graph, on a frame, on a note, under a drag.</summary>
        public static readonly Color Selected = new Color(0.40f, 0.70f, 1.00f);

        /// <summary>The selected row of a list panel. Softer than <see cref="Selected"/>
        /// because it fills a whole row rather than outlining one thing.</summary>
        public static readonly Color SelectedRow = new Color(0.40f, 0.60f, 0.90f);

        /// <summary>Matched by the find-usages query: which states and edges mention the
        /// parameter that was asked about.</summary>
        public static readonly Color FoundByQuery = new Color(0.96f, 0.84f, 0.22f);

        /// <summary>The pointer is over the row that names this edge. A hue of its own on
        /// purpose: hover is the answer to "which line is this row talking about", and reusing
        /// the selection blue or the query yellow would make it look like a state, not a
        /// gesture that ends when the pointer moves away.</summary>
        public static readonly Color Hovered = new Color(0.85f, 0.55f, 1.00f);

        /// <summary>The running Animator is in this state.</summary>
        public static readonly Color Playing = new Color(0.20f, 0.55f, 0.25f);

        /// <summary>Where a running transition is headed — the same green held back, so the two
        /// ends of a crossfade read as one thing happening rather than two states at once.</summary>
        public static readonly Color PlayingNext = new Color(0.16f, 0.36f, 0.20f);

        /// <summary>The transition running right now. Brighter than <see cref="Playing"/>: a
        /// line has less area to say it with.</summary>
        public static readonly Color PlayingEdge = new Color(0.35f, 0.85f, 0.45f);

        /// <summary>A clip dragged over a state will land here. Deliberately NOT green: it used
        /// to be, a shade away from <see cref="PlayingEdge"/>, and the two appear on the same
        /// graph meaning entirely different things.</summary>
        public static readonly Color DropTarget = new Color(0.30f, 0.80f, 0.85f);

        /// <summary>This line cannot fire: every transition on it is muted, or another
        /// transition leaving the same node is soloed and shuts the rest out.</summary>
        public static readonly Color Muted = new Color(0.80f, 0.32f, 0.32f);

        /// <summary>The layer's default state, and the Entry edge that reaches it.</summary>
        public static readonly Color DefaultState = new Color(0.78f, 0.45f, 0.13f);

        public static readonly Color DefaultEdge = new Color(0.93f, 0.63f, 0.26f);

        /// <summary>A transition with nothing else to say about it.</summary>
        public static readonly Color Edge = new Color(0.80f, 0.80f, 0.80f);

        /// <summary>Something is wrong with this row: a parameter nothing reads, a sync budget
        /// gone over. One tint, not two — the two it replaced were a tenth apart, which nobody
        /// can read as a severity.</summary>
        public static readonly Color Warning = new Color(1f, 0.50f, 0.50f);

        /// <summary>True of some of the multi-selection but not all of it.</summary>
        public static readonly Color Partial = new Color(1f, 0.85f, 0.40f);

        /// <summary>An either-or button in the behaviour inspector, showing which side is on.</summary>
        public static readonly Color ToggleOn = new Color(0.55f, 0.85f, 0.55f);

        // ---- state-node badges -----------------------------------------------

        public static readonly Color BadgeWriteDefaultsOn = new Color(0.47f, 0.78f, 0.51f);
        public static readonly Color BadgeBehavioursOn = new Color(0.55f, 0.67f, 0.94f);
        public static readonly Color BadgeOff = new Color(0.42f, 0.42f, 0.42f);

        // ---- what KIND of thing a node is ------------------------------------

        /// <summary>Opaque #393939. The stock node-border and port columns are translucent, so
        /// the graph background showed through and overlapping states bled into each other.</summary>
        public static readonly Color StateBody = new Color(0.224f, 0.224f, 0.224f);

        public static readonly Color SubStateMachineHeader = new Color(0.20f, 0.34f, 0.46f);
        public static readonly Color BlendTreeRootHeader = new Color(0.32f, 0.42f, 0.62f);
        public static readonly Color BlendTreeNestedHeader = new Color(0.38f, 0.32f, 0.55f);
        public static readonly Color BlendTreeChildHeader = new Color(0.36f, 0.50f, 0.32f);

        /// <summary>A blend tree child slot with no motion in it.</summary>
        public static readonly Color BlendTreeEmptyHeader = new Color(0.45f, 0.32f, 0.32f);

        public static readonly Color EntryNode = new Color(0.27f, 0.43f, 0.27f);
        public static readonly Color ExitNode = new Color(0.46f, 0.27f, 0.27f);
        public static readonly Color AnyStateNode = new Color(0.30f, 0.40f, 0.46f);

        // ---- panel chrome ----------------------------------------------------

        public static readonly Color Grip = new Color(0.50f, 0.50f, 0.50f);
        public static readonly Color GripHover = new Color(0.80f, 0.80f, 0.80f);

        /// <summary>The row being dragged, and the row the pointer is resting on. Both are
        /// <see cref="SelectedRow"/> at a weight that leaves the text readable.</summary>
        public static readonly Color DragRow = Fade(SelectedRow, 0.22f);
        public static readonly Color SelectedRowFill = Fade(SelectedRow, 0.35f);
        public static readonly Color FocusedRowFill = new Color(0.30f, 0.30f, 0.35f, 0.40f);

        /// <summary>A separator drawn as a dark line rather than a light one, so it reads as a
        /// gap in the panel rather than a control in it.</summary>
        public static readonly Color Separator = new Color(0f, 0f, 0f, 0.35f);

        // ---- the async-sync timeline -----------------------------------------
        //
        // The only corner of DaerD that answers to the editor theme, which is why these are
        // properties rather than constants — isProSkin has to be read at draw time. Everything
        // above assumes the dark theme. That is not a decision anyone made; it is where the
        // colours happened to be written. Deciding it is its own piece of work.

        /// <summary>A step where this target sends.</summary>
        public static Color SyncMark => EditorGUIUtility.isProSkin
            ? new Color(0.35f, 0.65f, 0.95f)
            : new Color(0.20f, 0.45f, 0.80f);

        /// <summary>A step that breaks one of the schedule's rules.</summary>
        public static Color SyncClash => EditorGUIUtility.isProSkin
            ? new Color(0.85f, 0.35f, 0.30f)
            : new Color(0.75f, 0.20f, 0.15f);

        /// <summary>The lane a target's marks are laid along.</summary>
        public static Color SyncTrack => EditorGUIUtility.isProSkin
            ? new Color(1f, 1f, 1f, 0.06f)
            : new Color(0f, 0f, 0f, 0.06f);

        // ---- colours the user picks ------------------------------------------
        //
        // Not vocabulary — data. A frame is whatever colour its author chose, and these are the
        // choices on offer. The defaults new frames and notes are born with come from the front
        // of each list rather than being written out again, which is how a palette edit stops
        // leaving the default behind.

        public static readonly (string name, Color color)[] FramePalette =
        {
            ("Blue", new Color(0.32f, 0.45f, 0.60f)),
            ("Green", new Color(0.34f, 0.55f, 0.36f)),
            ("Orange", new Color(0.74f, 0.51f, 0.20f)),
            ("Purple", new Color(0.52f, 0.40f, 0.65f)),
            ("Red", new Color(0.65f, 0.32f, 0.32f)),
            ("Gray", new Color(0.45f, 0.45f, 0.45f)),
        };

        public static readonly (string name, Color color)[] NotePalette =
        {
            ("Yellow", new Color(0.93f, 0.86f, 0.51f)),
            ("Green", new Color(0.72f, 0.86f, 0.55f)),
            ("Blue", new Color(0.62f, 0.78f, 0.92f)),
            ("Pink", new Color(0.93f, 0.68f, 0.77f)),
            ("Gray", new Color(0.78f, 0.78f, 0.78f)),
        };

        public static Color DefaultFrame => FramePalette[0].color;
        public static Color DefaultNote => NotePalette[0].color;

        /// <summary>The same colour at a different weight — for a fill that has to sit under
        /// text, or a border that has to sit over one.</summary>
        public static Color Fade(Color color, float alpha) =>
            new Color(color.r, color.g, color.b, alpha);

        // ---- the other half, which lives in DaerD.uss -------------------------
        //
        // UIElements resolves these itself and C# never sees them. Listed so the vocabulary can
        // be found from one place:
        //
        //   .graph-view          --grid-background-color, --line-color, --thick-line-color
        //   .compact-node        the node body, border and port columns
        //   .state-node__progress   the play-mode bar (rgb(140, 230, 160) — the PlayingEdge family)
        //   .transition-edge__*  the count badge and condition label
        //   .dd-frame__title     the frame title bar
        //   .ce-panel__header    the panel headers
    }
}

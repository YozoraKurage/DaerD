using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// Every colour DD DynamicAnalyze draws, named for what it says. The product keeps its own
    /// palette in DaerDColors and this is deliberately a second one: the module is meant to be
    /// liftable into its own assembly, and a palette it did not own would be the one thing
    /// stopping that. The rule the two share is the one that matters — a colour is written down
    /// once, under a name, and nowhere else.
    ///
    /// The inks say what KIND of signal a row is, so a glance down the window sorts the rows
    /// before any of them is read. The wire's own rows are the odd ones out on purpose: they
    /// are events rather than values, and losing one is the thing the run is usually about.
    /// </summary>
    static class WaveformColors
    {
        static bool Dark => EditorGUIUtility.isProSkin;

        /// <summary>Behind the waveform — darker than the window so the plot reads as a
        /// surface rather than as part of the chrome.</summary>
        public static Color Backdrop =>
            Dark ? new Color(0.16f, 0.16f, 0.17f) : new Color(0.76f, 0.76f, 0.78f);

        /// <summary>The time ruler, a shade off the plot so the boundary needs no line.</summary>
        public static Color Ruler =>
            Dark ? new Color(0.20f, 0.20f, 0.21f) : new Color(0.70f, 0.70f, 0.72f);

        /// <summary>A scope's own line, dividing the list into the wearer's rows, the other
        /// person's, and the wire's.</summary>
        public static Color Header =>
            Dark ? new Color(1f, 1f, 1f, 0.07f) : new Color(0f, 0f, 0f, 0.08f);

        /// <summary>Every other row, so a wide window can still be read across.</summary>
        public static Color RowTint =>
            Dark ? new Color(1f, 1f, 1f, 0.025f) : new Color(0f, 0f, 0f, 0.03f);

        /// <summary>Tick marks and the edges between bands.</summary>
        public static Color Grid =>
            Dark ? new Color(1f, 1f, 1f, 0.08f) : new Color(0f, 0f, 0f, 0.10f);

        /// <summary>Where the reader is. The one colour nothing else uses.</summary>
        public static Color Cursor => new Color(1f, 0.62f, 0.16f);

        /// <summary>The second cursor — where the reader is measuring FROM. A colour of its own
        /// rather than a dimmer cursor, because the two are read as a pair and the question is
        /// always which end is which.</summary>
        public static Color Mark => new Color(0.36f, 0.86f, 0.78f);

        public static Color BoolInk => new Color(0.42f, 0.83f, 0.45f);
        public static Color FloatInk => new Color(0.38f, 0.72f, 0.95f);
        public static Color IntInk => new Color(0.95f, 0.82f, 0.35f);

        /// <summary>The run being compared against, drawn under the one in hand. One ink for
        /// every kind: the ghost is there to be told apart from the live line, and repeating
        /// the kind colours in a dimmer shade would say the opposite.</summary>
        public static Color GhostInk =>
            Dark ? new Color(1f, 1f, 1f, 0.34f) : new Color(0f, 0f, 0f, 0.30f);

        /// <summary>The numbers at a row's own top and bottom — an annotation over the plot, so
        /// dim enough that the line stays the thing being looked at.</summary>
        public static Color RangeLabel =>
            Dark ? new Color(0.72f, 0.74f, 0.78f, 0.75f) : new Color(0.16f, 0.17f, 0.20f, 0.80f);

        /// <summary>The block a state's name sits in — filled, not a line, because a state is
        /// a span rather than a moment. What an unnamed band gets; a named one gets a hue of
        /// its own from <see cref="BandFor"/>.</summary>
        public static Color StateBand => new Color(0.62f, 0.51f, 0.90f, 0.45f);

        /// <summary>
        /// A band's own colour, from the name written in it. Two spans of the same state are the
        /// same colour wherever they are in the run and whatever else the run contains, so a
        /// layer that keeps returning to one state reads as a repeat rather than as a stripe to
        /// be spelled out — and a layer that goes somewhere new says so before it is read.
        ///
        /// The hue is the name's hash stepped by the golden ratio, which is what keeps names
        /// that hash to neighbouring numbers from landing on neighbouring hues. Saturation stays
        /// low and the fill stays translucent on purpose: the band is the background the label
        /// sits on, and it has to hold light text on the Pro skin and dark text on the other.
        /// </summary>
        public static Color BandFor(string name)
        {
            if (string.IsNullOrEmpty(name)) return StateBand;
            // FNV-1a, spelled out rather than string.GetHashCode: that one is allowed to differ
            // between runtimes, and a band changing colour when Unity is upgraded would read as
            // the state having changed.
            uint hash = 2166136261u;
            foreach (char letter in name)
            {
                hash ^= letter;
                hash *= 16777619u;
            }
            float hue = Mathf.Repeat(hash % 1024u * 0.6180339887f, 1f);
            var band = Dark
                ? Color.HSVToRGB(hue, 0.40f, 0.88f)
                : Color.HSVToRGB(hue, 0.52f, 0.94f);
            band.a = StateBand.a;
            return band;
        }

        /// <summary>The wire's rows: a sample sent, and a sample lost.</summary>
        public static Color EventInk => new Color(0.92f, 0.40f, 0.42f);

        /// <summary>
        /// A setting that will not do what it says — a synced name this controller has not got,
        /// and a list that is not the store's. On the settings panel rather than on the plot,
        /// which is where a thing said BEFORE a run belongs; the palette is here all the same,
        /// because the module keeps its colours in one place and where they are painted is not
        /// what decides that.
        /// </summary>
        public static Color Wrong =>
            Dark ? new Color(0.98f, 0.68f, 0.28f) : new Color(0.66f, 0.36f, 0.02f);
    }
}

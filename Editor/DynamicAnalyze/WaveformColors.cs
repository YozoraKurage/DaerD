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

        /// <summary>Every other row, so a wide window can still be read across.</summary>
        public static Color RowTint =>
            Dark ? new Color(1f, 1f, 1f, 0.025f) : new Color(0f, 0f, 0f, 0.03f);

        /// <summary>Tick marks and the edges between bands.</summary>
        public static Color Grid =>
            Dark ? new Color(1f, 1f, 1f, 0.08f) : new Color(0f, 0f, 0f, 0.10f);

        /// <summary>Where the reader is. The one colour nothing else uses.</summary>
        public static Color Cursor => new Color(1f, 0.62f, 0.16f);

        public static Color BoolInk => new Color(0.42f, 0.83f, 0.45f);
        public static Color FloatInk => new Color(0.38f, 0.72f, 0.95f);
        public static Color IntInk => new Color(0.95f, 0.82f, 0.35f);

        /// <summary>The block a state's name sits in — filled, not a line, because a state is
        /// a span rather than a moment.</summary>
        public static Color StateBand => new Color(0.62f, 0.51f, 0.90f, 0.45f);

        /// <summary>The wire's rows: a sample sent, and a sample lost.</summary>
        public static Color EventInk => new Color(0.92f, 0.40f, 0.42f);
    }
}

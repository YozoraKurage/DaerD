using UnityEngine.Playables;

namespace Yozolab.DaerD
{
    /// <summary>
    /// How a playable's numbers are written down for a reader. Presentation only — the model
    /// keeps the raw doubles, because a test asserts on those and a formatted string is a
    /// worse thing to assert on.
    /// </summary>
    static class PlayableFacts
    {
        /// <summary>
        /// A time or a duration in seconds. A playable with no end answers
        /// <see cref="double.MaxValue"/> rather than an infinity (measured: both a clip playable
        /// and a controller playable do), which prints as 1.8E+308 and reads as a bug; anything
        /// that large is written as the endlessness it stands for.
        /// </summary>
        public static string Seconds(double value)
        {
            if (double.IsInfinity(value) || value >= 1e30) return "∞";
            return value.ToString("0.###");
        }

        /// <summary>The play state, in the user's language — three words that are the whole
        /// vocabulary of the enum.</summary>
        public static string State(PlayState state)
        {
            switch (state)
            {
                case PlayState.Playing: return L.Tr("Playing");
                case PlayState.Paused: return L.Tr("Paused");
                default: return L.Tr("Delayed");
            }
        }
    }
}

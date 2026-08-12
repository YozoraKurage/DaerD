using System.Collections.Generic;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// What the run is told to do, and when: a list of "at this second, set this parameter to
    /// this". Everything a person would do to an avatar while watching it — press a menu
    /// toggle, drag a radial to a value, let go — is one of these, and putting them in a list
    /// rather than in the viewer's hands is what makes a run repeatable.
    ///
    /// Times are simulated seconds, so a stimulus does not move when the frame rate or the
    /// jitter does. An entry lands on the first frame whose start has reached its time, which
    /// means an entry between two frames happens on the later one — never the earlier, and
    /// never twice.
    /// </summary>
    sealed class Stimulus
    {
        public sealed class Entry
        {
            /// <summary>Simulated seconds from the start of the run.</summary>
            public float atSeconds;
            /// <summary>Which client to poke. Empty means every client that has the
            /// parameter — the wearer's own input reaches nobody else's copy directly, but
            /// telling a run "the user pressed this" should not have to know how many clients
            /// there are.</summary>
            public string scope = string.Empty;
            public string parameter = string.Empty;
            public float value;
        }

        public readonly List<Entry> entries = new List<Entry>();

        public Stimulus At(float seconds, string parameter, float value, string scope = null)
        {
            entries.Add(new Entry
            {
                atSeconds = seconds,
                parameter = parameter,
                value = value,
                scope = scope ?? string.Empty,
            });
            return this;
        }

        public Stimulus At(float seconds, string parameter, bool value, string scope = null) =>
            At(seconds, parameter, value ? 1f : 0f, scope);

        /// <summary>The entries in the order a run consumes them. Stable within one time, so
        /// two writes to the same parameter at the same second land in the order they were
        /// written down rather than in whichever order the sort felt like.</summary>
        public List<Entry> InOrder()
        {
            var ordered = new List<Entry>(entries);
            // A stable insertion sort: List.Sort is introsort and would reorder equal times.
            for (int i = 1; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                int j = i - 1;
                while (j >= 0 && ordered[j].atSeconds > entry.atSeconds)
                {
                    ordered[j + 1] = ordered[j];
                    j--;
                }
                ordered[j + 1] = entry;
            }
            return ordered;
        }
    }
}

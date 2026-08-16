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
    ///
    /// <para>WHY THE LIST IS IN LAYERS.</para>
    /// One list is the right shape for a stimulus somebody typed and the wrong shape for one
    /// taken off a recording, which arrives as several unrelated things at once: what the
    /// wearer pressed, what their body and voice were doing, and what the world touched them
    /// with. Asking "what would this controller do without the gestures" then means deleting
    /// rows and typing them back, which is not an experiment anybody runs twice. A track is
    /// those things kept apart and switched off whole — the same reason a DAW has them.
    ///
    /// <see cref="Track.muted"/> is part of the experiment and travels with it: "this run is
    /// the recording without its gesture track" is a question, and a saved answer that could
    /// not say which tracks were in it would be a result nobody can re-ask. Solo is not here,
    /// because solo is "mute all the others while I look at this one" — a state of the window
    /// somebody is standing in front of, and one that says nothing about the run once they
    /// walk away.
    ///
    /// <para>WHAT THE ENGINE SEES.</para>
    /// A flat list, from <see cref="InOrder"/>, exactly as before there were tracks. The engine
    /// has no idea any of this exists and is not meant to acquire one: a track is how an
    /// experiment is EDITED, and giving the run's own loop a second concept to be right about
    /// would buy nothing a merge does not already.
    ///
    /// A LAYER'S WEIGHT is deliberately not one of these, though a live session can turn one
    /// (see SimSession.Write). An entry is "at this second, set this parameter to this", and a
    /// weight is not a parameter: giving the entry a second kind of target changes what an
    /// entry is, and an entry is the unit a whole experiment gets written down in — everything
    /// that reads or saves a stimulus would have to learn the new shape before anybody could
    /// use the first timed weight. What that leaves out, said plainly: a weight cannot be part
    /// of a repeatable run, only of a session somebody is standing in. It is left out because a
    /// timed weight is a Layer Control by another name — a schedule of weights — and modelling
    /// that behaviour is the thing this module decided not to do. Turning the knob by hand and
    /// watching is the question it was wanted for: what does this look like at half weight.
    /// </summary>
    sealed class Stimulus
    {
        /// <summary>
        /// The names the tracks a person did not name themselves are called.
        ///
        /// Untranslated on purpose, and the same decision <c>PlayTools.Name</c> made for the
        /// same reason: these are written into a saved run and matched on when a track is taken
        /// from the recording again, so a name that changed with the editor's language would
        /// make a run written in one language unreadable as an experiment in another. What a
        /// person renames a track to is theirs, in whatever language they typed it.
        /// </summary>
        public const string HandTrack = "Hand";

        /// <summary>What the wearer pressed: the synced expression parameters.</summary>
        public const string MenuTrack = "Menu";

        /// <summary>What VRChat was feeding the avatar — gestures, locomotion, the voice.</summary>
        public const string BuiltInTrack = "Built-ins";

        /// <summary>What touched the avatar from outside: contacts, physbones, anything a
        /// component wrote. See <see cref="RunWarnings"/> for what this one cannot promise.</summary>
        public const string WorldTrack = "World";

        /// <summary>What a run saved before there were tracks reads back as. One track holding
        /// everything, under the name the panel used to have at the top of it.</summary>
        public const string OneTrack = "Timed inputs";

        public sealed class Entry
        {
            /// <summary>Simulated seconds from the start of the run.</summary>
            public float atSeconds;
            /// <summary>Which client to poke, or empty for the wearer — who is the one
            /// pressing things. A remote learns what the wearer did through the wire or not at
            /// all, and that is the question most runs are asking.</summary>
            public string scope = string.Empty;
            public string parameter = string.Empty;
            public float value;
        }

        /// <summary>
        /// One layer of the stimulus: a name somebody can read, whether it is switched off, and
        /// the entries in it.
        ///
        /// The name is the identity. A track taken from a recording is found again by it — that
        /// is what makes "take the Menu track down from the recording again and leave the rest
        /// of my edits alone" a thing the panel can offer — which also means a renamed track is
        /// one this module no longer claims to know the provenance of. Said rather than
        /// prevented: a name a person can edit and a name a machine matches on are the same
        /// field here, and the alternative was a hidden second identity that a rename would
        /// silently disagree with.
        /// </summary>
        public sealed class Track
        {
            public string name = string.Empty;
            /// <summary>Switched off, and part of the experiment — see the class doc.</summary>
            public bool muted;
            public readonly List<Entry> entries = new List<Entry>();

            public Track() { }

            public Track(string name) { this.name = name ?? string.Empty; }
        }

        /// <summary>In the order they were made, which is the order they merge in — see
        /// <see cref="InOrder"/>.</summary>
        public readonly List<Track> tracks = new List<Track>();

        /// <summary>The track by this name, made at the end of the list if there is none.</summary>
        public Track Named(string name)
        {
            var found = Find(name);
            if (found != null) return found;
            found = new Track(name);
            tracks.Add(found);
            return found;
        }

        /// <summary>The track by this name, or null. Different from <see cref="Named"/> on
        /// purpose: asking whether a run HAS a world track must not be what creates one.</summary>
        public Track Find(string name)
        {
            foreach (var track in tracks)
                if (track.name == name) return track;
            return null;
        }

        /// <summary>Everything written down, muted tracks included — what a panel counts, which
        /// is not what a run consumes.</summary>
        public int Count
        {
            get
            {
                int count = 0;
                foreach (var track in tracks) count += track.entries.Count;
                return count;
            }
        }

        /// <summary>
        /// Every entry a run would actually see, in no particular order. For the questions that
        /// are about the SET of inputs — does anything press a name nothing carries — where
        /// paying for the sort of <see cref="InOrder"/> would buy an ordering nobody reads.
        /// </summary>
        public IEnumerable<Entry> Active
        {
            get
            {
                foreach (var track in tracks)
                {
                    if (track.muted) continue;
                    foreach (var entry in track.entries) yield return entry;
                }
            }
        }

        public Stimulus At(float seconds, string parameter, float value, string scope = null) =>
            At(HandTrack, seconds, parameter, value, scope);

        public Stimulus At(float seconds, string parameter, bool value, string scope = null) =>
            At(HandTrack, seconds, parameter, value ? 1f : 0f, scope);

        /// <summary>An input on a named track, which is made if it does not exist yet. The
        /// overloads without one write to <see cref="HandTrack"/>: an input nobody said the
        /// provenance of is one somebody wrote by hand, and that is where the panel's Add and a
        /// live session's pokes both land.</summary>
        public Stimulus At(string track, float seconds, string parameter, float value,
            string scope = null)
        {
            Named(track).entries.Add(new Entry
            {
                atSeconds = seconds,
                parameter = parameter,
                value = value,
                scope = scope ?? string.Empty,
            });
            return this;
        }

        /// <summary>
        /// The entries in the order a run consumes them: every track that is not muted, merged
        /// by time.
        ///
        /// Stable, and stable across the merge as well as within a track — two writes to one
        /// parameter at one second land in track order first and in the order they were written
        /// down second. Which means the LAST track wins a tie, and that is the useful way round:
        /// a hand-written track sits under the ones taken from a recording, so an input typed to
        /// override what the recording did overrides it.
        /// </summary>
        public List<Entry> InOrder()
        {
            var merged = new List<Entry>();
            foreach (var track in tracks)
                if (!track.muted) merged.AddRange(track.entries);

            // Sorted by (time, position) rather than by time alone: List.Sort is introsort and
            // would reorder equal times, and the ordering above is the whole of what the merge
            // promises. Position is carried in a separate array so the comparison can see it —
            // an insertion sort kept the same promise and cost the square of the entry count,
            // which a track taken off a recording of any length is large enough to notice.
            var order = new int[merged.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            System.Array.Sort(order, (a, b) =>
            {
                int by = merged[a].atSeconds.CompareTo(merged[b].atSeconds);
                return by != 0 ? by : a.CompareTo(b);
            });

            var ordered = new List<Entry>(merged.Count);
            for (int i = 0; i < order.Length; i++) ordered.Add(merged[order[i]]);
            return ordered;
        }
    }
}

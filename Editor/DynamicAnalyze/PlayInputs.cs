using System.Collections.Generic;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// The edited inputs, played into an avatar somebody is really wearing.
    ///
    /// <para>THE OUTER LOOP.</para>
    /// The simulator answers an edited question quickly and is wrong about one thing on purpose:
    /// it does not model the world. A contact that fired because a hand was somewhere is, in a
    /// run, the value it had that day and nothing else — see <see cref="InputSurface"/> and the
    /// warning <see cref="RunWarnings"/> puts on such a run. There is exactly one way to find out
    /// what the world would really have done with the edited inputs, and it is to press them into
    /// an avatar in Play mode and record what comes back. So the loop somebody runs is two loops:
    /// a fast inner one (edit, simulate, compare against the recording) and a slow outer one
    /// (send, record, compare against the simulation). This is the send.
    ///
    /// <para>WHY THIS IS A CLOCK AND NOT A SENDER.</para>
    /// Nothing here writes anything. It holds the entries in the order they go and hands back the
    /// ones whose second has arrived, and the window does the writing through
    /// <see cref="PlayTools.Write"/>. Which means the part that can be wrong about time — an
    /// entry sent twice, an entry skipped because two updates landed in one frame, a run that
    /// never says it is finished — is a pure object a test can drive at whatever rate it likes,
    /// and the part that needs a tool installed is one call.
    ///
    /// <para>WHAT IS NOT SENT, AND WHY.</para>
    /// The same exclusions the extraction makes, for the same reason and once more at the door:
    /// a value a driver or an animation writes is computed by the avatar itself, and pressing it
    /// from outside fights the thing that computes it every frame rather than reproducing the
    /// run. An extraction has already left those out, so this catches the ones somebody typed.
    ///
    /// An input aimed at somebody else's copy is not sent either. There is one avatar in the
    /// editor and it is the wearer's; a remote's values are what the wire does to them, and there
    /// is no wire in Play mode to do it.
    ///
    /// The world track is not sent by default, which is the one exclusion that is a choice rather
    /// than an arithmetic fact. Its values came from real components, and in Play mode those
    /// components are running: a physbone writing its own parameter and a playback writing the
    /// same one is two authors on one value, and which of them wins is a race. Sending it is
    /// offered — a scene with no contacts in it, or a deliberate look at what the recorded world
    /// does to an edited avatar, are both real — and it is off until somebody asks.
    /// </summary>
    sealed class PlayInputs
    {
        readonly List<Stimulus.Entry> _due;
        int _at;

        /// <summary>Names that were written down and are not going out, because the avatar works
        /// them out for itself.</summary>
        public readonly List<string> derived = new List<string>();

        /// <param name="stimulus">The inputs as the panel holds them, tracks and all. Muted
        /// tracks are already out of the run and stay out of this.</param>
        /// <param name="world">Whether to send the world track as well — see the class doc.</param>
        /// <param name="derived">Every name the avatar computes for itself, or null to send
        /// whatever is written down. <see cref="InputSurface.Derived"/> is where this comes
        /// from.</param>
        public PlayInputs(Stimulus stimulus, bool world, ICollection<string> derived)
        {
            var wanted = new Stimulus();
            foreach (var track in stimulus != null ? stimulus.tracks : new List<Stimulus.Track>())
            {
                if (track.muted) continue;
                if (!world && track.name == Stimulus.WorldTrack) continue;
                var into = wanted.Named(track.name);
                foreach (var entry in track.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.parameter)) continue;
                    if (!string.IsNullOrEmpty(entry.scope)
                        && entry.scope != Simulation.LocalScope) continue;
                    if (derived != null && derived.Contains(entry.parameter))
                    {
                        if (!this.derived.Contains(entry.parameter))
                            this.derived.Add(entry.parameter);
                        continue;
                    }
                    into.entries.Add(entry);
                }
            }
            // The same merge a run would do, so what goes out is what the simulator was told —
            // spelling the ordering again here is how the two would come to disagree.
            _due = wanted.InOrder();
        }

        /// <summary>How many are going out in all.</summary>
        public int Count => _due.Count;

        /// <summary>How many have gone.</summary>
        public int Sent => _at;

        /// <summary>The second the last of them is due. Zero for a playback with nothing in
        /// it, which is also one that is finished before it starts.</summary>
        public float Length => _due.Count > 0 ? _due[_due.Count - 1].atSeconds : 0f;

        public bool Done => _at >= _due.Count;

        /// <summary>
        /// Everything due at or before this second and not yet handed over.
        ///
        /// Told the time rather than how much has passed, so a caller that misses an update —
        /// the editor's update is not a frame and does not promise to be regular — sends what it
        /// missed rather than losing it. Time going backwards sends nothing rather than
        /// rewinding: there is no rewinding an avatar, and a playback that quietly started again
        /// would press things nobody asked it to.
        /// </summary>
        public List<Stimulus.Entry> Due(float seconds)
        {
            var now = new List<Stimulus.Entry>();
            while (_at < _due.Count && _due[_at].atSeconds <= seconds)
            {
                now.Add(_due[_at]);
                _at++;
            }
            return now;
        }
    }
}

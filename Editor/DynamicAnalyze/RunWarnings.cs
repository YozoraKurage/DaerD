using System.Collections.Generic;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// What is already wrong with the experiment, said before it is run.
    ///
    /// The third of a set, and the three are told apart by when they can speak and about what.
    /// <see cref="SimNotes"/> reads the controller and says what a run of it cannot promise.
    /// <see cref="RunFindings"/> reads a finished trace and says what the result turned out to
    /// contain. This one reads neither: it reads the SETTINGS — the wire, the list of what
    /// travels, the timed inputs — and says which of them are set up to answer nothing.
    ///
    /// Kept out of SimNotes although both speak first. A note there is a standing limit of the
    /// simulator, true of every run of that controller and worth reading once; these are
    /// mistakes in what is about to be asked, each one fixed by editing a field on the panel it
    /// appears on, and each one gone the moment it is. Mixing them would make the notes a list
    /// that is sometimes actionable and sometimes not, which is how a list stops being read.
    ///
    /// Pure, and told lists of names rather than a controller or a parameter store: every one
    /// of these questions is about names either way, and this module is meant to stay liftable
    /// into an assembly of its own.
    /// </summary>
    static class RunWarnings
    {
        /// <summary>
        /// Everything worth saying about these settings, in the order they are worth saying it.
        /// </summary>
        /// <param name="wire">Whether anybody else is in the run. Nothing here is worth saying
        /// without them: each of these is about a value reaching — or failing to reach — a
        /// person who is not there in a single-client run, and a warning nobody can act on is
        /// how a panel full of them stops being read.</param>
        /// <param name="synced">What the window is about to put on the wire.</param>
        /// <param name="declared">The controller's own parameters.</param>
        /// <param name="stimulus">The timed inputs, or null for a run with none.</param>
        /// <param name="edited">Whether the inputs have been changed since they were taken off
        /// a recording. The caller is the only one who can know — a stimulus says what it is,
        /// not what it used to be — and it is what tells the one warning below that is about a
        /// limit of the simulator rather than about a mistake.</param>
        public static List<string> For(bool wire, IList<string> synced, IList<string> declared,
            Stimulus stimulus, bool edited)
        {
            var warnings = new List<string>();
            // First, and outside the wire's gate: every other warning here is about a value
            // reaching somebody else, and this one is about what the run itself will not
            // recompute — which is as true of a single client as of two.
            FrozenWorld(stimulus, edited, warnings);
            if (!wire) return warnings;
            NothingTravels(synced, warnings);
            NamesWithNothingBehindThem(synced, declared, warnings);
            InputsThatWillNotLeave(synced, declared, stimulus, warnings);
            return warnings;
        }

        /// <summary>
        /// A world track played back beside inputs that are no longer the ones it was recorded
        /// with.
        ///
        /// The world track is what touched the avatar from outside — a contact, a physbone,
        /// somebody else's hand. It is not a model of any of those: it is the values they
        /// produced that day, frozen. Change the menu track and, in a headset, the contact would
        /// have fired somewhere else or not at all; here it fires exactly where it did, and the
        /// run looks like a clean answer to a question that was half asked.
        ///
        /// Only when something HAS been changed, and only while the track is switched on. Said
        /// on every run that has a world track in it, the sentence would be a standing note
        /// about the simulator — which is <see cref="SimNotes"/>'s job — and would be read past
        /// by the time it mattered. This one appears the moment the run stops being a replay
        /// and goes away again when the track is muted, which is one of the two ways out of it;
        /// the other is the outer loop, where the edited inputs are played into an avatar
        /// somebody is really wearing and the world answers for itself.
        /// </summary>
        static void FrozenWorld(Stimulus stimulus, bool edited, List<string> warnings)
        {
            if (stimulus == null || !edited) return;
            var world = stimulus.Find(Stimulus.WorldTrack);
            if (world == null || world.muted || world.entries.Count == 0) return;
            warnings.Add(L.Tr(
                "The {0} track is frozen: it replays what touched the avatar that day, and the rest of the inputs are no longer what it was recorded beside. Nothing here works out where a contact or a physbone would have fired instead. Mute it to ask the question without the world in it, or play the inputs into a real avatar and record the answer.",
                Stimulus.WorldTrack));
        }

        /// <summary>
        /// A wire that carries nothing. The two copies then start from the same defaults and
        /// part company at the first input, and every difference the run shows is about an
        /// empty list rather than about the controller — which is the one failure here that
        /// makes every other answer in the window worthless rather than merely incomplete.
        /// </summary>
        static void NothingTravels(IList<string> synced, List<string> warnings)
        {
            if (synced != null && synced.Count > 0) return;
            warnings.Add(L.Tr(
                "Nothing is on the wire, so the other person learns nothing at all. Their copy runs on its own defaults for the whole run, and every difference between the two is that rather than anything this controller does."));
        }

        /// <summary>
        /// Synced names the controller has never heard of. They are carried and land nowhere:
        /// no client has a parameter to write, so the row that would show the value arriving
        /// does not exist either, and the run looks exactly like one where the value was
        /// dropped.
        ///
        /// A built-in is not one of these however the controller is written. VRChat feeds those
        /// by its own arrangement — <see cref="Simulation.Carry"/> skips them for that reason —
        /// so a store that names one is describing the platform rather than making a mistake.
        /// </summary>
        static void NamesWithNothingBehindThem(IList<string> synced, IList<string> declared,
            List<string> warnings)
        {
            var stale = new List<string>();
            for (int i = 0; synced != null && i < synced.Count; i++)
                if (Missing(synced[i], declared) && !stale.Contains(synced[i]))
                    stale.Add(synced[i]);
            if (stale.Count == 0) return;
            warnings.Add(L.Tr(
                "{0} synced name(s) are not parameters of this controller ({1}). Nothing carries them because there is nothing to carry — the usual reason is a parameter store that has moved on since the controller did.",
                stale.Count, Join(stale)));
        }

        /// <summary>
        /// Whether a name on the wire has nothing behind it. The window marks its list with
        /// this and the warning above counts the same answer, so the row a reader is looking at
        /// and the sentence under it can never disagree.
        /// </summary>
        public static bool Missing(string name, IList<string> declared)
        {
            if (string.IsNullOrEmpty(name) || VrcParameters.IsBuiltIn(name)) return false;
            return declared == null || !declared.Contains(name);
        }

        /// <summary>
        /// Inputs written down against the wearer for a parameter nothing carries. The wearer's
        /// copy does everything asked of it and nobody else ever sees any of it, which is the
        /// commonest way a run looks right and an avatar does not.
        ///
        /// The same question <see cref="RunFindings"/> asks fifth, asked at the other end of the
        /// run and off other evidence: that one reads the finished trace and reports what DID
        /// go nowhere, and this one reads the list of inputs while it can still be edited. Both
        /// are wanted. A warning here is cheap and can be wrong about a run that was never
        /// started; the finding is about a run that happened, and it is the one a saved result
        /// carries. Neither is the other's duplicate — one is advice and one is a record.
        ///
        /// Built-ins are not a mistake here however they are pressed, and a name the controller
        /// does not declare is left to the warning above rather than counted twice.
        /// </summary>
        static void InputsThatWillNotLeave(IList<string> synced, IList<string> declared,
            Stimulus stimulus, List<string> warnings)
        {
            if (stimulus == null) return;
            var stranded = new List<string>();
            // The active tracks only: a muted one is an input the experiment left out,
            // and a warning about something that is not going to happen is noise.
            foreach (var entry in stimulus.Active)
            {
                if (entry == null || string.IsNullOrEmpty(entry.parameter)) continue;
                // Empty is the wearer, who is the one pressing things. An input aimed at
                // somebody else's copy is asking what THAT copy does with it, and whether the
                // wire would have carried it is not the question being asked.
                if (!string.IsNullOrEmpty(entry.scope) && entry.scope != Simulation.LocalScope)
                    continue;
                string name = entry.parameter;
                if (stranded.Contains(name) || VrcParameters.IsBuiltIn(name)) continue;
                if (declared == null || !declared.Contains(name)) continue;
                if (synced != null && synced.Contains(name)) continue;
                stranded.Add(name);
            }
            if (stranded.Count == 0) return;
            warnings.Add(L.Tr(
                "{0} timed input(s) press something on the wearer that is not on the wire ({1}). Whatever they do, they do to one person — worth knowing now, while the list is still being written.",
                stranded.Count, Join(stranded)));
        }

        /// <summary>
        /// Whether the list on the wire is not what the avatar's own parameter store says
        /// travels. Order is not part of the answer — a sample carries a set — so this is
        /// membership and nothing else.
        ///
        /// Said beside the button that would replace one with the other rather than as a
        /// warning of its own, because it is not necessarily a mistake: a run that deliberately
        /// takes one parameter off the wire to see what breaks is a run whose list SHOULD
        /// differ from the store, and a HelpBox calling that an error would be nagging about
        /// the experiment being performed.
        /// </summary>
        public static bool DiffersFromStore(IList<string> synced, IList<string> stored)
        {
            if (stored == null) return false;
            int mine = synced != null ? synced.Count : 0;
            if (mine != stored.Count) return true;
            for (int i = 0; i < mine; i++)
                if (!stored.Contains(synced[i])) return true;
            return false;
        }

        /// <summary>Names, with a tail rather than a wall of them. A copy of the one in
        /// <see cref="RunFindings"/> for the same reason that one is a copy of SimNotes': the
        /// three lists want the same shape and none of them wants another's internals public
        /// for the sake of five lines.</summary>
        static string Join(List<string> names)
        {
            const int shown = 3;
            if (names.Count <= shown) return string.Join(", ", names.ToArray());
            var head = names.GetRange(0, shown);
            return string.Join(", ", head.ToArray())
                + L.Tr(" and {0} more", names.Count - shown);
        }
    }
}

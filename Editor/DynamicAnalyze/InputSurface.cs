using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// A recording's INPUT FACE: the part of what happened that was done TO the avatar, pulled
    /// out of the part that was the avatar answering.
    ///
    /// <para>WHY THIS EXISTS AT ALL.</para>
    /// A trace is a record and a record is not editable — the numbers in it are what happened,
    /// and changing one produces a file that says something untrue. What IS editable is the
    /// question the recording was an answer to, and a deterministic engine will answer an edited
    /// question. So the loop is: record, take the input face out, edit it, run it through the
    /// simulator, and lay the result against the recording. Everything after the first step is
    /// machinery this module already had; this is the step that was missing, and without it a
    /// recording could only ever be looked at.
    ///
    /// <para>WHAT IS LEFT OUT, AND WHY LEAVING IT IN WOULD BE WORSE THAN LOSING IT.</para>
    /// Two kinds of value in a recording moved because the avatar's own logic moved them: what
    /// a VRC Parameter Driver wrote, and what an animation wrote onto the Animator (the AAP
    /// idiom). The simulator computes both again from the same controller. Replaying them as
    /// inputs would apply each of them twice — once as a poke and once as the run's own work —
    /// and the second application is the one that would look like a bug in the controller. They
    /// are reported rather than dropped in silence, because "my parameter is not in the list"
    /// with no explanation is the same experience as a tool that lost it.
    ///
    /// <para>WHY THREE TRACKS AND NOT ONE.</para>
    /// The three come from different places and are edited for different reasons. The menu
    /// track is what a person pressed and is the one most experiments change. The built-ins are
    /// what VRChat was feeding the avatar out of a body and a voice — worth switching off whole
    /// to ask what the controller does on its own, and hardly ever worth editing one row of.
    /// The world track is what somebody else's hand and the avatar's own physics did, and it is
    /// the one with a limit worth stating: it is FROZEN. Edit the menu track and, in a headset,
    /// a contact would have fired somewhere else; the simulator does not model the world and
    /// will replay the contacts exactly as they were. <see cref="RunWarnings"/> says so when a
    /// run is set up that way, which is the only honest thing to do about a limit that cannot
    /// be fixed by being quiet about it.
    ///
    /// Not folded into <see cref="TraceClip"/>, which reads and writes clips and knows nothing
    /// about controllers: telling the three apart means walking a controller's behaviours and
    /// its clips, and a file format that reached for the avatar's logic would be a much larger
    /// thing than it is.
    /// </summary>
    static class InputSurface
    {
        /// <summary>
        /// What came out of a recording: the tracks, and what was deliberately not put in one.
        /// </summary>
        public sealed class Extraction
        {
            public readonly Stimulus stimulus = new Stimulus();

            /// <summary>Names a driver writes. The run computes them again.</summary>
            public readonly List<string> driven = new List<string>();

            /// <summary>Names an animation writes onto the Animator — every AAP. Same
            /// reason.</summary>
            public readonly List<string> animated = new List<string>();

            /// <summary>How many names were left out of every track.</summary>
            public int Left => driven.Count + animated.Count;
        }

        /// <summary>
        /// The clip's input face, split three ways.
        /// </summary>
        /// <param name="clip">The recording. Anything <see cref="TraceClip.Load"/> can read.</param>
        /// <param name="scope">Which client the inputs are aimed at — empty for the wearer,
        /// who is the one pressing things.</param>
        /// <param name="controller">What the run will be of, asked only about what it writes
        /// for itself. Null leaves nothing out, which is the honest answer when there is no
        /// controller to ask rather than a claim that nothing is derived.</param>
        /// <param name="parameters">The names a run can be told at all, or null for every row
        /// in the recording. A layer's state and weight rows are never inputs and are left out
        /// either way.</param>
        /// <param name="synced">What travels — the names that make the menu track. The caller
        /// decides where this comes from; a recording of an assembled avatar speaks the names
        /// its BUILD produced, so the build's own list is the right one to hand over when there
        /// is one.</param>
        public static Extraction Of(AnimationClip clip, string scope,
            AnimatorController controller, ICollection<string> parameters,
            ICollection<string> synced)
        {
            var extraction = new Extraction();
            var trace = TraceClip.Load(clip);
            var driven = DriverWrites(controller);
            var animated = controller != null
                ? AapWriteScan.CollectWrittenParameters(controller) : new HashSet<string>();

            // The three, made in this order whether or not they turn out to hold anything, so
            // the merge order is the same for every recording — and dropped at the end if they
            // do not, because a track with nothing in it is not material to edit.
            var menu = extraction.stimulus.Named(Stimulus.MenuTrack);
            var builtIn = extraction.stimulus.Named(Stimulus.BuiltInTrack);
            var world = extraction.stimulus.Named(Stimulus.WorldTrack);

            foreach (var signal in trace.Signals)
            {
                if (!TraceClip.CanDrive(signal, parameters)) continue;
                // Asked before anything else. A driver's output can perfectly well be a synced
                // expression parameter, and reading it as one would put the very values the run
                // is about to compute back into its own input.
                if (driven.Contains(signal.name))
                {
                    Remember(extraction.driven, signal.name);
                    continue;
                }
                if (animated.Contains(signal.name))
                {
                    Remember(extraction.animated, signal.name);
                    continue;
                }
                TraceClip.Changes(trace, signal, scope,
                    TrackFor(signal.name, synced, menu, builtIn, world));
            }

            for (int i = extraction.stimulus.tracks.Count - 1; i >= 0; i--)
                if (extraction.stimulus.tracks[i].entries.Count == 0)
                    extraction.stimulus.tracks.RemoveAt(i);
            return extraction;
        }

        /// <summary>
        /// Which of the three a name belongs to.
        ///
        /// What travels first, because that is the one a person recognises: an expression
        /// parameter is a thing they made and pressed, and it stays in the menu track even when
        /// it shares a name with something VRChat also knows about. Then the built-ins VRChat
        /// itself feeds — the ones carried with the pose, the expression sample or the voice —
        /// which is deliberately not every built-in: <c>IsLocal</c> and <c>IsOnFriendsList</c>
        /// are set per viewer and <c>PreviewMode</c> never leaves the client, so none of the
        /// three is something a person's body was doing. Those fall to the world track with
        /// everything else the avatar was told from outside.
        /// </summary>
        static Stimulus.Track TrackFor(string name, ICollection<string> synced,
            Stimulus.Track menu, Stimulus.Track builtIn, Stimulus.Track world)
        {
            if (synced != null && synced.Contains(name)) return menu;
            if (VrcParameters.TryFind(name, out var definition)
                && (definition.sync == VrcParameters.Sync.Ik
                    || definition.sync == VrcParameters.Sync.Playable
                    || definition.sync == VrcParameters.Sync.Speech))
                return builtIn;
            return world;
        }

        /// <summary>
        /// Every name the avatar works out for itself: what a driver writes and what an
        /// animation writes onto the Animator, in one set.
        ///
        /// The two are reported apart by an extraction, which has to say WHY each name is
        /// missing. Everything else — playing the inputs back into a real avatar, most of all —
        /// only wants to know that the avatar is the author of it and must not be argued with.
        /// </summary>
        public static HashSet<string> Derived(AnimatorController controller)
        {
            var names = DriverWrites(controller);
            if (controller != null)
                names.UnionWith(AapWriteScan.CollectWrittenParameters(controller));
            return names;
        }

        /// <summary>
        /// Every parameter some VRC Parameter Driver in this controller writes — the Set, the
        /// Add, the Random and the destination of a Copy. A Copy's SOURCE is not one of these:
        /// it is read, and a value that is only read is an input like any other.
        ///
        /// Read through <c>VrcParameterDriver.ReadSpec</c> rather than by casting to the SDK's
        /// type, which is the same bargain the rest of DaerD makes with the SDK — the package
        /// may not be installed, and a spec is what this needs anyway.
        /// </summary>
        public static HashSet<string> DriverWrites(AnimatorController controller)
        {
            var written = new HashSet<string>();
            if (controller == null) return written;
            foreach (var behaviour in controller.AllBehaviours())
            {
                if (!VrcParameterDriver.Is(behaviour)) continue;
                foreach (var entry in VrcParameterDriver.ReadSpec(behaviour).entries)
                    if (!string.IsNullOrEmpty(entry.name)) written.Add(entry.name);
            }
            return written;
        }

        static void Remember(List<string> names, string name)
        {
            if (!names.Contains(name)) names.Add(name);
        }
    }
}

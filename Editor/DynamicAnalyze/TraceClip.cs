using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// What a column of numbers cannot say about itself: whose it is, what its values mean, and
    /// what was asked to produce them.
    ///
    /// One shape written by two containers — a sub-asset beside a clip, a JSON block inside a
    /// <see cref="TraceFile"/> — so that there is one answer to "what does a saved run say" and
    /// one set of compatibility rules for it. A ScriptableObject because the older of the two
    /// containers needs it to be one; nothing about the fields depends on that.
    /// </summary>
    sealed class TraceManifest : ScriptableObject
    {
        [System.Serializable]
        public sealed class Entry
        {
            public string scope;
            public string name;
            public int kind;
            /// <summary>A state signal's names, in index order. Empty for every other kind.</summary>
            public List<string> labels = new List<string>();
        }

        /// <summary>One timed input, flattened. <see cref="Stimulus.Entry"/> is the same four
        /// numbers, and is deliberately not this type: the engine's shape is free to change
        /// with the engine, and a file's shape is not.</summary>
        [System.Serializable]
        public sealed class Poke
        {
            public float at;
            public string scope = string.Empty;
            public string parameter = string.Empty;
            public float value;
        }

        /// <summary>One layer of the timed inputs, flattened the same way and for the same
        /// reason. Whether a track was muted is written down because it is part of the
        /// experiment — "this run is the recording without its gestures" is a question, and a
        /// file that could not say which tracks were in it would be an answer nobody can
        /// re-ask.</summary>
        [System.Serializable]
        public sealed class Track
        {
            public string name = string.Empty;
            public bool muted;
            public List<Poke> entries = new List<Poke>();
        }

        /// <summary>
        /// The experiment that produced the run, beside the run.
        ///
        /// A deterministic clock was built so that two runs of the same settings could be laid
        /// side by side — and the settings lived only in the window's fields, so a saved run
        /// was a result whose question had been thrown away. A file that cannot say what was
        /// asked cannot be re-asked, compared against a changed setting, or handed to anyone.
        ///
        /// Flat and by value rather than a serialized <see cref="SimSettings"/>: this is a file
        /// format, and the engine's own shape is free to change with the engine while a file's
        /// is not. The window's shape is derived on the way back in (see
        /// <see cref="Restored"/>) rather than stored, so a form that grows a checkbox does not
        /// grow the format.
        /// </summary>
        [System.Serializable]
        public sealed class Settings
        {
            /// <summary>What this build writes. Bumped when a field's MEANING changes, which
            /// is the only change a reader cannot survive: adding one is already survivable,
            /// because a file written before it deserializes with that field's default.
            ///
            /// Two is the timed inputs in tracks. <see cref="stimulus"/> did not change shape
            /// and did not need to; what changed is that it is no longer where they are, and a
            /// reader that went on believing it would read a v2 run as one with no inputs at
            /// all. So the flat list stays, is written by nothing, and means "the inputs of a
            /// run saved before there were tracks".</summary>
            public const int Current = 2;

            /// <summary>Zero is a run saved before settings travelled at all — and, because
            /// Unity gives a missing block its defaults rather than a null, it is also how such
            /// a run says so. A file with version 0 has no settings, not settings of zero.</summary>
            public int version;

            public float fps = 60f;
            public float seconds = 10f;
            public float jitter;
            public int seed = 1;
            public bool lagRows = true;

            /// <summary>Whether there was a wire at all. The fields below mean nothing without
            /// it, and a single-client run is a real answer rather than a missing one.</summary>
            public bool wire;
            public float interval = 0.2f;
            public float latency;
            public float dropChance;
            public bool quantize = true;
            /// <summary>The wire's own seed as the run used it — which is the clock's seed
            /// unless the run gave the wire one of its own. Stored as the number that was
            /// actually rolled from rather than as the window's tick box: the number is what
            /// reproduces the run, and the box is worked out from it again on the way back.</summary>
            public int wireSeed = 1;
            public float remoteJoinsAt;
            public List<float> laterJoins = new List<float>();
            public List<string> parameters = new List<string>();

            /// <summary>The timed inputs of a version 1 run, in one flat list. Read, never
            /// written — see <see cref="Current"/>.</summary>
            public List<Poke> stimulus = new List<Poke>();

            /// <summary>The timed inputs, in the tracks they were edited in.</summary>
            public List<Track> tracks = new List<Track>();
        }

        public List<Entry> signals = new List<Entry>();

        /// <summary>What the run was made of, or a block at version 0 — see
        /// <see cref="Settings.version"/>.</summary>
        public Settings settings = new Settings();

        // ---- the shape, and the engine on either side of it --------------------

        // Both file formats write these same fields — the .anim's sub-asset and the .ddrun's
        // JSON block are two containers for one shape — so the translation between them and the
        // engine's own types lives here, once, rather than in whichever container was written
        // first. A second copy of it inside the new format would be the thing that quietly
        // disagrees about what a version 1 settings block means.

        /// <summary>What the manifest says about one signal: everything a column of numbers
        /// cannot say for itself.</summary>
        public static Entry EntryFor(SignalTrace.Signal signal)
        {
            var entry = new Entry
            {
                scope = signal.scope,
                name = signal.name,
                kind = (int)signal.kind,
            };
            if (signal.labels != null) entry.labels.AddRange(signal.labels);
            return entry;
        }

        /// <summary>The settings as a file keeps them.</summary>
        public static Settings Wrote(SimSettings settings)
        {
            var clock = settings.clock ?? new SimClock();
            var wire = settings.wire;
            var saved = new Settings
            {
                version = Settings.Current,
                fps = clock.fps,
                seconds = clock.seconds,
                jitter = clock.jitter,
                seed = clock.seed,
                lagRows = settings.lagRows,
                wire = wire != null,
            };
            if (wire != null)
            {
                saved.interval = wire.intervalSeconds;
                saved.latency = wire.latencySeconds;
                saved.dropChance = wire.dropChance;
                saved.quantize = wire.quantize;
                saved.wireSeed = wire.seed;
                saved.remoteJoinsAt = wire.remoteJoinsAt;
                saved.laterJoins.AddRange(wire.laterJoins);
                saved.parameters.AddRange(wire.parameters);
            }
            // Every track, muted ones included: a muted track is an input the experiment
            // deliberately does not use, and a file that dropped it would be one where taking a
            // question back is retyping it. Only the ACTIVE ones are what the run consumed, and
            // the manifest is the experiment rather than the run's own transcript.
            if (settings.stimulus != null)
                foreach (var track in settings.stimulus.tracks)
                {
                    var written = new Track
                    {
                        name = track.name,
                        muted = track.muted,
                    };
                    foreach (var entry in track.entries)
                        written.entries.Add(new Poke
                        {
                            at = entry.atSeconds,
                            scope = entry.scope,
                            parameter = entry.parameter,
                            value = entry.value,
                        });
                    saved.tracks.Add(written);
                }
            return saved;
        }

        /// <summary>
        /// The experiment back out of a settings block, or null when the block does not say —
        /// a run saved before settings travelled, or a file that was never a DD run at all.
        /// Null and a default <see cref="SimSettings"/> are different answers, and the caller is
        /// meant to tell them apart: nothing known is a reason to leave a form alone, not a
        /// reason to overwrite it with 60 fps.
        ///
        /// A version this build does not know is read anyway, for the fields it has names for.
        /// Refusing would leave the reader holding whatever settings were already in hand, which
        /// are nobody's; reading gives them the run as far as this build can describe it, and
        /// the ways it falls short are the fields that did not exist here.
        /// </summary>
        public static SimSettings Restored(Settings saved)
        {
            if (saved == null || saved.version <= 0) return null;

            var settings = new SimSettings
            {
                clock = new SimClock
                {
                    fps = saved.fps,
                    seconds = saved.seconds,
                    jitter = saved.jitter,
                    seed = saved.seed,
                },
                stimulus = new Stimulus(),
                lagRows = saved.lagRows,
                wire = saved.wire
                    ? new SyncWire
                    {
                        intervalSeconds = saved.interval,
                        latencySeconds = saved.latency,
                        dropChance = saved.dropChance,
                        quantize = saved.quantize,
                        seed = saved.wireSeed,
                        remoteJoinsAt = saved.remoteJoinsAt,
                    }
                    : null,
            };
            Inputs(saved, settings.stimulus);
            if (settings.wire == null) return settings;
            foreach (float join in saved.laterJoins) settings.wire.Joining(join);
            settings.wire.Syncs(saved.parameters.ToArray());
            return settings;
        }

        /// <summary>
        /// The timed inputs out of a settings block, whichever way that block writes them down.
        ///
        /// A version 1 run had one flat list and no way to say it was one of several — so it
        /// comes back as one track under the name that used to be at the top of the panel. Not
        /// as the hand-written track, although that is where a typed input goes today: a run
        /// taken off a recording before tracks existed is not something somebody typed, and
        /// calling it that would be this module inventing a provenance for it. A person who
        /// wants it under another name renames it, which is a thing tracks can do.
        ///
        /// Both are read rather than one or the other. A file's version says what wrote it, not
        /// what is in it, and a reader that trusted the number over the bytes would lose a
        /// run's inputs to a field somebody forgot to bump.
        /// </summary>
        static void Inputs(Settings saved, Stimulus stimulus)
        {
            foreach (var poke in saved.stimulus)
                stimulus.At(Stimulus.OneTrack, poke.at, poke.parameter, poke.value, poke.scope);
            foreach (var track in saved.tracks)
            {
                var into = stimulus.Named(track.name);
                into.muted = track.muted;
                foreach (var poke in track.entries)
                    into.entries.Add(new Stimulus.Entry
                    {
                        atSeconds = poke.at,
                        scope = poke.scope,
                        parameter = poke.parameter,
                        value = poke.value,
                    });
            }
        }
    }

    /// <summary>
    /// A run as an AnimationClip: the format runs used to be SAVED in, and the one they are
    /// still READ from and can still be EXPORTED to.
    ///
    /// <para>WHY IT IS NO LONGER WHERE A RUN GOES.</para>
    /// A clip is a set of values over time, which is what a trace is — but it is also a key on
    /// every curve at every frame whether the value moved or not, and a recording is a few
    /// hundred rows of which a handful ever change. <see cref="TraceFile"/> is the shape that
    /// argument actually leads to, and the commit that introduced it has the measurements.
    ///
    /// <para>WHY IT IS STILL HERE, BOTH WAYS.</para>
    /// Reading, because every run anybody has already saved is one of these and a tool that
    /// stopped opening its own old files would be a tool that ate an afternoon's work. Writing,
    /// because the one genuinely good thing about a clip is that the Animation window will show
    /// it — so Export As AnimationClip… stays on the menu, for reading a run in Unity's own
    /// curve editor and for handing one to anything that speaks clips. What an export is NOT is
    /// a saved run: nothing re-opens it expecting to find the run it came from, and a person
    /// who exports and then edits the curves has an animation rather than a record.
    ///
    /// The question travels with the answer here too. A clip carries the settings the run was
    /// made with (<see cref="TraceManifest.Settings"/>) as a sub-asset, which is the same block
    /// the new format writes as JSON — one shape, two containers.
    ///
    /// The curves are bound the way an animated animator parameter is bound — path "", type
    /// Animator, the signal's own name — so the Animation window shows a run without being told
    /// anything about DaerD.
    /// </summary>
    static class TraceClip
    {
        /// <summary>The curve that says where the frames were. Its keys ARE the timeline: a
        /// run with jitter has no frame rate to divide by, so the times have to travel.</summary>
        public const string StepCurve = "DD/step";

        // ---- writing --------------------------------------------------------

        public static AnimationClip ToClip(SignalTrace trace)
        {
            var clip = new AnimationClip { name = "DD Run" };
            if (trace == null || trace.Frames == 0) return clip;
            clip.frameRate = Mathf.Max(1f, Mathf.Round(trace.Frames / Mathf.Max(1e-4f,
                trace.TimeAt(trace.Frames - 1))));

            var steps = new Keyframe[trace.Frames];
            for (int frame = 0; frame < trace.Frames; frame++)
                steps[frame] = Constant(trace.TimeAt(frame), trace.StepAt(frame));
            AnimationUtility.SetEditorCurve(clip, Bind(StepCurve),
                new AnimationCurve(steps));

            foreach (var signal in trace.Signals)
            {
                var keys = new Keyframe[trace.Frames];
                for (int frame = 0; frame < trace.Frames; frame++)
                {
                    float time = trace.TimeAt(frame), value = signal.At(frame);
                    // A Float is a line between samples; everything else holds its value until
                    // it changes, which is what it did.
                    keys[frame] = signal.kind == SignalKind.Float
                        ? new Keyframe(time, value) : Constant(time, value);
                }
                AnimationUtility.SetEditorCurve(clip, Bind(signal.Path), new AnimationCurve(keys));
            }
            return clip;
        }

        /// <summary><paramref name="settings"/> is the experiment that produced
        /// <paramref name="trace"/>, or null for a trace whose provenance nobody can vouch for.
        /// Null writes no settings block rather than a plausible one — a run saved with settings
        /// it was not run with is worse than a run saved with none, because it reads exactly
        /// like one that means something.</summary>
        public static TraceManifest ToManifest(SignalTrace trace, SimSettings settings = null)
        {
            var manifest = ScriptableObject.CreateInstance<TraceManifest>();
            manifest.name = "DD Signals";
            if (settings != null) manifest.settings = TraceManifest.Wrote(settings);
            if (trace == null) return manifest;
            foreach (var signal in trace.Signals)
                manifest.signals.Add(TraceManifest.EntryFor(signal));
            return manifest;
        }

        /// <summary>
        /// Exports the run to a .anim, with its signal list and the experiment that produced it
        /// beside it in the same file. Returns the clip that is now on disk.
        ///
        /// Still writes the manifest, although nothing re-opens an export expecting a run: a
        /// clip whose curves are named "Local/GestureLeft" and whose state rows are bare
        /// integers is unreadable without one, and an export that could not be read back is a
        /// worse thing to hand somebody than one that can.
        /// </summary>
        public static AnimationClip Save(SignalTrace trace, string path,
            SimSettings settings = null)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var clip = ToClip(trace);
            AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.AddObjectToAsset(ToManifest(trace, settings), clip);
            // Both, and in this order: a sub-asset added to a clip that has not been written
            // through stays invisible until the asset is imported again.
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        }

        // ---- reading --------------------------------------------------------

        /// <summary>
        /// The run back out of a clip. With the manifest beside it every signal keeps its scope,
        /// its kind and a state's names; without one — a clip from somewhere else — the curves
        /// still load, as unscoped Floats, which is enough to look at and enough to drive a run
        /// with.
        /// </summary>
        public static SignalTrace Load(AnimationClip clip)
        {
            var trace = new SignalTrace();
            if (clip == null) return trace;
            var bindings = AnimationUtility.GetCurveBindings(clip);
            AnimationCurve steps = null;
            foreach (var binding in bindings)
                if (binding.propertyName == StepCurve)
                    steps = AnimationUtility.GetEditorCurve(clip, binding);
            if (steps == null || steps.length == 0) return trace;

            var manifest = ManifestOf(clip);
            var declared = new List<SignalTrace.Signal>();
            var curves = new List<AnimationCurve>();
            foreach (var binding in bindings)
            {
                if (binding.propertyName == StepCurve) continue;
                var entry = Lookup(manifest, binding.propertyName);
                var signal = trace.Declare(
                    entry != null ? entry.scope : string.Empty,
                    entry != null ? entry.name : binding.propertyName,
                    entry != null ? (SignalKind)entry.kind : SignalKind.Float,
                    entry != null && entry.labels.Count > 0 ? entry.labels.ToArray() : null);
                declared.Add(signal);
                curves.Add(AnimationUtility.GetEditorCurve(clip, binding));
            }

            for (int frame = 0; frame < steps.length; frame++)
            {
                float time = steps[frame].time;
                trace.Frame(time, steps[frame].value);
                for (int i = 0; i < declared.Count; i++)
                    declared[i].Push(curves[i].Evaluate(time));
            }
            return trace;
        }

        /// <summary>The experiment a saved clip was made with, or null when it does not say —
        /// see <see cref="TraceManifest.Restored"/>, which is where that reading lives now that
        /// two formats write the same block.</summary>
        public static SimSettings SettingsOf(AnimationClip clip)
        {
            var manifest = ManifestOf(clip);
            return TraceManifest.Restored(manifest != null ? manifest.settings : null);
        }

        public static TraceManifest ManifestOf(AnimationClip clip)
        {
            if (clip == null) return null;
            string path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is TraceManifest manifest) return manifest;
            return null;
        }

        static TraceManifest.Entry Lookup(TraceManifest manifest, string path)
        {
            if (manifest == null) return null;
            foreach (var entry in manifest.signals)
            {
                string full = string.IsNullOrEmpty(entry.scope)
                    ? entry.name : entry.scope + "/" + entry.name;
                if (full == path) return entry;
            }
            return null;
        }

        static EditorCurveBinding Bind(string property) =>
            EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), property);

        /// <summary>A key that holds its value until the next one — a Bool, an Int and a state
        /// index are all step functions, and drawing them as ramps would be a lie.</summary>
        static Keyframe Constant(float time, float value) =>
            new Keyframe(time, value)
            {
                inTangent = float.PositiveInfinity,
                outTangent = float.PositiveInfinity,
            };
    }
}

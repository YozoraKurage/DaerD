using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>What a clip cannot say about a curve: whose it is, and what its numbers
    /// mean. Rides along as a sub-asset so a saved run is still one file.</summary>
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

        public List<Entry> signals = new List<Entry>();
    }

    /// <summary>
    /// A run, as an AnimationClip. Not a container invented for the purpose — a clip IS a set
    /// of values over time, which is what a trace is, and Unity already has a curve editor, a
    /// diff, a meta file and a Ctrl+Z for one.
    ///
    /// It reads both ways on purpose. A saved run opens again as a run, and a saved run also
    /// opens as INPUT: the values one experiment recorded become the pokes the next one is
    /// driven by. Being able to write a stimulus by hand and not being able to capture one
    /// would have been a strange place to stop.
    ///
    /// The curves are bound the way an animated animator parameter is bound — path "", type
    /// Animator, the signal's own name — so the Animation window shows a saved run without
    /// being told anything about DaerD.
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

        public static TraceManifest ToManifest(SignalTrace trace)
        {
            var manifest = ScriptableObject.CreateInstance<TraceManifest>();
            manifest.name = "DD Signals";
            if (trace == null) return manifest;
            foreach (var signal in trace.Signals)
            {
                var entry = new TraceManifest.Entry
                {
                    scope = signal.scope,
                    name = signal.name,
                    kind = (int)signal.kind,
                };
                if (signal.labels != null) entry.labels.AddRange(signal.labels);
                manifest.signals.Add(entry);
            }
            return manifest;
        }

        /// <summary>Writes the run to a .anim, with its signal list beside it in the same
        /// file. Returns the clip that is now on disk.</summary>
        public static AnimationClip Save(SignalTrace trace, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var clip = ToClip(trace);
            AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.AddObjectToAsset(ToManifest(trace), clip);
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

        /// <summary>
        /// The clip as input rather than as a record: every moment one of these parameters
        /// changed becomes a poke at that second. What a run recorded can then drive the next
        /// one — the same experiment against a different wire, a different frame rate, a
        /// different seed.
        /// </summary>
        public static Stimulus ToStimulus(AnimationClip clip, string scope,
            ICollection<string> parameters)
        {
            var stimulus = new Stimulus();
            var trace = Load(clip);
            foreach (var signal in trace.Signals)
            {
                // Only what the target controller can actually be told, and only the wearer's
                // side of a two-client recording: a remote's values are what the run works
                // out, not what it is given.
                if (parameters != null && !parameters.Contains(signal.name)) continue;
                if (!string.IsNullOrEmpty(signal.scope)
                    && signal.scope != Simulation.LocalScope) continue;
                if (signal.kind == SignalKind.State) continue;

                for (int frame = 0; frame < trace.Frames; frame++)
                {
                    if (frame != 0 && !signal.ChangedAt(frame)) continue;
                    // The start of the frame the change was seen in, not its end: a sample is
                    // taken after the frame ran, so what caused it was already true when the
                    // frame began. A quarter of a frame earlier still, so that repeating the
                    // run lands the poke on the same frame and not on the one after it however
                    // the arithmetic rounds.
                    float at = Mathf.Max(0f,
                        trace.StartOfFrame(frame) - trace.StepAt(frame) * 0.25f);
                    stimulus.At(at, signal.name, signal.At(frame), scope);
                }
            }
            return stimulus;
        }

        public static TraceManifest ManifestOf(AnimationClip clip)
        {
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

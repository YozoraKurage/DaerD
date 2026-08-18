using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
// UnityEngine has a CompressionLevel of its own (it is about asset bundles), and an
// alias is what keeps the two apart without spelling the namespace at the call site.
using CompressionLevel = System.IO.Compression.CompressionLevel;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// A run, as a file of its own.
    ///
    /// <para>WHY NOT A CLIP ANY MORE.</para>
    /// A run used to save as an AnimationClip, on the reasoning that a clip IS a set of values
    /// over time. It is — and a clip is also Unity YAML with a key on every signal at every
    /// frame, whether or not the signal moved. A recording is not that shape: it is a few
    /// hundred rows of which a handful ever change, sampled thousands of times, so the honest
    /// encoding is the moments a value became something else and nothing at all for the frames
    /// in between. Written that way the same run is orders of magnitude smaller, which is the
    /// difference between keeping the recordings of an afternoon and deleting them.
    ///
    /// <para>THE SHAPE ON DISK.</para>
    /// <list type="bullet">
    /// <item>An uncompressed header: the five bytes <c>DDRUN</c> and a little-endian version.
    /// Outside the compression on purpose — a reader has to be able to say "this is not one of
    /// mine" and "this is one of mine from a later build" without first trusting the bytes
    /// enough to inflate them.</item>
    /// <item>Everything after it through a <see cref="GZipStream"/>.</item>
    /// <item>One JSON block (<see cref="Meta"/>): the signal list and the experiment, the same
    /// fields the sub-asset carried, through <see cref="JsonUtility"/>.</item>
    /// <item>The clock: a frame count, then that many float32 times, then that many float32
    /// steps. Both, rather than one reconstructed from the other — a live trace that has been
    /// trimmed starts at the second it starts at, so the first frame's time is not its length,
    /// and jitter means there is no frame rate to divide by either. Cheap to be exact about:
    /// two nearly constant float arrays are what gzip is best at.</item>
    /// <item>Then, per signal in the manifest's order, its change points: a count, then pairs
    /// of (frame index as a varint delta, float32 value). Frame 0 is always one of them, so a
    /// reader never has to invent a starting value.</item>
    /// </list>
    ///
    /// <para>WHAT THE FORMAT DELIBERATELY DOES NOT DEPEND ON.</para>
    /// A ScriptableObject sub-asset. The manifest's SHAPE is reused — <see cref="TraceManifest"/>
    /// is where the fields and their compatibility rules live, and there is no second definition
    /// of what a saved experiment looks like — but nothing here goes through the AssetDatabase.
    /// That takes with it the trap that a sub-asset added to an asset which has not been written
    /// through stays invisible until the asset is imported again, and the whole question of
    /// whether a run has to live under Assets/ at all.
    ///
    /// <para>WHAT IS NOT IN IT.</para>
    /// <see cref="SignalTrace.Recorded"/>, which counts the frames a live session has since
    /// dropped. That is a session's bookkeeping — it exists so a viewer can tell how much of a
    /// still-growing trace it has not drawn yet — and a file is not growing. A loaded run
    /// therefore says it has recorded exactly the frames it holds, which is true of it.
    ///
    /// <para>NO SCRIPTED IMPORTER, IN V1.</para>
    /// A .ddrun under Assets/ imports as a DefaultAsset: it shows in the Project window, it can
    /// be moved and deleted, and nothing about it is broken. What an importer would buy is a
    /// typed ObjectField to drag a run into, and the window picks runs through a file panel
    /// today. An importer is also a promise — every run in every project re-imports through it
    /// on every version of it — and making that promise before anybody has asked to drag a run
    /// anywhere is the expensive order to do this in.
    /// </summary>
    static class TraceFile
    {
        /// <summary>Without the dot, which is the shape Unity's file panels want.</summary>
        public const string Extension = "ddrun";

        /// <summary>What this build writes. A reader refuses a number above this rather than
        /// guessing: a later format may mean something different by the same bytes, and a run
        /// read wrongly is worse than a run not read at all — see <see cref="TraceManifest.Settings.Current"/>
        /// for the other half of that argument, which is about fields rather than layout and so
        /// comes out the other way.</summary>
        public const int Version = 1;

        static readonly byte[] Magic = { (byte)'D', (byte)'D', (byte)'R', (byte)'U', (byte)'N' };

        /// <summary>A run and the experiment that produced it, which is what a file holds and
        /// what every caller that opens one wants both halves of. <see cref="settings"/> is null
        /// for a file that does not say — see <see cref="TraceManifest.Restored"/>.</summary>
        internal sealed class Run
        {
            public SignalTrace trace = new SignalTrace();
            public SimSettings settings;
        }

        /// <summary>The JSON block. Two fields because there are two things a run's numbers
        /// cannot say for themselves: whose each row is and what its values mean, and what was
        /// asked to produce them.</summary>
        [Serializable]
        sealed class Meta
        {
            public List<TraceManifest.Entry> signals = new List<TraceManifest.Entry>();
            public TraceManifest.Settings settings = new TraceManifest.Settings();
        }

        /// <summary>Whether this path names a run in this format rather than a legacy clip.
        /// By extension, because the alternative — open it and look at the magic — is a disk
        /// read to decide which of two readers to hand a path to.</summary>
        public static bool Is(string path) =>
            !string.IsNullOrEmpty(path)
            && path.EndsWith("." + Extension, StringComparison.OrdinalIgnoreCase);

        // ---- writing --------------------------------------------------------

        /// <summary>
        /// Writes the run, and the experiment that produced it, to <paramref name="path"/>.
        ///
        /// <paramref name="settings"/> is null for a trace whose provenance nobody can vouch
        /// for, and null writes an empty settings block rather than a plausible one — a run
        /// saved with settings it was not run with is worse than a run saved with none, because
        /// it reads exactly like one that means something.
        /// </summary>
        public static void Save(SignalTrace trace, string path, SimSettings settings = null)
        {
            if (string.IsNullOrEmpty(path)) return;
            trace = trace ?? new SignalTrace();
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            using (var file = File.Create(path))
            {
                file.Write(Magic, 0, Magic.Length);
                file.WriteByte((byte)Version);
                file.WriteByte((byte)(Version >> 8));
                file.WriteByte((byte)(Version >> 16));
                file.WriteByte((byte)(Version >> 24));
                using (var zip = new GZipStream(file, CompressionLevel.Optimal))
                using (var writer = new BinaryWriter(zip, new UTF8Encoding(false)))
                    Body(trace, settings, writer);
            }
        }

        static void Body(SignalTrace trace, SimSettings settings, BinaryWriter writer)
        {
            var meta = new Meta();
            foreach (var signal in trace.Signals) meta.signals.Add(TraceManifest.EntryFor(signal));
            if (settings != null) meta.settings = TraceManifest.Wrote(settings);
            writer.Write(JsonUtility.ToJson(meta));

            int frames = trace.Frames;
            writer.Write(frames);
            for (int frame = 0; frame < frames; frame++) writer.Write(trace.TimeAt(frame));
            for (int frame = 0; frame < frames; frame++) writer.Write(trace.StepAt(frame));
            foreach (var signal in trace.Signals) Column(signal, frames, writer);
        }

        /// <summary>
        /// One signal, as the frames it became something else.
        ///
        /// Counted first and then written, which is two passes over the column rather than a
        /// buffer of unknown size per signal — a run of a few hundred rows is a few hundred
        /// scans of an array of floats, and the alternative is holding a second copy of the
        /// whole recording in memory while saving it.
        ///
        /// A change is compared exactly rather than through <c>Mathf.Approximately</c>, which is
        /// what <see cref="SignalTrace.Signal.ChangedAt"/> asks and is the right question for a
        /// viewer drawing an edge. It is the wrong one here: a difference too small to draw is
        /// still a difference the file has to give back, or the run that comes out is not the
        /// run that went in.
        /// </summary>
        static void Column(SignalTrace.Signal signal, int frames, BinaryWriter writer)
        {
            if (frames <= 0)
            {
                writer.Write(0);
                return;
            }
            int count = 1;
            for (int frame = 1; frame < frames; frame++)
                if (!signal.At(frame).Equals(signal.At(frame - 1))) count++;
            writer.Write(count);

            Delta(writer, 0u);
            writer.Write(signal.At(0));
            int previous = 0;
            for (int frame = 1; frame < frames; frame++)
            {
                if (signal.At(frame).Equals(signal.At(frame - 1))) continue;
                Delta(writer, (uint)(frame - previous));
                writer.Write(signal.At(frame));
                previous = frame;
            }
        }

        // ---- reading --------------------------------------------------------

        /// <summary>The run out of a file, without its settings — the reading a viewer wants.</summary>
        public static SignalTrace Load(string path) => Read(path).trace;

        /// <summary>The experiment a saved run was made with, or null when the file does not
        /// say. Null and a default <see cref="SimSettings"/> are different answers and the
        /// caller is meant to tell them apart: nothing known is a reason to leave a form alone,
        /// not a reason to overwrite it with 60 fps.</summary>
        public static SimSettings SettingsOf(string path) => Read(path).settings;

        /// <summary>
        /// A file, whole. Both halves at once because both come out of one pass, and a caller
        /// that asked for them separately would inflate the same run twice.
        ///
        /// Throws rather than returning an empty run: every way this fails is a thing the
        /// person who picked the file needs told, and the messages are written for them.
        /// </summary>
        /// <exception cref="IOException">The file is not there, or cannot be read.</exception>
        /// <exception cref="InvalidDataException">It is not a run, is a run from a later build,
        /// or is damaged.</exception>
        public static Run Read(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException(
                    L.Tr("There is no run at '{0}'.", path ?? string.Empty), path);

            using (var file = File.OpenRead(path))
            {
                Header(file);
                using (var zip = new GZipStream(file, CompressionMode.Decompress))
                using (var reader = new BinaryReader(zip, new UTF8Encoding(false)))
                    return Body(reader);
            }
        }

        /// <summary>
        /// The marker and the version, read before anything is inflated.
        ///
        /// A version this build does not know is refused with the number in the message, because
        /// the answer to it is "the DaerD that wrote this", and a reader who is not told the
        /// number cannot go looking for it. The opposite decision from a settings BLOCK from a
        /// later build, which is read for the fields this build has names for: a field it has
        /// never heard of is one it can survive not knowing, and a layout it has never heard of
        /// is bytes it would misread as numbers.
        /// </summary>
        static void Header(Stream file)
        {
            var head = new byte[Magic.Length + 4];
            bool whole = Fill(file, head) == head.Length;
            for (int i = 0; whole && i < Magic.Length; i++) whole &= head[i] == Magic[i];
            if (!whole)
                throw new InvalidDataException(L.Tr(
                    "This file is not a DD run: it does not begin with the run marker."));

            int version = head[Magic.Length]
                | (head[Magic.Length + 1] << 8)
                | (head[Magic.Length + 2] << 16)
                | (head[Magic.Length + 3] << 24);
            if (version <= 0 || version > Version)
                throw new InvalidDataException(L.Tr(
                    "This run is in format version {0} and this build of DaerD reads up to version {1}. Update DaerD to open it.",
                    version, Version));
        }

        static int Fill(Stream stream, byte[] into)
        {
            int got = 0;
            while (got < into.Length)
            {
                int read = stream.Read(into, got, into.Length - got);
                if (read <= 0) break;
                got += read;
            }
            return got;
        }

        static Run Body(BinaryReader reader)
        {
            var meta = JsonUtility.FromJson<Meta>(reader.ReadString()) ?? new Meta();
            int frames = reader.ReadInt32();
            if (frames < 0) throw new InvalidDataException(Damaged());

            var times = new float[frames];
            for (int frame = 0; frame < frames; frame++) times[frame] = reader.ReadSingle();
            var steps = new float[frames];
            for (int frame = 0; frame < frames; frame++) steps[frame] = reader.ReadSingle();

            var trace = new SignalTrace();
            var signals = new List<SignalTrace.Signal>();
            foreach (var entry in meta.signals)
                signals.Add(trace.Declare(entry.scope, entry.name, (SignalKind)entry.kind,
                    Labels(entry)));
            for (int frame = 0; frame < frames; frame++) trace.Frame(times[frame], steps[frame]);
            foreach (var signal in signals) Column(reader, signal, frames);

            return new Run
            {
                trace = trace,
                settings = TraceManifest.Restored(meta.settings),
            };
        }

        /// <summary>
        /// The names behind a state row's numbers.
        ///
        /// A State comes back holding an array even when the layer had nothing to name, because
        /// an empty band is still a band and a row that arrived with no names is not the same
        /// row as one that was never meant to have any. Every other kind is the second case,
        /// which is what null means here — the shape <see cref="SignalTrace.Signal.labels"/>
        /// documents.
        /// </summary>
        static string[] Labels(TraceManifest.Entry entry)
        {
            if (entry.labels != null && entry.labels.Count > 0) return entry.labels.ToArray();
            return (SignalKind)entry.kind == SignalKind.State ? new string[0] : null;
        }

        /// <summary>The column back out, expanded to one sample per frame. A held value between
        /// change points rather than an interpolated one: what the file says is that nothing
        /// happened, and a Float that was steady for a second is not a ramp.</summary>
        static void Column(BinaryReader reader, SignalTrace.Signal signal, int frames)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > frames) throw new InvalidDataException(Damaged());

            var at = new int[count];
            var value = new float[count];
            int previous = 0;
            for (int i = 0; i < count; i++)
            {
                long frame = (i == 0 ? 0L : previous) + Delta(reader);
                if (frame >= frames) throw new InvalidDataException(Damaged());
                previous = at[i] = (int)frame;
                value[i] = reader.ReadSingle();
            }

            int index = 0;
            float held = 0f;
            for (int frame = 0; frame < frames; frame++)
            {
                while (index < count && at[index] == frame) held = value[index++];
                signal.Push(held);
            }
        }

        static string Damaged() =>
            L.Tr("This run is damaged: it does not hold as many values as it says it does.");

        // ---- varint ---------------------------------------------------------

        /// <summary>
        /// A frame index as its distance from the previous change, seven bits at a time.
        ///
        /// Which is the whole reason the deltas rather than the indices are written: a signal
        /// that moves every few frames spends one byte per change however long the recording
        /// is, where the index itself costs three or four bytes once a run is a few thousand
        /// frames long.
        /// </summary>
        static void Delta(BinaryWriter writer, uint frames)
        {
            while (frames >= 0x80)
            {
                writer.Write((byte)(frames | 0x80));
                frames >>= 7;
            }
            writer.Write((byte)frames);
        }

        static uint Delta(BinaryReader reader)
        {
            uint frames = 0;
            for (int shift = 0; shift <= 28; shift += 7)
            {
                byte part = reader.ReadByte();
                frames |= (uint)(part & 0x7F) << shift;
                if ((part & 0x80) == 0) return frames;
            }
            throw new InvalidDataException(Damaged());
        }
    }
}

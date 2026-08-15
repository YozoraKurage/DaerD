using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// What the avatar actually did, written down. Not a simulation of anything — the values
    /// come off a controller somebody else is running, in Play mode, and the only thing this
    /// contributes is looking once a frame and keeping what it saw.
    ///
    /// <para>WHY IT READS A GRAPH AND NOT AN ANIMATOR.</para>
    /// The two tools an avatar is worn with in the editor — GestureManager and Av3Emulator —
    /// both build VRChat's layer stack as a PlayableGraph: one
    /// <see cref="AnimatorControllerPlayable"/> per VRC playable layer under a layer mixer,
    /// with the Animator component as the graph's output and no controller of its own. Ask that
    /// Animator for a parameter and it may answer for a layer nobody asked about, or for
    /// nothing at all. So the values are taken where they live, through Unity's own Playable
    /// API, which means this works the same for both tools and for whatever the third one turns
    /// out to be. Neither tool's types are named anywhere here — <see cref="PlayTools"/> is
    /// where they are named, and what it is asked is only ever which of the avatars in the
    /// scene is meant, never what any of them is doing.
    ///
    /// The alternative was reading GestureManager's own Vrc3Param objects by reflection. It was
    /// dropped: private API, a different shape in every release, and Av3Emulator would need a
    /// second implementation of the same idea that could disagree with the first.
    ///
    /// <para>WHAT A RECORDING CANNOT SEE.</para>
    /// A Trigger is a press, and this is a poll: it looks once per editor update, and a trigger
    /// raised and consumed between two looks leaves nothing standing to be read. Recorded as a
    /// <see cref="SignalKind.Trigger"/> all the same, because that is what it is — the row is
    /// honest about the frames it saw and silent about the ones it did not. The count of frames
    /// it did not see is kept (<see cref="Missed"/>) so a reader can tell a quiet run from an
    /// unwatched one.
    ///
    /// Layer weight is the controller's own, the same row a simulated run records. The weight
    /// VRChat mixes the whole playable layer in at — the mixer input above this playable — is
    /// not recorded: it is a different quantity with the same word for it, and a row that
    /// quietly meant sometimes one and sometimes the other would be worse than the row being
    /// absent.
    /// </summary>
    sealed class PlayRecorder
    {
        sealed class Reader
        {
            public SignalTrace.Signal signal;
            public System.Func<float> read;
        }

        readonly List<Reader> _readers = new List<Reader>();
        readonly PlaySource _source;
        /// <summary>The avatar this is watching. Kept for one question only — is it still
        /// there — because the graph cannot answer it (see <see cref="Alive"/>).</summary>
        readonly Animator _animator;
        int _lastFrame;
        float _lastTime, _zero;
        bool _started;

        public SignalTrace Trace { get; } = new SignalTrace();

        /// <summary>Whether a playable running THIS window's controller was found, which is
        /// what the state, via and weight rows need — without it a recording is the parameters
        /// and nothing else. See <see cref="Matching"/> for what "running this controller"
        /// is decided by.</summary>
        public bool Matched { get; private set; }

        /// <summary>Whether the values come off a PlayableGraph rather than off the Animator
        /// component directly. False is the plain-playback case, and worth showing: it means no
        /// tool has hold of this avatar, so nothing VRChat-shaped is happening to it.</summary>
        public bool FromGraph { get; private set; }

        /// <summary>Frames the editor's update did not get a look at — the gaps in
        /// <c>Time.frameCount</c> between two samples, added up. A recording with a large
        /// number here is one whose Triggers and instant transitions are not to be trusted.</summary>
        public int Missed { get; private set; }

        public int Frames => Trace.Frames;

        /// <summary>
        /// Whether there is still something to read.
        ///
        /// Asked of the AVATAR rather than of the graph, because the graph cannot be asked:
        /// measured, a destroyed PlayableGraph keeps every handle into it valid and goes on
        /// answering (see <see cref="Matching"/>). What that costs is stated rather than papered
        /// over — a tool that drops its graph and leaves the avatar in the scene is one this
        /// goes on recording, and the values it records after that are the last ones the dead
        /// graph held. What it catches is the case that actually happens: leaving Play mode
        /// takes the scene with it, and a recorder still ticking would be reading a destroyed
        /// Animator.
        /// </summary>
        public bool Alive => _animator != null && _source != null && _source.Alive;

        PlayRecorder(Animator animator, PlaySource source, StateTables tables, int[] alignment,
            bool fromGraph)
        {
            _animator = animator;
            _source = source;
            FromGraph = fromGraph;
            Matched = alignment != null;
            Declare(tables, alignment);
        }

        // ---- finding something to read --------------------------------------

        /// <summary>
        /// Every Animator in the scene some PlayableGraph is writing to — the candidate list,
        /// found without knowing which tool built the graph.
        ///
        /// Measured (DynamicAnalyzeRecTests): a plain Animator component with a controller on it
        /// has a graph of its own in this list too, named after the component, so an avatar
        /// nobody has wrapped turns up here as readily as one under a tool.
        /// </summary>
        public static List<Animator> Driven()
        {
            var found = new List<Animator>();
            foreach (var graph in UnityEditor.Playables.Utility.GetAllGraphs())
            {
                if (!graph.IsValid()) continue;
                for (int i = 0; i < graph.GetOutputCount(); i++)
                {
                    var target = TargetOf(graph.GetOutput(i));
                    if (target == null || found.Contains(target)) continue;
                    found.Add(target);
                }
            }
            return found;
        }

        /// <summary>
        /// Every controller playable feeding this Animator, in the order the graphs were made.
        ///
        /// The walk stops the moment it reaches one rather than descending into it: measured, an
        /// <see cref="AnimatorControllerPlayable"/> is several playables inside, and walking
        /// into it would find its parts and call them layers.
        /// </summary>
        public static List<AnimatorControllerPlayable> PlayablesOn(Animator animator)
        {
            var found = new List<AnimatorControllerPlayable>();
            if (animator == null) return found;
            foreach (var graph in UnityEditor.Playables.Utility.GetAllGraphs())
            {
                if (!graph.IsValid()) continue;
                for (int i = 0; i < graph.GetOutputCount(); i++)
                {
                    var output = graph.GetOutput(i);
                    var target = TargetOf(output);
                    if (target == null || target != animator) continue;
                    Walk(output.GetSourcePlayable(), found, 0);
                }
            }
            return found;
        }

        static Animator TargetOf(PlayableOutput output)
        {
            if (!output.IsOutputValid()) return null;
            if (!output.IsPlayableOutputOfType<AnimationPlayableOutput>()) return null;
            var target = ((AnimationPlayableOutput)output).GetTarget();
            // A graph whose Animator has been destroyed answers a real null here — measured —
            // which is what keeps last session's graphs out of this session's candidates.
            return target == null ? null : target;
        }

        /// <summary>Depth is bounded because a graph is somebody else's data structure and a
        /// cycle in one would be their bug hanging the editor rather than ours.</summary>
        const int Deep = 24;

        static void Walk(Playable playable, List<AnimatorControllerPlayable> found, int depth)
        {
            if (!playable.IsValid() || depth > Deep) return;
            if (playable.IsPlayableOfType<AnimatorControllerPlayable>())
            {
                found.Add((AnimatorControllerPlayable)playable);
                return;
            }
            for (int i = 0; i < playable.GetInputCount(); i++)
                Walk(playable.GetInput(i), found, depth + 1);
        }

        /// <summary>
        /// Which of these playables is running the window's controller, or -1.
        ///
        /// Decided on the layer names, as a multiset: same count, same names, order and
        /// duplicates included. Not on the controller ASSET, which is the obvious test and the
        /// wrong one — GestureManager hands the controller to Mecanim wrapped in an
        /// AnimatorOverrideController, so the object running is never the object in the field.
        /// Measured (DynamicAnalyzeRecTests): the wrapper changes nothing a recording reads —
        /// the layer names are the base controller's and so is every state's fullPathHash, so
        /// the tables built from the asset in the field name the states of the avatar on screen.
        ///
        /// The LAST match wins rather than the first. Measured: a destroyed PlayableGraph is
        /// still handed out by <c>Utility.GetAllGraphs</c> and still answers every question put
        /// to it, so an avatar re-selected inside one Play session leaves the old graph in the
        /// list beside the new one. Graphs come out in the order they were made, so the newest
        /// is the one that is really running.
        /// </summary>
        public static int Matching(AnimatorController controller,
            List<AnimatorControllerPlayable> playables)
        {
            if (controller == null || playables == null) return -1;
            var names = LayerNames(controller);
            if (names.Count == 0) return -1;
            int at = -1;
            for (int i = 0; i < playables.Count; i++)
                if (playables[i].IsValid() && Fits(playables[i], names)) at = i;
            return at;
        }

        static List<string> LayerNames(AnimatorController controller)
        {
            var names = new List<string>();
            foreach (var layer in controller.layers) names.Add(layer.name ?? string.Empty);
            return names;
        }

        static bool Fits(AnimatorControllerPlayable playable, List<string> names)
        {
            if (playable.GetLayerCount() != names.Count) return false;
            var left = new List<string>(names);
            for (int i = 0; i < playable.GetLayerCount(); i++)
                if (!left.Remove(playable.GetLayerName(i))) return false;
            return true;
        }

        /// <summary>
        /// Which controller layer each of the source's layers is, by name — the first layer of
        /// that name that has not been claimed yet.
        ///
        /// A mapping rather than an assumption that index i is layer i, because the multiset
        /// above lets the two agree on names while disagreeing on order. Reading a layer's state
        /// under another layer's labels would produce rows that are wrong rather than absent,
        /// which is the one failure a recording must not have.
        /// </summary>
        static int[] Align(PlaySource source, AnimatorController controller)
        {
            var names = LayerNames(controller);
            var taken = new bool[names.Count];
            var alignment = new int[source.LayerCount];
            for (int i = 0; i < alignment.Length; i++)
            {
                alignment[i] = -1;
                string name = source.LayerName(i);
                for (int j = 0; j < names.Count; j++)
                {
                    if (taken[j] || names[j] != name) continue;
                    taken[j] = true;
                    alignment[i] = j;
                    break;
                }
            }
            return alignment;
        }

        /// <summary>
        /// A recorder aimed at this Animator, or null for no Animator at all.
        ///
        /// Three outcomes, in the order they are looked for: a graph playable whose layers are
        /// this controller's, which is a recording with state rows; any graph playable at all,
        /// which is a recording of parameters only and says so; and no graph, which falls back
        /// to reading the Animator component the ordinary way.
        ///
        /// The unmatched case reads the LAST playable for the same reason the matched case picks
        /// the last — it is the newest, and there is nothing better to go on once the layer
        /// names have already failed to say which one is meant.
        /// </summary>
        public static PlayRecorder On(Animator animator, AnimatorController controller)
        {
            if (animator == null) return null;
            var playables = PlayablesOn(animator);
            int at = Matching(controller, playables);
            if (at >= 0)
            {
                var source = PlaySource.Of(playables[at]);
                return new PlayRecorder(animator, source, new StateTables(controller),
                    Align(source, controller), true);
            }
            for (int i = playables.Count - 1; i >= 0; i--)
            {
                if (!playables[i].IsValid()) continue;
                return new PlayRecorder(animator, PlaySource.Of(playables[i]), null, null, true);
            }
            return new PlayRecorder(animator, PlaySource.Of(animator), null, null, false);
        }

        /// <summary>
        /// The Animator this window would record if nobody said which. What the arm toggle
        /// needs, because entering Play mode builds the scene again and whatever was in the
        /// field is not the object that came back.
        ///
        /// Running this controller comes first and stays first, ahead of anything a tool says:
        /// arming refuses an avatar that is not running it (see the window's StartRecording), so
        /// a preference that could pick one over an avatar that IS running it would be a
        /// preference for never starting. Underneath that, and only there, the tools break the
        /// tie that used to be broken by "whichever graph Unity handed out first" — see
        /// <see cref="PlayTools.Preferred"/> for the order and why.
        /// </summary>
        public static Animator Likeliest(AnimatorController controller)
        {
            var driven = PlayTools.Candidates(Driven());
            var running = new List<Animator>();
            foreach (var animator in driven)
                if (Matching(controller, PlayablesOn(animator)) >= 0) running.Add(animator);
            var pick = PlayTools.Preferred(running);
            if (pick != null) return pick;
            if (running.Count > 0) return running[0];
            pick = PlayTools.Preferred(driven);
            if (pick != null) return pick;
            return driven.Count > 0 ? driven[0] : null;
        }

        // ---- what it writes down --------------------------------------------

        /// <summary>
        /// The rows, named exactly the way a simulated run names them — parameters by name, then
        /// "layer/state", "layer/transition", "layer/via" and the layer's weight row. Not a
        /// coincidence and not a nicety: it is what lets a recording be laid under a run as a
        /// ghost, saved as the same kind of clip, and read by the same findings.
        ///
        /// The parameters come off the SOURCE rather than off the controller, because the source
        /// is what is running. A recording with no matched playable still gets them, and they
        /// are then the only thing it has.
        /// </summary>
        void Declare(StateTables tables, int[] alignment)
        {
            for (int i = 0; i < _source.ParameterCount; i++)
            {
                var parameter = _source.ParameterAt(i);
                string name = parameter.name;
                var type = parameter.type;
                var signal = Trace.Declare(Simulation.PlayScope, name, TraceRecorder.KindOf(type));
                _readers.Add(new Reader { signal = signal, read = () => _source.Read(name, type) });
            }
            if (alignment == null) return;

            for (int i = 0; i < alignment.Length; i++)
            {
                int layer = i, at = alignment[i];
                if (at < 0) continue;
                string name = tables.LayerName(at);
                var state = Trace.Declare(Simulation.PlayScope, name + "/state",
                    SignalKind.State, tables.StateLabels(at));
                _readers.Add(new Reader
                {
                    signal = state,
                    read = () => tables.StateAt(at, _source.StateHash(layer)),
                });
                var moving = Trace.Declare(Simulation.PlayScope, name + "/transition",
                    SignalKind.Bool);
                _readers.Add(new Reader
                {
                    signal = moving,
                    read = () => _source.InTransition(layer) ? 1f : 0f,
                });
                var via = Trace.Declare(Simulation.PlayScope, name + "/via",
                    SignalKind.State, tables.TransitionLabels(at));
                _readers.Add(new Reader
                {
                    signal = via,
                    read = () => _source.InTransition(layer)
                        ? tables.TransitionAt(at, _source.TransitionHash(layer)) : -1,
                });
                var weight = Trace.Declare(Simulation.PlayScope, SimClient.WeightRow(name),
                    SignalKind.Float);
                _readers.Add(new Reader
                {
                    signal = weight,
                    read = () => _source.LayerWeight(layer),
                });
            }
        }

        /// <summary>
        /// One look, if this frame has not been looked at yet.
        ///
        /// Keyed on the frame counter rather than on the clock: the editor's update fires more
        /// than once a frame and the values do not change between those calls, so a second
        /// sample would be a duplicate frame in the trace and a step of zero seconds beside it.
        /// The gaps go the other way too — the editor can be busy for several frames at once —
        /// and those are counted rather than filled in, because a frame nobody looked at is a
        /// frame this has nothing to say about.
        ///
        /// Time is counted from the first sample, so a recording starts at 0 the way a run does
        /// and the two can be compared without anybody doing arithmetic. The first frame is
        /// given a step of zero for the same reason: nothing elapsed that this saw.
        ///
        /// Nothing is ever trimmed. A live SESSION keeps a window because it is meant to be
        /// watched and left running; a recording is meant to be kept, and dropping the beginning
        /// of one would throw away the part somebody pressed something in.
        ///
        /// Taking the frame and the clock as arguments rather than reading them is what lets the
        /// dropping and the counting be tested at all — a test can hand it any frame numbers it
        /// likes, including the ones a busy editor would produce.
        /// </summary>
        internal bool Sample(int frame, float time)
        {
            if (!Alive) return false;
            if (_started && frame == _lastFrame) return false;
            float step = 0f;
            if (_started)
            {
                Missed += Mathf.Max(0, frame - _lastFrame - 1);
                step = Mathf.Max(0f, time - _lastTime);
            }
            else _zero = time;
            _started = true;
            _lastFrame = frame;
            _lastTime = time;

            Trace.Frame(time - _zero, step);
            foreach (var reader in _readers) reader.signal.Push(reader.read());
            return true;
        }
    }
}

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
            /// <summary>Which avatar this row comes off. Held rather than looked up, because the
            /// question asked of it once a frame is whether that avatar is still there.</summary>
            public Watched of;
            public System.Func<float> read;
        }

        /// <summary>
        /// One avatar being read, and the scope its rows are written under.
        ///
        /// There is always at least one — the avatar somebody aimed at — and there is one more
        /// per other person's copy of it when the recording was asked for those too. Each is
        /// matched against the controller on its own: a copy is a separate graph and could have
        /// been left running something else, and a recording that assumed otherwise would name
        /// one avatar's states with another's labels.
        /// </summary>
        sealed class Watched
        {
            /// <summary>The avatar this is watching. Kept for one question only — is it still
            /// there — because the graph cannot answer it (see <see cref="Alive"/>).</summary>
            public Animator animator;
            public PlaySource source;
            public string scope;
            public bool matched;
            public bool fromGraph;
            /// <summary>Which playable layer of this avatar's own build the rows were named
            /// from — "FX" — or empty when they were named from the controller in the window's
            /// field. See <see cref="Fitting"/>.</summary>
            public string built = string.Empty;
            /// <summary>What <see cref="Look"/> last found, asked once a frame rather than once
            /// a row: a real avatar has hundreds of rows and they are all this one avatar's.</summary>
            public bool alive = true;

            public bool IsAlive => animator != null && source != null && source.Alive;

            public void Look() { alive = IsAlive; }
        }

        readonly List<Reader> _readers = new List<Reader>();
        readonly List<Watched> _watched = new List<Watched>();
        int _lastFrame;
        float _lastTime, _zero;
        bool _started;

        public SignalTrace Trace { get; } = new SignalTrace();

        /// <summary>Whether a playable running THIS window's controller was found, which is
        /// what the state, via and weight rows need — without it a recording is the parameters
        /// and nothing else. Answers for the avatar that was aimed at; a copy that failed to
        /// match while the wearer matched costs that copy its state rows and nothing else. See
        /// <see cref="Matching"/> for what "running this controller" is decided by.</summary>
        public bool Matched => _watched.Count > 0 && _watched[0].matched;

        /// <summary>Whether the values come off a PlayableGraph rather than off the Animator
        /// component directly. False is the plain-playback case, and worth showing: it means no
        /// tool has hold of this avatar, so nothing VRChat-shaped is happening to it.</summary>
        public bool FromGraph => _watched.Count > 0 && _watched[0].fromGraph;

        /// <summary>Which playable layer of the avatar's own build the rows are named from, or
        /// empty when they are named from the controller in the window's field. Worth showing
        /// rather than hiding: the two can name the same state differently, and a reader
        /// comparing a recording against the asset in front of them has to know which they are
        /// looking at.</summary>
        public string Built =>
            _watched.Count > 0 ? _watched[0].built : string.Empty;

        /// <summary>How many avatars are being read at once — one, plus a scope each for the
        /// other people's copies that were found when the recording started.</summary>
        public int Sources => _watched.Count;

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
        ///
        /// Asked of the avatar that was AIMED at, not of all of them. A copy that goes away
        /// mid-recording holds its last value and the recording carries on (see
        /// <see cref="Sample"/>); the wearer going away is the recording being over, because
        /// there is nothing left that the run was about.
        /// </summary>
        public bool Alive => _watched.Count > 0 && _watched[0].IsAlive;

        PlayRecorder() { }

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

        /// <summary>What a set of rows can be named from: a controller, which playable of this
        /// avatar's is running it, and which of the avatar's built playable layers it came out
        /// of (empty for the one in the window's field).</summary>
        internal struct Fit
        {
            public AnimatorController controller;
            public string built;
            public int at;
        }

        /// <summary>
        /// Which controller this avatar's rows should be named from, and which playable is
        /// running it.
        ///
        /// <para>THE BUILT ONE FIRST.</para>
        /// On an avatar assembled by a build, the controller in the window's field is an INPUT
        /// to the thing running rather than the thing running: layers have been merged into it
        /// from elsewhere and parameters renamed, so its layer names do not match the graph and
        /// a recording of it loses every state, transition and weight row it could have had.
        /// The build's own output does match, because it is what is playing. Where a build was
        /// watched, its controllers are therefore tried first, and where none was — most
        /// projects, most of the time — this is exactly what it was before.
        ///
        /// <para>WHAT IS AND IS NOT PROVED BY A MATCH.</para>
        /// That the graph is running THIS controller, decided by the layer-name multiset like
        /// any other match. Not that the controller descends from the one in the field: nothing
        /// in a built controller says what it was built out of, and claiming otherwise would be
        /// inventing a provenance. What is actually being relied on is narrower and true — the
        /// registry says this avatar was built, and the avatar in front of the recorder is that
        /// avatar. The window says which controller the rows came from so that a reader is
        /// never left guessing which of the two they are reading. And picking a target still
        /// prefers an avatar running the field's controller outright, so this only ever decides
        /// cases the old rule left with nothing at all.
        /// </summary>
        internal static Fit Fitting(Animator animator, AnimatorController controller,
            List<AnimatorControllerPlayable> playables)
        {
            foreach (var candidate in BuildCapture.ControllersFor(animator))
            {
                int found = Matching(candidate, playables);
                if (found < 0) continue;
                return new Fit
                {
                    controller = candidate,
                    built = BuildCapture.KindOf(animator, candidate),
                    at = found,
                };
            }
            return new Fit
            {
                controller = controller,
                built = string.Empty,
                at = Matching(controller, playables),
            };
        }

        /// <summary>Whether this avatar's graph is running the window's controller — or the
        /// controller its own build made. Asked wherever "is this the avatar the window is
        /// about" used to be asked of the field's controller alone.</summary>
        public static bool Runs(AnimatorController controller, Animator animator) =>
            Fitting(animator, controller, PlayablesOn(animator)).at >= 0;

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

        /// <summary>A recorder aimed at this Animator and nobody else.</summary>
        public static PlayRecorder On(Animator animator, AnimatorController controller) =>
            On(animator, controller, null);

        /// <summary>
        /// A recorder aimed at this Animator, or null for no Animator at all — and at the other
        /// people's copies of it beside, if any were handed in.
        ///
        /// <para>WHY THE COPIES GO IN THE SAME RECORDING.</para>
        /// Under Av3Emulator the wearer and the copies are separate avatars running the same
        /// controller off values that crossed a wire, and the whole question anybody records
        /// them to ask is what the difference between them is. Two recordings taken separately
        /// could not answer it: they would have different frame numbers and different starting
        /// instants, and lining them up afterwards would be arithmetic nobody should have to
        /// trust. One trace, one clock, a scope each — which is the shape a simulated run
        /// already has, so the viewer, the findings and the saved clip all take it as they are.
        ///
        /// <para>WHO IS IN IT IS DECIDED ONCE.</para>
        /// The list is taken when the recording starts and never looked at again. A clone that
        /// appears later — somebody ticking Av3Emulator's box mid-session — is not in this
        /// recording and is caught by starting another one. Adding a scope partway through would
        /// mean a row with fewer samples than the trace has frames, and every reader of a trace
        /// is written on the promise that there is no such row.
        ///
        /// Each avatar gets the same three-way look, on its own: a graph playable whose layers
        /// are this controller's — or its build's, see <see cref="Fitting"/> — which is rows
        /// with states; any graph playable at all, which is parameters only; and no graph, which
        /// falls back to the Animator component. The unmatched case reads the LAST playable for
        /// the same reason the matched case picks the last — it is the newest, and there is
        /// nothing better to go on once the layer names have already failed to say which one is
        /// meant.
        /// </summary>
        public static PlayRecorder On(Animator animator, AnimatorController controller,
            List<Animator> clones)
        {
            if (animator == null) return null;
            var recorder = new PlayRecorder();
            recorder.Watch(animator, controller, Simulation.PlayScope);
            if (clones == null) return recorder;
            int at = 0;
            foreach (var clone in clones)
            {
                if (clone == null || clone == animator) continue;
                recorder.Watch(clone, controller, Simulation.PlayRemoteScopeAt(at));
                at++;
            }
            return recorder;
        }

        /// <summary>One more avatar to read, under a scope of its own.</summary>
        void Watch(Animator animator, AnimatorController controller, string scope)
        {
            var playables = PlayablesOn(animator);
            var fit = Fitting(animator, controller, playables);
            if (fit.at >= 0)
            {
                var source = PlaySource.Of(playables[fit.at]);
                var watched = Add(animator, source, scope, true, true);
                watched.built = fit.built;
                Declare(watched, new StateTables(fit.controller), Align(source, fit.controller));
                return;
            }
            for (int i = playables.Count - 1; i >= 0; i--)
            {
                if (!playables[i].IsValid()) continue;
                Declare(Add(animator, PlaySource.Of(playables[i]), scope, false, true),
                    null, null);
                return;
            }
            Declare(Add(animator, PlaySource.Of(animator), scope, false, false), null, null);
        }

        Watched Add(Animator animator, PlaySource source, string scope, bool matched,
            bool fromGraph)
        {
            var watched = new Watched
            {
                animator = animator,
                source = source,
                scope = scope,
                matched = matched,
                fromGraph = fromGraph,
            };
            _watched.Add(watched);
            return watched;
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
        ///
        /// An avatar running a controller its own BUILD made sits between the two: it is the
        /// one meant whenever there is nothing running the field's controller outright, and it
        /// is never preferred over one that is. The order matters on exactly one scene — the
        /// gimmick being edited is in it beside the assembled avatar it belongs to — and there
        /// the plain one is the one somebody pointed the window at.
        /// </summary>
        public static Animator Likeliest(AnimatorController controller)
        {
            var driven = PlayTools.Candidates(Driven());
            var running = new List<Animator>();
            var built = new List<Animator>();
            foreach (var animator in driven)
            {
                var playables = PlayablesOn(animator);
                if (Matching(controller, playables) >= 0) running.Add(animator);
                else if (Fitting(animator, controller, playables).at >= 0) built.Add(animator);
            }
            var pick = PlayTools.Preferred(running);
            if (pick != null) return pick;
            if (running.Count > 0) return running[0];
            pick = PlayTools.Preferred(built);
            if (pick != null) return pick;
            if (built.Count > 0) return built[0];
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
        void Declare(Watched watched, StateTables tables, int[] alignment)
        {
            var source = watched.source;
            string scope = watched.scope;
            for (int i = 0; i < source.ParameterCount; i++)
            {
                var parameter = source.ParameterAt(i);
                string name = parameter.name;
                var type = parameter.type;
                var signal = Trace.Declare(scope, name, TraceRecorder.KindOf(type));
                _readers.Add(new Reader
                {
                    signal = signal,
                    of = watched,
                    read = () => source.Read(name, type),
                });
            }
            if (alignment == null) return;

            for (int i = 0; i < alignment.Length; i++)
            {
                int layer = i, at = alignment[i];
                if (at < 0) continue;
                string name = tables.LayerName(at);
                var state = Trace.Declare(scope, name + "/state",
                    SignalKind.State, tables.StateLabels(at));
                _readers.Add(new Reader
                {
                    signal = state,
                    of = watched,
                    read = () => tables.StateAt(at, source.StateHash(layer)),
                });
                var moving = Trace.Declare(scope, name + "/transition", SignalKind.Bool);
                _readers.Add(new Reader
                {
                    signal = moving,
                    of = watched,
                    read = () => source.InTransition(layer) ? 1f : 0f,
                });
                var via = Trace.Declare(scope, name + "/via",
                    SignalKind.State, tables.TransitionLabels(at));
                _readers.Add(new Reader
                {
                    signal = via,
                    of = watched,
                    read = () => source.InTransition(layer)
                        ? tables.TransitionAt(at, source.TransitionHash(layer)) : -1,
                });
                var weight = Trace.Declare(scope, SimClient.WeightRow(name), SignalKind.Float);
                _readers.Add(new Reader
                {
                    signal = weight,
                    of = watched,
                    read = () => source.LayerWeight(layer),
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
            foreach (var watched in _watched) watched.Look();
            foreach (var reader in _readers)
                reader.signal.Push(reader.of.alive ? reader.read() : Held(reader.signal));
            return true;
        }

        /// <summary>
        /// What a row says on a frame its avatar was not there for: whatever it last said.
        ///
        /// Only ever one of the copies — the recording stops when the avatar it was aimed at
        /// goes (see <see cref="Alive"/>) — and a copy CAN go on its own, because Av3Emulator
        /// destroys a clone the moment somebody unticks it. Every signal in a trace has exactly
        /// as many samples as the trace has frames, and every reader of one is written on that,
        /// so the frame has to be filled with something; holding is the only filling that
        /// cannot be read as an event. What it costs is that a row goes flat rather than
        /// stopping, which is why <see cref="Sources"/> is on the panel: the reader is told how
        /// many avatars a recording was of, and a flat tail is then a leaving rather than a
        /// mystery.
        /// </summary>
        static float Held(SignalTrace.Signal signal) =>
            signal.Frames > 0 ? signal.At(signal.Frames - 1) : 0f;
    }
}

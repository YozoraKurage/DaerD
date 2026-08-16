using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// One client's copy of the avatar: a real Animator running the real controller, stepped by
    /// hand. Not a model of Mecanim — Mecanim itself, off a hidden GameObject, which is the
    /// only way an answer about a controller this size is worth anything.
    ///
    /// What IS modelled is the Parameter Driver. The SDK's own behaviour is not asked to run:
    /// it would be absent in a project without the SDK, it would run on every client alike, and
    /// nothing could be injected between it and the parameters. Reading its spec and applying
    /// it here costs a little fidelity and buys the three things this whole feature is for —
    /// working without the SDK, telling clients apart (<see cref="IsLocal"/> and localOnly),
    /// and being able to interfere.
    ///
    /// The SDK's driver applies its entries from OnStateEnter — read off the shipped type, and
    /// pinned by VrcSdkConformanceTests so it stays read rather than remembered. This applies
    /// them at the same event, one step later in the frame: Mecanim runs, and every state
    /// entered by it is then served.
    ///
    /// That ordering used to be written down here as a divergence, and it is not one. Measured
    /// (PlayModeProbeTests): a write made from a real OnStateEnter is not read by any
    /// transition inside the step that made it — not on another layer, in either direction, and
    /// not even on the layer that wrote it. Mecanim raises the callbacks after the frame's
    /// transitions have been decided, which is the same place this serves drivers from, so a
    /// chain of drivers costs a frame a link on a headset too. The layer index has nothing to
    /// do with it; layers are served in index order here, and it makes no difference to
    /// anything a driver writes.
    ///
    /// What does differ from a headset, stated so it can be argued with: a transition from a
    /// state to itself is only visible while it blends, so the drivers of one written with a
    /// duration of 0 do not fire — such a transition is finished inside the step it begins on
    /// and leaves no frame carrying evidence of the re-entry. A blended self transition taken
    /// again before its own blend ends counts as one entry rather than two, for the reason
    /// <see cref="_served"/> gives.
    /// </summary>
    sealed class SimClient : IDisposable
    {
        /// <summary>VRChat's own name for "this avatar belongs to the player reading it". Spelt
        /// here rather than borrowed so the module keeps its short list of dependencies; the
        /// core spells the same name in NetworkSyncBuilder.</summary>
        public const string IsLocalParameter = "IsLocal";

        readonly GameObject _host;
        readonly Animator _animator;
        readonly Dictionary<string, AnimatorControllerParameterType> _types =
            new Dictionary<string, AnimatorControllerParameterType>();
        /// <summary>What Mecanim's hashes are called, and what each state's drivers would do.
        /// Read off the controller rather than kept here, because a recording of the avatar
        /// actually running needs exactly the same tables off exactly the same asset — see
        /// <see cref="StateTables"/>.</summary>
        readonly StateTables _tables;
        readonly int[] _entered;

        /// <summary>Per layer: whether the blend it is in has already had its destination
        /// served. Needed because a state entered from ITSELF leaves <see cref="_entered"/>
        /// where it was, so the only evidence of that entry is the layer aiming at where it
        /// already is — and a blend is several frames of aiming, not one.
        ///
        /// Cleared when the layer is seen settled, which counts a self transition taken before
        /// the previous blend into that same state finished as one entry where a headset would
        /// fire two. Telling a restarted blend from a running one means watching the
        /// transition's normalizedTime for a step backwards, and a drive fired twice for one
        /// entry would be a run inventing a write nobody made — the safe side of that trade is
        /// the one where a press the run could not see is missed rather than doubled.</summary>
        readonly bool[] _served;

        readonly AnimatorController _copy;
        readonly List<string> _triggerNames = new List<string>();
        readonly HashSet<string> _pulsed = new HashSet<string>();
        SimRandom _random;

        /// <summary>Which client this is, in the trace and in a stimulus that names one.</summary>
        public string Scope { get; }

        /// <summary>Whether this copy belongs to the player reading it. Decides the IsLocal
        /// parameter and whether a localOnly driver runs.</summary>
        public bool IsLocal { get; }

        public int LayerCount => _tables.LayerCount;

        public SimClient(AnimatorController controller, string scope, bool isLocal, int seed)
        {
            Scope = scope ?? string.Empty;
            IsLocal = isLocal;
            _random = new SimRandom(seed);

            _host = new GameObject("DD DynamicAnalyze (" + Scope + ")");
            // Never saved and never in a build: a run leaves nothing behind even if it throws.
            _host.hideFlags = HideFlags.HideAndDontSave;
            _animator = _host.AddComponent<Animator>();
            _animator.applyRootMotion = false;
            // Nothing is on screen, and an Animator that only animates when it is would never
            // animate at all.
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            // IsLocal has to be true BEFORE the animator initializes, not after. Entry
            // transitions are resolved once, when the layer is first entered, so a controller
            // that splits the wearer from a remote with "Entry --(IsLocal)--> …" reads the
            // value at that instant and never again — write it afterwards and the wearer runs
            // down the remote's side of every such layer for the whole session. VRChat sets it
            // before the avatar's animator exists, so the faithful way to say it is as the
            // parameter's own default.
            //
            // Which is why each client gets its own copy of the controller: the default is on
            // the controller, the two clients need different ones, and editing the original
            // would both dirty somebody's asset and re-bind the animator already running it.
            // The copy shares the state machines it was made from, so it costs a header rather
            // than a controller.
            _copy = UnityEngine.Object.Instantiate(controller);
            if (_copy != null)
            {
                _copy.hideFlags = HideFlags.HideAndDontSave;
                var parameters = _copy.parameters;
                bool found = false;
                for (int i = 0; i < parameters.Length; i++)
                    if (parameters[i].name == IsLocalParameter)
                    {
                        parameters[i].defaultBool = IsLocal;
                        found = true;
                    }
                if (found) _copy.parameters = parameters;
                controller = _copy;
            }

            _animator.runtimeAnimatorController = controller;
            _animator.Rebind();

            foreach (var parameter in controller.parameters)
            {
                _types[parameter.name] = parameter.type;
                if (parameter.type == AnimatorControllerParameterType.Trigger)
                    _triggerNames.Add(parameter.name);
            }

            _tables = new StateTables(controller);
            _entered = new int[_tables.LayerCount];
            _served = new bool[_tables.LayerCount];

            if (_types.ContainsKey(IsLocalParameter))
                Write(IsLocalParameter, IsLocal ? 1f : 0f);
        }

        // ---- parameters -----------------------------------------------------

        public bool Has(string parameter) =>
            !string.IsNullOrEmpty(parameter) && _types.ContainsKey(parameter);

        public AnimatorControllerParameterType TypeOf(string parameter) =>
            _types.TryGetValue(parameter, out var type)
                ? type : AnimatorControllerParameterType.Float;

        /// <summary>Every parameter as a number, whatever it is underneath — a trace has one
        /// shape of sample, and a Bool is a square wave over the same axis as a Float.</summary>
        public float Read(string parameter)
        {
            if (!_types.TryGetValue(parameter, out var type)) return 0f;
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    return _animator.GetBool(parameter) ? 1f : 0f;
                case AnimatorControllerParameterType.Int:
                    return _animator.GetInteger(parameter);
                default:
                    return _animator.GetFloat(parameter);
            }
        }

        /// <summary>
        /// The value as a run should RECORD it, which for everything but a Trigger is the value.
        ///
        /// A trigger is not a value, it is a press: Mecanim clears it in the same frame a
        /// transition consumes it, so a trigger that did its job would be read back as zero on
        /// every frame including the one it fired on, and a run would show the transition
        /// happening for no reason anybody could see. What is recorded instead is whether it was
        /// standing when the frame began or is standing now — one frame of 1 per press, held for
        /// as long as nothing consumes it. A pulse, which is what a trigger is.
        /// </summary>
        public float Sample(string parameter) =>
            _pulsed.Contains(parameter) ? 1f : Read(parameter);

        public void Write(string parameter, float value)
        {
            if (!_types.TryGetValue(parameter, out var type)) return;
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                    _animator.SetBool(parameter, value != 0f);
                    break;
                case AnimatorControllerParameterType.Trigger:
                    if (value != 0f) _animator.SetTrigger(parameter);
                    else _animator.ResetTrigger(parameter);
                    break;
                case AnimatorControllerParameterType.Int:
                    _animator.SetInteger(parameter, Mathf.RoundToInt(value));
                    break;
                default:
                    _animator.SetFloat(parameter, value);
                    break;
            }
        }

        // ---- layers ---------------------------------------------------------

        public string[] StateLabels(int layer) =>
            _tables.StateLabels(layer);

        /// <summary>Which state the layer is in, as an index into <see cref="StateLabels"/>,
        /// or -1 for a layer that is nowhere this run can name.</summary>
        public int CurrentState(int layer)
        {
            if (layer < 0 || layer >= LayerCount) return -1;
            return _tables.StateAt(layer, _animator.GetCurrentAnimatorStateInfo(layer).fullPathHash);
        }

        public bool InTransition(int layer) =>
            layer >= 0 && layer < LayerCount && _animator.IsInTransition(layer);

        /// <summary>The transitions this layer can be seen in, in the order the layer authors
        /// them — the names <see cref="CurrentTransition"/> indexes into.</summary>
        public string[] TransitionLabels(int layer) =>
            _tables.TransitionLabels(layer);

        /// <summary>Which transition the layer is blending through, as an index into
        /// <see cref="TransitionLabels"/>. -1 for a settled layer, and -1 for a transition this
        /// run cannot tell apart from another — see <see cref="StateTables"/> for which
        /// those are.</summary>
        public int CurrentTransition(int layer)
        {
            if (layer < 0 || layer >= LayerCount) return -1;
            if (!_animator.IsInTransition(layer)) return -1;
            return _tables.TransitionAt(layer,
                _animator.GetAnimatorTransitionInfo(layer).fullPathHash);
        }

        // ---- layer weight ---------------------------------------------------

        /// <summary>The end of a layer's weight row's name.</summary>
        const string WeightSuffix = "/weight";

        /// <summary>
        /// What a layer's weight row is called. Built here, rather than spelt where it is
        /// declared, because it is the only row name that has to be recognised again: a value
        /// typed into that cell comes back as a name and nothing else, and the name has to find
        /// its way to the layer it was made from.
        /// </summary>
        public static string WeightRow(string layer) => layer + WeightSuffix;

        /// <summary>
        /// Which layer this row is the weight of, or -1 for a name that is not one.
        ///
        /// Every layer's row name is built again and compared, rather than the row being taken
        /// apart at its last '/': a layer may be called "Face/Eyes", and splitting would go
        /// looking for a layer called "Face". The first layer of a name wins, which is the one
        /// whose row <see cref="SignalTrace.Find"/> answers with.
        /// </summary>
        public int WeightRowLayer(string row)
        {
            if (row == null || !row.EndsWith(WeightSuffix, StringComparison.Ordinal)) return -1;
            for (int i = 0; i < _tables.LayerCount; i++)
                if (WeightRow(_tables.LayerName(i)) == row) return i;
            return -1;
        }

        /// <summary>
        /// How much of this layer is being mixed in — the one thing about a layer that changes
        /// what a run RECORDS, and the reason it is worth a row of its own. A layer's weight
        /// scales what its animation writes, animated parameters included: measured, an AAP
        /// writing 1 over a base that writes nothing records 0.5 at half weight, and over a
        /// base that writes 0.2 it records 0.6 — the layer blends its value in over what the
        /// layers below it left, and its parameter goes with it.
        ///
        /// Layer 0 is pinned, also measured: Mecanim answers 1 for it whatever anybody sets,
        /// and runs it in full even where the controller declares a default weight of 0. Its
        /// row is recorded like every other layer's — a flat 1 IS the answer to what the base
        /// layer's weight is, and a special case that hid one row of one layer would cost more
        /// than the row does.
        /// </summary>
        public float LayerWeight(int layer) =>
            layer >= 0 && layer < LayerCount ? _animator.GetLayerWeight(layer) : 0f;

        /// <summary>
        /// Sets it, clamped to 0..1. Mecanim does not clamp — measured: a weight of 1.5 reads
        /// back as 1.5 and mixes the layer in past the values it was blending towards — but
        /// nothing on a headset can ask for one, and a run answering "what if the weight were
        /// 1.5" would be answering a question that cannot be put to an avatar.
        ///
        /// Layer 0 is not refused, it is simply not taken: passing it on costs nothing and
        /// Mecanim ignores it. See <see cref="LayerWeight"/>.
        /// </summary>
        public void SetLayerWeight(int layer, float weight)
        {
            if (layer < 0 || layer >= LayerCount) return;
            _animator.SetLayerWeight(layer, Mathf.Clamp01(weight));
        }

        // ---- the frame ------------------------------------------------------

        /// <summary>
        /// One frame: Mecanim runs, then every state entered by it has its drivers applied. The
        /// order is what makes the drivers observable at all — Mecanim has to have moved before
        /// there is an entry to notice.
        ///
        /// For everything but one case an entry is a change of state. The exception is a
        /// transition from a state to itself, which really does re-enter it — measured in play
        /// mode and in the editor alike, for a state's own transition and for an any-state one
        /// with canTransitionToSelf, Mecanim calls OnStateEnter a second time — while leaving
        /// the hash exactly where it was. So the entry is caught as the layer beginning to
        /// blend towards where it already is, on the rising edge of that (see
        /// <see cref="_served"/>) rather than once per frame of the blend.
        ///
        /// What is still not caught is a self transition of duration 0: measured, a blend of no
        /// length is finished inside the step it began on, and no frame of the run has the
        /// layer aiming anywhere. Not a hole that can be plugged from here — the difference
        /// between such a transition having fired and nothing having happened is not a thing
        /// Mecanim keeps for anybody to read.
        /// </summary>
        public void Step(float deltaTime)
        {
            // Which triggers were standing when this frame began — taken before Mecanim runs,
            // because running it is what takes them down. See Sample: this is the frame the
            // press is visible on whether or not it was consumed on it. A trigger a driver
            // raises later in this frame is standing at the end of it and needs no note.
            _pulsed.Clear();
            foreach (var name in _triggerNames)
                if (_animator.GetBool(name)) _pulsed.Add(name);
            _animator.Update(deltaTime);
            for (int layer = 0; layer < _entered.Length; layer++)
            {
                bool blending = _animator.IsInTransition(layer);
                // The destination of a transition is entered when the transition starts, which
                // is what a headset's OnStateEnter reports too — waiting for it to finish would
                // put every driver a blend-length late.
                int hash = blending
                    ? _animator.GetNextAnimatorStateInfo(layer).fullPathHash
                    : _animator.GetCurrentAnimatorStateInfo(layer).fullPathHash;
                if (hash != _entered[layer])
                {
                    _entered[layer] = hash;
                    // A blend served here is remembered as served: the frames after it name the
                    // same destination, and reading those as entries would drive an ordinary
                    // transition once per frame it takes.
                    _served[layer] = blending;
                    Serve(hash);
                }
                else if (!blending) _served[layer] = false;
                else if (!_served[layer])
                {
                    // Aiming at the state it is already in: a self transition, and its entry.
                    _served[layer] = true;
                    Serve(hash);
                }
            }
        }

        void Serve(int hash)
        {
            if (!_tables.Drivers.TryGetValue(hash, out var specs)) return;
            foreach (var spec in specs) Apply(spec);
        }

        void Apply(ControllerIR.DriverSpec spec)
        {
            // The wearer's client is the only one a localOnly driver runs on. Modelling this is
            // half the reason the driver is simulated rather than left to the SDK.
            if (spec == null || (spec.localOnly && !IsLocal)) return;
            foreach (var entry in spec.entries)
            {
                if (entry == null || !Has(entry.name)) continue;
                switch (entry.kind)
                {
                    case 1:                                     // Add
                        Write(entry.name, Read(entry.name) + entry.value);
                        break;
                    case 2:                                     // Random
                        var type = TypeOf(entry.name);
                        float rolled = type == AnimatorControllerParameterType.Bool
                            || type == AnimatorControllerParameterType.Trigger
                            ? (_random.NextChance(entry.chance) ? 1f : 0f)
                            : _random.NextRange(entry.min, entry.max);
                        // The SDK's own option: never the value it just had. Bounded rather
                        // than a loop, because a Bool with a chance of 1 has nowhere else to go
                        // and would spin forever being asked for one.
                        for (int tries = 0; entry.preventRepeats && tries < 8
                             && Mathf.Approximately(rolled, Read(entry.name)); tries++)
                            rolled = type == AnimatorControllerParameterType.Bool
                                || type == AnimatorControllerParameterType.Trigger
                                ? (_random.NextChance(entry.chance) ? 1f : 0f)
                                : _random.NextRange(entry.min, entry.max);
                        Write(entry.name, rolled);
                        break;
                    case 3:                                     // Copy
                        if (!Has(entry.source)) break;
                        float value = Read(entry.source);
                        if (entry.convertRange) value = Remap(value, entry);
                        Write(entry.name, value);
                        break;
                    default:                                    // Set
                        Write(entry.name, entry.value);
                        break;
                }
            }
        }

        /// <summary>The driver's range conversion, straight: source range onto destination
        /// range. Not clamped — whether a headset clamps a source outside its declared range is
        /// not something this can promise, and inventing a clamp would hide the case rather
        /// than show it.</summary>
        static float Remap(float value, ControllerIR.DriverEntry entry)
        {
            float span = entry.sourceMax - entry.sourceMin;
            if (Mathf.Approximately(span, 0f)) return entry.destMin;
            float at = (value - entry.sourceMin) / span;
            return entry.destMin + at * (entry.destMax - entry.destMin);
        }

        public void Dispose()
        {
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
            if (_copy != null) UnityEngine.Object.DestroyImmediate(_copy);
        }
    }

    /// <summary>
    /// A controller's hashes, named. Mecanim answers "which state" and "which transition" with
    /// a number and nothing else, and the number is a hash of a path nobody wrote down — so
    /// anything that wants to say where a layer IS has to walk the controller first and build
    /// the table itself.
    ///
    /// Its own type because there are now two things that walk: <see cref="SimClient"/>, which
    /// steps a controller it made, and <see cref="PlayRecorder"/>, which watches one somebody
    /// else is running. Two copies of this walk would be two answers to "what is that state
    /// called", and the whole worth of a recorded row is that it is labelled the same way the
    /// simulated row beside it is.
    ///
    /// The drivers come with it. They are keyed by the same state hash, found on the same pass,
    /// and splitting them off would mean walking the controller twice to learn two things about
    /// one state. A recorder that has no use for them simply does not ask.
    /// </summary>
    sealed class StateTables
    {
        readonly string[] _layerNames;
        readonly List<string>[] _stateNames;
        readonly Dictionary<int, int>[] _stateOf;
        readonly List<string>[] _transitionNames;
        readonly Dictionary<int, int>[] _transitionOf;
        readonly Dictionary<int, List<ControllerIR.DriverSpec>> _drivers =
            new Dictionary<int, List<ControllerIR.DriverSpec>>();

        /// <summary>What each state's Parameter Drivers would do, by the hash Mecanim reports
        /// for that state. Empty for a controller that carries none.</summary>
        public Dictionary<int, List<ControllerIR.DriverSpec>> Drivers => _drivers;

        public int LayerCount => _stateNames.Length;

        public StateTables(AnimatorController controller)
        {
            var layers = controller != null ? controller.layers : new AnimatorControllerLayer[0];
            _layerNames = new string[layers.Length];
            _stateNames = new List<string>[layers.Length];
            _stateOf = new Dictionary<int, int>[layers.Length];
            _transitionNames = new List<string>[layers.Length];
            _transitionOf = new Dictionary<int, int>[layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                _layerNames[i] = layers[i].name ?? string.Empty;
                _stateNames[i] = new List<string>();
                _stateOf[i] = new Dictionary<int, int>();
                _transitionNames[i] = new List<string>();
                _transitionOf[i] = new Dictionary<int, int>();
                if (layers[i].stateMachine == null) continue;
                var naming = new Naming();
                Collect(layers[i].stateMachine, layers[i].name, string.Empty, i, naming);
                CollectTransitions(layers[i].stateMachine, naming);
                NameTransitions(i, naming);
            }
        }

        public string LayerName(int layer) =>
            layer >= 0 && layer < _layerNames.Length ? _layerNames[layer] : string.Empty;

        public string[] StateLabels(int layer) =>
            layer >= 0 && layer < _stateNames.Length
                ? _stateNames[layer].ToArray() : new string[0];

        public string[] TransitionLabels(int layer) =>
            layer >= 0 && layer < _transitionNames.Length
                ? _transitionNames[layer].ToArray() : new string[0];

        /// <summary>Which of <see cref="StateLabels"/> this hash is, or -1 for a state this
        /// table cannot name.</summary>
        public int StateAt(int layer, int hash) =>
            layer >= 0 && layer < _stateOf.Length
                && _stateOf[layer].TryGetValue(hash, out int at) ? at : -1;

        /// <summary>Which of <see cref="TransitionLabels"/> this hash is, or -1.</summary>
        public int TransitionAt(int layer, int hash) =>
            layer >= 0 && layer < _transitionOf.Length
                && _transitionOf[layer].TryGetValue(hash, out int at) ? at : -1;

        /// <summary>
        /// Walks a layer for the two things a run needs of it: what to call each state, and
        /// what its drivers would do. States are keyed by the hash Mecanim reports, which is of
        /// the FULL path — layer name, sub-machines, state — so two states of one name in
        /// different sub-machines stay apart.
        /// </summary>
        void Collect(AnimatorStateMachine machine, string fullPrefix, string label, int layer,
            Naming naming)
        {
            foreach (var child in machine.states)
            {
                if (child.state == null) continue;
                string path = fullPrefix + "." + child.state.name;
                int hash = Animator.StringToHash(path);
                naming.paths[child.state] = path;
                naming.labels[child.state] = label + child.state.name;
                if (!_stateOf[layer].ContainsKey(hash))
                {
                    _stateOf[layer][hash] = _stateNames[layer].Count;
                    // Labelled without the layer's own name, which every row of that layer
                    // would otherwise repeat.
                    _stateNames[layer].Add(label + child.state.name);
                }
                foreach (var behaviour in child.state.behaviours)
                {
                    if (!VrcParameterDriver.Is(behaviour)) continue;
                    if (!_drivers.TryGetValue(hash, out var specs))
                        _drivers[hash] = specs = new List<ControllerIR.DriverSpec>();
                    specs.Add(VrcParameterDriver.ReadSpec(behaviour));
                }
            }
            foreach (var child in machine.stateMachines)
            {
                if (child.stateMachine == null) continue;
                naming.machines[child.stateMachine] = label + child.stateMachine.name;
                Collect(child.stateMachine, fullPrefix + "." + child.stateMachine.name,
                    label + child.stateMachine.name + ".", layer, naming);
            }
        }

        // ---- which transition ------------------------------------------------

        /// <summary>What Mecanim puts between the two ends of a transition in the path it
        /// hashes. ASCII, and not the arrow a label shows.</summary>
        const string Joint = " -> ";

        /// <summary>The source of an any-state transition, in Mecanim's path for it. It is
        /// spelt "Entry" rather than anything with "any" in it — measured, not guessed.</summary>
        const string AnyStatePath = "Entry";

        /// <summary>Both ways out of a machine, which Mecanim spells identically: an explicit
        /// Exit, and a destination that is a sub-machine rather than a state.</summary>
        const string ExitPath = "Exit";

        const string AnyStateLabel = "Any State";

        /// <summary>Between the two ends of a transition on a ROW. The typographic arrow, not
        /// the ASCII one the hashed path uses, because a row is read rather than parsed.</summary>
        const string Arrow = " → ";

        /// <summary>What a layer's nodes are called, filled while walking it. Kept for the
        /// length of the walk only: a transition's destination can be anywhere in the layer,
        /// including a sub-machine the walk has not reached yet, so no transition can be named
        /// until every node has been.</summary>
        sealed class Naming
        {
            public readonly Dictionary<AnimatorState, string> paths =
                new Dictionary<AnimatorState, string>();
            public readonly Dictionary<AnimatorState, string> labels =
                new Dictionary<AnimatorState, string>();
            public readonly Dictionary<AnimatorStateMachine, string> machines =
                new Dictionary<AnimatorStateMachine, string>();

            /// <summary>Every transition found, in the order the layer authors them: the hash
            /// Mecanim would report for it, and the label a row would show.</summary>
            public readonly List<KeyValuePair<int, string>> transitions =
                new List<KeyValuePair<int, string>>();
        }

        /// <summary>
        /// Which transitions a layer HAS, so that a run can say which one is firing rather than
        /// only that something is.
        ///
        /// Mecanim reports a transition as a hash and nothing else, and what it hashes was
        /// measured rather than assumed — the source's full path, " -> ", the destination's
        /// full path, out of the same full paths states are keyed by, and pinned by the tests
        /// that spell those paths out. Two ends of the graph have no path of their own and
        /// travel as bare words: an any-state transition's source is "Entry", and a destination
        /// that is an Exit is "Exit".
        ///
        /// What this deliberately cannot name — the row falls back to "—" rather than guess:
        /// two transitions between the same pair of states hash the same, so a run says which
        /// pair is blending and not which of the two conditions opened it; a destination that
        /// is a sub-machine is spelt exactly like an Exit from the same state, so a state that
        /// has both has two different transitions under one hash and this names neither; and an
        /// Entry transition is not a blend at all, so nothing is ever in one to report.
        ///
        /// The paths are built from the layer's name, the way the state table's are — Mecanim
        /// itself builds them from the layer's root MACHINE's name, which Unity keeps equal to
        /// the layer's. A controller where the two have been driven apart is one where the
        /// state row already names nothing; this row is no worse off, and no better.
        /// </summary>
        void CollectTransitions(AnimatorStateMachine machine, Naming naming)
        {
            foreach (var child in machine.states)
            {
                if (child.state == null || !naming.paths.TryGetValue(child.state, out var path))
                    continue;
                foreach (var transition in child.state.transitions)
                    Note(naming, path, naming.labels[child.state], transition);
            }
            // An any-state transition of a sub-machine is labelled like any other: which
            // machine it was written in is not something a run can see it by, and giving it a
            // different label would only put two labels under one hash and lose both.
            foreach (var transition in machine.anyStateTransitions)
                Note(naming, AnyStatePath, AnyStateLabel, transition);
            foreach (var child in machine.stateMachines)
                if (child.stateMachine != null) CollectTransitions(child.stateMachine, naming);
        }

        void Note(Naming naming, string sourcePath, string sourceLabel,
            AnimatorStateTransition transition)
        {
            if (transition == null) return;
            string path, label;
            if (transition.destinationState != null)
            {
                if (!naming.paths.TryGetValue(transition.destinationState, out path)) return;
                label = naming.labels[transition.destinationState];
            }
            else if (transition.destinationStateMachine != null)
            {
                path = ExitPath;
                label = naming.machines.TryGetValue(transition.destinationStateMachine,
                    out var machine) ? machine : transition.destinationStateMachine.name;
            }
            else if (transition.isExit)
            {
                path = ExitPath;
                label = ExitPath;
            }
            else
            {
                // A transition with no destination at all: nothing to blend to, nothing to name.
                return;
            }
            naming.transitions.Add(new KeyValuePair<int, string>(
                Animator.StringToHash(sourcePath + Joint + path), sourceLabel + Arrow + label));
        }

        /// <summary>
        /// The walk's findings as a row's labels. A hash that two transitions of DIFFERENT
        /// names share is dropped rather than resolved: showing one of the two names would be
        /// a run asserting something it does not know, and an unnamed frame is the honest
        /// answer. Transitions of the same name under one hash keep it — there is nothing to
        /// choose between them.
        /// </summary>
        void NameTransitions(int layer, Naming naming)
        {
            var agreed = new Dictionary<int, string>();
            foreach (var found in naming.transitions)
            {
                if (!agreed.TryGetValue(found.Key, out var label)) agreed[found.Key] = found.Value;
                else if (label != null && label != found.Value) agreed[found.Key] = null;
            }
            foreach (var found in naming.transitions)
            {
                if (agreed[found.Key] == null || _transitionOf[layer].ContainsKey(found.Key))
                    continue;
                _transitionOf[layer][found.Key] = _transitionNames[layer].Count;
                _transitionNames[layer].Add(found.Value);
            }
        }
    }
}

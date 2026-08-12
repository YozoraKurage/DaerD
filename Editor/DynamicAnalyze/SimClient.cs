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
    /// Where it differs from a headset, stated so it can be argued with:
    /// the drivers of a state are applied just AFTER the frame that entered it, not inside it,
    /// so their effect on a transition shows up one frame later than on a headset; a transition
    /// from a state to itself enters nothing this can see, so its drivers do not fire; and
    /// layers are served in index order, which VRChat does not promise.
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
        readonly List<string>[] _stateNames;
        readonly Dictionary<int, int>[] _stateOf;
        readonly Dictionary<int, List<ControllerIR.DriverSpec>> _drivers =
            new Dictionary<int, List<ControllerIR.DriverSpec>>();
        readonly int[] _entered;
        readonly AnimatorController _copy;
        SimRandom _random;

        /// <summary>Which client this is, in the trace and in a stimulus that names one.</summary>
        public string Scope { get; }

        /// <summary>Whether this copy belongs to the player reading it. Decides the IsLocal
        /// parameter and whether a localOnly driver runs.</summary>
        public bool IsLocal { get; }

        public int LayerCount => _stateNames.Length;

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
                _types[parameter.name] = parameter.type;

            var layers = controller.layers;
            _stateNames = new List<string>[layers.Length];
            _stateOf = new Dictionary<int, int>[layers.Length];
            _entered = new int[layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                _stateNames[i] = new List<string>();
                _stateOf[i] = new Dictionary<int, int>();
                _entered[i] = 0;
                if (layers[i].stateMachine != null)
                    Collect(layers[i].stateMachine, layers[i].name, string.Empty, i);
            }

            if (_types.ContainsKey(IsLocalParameter))
                Write(IsLocalParameter, IsLocal ? 1f : 0f);
        }

        /// <summary>
        /// Walks a layer for the two things a run needs of it: what to call each state, and
        /// what its drivers would do. States are keyed by the hash Mecanim reports, which is of
        /// the FULL path — layer name, sub-machines, state — so two states of one name in
        /// different sub-machines stay apart.
        /// </summary>
        void Collect(AnimatorStateMachine machine, string fullPrefix, string label, int layer)
        {
            foreach (var child in machine.states)
            {
                if (child.state == null) continue;
                string path = fullPrefix + "." + child.state.name;
                int hash = Animator.StringToHash(path);
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
                if (child.stateMachine != null)
                    Collect(child.stateMachine, fullPrefix + "." + child.stateMachine.name,
                        label + child.stateMachine.name + ".", layer);
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
            layer >= 0 && layer < _stateNames.Length
                ? _stateNames[layer].ToArray() : new string[0];

        /// <summary>Which state the layer is in, as an index into <see cref="StateLabels"/>,
        /// or -1 for a layer that is nowhere this run can name.</summary>
        public int CurrentState(int layer)
        {
            if (layer < 0 || layer >= _stateOf.Length) return -1;
            int hash = _animator.GetCurrentAnimatorStateInfo(layer).fullPathHash;
            return _stateOf[layer].TryGetValue(hash, out int at) ? at : -1;
        }

        public bool InTransition(int layer) =>
            layer >= 0 && layer < _stateOf.Length && _animator.IsInTransition(layer);

        // ---- the frame ------------------------------------------------------

        /// <summary>
        /// One frame: Mecanim runs, then every state entered by it has its drivers applied. The
        /// order is what makes the drivers observable at all — Mecanim has to have moved before
        /// there is an entry to notice.
        /// </summary>
        public void Step(float deltaTime)
        {
            _animator.Update(deltaTime);
            for (int layer = 0; layer < _entered.Length; layer++)
            {
                // The destination of a transition is entered when the transition starts, which
                // is what a headset's OnStateEnter reports too — waiting for it to finish would
                // put every driver a blend-length late.
                int hash = _animator.IsInTransition(layer)
                    ? _animator.GetNextAnimatorStateInfo(layer).fullPathHash
                    : _animator.GetCurrentAnimatorStateInfo(layer).fullPathHash;
                if (hash == _entered[layer]) continue;
                _entered[layer] = hash;
                if (!_drivers.TryGetValue(hash, out var specs)) continue;
                foreach (var spec in specs) Apply(spec);
            }
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
                        Write(entry.name,
                            type == AnimatorControllerParameterType.Bool
                                || type == AnimatorControllerParameterType.Trigger
                                ? (_random.NextChance(entry.chance) ? 1f : 0f)
                                : _random.NextRange(entry.min, entry.max));
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
}

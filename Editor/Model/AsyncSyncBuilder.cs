using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Parameter compression via time-multiplexing ("async sync"): instead of syncing
    /// N parameters at 8 bits each, only a small fixed set of expression parameters is
    /// synced — an index plus value channels — and the parameters take turns. A local-only
    /// state cycle copies the parameters of slot i into the channels and sets the index
    /// (Parameter Driver, localOnly); VRChat pushes the synced set to remotes on its ~0.3 s
    /// cadence, where an Any-State decoder (IsLocal == false, index == i) copies the
    /// channels back. The targets themselves stay unsynced.
    ///
    /// A slot carries one Bool or Int target, or up to <see cref="Request.floatChannels"/>
    /// Float targets at once — more Float channels mean fewer slots and a shorter cycle,
    /// at 8 synced bits per extra channel. Targets marked priority are interleaved into
    /// the cycle every other step, so they refresh fast while the rest share the steps
    /// in between.
    ///
    /// Caveats inherent to the technique: values arrive with up to a full pass of latency,
    /// and a remote joining mid-cycle fills in over one pass. The index and the values do
    /// NOT need to be paired defensively — VRChat delivers a sync of the expression
    /// parameters together, so the index a remote reads belongs to the values it reads with
    /// it. The one documented exception is a parameter a puppet control is dragging, which
    /// streams on its own; keep such parameters out of the multiplexed set.
    ///
    /// Synced Floats are 8-bit fixed point over -1..1 on the remote side (about 0.008 per
    /// step) while the local value keeps full precision, so a multiplexed Float reads back
    /// slightly quantized for everyone but the wearer.
    /// See https://www.proustite.com/articles/float-expression-parameters/
    /// </summary>
    static class AsyncSyncBuilder
    {
        public enum IndexEncoding
        {
            /// <summary>One synced Int holds the slot index (8 bits, up to 255 slots).</summary>
            Int,
            /// <summary>ceil(log2 N) synced Bools hold the index bits, LSB-first.</summary>
            Bool,
            /// <summary>Pick whichever costs fewer synced bits for the slot count, preferring
            /// the single Int when they tie. See <see cref="ResolveEncoding"/>.</summary>
            Auto,
        }

        public class Request
        {
            public AnimatorController controller;
            /// <summary>Existing Float / Int / Bool parameters to multiplex, in slot order.</summary>
            public List<string> targets = new List<string>();
            /// <summary>Prefix for the generated synced parameters ("/Index", "/Float", …).</summary>
            public string baseName = "Async";
            public IndexEncoding encoding = IndexEncoding.Auto;
            /// <summary>Dwell per slot in seconds. VRChat syncs roughly every 0.3 s — shorter
            /// steps risk remotes skipping slots.</summary>
            public float stepSeconds = 0.3f;
            /// <summary>Synced Float channels (1–8). Each slot carries up to this many Float
            /// targets at once: fewer slots and a shorter cycle, 8 more synced bits each.</summary>
            public int floatChannels = 1;
            /// <summary>Targets refreshed every other step; the rest share the steps between.
            /// Names not present in <see cref="targets"/> are ignored, so a stale saved setup
            /// doesn't block regeneration.</summary>
            public List<string> priorities = new List<string>();
            /// <summary>Layer to create; defaults to the base name.</summary>
            public string layerName;
            /// <summary>Existing async-sync layer to REGENERATE in place (its states are
            /// rebuilt), or -1 to create a new layer.</summary>
            public int layerIndex = -1;
            /// <summary>When set, the generated synced parameters are added to this store.</summary>
            public ParameterStore store;
            public bool addToStore = true;
            /// <summary>Fill the generated states with an Empty clip. Motion-less states work,
            /// but the analyzer (rightly) flags every one of them and this layer creates a lot
            /// of them. Ignored when no clip resolves, or when it has zero length — normalized
            /// exit times need a length to divide by.</summary>
            public bool assignEmptyClip = true;
            /// <summary>Clip used by <see cref="assignEmptyClip"/>; defaults to the controller's
            /// designated Empty clip when left null.</summary>
            public AnimationClip emptyClip;
            /// <summary>Tests only: build the structure without VRCAvatarParameterDriver.</summary>
            internal bool skipDrivers;
        }

        /// <summary>One step's payload: a single Bool / Int target, or a batch of Floats
        /// sent through the Float channels together.</summary>
        public class Slot
        {
            public readonly List<string> targets = new List<string>();
            public bool priority;
        }

        public static string IndexParameter(string baseName) => baseName + "/Index";

        public static string BitParameter(string baseName, int bit) =>
            baseName + "/Index/b" + bit;

        public static string ChannelParameter(string baseName, AnimatorControllerParameterType type) =>
            baseName + "/" + type;

        /// <summary>Channel 0 keeps the legacy "/Float" name; extras are "/Float2", "/Float3"…</summary>
        public static string FloatChannelParameter(string baseName, int channel) =>
            channel == 0
                ? baseName + "/" + AnimatorControllerParameterType.Float
                : baseName + "/" + AnimatorControllerParameterType.Float + (channel + 1);

        // ---- slots and schedule ----------------------------------------------

        /// <summary>
        /// Groups the targets into slots, in listed order: Bool / Int targets one per slot,
        /// Float targets batched up to <see cref="Request.floatChannels"/> per slot. Priority
        /// and regular Floats never share a batch — a batch is revisited as a whole or not
        /// at all.
        /// </summary>
        public static List<Slot> BuildSlots(Request r)
        {
            var slots = new List<Slot>();
            if (r?.targets == null || r.controller == null) return slots;

            int channels = Mathf.Clamp(r.floatChannels, 1, 8);
            var priorities = new HashSet<string>(r.priorities ?? new List<string>());
            Slot openFloats = null, openPriorityFloats = null;

            foreach (var name in r.targets)
            {
                var parameter = DbtBuilder.FindParameter(r.controller, name);
                if (parameter == null) continue;
                bool priority = priorities.Contains(name);

                if (parameter.type != AnimatorControllerParameterType.Float)
                {
                    var slot = new Slot { priority = priority };
                    slot.targets.Add(name);
                    slots.Add(slot);
                    continue;
                }

                var open = priority ? openPriorityFloats : openFloats;
                if (open == null || open.targets.Count >= channels)
                {
                    open = new Slot { priority = priority };
                    slots.Add(open);
                    if (priority) openPriorityFloats = open;
                    else openFloats = open;
                }
                open.targets.Add(name);
            }
            return slots;
        }

        /// <summary>
        /// The order the cycle visits the slots, as indices into <see cref="BuildSlots"/>.
        /// Without priorities (or with nothing BUT priorities) it is the plain round-robin.
        /// Otherwise priority and regular slots alternate — even steps cycle the priority
        /// set, odd steps cycle the rest — until both sets are fully covered, so a single
        /// priority slot refreshes every 2 × (number of priority slots) steps.
        /// </summary>
        public static List<int> BuildSchedule(List<Slot> slots)
        {
            var schedule = new List<int>();
            if (slots == null) return schedule;

            var priority = new List<int>();
            var regular = new List<int>();
            for (int i = 0; i < slots.Count; i++)
                (slots[i].priority ? priority : regular).Add(i);

            if (priority.Count == 0 || regular.Count == 0)
            {
                for (int i = 0; i < slots.Count; i++) schedule.Add(i);
                return schedule;
            }

            // Alternating two disjoint sets never puts one slot in adjacent steps (including
            // the wrap) — which the decoder needs: its Any-State transitions have
            // canTransitionToSelf off, so a repeated index would not re-trigger.
            int pairs = Mathf.Max(priority.Count, regular.Count);
            for (int k = 0; k < pairs; k++)
            {
                schedule.Add(priority[k % priority.Count]);
                schedule.Add(regular[k % regular.Count]);
            }
            return schedule;
        }

        // ---- resolution and cost ---------------------------------------------

        /// <summary>
        /// The encoding a request actually builds with. Auto weighs the two: the Bool index
        /// costs ceil(log2 N) bits against the Int's flat 8, so it wins for anything under 256
        /// slots. A tie goes to the Int purely on tidiness — one parameter in the store and one
        /// condition per decoder route instead of eight. Both are equally safe on the wire:
        /// expression parameters arrive together, so the index bits can't be read half-updated.
        /// </summary>
        public static IndexEncoding ResolveEncoding(Request r)
        {
            if (r == null || r.encoding != IndexEncoding.Auto) return r?.encoding ?? IndexEncoding.Int;
            int slots = Mathf.Max(2, BuildSlots(r).Count);
            return NetworkSyncBuilder.BitsRequired(slots) < 8 ? IndexEncoding.Bool : IndexEncoding.Int;
        }

        /// <summary>
        /// The clip the generated states will play, or null when they stay motion-less. A
        /// zero-length clip is refused: exit times are normalized to the motion, so there would
        /// be nothing to divide the step interval by.
        /// </summary>
        public static AnimationClip ResolveEmptyClip(Request r)
        {
            if (r == null || !r.assignEmptyClip) return null;
            var clip = r.emptyClip != null ? r.emptyClip : GraphFrameData.GetEmptyClip(r.controller);
            return clip != null && clip.length > 0f ? clip : null;
        }

        /// <summary>Float channels the request actually uses — capped by how many Floats any
        /// one slot really carries, so unused channels are neither created nor billed.</summary>
        public static int FloatChannelsUsed(Request r)
        {
            int used = 0;
            foreach (var slot in BuildSlots(r))
            {
                if (slot.targets.Count == 0) continue;
                var parameter = DbtBuilder.FindParameter(r.controller, slot.targets[0]);
                if (parameter != null && parameter.type == AnimatorControllerParameterType.Float)
                    used = Mathf.Max(used, slot.targets.Count);
            }
            return used;
        }

        /// <summary>Seconds for one full pass of the schedule — the worst-case age of a
        /// regular value.</summary>
        public static float CycleSeconds(Request r) =>
            r == null ? 0f : BuildSchedule(BuildSlots(r)).Count * r.stepSeconds;

        /// <summary>Seconds between two visits of one priority slot, or 0 when the schedule
        /// has no priority/regular split.</summary>
        public static float PriorityIntervalSeconds(Request r)
        {
            if (r == null) return 0f;
            int priority = 0, regular = 0;
            foreach (var slot in BuildSlots(r))
            {
                if (slot.priority) priority++;
                else regular++;
            }
            if (priority == 0 || regular == 0) return 0f;
            return 2f * priority * r.stepSeconds;
        }

        /// <summary>
        /// Slots that can still be added without spending another synced bit. The Bool index
        /// only grows at powers of two, so the tail of each range is free; an Int index has room
        /// for 255 slots from the start.
        /// </summary>
        public static int FreeSlots(Request r)
        {
            if (r?.targets == null || ResolveEncoding(r) != IndexEncoding.Bool) return 0;
            int count = Mathf.Max(2, BuildSlots(r).Count);
            int capacity = 1 << NetworkSyncBuilder.BitsRequired(count);
            return Mathf.Max(0, capacity - count);
        }

        /// <summary>Synced bits the generated parameters will occupy.</summary>
        public static int CompressedBits(Request r)
        {
            int bits = ResolveEncoding(r) == IndexEncoding.Int
                ? 8
                : NetworkSyncBuilder.BitsRequired(Mathf.Max(2, BuildSlots(r).Count));
            foreach (var type in ChannelTypes(r))
                bits += type == AnimatorControllerParameterType.Bool ? 1
                    : type == AnimatorControllerParameterType.Float ? FloatChannelsUsed(r) * 8
                    : 8;
            return bits;
        }

        /// <summary>Synced bits the targets would occupy if each synced directly.</summary>
        public static int DirectBits(Request r)
        {
            int bits = 0;
            foreach (var name in r.targets)
            {
                var parameter = DbtBuilder.FindParameter(r.controller, name);
                if (parameter == null) continue;
                bits += parameter.type == AnimatorControllerParameterType.Bool ? 1 : 8;
            }
            return bits;
        }

        static List<AnimatorControllerParameterType> ChannelTypes(Request r)
        {
            var types = new List<AnimatorControllerParameterType>();
            foreach (var name in r.targets)
            {
                var parameter = DbtBuilder.FindParameter(r.controller, name);
                if (parameter != null && !types.Contains(parameter.type))
                    types.Add(parameter.type);
            }
            return types;
        }

        // ---- reserved names ----------------------------------------------------

        /// <summary>
        /// True for parameters that belong to the sync machinery rather than to the avatar:
        /// IsLocal, and anything under the "base/" namespace of a saved async-sync setup.
        /// Multiplexing one of these would feed the cycle back into itself.
        /// </summary>
        public static bool IsReservedName(AnimatorController controller, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name == NetworkSyncBuilder.IsLocalParameter) return true;
            foreach (var config in GraphFrameData.GetAsyncSyncs(controller))
                if (!string.IsNullOrEmpty(config.baseName)
                    && name.StartsWith(config.baseName + "/"))
                    return true;
            return false;
        }

        /// <summary>
        /// Targets a puppet control (radial / two-axis / four-axis) in the controller's
        /// expressions menu drives. Those are the parameters that don't fit the technique: a
        /// puppet drag is a continuous stream, and a slot only gets one sample per pass.
        /// </summary>
        public static List<string> PuppetDrivenTargets(Request r)
        {
            var driven = new List<string>();
            if (r?.targets == null || r.controller == null) return driven;

            // Only the menu explicitly associated with this controller — this runs on every
            // repaint of the wizard, and DaerD never goes looking through the scene on its own.
            var menu = GraphFrameData.GetExpressionsMenu(r.controller);
            if (menu == null) return driven;

            var puppeted = new HashSet<string>();
            foreach (var control in VrcMenuAccess.Read(menu))
            {
                if (control.type != VrcMenuAccess.ControlType.RadialPuppet
                    && control.type != VrcMenuAccess.ControlType.TwoAxisPuppet
                    && control.type != VrcMenuAccess.ControlType.FourAxisPuppet)
                    continue;
                foreach (var name in control.subParameters)
                    if (!string.IsNullOrEmpty(name)) puppeted.Add(name);
            }
            foreach (var name in r.targets)
                if (puppeted.Contains(name)) driven.Add("'" + name + "'");
            return driven;
        }

        // ---- validation ----------------------------------------------------------

        /// <summary>Human-readable reason the request can't run, or null when it can.</summary>
        public static string Validate(Request r)
        {
            var controller = r.controller;
            if (controller == null) return L.Tr("No controller.");
            if (string.IsNullOrEmpty(r.baseName))
                return L.Tr("The sync parameter needs a name.");
            if (r.targets == null || r.targets.Count < 2)
                return L.Tr("Pick at least two parameters to multiplex.");
            if (!(r.stepSeconds > 0f))
                return L.Tr("The step interval must be greater than zero.");
            if (r.floatChannels < 1 || r.floatChannels > 8)
                return L.Tr("Float channels must be between 1 and 8.");
            if (r.layerIndex >= controller.layers.Length)
                return L.Tr("The target layer no longer exists.");

            var seen = new HashSet<string>();
            foreach (var name in r.targets)
            {
                if (!seen.Add(name))
                    return L.Tr("Parameter '{0}' is listed more than once.", name);
                var parameter = DbtBuilder.FindParameter(controller, name);
                if (parameter == null)
                    return L.Tr("Parameter '{0}' does not exist.", name);
                if (parameter.type == AnimatorControllerParameterType.Trigger)
                    return L.Tr("Triggers can't be multiplexed ('{0}').", name);
                // The machinery must not multiplex itself: IsLocal, another setup's generated
                // parameters, or anything under this request's own namespace.
                if (IsReservedName(controller, name) || name.StartsWith(r.baseName + "/"))
                    return L.Tr("'{0}' belongs to the sync machinery and can't be multiplexed.", name);
            }

            if (BuildSlots(r).Count > 255)
                return L.Tr("Int encoding supports up to 255 states.");

            var isLocal = DbtBuilder.FindParameter(controller, NetworkSyncBuilder.IsLocalParameter);
            if (isLocal != null && isLocal.type != AnimatorControllerParameterType.Bool)
                return L.Tr("Parameter '{0}' exists but is not a Bool.", NetworkSyncBuilder.IsLocalParameter);

            foreach (var (name, type) in GeneratedParameters(r))
            {
                if (seen.Contains(name))
                    return L.Tr("Generated parameter '{0}' collides with a target.", name);
                var existing = DbtBuilder.FindParameter(controller, name);
                if (existing != null && existing.type != type)
                    return L.Tr("Parameter '{0}' exists with a different type.", name);
            }

            if (!r.skipDrivers && !VrcParameterDriver.SdkAvailable)
                return L.Tr("VRChat SDK not found — the Parameter Driver behaviour is required.");
            return null;
        }

        /// <summary>Non-blocking observations shown in the wizard before running.</summary>
        public static List<string> Warnings(Request r)
        {
            var warnings = new List<string>();
            if (r.controller == null || r.targets == null) return warnings;

            // The whole point is spending fewer synced bits; at small counts the index and the
            // channels can cost as much as the parameters they replace.
            if (r.targets.Count >= 2)
            {
                int compressed = CompressedBits(r);
                int direct = DirectBits(r);
                if (compressed >= direct)
                    warnings.Add(L.Tr(
                        "At {0} parameters this saves nothing: {1} synced bit(s) compressed against {2} direct. Multiplex more parameters, or leave them synced directly.",
                        r.targets.Count, compressed, direct));
            }

            float cycle = CycleSeconds(r);
            if (cycle > 3f)
                warnings.Add(L.Tr(
                    "One full pass takes {0:0.#} s ({1} steps × {2:0.##} s), so a remote can be that far behind on any one value.",
                    cycle, BuildSchedule(BuildSlots(r)).Count, r.stepSeconds));

            // Priority on everything degenerates to the plain cycle — the flag only means
            // something relative to parameters that go without it.
            if (r.priorities != null && r.priorities.Count > 0)
            {
                bool anyRegular = false;
                foreach (var name in r.targets)
                    if (!r.priorities.Contains(name)) { anyRegular = true; break; }
                if (!anyRegular)
                    warnings.Add(L.Tr("Every parameter is marked priority, which is the same as no priority at all — the plain cycle is generated."));
            }

            // Expression parameters reach remotes together, so a multiplexed value is only ever
            // as stale as the cycle. Puppet controls are the documented exception: dragging one
            // streams its parameter continuously, and the round-robin samples it once per pass —
            // remotes would see the drag as a staircase.
            var puppeted = PuppetDrivenTargets(r);
            if (puppeted.Count > 0)
                warnings.Add(L.Tr(
                    "Puppet controls drive {0}. A puppet drag streams continuously, so multiplexing it makes remotes see the drag one step per pass — those are better left synced directly.",
                    string.Join(", ", puppeted)));

            if (r.assignEmptyClip)
            {
                var clip = r.emptyClip != null ? r.emptyClip : GraphFrameData.GetEmptyClip(r.controller);
                if (clip == null)
                    warnings.Add(L.Tr("No Empty clip is set for this controller, so the generated states stay motion-less — the analyzer flags every one of them. Set one in the controller overview to have them filled in."));
                else if (clip.length <= 0f)
                    warnings.Add(L.Tr("The Empty clip '{0}' has zero length, so it can't carry the step timing; the generated states stay motion-less.", clip.name));
            }

            if (r.layerIndex >= 0 && r.layerIndex < r.controller.layers.Length
                && r.controller.layers[r.layerIndex].defaultWeight <= 0f && r.layerIndex != 0)
                warnings.Add(L.Tr("The target layer's weight is 0 — the send cycle won't run until it is raised."));

            if (r.stepSeconds < 0.3f)
                warnings.Add(L.Tr("Steps shorter than VRChat's ~0.3 s sync cadence risk remotes skipping slots."));
            foreach (var type in ChannelTypes(r))
                if (type == AnimatorControllerParameterType.Float)
                {
                    warnings.Add(L.Tr("Remote Floats are 8-bit fixed point over -1..1 (about 0.008 per step), so multiplexed Floats read back quantized for everyone but the wearer, and values outside -1..1 don't survive the trip at all."));
                    break;
                }
            if (r.store != null && r.addToStore)
                foreach (var name in r.targets)
                {
                    var entry = r.store.Find(name);
                    if (entry != null && entry.synced)
                    {
                        warnings.Add(L.Tr("Some targets are still synced in the parameter store — unsync them there to actually save bits."));
                        break;
                    }
                }
            return warnings;
        }

        /// <summary>The synced parameters this request will create (name, animator type).</summary>
        public static List<(string name, AnimatorControllerParameterType type)> GeneratedParameters(Request r)
        {
            var generated = new List<(string, AnimatorControllerParameterType)>();
            if (ResolveEncoding(r) == IndexEncoding.Int)
                generated.Add((IndexParameter(r.baseName), AnimatorControllerParameterType.Int));
            else
            {
                int bits = NetworkSyncBuilder.BitsRequired(Mathf.Max(2, BuildSlots(r).Count));
                for (int i = 0; i < bits; i++)
                    generated.Add((BitParameter(r.baseName, i), AnimatorControllerParameterType.Bool));
            }
            foreach (var type in ChannelTypes(r))
            {
                if (type == AnimatorControllerParameterType.Float)
                {
                    int channels = FloatChannelsUsed(r);
                    for (int i = 0; i < channels; i++)
                        generated.Add((FloatChannelParameter(r.baseName, i), type));
                }
                else
                {
                    generated.Add((ChannelParameter(r.baseName, type), type));
                }
            }
            return generated;
        }

        // ---- build ----------------------------------------------------------------

        /// <summary>Runs the (pre-validated) request; returns false when validation fails.</summary>
        public static bool Apply(Request r)
        {
            if (Validate(r) != null) return false;
            var controller = r.controller;

            using (new UndoScope("Async Sync"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "Async Sync");

                EnsureParameter(controller, NetworkSyncBuilder.IsLocalParameter,
                    AnimatorControllerParameterType.Bool);
                var generated = GeneratedParameters(r);
                foreach (var (name, type) in generated)
                    EnsureParameter(controller, name, type);

                AnimatorStateMachine stateMachine;
                if (r.layerIndex >= 0)
                {
                    // Regenerate the designated layer in place instead of stacking new ones.
                    stateMachine = controller.layers[r.layerIndex].stateMachine;
                    Undo.RegisterCompleteObjectUndo(stateMachine, "Async Sync");
                    ClearStateMachine(stateMachine);
                }
                else
                {
                    string layerName = string.IsNullOrEmpty(r.layerName) ? r.baseName : r.layerName;
                    controller.AddLayer(DbtBuilder.UniqueLayerName(controller, layerName));
                    var layers = controller.layers;
                    layers[layers.Length - 1].defaultWeight = 1f;
                    controller.layers = layers;
                    stateMachine = layers[layers.Length - 1].stateMachine;
                    Undo.RegisterCompleteObjectUndo(stateMachine, "Async Sync");
                }

                var slots = BuildSlots(r);
                var schedule = BuildSchedule(slots);
                var encoding = ResolveEncoding(r);
                // Motion for the generated states. Zero-length clips are refused: exit times are
                // normalized to the motion, so a length of 0 would make them meaningless.
                var empty = ResolveEmptyClip(r);

                string[] indexBits = null;
                if (encoding == IndexEncoding.Bool)
                {
                    int bits = NetworkSyncBuilder.BitsRequired(slots.Count);
                    indexBits = new string[bits];
                    for (int i = 0; i < bits; i++)
                        indexBits[i] = BitParameter(r.baseName, i);
                }

                // Local side: the cycle, one state per SCHEDULE step — a priority slot appears
                // several times, and each appearance needs its own state to keep the ring a
                // ring. A motion-less state advances normalized time at one unit per second,
                // so its exit time reads directly as seconds; with the Empty clip filled in,
                // the same dwell has to be expressed in units of that clip.
                float exitTime = empty != null ? r.stepSeconds / empty.length : r.stepSeconds;

                var visits = new Dictionary<int, int>();
                var sendStates = new List<AnimatorState>(schedule.Count);
                for (int k = 0; k < schedule.Count; k++)
                {
                    int slotIndex = schedule[k];
                    var slot = slots[slotIndex];
                    visits.TryGetValue(slotIndex, out int visit);
                    visits[slotIndex] = visit + 1;

                    var state = stateMachine.AddState(
                        SlotStateName("Send", slot, visit),
                        new Vector3(260f, 60f + k * 70f, 0f));
                    state.writeDefaultValues = true;
                    state.motion = empty;
                    sendStates.Add(state);

                    if (r.skipDrivers) continue;
                    var driver = VrcParameterDriver.AddTo(state, "Async Send");
                    if (driver == null) continue;
                    Undo.RegisterCompleteObjectUndo(driver, "Async Sync");
                    VrcParameterDriver.SetLocalOnly(driver, true);
                    // Values first, then the index — remotes react to the index change.
                    AddChannelCopies(driver, r, slot, toChannels: true);
                    if (encoding == IndexEncoding.Int)
                        VrcParameterDriver.AddSetEntry(driver, IndexParameter(r.baseName), slotIndex);
                    else
                        for (int bit = 0; bit < indexBits.Length; bit++)
                            VrcParameterDriver.AddSetEntry(driver, indexBits[bit],
                                ((slotIndex >> bit) & 1) == 1 ? 1f : 0f);
                }
                for (int k = 0; k < sendStates.Count; k++)
                {
                    var transition = sendStates[k].AddTransition(sendStates[(k + 1) % sendStates.Count]);
                    transition.hasExitTime = true;
                    transition.exitTime = exitTime;
                    transition.hasFixedDuration = true;
                    transition.duration = 0f;
                    EditorUtility.SetDirty(transition);
                }

                // Remote side: Any-State decoder — one state per SLOT (revisits reuse it).
                var idle = stateMachine.AddState("Remote Idle", new Vector3(620f, 60f, 0f));
                idle.writeDefaultValues = true;
                idle.motion = empty;
                for (int i = 0; i < slots.Count; i++)
                {
                    var slot = slots[i];
                    var state = stateMachine.AddState(
                        SlotStateName("Recv", slot, 0),
                        new Vector3(620f, 130f + i * 70f, 0f));
                    state.writeDefaultValues = true;
                    state.motion = empty;

                    if (!r.skipDrivers)
                    {
                        var driver = VrcParameterDriver.AddTo(state, "Async Recv");
                        if (driver != null)
                        {
                            Undo.RegisterCompleteObjectUndo(driver, "Async Sync");
                            VrcParameterDriver.SetLocalOnly(driver, false);
                            AddChannelCopies(driver, r, slot, toChannels: false);
                        }
                    }

                    var transition = stateMachine.AddAnyStateTransition(state);
                    transition.canTransitionToSelf = false;
                    transition.hasExitTime = false;
                    transition.hasFixedDuration = true;
                    transition.duration = 0f;
                    transition.AddCondition(AnimatorConditionMode.IfNot, 0f,
                        NetworkSyncBuilder.IsLocalParameter);
                    if (encoding == IndexEncoding.Int)
                        transition.AddCondition(AnimatorConditionMode.Equals, i, IndexParameter(r.baseName));
                    else
                        for (int bit = 0; bit < indexBits.Length; bit++)
                            transition.AddCondition(((i >> bit) & 1) == 1
                                    ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot,
                                0f, indexBits[bit]);
                    EditorUtility.SetDirty(transition);
                }

                // Entry: locals fall through to the first send slot; remotes branch to Idle.
                stateMachine.defaultState = sendStates[0];
                var entry = stateMachine.AddEntryTransition(idle);
                entry.AddCondition(AnimatorConditionMode.IfNot, 0f, NetworkSyncBuilder.IsLocalParameter);

                // The generated parameters are the only ones that need to sync.
                if (r.addToStore && r.store != null && r.store.Target != null)
                    foreach (var (name, type) in generated)
                    {
                        if (r.store.Find(name) != null) continue;
                        var mapped = VrcExpressionParameters.MapType(type);
                        if (mapped == null) continue;
                        r.store.Add(new VrcExpressionParameters.Entry
                        {
                            name = name,
                            valueType = mapped.Value,
                            synced = true,
                            saved = false,
                        });
                    }

                // Remember the setup with the controller so the wizard can re-open and
                // regenerate this layer later (same pattern as the DBT layer choice).
                GraphFrameData.SaveAsyncSync(controller, new GraphFrameData.AsyncSyncConfig
                {
                    layer = stateMachine,
                    baseName = r.baseName,
                    encoding = (int)r.encoding,
                    stepSeconds = r.stepSeconds,
                    floatChannels = r.floatChannels,
                    targets = new List<string>(r.targets),
                    priorities = new List<string>(r.priorities ?? new List<string>()),
                });

                EditorUtility.SetDirty(stateMachine);
                EditorUtility.SetDirty(controller);
            }
            return true;
        }

        /// <summary>"Send X", "Send X +2" for a batch, "(2)" suffixed on repeat visits so
        /// state names stay unique inside the machine.</summary>
        static string SlotStateName(string prefix, Slot slot, int visit)
        {
            string name = prefix + " " + DbtBuilder.Sanitize(slot.targets[0]);
            if (slot.targets.Count > 1) name += " +" + (slot.targets.Count - 1);
            if (visit > 0) name += " (" + (visit + 1) + ")";
            return name;
        }

        /// <summary>Adds the copy entries for one slot: each Float target pairs with its
        /// channel by position; Bool / Int slots hold one target on the type's channel.</summary>
        static void AddChannelCopies(StateMachineBehaviour driver, Request r, Slot slot, bool toChannels)
        {
            for (int j = 0; j < slot.targets.Count; j++)
            {
                string target = slot.targets[j];
                var type = DbtBuilder.FindParameter(r.controller, target).type;
                string channel = type == AnimatorControllerParameterType.Float
                    ? FloatChannelParameter(r.baseName, j)
                    : ChannelParameter(r.baseName, type);
                if (toChannels)
                    VrcParameterDriver.AddCopyEntry(driver, target, channel);
                else
                    VrcParameterDriver.AddCopyEntry(driver, channel, target);
            }
        }

        /// <summary>Empties a layer for regeneration: transitions, states (and their
        /// behaviours, so no orphaned sub-assets pile up) and nested machines.</summary>
        static void ClearStateMachine(AnimatorStateMachine stateMachine)
        {
            foreach (var transition in stateMachine.anyStateTransitions)
                if (transition != null)
                    stateMachine.RemoveAnyStateTransition(transition);
            foreach (var transition in stateMachine.entryTransitions)
                if (transition != null)
                    stateMachine.RemoveEntryTransition(transition);
            foreach (var child in stateMachine.states)
            {
                if (child.state == null) continue;
                foreach (var behaviour in child.state.behaviours)
                    if (behaviour != null)
                        Undo.DestroyObjectImmediate(behaviour);
                stateMachine.RemoveState(child.state);
            }
            foreach (var child in stateMachine.stateMachines)
                if (child.stateMachine != null)
                    stateMachine.RemoveStateMachine(child.stateMachine);
        }

        static void EnsureParameter(AnimatorController controller, string name,
            AnimatorControllerParameterType type)
        {
            if (DbtBuilder.FindParameter(controller, name) == null)
                controller.AddParameter(name, type);
        }
    }
}

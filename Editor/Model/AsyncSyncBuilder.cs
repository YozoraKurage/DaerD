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
    /// A slot carries one Int target, up to <see cref="Request.floatChannels"/> Float targets
    /// or up to <see cref="Request.boolChannels"/> Bool targets at once — more channels mean
    /// fewer slots and a shorter cycle, at 8 synced bits per extra Float channel and 1 per
    /// extra Bool channel. Each target has a sync rate: a ×N slot is
    /// placed N times per pass, spread as evenly as the other slots allow, so it
    /// refreshes N times as often as a ×1 slot. Rates sharing a common factor are
    /// normalized away (all-×2 is the same cycle as all-×1), and a rate the other slots
    /// can't separate is lowered to what they can.
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
    ///
    /// Targets marked requestable additionally accept sync REQUESTS: a local, unsynced Bool
    /// ("base/Req/target") that anything on the avatar can raise — DaerD's per-state sync
    /// request drives it from a Parameter Driver. At each step boundary the cycle checks the
    /// flags and jumps to a requested slot instead of the ring's next step, so a fresh value
    /// reaches remotes after at most one step instead of a full pass; the slot's send driver
    /// clears the flag as it services it. A request for the slot that just sent is picked up
    /// one step later — never back-to-back, which the decoder couldn't see.
    ///
    /// Requests queue, they don't interrupt: a redirect carries the ring's exit time, so the
    /// step that was running when a flag went up still spends its full dwell and the jump
    /// happens at the boundary. One request is serviced per boundary — the first in cycle
    /// order — and flags that lost the boundary stay raised for the next one.
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
            /// the single Int when they tie. See <see cref="AsyncSyncCost.ResolveEncoding"/>.</summary>
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
            /// <summary>Synced Bool channels (1–8). Each slot carries up to this many Bool
            /// targets at once, at 1 more synced bit each — the cheapest speed-up there is:
            /// four channels quarter the pass for a single extra bit.</summary>
            public int boolChannels = 1;
            /// <summary>Per-target sync rate (times per pass), 1 when absent. Entries for
            /// names not present in <see cref="targets"/> are ignored, so a stale saved
            /// setup doesn't block regeneration.</summary>
            public Dictionary<string, int> rates = new Dictionary<string, int>();

            /// <summary>
            /// Targets that accept an on-demand sync request. Each gets a local, unsynced
            /// Bool ("base/Req/target"); anything on the avatar — typically a DaerD sync
            /// request on a state — sets it to 1, and the send cycle jumps to that target's
            /// slot at the next step boundary instead of waiting out the pass. The slot's
            /// send driver resets the flag once the value is on the wire.
            /// </summary>
            public List<string> requestTargets = new List<string>();

            /// <summary>
            /// Targets that start a slot of their own instead of joining the batch the target
            /// before them opened. Batched targets ride one Parameter Driver copy in one step,
            /// so they are sent together by construction and no schedule can separate them —
            /// this is how two of them are told to occupy different steps instead. Entries for
            /// names that are not targets are ignored (same contract as
            /// <see cref="rates"/>), so a stale saved setup doesn't block regeneration.
            /// </summary>
            public List<string> slotBreaks = new List<string>();

            public int RateOf(string name) =>
                rates != null && rates.TryGetValue(name, out int rate)
                    ? Mathf.Clamp(rate, 1, MaxRate) : 1;

            /// <summary>
            /// Explicit cycle, as a sequence of target names (a batched Float stands for its
            /// whole slot). When non-empty this IS the schedule — rates are ignored — and
            /// validation enforces what the decoder needs: every slot visited, no slot in
            /// adjacent steps. The deepest control the recipe API exposes.
            /// </summary>
            public List<string> scheduleOverride = new List<string>();

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

        /// <summary>The largest allowed sync rate. Higher never helps: a slot can't be
        /// separated by fewer other steps than it occupies.</summary>
        public const int MaxRate = 8;

        /// <summary>One step's payload: a single Bool / Int target, or a batch of Floats
        /// sent through the Float channels together.</summary>
        public class Slot
        {
            public readonly List<string> targets = new List<string>();
            public int rate = 1;
        }

        // Spelled out in AsyncSyncNaming; the facade keeps the names its callers know.

        public static string IndexParameter(string baseName) =>
            AsyncSyncNaming.IndexParameter(baseName);

        public static string BitParameter(string baseName, int bit) =>
            AsyncSyncNaming.BitParameter(baseName, bit);

        public static string ChannelParameter(string baseName, AnimatorControllerParameterType type) =>
            AsyncSyncNaming.ChannelParameter(baseName, type);

        public static string FloatChannelParameter(string baseName, int channel) =>
            AsyncSyncNaming.FloatChannelParameter(baseName, channel);

        public static string BoolChannelParameter(string baseName, int channel) =>
            AsyncSyncNaming.BoolChannelParameter(baseName, channel);

        public static string RequestParameter(string baseName, string target) =>
            AsyncSyncNaming.RequestParameter(baseName, target);

        public static string DefaultBaseName(AnimatorController controller) =>
            AsyncSyncNaming.DefaultBaseName(controller);

        internal static string DefaultBaseName(string guid, ICollection<string> taken) =>
            AsyncSyncNaming.DefaultBaseName(guid, taken);

        // ---- slots and schedule ----------------------------------------------

        // The math itself lives in AsyncSyncSchedule; these keep the facade's surface.

        public static List<Slot> BuildSlots(Request r) => AsyncSyncSchedule.BuildSlots(r);

        public static List<int> BuildSchedule(List<Slot> slots) =>
            AsyncSyncSchedule.BuildSchedule(slots);

        public static int[] EffectiveWeights(List<Slot> slots) =>
            AsyncSyncSchedule.EffectiveWeights(slots);

        static int Gcd(int a, int b) => AsyncSyncSchedule.Gcd(a, b);

        public static List<int> ResolveScheduleOverride(Request r, List<Slot> slots,
            List<string> errors) => AsyncSyncSchedule.ResolveScheduleOverride(r, slots, errors);

        public static List<int> EffectiveSchedule(Request r, List<Slot> slots) =>
            AsyncSyncSchedule.EffectiveSchedule(r, slots);

        public static List<string> RepairScheduleOverride(Request r, List<string> schedule) =>
            AsyncSyncSchedule.RepairScheduleOverride(r, schedule);

        public static int NextStepSlot(List<int> steps, int slotCount) =>
            AsyncSyncSchedule.NextStepSlot(steps, slotCount);

        // ---- resolution and cost ---------------------------------------------

        // Worked out in AsyncSyncCost; the facade stays the single entry point.

        public static IndexEncoding ResolveEncoding(Request r) => AsyncSyncCost.ResolveEncoding(r);

        public static AnimationClip ResolveEmptyClip(Request r) => AsyncSyncCost.ResolveEmptyClip(r);

        public static int FloatChannelsUsed(Request r) => AsyncSyncCost.FloatChannelsUsed(r);

        public static int BoolChannelsUsed(Request r) => AsyncSyncCost.BoolChannelsUsed(r);

        public static float CycleSeconds(Request r) => AsyncSyncCost.CycleSeconds(r);

        public static Dictionary<string, float> RefreshIntervals(Request r) =>
            AsyncSyncCost.RefreshIntervals(r);

        public static int FreeSlots(Request r) => AsyncSyncCost.FreeSlots(r);

        public static int CompressedBits(Request r) => AsyncSyncCost.CompressedBits(r);

        public static int DirectBits(Request r) => AsyncSyncCost.DirectBits(r);

        static List<AnimatorControllerParameterType> ChannelTypes(Request r) =>
            AsyncSyncCost.ChannelTypes(r);

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
            if (r.boolChannels < 1 || r.boolChannels > 8)
                return L.Tr("Bool channels must be between 1 and 8.");
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

            if (r.rates != null)
                foreach (var rate in r.rates)
                    if (rate.Value < 1 || rate.Value > MaxRate)
                        return L.Tr("Sync rates must be between 1 and {0} ('{1}').", MaxRate, rate.Key);

            int slotCount = BuildSlots(r).Count;
            if (slotCount > 255)
                return L.Tr("Int encoding supports up to 255 states.");
            // With one slot the index never changes, and the decoder — which fires on the
            // index changing — would copy exactly once and then go deaf.
            if (slotCount < 2)
                return L.Tr("Everything fits into a single slot, so the index would never change and remotes would stop decoding. Lower the channel count, or add parameters.");

            if (r.scheduleOverride != null && r.scheduleOverride.Count > 0)
            {
                var errors = new List<string>();
                if (ResolveScheduleOverride(r, BuildSlots(r), errors) == null && errors.Count > 0)
                    return errors[0];
            }

            var isLocal = DbtBuilder.FindParameter(controller, NetworkSyncBuilder.IsLocalParameter);
            if (isLocal != null && isLocal.type != AnimatorControllerParameterType.Bool)
                return L.Tr("Parameter '{0}' exists but is not a Bool.", NetworkSyncBuilder.IsLocalParameter);

            var machineParameters = GeneratedParameters(r);
            machineParameters.AddRange(RequestParameters(r));
            foreach (var (name, type) in machineParameters)
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
                    cycle, EffectiveSchedule(r, BuildSlots(r)).Count, r.stepSeconds));

            // Say when a rate could not be honored: normalization (common factor) is
            // intentional and invisible, but a cap changes what the user asked for. An
            // explicit schedule replaces rates entirely, so the check would only mislead.
            if (r.scheduleOverride == null || r.scheduleOverride.Count == 0)
            {
                var slots = BuildSlots(r);
                var weights = EffectiveWeights(slots);
                var schedule = BuildSchedule(slots);
                var occurrences = new int[slots.Count];
                foreach (var step in schedule) occurrences[step]++;

                int rateGcd = 0;
                foreach (var slot in slots) rateGcd = Gcd(rateGcd, Mathf.Clamp(slot.rate, 1, MaxRate));
                for (int i = 0; i < slots.Count; i++)
                {
                    int asked = Mathf.Clamp(slots[i].rate, 1, MaxRate) / Mathf.Max(1, rateGcd);
                    if (occurrences[i] < asked)
                    {
                        warnings.Add(L.Tr(
                            "The ×{0} rate on '{1}' can't be separated by the other slots; it effectively runs at ×{2}. Add parameters or lower the rate.",
                            slots[i].rate, slots[i].targets[0], Mathf.Max(1, occurrences[i])));
                        break;
                    }
                }
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

            // The send ring copies each target into its channel with a Parameter Driver, and a
            // driver cannot read a value that animation writes: an AAP target would send the
            // animator's own field (usually the default) rather than what the tree computed.
            //
            // Said rather than refused, even though a multiplexed AAP never works. The scan
            // asks whether SOME reachable clip animates the parameter, which a clip on a
            // weight-0 layer or an unreachable state also satisfies — rejecting on that would
            // block a setup that is in fact fine, with no way past it. Refuse this once the
            // scan can tell a clip that plays from one that merely exists.
            var animated = new List<string>();
            var written = AapWriteScan.CollectWrittenParameters(r.controller);
            foreach (var name in r.targets)
                if (written.Contains(name)) animated.Add("'" + name + "'");
            if (animated.Count > 0)
                warnings.Add(L.Tr(
                    "Animation writes {0} (AAP — a DBT gadget output or a hand-made AAP clip). The send cycle copies targets with a Parameter Driver, which can't read an animated value, so those never reach remotes.",
                    string.Join(", ", animated)));

            if (r.assignEmptyClip)
            {
                var clip = r.emptyClip != null ? r.emptyClip : GraphFrameData.GetEmptyClip(r.controller);
                // Applying creates the clip when none is designated, so this only bites on a
                // controller with no asset file to store one in.
                if (clip == null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(r.controller)))
                    warnings.Add(L.Tr("No Empty clip is set for this controller, so the generated states stay motion-less — the analyzer flags every one of them. Set one in the controller overview to have them filled in."));
                else if (clip != null && clip.length <= 0f)
                    warnings.Add(L.Tr("The Empty clip '{0}' has zero length, so it can't carry the step timing; the generated states stay motion-less.", clip.name));
            }

            if (r.layerIndex >= 0 && r.layerIndex < r.controller.layers.Length
                && r.controller.layers[r.layerIndex].defaultWeight <= 0f && r.layerIndex != 0)
                warnings.Add(L.Tr("The target layer's weight is 0 — the send cycle won't run until it is raised."));

            // Stale request entries are ignored rather than blocking (same contract as rates),
            // but a recipe author typing .Requestable("Typo") deserves to hear about it.
            if (r.requestTargets != null)
                foreach (var name in r.requestTargets)
                    if (!r.targets.Contains(name))
                    {
                        warnings.Add(L.Tr(
                            "Sync requests are enabled for '{0}', which is not multiplexed here — the entry is ignored.",
                            name));
                        break;
                    }

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
                else if (type == AnimatorControllerParameterType.Bool)
                {
                    int channels = BoolChannelsUsed(r);
                    for (int i = 0; i < channels; i++)
                        generated.Add((BoolChannelParameter(r.baseName, i), type));
                }
                else
                {
                    generated.Add((ChannelParameter(r.baseName, type), type));
                }
            }
            return generated;
        }

        /// <summary>
        /// The request flags this request will create (all Bool). Unlike
        /// <see cref="GeneratedParameters"/> these stay local: they are never synced and never
        /// added to the parameter store — a request is raised and serviced on the wearer's
        /// client, and remotes just see the slot arrive early.
        /// </summary>
        public static List<(string name, AnimatorControllerParameterType type)> RequestParameters(Request r)
        {
            var generated = new List<(string, AnimatorControllerParameterType)>();
            foreach (var target in RequestableTargets(r))
                generated.Add((RequestParameter(r.baseName, target),
                    AnimatorControllerParameterType.Bool));
            return generated;
        }

        /// <summary>Request targets in cycle order, deduplicated, restricted to actual
        /// targets — a stale saved entry must not block regeneration (same contract as
        /// <see cref="Request.rates"/>).</summary>
        public static List<string> RequestableTargets(Request r)
        {
            var requestable = new List<string>();
            if (r?.requestTargets == null || r.targets == null) return requestable;
            foreach (var target in r.targets)
                if (r.requestTargets.Contains(target) && !requestable.Contains(target))
                    requestable.Add(target);
            return requestable;
        }

        // ---- build ----------------------------------------------------------------

        /// <summary>
        /// A runnable request rebuilt from a saved setup — what regenerating outside the
        /// wizard (per-state sync requests, the layer panel) starts from. Mirrors the wizard's
        /// own restore: layer resolved through the config's state machine, store and Empty
        /// clip from the controller's current associations, and the explicit cycle when the
        /// setup has one — without that last part, adding a sync request to a state would
        /// quietly rebuild a hand-timed (or recipe-timed) layer on the rate-derived pass.
        /// </summary>
        public static Request FromConfig(AnimatorController controller,
            GraphFrameData.AsyncSyncConfig config)
        {
            var request = new Request
            {
                controller = controller,
                baseName = config.baseName,
                encoding = (IndexEncoding)config.encoding,
                stepSeconds = config.stepSeconds,
                floatChannels = Mathf.Clamp(config.FloatChannelsOrDefault, 1, 8),
                boolChannels = Mathf.Clamp(config.BoolChannelsOrDefault, 1, 8),
                store = ParameterStore.Of(controller),
                emptyClip = GraphFrameData.GetEmptyClip(controller),
                layerIndex = LayerIndexOf(controller, config),
            };
            request.targets.AddRange(config.targets);
            foreach (var rate in config.RateMap())
                request.rates[rate.Key] = rate.Value;
            if (config.requests != null)
                request.requestTargets.AddRange(config.requests);
            if (config.schedule != null)
                request.scheduleOverride.AddRange(config.schedule);
            if (config.slotBreaks != null)
                request.slotBreaks.AddRange(config.slotBreaks);
            return request;
        }

        /// <summary>The layer a saved setup owns right now, or -1 when it is gone.</summary>
        public static int LayerIndexOf(AnimatorController controller,
            GraphFrameData.AsyncSyncConfig config)
        {
            if (controller == null || config == null) return -1;
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].stateMachine == config.layer)
                    return i;
            return -1;
        }

        /// <summary>Runs the (pre-validated) request; returns false when validation fails.</summary>
        public static bool Apply(Request r) => AsyncSyncApplier.Apply(r);
    }
}

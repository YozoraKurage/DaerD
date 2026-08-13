using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
// One step of an explicit grid is saved data first and a request field second, so the shape
// is declared with the rest of the saved setup; the alias keeps the request reading plainly.
using StepSpec = Yozolab.DaerD.GraphFrameData.AsyncSyncConfig.StepSpec;
using SyncGroup = Yozolab.DaerD.GraphFrameData.AsyncSyncConfig.SyncGroup;

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
    /// and up to <see cref="Request.boolChannels"/> Bool targets at once — more channels mean
    /// fewer slots and a shorter cycle, at 8 synced bits per extra Float channel and 1 per
    /// extra Bool channel. The automatic batching only ever fills a slot with targets of one
    /// type; <see cref="Request.steps"/> is how a slot is told to carry several. Each target
    /// has a sync rate: a ×N slot is
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
    /// flags and takes a DETOUR: one extra step that sends the requested slot and then rejoins
    /// the ring where it left, so a fresh value reaches remotes after at most one step instead
    /// of a full pass. The detour state clears the flag as it services it.
    ///
    /// Rejoining where it left is what makes the pass survive being interrupted. A jump that
    /// simply resumed from the requested slot's own position would move the ring's place in
    /// the pass, and a request that keeps being raised at the same point would then pin the
    /// cycle to one stretch of the pass and starve everything outside it — remotes would wait
    /// forever for values that are never sent. The detour spends a step and gives the place
    /// back, so the pass always advances: the step it left from is written to
    /// "base/Return" by every send state, and the request state reads it on the way back.
    ///
    /// A detour carries no routes of its own, so requests never chain. That bounds the whole
    /// cost of them: worst case the pass alternates detour, step, detour, step, which is one
    /// pass in twice the time — and never more, however hard the flags are driven.
    ///
    /// Requests queue, they don't interrupt: a detour carries the ring's exit time, so the
    /// step that was running when a flag went up still spends its full dwell and the detour
    /// happens at the boundary. One request is serviced per boundary — the first in cycle
    /// order — and flags that lost the boundary stay raised for the next one.
    /// <see cref="AsyncSyncSchedule.RequestOrigins"/> says which steps may start a detour: not
    /// the ones already sending that slot, and not the ones whose successor would repeat the
    /// index the detour wrote.
    ///
    /// A slot may not send twice in a row, because the decoder fires on the index CHANGING
    /// and a repeated index is a step nobody sees. <see cref="Request.allowRepeatSteps"/>
    /// buys that restriction off with a clock: a phase folded into the index, alternating
    /// between neighbouring steps, so one slot twice running still shows the decoder two
    /// different values. It is paid for in decoder states — a slot that repeats needs one per
    /// phase — which is why it is asked for rather than assumed. A detour sends its slot in
    /// one fixed phase (<see cref="AsyncSyncSchedule.RequestPhase"/>), so a clock changes
    /// which steps may start one rather than whether any may.
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
            /// Generate the remote-initialized flag: a local, unsynced Bool ("base/Ready")
            /// that reads 0 until this client has decoded every slot at least once, and 1
            /// from then on. The wearer reads 1 immediately — their own values were never
            /// anywhere else — so "a remote that has finished initializing" is
            /// <c>Ready &amp;&amp; !IsLocal</c>.
            ///
            /// Off by default: it costs a slot's worth of local Bools and a second layer,
            /// and a setup nobody reads it from would pay for both.
            /// </summary>
            public bool ready;

            /// <summary>
            /// Generate the drift-suspicion flag: a local, unsynced Bool ("base/Stale") that
            /// reads 1 when the lap that just closed did not bring every slot, and 0 when it
            /// did. Unlike <see cref="ready"/> it falls again — it is a reading of the last
            /// lap, not of the whole session.
            ///
            /// Measured by watching the lap marker (a slot the pass sends exactly once) rather
            /// than by timing anything: no window to size, no margin to guess, and a pass
            /// stretched by a request cannot make it wrong. A pass with no such slot cannot
            /// carry the flag, and <see cref="Validate"/> says so.
            /// </summary>
            public bool stale;

            /// <summary>
            /// Sets of targets that must reach a remote's real parameters together. The pass
            /// sends them whenever it sends them; the decoder holds each aside as it arrives,
            /// and one driver copies the whole set across once the last one is in — so remotes
            /// never see half a change, however many steps apart the halves were sent.
            ///
            /// A driver's entries all run in one frame, which is what makes the simultaneity
            /// structural rather than a matter of timing. Members that share a step already
            /// arrive together and need no group; this is for the ones that cannot share one,
            /// because their types differ or the channels are too narrow.
            ///
            /// Costs nothing on the wire: a shadow parameter and a flag per member, both
            /// animator-local, and a two-state layer per group.
            ///
            /// Worth exactly as much as <see cref="ready"/> says it is, and no more. A client
            /// that has just arrived decodes whatever index it finds — zero, because nothing
            /// has reached it yet — as that slot arriving, so its first commit can carry a
            /// value nobody sent. After the first full pass every member has been sent at
            /// least once and the guarantee holds; before it, read Ready.
            /// </summary>
            public List<SyncGroup> groups = new List<SyncGroup>();

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
            /// adjacent steps. Ignored entirely when <see cref="steps"/> is non-empty: a grid
            /// says which targets share a step as well as when, so a cycle written in the
            /// older vocabulary can only contradict it.
            /// </summary>
            public List<string> scheduleOverride = new List<string>();

            /// <summary>
            /// The pass written out as sets: one entry per step, each naming the targets that
            /// step sends. When non-empty this replaces the automatic batching AND the rates
            /// AND <see cref="scheduleOverride"/> — the slots become the distinct sets, in
            /// first-appearance order, and every step is the slot its set names.
            ///
            /// This is the only way to put targets of different types in one step: the
            /// automatic batching groups by type and rate, so it can pack Floats with Floats
            /// but never a Float with the Bool that belongs beside it. Which targets share a
            /// step is not a timing choice — one driver copies a step in one go, so they are
            /// sent together by construction — which is why it has to be said outright.
            /// </summary>
            public List<StepSpec> steps = new List<StepSpec>();

            /// <summary>
            /// Let a slot occupy adjacent steps — the wrap included — by giving the pass a
            /// clock: a phase folded into the index that alternates between neighbours, so
            /// the decoder sees a different index even when the payload is the same. Off by
            /// default, because a slot that repeats then needs a decoder state (and an
            /// Any-State route) per phase, and a setup that never repeats would pay for
            /// nothing. Also what lets a single-slot setup build at all.
            /// </summary>
            public bool allowRepeatSteps;

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

        /// <summary>One step's payload: the targets copied into the channels together. The
        /// automatic batching fills it with one type at a time; a step written out by hand
        /// (see <see cref="Request.steps"/>) may carry one of each.</summary>
        public class Slot
        {
            public readonly List<string> targets = new List<string>();
            public int rate = 1;
        }

        /// <summary>
        /// The clock over one pass: the phase each step sends, the phases each slot is decoded
        /// in, and the index value a (slot, phase) pair puts on the wire.
        ///
        /// Phases are laid end to end rather than multiplied out — a slot that never repeats
        /// still spends one index value, not two — which is what keeps the clock cheap on a
        /// pass where only one slot needs it. With <see cref="Request.allowRepeatSteps"/> off
        /// every slot has exactly one phase and <see cref="Index"/> is the slot number it has
        /// always been, so the whole build runs the same path either way.
        /// </summary>
        public class Clock
        {
            /// <summary>The phase each step of the schedule sends, one entry per step.</summary>
            public readonly int[] stepPhases;
            /// <summary>Phases each slot is decoded in: 2 for one the pass puts beside
            /// itself, 1 for every other slot.</summary>
            public readonly int[] slotPhases;
            /// <summary>Distinct values the index takes — what it has to be wide enough for,
            /// and the slot count exactly when no slot repeats.</summary>
            public readonly int indexValues;
            /// <summary>
            /// Whether the phases actually separate every pair of neighbouring steps that
            /// sends one slot. False only for a pass sending ONE slot from end to end in an
            /// odd number of steps: that closes the alternation into a ring of odd length,
            /// which two phases cannot colour. <see cref="Validate"/> refuses it.
            /// </summary>
            public readonly bool separates;
            /// <summary>Each slot's first index value: the phases before it, added up.</summary>
            readonly int[] _first;

            internal Clock(int[] stepPhases, int[] slotPhases, bool separates)
            {
                this.stepPhases = stepPhases;
                this.slotPhases = slotPhases;
                this.separates = separates;
                _first = new int[slotPhases.Length];
                for (int i = 0; i < slotPhases.Length; i++)
                {
                    _first[i] = indexValues;
                    indexValues += slotPhases[i];
                }
            }

            /// <summary>The index value one slot sends in one of its phases.</summary>
            public int Index(int slot, int phase) => _first[slot] + phase;
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

        public static string ReturnParameter(string baseName) =>
            AsyncSyncNaming.ReturnParameter(baseName);

        public static string ReadyParameter(string baseName) =>
            AsyncSyncNaming.ReadyParameter(baseName);

        public static string SeenParameter(string baseName, string slotName) =>
            AsyncSyncNaming.SeenParameter(baseName, slotName);

        public static string ReadyLayerName(string layerName) =>
            AsyncSyncNaming.ReadyLayerName(layerName);

        public static string StaleParameter(string baseName) =>
            AsyncSyncNaming.StaleParameter(baseName);

        public static string FreshParameter(string baseName, string slotName) =>
            AsyncSyncNaming.FreshParameter(baseName, slotName);

        public static string StaleLayerName(string layerName) =>
            AsyncSyncNaming.StaleLayerName(layerName);

        public static string HoldParameter(string baseName, string target) =>
            AsyncSyncNaming.HoldParameter(baseName, target);

        public static string HeldParameter(string baseName, string target) =>
            AsyncSyncNaming.HeldParameter(baseName, target);

        public static string GroupLayerName(string layerName, string groupName) =>
            AsyncSyncNaming.GroupLayerName(layerName, groupName);

        public static string DefaultBaseName(AnimatorController controller) =>
            AsyncSyncNaming.DefaultBaseName(controller);

        internal static string DefaultBaseName(string guid, ICollection<string> taken) =>
            AsyncSyncNaming.DefaultBaseName(guid, taken);

        // ---- slots and schedule ----------------------------------------------

        // The math itself lives in AsyncSyncSchedule; these keep the facade's surface.

        public static List<Slot> BuildSlots(Request r) => AsyncSyncSchedule.BuildSlots(r);

        public static List<int> BuildSchedule(List<Slot> slots) =>
            AsyncSyncSchedule.BuildSchedule(slots);

        public static Clock BuildClock(Request r, List<Slot> slots, List<int> schedule) =>
            AsyncSyncSchedule.BuildClock(r, slots, schedule);

        public static int[] EffectiveWeights(List<Slot> slots) =>
            AsyncSyncSchedule.EffectiveWeights(slots);

        static int Gcd(int a, int b) => AsyncSyncSchedule.Gcd(a, b);

        public static List<int> ResolveScheduleOverride(Request r, List<Slot> slots,
            List<string> errors) => AsyncSyncSchedule.ResolveScheduleOverride(r, slots, errors);

        public static List<int> EffectiveSchedule(Request r, List<Slot> slots) =>
            AsyncSyncSchedule.EffectiveSchedule(r, slots);

        public static List<string> RepairScheduleOverride(Request r, List<string> schedule) =>
            AsyncSyncSchedule.RepairScheduleOverride(r, schedule);

        public static List<StepSpec> RepairSteps(Request r, List<StepSpec> steps) =>
            AsyncSyncSchedule.RepairSteps(r, steps);

        public static List<string> NormalizeStep(Request r, StepSpec step) =>
            AsyncSyncSchedule.NormalizeStep(r, step);

        public static bool StepHasRoom(Request r, List<string> members,
            AnimatorControllerParameterType type) =>
            AsyncSyncSchedule.StepHasRoom(r, members, type);

        public static int NextStepSlot(List<int> steps, int slotCount) =>
            AsyncSyncSchedule.NextStepSlot(steps, slotCount);

        public static List<int> RequestOrigins(List<int> schedule, Clock clock, int slot) =>
            AsyncSyncSchedule.RequestOrigins(schedule, clock, slot);

        // ---- resolution and cost ---------------------------------------------

        // Worked out in AsyncSyncCost; the facade stays the single entry point.

        public static IndexEncoding ResolveEncoding(Request r) => AsyncSyncCost.ResolveEncoding(r);

        public static AnimationClip ResolveEmptyClip(Request r) => AsyncSyncCost.ResolveEmptyClip(r);

        public static int FloatChannelsUsed(Request r) => AsyncSyncCost.FloatChannelsUsed(r);

        public static int BoolChannelsUsed(Request r) => AsyncSyncCost.BoolChannelsUsed(r);

        public static float CycleSeconds(Request r) => AsyncSyncCost.CycleSeconds(r);

        public static float WorstCycleSeconds(Request r) => AsyncSyncCost.WorstCycleSeconds(r);

        public static Dictionary<string, float> RefreshIntervals(Request r) =>
            AsyncSyncCost.RefreshIntervals(r);

        public static int FreeSlots(Request r) => AsyncSyncCost.FreeSlots(r);

        public static int IndexValues(Request r) => AsyncSyncCost.IndexValues(r);

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
                        return L.Tr("Sync weights must be between 1 and {0} ('{1}').", MaxRate, rate.Key);

            if (r.steps != null && r.steps.Count > 0)
            {
                string problem = ValidateSteps(r);
                if (problem != null) return problem;
            }

            int slotCount = BuildSlots(r).Count;
            // The index values, not the slots: a clock gives some slots two of them, which is
            // what the encoding actually has to hold.
            if (IndexValues(r) > 255)
                return L.Tr("Int encoding supports up to 255 states.");
            // With one slot the index never changes, and the decoder — which fires on the
            // index changing — would copy exactly once and then go deaf. A clock changes the
            // index by itself, so it is the one thing that makes a single slot decodable.
            if (slotCount < 2 && !r.allowRepeatSteps)
                return L.Tr("Everything fits into a single slot, so the index would never change and remotes would stop decoding. Lower the channel count, or add parameters.");
            // The clock alternates between neighbours, so a pass that sends ONE slot from end
            // to end closes the alternation into a ring — and a ring of odd length has no
            // alternating colouring: the wrap would repeat a phase and lose that step.
            if (r.allowRepeatSteps)
            {
                var clockSlots = BuildSlots(r);
                if (!BuildClock(r, clockSlots, EffectiveSchedule(r, clockSlots)).separates)
                    return L.Tr("Every step sends the same parameters, so only the clock tells them apart — and the clock alternates, which an odd number of steps can't do. Add or drop a step.");
            }

            // A grid says which targets share a step as well as when, so a cycle saved beside
            // one is not the pass being built and must not be judged as though it were.
            if ((r.steps == null || r.steps.Count == 0)
                && r.scheduleOverride != null && r.scheduleOverride.Count > 0)
            {
                var errors = new List<string>();
                if (ResolveScheduleOverride(r, BuildSlots(r), errors) == null && errors.Count > 0)
                    return errors[0];
            }

            // Two groups under one name would want one layer between them, and the saved
            // setup could not tell them apart afterwards either.
            if (r.groups != null)
            {
                var names = new HashSet<string>();
                foreach (var group in r.groups)
                    if (group != null && !string.IsNullOrEmpty(group.name)
                        && !names.Add(group.name))
                        return L.Tr("Two groups are called '{0}'. Give them different names.",
                            group.name);
            }

            // The flag is measured by watching a slot the pass sends exactly once. Refused
            // rather than quietly dropped: a Stale that never falls is worse than none, and
            // the two ways out are worth naming.
            if (r.stale && LapMarkerSlot(r) < 0)
                return L.Tr("No slot closes a lap on its own, so there is nothing to measure a lap against: every slot is either sent more than once or open to requests. Send one of them once per pass, or take its request away.");

            var isLocal = DbtBuilder.FindParameter(controller, NetworkSyncBuilder.IsLocalParameter);
            if (isLocal != null && isLocal.type != AnimatorControllerParameterType.Bool)
                return L.Tr("Parameter '{0}' exists but is not a Bool.", NetworkSyncBuilder.IsLocalParameter);

            var machineParameters = GeneratedParameters(r);
            machineParameters.AddRange(RequestParameters(r));
            machineParameters.AddRange(ReadyParameters(r));
            machineParameters.AddRange(StaleParameters(r));
            machineParameters.AddRange(GroupParameters(r));
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

        /// <summary>
        /// What the decoder needs of an explicit grid, in the vocabulary
        /// <see cref="AsyncSyncSchedule.ResolveScheduleOverride"/> uses for the same rules:
        /// every step sends something it has the channels for, every target is sent by some
        /// step, and no two neighbouring steps — the wrap included — send the same set. That
        /// last one is the physical rule the whole technique rests on: the decoder's Any-State
        /// routes fire on the index CHANGING, so a step that repeats its neighbour's payload
        /// spends an index nobody ever sees. It is also the rule
        /// <see cref="Request.allowRepeatSteps"/> pays to lift, and the only one checked here
        /// that a clock makes untrue.
        ///
        /// Names in a step that are not multiplexed are dropped rather than reported, the same
        /// contract <see cref="Request.rates"/> keeps — a stale saved entry must not block
        /// regeneration.
        /// </summary>
        static string ValidateSteps(Request r)
        {
            var controller = r.controller;
            var sets = new List<List<string>>();
            foreach (var step in r.steps) sets.Add(NormalizeStep(r, step));

            var covered = new HashSet<string>();
            for (int k = 0; k < sets.Count; k++)
            {
                if (sets[k].Count == 0)
                    return L.Tr("Step {0} sends nothing — an empty step spends an index without carrying a value.", k + 1);
                // Counted in a fixed order rather than off a map, so a step that overruns two
                // kinds of channel at once always names the same one first.
                foreach (var type in new[]
                         {
                             AnimatorControllerParameterType.Float,
                             AnimatorControllerParameterType.Bool,
                             AnimatorControllerParameterType.Int,
                         })
                {
                    int count = 0;
                    foreach (var name in sets[k])
                        if (DbtBuilder.FindParameter(controller, name).type == type) count++;
                    int capacity = AsyncSyncSchedule.StepCapacity(r, type);
                    if (count > capacity)
                        return L.Tr("Step {0} sends {1} {2} parameters, but only {3} channel(s) of that type exist.",
                            k + 1, count, type, capacity);
                }
                foreach (var name in sets[k]) covered.Add(name);
            }

            foreach (var name in r.targets)
                if (!covered.Contains(name))
                    return L.Tr("'{0}' is never sent — no step of the pass carries it.", name);

            // Both of these are what a clock buys off, so with one they are not rules at all:
            // the phase changes the index between neighbours, and Validate's own check on the
            // clock takes over from here.
            if (r.allowRepeatSteps) return null;
            for (int k = 0; k < sets.Count && sets.Count > 1; k++)
            {
                int next = (k + 1) % sets.Count;
                if (AsyncSyncSchedule.SameStep(sets[k], sets[next]))
                    return L.Tr("Steps {0} and {1} send the same parameters, so the index would not change between them and remotes would not decode the second one.",
                        k + 1, next + 1);
            }
            if (BuildSlots(r).Count < 2)
                return L.Tr("Every step sends the same parameters, so the index would never change and remotes would stop decoding. Give at least two steps different parameters.");
            return null;
        }

        /// <summary>
        /// Non-blocking observations shown in the wizard before running.
        ///
        /// <paramref name="animated"/> is the AAP set (<see cref="AapWriteScan"/>) when the
        /// caller already holds one. That scan walks every state, every blend tree and every
        /// clip's curve bindings, and this method is called straight from OnGUI — a draw path
        /// that scanned per event would spend most of a mouse drag inside AnimationUtility.
        /// Null means "work it out", which is what the one-shot callers (a recipe's build, the
        /// tests) want and what keeps this method answerable on its own.
        /// </summary>
        public static List<string> Warnings(Request r, HashSet<string> animated = null)
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

            // A type with one target is not multiplexed at all. Its channel carries that one
            // parameter and nothing else, for the bits syncing it directly would cost, and
            // the value reaches remotes no sooner for having gone round the ring — later, if
            // anything, since it now waits its turn. Said per type, because the bits differ
            // and because the fix ("multiplex another one of these, or leave this one out")
            // is a different sentence for each.
            var lone = DbtBuilder.ParametersByName(r.controller);
            foreach (var type in ChannelTypes(r))
            {
                string only = null;
                int count = 0;
                foreach (var name in r.targets)
                    if (lone.Find(name)?.type == type) { count++; only = name; }
                if (count != 1) continue;
                warnings.Add(L.Tr(
                    "'{0}' is the only {1} here, and a type with one target multiplexes nothing: its channel costs the {2} synced bit(s) that syncing '{0}' directly would, and the value reaches remotes no sooner for the trip. Sync it directly, or multiplex another {1} beside it.",
                    only, type,
                    type == AnimatorControllerParameterType.Bool ? 1 : 8));
            }

            // Refused outright without a clock; with one it builds and decodes, and is still
            // every target on the wire every step — direct sync wearing a cycle.
            if (r.allowRepeatSteps && r.targets.Count >= 2 && BuildSlots(r).Count < 2)
                warnings.Add(L.Tr(
                    "Everything fits into a single slot, so every step sends every target. The clock keeps remotes decoding, but this is direct sync with the index on top — lower the channel count to get a cycle back."));

            float cycle = CycleSeconds(r);
            if (cycle > 3f)
                warnings.Add(L.Tr(
                    "One full pass takes {0:0.#} s ({1} steps × {2:0.##} s), so a remote can be that far behind on any one value.",
                    cycle, EffectiveSchedule(r, BuildSlots(r)).Count, r.stepSeconds));

            // The one structural change that shortens every pass at once, offered only when it
            // would actually build and actually help — AsyncSyncSplit.ByType decides both, and
            // says nothing at all otherwise.
            var byType = AsyncSyncSplit.ByType(r);
            if (byType.Count > 1) warnings.Add(AsyncSyncSplit.Advice(r, byType));

            // Say when a rate could not be honored: normalization (common factor) is
            // intentional and invisible, but a cap changes what the user asked for. An
            // explicit schedule or grid replaces rates entirely, so the check would only mislead.
            if ((r.scheduleOverride == null || r.scheduleOverride.Count == 0)
                && (r.steps == null || r.steps.Count == 0))
            {
                var slots = BuildSlots(r);
                var weights = EffectiveWeights(slots);
                // The pass that will be built, which with no override and no grid is the
                // rate-derived one — except that a clock lets it keep an adjacent visit the
                // repair would otherwise have dropped, and this counts visits.
                var schedule = EffectiveSchedule(r, slots);
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
                            "The ×{0} weight on '{1}' can't be separated by the other slots; it effectively runs at ×{2}. Add parameters or lower the weight.",
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
            var written = animated ?? AapWriteScan.CollectWrittenParameters(r.controller);
            var animatedTargets = new List<string>();
            foreach (var name in r.targets)
                if (written.Contains(name)) animatedTargets.Add("'" + name + "'");
            if (animatedTargets.Count > 0)
                warnings.Add(L.Tr(
                    "Animation writes {0} (AAP — a DBT gadget output or a hand-made AAP clip). The send cycle copies targets with a Parameter Driver, which can't read an animated value, so those never reach remotes.",
                    string.Join(", ", animatedTargets)));

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

            var requestable = RequestableTargets(r);
            if (requestable.Count > 0)
            {
                // The price of requests, said in seconds rather than left to be discovered:
                // a detour is an extra step, and the pass is what waits for it.
                warnings.Add(L.Tr(
                    "Sync requests make a pass take up to {0:0.#} s instead of {1:0.#} s — a request spends an extra step before the cycle carries on where it left. That is the ceiling however often they are raised, but a target driven on every state change spends it.",
                    WorstCycleSeconds(r), CycleSeconds(r)));

                // A slot the pass already visits every other step has no boundary a detour
                // could be inserted at without repeating an index the decoder would miss.
                var requestSlots = BuildSlots(r);
                var requestSchedule = EffectiveSchedule(r, requestSlots);
                var requestClock = BuildClock(r, requestSlots, requestSchedule);
                for (int i = 0; i < requestSlots.Count; i++)
                {
                    bool asked = false;
                    foreach (var name in requestSlots[i].targets)
                        if (requestable.Contains(name)) asked = true;
                    if (!asked) continue;
                    if (RequestOrigins(requestSchedule, requestClock, i).Count > 0) continue;
                    warnings.Add(L.Tr(
                        "'{0}' is sent so often that no step is left to request it from — the flag is built, but the cycle reaches the slot as fast as a request could. Lower its rate, or drop the request.",
                        requestSlots[i].targets[0]));
                    break;
                }
            }

            // Ready's own number, since the flag is invisible until someone else is in the
            // instance: a remote has decoded every slot within one pass of arriving, so that
            // is when it latches — later if the wire drops a step, never earlier.
            if (r.ready)
                warnings.Add(L.Tr(
                    "'{0}' turns on once a remote has decoded every slot — within {1:0.#} s of arriving when nothing is lost, and later when something is. It never turns off again, and the wearer reads it as on from the start.",
                    ReadyParameter(r.baseName), WorstCycleSeconds(r)));

            // What the flag reads on a remote that has only just arrived, said here rather
            // than left to be discovered in an instance with someone else in it.
            if (r.stale)
            {
                var staleSlots = BuildSlots(r);
                int marker = LapMarkerSlot(r);
                if (marker >= 0)
                    warnings.Add(L.Tr(
                        "'{0}' is judged every time '{1}' comes round, which is once per pass. A remote that arrives mid-pass reads it as on for the rest of that pass — pair it with the remote initialized flag to tell that apart from a cycle that has actually started dropping steps.",
                        StaleParameter(r.baseName), staleSlots[marker].targets[0]));
            }

            // A group whose members already share a step is machinery for a guarantee the
            // step itself is already making — one driver copies them, so they were never
            // going to arrive apart.
            var groups = EffectiveGroups(r);
            if (groups.Count > 0)
            {
                var groupSlots = BuildSlots(r);
                var slotOfTarget = new Dictionary<string, int>();
                for (int i = 0; i < groupSlots.Count; i++)
                    foreach (var name in groupSlots[i].targets)
                        slotOfTarget[name] = i;
                foreach (var group in groups)
                {
                    var seen = new HashSet<int>();
                    foreach (var name in group.members)
                        if (slotOfTarget.TryGetValue(name, out int slot)) seen.Add(slot);
                    if (seen.Count > 1) continue;
                    warnings.Add(L.Tr(
                        "'{0}' groups parameters that already share a step, so they were never going to arrive apart. The group holds them back for nothing — drop it, or split the step.",
                        group.name));
                    break;
                }
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
                int bits = NetworkSyncBuilder.BitsRequired(Mathf.Max(2, IndexValues(r)));
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
        /// What the request machinery needs: one Bool flag per requestable target, and the Int
        /// holding the step a detour has to come back to. Unlike <see cref="GeneratedParameters"/>
        /// these stay local: they are never synced and never added to the parameter store — a
        /// request is raised and serviced on the wearer's client, and remotes just see the slot
        /// arrive early. A setup nobody can request from creates none of them.
        /// </summary>
        public static List<(string name, AnimatorControllerParameterType type)> RequestParameters(Request r)
        {
            var generated = new List<(string, AnimatorControllerParameterType)>();
            foreach (var target in RequestableTargets(r))
                generated.Add((RequestParameter(r.baseName, target),
                    AnimatorControllerParameterType.Bool));
            if (generated.Count > 0)
                generated.Add((ReturnParameter(r.baseName), AnimatorControllerParameterType.Int));
            return generated;
        }

        /// <summary>
        /// The Ready flag and the per-slot bits behind it, or nothing when the setup does not
        /// ask for it. Local like the request flags: a remote cannot tell the wearer what it
        /// has received, so this whole mechanism is each client reading its own decoder.
        ///
        /// One bit per SLOT rather than per target, because a slot arrives whole — and per
        /// slot rather than per index value, because a clock gives one slot two indices and
        /// either of them proves the values came.
        /// </summary>
        public static List<(string name, AnimatorControllerParameterType type)> ReadyParameters(Request r)
        {
            var generated = new List<(string, AnimatorControllerParameterType)>();
            if (r == null || !r.ready) return generated;
            var slots = BuildSlots(r);
            if (slots.Count == 0) return generated;
            generated.Add((ReadyParameter(r.baseName), AnimatorControllerParameterType.Bool));
            foreach (var slotName in AsyncSyncApplier.SlotNames(slots))
                generated.Add((SeenParameter(r.baseName, slotName),
                    AnimatorControllerParameterType.Bool));
            return generated;
        }

        /// <summary>
        /// The Stale flag and the per-lap bits behind it, or nothing when the setup does not
        /// ask for it. Local, like everything else the remote reads about its own decoding.
        ///
        /// Its own bits rather than <see cref="ReadyParameters"/>'s: those accumulate and
        /// these are cleared every lap, and one set cannot be both. Bools cost nothing on the
        /// wire, which is the only reason that trade is cheap.
        /// </summary>
        public static List<(string name, AnimatorControllerParameterType type)> StaleParameters(Request r)
        {
            var generated = new List<(string, AnimatorControllerParameterType)>();
            if (r == null || !r.stale) return generated;
            var slots = BuildSlots(r);
            if (slots.Count == 0) return generated;
            generated.Add((StaleParameter(r.baseName), AnimatorControllerParameterType.Bool));
            foreach (var slotName in AsyncSyncApplier.SlotNames(slots))
                generated.Add((FreshParameter(r.baseName, slotName),
                    AnimatorControllerParameterType.Bool));
            return generated;
        }

        /// <summary>
        /// The groups the setup actually builds: members restricted to real targets,
        /// deduplicated, and each target left in the FIRST group that claims it — a target can
        /// only be held in one place, and a second claim would have two commits writing it.
        /// A group with fewer than two usable members is dropped: holding one target back
        /// until it arrives is what the decoder does anyway.
        ///
        /// Same "a stale saved entry must not block regeneration" contract as
        /// <see cref="Request.rates"/>, which is why this filters rather than refuses.
        /// </summary>
        public static List<SyncGroup> EffectiveGroups(Request r)
        {
            var groups = new List<SyncGroup>();
            if (r?.groups == null || r.targets == null) return groups;
            var claimed = new HashSet<string>();
            foreach (var group in r.groups)
            {
                if (group == null) continue;
                var kept = new SyncGroup { name = group.name };
                // In cycle order, so the members read as the pass visits them rather than as
                // they were typed — and so the commit driver's entries do too.
                foreach (var name in r.targets)
                    if (group.members != null && group.members.Contains(name)
                        && claimed.Add(name))
                        kept.members.Add(name);
                if (kept.members.Count < 2)
                {
                    // Put the names back: a group too small to build must not eat a member the
                    // next group could still hold.
                    foreach (var name in kept.members) claimed.Remove(name);
                    continue;
                }
                if (string.IsNullOrEmpty(kept.name))
                    kept.name = "Group " + (groups.Count + 1);
                groups.Add(kept);
            }
            return groups;
        }

        /// <summary>Every target held by some group — what the decoder checks to know whether
        /// a value goes to its parameter or to the parameter's shadow.</summary>
        public static HashSet<string> GroupMembers(Request r)
        {
            var members = new HashSet<string>();
            foreach (var group in EffectiveGroups(r))
                foreach (var name in group.members)
                    members.Add(name);
            return members;
        }

        /// <summary>
        /// A shadow and a flag per grouped member. The shadow carries the target's own type —
        /// it stands in for it — and neither is ever synced: the whole mechanism runs on the
        /// receiving client, out of values that already arrived.
        /// </summary>
        public static List<(string name, AnimatorControllerParameterType type)> GroupParameters(Request r)
        {
            var generated = new List<(string, AnimatorControllerParameterType)>();
            if (r?.controller == null) return generated;
            var byName = DbtBuilder.ParametersByName(r.controller);
            foreach (var group in EffectiveGroups(r))
                foreach (var name in group.members)
                {
                    var parameter = byName.Find(name);
                    if (parameter == null) continue;
                    generated.Add((HoldParameter(r.baseName, name), parameter.type));
                    generated.Add((HeldParameter(r.baseName, name),
                        AnimatorControllerParameterType.Bool));
                }
            return generated;
        }

        /// <summary>
        /// The slot whose arrival closes a lap, or -1 when the pass has none. Rates always
        /// leave one; a cycle written by hand need not, and a slot anything can request is no
        /// use as one however rarely the pass sends it.
        /// </summary>
        public static int LapMarkerSlot(Request r)
        {
            var slots = BuildSlots(r);
            if (slots.Count == 0) return -1;
            var requested = new HashSet<int>();
            var requestable = RequestableTargets(r);
            for (int i = 0; i < slots.Count; i++)
                foreach (var name in slots[i].targets)
                    if (requestable.Contains(name)) requested.Add(i);
            return AsyncSyncSchedule.LapMarker(EffectiveSchedule(r, slots), requested);
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
        /// clip from the controller's current associations, and the explicit cycle or grid when
        /// the setup has one — without that last part, adding a sync request to a state would
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
                allowRepeatSteps = config.allowRepeatSteps,
                ready = config.ready,
                stale = config.stale,
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
            // Copied down to the member lists, for the reason the grid is: this request is
            // handed to editors that go on rewriting it, and the saved setup must not move.
            if (config.groups != null)
                foreach (var group in config.groups)
                {
                    if (group == null) continue;
                    var copy = new SyncGroup { name = group.name };
                    if (group.members != null) copy.members.AddRange(group.members);
                    request.groups.Add(copy);
                }
            // Copied down to the step lists: this request is handed to editors that go on
            // rewriting its grid, and the saved setup must not move with them.
            if (config.steps != null)
                foreach (var step in config.steps)
                {
                    var copy = new StepSpec();
                    if (step?.targets != null) copy.targets.AddRange(step.targets);
                    request.steps.Add(copy);
                }
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

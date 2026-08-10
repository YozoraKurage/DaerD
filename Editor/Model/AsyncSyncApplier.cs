using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
// The build moved out of AsyncSyncBuilder unchanged and still speaks its vocabulary;
// importing the facade keeps Request, Slot and the naming / schedule / cost helpers in
// scope, so every statement below reads exactly as it did there.
using static Yozolab.DaerD.AsyncSyncBuilder;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Writes the layer an <see cref="AsyncSyncBuilder.Request"/> describes: the generated
    /// parameters, the local send ring (one state per schedule step, each driving the value
    /// channels and then the index), the request routes that let a raised flag jump the ring
    /// at a step boundary, and the remote Any-State decoder (one state per slot, or per clock
    /// phase of a slot the pass puts beside itself). Finishes by syncing the generated
    /// parameters in the store and saving the setup on the controller so the wizard can
    /// regenerate this same layer later. See <see cref="AsyncSyncBuilder"/> for why the
    /// technique looks like this; everything here happens inside one UndoScope.
    /// </summary>
    static class AsyncSyncApplier
    {
        public static bool Apply(Request r)
        {
            if (Validate(r) != null) return false;
            var controller = r.controller;

            using (new UndoScope("Async Sync"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "Async Sync");

                var generated = EnsureParameters(controller, r);
                var stateMachine = ResolveStateMachine(controller, r);

                var slots = BuildSlots(r);
                var schedule = EffectiveSchedule(r, slots);
                var encoding = ResolveEncoding(r);
                var empty = ResolveOrCreateEmptyClip(controller, r);
                var clock = BuildClock(r, slots, schedule);
                var indexBits = IndexBitNames(r, encoding, clock);

                var sendStates = BuildSendRing(stateMachine, r, slots, schedule, clock, encoding,
                    indexBits, empty);
                var idle = BuildDecoder(stateMachine, r, slots, clock, encoding, indexBits, empty);

                // Entry: locals fall through to the first send slot; remotes branch to Idle.
                stateMachine.defaultState = sendStates[0];
                var entry = stateMachine.AddEntryTransition(idle);
                entry.AddCondition(AnimatorConditionMode.IfNot, 0f, NetworkSyncBuilder.IsLocalParameter);

                SyncGeneratedParameters(r, generated);
                SaveConfig(controller, stateMachine, r);

                EditorUtility.SetDirty(stateMachine);
                EditorUtility.SetDirty(controller);
            }
            return true;
        }

        /// <summary>IsLocal, the synced set and the local request flags. Returns the synced
        /// set, because that is exactly the list the parameter store step below wants.</summary>
        static List<(string name, AnimatorControllerParameterType type)> EnsureParameters(
            AnimatorController controller, Request r)
        {
            DbtBuilder.EnsureParameter(controller, NetworkSyncBuilder.IsLocalParameter,
                AnimatorControllerParameterType.Bool);
            var generated = GeneratedParameters(r);
            foreach (var (name, type) in generated)
                DbtBuilder.EnsureParameter(controller, name, type);
            // The request flags are animator-local machinery: created, never synced.
            foreach (var (name, type) in RequestParameters(r))
                DbtBuilder.EnsureParameter(controller, name, type);
            return generated;
        }

        /// <summary>The state machine this run writes into: the designated layer, emptied
        /// first, or a freshly added one at full weight.</summary>
        static AnimatorStateMachine ResolveStateMachine(AnimatorController controller, Request r)
        {
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
            return stateMachine;
        }

        static AnimationClip ResolveOrCreateEmptyClip(AnimatorController controller, Request r)
        {
            // Motion for the generated states. Zero-length clips are refused: exit times are
            // normalized to the motion, so a length of 0 would make them meaningless.
            var empty = ResolveEmptyClip(r);
            // Nothing designated: create the clip rather than leave every generated state
            // motion-less. A clip that exists but was refused (zero length, explicit or
            // designated) is left alone — that is the user's own clip, and the warning says so.
            if (empty == null && r.assignEmptyClip && r.emptyClip == null
                && GraphFrameData.GetEmptyClip(controller) == null)
                empty = GraphFrameData.EnsureEmptyClip(controller);
            return empty;
        }

        /// <summary>The synced Bools carrying the index, LSB-first — null under Int
        /// encoding, where the index is one parameter. Wide enough for the clock's values
        /// rather than for the slots, which are the same count until a slot repeats.</summary>
        static string[] IndexBitNames(Request r, IndexEncoding encoding, Clock clock)
        {
            string[] indexBits = null;
            if (encoding == IndexEncoding.Bool)
            {
                int bits = NetworkSyncBuilder.BitsRequired(clock.indexValues);
                indexBits = new string[bits];
                for (int i = 0; i < bits; i++)
                    indexBits[i] = BitParameter(r.baseName, i);
            }
            return indexBits;
        }

        /// <summary>Builds the local send cycle and returns its states in schedule order.</summary>
        static List<AnimatorState> BuildSendRing(AnimatorStateMachine stateMachine, Request r,
            List<Slot> slots, List<int> schedule, Clock clock, IndexEncoding encoding,
            string[] indexBits, AnimationClip empty)
        {
            // Local side: the cycle, one state per SCHEDULE step — a priority slot appears
            // several times, and each appearance needs its own state to keep the ring a
            // ring. A motion-less state advances normalized time at one unit per second,
            // so its exit time reads directly as seconds; with the Empty clip filled in,
            // the same dwell has to be expressed in units of that clip.
            float exitTime = empty != null ? r.stepSeconds / empty.length : r.stepSeconds;

            var requestable = RequestableTargets(r);
            var slotOfTarget = new Dictionary<string, int>();
            for (int i = 0; i < slots.Count; i++)
                foreach (var name in slots[i].targets)
                    slotOfTarget[name] = i;

            var slotNames = SlotNames(slots);
            var visits = new Dictionary<int, int>();
            var sendStates = new List<AnimatorState>(schedule.Count);
            var firstSendOfSlot = new Dictionary<int, AnimatorState>();
            for (int k = 0; k < schedule.Count; k++)
            {
                int slotIndex = schedule[k];
                var slot = slots[slotIndex];
                visits.TryGetValue(slotIndex, out int visit);
                visits[slotIndex] = visit + 1;

                var state = stateMachine.AddState(
                    SlotStateName("Send", slotNames[slotIndex], visit),
                    new Vector3(260f, 60f + k * 70f, 0f));
                state.writeDefaultValues = true;
                state.motion = empty;
                sendStates.Add(state);
                if (visit == 0) firstSendOfSlot[slotIndex] = state;

                if (r.skipDrivers) continue;
                var driver = VrcParameterDriver.AddTo(state, "Async Send");
                if (driver == null) continue;
                Undo.RegisterCompleteObjectUndo(driver, "Async Sync");
                VrcParameterDriver.SetLocalOnly(driver, true);
                // Values first, then the index — remotes react to the index change. The index
                // is the slot's, in this step's clock phase: with no clock that is the slot
                // number it always was, and with one it is what makes a repeat a change.
                AddChannelCopies(driver, r, slot, toChannels: true);
                int index = clock.Index(slotIndex, clock.stepPhases[k]);
                if (encoding == IndexEncoding.Int)
                    VrcParameterDriver.AddSetEntry(driver, IndexParameter(r.baseName), index);
                else
                    for (int bit = 0; bit < indexBits.Length; bit++)
                        VrcParameterDriver.AddSetEntry(driver, indexBits[bit],
                            ((index >> bit) & 1) == 1 ? 1f : 0f);
                // Entering this state IS the service: the fresh value was just copied, so
                // any pending request for this slot's targets is satisfied — clear it.
                foreach (var name in slot.targets)
                    if (requestable.Contains(name))
                        VrcParameterDriver.AddSetEntry(driver,
                            RequestParameter(r.baseName, name), 0f);
            }
            // Sync requests: from every step, a raised flag redirects the cycle to the
            // requested slot at the step boundary. These are added BEFORE the ring
            // transition, so they win when their flag is up; the same exit time keeps the
            // current slot's dwell (the values just sent still need their sync window).
            // No route targets the state's own slot: back-to-back sends of one index are
            // invisible to the decoder (canTransitionToSelf is off), and the routes of the
            // steps that follow — one per OTHER slot — pick the still-raised flag up. A clock
            // could carry such a jump, but only onto a send state of the opposite phase, and a
            // slot the pass visits once has none; the flag is cleared either way, because the
            // slot's own driver clears it every time it sends.
            for (int k = 0; k < sendStates.Count; k++)
                foreach (var name in requestable)
                {
                    int slotIndex = slotOfTarget[name];
                    if (slotIndex == schedule[k]) continue;
                    var transition = sendStates[k].AddTransition(firstSendOfSlot[slotIndex]);
                    transition.hasExitTime = true;
                    transition.exitTime = exitTime;
                    transition.hasFixedDuration = true;
                    transition.duration = 0f;
                    transition.AddCondition(AnimatorConditionMode.If, 0f,
                        RequestParameter(r.baseName, name));
                    EditorUtility.SetDirty(transition);
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
            return sendStates;
        }

        /// <summary>Builds the remote side and returns its Idle state — the one the entry
        /// transition branches to.</summary>
        static AnimatorState BuildDecoder(AnimatorStateMachine stateMachine, Request r,
            List<Slot> slots, Clock clock, IndexEncoding encoding, string[] indexBits,
            AnimationClip empty)
        {
            // Remote side: Any-State decoder — one state per SLOT (revisits reuse it), and one
            // per PHASE of a slot the pass repeats. The two states of such a slot copy exactly
            // the same channels, and that redundancy IS the mechanism: the route is refused
            // when it would re-enter the state the machine is already in, so a slot sending
            // twice running needs somewhere else to land.
            var idle = stateMachine.AddState("Remote Idle", new Vector3(620f, 60f, 0f));
            idle.writeDefaultValues = true;
            idle.motion = empty;
            var slotNames = SlotNames(slots);
            int row = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                for (int phase = 0; phase < clock.slotPhases[i]; phase++)
                {
                    // The second phase takes the "(2)" a second visit would take on the send
                    // side; a slot only ever had one Recv state, so nothing can collide.
                    var state = stateMachine.AddState(
                        SlotStateName("Recv", slotNames[i], phase),
                        new Vector3(620f, 130f + row++ * 70f, 0f));
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

                    int index = clock.Index(i, phase);
                    var transition = stateMachine.AddAnyStateTransition(state);
                    transition.canTransitionToSelf = false;
                    transition.hasExitTime = false;
                    transition.hasFixedDuration = true;
                    transition.duration = 0f;
                    transition.AddCondition(AnimatorConditionMode.IfNot, 0f,
                        NetworkSyncBuilder.IsLocalParameter);
                    if (encoding == IndexEncoding.Int)
                        transition.AddCondition(AnimatorConditionMode.Equals, index,
                            IndexParameter(r.baseName));
                    else
                        for (int bit = 0; bit < indexBits.Length; bit++)
                            transition.AddCondition(((index >> bit) & 1) == 1
                                    ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot,
                                0f, indexBits[bit]);
                    EditorUtility.SetDirty(transition);
                }
            }
            return idle;
        }

        static void SyncGeneratedParameters(Request r,
            List<(string name, AnimatorControllerParameterType type)> generated)
        {
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
        }

        static void SaveConfig(AnimatorController controller,
            AnimatorStateMachine stateMachine, Request r)
        {
            // Remember the setup with the controller so the wizard can re-open and
            // regenerate this layer later (same pattern as the DBT layer choice).
            GraphFrameData.SaveAsyncSync(controller, new GraphFrameData.AsyncSyncConfig
            {
                layer = stateMachine,
                baseName = r.baseName,
                encoding = (int)r.encoding,
                stepSeconds = r.stepSeconds,
                floatChannels = r.floatChannels,
                boolChannels = r.boolChannels,
                targets = new List<string>(r.targets),
                rates = GraphFrameData.AsyncSyncConfig.ToRateEntries(r.rates),
                requests = RequestableTargets(r),
                schedule = r.scheduleOverride != null
                    ? new List<string>(r.scheduleOverride) : new List<string>(),
                slotBreaks = r.slotBreaks != null
                    ? new List<string>(r.slotBreaks) : new List<string>(),
                steps = CopySteps(r.steps),
                allowRepeatSteps = r.allowRepeatSteps,
            });
        }

        /// <summary>A grid copied down to its step lists. The shallow copy the other lists get
        /// would leave the saved setup sharing step objects with the request that built it, and
        /// the wizard goes on editing that request after applying.</summary>
        static List<GraphFrameData.AsyncSyncConfig.StepSpec> CopySteps(
            List<GraphFrameData.AsyncSyncConfig.StepSpec> steps)
        {
            var copy = new List<GraphFrameData.AsyncSyncConfig.StepSpec>();
            if (steps == null) return copy;
            foreach (var step in steps)
            {
                var clone = new GraphFrameData.AsyncSyncConfig.StepSpec();
                if (step?.targets != null) clone.targets.AddRange(step.targets);
                copy.Add(clone);
            }
            return copy;
        }

        /// <summary>
        /// The states <see cref="Apply"/> would give this request: the send ring in schedule
        /// order, the decoder's Idle, and one Recv per slot per clock phase. Kept beside the
        /// code that names them so the two cannot drift — the exporter compares a live layer
        /// against this to decide whether it may be written back as an AsyncSync call, and a
        /// name it got wrong would quietly rewrite a layer someone had edited by hand.
        /// </summary>
        internal static List<string> ExpectedStateNames(Request r)
        {
            var names = new List<string>();
            if (r == null) return names;
            var slots = BuildSlots(r);
            if (slots.Count == 0) return names;

            var slotNames = SlotNames(slots);
            var schedule = EffectiveSchedule(r, slots);
            var clock = BuildClock(r, slots, schedule);
            var visits = new Dictionary<int, int>();
            foreach (var slotIndex in schedule)
            {
                visits.TryGetValue(slotIndex, out int visit);
                visits[slotIndex] = visit + 1;
                names.Add(SlotStateName("Send", slotNames[slotIndex], visit));
            }
            names.Add("Remote Idle");
            for (int i = 0; i < slots.Count; i++)
                for (int phase = 0; phase < clock.slotPhases[i]; phase++)
                    names.Add(SlotStateName("Recv", slotNames[i], phase));
            return names;
        }

        /// <summary>
        /// What each slot is called: its first target, and "+2" for the ones riding with it.
        /// That is unique as long as the slots partition the targets, which the automatic
        /// batching's do — but a grid's slots overlap, and {A,B} beside {A,C} would leave two
        /// states of one machine answering to "Send A +1". Later collisions take a "#n", so a
        /// setup whose slots do partition (every one built before grids existed) keeps the
        /// names it has, and the export can still recognise it.
        /// </summary>
        static List<string> SlotNames(List<Slot> slots)
        {
            var names = new List<string>();
            var taken = new HashSet<string>();
            foreach (var slot in slots)
            {
                string stem = DbtBuilder.Sanitize(slot.targets[0]);
                if (slot.targets.Count > 1) stem += " +" + (slot.targets.Count - 1);
                string name = stem;
                // Terminates: the taken set is finite and every candidate name is distinct.
                for (int n = 2; !taken.Add(name); n++) name = stem + " #" + n;
                names.Add(name);
            }
            return names;
        }

        /// <summary>One state's name: the slot's, behind "Send" / "Recv", with "(2)" on repeat
        /// visits so the ring's several states for one slot stay apart.</summary>
        static string SlotStateName(string prefix, string slotName, int visit) =>
            prefix + " " + slotName + (visit > 0 ? " (" + (visit + 1) + ")" : string.Empty);

        /// <summary>
        /// Adds the copy entries for one slot: each batched Float or Bool target pairs with the
        /// channel at its own position AMONG THE TARGETS OF ITS OWN TYPE, and an Int rides the
        /// type's single channel. The position in the slot would do while a slot held one type
        /// only; a slot that mixes them has each type count from 0 of its own, or a Bool sitting
        /// second behind a Float would be copied into Bool channel 1 — a parameter nothing
        /// generates. Both directions go through here, so the numbering cannot disagree
        /// between the send ring and the decoder.
        /// </summary>
        internal static void AddChannelCopies(StateMachineBehaviour driver, Request r, Slot slot,
            bool toChannels)
        {
            int floats = 0, bools = 0;
            foreach (var target in slot.targets)
            {
                var type = DbtBuilder.FindParameter(r.controller, target).type;
                string channel = type == AnimatorControllerParameterType.Float
                    ? FloatChannelParameter(r.baseName, floats++)
                    : type == AnimatorControllerParameterType.Bool
                        ? BoolChannelParameter(r.baseName, bools++)
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
    }
}

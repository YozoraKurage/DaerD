using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
// The build moved out of AsyncSyncBuilder unchanged and still speaks its vocabulary;
// importing the facade keeps Request, Slot and the naming / schedule / cost helpers in
// scope, so every statement below reads exactly as it did there.
using static Yozolab.DaerD.AsyncSyncBuilder;
using SyncGroup = Yozolab.DaerD.GraphFrameData.AsyncSyncConfig.SyncGroup;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Writes the layer an <see cref="AsyncSyncBuilder.Request"/> describes: the generated
    /// parameters, the local send ring (one state per schedule step, each driving the value
    /// channels and then the index), the detour states a raised request flag takes at a step
    /// boundary before handing the ring back its place, and the remote Any-State decoder (one
    /// state per slot, or per clock
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

                // Read before anything is saved over it: it is what says whether a previous
                // run left a Ready layer, which this one either regenerates or takes away.
                var previous = FindConfig(controller, stateMachine);

                var sendStates = BuildSendRing(stateMachine, r, slots, schedule, clock, encoding,
                    indexBits, empty);
                var idle = BuildDecoder(stateMachine, r, slots, clock, encoding, indexBits, empty);

                // Entry: locals fall through to the first send slot; remotes branch to Idle.
                stateMachine.defaultState = sendStates[0];
                var entry = stateMachine.AddEntryTransition(idle);
                entry.AddCondition(AnimatorConditionMode.IfNot, 0f, NetworkSyncBuilder.IsLocalParameter);

                var readyLayer = BuildReadyLayer(controller, r, slots, previous, empty);
                var staleLayer = BuildStaleLayer(controller, r, slots, clock, encoding,
                    indexBits, previous, empty);
                var groups = BuildGroupLayers(controller, r, previous, empty);

                SyncGeneratedParameters(r, generated);
                SaveConfig(controller, stateMachine, readyLayer, staleLayer, groups, r);

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
            // Ready and its per-slot bits, on the same terms and for the same reason.
            foreach (var (name, type) in ReadyParameters(r))
                DbtBuilder.EnsureParameter(controller, name, type);
            foreach (var (name, type) in StaleParameters(r))
                DbtBuilder.EnsureParameter(controller, name, type);
            // The shadows and their flags, on the same terms: created, never synced.
            foreach (var (name, type) in GroupParameters(r))
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

                if (r.skipDrivers) continue;
                var driver = AddSendDriver(state, r, slot, requestable, clock, encoding,
                    indexBits, clock.Index(slotIndex, clock.stepPhases[k]));
                // Where a detour started, so the request state can put the ring back. Written
                // by the ring and never by a detour, which is what makes it survive one.
                if (driver != null && requestable.Count > 0)
                    VrcParameterDriver.AddSetEntry(driver, ReturnParameter(r.baseName), k);
            }
            BuildDetours(stateMachine, r, slots, schedule, clock, encoding, indexBits, empty,
                exitTime, requestable, slotOfTarget, slotNames, sendStates);

            for (int k = 0; k < sendStates.Count; k++)
                Step(sendStates[k], sendStates[(k + 1) % sendStates.Count], exitTime);
            return sendStates;
        }

        /// <summary>
        /// The request detours: one state per slot anything can ask for, reached from the steps
        /// <see cref="AsyncSyncSchedule.RequestOrigins"/> allows and leaving again for the step
        /// after the one it was reached from.
        ///
        /// The detours are wired BEFORE the ring transition on each send state, so a raised
        /// flag wins over the plain next step; they carry the ring's own exit time, so the step
        /// that was running still spends its full dwell. Several requests on one boundary are
        /// tried in cycle order and the losers keep their flags for the next boundary.
        ///
        /// A detour state carries no request routes itself. That is the whole starvation
        /// guarantee: two flags held up can only ever produce detour, step, detour, step, so
        /// the ring advances on every other step and every slot still comes around — at worst
        /// in twice the nominal pass.
        /// </summary>
        static void BuildDetours(AnimatorStateMachine stateMachine, Request r, List<Slot> slots,
            List<int> schedule, Clock clock, IndexEncoding encoding, string[] indexBits,
            AnimationClip empty, float exitTime, List<string> requestable,
            Dictionary<string, int> slotOfTarget, List<string> slotNames,
            List<AnimatorState> sendStates)
        {
            if (requestable.Count == 0) return;

            // One detour per requested SLOT, not per target: a slot goes out whole, so two
            // requestable targets riding together are served by the same extra step.
            var detourOfSlot = new Dictionary<int, AnimatorState>();
            var originsOfSlot = new Dictionary<int, List<int>>();
            int row = 0;
            foreach (var name in requestable)
            {
                int slotIndex = slotOfTarget[name];
                if (detourOfSlot.ContainsKey(slotIndex)) continue;
                var origins = AsyncSyncSchedule.RequestOrigins(schedule, clock, slotIndex);
                // Nowhere to be asked from — a slot that occupies every other step of the pass
                // is already as fresh as a detour could make it. Warnings says so; here it
                // simply means there is nothing to build.
                if (origins.Count == 0) continue;
                originsOfSlot[slotIndex] = origins;

                var state = stateMachine.AddState(
                    RequestStateName(slotNames[slotIndex]),
                    new Vector3(440f, 60f + row++ * 70f, 0f));
                state.writeDefaultValues = true;
                state.motion = empty;
                detourOfSlot[slotIndex] = state;

                if (r.skipDrivers) continue;
                // Same payload as the slot's ordinary step, in the phase the origins were
                // computed against — and no Return entry, because the ring's place is exactly
                // what this state is carrying back.
                AddSendDriver(state, r, slots[slotIndex], requestable, clock, encoding, indexBits,
                    clock.Index(slotIndex, AsyncSyncSchedule.RequestPhase));
            }

            foreach (var name in requestable)
            {
                int slotIndex = slotOfTarget[name];
                if (!detourOfSlot.TryGetValue(slotIndex, out var detour)) continue;
                foreach (int k in originsOfSlot[slotIndex])
                    Step(sendStates[k], detour, exitTime)
                        .AddCondition(AnimatorConditionMode.If, 0f,
                            RequestParameter(r.baseName, name));
            }

            foreach (var pair in detourOfSlot)
            {
                var origins = originsOfSlot[pair.Key];
                foreach (int k in origins)
                    Step(pair.Value, sendStates[(k + 1) % sendStates.Count], exitTime)
                        .AddCondition(AnimatorConditionMode.Equals, k, ReturnParameter(r.baseName));
                // Last, so it only answers when no return matched. It never should — every
                // origin has a route above — but a send ring that wedges stops the whole
                // avatar's sync, and one unconditional transition is a cheap floor under that.
                Step(pair.Value, sendStates[(origins[0] + 1) % sendStates.Count], exitTime);
            }
        }

        /// <summary>One boundary of the cycle: full dwell, then switch with no blend.</summary>
        static AnimatorStateTransition Step(AnimatorState from, AnimatorState to, float exitTime)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = true;
            transition.exitTime = exitTime;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            EditorUtility.SetDirty(transition);
            return transition;
        }

        /// <summary>
        /// What a step puts on the wire: the slot's values into the channels, then the index —
        /// remotes react to the index change, so the values have to be there first. Entering
        /// the state IS the service, so any pending request for the slot's targets is satisfied
        /// and its flag comes down. Shared by the ring and the detours, which send the same
        /// payload and differ only in the index they write and where they go next.
        /// </summary>
        static StateMachineBehaviour AddSendDriver(AnimatorState state, Request r, Slot slot,
            List<string> requestable, Clock clock, IndexEncoding encoding, string[] indexBits,
            int index)
        {
            var driver = VrcParameterDriver.AddTo(state, "Async Send");
            if (driver == null) return null;
            Undo.RegisterCompleteObjectUndo(driver, "Async Sync");
            VrcParameterDriver.SetLocalOnly(driver, true);
            AddChannelCopies(driver, r, slot, toChannels: true);
            if (encoding == IndexEncoding.Int)
                VrcParameterDriver.AddSetEntry(driver, IndexParameter(r.baseName), index);
            else
                for (int bit = 0; bit < indexBits.Length; bit++)
                    VrcParameterDriver.AddSetEntry(driver, indexBits[bit],
                        ((index >> bit) & 1) == 1 ? 1f : 0f);
            foreach (var name in slot.targets)
                if (requestable.Contains(name))
                    VrcParameterDriver.AddSetEntry(driver,
                        RequestParameter(r.baseName, name), 0f);
            return driver;
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
                            // This client has decoded the slot at least once, and nothing
                            // ever clears it — see AsyncSyncBuilder.ReadyParameters for why
                            // the bits accumulate instead of being a per-pass reading.
                            if (r.ready)
                                VrcParameterDriver.AddSetEntry(driver,
                                    SeenParameter(r.baseName, slotNames[i]), 1f);
                            // The same arrival, read as "this lap" rather than "ever": the
                            // Stale watcher puts these down once a lap. See BuildStaleLayer.
                            if (r.stale)
                                VrcParameterDriver.AddSetEntry(driver,
                                    FreshParameter(r.baseName, slotNames[i]), 1f);
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

        /// <summary>
        /// The Ready watcher: a layer of its own, because it has to be evaluated while the
        /// sync layer is busy being a ring. Three states and no drivers on the remote path —
        /// the bits it reads are set by the decoder, and the only thing here is the moment
        /// they are all up.
        ///
        /// The bits accumulate and are never cleared, so the transition asks "has every slot
        /// arrived by now", not "did every slot arrive this pass". That is what makes the flag
        /// a latch with no window to size and no clearing for the check to race against, and
        /// it means a client that starts decoding mid-pass is Ready one pass later rather than
        /// two. Late is the only safe direction to be wrong in, and this is never early.
        ///
        /// Returns the layer's state machine so the saved setup can own it — or null when the
        /// setup does not ask for the flag, in which case the layer a previous run left is
        /// taken away rather than left holding whatever it last latched.
        /// </summary>
        static AnimatorStateMachine BuildReadyLayer(AnimatorController controller, Request r,
            List<Slot> slots, GraphFrameData.AsyncSyncConfig previous, AnimationClip empty)
        {
            string wanted = ReadyLayerName(MainLayerName(r));
            var existing = ResolveExistingLayer(controller,
                previous != null ? previous.readyLayer : null, wanted);
            if (!r.ready || slots.Count == 0)
            {
                RemoveLayer(controller, existing);
                return null;
            }
            var machine = ResolveWatcherLayer(controller, existing, wanted);

            var watch = AddReadyState(machine, "Watch", empty, 260f, 60f);
            var ready = AddReadyState(machine, "Ready", empty, 440f, 60f);
            var local = AddReadyState(machine, "Local", empty, 260f, 140f);

            // Everyone starts watching, and the wearer leaves on their first frame: their own
            // values were never anywhere else, so there is nothing for them to wait for.
            //
            // A route out of the default state rather than a condition on Entry. A conditional
            // entry transition is evaluated once, at a moment nothing here controls, and a run
            // of this layer shows it never being taken at all — the wearer would sit in Watch
            // waiting for values that are already theirs. The default state is the one place a
            // layer is guaranteed to begin.
            machine.defaultState = watch;
            var mine = Instant(watch, local);
            mine.AddCondition(AnimatorConditionMode.If, 0f, NetworkSyncBuilder.IsLocalParameter);

            var transition = Instant(watch, ready);
            foreach (var slotName in SlotNames(slots))
                transition.AddCondition(AnimatorConditionMode.If, 0f,
                    SeenParameter(r.baseName, slotName));

            // Ready and Local have no way out, which is the latch: whatever the wire does
            // afterwards, a client that has once seen everything has seen everything.
            if (!r.skipDrivers)
            {
                AddReadyDriver(ready, r);
                AddReadyDriver(local, r);
            }
            EditorUtility.SetDirty(machine);
            return machine;
        }

        /// <summary>The name the cycle's own layer answers to, which the watchers derive
        /// theirs from.</summary>
        static string MainLayerName(Request r) =>
            string.IsNullOrEmpty(r.layerName) ? r.baseName : r.layerName;

        /// <summary>
        /// A watcher layer a previous run left, or null. The saved setup's, and failing that
        /// the layer answering to the name this run would create — a fresh checkout or an
        /// in-memory controller has no saved setup to ask, and the alternative is stacking a
        /// second watcher on every run. Same fallback the recipe makes for the cycle's layer.
        /// </summary>
        static AnimatorStateMachine ResolveExistingLayer(AnimatorController controller,
            AnimatorStateMachine saved, string expected)
        {
            if (saved != null && LayerIndexOf(controller, saved) >= 0) return saved;
            foreach (var layer in controller.layers)
                if (layer.name == expected)
                    return layer.stateMachine;
            return null;
        }

        /// <summary>The watcher's layer, emptied for regeneration or freshly added.</summary>
        static AnimatorStateMachine ResolveWatcherLayer(AnimatorController controller,
            AnimatorStateMachine existing, string layerName)
        {
            if (existing != null)
            {
                Undo.RegisterCompleteObjectUndo(existing, "Async Sync");
                ClearStateMachine(existing);
                return existing;
            }
            controller.AddLayer(DbtBuilder.UniqueLayerName(controller, layerName));
            var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1f;
            controller.layers = layers;
            var machine = layers[layers.Length - 1].stateMachine;
            Undo.RegisterCompleteObjectUndo(machine, "Async Sync");
            return machine;
        }

        /// <summary>A transition that fires the moment its conditions hold, with no blend —
        /// what every conditioned route inside a watcher is.</summary>
        static AnimatorStateTransition Instant(AnimatorState from, AnimatorState to)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            EditorUtility.SetDirty(transition);
            return transition;
        }

        /// <summary>
        /// A route with nothing to wait for: the else of a judgement, and the way back out of a
        /// state whose whole job was the driver that ran on the way in.
        ///
        /// It carries an exit time of zero rather than no exit time at all. A transition with
        /// neither a condition nor an exit time is never taken — the state machine sits in the
        /// state forever, which reads in a run as a watcher that judged once and then went
        /// deaf, and which nothing about the layer's SHAPE would show.
        /// </summary>
        static AnimatorStateTransition Immediate(AnimatorState from, AnimatorState to)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = true;
            transition.exitTime = 0f;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            EditorUtility.SetDirty(transition);
            return transition;
        }

        /// <summary>"The index is this value", as conditions on one transition.</summary>
        static void AddIndexEquals(AnimatorStateTransition transition, Request r,
            IndexEncoding encoding, string[] indexBits, int index)
        {
            if (encoding == IndexEncoding.Int)
            {
                transition.AddCondition(AnimatorConditionMode.Equals, index,
                    IndexParameter(r.baseName));
                return;
            }
            for (int bit = 0; bit < indexBits.Length; bit++)
                transition.AddCondition(((index >> bit) & 1) == 1
                        ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot,
                    0f, indexBits[bit]);
        }

        /// <summary>"The index is no longer this value", as routes rather than conditions: an
        /// Int says it in one, and a Bool index differs exactly when SOME bit does, which is a
        /// disjunction — and the conditions on one transition are all ANDed.</summary>
        static void AddIndexLeaves(AnimatorState from, AnimatorState to, Request r,
            IndexEncoding encoding, string[] indexBits, int index)
        {
            if (encoding == IndexEncoding.Int)
            {
                Instant(from, to).AddCondition(AnimatorConditionMode.NotEqual, index,
                    IndexParameter(r.baseName));
                return;
            }
            for (int bit = 0; bit < indexBits.Length; bit++)
                Instant(from, to).AddCondition(((index >> bit) & 1) == 1
                        ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If,
                    0f, indexBits[bit]);
        }

        /// <summary>
        /// The Stale watcher: judged once a lap, at the moment the marker slot arrives.
        ///
        /// Watching a slot the pass sends exactly once is what spares this a timer — there is
        /// no window to size, no margin to guess, and a lap stretched by a request cannot make
        /// it wrong, because the measure is the lap itself rather than a number of seconds.
        ///
        /// The bits are cleared HERE and not by the decoder, which is what keeps the reading
        /// and the clearing in one layer with nothing to race: the marker's own step has a
        /// full dwell still to run when the judgement lands, so the next slot is hundreds of
        /// frames away from the clear. The marker is left out of the check for the same
        /// reason from the other side — being here IS its arrival, so whether its own bit is
        /// up yet this frame must not matter.
        ///
        /// Not an Any-State route: the index sits on the marker for the whole dwell, and an
        /// Any-State transition would re-enter the judgement on every frame of it. The
        /// watcher arms itself again only once the index has moved on.
        /// </summary>
        static AnimatorStateMachine BuildStaleLayer(AnimatorController controller, Request r,
            List<Slot> slots, Clock clock, IndexEncoding encoding, string[] indexBits,
            GraphFrameData.AsyncSyncConfig previous, AnimationClip empty)
        {
            string wanted = StaleLayerName(MainLayerName(r));
            var existing = ResolveExistingLayer(controller,
                previous != null ? previous.staleLayer : null, wanted);
            int marker = LapMarkerSlot(r);
            if (!r.stale || marker < 0)
            {
                RemoveLayer(controller, existing);
                return null;
            }
            var machine = ResolveWatcherLayer(controller, existing, wanted);

            var idle = AddReadyState(machine, "Idle", empty, 260f, 60f);
            var judge = AddReadyState(machine, "Judge", empty, 440f, 60f);
            var dirty = AddReadyState(machine, "Dirty", empty, 620f, 60f);
            var clean = AddReadyState(machine, "Clean", empty, 620f, 140f);
            machine.defaultState = idle;

            // A slot the pass sends once, so this fires once a lap. Remotes only: the wearer
            // has nothing to be behind on, and the flag stays at its default for them.
            var arm = Instant(idle, judge);
            arm.AddCondition(AnimatorConditionMode.IfNot, 0f, NetworkSyncBuilder.IsLocalParameter);
            AddIndexEquals(arm, r, encoding, indexBits, clock.Index(marker, 0));

            // One route per slot that did not arrive, then the fall-through. Conditions on one
            // transition are ANDed, so "any of them is missing" has to be spread over routes —
            // and they are tried in order, which makes the last one the else.
            var slotNames = SlotNames(slots);
            for (int i = 0; i < slots.Count; i++)
            {
                if (i == marker) continue;
                Instant(judge, dirty).AddCondition(AnimatorConditionMode.IfNot, 0f,
                    FreshParameter(r.baseName, slotNames[i]));
            }
            Immediate(judge, clean);

            AddIndexLeaves(dirty, idle, r, encoding, indexBits, clock.Index(marker, 0));
            AddIndexLeaves(clean, idle, r, encoding, indexBits, clock.Index(marker, 0));

            if (!r.skipDrivers)
            {
                AddJudgementDriver(dirty, r, slotNames, 1f);
                AddJudgementDriver(clean, r, slotNames, 0f);
            }
            EditorUtility.SetDirty(machine);
            return machine;
        }

        /// <summary>
        /// One commit layer per group: it waits for every member's arrival flag, copies the
        /// whole set out of the shadows into the real parameters, and puts the flags down
        /// again. Two states, because a driver cannot ask a question — the waiting has to be
        /// a transition, and what it guards has to be somewhere to enter.
        ///
        /// The copies and the clears are entries of ONE driver, so they run in one frame: the
        /// simultaneity is structural rather than a matter of timing, which is the whole point
        /// of the exercise. And the guard is "every member has arrived" rather than "the last
        /// member just did", so a lap that lost one of them commits nothing and leaves the
        /// remote on the last complete set — a stale whole rather than a torn one.
        ///
        /// Nothing here runs on the wearer: the decoder that raises the flags is behind the
        /// cycle layer's remote branch, so the flags never come up and the real parameters are
        /// never written from the shadows.
        /// </summary>
        static List<SyncGroup> BuildGroupLayers(AnimatorController controller, Request r,
            GraphFrameData.AsyncSyncConfig previous, AnimationClip empty)
        {
            var built = new List<SyncGroup>();
            string main = MainLayerName(r);
            foreach (var group in EffectiveGroups(r))
            {
                string wanted = GroupLayerName(main, group.name);
                var machine = ResolveWatcherLayer(controller,
                    ResolveExistingLayer(controller, PreviousGroupLayer(previous, group.name),
                        wanted),
                    wanted);

                var idle = AddReadyState(machine, "Idle", empty, 260f, 60f);
                var commit = AddReadyState(machine, "Commit", empty, 440f, 60f);
                machine.defaultState = idle;

                var arm = Instant(idle, commit);
                foreach (var name in group.members)
                    arm.AddCondition(AnimatorConditionMode.If, 0f,
                        HeldParameter(r.baseName, name));
                // Straight back: the flags are down by the time this is evaluated, so the
                // guard above cannot fire again until the next member arrives.
                Immediate(commit, idle);

                if (!r.skipDrivers) AddCommitDriver(commit, r, group);
                EditorUtility.SetDirty(machine);

                var record = new SyncGroup { name = group.name, layer = machine };
                record.members.AddRange(group.members);
                built.Add(record);
            }
            RemoveRetiredGroupLayers(controller, r, previous, built, main);
            return built;
        }

        /// <summary>Copies the set across and opens the next round. One driver, so one
        /// frame — see <see cref="BuildGroupLayers"/>.</summary>
        static void AddCommitDriver(AnimatorState state, Request r, SyncGroup group)
        {
            var driver = VrcParameterDriver.AddTo(state, "Async Group");
            if (driver == null) return;
            Undo.RegisterCompleteObjectUndo(driver, "Async Sync");
            VrcParameterDriver.SetLocalOnly(driver, false);
            foreach (var name in group.members)
                VrcParameterDriver.AddCopyEntry(driver, HoldParameter(r.baseName, name), name);
            foreach (var name in group.members)
                VrcParameterDriver.AddSetEntry(driver, HeldParameter(r.baseName, name), 0f);
        }

        static AnimatorStateMachine PreviousGroupLayer(GraphFrameData.AsyncSyncConfig previous,
            string name)
        {
            if (previous?.groups == null) return null;
            foreach (var group in previous.groups)
                if (group != null && group.name == name)
                    return group.layer;
            return null;
        }

        /// <summary>
        /// Takes away the layers of groups this run no longer builds. From the saved setup,
        /// which knows their layers, and also by name from the request's own list — a group
        /// emptied down to one member is gone from <see cref="AsyncSyncBuilder.EffectiveGroups"/>
        /// but still named there, and on a controller with no saved setup to read that name is
        /// the only thing left pointing at the layer.
        /// </summary>
        static void RemoveRetiredGroupLayers(AnimatorController controller, Request r,
            GraphFrameData.AsyncSyncConfig previous, List<SyncGroup> built, string main)
        {
            bool Kept(AnimatorStateMachine machine)
            {
                foreach (var record in built)
                    if (record.layer == machine) return true;
                return false;
            }

            if (previous?.groups != null)
                foreach (var group in previous.groups)
                    if (group?.layer != null && !Kept(group.layer))
                        RemoveLayer(controller, group.layer);

            if (r.groups == null) return;
            foreach (var group in r.groups)
            {
                if (group == null || string.IsNullOrEmpty(group.name)) continue;
                bool still = false;
                foreach (var record in built)
                    if (record.name == group.name) still = true;
                if (still) continue;
                var stray = ResolveExistingLayer(controller, null,
                    GroupLayerName(main, group.name));
                if (stray != null && !Kept(stray)) RemoveLayer(controller, stray);
            }
        }

        /// <summary>Says what the lap was, then opens the next one. Not localOnly: every
        /// client judges its own decoding, exactly like the Ready watcher.</summary>
        static void AddJudgementDriver(AnimatorState state, Request r, List<string> slotNames,
            float verdict)
        {
            var driver = VrcParameterDriver.AddTo(state, "Async Stale");
            if (driver == null) return;
            Undo.RegisterCompleteObjectUndo(driver, "Async Sync");
            VrcParameterDriver.SetLocalOnly(driver, false);
            VrcParameterDriver.AddSetEntry(driver, StaleParameter(r.baseName), verdict);
            foreach (var slotName in slotNames)
                VrcParameterDriver.AddSetEntry(driver,
                    FreshParameter(r.baseName, slotName), 0f);
        }

        static AnimatorState AddReadyState(AnimatorStateMachine machine, string name,
            AnimationClip empty, float x, float y)
        {
            var state = machine.AddState(name, new Vector3(x, y, 0f));
            state.writeDefaultValues = true;
            state.motion = empty;
            return state;
        }

        /// <summary>Raises the flag. Not localOnly: this runs on every client's copy of the
        /// avatar, which is the whole point — each of them is answering for itself.</summary>
        static void AddReadyDriver(AnimatorState state, Request r)
        {
            var driver = VrcParameterDriver.AddTo(state, "Async Ready");
            if (driver == null) return;
            Undo.RegisterCompleteObjectUndo(driver, "Async Sync");
            VrcParameterDriver.SetLocalOnly(driver, false);
            VrcParameterDriver.AddSetEntry(driver, ReadyParameter(r.baseName), 1f);
        }

        /// <summary>The saved setup that owns this layer, matched by reference rather than by
        /// base name — the wizard can rename a setup, and the layer it is regenerating is
        /// still the one whose Ready layer this run inherits.</summary>
        static GraphFrameData.AsyncSyncConfig FindConfig(AnimatorController controller,
            AnimatorStateMachine machine)
        {
            foreach (var config in GraphFrameData.GetAsyncSyncs(controller))
                if (config.layer == machine)
                    return config;
            return null;
        }

        static int LayerIndexOf(AnimatorController controller, AnimatorStateMachine machine)
        {
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].stateMachine == machine)
                    return i;
            return -1;
        }

        static void RemoveLayer(AnimatorController controller, AnimatorStateMachine machine)
        {
            int index = machine != null ? LayerIndexOf(controller, machine) : -1;
            if (index >= 0) controller.RemoveLayer(index);
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
            AnimatorStateMachine stateMachine, AnimatorStateMachine readyLayer,
            AnimatorStateMachine staleLayer, List<SyncGroup> groups, Request r)
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
                ready = r.ready,
                readyLayer = readyLayer,
                stale = r.stale,
                staleLayer = staleLayer,
                groups = groups,
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
            // In the order BuildDetours creates them: cycle order, first mention of a slot,
            // and only the slots that have somewhere to be requested from.
            var detoured = new HashSet<int>();
            var slotOfTarget = new Dictionary<string, int>();
            for (int i = 0; i < slots.Count; i++)
                foreach (var target in slots[i].targets)
                    slotOfTarget[target] = i;
            foreach (var target in RequestableTargets(r))
            {
                if (!slotOfTarget.TryGetValue(target, out int slotIndex)) continue;
                if (!detoured.Add(slotIndex)) continue;
                if (AsyncSyncSchedule.RequestOrigins(schedule, clock, slotIndex).Count == 0) continue;
                names.Add(RequestStateName(slotNames[slotIndex]));
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
        internal static List<string> SlotNames(List<Slot> slots)
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

        /// <summary>The detour state's name. "(req)" rather than a visit number: it is not one
        /// of the pass's steps, and a slot has at most one of these however often it is sent.</summary>
        static string RequestStateName(string slotName) => "Send " + slotName + " (req)";

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
            // Only the arriving direction is diverted. The wearer's own parameter is what the
            // cycle reads, group or no group — the holding is for values that came off the
            // wire, and there is nothing to hold on the side that already has them.
            var held = toChannels ? null : GroupMembers(r);
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
                else if (held.Contains(target))
                    VrcParameterDriver.AddCopyEntry(driver, channel,
                        HoldParameter(r.baseName, target));
                else
                    VrcParameterDriver.AddCopyEntry(driver, channel, target);
            }
            // Raised after the values are in their shadows, so the commit this may complete
            // never runs on a Hold that has not been written yet.
            if (held != null)
                foreach (var target in slot.targets)
                    if (held.Contains(target))
                        VrcParameterDriver.AddSetEntry(driver,
                            HeldParameter(r.baseName, target), 1f);
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

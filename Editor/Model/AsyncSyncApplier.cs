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
                var idle = BuildDecoder(stateMachine, r, slots, schedule, clock, encoding,
                    indexBits, empty);

                // Entry: locals fall through to the first send slot; remotes branch to Idle.
                stateMachine.defaultState = sendStates[0];
                var entry = stateMachine.AddEntryTransition(idle);
                entry.AddCondition(AnimatorConditionMode.IfNot, 0f, NetworkSyncBuilder.IsLocalParameter);

                var readyLayer = BuildReadyLayer(controller, r, slots, previous, empty);
                var staleLayer = BuildStaleLayer(controller, r, slots, clock, encoding,
                    indexBits, previous, empty);
                // After the Ready layer, and told whether it was built: the commit guard below
                // is only allowed to lean on the flag when there is one.
                var groups = BuildGroupLayers(controller, r, readyLayer != null, previous, empty);

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

            var groups = EffectiveGroups(r);
            var latchSteps = LatchSteps(r, slots, schedule);

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
                    indexBits, clock.Index(slotIndex, clock.stepPhases[k]),
                    LatchedAt(groups, latchSteps, k));
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
                //
                // And no latch of its own, which is the one thing a detour deliberately does
                // NOT do. A detour is a slot sent out of turn; latching here would put a fresh
                // reading of some members on the wire while the rest of the group was still
                // travelling from the last one, which is the tear the latch exists to close.
                // So a request for a grouped target is answered with the group's current
                // reading — consistent, and at worst one lap old. See AddSendDriver.
                AddSendDriver(state, r, slots[slotIndex], requestable, clock, encoding, indexBits,
                    clock.Index(slotIndex, AsyncSyncSchedule.RequestPhase), null);
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
        /// Which step of the ring each group latches at, parallel to
        /// <see cref="AsyncSyncBuilder.EffectiveGroups"/>: the first step of the pass that
        /// carries any of its members, or -1 for a group the pass does not send at all.
        ///
        /// A step the ring already stops at, rather than a step of its own. A step per group
        /// would cost the whole pass a place — every other slot would come round that much
        /// less often — to do something that has no payload of its own to send. The first
        /// member's step is where the group's lap has to begin anyway: the values sent from
        /// that step must be the ones the reading was taken from, and any later step would
        /// leave the members before it sending from the previous reading.
        /// </summary>
        static List<int> LatchSteps(Request r, List<Slot> slots, List<int> schedule)
        {
            var steps = new List<int>();
            foreach (var group in EffectiveGroups(r))
            {
                int found = -1;
                for (int k = 0; k < schedule.Count && found < 0; k++)
                    foreach (var target in slots[schedule[k]].targets)
                        if (group.members.Contains(target))
                        {
                            found = k;
                            break;
                        }
                steps.Add(found);
            }
            return steps;
        }

        /// <summary>
        /// The groups whose lap window this decoder state opens: the ones latching at a step
        /// that sends exactly what this state decodes.
        ///
        /// Matched by the (slot, phase) the step sends rather than by the step itself, because
        /// a decoder state is what an INDEX means and several steps of the pass can mean the
        /// same one. That is also why the window can be opened more often than the latch is
        /// taken — see <see cref="BuildDecoder"/> for why that costs nothing but freshness.
        /// </summary>
        static List<SyncGroup> OpenedAt(List<SyncGroup> groups, List<int> latchSteps,
            List<int> schedule, Clock clock, int slot, int phase)
        {
            var opened = new List<SyncGroup>();
            for (int g = 0; g < groups.Count; g++)
            {
                int step = latchSteps[g];
                if (step >= 0 && schedule[step] == slot && clock.stepPhases[step] == phase)
                    opened.Add(groups[g]);
            }
            return opened;
        }

        /// <summary>The groups whose latch point is this step, or null for the steps — most of
        /// them — that are nobody's.</summary>
        static List<SyncGroup> LatchedAt(List<SyncGroup> groups, List<int> latchSteps, int step)
        {
            List<SyncGroup> latched = null;
            for (int g = 0; g < groups.Count; g++)
                if (latchSteps[g] == step)
                {
                    if (latched == null) latched = new List<SyncGroup>();
                    latched.Add(groups[g]);
                }
            return latched;
        }

        /// <summary>
        /// What a step puts on the wire: the slot's values into the channels, then the index —
        /// remotes react to the index change, so the values have to be there first. Entering
        /// the state IS the service, so any pending request for the slot's targets is satisfied
        /// and its flag comes down. Shared by the ring and the detours, which send the same
        /// payload and differ only in the index they write and where they go next.
        ///
        /// <paramref name="latched"/> is the groups this step opens a lap for, and its entries
        /// come first: every member of such a group is read into its latch here, and from then
        /// until this step comes round again the pass sends members out of their latches rather
        /// than out of the parameters themselves (see <see cref="AddChannelCopies"/>). The
        /// entries of one driver run in order and in one frame, so a group's reading is one
        /// moment's — that is the whole mechanism, and it is structural rather than a matter of
        /// timing.
        ///
        /// The reason it is needed at all is that a group's members travel in DIFFERENT steps.
        /// Without the latch, a change the wearer makes between two of those steps puts a new
        /// value on the wire for one member and an old one for the other, and no amount of
        /// waiting on the far side can reassemble two halves that were never a pair: over
        /// thirteen change moments a tenth of a second apart, four arrived torn. With it, the
        /// only values a lap can carry are the ones that were true when it started.
        ///
        /// The cost is freshness, and it is bounded: a change made just after a group's latch
        /// step is not sent until the ring comes round to that step again, so a member can be
        /// one lap behind what it would have been. A whole set one lap old rather than a fresh
        /// half of one is the trade a group is asking for in the first place.
        /// </summary>
        static StateMachineBehaviour AddSendDriver(AnimatorState state, Request r, Slot slot,
            List<string> requestable, Clock clock, IndexEncoding encoding, string[] indexBits,
            int index, List<SyncGroup> latched)
        {
            var driver = VrcParameterDriver.AddTo(state, "Async Send");
            if (driver == null) return null;
            Undo.RegisterCompleteObjectUndo(driver, "Async Sync");
            VrcParameterDriver.SetLocalOnly(driver, true);
            // Before the channels, so this step's own members go out of the reading it just
            // took rather than out of the previous one.
            if (latched != null)
                foreach (var group in latched)
                    foreach (var name in group.members)
                        VrcParameterDriver.AddCopyEntry(driver, name,
                            LatchParameter(r.baseName, name));
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

        /// <summary>
        /// Builds the remote side and returns its Idle state — the one the entry transition
        /// branches to.
        ///
        /// The decoder also opens and closes each group's lap window. The slot a group latches
        /// at on the way out is the slot whose arrival puts every one of that group's flags
        /// down on the way in, before the state raises its own — so the flags standing when a
        /// commit fires all belong to decodes taken since that arrival, which is to say to one
        /// latch. Without that, a lap that lost a member would leave its flag up and the next
        /// lap's other member would complete a commit across two readings; the sending latch
        /// alone cannot see that, because it is the receiving side that lost something.
        ///
        /// A slot the pass sends more than once in a phase the decoder cannot tell apart opens
        /// the window on each of those arrivals. That costs the group freshness, never
        /// correctness: the window is reopened by a value that is itself part of the current
        /// latch, so what it waits for is the rest of that same latch coming round.
        /// </summary>
        static AnimatorState BuildDecoder(AnimatorStateMachine stateMachine, Request r,
            List<Slot> slots, List<int> schedule, Clock clock, IndexEncoding encoding,
            string[] indexBits, AnimationClip empty)
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
            var groups = EffectiveGroups(r);
            var latchSteps = LatchSteps(r, slots, schedule);
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
                            // First, so a lap's window is opened before anything is counted
                            // into it — including this state's own arrival, which belongs to
                            // the lap it is opening and not to the one before.
                            foreach (var group in OpenedAt(groups, latchSteps, schedule, clock,
                                i, phase))
                                foreach (var name in group.members)
                                    VrcParameterDriver.AddSetEntry(driver,
                                        HeldParameter(r.baseName, name), 0f);
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
            //
            // The remote side also puts every group's arrival flags down on the way in. A flag
            // raised before this moment stands for a decode that may never have been an
            // arrival at all — see BuildGroupLayers for the one that isn't — so the commit
            // this layer now guards starts from nothing rather than from whatever the first
            // frames latched. The wearer's side is left exactly as it was: nothing on their
            // copy ever raises those flags.
            if (!r.skipDrivers)
            {
                AddReadyDriver(ready, r, clearHeld: true);
                AddReadyDriver(local, r, clearHeld: false);
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

        /// <summary>How long a route with nothing to wait for waits, in seconds. Under one
        /// frame at any frame rate anybody runs at, so the route is taken on the first frame
        /// the state is evaluated at all — and still a real wait, which is what
        /// <see cref="Immediate"/> needs it to be.</summary>
        const float ImmediateSeconds = 0.001f;

        /// <summary>
        /// A route with nothing to wait for — the else of a judgement, or the way back out of
        /// a state whose whole job was the driver that ran on the way in — taken on the first
        /// frame the state it leaves is evaluated at all.
        ///
        /// It carries an exit time rather than no exit time at all. A transition with neither a
        /// condition nor an exit time is never taken — the state machine sits in the state
        /// forever, which reads in a run as a watcher that judged once and then went deaf, and
        /// which nothing about the layer's SHAPE would show.
        ///
        /// The exit time is a millisecond, and emphatically not zero. Zero reads as "leave at
        /// once" and measures as the opposite: an exit time of 0 fires exactly where 1 does, at
        /// the loop boundary, so a state carrying a 0.5 s clip held the layer for 0.5 s and a
        /// motion-less one — whose normalized unit is a second — held it for about 1.02 s
        /// (61 frames at 60 fps). Every judgement paid that, once a lap, to say a thing it had
        /// already worked out. A millisecond is the same intent expressed as a number Mecanim
        /// reads the way it is meant, and it is written in seconds and divided by the motion the
        /// way <see cref="Step"/>'s dwell is, so it stays a millisecond whether or not the
        /// generated states carry the Empty clip.
        ///
        /// Being sub-frame is what makes the judgement's else an else: the conditioned routes
        /// out of the same state become eligible on that same first frame, and Mecanim takes the
        /// first transition in list order. AsyncSyncDwellTests pins the tie.
        ///
        /// Not "no exit time plus a condition that is always true", which is the usual way round
        /// this: an always-true condition needs a parameter that can spell one, and while the Int
        /// index can (greater than -1) the bit encoding has nothing of the sort — it would mean
        /// generating a parameter for the trick's sake, and a condition that asks nothing is one
        /// more thing for whoever opens the generated layer to work out.
        ///
        /// A group's commit takes this route too, and did not always. It waited a whole loop of
        /// its own motion instead, on purpose and against the obvious reading, because coming
        /// straight back was MEASURED to make the group tear more often rather than less: with
        /// the guard closing the moment the last member arrived, every commit copied out each
        /// member's first decode after the previous commit, and a change made between two
        /// members' sends went out half-old. Over thirteen change moments a tenth of a second
        /// apart, four tore with the wait and nine without it. What the wait was really doing
        /// was making the odds better, and it was kept while that was the only lever there was.
        ///
        /// The latch took the lever away. A lap can no longer carry two readings, so there is no
        /// longer a worse half to hold out for, and the two numbers above are both zero on this
        /// build with the commit prompt. What is left is the plain reading the wait was hiding:
        /// a whole set is copied out on the frame it completes rather than up to a second later.
        /// </summary>
        static AnimatorStateTransition Immediate(AnimatorState from, AnimatorState to,
            AnimationClip empty)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = true;
            transition.exitTime = empty != null
                ? ImmediateSeconds / empty.length : ImmediateSeconds;
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
        /// The Stale watcher: judged once a lap, at the moment the marker step arrives.
        ///
        /// Watching one step of the pass is what spares this a timer — there is no window to
        /// size, no margin to guess, and a lap stretched by a request cannot make it wrong,
        /// because the measure is the lap itself rather than a number of seconds. The marker
        /// is a slot the pass sends exactly once where there is one, and an index value bought
        /// for one step where there is not (see <see cref="AsyncSyncSchedule.BuildClock"/>);
        /// either way it is a value the ring writes once a pass and no detour ever writes.
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
            int marker = clock.markerSlot;
            int markerIndex = clock.MarkerIndex;
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
            AddIndexEquals(arm, r, encoding, indexBits, markerIndex);

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
            Immediate(judge, clean, empty);

            AddIndexLeaves(dirty, idle, r, encoding, indexBits, markerIndex);
            AddIndexLeaves(clean, idle, r, encoding, indexBits, markerIndex);

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
        /// The set they change to is a set the wearer held at one moment, which took two more
        /// things than this layer. The members leave in different steps of the cycle, so the
        /// values are read into their latches in one step and sent from there
        /// (<see cref="AddSendDriver"/>), and the flags are all put down again when the step
        /// that took that reading arrives (<see cref="BuildDecoder"/>). This guard is then
        /// "every member of one reading is in", and not merely "every member is in": measured
        /// over thirteen change moments a tenth of a second apart, four of them used to reach
        /// the far side torn and none does.
        ///
        /// What is still open is narrow and lives on the receiving side: a lap that loses the
        /// arrival which would have opened the window leaves the previous lap's flags standing,
        /// and a member of the new lap can complete a commit against them. Closing that needs a
        /// generation number ON THE WIRE, and the wire is the one thing a group is not allowed
        /// to spend — see <see cref="AsyncSyncBuilder.GroupParameters"/>.
        ///
        /// Nothing here runs on the wearer: the decoder that raises the flags is behind the
        /// cycle layer's remote branch, so the flags never come up and the real parameters are
        /// never written from the shadows.
        ///
        /// With <see cref="Request.ready"/> on, the guard also asks for the flag, and the Ready
        /// watcher puts the arrival flags down as it latches. Together those close the one hole
        /// a group had: a client whose copy of the avatar starts before anything has reached it
        /// reads the index it finds — zero, which is a real slot — and decodes the channels
        /// beside it as that slot arriving, so the very first commit could carry a value nobody
        /// sent. The flag alone would not do it, because that first decode raises the slot's
        /// Seen bit as readily as a real one and the latch would land in the same frame as the
        /// commit; the flags being put down at the latch is what makes every flag the guard
        /// then sees a decode taken AFTER it, and every decode after the first frame comes from
        /// an index change somebody actually sent. The cost is that the first commit waits for
        /// one more visit of each member — later, which is the safe direction.
        ///
        /// With the flag off there is nothing to ask for and the guard is the members alone,
        /// exactly as before. <see cref="AsyncSyncBuilder.Warnings"/> says so.
        /// </summary>
        static List<SyncGroup> BuildGroupLayers(AnimatorController controller, Request r,
            bool readyGuard, GraphFrameData.AsyncSyncConfig previous, AnimationClip empty)
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
                if (readyGuard)
                    arm.AddCondition(AnimatorConditionMode.If, 0f,
                        ReadyParameter(r.baseName));
                foreach (var name in group.members)
                    arm.AddCondition(AnimatorConditionMode.If, 0f,
                        HeldParameter(r.baseName, name));
                // Straight back, so the next whole set is copied out the moment it lands. The
                // flags are down by the time this is evaluated, so the guard above cannot fire
                // again until every member has arrived once more, and there is nothing else
                // here to wait for. See Immediate for why the wait was here until the latch
                // made it pointless.
                Immediate(commit, idle, empty);

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

        /// <summary>
        /// Raises the flag. Not localOnly: this runs on every client's copy of the avatar,
        /// which is the whole point — each of them is answering for itself.
        ///
        /// <paramref name="clearHeld"/> adds the group arrival flags, put down in the same
        /// frame the flag goes up. Only on the remote path, and only where there are groups:
        /// it is the other half of the commit guard, and the wearer has nothing to put down.
        /// </summary>
        static void AddReadyDriver(AnimatorState state, Request r, bool clearHeld)
        {
            var driver = VrcParameterDriver.AddTo(state, "Async Ready");
            if (driver == null) return;
            Undo.RegisterCompleteObjectUndo(driver, "Async Sync");
            VrcParameterDriver.SetLocalOnly(driver, false);
            VrcParameterDriver.AddSetEntry(driver, ReadyParameter(r.baseName), 1f);
            if (!clearHeld) return;
            foreach (var group in EffectiveGroups(r))
                foreach (var name in group.members)
                    VrcParameterDriver.AddSetEntry(driver,
                        HeldParameter(r.baseName, name), 0f);
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
        ///
        /// A grouped target is diverted on BOTH sides, and to different ends. Leaving, it is
        /// sent from its latch — the reading the group's own step took, so the members of one
        /// lap belong to one moment (see <see cref="AddSendDriver"/>). Arriving, it is put in
        /// its shadow instead of in the parameter, so the commit can hand the set over at
        /// once. An ungrouped target goes straight from and to itself as it always did.
        /// </summary>
        internal static void AddChannelCopies(StateMachineBehaviour driver, Request r, Slot slot,
            bool toChannels)
        {
            var grouped = GroupMembers(r);
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
                    VrcParameterDriver.AddCopyEntry(driver,
                        grouped.Contains(target) ? LatchParameter(r.baseName, target) : target,
                        channel);
                else if (grouped.Contains(target))
                    VrcParameterDriver.AddCopyEntry(driver, channel,
                        HoldParameter(r.baseName, target));
                else
                    VrcParameterDriver.AddCopyEntry(driver, channel, target);
            }
            // Raised after the values are in their shadows, so the commit this may complete
            // never runs on a Hold that has not been written yet.
            if (toChannels) return;
            foreach (var target in slot.targets)
                if (grouped.Contains(target))
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

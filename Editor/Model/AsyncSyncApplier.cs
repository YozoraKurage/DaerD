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
    /// at a step boundary, and the remote Any-State decoder (one state per slot). Finishes by
    /// syncing the generated parameters in the store and saving the setup on the controller so
    /// the wizard can regenerate this same layer later. See <see cref="AsyncSyncBuilder"/> for
    /// why the technique looks like this; everything here happens inside one UndoScope.
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
                var indexBits = IndexBitNames(r, encoding, slots);

                var sendStates = BuildSendRing(stateMachine, r, slots, schedule, encoding,
                    indexBits, empty);
                var idle = BuildDecoder(stateMachine, r, slots, encoding, indexBits, empty);

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
        /// encoding, where the index is one parameter.</summary>
        static string[] IndexBitNames(Request r, IndexEncoding encoding, List<Slot> slots)
        {
            string[] indexBits = null;
            if (encoding == IndexEncoding.Bool)
            {
                int bits = NetworkSyncBuilder.BitsRequired(slots.Count);
                indexBits = new string[bits];
                for (int i = 0; i < bits; i++)
                    indexBits[i] = BitParameter(r.baseName, i);
            }
            return indexBits;
        }

        /// <summary>Builds the local send cycle and returns its states in schedule order.</summary>
        static List<AnimatorState> BuildSendRing(AnimatorStateMachine stateMachine, Request r,
            List<Slot> slots, List<int> schedule, IndexEncoding encoding, string[] indexBits,
            AnimationClip empty)
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
                    SlotStateName("Send", slot, visit),
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
                // Values first, then the index — remotes react to the index change.
                AddChannelCopies(driver, r, slot, toChannels: true);
                if (encoding == IndexEncoding.Int)
                    VrcParameterDriver.AddSetEntry(driver, IndexParameter(r.baseName), slotIndex);
                else
                    for (int bit = 0; bit < indexBits.Length; bit++)
                        VrcParameterDriver.AddSetEntry(driver, indexBits[bit],
                            ((slotIndex >> bit) & 1) == 1 ? 1f : 0f);
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
            // invisible to the decoder (canTransitionToSelf is off), and the next step's
            // routes — one per OTHER slot, and the ring never repeats a slot — pick the
            // still-raised flag up one step later.
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
            List<Slot> slots, IndexEncoding encoding, string[] indexBits, AnimationClip empty)
        {
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
            });
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

        /// <summary>Adds the copy entries for one slot: each batched Float or Bool target
        /// pairs with the channel at its own position in the slot (a slot never mixes types,
        /// so the position IS the channel number); an Int slot holds one target on the type's
        /// channel.</summary>
        static void AddChannelCopies(StateMachineBehaviour driver, Request r, Slot slot, bool toChannels)
        {
            for (int j = 0; j < slot.targets.Count; j++)
            {
                string target = slot.targets[j];
                var type = DbtBuilder.FindParameter(r.controller, target).type;
                string channel = type == AnimatorControllerParameterType.Float
                    ? FloatChannelParameter(r.baseName, j)
                    : type == AnimatorControllerParameterType.Bool
                        ? BoolChannelParameter(r.baseName, j)
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

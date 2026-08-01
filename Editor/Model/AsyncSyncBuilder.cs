using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Parameter compression via time-multiplexing ("async sync"): instead of syncing
    /// N parameters at 8 bits each, only a small fixed set of expression parameters is
    /// synced — an index plus one value channel per value type — and the parameters take
    /// turns. A local-only state cycle copies parameter i into its type's channel and sets
    /// the index (Parameter Driver, localOnly); VRChat pushes the synced pair to remotes on
    /// its ~0.3 s cadence, where an Any-State decoder (IsLocal == false, index == i) copies
    /// the channel back into parameter i. The targets themselves stay unsynced.
    ///
    /// Caveats inherent to the technique: values arrive with up to N × step latency, a
    /// remote joining mid-cycle fills in over one full cycle, and index/value are separate
    /// synced parameters so a step can transiently pair a fresh index with a stale value.
    /// </summary>
    static class AsyncSyncBuilder
    {
        public enum IndexEncoding
        {
            /// <summary>One synced Int holds the slot index (8 bits, up to 255 slots).</summary>
            Int,
            /// <summary>ceil(log2 N) synced Bools hold the index bits, LSB-first.</summary>
            Bool,
        }

        public class Request
        {
            public AnimatorController controller;
            /// <summary>Existing Float / Int / Bool parameters to multiplex, in slot order.</summary>
            public List<string> targets = new List<string>();
            /// <summary>Prefix for the generated synced parameters ("/Index", "/Float", …).</summary>
            public string baseName = "Async";
            public IndexEncoding encoding = IndexEncoding.Int;
            /// <summary>Dwell per slot in seconds. VRChat syncs roughly every 0.3 s — shorter
            /// steps risk remotes skipping slots.</summary>
            public float stepSeconds = 0.3f;
            /// <summary>Layer to create; defaults to the base name.</summary>
            public string layerName;
            /// <summary>Existing async-sync layer to REGENERATE in place (its states are
            /// rebuilt), or -1 to create a new layer.</summary>
            public int layerIndex = -1;
            /// <summary>When set, the generated synced parameters are added to this store.</summary>
            public ParameterStore store;
            public bool addToStore = true;
            /// <summary>Tests only: build the structure without VRCAvatarParameterDriver.</summary>
            internal bool skipDrivers;
        }

        public static string IndexParameter(string baseName) => baseName + "/Index";

        public static string BitParameter(string baseName, int bit) =>
            baseName + "/Index/b" + bit;

        public static string ChannelParameter(string baseName, AnimatorControllerParameterType type) =>
            baseName + "/" + type;

        /// <summary>Synced bits the generated parameters will occupy.</summary>
        public static int CompressedBits(Request r)
        {
            int bits = r.encoding == IndexEncoding.Int
                ? 8
                : NetworkSyncBuilder.BitsRequired(Mathf.Max(2, r.targets.Count));
            foreach (var type in ChannelTypes(r))
                bits += type == AnimatorControllerParameterType.Bool ? 1 : 8;
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

        /// <summary>Human-readable reason the request can't run, or null when it can.</summary>
        public static string Validate(Request r)
        {
            var controller = r.controller;
            if (controller == null) return L.Tr("No controller.");
            if (string.IsNullOrEmpty(r.baseName))
                return L.Tr("The sync parameter needs a name.");
            if (r.targets == null || r.targets.Count < 2)
                return L.Tr("Pick at least two parameters to multiplex.");
            if (r.targets.Count > 255)
                return L.Tr("Int encoding supports up to 255 states.");
            if (!(r.stepSeconds > 0f))
                return L.Tr("The step interval must be greater than zero.");
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
            }

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

            if (r.stepSeconds < 0.3f)
                warnings.Add(L.Tr("Steps shorter than VRChat's ~0.3 s sync cadence risk remotes skipping slots."));
            foreach (var type in ChannelTypes(r))
                if (type == AnimatorControllerParameterType.Float)
                {
                    warnings.Add(L.Tr("The synced Float channel carries -1..1 at 8-bit precision — values outside that range won't survive the trip."));
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
            if (r.encoding == IndexEncoding.Int)
                generated.Add((IndexParameter(r.baseName), AnimatorControllerParameterType.Int));
            else
            {
                int bits = NetworkSyncBuilder.BitsRequired(Mathf.Max(2, r.targets.Count));
                for (int i = 0; i < bits; i++)
                    generated.Add((BitParameter(r.baseName, i), AnimatorControllerParameterType.Bool));
            }
            foreach (var type in ChannelTypes(r))
                generated.Add((ChannelParameter(r.baseName, type), type));
            return generated;
        }

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

                int count = r.targets.Count;
                string[] indexBits = null;
                if (r.encoding == IndexEncoding.Bool)
                {
                    int bits = NetworkSyncBuilder.BitsRequired(count);
                    indexBits = new string[bits];
                    for (int i = 0; i < bits; i++)
                        indexBits[i] = BitParameter(r.baseName, i);
                }

                // Local side: the cycle. States carry no motion ON PURPOSE — a motion-less
                // state advances normalized time at one unit per second, which makes the
                // exit time below read directly as seconds.
                var sendStates = new List<AnimatorState>(count);
                for (int i = 0; i < count; i++)
                {
                    var state = stateMachine.AddState(
                        "Send " + DbtBuilder.Sanitize(r.targets[i]),
                        new Vector3(260f, 60f + i * 70f, 0f));
                    state.writeDefaultValues = true;
                    sendStates.Add(state);

                    if (r.skipDrivers) continue;
                    var driver = VrcParameterDriver.AddTo(state, "Async Send");
                    if (driver == null) continue;
                    Undo.RegisterCompleteObjectUndo(driver, "Async Sync");
                    VrcParameterDriver.SetLocalOnly(driver, true);
                    var type = DbtBuilder.FindParameter(controller, r.targets[i]).type;
                    // Value first, then the index — remotes react to the index change.
                    VrcParameterDriver.AddCopyEntry(driver, r.targets[i],
                        ChannelParameter(r.baseName, type));
                    if (r.encoding == IndexEncoding.Int)
                        VrcParameterDriver.AddSetEntry(driver, IndexParameter(r.baseName), i);
                    else
                        for (int bit = 0; bit < indexBits.Length; bit++)
                            VrcParameterDriver.AddSetEntry(driver, indexBits[bit],
                                ((i >> bit) & 1) == 1 ? 1f : 0f);
                }
                for (int i = 0; i < count; i++)
                {
                    var transition = sendStates[i].AddTransition(sendStates[(i + 1) % count]);
                    transition.hasExitTime = true;
                    transition.exitTime = r.stepSeconds;   // seconds — the states have no motion
                    transition.hasFixedDuration = true;
                    transition.duration = 0f;
                    EditorUtility.SetDirty(transition);
                }

                // Remote side: Any-State decoder.
                var idle = stateMachine.AddState("Remote Idle", new Vector3(620f, 60f, 0f));
                idle.writeDefaultValues = true;
                for (int i = 0; i < count; i++)
                {
                    var state = stateMachine.AddState(
                        "Recv " + DbtBuilder.Sanitize(r.targets[i]),
                        new Vector3(620f, 130f + i * 70f, 0f));
                    state.writeDefaultValues = true;

                    if (!r.skipDrivers)
                    {
                        var driver = VrcParameterDriver.AddTo(state, "Async Recv");
                        if (driver != null)
                        {
                            Undo.RegisterCompleteObjectUndo(driver, "Async Sync");
                            VrcParameterDriver.SetLocalOnly(driver, false);
                            var type = DbtBuilder.FindParameter(controller, r.targets[i]).type;
                            VrcParameterDriver.AddCopyEntry(driver,
                                ChannelParameter(r.baseName, type), r.targets[i]);
                        }
                    }

                    var transition = stateMachine.AddAnyStateTransition(state);
                    transition.canTransitionToSelf = false;
                    transition.hasExitTime = false;
                    transition.hasFixedDuration = true;
                    transition.duration = 0f;
                    transition.AddCondition(AnimatorConditionMode.IfNot, 0f,
                        NetworkSyncBuilder.IsLocalParameter);
                    if (r.encoding == IndexEncoding.Int)
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
                    targets = new List<string>(r.targets),
                });

                EditorUtility.SetDirty(stateMachine);
                EditorUtility.SetDirty(controller);
            }
            return true;
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

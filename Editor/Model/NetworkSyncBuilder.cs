using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Generates the VRChat network-sync pattern for a layer whose states only change locally
    /// (Parameter Drivers, contacts, …): every root-level state writes its index into a synced
    /// parameter (one Int, or ceil(log2 N) Bool bits, LSB-first) through a Parameter Driver,
    /// and a mirrored set of remote states plays the same motions for everyone else, routed by
    /// that parameter. IsLocal conditions fence the local and remote halves apart, and Entry
    /// branches on IsLocal (the local default state stays the fallback).
    /// </summary>
    static class NetworkSyncBuilder
    {
        public const string IsLocalParameter = "IsLocal";
        public const string DriverInstanceName = "Network";

        public enum Encoding
        {
            /// <summary>One synced Int holds the state index (up to 255 states).</summary>
            Int,
            /// <summary>ceil(log2 N) synced Bools hold the index bits, LSB-first (up to 8 bits).</summary>
            Bool,
        }

        public enum RemoteWiring
        {
            /// <summary>One AnyState transition per mirror state (N transitions).</summary>
            AnyState,
            /// <summary>Transitions between every mirror pair (N×(N-1) transitions).</summary>
            AllToAll,
        }

        public class Request
        {
            public AnimatorController controller;
            public int layerIndex = -1;
            /// <summary>Int mode: the parameter itself. Bool mode: prefix for "/b0" … "/bn".</summary>
            public string syncParameter;
            public Encoding encoding = Encoding.Int;
            public RemoteWiring wiring = RemoteWiring.AnyState;
            /// <summary>Copy timing settings from each original state's first outgoing
            /// transition onto its remote routing transitions (exit time stays off).</summary>
            public bool preserveTransitionProperties;
            public string remotePrefix = "[Net] ";
            /// <summary>OFF copies StateMachineBehaviours onto the mirrors (risk of double firing).</summary>
            public bool stripBehaviours = true;
            public bool packIntoSubMachine = true;
            /// <summary>Write the sync values through a dedicated driver named "Network"
            /// instead of appending rows to a driver already on the state.</summary>
            public bool ownDriverInstance = true;
            /// <summary>Tests only: build the structure without VRCAvatarParameterDriver.</summary>
            internal bool skipDrivers;
        }

        public static int BitsRequired(int stateCount)
        {
            int bits = 0, remaining = stateCount - 1;
            while (remaining > 0) { bits++; remaining >>= 1; }
            return Mathf.Max(bits, 1);
        }

        public static string BitParameterName(string baseName, int bit) => baseName + "/b" + bit;

        /// <summary>Human-readable reason the request can't run, or null when it can.</summary>
        public static string Validate(Request r)
        {
            var controller = r.controller;
            if (controller == null) return L.Tr("No controller.");
            if (r.layerIndex < 0 || r.layerIndex >= controller.layers.Length)
                return L.Tr("The target layer no longer exists.");
            var stateMachine = controller.layers[r.layerIndex].stateMachine;
            if (stateMachine == null)
                return L.Tr("The target layer no longer exists.");

            int count = 0;
            foreach (var child in stateMachine.states)
                if (child.state != null) count++;
            if (count < 2)
                return L.Tr("The target layer needs at least two states to sync.");

            if (string.IsNullOrEmpty(r.syncParameter))
                return L.Tr("The sync parameter needs a name.");
            if (string.IsNullOrEmpty(r.remotePrefix))
                return L.Tr("The remote state prefix must not be empty.");

            var isLocal = DbtBuilder.FindParameter(controller, IsLocalParameter);
            if (isLocal != null && isLocal.type != AnimatorControllerParameterType.Bool)
                return L.Tr("Parameter '{0}' exists but is not a Bool.", IsLocalParameter);

            if (r.encoding == Encoding.Int)
            {
                if (count > 255)
                    return L.Tr("Int encoding supports up to 255 states.");
                var existing = DbtBuilder.FindParameter(controller, r.syncParameter);
                if (existing != null && existing.type != AnimatorControllerParameterType.Int)
                    return L.Tr("Parameter '{0}' exists but is not an Int.", r.syncParameter);
            }
            else
            {
                int bits = BitsRequired(count);
                if (bits > 8)
                    return L.Tr("Bool encoding supports up to 8 bits (256 states).");
                for (int i = 0; i < bits; i++)
                {
                    string name = BitParameterName(r.syncParameter, i);
                    var existing = DbtBuilder.FindParameter(controller, name);
                    if (existing != null && existing.type != AnimatorControllerParameterType.Bool)
                        return L.Tr("Parameter '{0}' exists but is not a Bool.", name);
                }
            }

            if (!r.skipDrivers && !VrcParameterDriver.SdkAvailable)
                return L.Tr("VRChat SDK not found — the Parameter Driver behaviour is required.");
            return null;
        }

        /// <summary>Non-blocking observations shown in the wizard before running.</summary>
        public static List<string> Warnings(Request r)
        {
            var warnings = new List<string>();
            var controller = r.controller;
            if (controller == null || r.layerIndex < 0 || r.layerIndex >= controller.layers.Length)
                return warnings;
            var stateMachine = controller.layers[r.layerIndex].stateMachine;
            if (stateMachine == null) return warnings;

            if (!string.IsNullOrEmpty(r.remotePrefix))
                foreach (var child in stateMachine.states)
                    if (child.state != null && child.state.name.StartsWith(r.remotePrefix))
                    {
                        warnings.Add(L.Tr("Some states already carry the remote prefix — this layer may already be synced."));
                        break;
                    }
            if (stateMachine.stateMachines.Length > 0)
                warnings.Add(L.Tr("Sub-state machines are not mirrored; only root-level states are synced."));
            if (DbtBuilder.FindParameter(controller, r.syncParameter) != null && r.encoding == Encoding.Int)
                warnings.Add(L.Tr("The existing parameter '{0}' will be reused.", r.syncParameter));
            return warnings;
        }

        /// <summary>Runs the (pre-validated) request; returns false when validation fails.</summary>
        public static bool Apply(Request r)
        {
            if (Validate(r) != null) return false;
            var controller = r.controller;
            var stateMachine = controller.layers[r.layerIndex].stateMachine;

            var originals = new List<AnimatorState>();
            var positions = new List<Vector3>();
            foreach (var child in stateMachine.states)
                if (child.state != null)
                {
                    originals.Add(child.state);
                    positions.Add(child.position);
                }

            using (new UndoScope("Network Sync"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "Network Sync");
                Undo.RegisterCompleteObjectUndo(stateMachine, "Network Sync");

                EnsureParameter(controller, IsLocalParameter, AnimatorControllerParameterType.Bool);
                var syncParams = EnsureSyncParameters(r, originals.Count);

                // Mirrors sit below the local block, preserving the local layout.
                float minY = float.MaxValue, maxY = float.MinValue;
                foreach (var position in positions)
                {
                    minY = Mathf.Min(minY, position.y);
                    maxY = Mathf.Max(maxY, position.y);
                }
                var offset = new Vector3(0f, (maxY - minY) + 160f, 0f);

                // Local side: drivers write the state index; IsLocal fences existing routing.
                if (!r.skipDrivers)
                    for (int i = 0; i < originals.Count; i++)
                        WriteDriver(r, originals[i], syncParams, i);

                var originalSet = new HashSet<AnimatorState>(originals);
                foreach (var state in originals)
                    foreach (var transition in state.transitions)
                        AddIsLocalCondition(transition, local: true);
                foreach (var transition in stateMachine.anyStateTransitions)
                    if (transition != null && transition.destinationState != null
                        && originalSet.Contains(transition.destinationState))
                        AddIsLocalCondition(transition, local: true);
                foreach (var transition in stateMachine.entryTransitions)
                    if (transition != null)
                        AddIsLocalCondition(transition, local: true);

                // Remote side: mirror states …
                var mirrors = new List<AnimatorState>(originals.Count);
                for (int i = 0; i < originals.Count; i++)
                {
                    var source = originals[i];
                    var mirror = stateMachine.AddState(
                        StateDuplicator.MakeUniqueName(stateMachine, r.remotePrefix + source.name),
                        positions[i] + offset);
                    StateMachineCloner.CopyStateFields(source, mirror);
                    if (!r.stripBehaviours)
                        CopyBehaviours(source, mirror);
                    EditorUtility.SetDirty(mirror);
                    mirrors.Add(mirror);
                }

                // … routed by the sync value, gated on IsLocal == false.
                for (int i = 0; i < originals.Count; i++)
                {
                    var snapshot = r.preserveTransitionProperties ? FirstSnapshot(originals[i]) : null;
                    if (r.wiring == RemoteWiring.AnyState)
                    {
                        var transition = stateMachine.AddAnyStateTransition(mirrors[i]);
                        ConfigureRemoteTransition(transition, snapshot);
                        transition.canTransitionToSelf = false;
                        AddSyncConditions(transition, r.encoding, syncParams, i);
                        transition.AddCondition(AnimatorConditionMode.IfNot, 0f, IsLocalParameter);
                        EditorUtility.SetDirty(transition);
                    }
                    else
                    {
                        for (int j = 0; j < mirrors.Count; j++)
                        {
                            if (j == i) continue;
                            var transition = mirrors[j].AddTransition(mirrors[i]);
                            ConfigureRemoteTransition(transition, snapshot);
                            AddSyncConditions(transition, r.encoding, syncParams, i);
                            transition.AddCondition(AnimatorConditionMode.IfNot, 0f, IsLocalParameter);
                            EditorUtility.SetDirty(transition);
                        }
                    }
                }

                // Entry branches on IsLocal; the local default state stays the fallback.
                int defaultIndex = originals.IndexOf(stateMachine.defaultState);
                if (defaultIndex < 0) defaultIndex = 0;
                var entry = stateMachine.AddEntryTransition(mirrors[defaultIndex]);
                entry.AddCondition(AnimatorConditionMode.IfNot, 0f, IsLocalParameter);

                if (r.packIntoSubMachine)
                    StatePacker.Pack(stateMachine, mirrors, DriverInstanceName);

                EditorUtility.SetDirty(stateMachine);
                EditorUtility.SetDirty(controller);
            }
            return true;
        }

        static void EnsureParameter(AnimatorController controller, string name,
            AnimatorControllerParameterType type)
        {
            if (DbtBuilder.FindParameter(controller, name) == null)
                controller.AddParameter(name, type);
        }

        static string[] EnsureSyncParameters(Request r, int stateCount)
        {
            if (r.encoding == Encoding.Int)
            {
                EnsureParameter(r.controller, r.syncParameter, AnimatorControllerParameterType.Int);
                return new[] { r.syncParameter };
            }
            int bits = BitsRequired(stateCount);
            var names = new string[bits];
            for (int i = 0; i < bits; i++)
            {
                names[i] = BitParameterName(r.syncParameter, i);
                EnsureParameter(r.controller, names[i], AnimatorControllerParameterType.Bool);
            }
            return names;
        }

        static void WriteDriver(Request r, AnimatorState state, string[] syncParams, int value)
        {
            StateMachineBehaviour driver;
            if (r.ownDriverInstance)
                driver = VrcParameterDriver.AddTo(state, DriverInstanceName);
            else
                driver = VrcParameterDriver.FindOn(state) ?? VrcParameterDriver.AddTo(state);
            if (driver == null) return;

            Undo.RegisterCompleteObjectUndo(driver, "Network Sync");
            VrcParameterDriver.SetLocalOnly(driver, true);
            if (r.encoding == Encoding.Int)
                VrcParameterDriver.AddSetEntry(driver, syncParams[0], value);
            else
                for (int bit = 0; bit < syncParams.Length; bit++)
                    VrcParameterDriver.AddSetEntry(driver, syncParams[bit], ((value >> bit) & 1) == 1 ? 1f : 0f);
        }

        /// <summary>Appends an IsLocal condition unless the transition already tests it.</summary>
        static void AddIsLocalCondition(AnimatorTransitionBase transition, bool local)
        {
            foreach (var condition in transition.conditions)
                if (condition.parameter == IsLocalParameter)
                    return;
            Undo.RegisterCompleteObjectUndo(transition, "Network Sync");
            transition.AddCondition(local ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot,
                0f, IsLocalParameter);
            EditorUtility.SetDirty(transition);
        }

        static void AddSyncConditions(AnimatorStateTransition transition, Encoding encoding,
            string[] syncParams, int value)
        {
            if (encoding == Encoding.Int)
            {
                transition.AddCondition(AnimatorConditionMode.Equals, value, syncParams[0]);
                return;
            }
            for (int bit = 0; bit < syncParams.Length; bit++)
                transition.AddCondition(((value >> bit) & 1) == 1
                    ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f, syncParams[bit]);
        }

        /// <summary>Remote routing must react immediately — exit time stays off even when
        /// timing settings are copied over.</summary>
        static void ConfigureRemoteTransition(AnimatorStateTransition transition,
            TransitionClipboard.Snapshot snapshot)
        {
            if (snapshot != null)
                TransitionClipboard.Apply(transition, snapshot, includeConditions: false);
            else
            {
                transition.hasFixedDuration = true;
                transition.duration = 0f;
            }
            transition.hasExitTime = false;
        }

        static TransitionClipboard.Snapshot FirstSnapshot(AnimatorState state)
        {
            foreach (var transition in state.transitions)
                if (transition != null)
                    return TransitionClipboard.Capture(transition);
            return null;
        }

        static void CopyBehaviours(AnimatorState source, AnimatorState target)
        {
            foreach (var behaviour in source.behaviours)
            {
                if (behaviour == null) continue;
                var copy = target.AddStateMachineBehaviour(behaviour.GetType());
                if (copy != null)
                    EditorUtility.CopySerialized(behaviour, copy);
            }
        }
    }
}

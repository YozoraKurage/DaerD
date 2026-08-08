using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Semantic comparison of two <see cref="ControllerIR"/> snapshots. Returns human-readable
    /// difference lines ("Layer 'Hand' / State 'Idle': speed 1 ≠ 2") — an empty list means the
    /// controllers are structurally identical as far as the IR models them. Used by the
    /// round-trip tests and by a recipe's Verify (drift between code and controller).
    /// </summary>
    static class ControllerIRDiff
    {
        public static List<string> Compare(ControllerIR a, ControllerIR b)
        {
            var diffs = new List<string>();
            CompareParameters(a, b, diffs);
            CompareCount("Layers", a.layers.Count, b.layers.Count, diffs);
            int layers = Mathf.Min(a.layers.Count, b.layers.Count);
            for (int i = 0; i < layers; i++)
                CompareLayer(a.layers[i], b.layers[i], diffs);
            return diffs;
        }

        static void CompareParameters(ControllerIR a, ControllerIR b, List<string> diffs)
        {
            CompareCount("Parameters", a.parameters.Count, b.parameters.Count, diffs);
            int count = Mathf.Min(a.parameters.Count, b.parameters.Count);
            for (int i = 0; i < count; i++)
            {
                var pa = a.parameters[i];
                var pb = b.parameters[i];
                string where = "Parameter '" + pa.name + "'";
                Field(where, "name", pa.name, pb.name, diffs);
                Field(where, "type", pa.type, pb.type, diffs);
                // Reference-only declarations make no claim about the default value.
                if (pa.hasDefault && pb.hasDefault)
                {
                    Field(where, "default float", pa.defaultFloat, pb.defaultFloat, diffs);
                    Field(where, "default int", pa.defaultInt, pb.defaultInt, diffs);
                    Field(where, "default bool", pa.defaultBool, pb.defaultBool, diffs);
                }
            }
        }

        static void CompareLayer(ControllerIR.Layer a, ControllerIR.Layer b, List<string> diffs)
        {
            string where = "Layer '" + a.name + "'";
            Field(where, "name", a.name, b.name, diffs);
            Field(where, "weight", a.defaultWeight, b.defaultWeight, diffs);
            Field(where, "blending", a.blending, b.blending, diffs);
            Field(where, "IK pass", a.ikPass, b.ikPass, diffs);
            Asset(where, "mask", a.mask, b.mask, diffs);
            Field(where, "synced layer index", a.syncedLayerIndex, b.syncedLayerIndex, diffs);
            Field(where, "synced timing", a.syncedLayerAffectsTiming, b.syncedLayerAffectsTiming, diffs);

            CompareCount(where + ": motion overrides", a.syncedMotions.Count, b.syncedMotions.Count, diffs);
            int overrides = Mathf.Min(a.syncedMotions.Count, b.syncedMotions.Count);
            for (int i = 0; i < overrides; i++)
            {
                Field(where, "override state", a.syncedMotions[i].statePath, b.syncedMotions[i].statePath, diffs);
                Asset(where + " override '" + a.syncedMotions[i].statePath + "'", "motion",
                    a.syncedMotions[i].motion, b.syncedMotions[i].motion, diffs);
            }

            CompareCount(where + ": behaviour overrides",
                a.syncedBehaviours.Count, b.syncedBehaviours.Count, diffs);
            int behaviourOverrides = Mathf.Min(a.syncedBehaviours.Count, b.syncedBehaviours.Count);
            for (int i = 0; i < behaviourOverrides; i++)
            {
                Field(where, "behaviour override state",
                    a.syncedBehaviours[i].statePath, b.syncedBehaviours[i].statePath, diffs);
                CompareBehaviours(where + " override '" + a.syncedBehaviours[i].statePath + "'",
                    a.syncedBehaviours[i].behaviours, b.syncedBehaviours[i].behaviours, diffs);
            }

            if ((a.machine == null) != (b.machine == null))
            {
                diffs.Add(where + ": state machine present " + (a.machine != null) + " ≠ " + (b.machine != null));
                return;
            }
            if (a.machine != null)
                CompareMachine(where, a.machine, b.machine, diffs);
        }

        static void CompareMachine(string where, ControllerIR.Machine a, ControllerIR.Machine b,
            List<string> diffs)
        {
            Field(where, "machine name", a.name, b.name, diffs);
            Field(where, "default state", a.defaultState, b.defaultState, diffs);
            Field(where, "entry position", a.entryPosition, b.entryPosition, diffs);
            Field(where, "exit position", a.exitPosition, b.exitPosition, diffs);
            Field(where, "any-state position", a.anyStatePosition, b.anyStatePosition, diffs);

            CompareBehaviours(where, a.behaviours, b.behaviours, diffs);

            CompareCount(where + ": states", a.states.Count, b.states.Count, diffs);
            int states = Mathf.Min(a.states.Count, b.states.Count);
            for (int i = 0; i < states; i++)
                CompareState(where + " / State '" + a.states[i].name + "'", a.states[i], b.states[i], diffs);

            CompareTransitions(where + " / AnyState", a.anyStateTransitions, b.anyStateTransitions, diffs);
            CompareTransitions(where + " / Entry", a.entryTransitions, b.entryTransitions, diffs);

            CompareCount(where + ": sub-machines", a.machines.Count, b.machines.Count, diffs);
            int machines = Mathf.Min(a.machines.Count, b.machines.Count);
            for (int i = 0; i < machines; i++)
            {
                var ca = a.machines[i];
                var cb = b.machines[i];
                string child = where + " / Machine '" + ca.machine.name + "'";
                Field(child, "position", ca.position, cb.position, diffs);
                CompareTransitions(child + " (as source)", ca.transitions, cb.transitions, diffs);
                CompareMachine(child, ca.machine, cb.machine, diffs);
            }
        }

        static void CompareState(string where, ControllerIR.State a, ControllerIR.State b,
            List<string> diffs)
        {
            Field(where, "name", a.name, b.name, diffs);
            Field(where, "position", a.position, b.position, diffs);
            Field(where, "speed", a.speed, b.speed, diffs);
            Field(where, "cycle offset", a.cycleOffset, b.cycleOffset, diffs);
            Field(where, "mirror", a.mirror, b.mirror, diffs);
            Field(where, "foot IK", a.ikOnFeet, b.ikOnFeet, diffs);
            Field(where, "write defaults", a.writeDefaultValues, b.writeDefaultValues, diffs);
            Field(where, "tag", a.tag, b.tag, diffs);
            ParameterOverride(where, "speed", a.speedParameterActive, a.speedParameter,
                b.speedParameterActive, b.speedParameter, diffs);
            ParameterOverride(where, "mirror", a.mirrorParameterActive, a.mirrorParameter,
                b.mirrorParameterActive, b.mirrorParameter, diffs);
            ParameterOverride(where, "cycle offset", a.cycleOffsetParameterActive, a.cycleOffsetParameter,
                b.cycleOffsetParameterActive, b.cycleOffsetParameter, diffs);
            ParameterOverride(where, "motion time", a.timeParameterActive, a.timeParameter,
                b.timeParameterActive, b.timeParameter, diffs);

            CompareMotion(where, a.motionAsset, a.tree, b.motionAsset, b.tree, diffs);
            CompareBehaviours(where, a.behaviours, b.behaviours, diffs);
            CompareTransitions(where, a.transitions, b.transitions, diffs);
        }

        static void CompareMotion(string where, Motion assetA, ControllerIR.Tree treeA,
            Motion assetB, ControllerIR.Tree treeB, List<string> diffs)
        {
            if (assetA != assetB)
                diffs.Add(where + ": motion " + Name(assetA) + " ≠ " + Name(assetB));
            if ((treeA == null) != (treeB == null))
            {
                diffs.Add(where + ": embedded tree present " + (treeA != null) + " ≠ " + (treeB != null));
                return;
            }
            if (treeA != null)
                CompareTree(where + " / Tree '" + treeA.name + "'", treeA, treeB, diffs);
        }

        static void CompareTree(string where, ControllerIR.Tree a, ControllerIR.Tree b,
            List<string> diffs)
        {
            Field(where, "name", a.name, b.name, diffs);
            Field(where, "blend type", a.type, b.type, diffs);
            Field(where, "parameter", a.blendParameter, b.blendParameter, diffs);
            Field(where, "parameter Y", a.blendParameterY, b.blendParameterY, diffs);
            Field(where, "auto thresholds", a.useAutomaticThresholds, b.useAutomaticThresholds, diffs);
            // Min/max only matter to a 1D tree with automatic thresholds; other configurations
            // let Unity write whatever transient values it likes into them.
            if (a.type == UnityEditor.Animations.BlendTreeType.Simple1D && a.useAutomaticThresholds)
            {
                Field(where, "min threshold", a.minThreshold, b.minThreshold, diffs);
                Field(where, "max threshold", a.maxThreshold, b.maxThreshold, diffs);
            }
            Field(where, "normalized blend values", a.normalizedBlendValues, b.normalizedBlendValues, diffs);

            CompareCount(where + ": children", a.children.Count, b.children.Count, diffs);
            int count = Mathf.Min(a.children.Count, b.children.Count);
            for (int i = 0; i < count; i++)
            {
                var ca = a.children[i];
                var cb = b.children[i];
                string child = where + " child " + i;
                Field(child, "threshold", ca.threshold, cb.threshold, diffs);
                Field(child, "position", ca.position, cb.position, diffs);
                Field(child, "time scale", ca.timeScale, cb.timeScale, diffs);
                Field(child, "cycle offset", ca.cycleOffset, cb.cycleOffset, diffs);
                Field(child, "mirror", ca.mirror, cb.mirror, diffs);
                Field(child, "direct parameter", ca.directParameter, cb.directParameter, diffs);
                CompareMotion(child, ca.motionAsset, ca.tree, cb.motionAsset, cb.tree, diffs);
            }
        }

        static void CompareBehaviours(string where, List<ControllerIR.Behaviour> a,
            List<ControllerIR.Behaviour> b, List<string> diffs)
        {
            CompareCount(where + ": behaviours", a.Count, b.Count, diffs);
            int count = Mathf.Min(a.Count, b.Count);
            for (int i = 0; i < count; i++)
            {
                string child = where + " / Behaviour '" + a[i].typeName + "'";
                Field(child, "type", a[i].typeName, b[i].typeName, diffs);
                // Recipe-declared behaviours (typed driver spec, configure action) carry no
                // JSON snapshot; contents can only be compared when both sides have one.
                if (!string.IsNullOrEmpty(a[i].json) && !string.IsNullOrEmpty(b[i].json)
                    && NormalizeBehaviourJson(a[i].json) != NormalizeBehaviourJson(b[i].json))
                    diffs.Add(child + ": serialized data differs");
            }
        }

        /// <summary>
        /// Hide flags don't affect behaviour semantics (and past DaerD versions left them
        /// inconsistent), so they're stripped before comparing snapshots.
        /// </summary>
        internal static string NormalizeBehaviourJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return string.Empty;
            return Regex.Replace(json, "\"m_ObjectHideFlags\"\\s*:\\s*\\d+\\s*,?", string.Empty);
        }

        static void CompareTransitions(string where, List<ControllerIR.Transition> a,
            List<ControllerIR.Transition> b, List<string> diffs)
        {
            CompareCount(where + ": transitions", a.Count, b.Count, diffs);
            int count = Mathf.Min(a.Count, b.Count);
            for (int i = 0; i < count; i++)
            {
                var ta = a[i];
                var tb = b[i];
                string child = where + " / Transition " + i + " (→ "
                    + (ta.target == ControllerIR.Transition.Target.Exit ? "Exit" : ta.destination) + ")";
                Field(child, "target kind", ta.target, tb.target, diffs);
                Field(child, "destination", ta.destination, tb.destination, diffs);
                Field(child, "solo", ta.solo, tb.solo, diffs);
                Field(child, "mute", ta.mute, tb.mute, diffs);
                Field(child, "kind", ta.isStateTransition, tb.isStateTransition, diffs);
                if (ta.isStateTransition && tb.isStateTransition)
                {
                    Field(child, "has exit time", ta.hasExitTime, tb.hasExitTime, diffs);
                    // The exit-time VALUE is invisible while the flag is off; Unity keeps
                    // stale numbers there, and declared IR keeps its default.
                    if (ta.hasExitTime && tb.hasExitTime)
                        Field(child, "exit time", ta.exitTime, tb.exitTime, diffs);
                    Field(child, "fixed duration", ta.hasFixedDuration, tb.hasFixedDuration, diffs);
                    Field(child, "duration", ta.duration, tb.duration, diffs);
                    Field(child, "offset", ta.offset, tb.offset, diffs);
                    Field(child, "interruption", ta.interruptionSource, tb.interruptionSource, diffs);
                    Field(child, "ordered interruption", ta.orderedInterruption, tb.orderedInterruption, diffs);
                    Field(child, "can transition to self", ta.canTransitionToSelf, tb.canTransitionToSelf, diffs);
                }

                CompareCount(child + ": conditions", ta.conditions.Count, tb.conditions.Count, diffs);
                int conditions = Mathf.Min(ta.conditions.Count, tb.conditions.Count);
                for (int c = 0; c < conditions; c++)
                {
                    Field(child, "condition " + c + " mode", ta.conditions[c].mode, tb.conditions[c].mode, diffs);
                    Field(child, "condition " + c + " parameter", ta.conditions[c].parameter, tb.conditions[c].parameter, diffs);
                    Field(child, "condition " + c + " threshold", ta.conditions[c].threshold, tb.conditions[c].threshold, diffs);
                }
            }
        }

        // ---- primitives --------------------------------------------------------

        static void CompareCount(string what, int a, int b, List<string> diffs)
        {
            if (a != b) diffs.Add(what + ": count " + a + " ≠ " + b);
        }

        static void Field<T>(string where, string field, T a, T b, List<string> diffs)
        {
            if (!EqualityComparer<T>.Default.Equals(a, b))
                diffs.Add(where + ": " + field + " " + Print(a) + " ≠ " + Print(b));
        }

        static void ParameterOverride(string where, string what, bool activeA, string paramA,
            bool activeB, string paramB, List<string> diffs)
        {
            if (activeA != activeB)
                diffs.Add(where + ": " + what + " parameter active " + activeA + " ≠ " + activeB);
            // The parameter NAME only matters while the override is on — Unity keeps stale
            // names around when it's off, and they're invisible.
            else if (activeA && paramA != paramB)
                diffs.Add(where + ": " + what + " parameter '" + paramA + "' ≠ '" + paramB + "'");
        }

        static void Asset(string where, string field, Object a, Object b, List<string> diffs)
        {
            if (a != b) diffs.Add(where + ": " + field + " " + Name(a) + " ≠ " + Name(b));
        }

        static string Name(Object asset) => asset == null ? "(none)" : "'" + asset.name + "'";

        static string Print(object value) =>
            value is float f ? f.ToString(CultureInfo.InvariantCulture)
            : value == null ? "(null)"
            : value.ToString();
    }
}

using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    // ---- folded uniform settings ---------------------------------------------

    /// <summary>
    /// The driver's code-shape optimizer. It answers a question about the generated
    /// text rather than about the controller — which states a layer configures
    /// identically, so N repeated calls can be emitted as one foreach — and the driver
    /// hands it the machine to plan over and the state builders to fold.
    /// </summary>
    class RecipeFoldPlanner
    {
        readonly RecipeDriver _driver;
        readonly ControllerBuilder _c;

        internal RecipeFoldPlanner(RecipeDriver driver, ControllerBuilder c)
        {
            _driver = driver;
            _c = c;
        }

        const int FoldThreshold = 3;

        /// <summary>States a layer configures identically — the same shared clip, Write
        /// Defaults off, the same driver entries. Repeating the call per state buries
        /// the signal; one foreach states it once.</summary>
        internal class FoldPlan
        {
            public readonly List<List<ControllerIR.State>> animGroups =
                new List<List<ControllerIR.State>>();
            public readonly HashSet<ControllerIR.State> animDeferred =
                new HashSet<ControllerIR.State>();
            public List<ControllerIR.State> wdGroup;
            public readonly HashSet<ControllerIR.State> wdDeferred =
                new HashSet<ControllerIR.State>();
            public readonly List<List<ControllerIR.State>> behaviourGroups =
                new List<List<ControllerIR.State>>();
            public readonly HashSet<ControllerIR.State> behaviourDeferred =
                new HashSet<ControllerIR.State>();
        }

        internal static FoldPlan PlanFolds(ControllerIR.Machine root)
        {
            var all = new List<ControllerIR.State>();
            void Collect(ControllerIR.Machine machine)
            {
                all.AddRange(machine.states);
                foreach (var child in machine.machines) Collect(child.machine);
            }
            Collect(root);

            var plan = new FoldPlan();
            var byMotion = new Dictionary<Motion, List<ControllerIR.State>>();
            foreach (var state in all)
                if (state.tree == null && state.motionAsset != null)
                {
                    if (!byMotion.TryGetValue(state.motionAsset, out var group))
                        byMotion[state.motionAsset] = group = new List<ControllerIR.State>();
                    group.Add(state);
                }
            foreach (var state in all)   // groups in first-appearance order
                if (state.tree == null && state.motionAsset != null
                    && byMotion.TryGetValue(state.motionAsset, out var group)
                    && group.Count >= FoldThreshold && group[0] == state)
                {
                    plan.animGroups.Add(group);
                    foreach (var member in group) plan.animDeferred.Add(member);
                }

            var wd = all.FindAll(state => !state.writeDefaultValues);
            if (wd.Count >= FoldThreshold)
            {
                plan.wdGroup = wd;
                foreach (var state in wd) plan.wdDeferred.Add(state);
            }

            var bySignature = new Dictionary<string, List<ControllerIR.State>>();
            foreach (var state in all)
            {
                string signature = BehaviourSignature(state.behaviours);
                if (signature == null) continue;
                if (!bySignature.TryGetValue(signature, out var group))
                    bySignature[signature] = group = new List<ControllerIR.State>();
                group.Add(state);
            }
            foreach (var state in all)
            {
                string signature = BehaviourSignature(state.behaviours);
                if (signature == null || !bySignature.TryGetValue(signature, out var group)
                    || group.Count < FoldThreshold || group[0] != state)
                    continue;
                plan.behaviourGroups.Add(group);
                foreach (var member in group) plan.behaviourDeferred.Add(member);
            }
            return plan;
        }

        /// <summary>Canonical text of a state's whole behaviour list — states fold
        /// together only when every behaviour matches, in order. Null: nothing to fold
        /// (empty list) or not foldable (an opaque configure action).</summary>
        static string BehaviourSignature(List<ControllerIR.Behaviour> behaviours)
        {
            if (behaviours.Count == 0) return null;
            var text = new StringBuilder();
            foreach (var b in behaviours)
            {
                if (b.configure != null) return null;
                text.Append(b.typeName).Append('|').Append(b.instanceName).Append('|');
                if (b.driver != null)
                {
                    text.Append("D:").Append(b.driver.localOnly);
                    foreach (var e in b.driver.entries)
                        text.Append(';').Append(e.kind).Append(',').Append(e.name)
                            .Append(',').Append(e.value).Append(',').Append(e.min)
                            .Append(',').Append(e.max).Append(',').Append(e.chance)
                            .Append(',').Append(e.source).Append(',').Append(e.convertRange)
                            .Append(',').Append(e.sourceMin).Append(',').Append(e.sourceMax)
                            .Append(',').Append(e.destMin).Append(',').Append(e.destMax);
                }
                else
                    text.Append("J:").Append(b.json);
                text.Append('\n');
            }
            return text.ToString();
        }

        internal void EmitFolds(FoldPlan plan,
            List<(StateBuilder builder, ControllerIR.State state)> order)
        {
            if (plan.animGroups.Count == 0 && plan.wdGroup == null
                && plan.behaviourGroups.Count == 0)
                return;
            var builderOf = new Dictionary<ControllerIR.State, StateBuilder>();
            foreach (var (sb, state) in order) builderOf[state] = sb;

            _c.Script.Blank();
            foreach (var group in plan.animGroups)
                Fold(group, builderOf, (sb, state) => sb.WithAnimation(state.motionAsset));
            if (plan.wdGroup != null)
                Fold(plan.wdGroup, builderOf, (sb, state) => sb.WithWriteDefaultsSetTo(false));
            foreach (var group in plan.behaviourGroups)
                Fold(group, builderOf, (sb, state) =>
                {
                    foreach (var behaviour in state.behaviours)
                        _driver.EmitBehaviour(sb, behaviour);
                });
        }

        /// <summary>
        /// One foreach standing for N identical call sequences. The first state runs
        /// with the recorder capturing (one entry per call), the rest run with recording
        /// off — every builder is still driven for real, so the replayed IR keeps its
        /// guarantee, and the loop's text comes from actually recorded calls.
        /// </summary>
        void Fold(List<ControllerIR.State> group,
            Dictionary<ControllerIR.State, StateBuilder> builderOf,
            System.Action<StateBuilder, ControllerIR.State> apply)
        {
            var script = _c.Script;
            script.BeginCapture();
            apply(builderOf[group[0]], group[0]);
            var calls = script.EndCapture();
            _c.Script = null;
            for (int i = 1; i < group.Count; i++)
                apply(builderOf[group[i]], group[i]);
            _c.Script = script;

            string loop = LoopVar();
            var names = new List<string>();
            foreach (var state in group) names.Add(script.NameArg(builderOf[state]));

            string single = "foreach (var " + loop + " in new[] { " + string.Join(", ", names)
                + " }) " + loop + "." + string.Join(".", calls) + ";";
            if (single.Length <= 100)
            {
                script.Statement(single);
                return;
            }

            string header = "foreach (var " + loop + " in new[] { " + string.Join(", ", names) + " })";
            if (header.Length <= 100)
                script.Statement(header);
            else
            {
                script.Statement("foreach (var " + loop + " in new[] {");
                var row = new StringBuilder("        ");
                foreach (var name in names)
                {
                    if (row.Length > 8 && row.Length + name.Length + 2 > 96)
                    {
                        script.Statement(row.ToString());
                        row = new StringBuilder("        ");
                    }
                    row.Append(name).Append(',').Append(' ');
                }
                script.Statement(row.ToString().TrimEnd());
                script.Statement("    })");
            }

            // The loop body, wrapped at call boundaries like any long chain.
            var line = new StringBuilder("    " + loop);
            bool first = true;
            foreach (var call in calls)
            {
                if (!first && line.Length + call.Length + 2 > 100)
                {
                    script.Statement(line.ToString());
                    line = new StringBuilder("        ");
                }
                line.Append('.').Append(call);
                first = false;
            }
            script.Statement(line.Append(';').ToString());
        }

        string _loopVar;

        string LoopVar() => _loopVar ?? (_loopVar = _c.Script.Reserve("s"));

        /// <summary>Whether the transitions block will say anything at all.</summary>
        internal static bool HasWiring(ControllerIR.Machine machine, string prefix)
        {
            if (machine.anyStateTransitions.Count > 0 || machine.entryTransitions.Count > 0)
                return true;
            if (machine.states.Count > 0 && machine.defaultState != null
                && machine.defaultState != ControllerIR.Join(prefix, machine.states[0].name))
                return true;
            foreach (var state in machine.states)
                if (state.transitions.Count > 0) return true;
            foreach (var child in machine.machines)
                if (child.transitions.Count > 0
                    || HasWiring(child.machine, ControllerIR.Join(prefix, child.machine.name)))
                    return true;
            return false;
        }
    }
}

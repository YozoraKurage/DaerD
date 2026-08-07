using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// Converts a controller (or a subset of its layers) into recipe source code. The
    /// exporter never writes C# by hand: it drives a real <see cref="ControllerBuilder"/>
    /// with a <see cref="RecipeScript"/> recorder attached, so the emitted text is the exact
    /// call sequence whose result can be diffed against the original — that replayed builder
    /// comes back in the result for the tests to verify. Assets become [SerializeField]
    /// fields (pre-assigned on the generated .asset), never GUIDs in code.
    /// </summary>
    static class RecipeExporter
    {
        public class Result
        {
            public string code;
            public string className;
            public readonly List<FieldRef> fields = new List<FieldRef>();
            public readonly List<string> warnings = new List<string>();
            /// <summary>The builder the recording run drove — its IR is what the code builds.</summary>
            internal ControllerBuilder replayed;
        }

        public class FieldRef
        {
            public string fieldName;
            public string fieldType;
            public Object asset;
        }

        /// <summary>
        /// <paramref name="layerNames"/> null exports the whole controller (an exclusive
        /// recipe); a subset exports those layers plus only the parameters they reference.
        /// </summary>
        public static Result Export(AnimatorController controller, ICollection<string> layerNames,
            string className, string namespaceName)
        {
            var result = new Result { className = className };
            if (controller == null) return result;

            var full = ControllerIR.Parse(controller);
            var ir = full;
            if (layerNames != null)
            {
                ir = full.FilterTo(layerNames, ReferencedParameters(controller, layerNames));
                // Synced indices refer to the FULL layer list; remap them into the subset
                // (or to -1, which the driver reports as an unexportable sync source).
                foreach (var layer in ir.layers)
                {
                    if (layer.syncedLayerIndex < 0) continue;
                    string sourceName = layer.syncedLayerIndex < full.layers.Count
                        ? full.layers[layer.syncedLayerIndex].name : null;
                    layer.syncedLayerIndex = -1;
                    for (int i = 0; i < ir.layers.Count; i++)
                        if (ir.layers[i].name == sourceName && ir.layers[i].machine != null)
                            layer.syncedLayerIndex = i;
                }
            }

            var script = new RecipeScript();
            var builder = new ControllerBuilder { Script = script };
            script.RegisterRoot(builder);
            result.replayed = builder;

            RegisterAssets(ir, script, result);
            Drive(builder, ir, result.warnings);
            result.warnings.AddRange(builder.Bake());

            result.code = Compose(script, className, namespaceName, controller, result);
            return result;
        }

        /// <summary>Only the parameters the exported layers actually use travel with a
        /// partial export.</summary>
        static HashSet<string> ReferencedParameters(AnimatorController controller,
            ICollection<string> layerNames)
        {
            var referenced = new HashSet<string>();
            foreach (var layer in controller.layers)
                if (layerNames.Contains(layer.name) && layer.stateMachine != null)
                    referenced.UnionWith(LayerClipboard.CollectParameterNames(layer.stateMachine));
            return referenced;
        }

        // ---- asset fields ------------------------------------------------------

        /// <summary>Walks the IR in emission order so field declarations come out in a
        /// stable, readable order.</summary>
        static void RegisterAssets(ControllerIR ir, RecipeScript script, Result result)
        {
            void Register(Object asset)
            {
                if (asset == null || script.Assets.ContainsKey(asset)) return;
                string name = script.RegisterAsset(asset, asset.name);
                result.fields.Add(new FieldRef
                {
                    fieldName = name,
                    fieldType = asset is AnimationClip ? "AnimationClip"
                        : asset is AvatarMask ? "AvatarMask" : "Motion",
                    asset = asset,
                });
            }

            void Tree(ControllerIR.Tree tree)
            {
                if (tree == null) return;
                foreach (var child in tree.children)
                {
                    Register(child.motionAsset);
                    Tree(child.tree);
                }
            }

            void Machine(ControllerIR.Machine machine)
            {
                if (machine == null) return;
                foreach (var state in machine.states)
                {
                    Register(state.motionAsset);
                    Tree(state.tree);
                }
                foreach (var child in machine.machines)
                    Machine(child.machine);
            }

            foreach (var layer in ir.layers)
            {
                Register(layer.mask);
                Machine(layer.machine);
                foreach (var entry in layer.syncedMotions)
                    Register(entry.motion);
            }
        }

        // ---- driving the builder ------------------------------------------------

        static void Drive(ControllerBuilder c, ControllerIR ir, List<string> warnings)
        {
            // Parameters first, whatever the layer layout — they're the controller-wide
            // vocabulary, and one per line so a long list stays scannable.
            if (ir.parameters.Count > 0)
                c.Script.Comment(Header("Parameters"));
            foreach (var p in ir.parameters)
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: c.Float(p.name, p.defaultFloat); break;
                    case AnimatorControllerParameterType.Int: c.Int(p.name, p.defaultInt); break;
                    case AnimatorControllerParameterType.Bool: c.Bool(p.name, p.defaultBool); break;
                    default: c.Trigger(p.name); break;
                }

            foreach (var layer in ir.layers)
            {
                c.Script.Blank();
                if (layer.machine == null)
                {
                    string source = layer.syncedLayerIndex >= 0
                        && layer.syncedLayerIndex < ir.layers.Count
                        ? ir.layers[layer.syncedLayerIndex].name : "?";
                    c.Script.Comment(Header("Synced Layer: " + layer.name + " (mirrors " + source + ")"));
                    DriveSyncedLayer(c, ir, layer, warnings);
                    continue;
                }
                c.Script.Comment(Header("Layer: " + layer.name));

                var lb = c.Layer(layer.name);
                if (layer.defaultWeight != 1f) lb.Weight(layer.defaultWeight);
                if (layer.blending == AnimatorLayerBlendingMode.Additive) lb.Additive();
                if (layer.ikPass) lb.IkPass();
                if (layer.mask != null) lb.Mask(layer.mask);

                var states = new Dictionary<string, StateBuilder>();
                var machines = new Dictionary<string, MachineBuilder>();
                CreateScope(lb, layer.machine, states, machines);
                // States above, wiring below — the gap is what makes a layer readable.
                c.Script.Blank();
                WireScope(lb, layer.machine, states, machines, warnings);
            }
        }

        /// <summary>"---- text ----…" divider padded to a steady width.</summary>
        static string Header(string text)
        {
            const int width = 72;
            string lead = "---- " + text + " ";
            return lead.Length >= width ? lead.TrimEnd() : lead + new string('-', width - lead.Length);
        }

        static void DriveSyncedLayer(ControllerBuilder c, ControllerIR ir,
            ControllerIR.Layer layer, List<string> warnings)
        {
            if (layer.syncedLayerIndex < 0 || layer.syncedLayerIndex >= ir.layers.Count)
            {
                warnings.Add(L.Tr("Synced layer '{0}' points outside the exported layers and was skipped — export its source layer too.", layer.name));
                return;
            }
            var lb = c.SyncedLayer(layer.name, ir.layers[layer.syncedLayerIndex].name);
            if (layer.defaultWeight != 1f) lb.Weight(layer.defaultWeight);
            if (layer.blending == AnimatorLayerBlendingMode.Additive) lb.Additive();
            if (layer.ikPass) lb.IkPass();
            if (layer.mask != null) lb.Mask(layer.mask);
            if (layer.syncedLayerAffectsTiming) lb.AffectsTiming();
            foreach (var entry in layer.syncedMotions)
                lb.Override(entry.statePath, entry.motion);
        }

        static void CreateScope(MachineScope scope, ControllerIR.Machine machine,
            Dictionary<string, StateBuilder> states, Dictionary<string, MachineBuilder> machines)
        {
            foreach (var state in machine.states)
            {
                var sb = scope.State(state.name, state.motionAsset);
                states[sb.Path] = sb;
                sb.At(state.position.x, state.position.y);
                if (state.speed != 1f) sb.Speed(state.speed);
                if (state.cycleOffset != 0f) sb.CycleOffset(state.cycleOffset);
                if (state.mirror) sb.Mirror();
                if (state.ikOnFeet) sb.FootIK();
                if (!state.writeDefaultValues) sb.WriteDefaults(false);
                if (!string.IsNullOrEmpty(state.tag)) sb.Tag(state.tag);
                if (state.speedParameterActive) sb.SpeedBy(state.speedParameter);
                if (state.mirrorParameterActive) sb.MirrorBy(state.mirrorParameter);
                if (state.cycleOffsetParameterActive) sb.CycleOffsetBy(state.cycleOffsetParameter);
                if (state.timeParameterActive) sb.TimeBy(state.timeParameter);

                if (state.tree != null)
                    DriveTree(sb.Tree(state.tree.name), state.tree);

                foreach (var behaviour in state.behaviours)
                    DriveBehaviour(sb, behaviour);
            }

            // Node positions last — bookkeeping, not structure; they chain onto one line.
            scope.EntryAt(machine.entryPosition.x, machine.entryPosition.y);
            scope.ExitAt(machine.exitPosition.x, machine.exitPosition.y);
            scope.AnyStateAt(machine.anyStatePosition.x, machine.anyStatePosition.y);
            if (machine.parentPosition != Vector3.zero)
                scope.ParentAt(machine.parentPosition.x, machine.parentPosition.y);

            foreach (var child in machine.machines)
            {
                var mb = scope.AddMachine(child.machine.name).At(child.position.x, child.position.y);
                machines[mb.Prefix] = mb;
                CreateScope(mb, child.machine, states, machines);
            }
        }

        static void DriveTree(TreeBuilder tb, ControllerIR.Tree tree)
        {
            switch (tree.type)
            {
                case BlendTreeType.Direct: tb.Direct(); break;
                case BlendTreeType.Simple1D: tb.Blend1D(tree.blendParameter); break;
                default: tb.Blend2D(tree.blendParameter, tree.blendParameterY, tree.type); break;
            }
            if (!tree.useAutomaticThresholds) tb.AutoThresholds(false);
            else if (tree.type == BlendTreeType.Simple1D
                && (tree.minThreshold != 0f || tree.maxThreshold != 1f))
                tb.ThresholdRange(tree.minThreshold, tree.maxThreshold);
            if (tree.normalizedBlendValues) tb.NormalizedBlendValues();

            foreach (var child in tree.children)
            {
                TreeChildBuilder slot;
                if (child.tree != null)
                {
                    var nested = tb.AddTree(child.tree.name);
                    DriveTree(nested, child.tree);
                    slot = nested.Slot;
                }
                else
                    slot = tb.Add(child.motionAsset);

                if (child.threshold != 0f) slot.Threshold(child.threshold);
                if (child.position != Vector2.zero) slot.Position(child.position.x, child.position.y);
                if (child.timeScale != 1f) slot.TimeScale(child.timeScale);
                if (child.cycleOffset != 0f) slot.CycleOffset(child.cycleOffset);
                if (child.mirror) slot.Mirror();
                if (!string.IsNullOrEmpty(child.directParameter))
                    slot.DirectParameter(child.directParameter);
            }
        }

        static void DriveBehaviour(StateBuilder sb, ControllerIR.Behaviour behaviour)
        {
            if (behaviour.driver != null)
            {
                // The one SDK type with a typed builder: readable entries instead of JSON.
                string instance = behaviour.instanceName != behaviour.typeName
                    && !string.IsNullOrEmpty(behaviour.instanceName) ? behaviour.instanceName : null;
                var db = sb.Driver(instance);
                if (behaviour.driver.localOnly) db.LocalOnly();
                foreach (var entry in behaviour.driver.entries)
                    switch (entry.kind)
                    {
                        case 1: db.Add(entry.name, entry.value); break;
                        case 2: db.Random(entry.name, entry.min, entry.max, entry.chance); break;
                        case 3:
                            if (entry.convertRange)
                                db.CopyRange(entry.source, entry.name, entry.sourceMin,
                                    entry.sourceMax, entry.destMin, entry.destMax);
                            else
                                db.Copy(entry.source, entry.name);
                            break;
                        default: db.Set(entry.name, entry.value); break;
                    }
                return;
            }
            sb.BehaviourJson(behaviour.typeName, behaviour.json);
        }

        static void WireScope(MachineScope scope, ControllerIR.Machine machine,
            Dictionary<string, StateBuilder> states, Dictionary<string, MachineBuilder> machines,
            List<string> warnings)
        {
            // .Default() only when the implicit first-state rule doesn't already cover it.
            if (machine.states.Count > 0 && machine.defaultState != null)
            {
                string firstPath = states.Count > 0 && machine.states.Count > 0
                    ? PathOf(scope, machine.states[0].name) : null;
                if (machine.defaultState != firstPath
                    && states.TryGetValue(machine.defaultState, out var defaultBuilder))
                    defaultBuilder.Default();
            }

            foreach (var state in machine.states)
            {
                var sb = states[PathOf(scope, state.name)];
                foreach (var t in state.transitions)
                    EmitTransition(Wire(sb, t, states, machines, warnings), t);
            }
            foreach (var t in machine.anyStateTransitions)
            {
                var built = t.target == ControllerIR.Transition.Target.State
                    && states.TryGetValue(t.destination, out var ds) ? scope.AnyTo(ds)
                    : t.target == ControllerIR.Transition.Target.Machine
                    && machines.TryGetValue(t.destination, out var dm) ? scope.AnyTo(dm)
                    : null;
                if (built == null)
                    warnings.Add(L.Tr("Any-State transition to '{0}' could not be resolved — skipped.", t.destination));
                else
                    EmitTransition(built, t);
            }
            foreach (var t in machine.entryTransitions)
            {
                var built = t.target == ControllerIR.Transition.Target.State
                    && states.TryGetValue(t.destination, out var ds) ? scope.EntryTo(ds)
                    : t.target == ControllerIR.Transition.Target.Machine
                    && machines.TryGetValue(t.destination, out var dm) ? scope.EntryTo(dm)
                    : null;
                if (built == null)
                    warnings.Add(L.Tr("Entry transition to '{0}' could not be resolved — skipped.", t.destination));
                else
                    EmitTransition(built, t);
            }

            foreach (var child in machine.machines)
            {
                var mb = machines[ControllerIR.Join(scope.Prefix, child.machine.name)];
                foreach (var t in child.transitions)
                {
                    var built = t.target == ControllerIR.Transition.Target.Exit ? mb.ToExit()
                        : t.target == ControllerIR.Transition.Target.State
                        && states.TryGetValue(t.destination, out var ds) ? mb.To(ds)
                        : t.target == ControllerIR.Transition.Target.Machine
                        && machines.TryGetValue(t.destination, out var dm) ? mb.To(dm)
                        : null;
                    if (built == null)
                        warnings.Add(L.Tr("Transition from machine '{0}' to '{1}' could not be resolved — skipped.",
                            child.machine.name, t.destination));
                    else
                        EmitTransition(built, t);
                }
                WireScope(mb, child.machine, states, machines, warnings);
            }
        }

        static string PathOf(MachineScope scope, string stateName) =>
            ControllerIR.Join(scope.Prefix, stateName);

        static TransitionBuilder Wire(StateBuilder sb, ControllerIR.Transition t,
            Dictionary<string, StateBuilder> states, Dictionary<string, MachineBuilder> machines,
            List<string> warnings)
        {
            switch (t.target)
            {
                case ControllerIR.Transition.Target.Exit:
                    return sb.ToExit();
                case ControllerIR.Transition.Target.State
                    when states.TryGetValue(t.destination, out var destination):
                    return sb.To(destination);
                case ControllerIR.Transition.Target.Machine
                    when machines.TryGetValue(t.destination, out var destination):
                    return sb.To(destination);
            }
            warnings.Add(L.Tr("Transition from '{0}' to '{1}' could not be resolved — skipped.",
                sb.Path, t.destination));
            return null;
        }

        /// <summary>Conditions, then only the settings that differ from authoring defaults.</summary>
        static void EmitTransition(TransitionBuilder tb, ControllerIR.Transition t)
        {
            if (tb == null) return;
            foreach (var condition in t.conditions)
                switch (condition.mode)
                {
                    case AnimatorConditionMode.If: tb.If(condition.parameter); break;
                    case AnimatorConditionMode.IfNot: tb.IfNot(condition.parameter); break;
                    case AnimatorConditionMode.Greater: tb.IfGreater(condition.parameter, condition.threshold); break;
                    case AnimatorConditionMode.Less: tb.IfLess(condition.parameter, condition.threshold); break;
                    case AnimatorConditionMode.Equals: tb.IfIntEquals(condition.parameter, (int)condition.threshold); break;
                    default: tb.IfIntNotEquals(condition.parameter, (int)condition.threshold); break;
                }

            if (!t.isStateTransition)
            {
                if (t.solo) tb.Solo();
                if (t.mute) tb.Mute();
                return;
            }
            if (t.hasExitTime) tb.ExitTime(t.exitTime);
            if (!t.hasFixedDuration) tb.DurationNormalized(t.duration);
            else if (t.duration != 0f) tb.Duration(t.duration);
            if (t.offset != 0f) tb.Offset(t.offset);
            if (t.interruptionSource != TransitionInterruptionSource.None || !t.orderedInterruption)
                tb.Interruption(t.interruptionSource, t.orderedInterruption);
            if (t.canTransitionToSelf) tb.CanTransitionToSelf();
            if (t.solo) tb.Solo();
            if (t.mute) tb.Mute();
        }

        // ---- composing the file --------------------------------------------------

        const string CheatSheet =
@"// DaerD recipe — edit this file, then press Generate on the recipe asset.
// API quick reference (Yozolab.DaerD.Authoring):
//   Parameters   c.Float(""X"", 0.5f)  c.Int(""N"")  c.Bool(""Go"")  c.Trigger(""Fire"")
//   Layers       var fx = c.Layer(""Name"").Weight(1f).Additive().IkPass().Mask(mask);
//                c.SyncedLayer(""Mirror"", ""Name"").Override(""StatePath"", clip);
//   States       var s = fx.State(""Idle"", clip).At(x, y).Speed(1f).WriteDefaults(false)
//                        .Tag(""t"").SpeedBy(""X"").TimeBy(""X"").Default();
//   Sub-machines var sub = fx.AddMachine(""Sub"").At(x, y);  sub.State(...);  sub.To(s);
//   Transitions  s.To(other) / s.ToExit() / fx.AnyTo(s) / fx.EntryTo(s), then chain:
//                .If(""Go"") .IfNot(""Go"") .IfGreater(""X"", .5f) .IfLess .IfIntEquals(""N"", 2)
//                .ExitTime(.9f) .Duration(.15f) .DurationNormalized(.25f) .Offset(.1f)
//                .Interruption(TransitionInterruptionSource.Destination) .CanTransitionToSelf()
//   Blend trees  var t = s.Tree(""Move"").Blend1D(""X"") / .Blend2D(""X"",""Y"") / .Direct();
//                t.Add(clip).Threshold(.5f).Position(x, y).TimeScale(2f);  t.AddTree(""Nested"")
//   Behaviours   s.Driver().LocalOnly().Set(""N"", 1f).Add(""N"", 1f).Copy(""A"", ""B"")
//                        .Random(""N"", 0f, 1f, .5f);  s.BehaviourJson(typeName, json)
//   Escape hatch c.Raw(controller => { /* full UnityEditor.Animations access */ });
// Assets are the [SerializeField] fields below — assign them on the recipe asset.";

        static string Compose(RecipeScript script, string className, string namespaceName,
            AnimatorController controller, Result result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated> Exported from \"" + controller.name
                + "\" by DaerD. Safe to edit — this file is yours now. </auto-generated>");
            sb.AppendLine(CheatSheet);
            sb.AppendLine();
            sb.AppendLine("using UnityEditor.Animations;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using Yozolab.DaerD.Authoring;");
            sb.AppendLine();

            bool hasNamespace = !string.IsNullOrEmpty(namespaceName);
            string indent = hasNamespace ? "    " : string.Empty;
            if (hasNamespace)
            {
                sb.AppendLine("namespace " + namespaceName);
                sb.AppendLine("{");
            }

            sb.AppendLine(indent + "public class " + className + " : ControllerRecipe");
            sb.AppendLine(indent + "{");
            foreach (var field in result.fields)
                sb.AppendLine(indent + "    [SerializeField] " + field.fieldType + " "
                    + field.fieldName + ";");
            if (result.fields.Count > 0) sb.AppendLine();

            sb.AppendLine(indent + "    protected override void Build(ControllerBuilder c)");
            sb.AppendLine(indent + "    {");
            var body = StripUnusedVariables(script.Lines);
            while (body.Count > 0 && body[0].Length == 0) body.RemoveAt(0);
            while (body.Count > 0 && body[body.Count - 1].Length == 0) body.RemoveAt(body.Count - 1);
            foreach (var line in body)
                sb.AppendLine(line.Length == 0 ? string.Empty : indent + "        " + line);
            sb.AppendLine(indent + "    }");
            sb.AppendLine(indent + "}");
            if (hasNamespace) sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>Drops "var t = " from declarations nothing refers back to (one-shot
        /// transitions, tree slots), leaving plain fluent statements.</summary>
        internal static List<string> StripUnusedVariables(IReadOnlyList<string> lines)
        {
            var counts = new Dictionary<string, int>();
            foreach (var line in lines)
                foreach (Match token in Regex.Matches(line, @"[A-Za-z_][A-Za-z0-9_]*"))
                    counts[token.Value] = counts.TryGetValue(token.Value, out var n) ? n + 1 : 1;

            var output = new List<string>(lines.Count);
            foreach (var line in lines)
            {
                var declaration = Regex.Match(line, @"^var ([A-Za-z_][A-Za-z0-9_]*) = (.*)$");
                output.Add(declaration.Success && counts[declaration.Groups[1].Value] == 1
                    ? declaration.Groups[2].Value
                    : line);
            }
            return output;
        }
    }
}

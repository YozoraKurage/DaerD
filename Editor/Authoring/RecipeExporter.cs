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
    ///
    /// Two files come out, halves of one partial class: the generated one, rewritten whole on
    /// every export, and a hand half written only when it doesn't exist yet. The split is what
    /// makes the round trip survivable — export, reshape the code, Generate, export again —
    /// since the re-export lands next to the reshaped half instead of on top of it.
    /// </summary>
    static class RecipeExporter
    {
        public class Result
        {
            /// <summary>The generated half ("&lt;Name&gt;.Generated.cs") — always rewritten.</summary>
            public string code;
            /// <summary>The hand half ("&lt;Name&gt;.cs") — only written when it doesn't exist yet.</summary>
            public string handHalf;
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
            new RecipeDriver(builder, ir, result.warnings).Run();
            result.warnings.AddRange(builder.Bake());

            result.code = ComposeGenerated(script, className, namespaceName, controller, result);
            result.handHalf = ComposeHandHalf(className, namespaceName);
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

        /// <summary>"---- text ----…" divider padded to a steady width.</summary>
        internal static string Header(string text)
        {
            const int width = 72;
            string lead = "---- " + text + " ";
            return lead.Length >= width ? lead.TrimEnd() : lead + new string('-', width - lead.Length);
        }

        // ---- composing the file --------------------------------------------------

        const string CheatSheet =
@"// AnimatorAsCode-style API (Yozolab.DaerD.Authoring), quick reference:
//   Parameters   var go = c.BoolParameter(""Go"");   var x = c.FloatParameter(""X"", 0.5f);
//                c.IntParameter(""N"");   c.TriggerParameter(""Fire"");
//   Layers       var fx = c.Layer(""Name"").WithWeight(1).Additive().WithAvatarMask(mask);
//                c.SyncedLayer(""Mirror"", ""Name"").Override(""StatePath"", clip);
//   States       var s = fx.NewState(""Idle"").WithAnimation(clip).At(260, 60)
//                    .WithWriteDefaultsSetTo(false).WithSpeedSetTo(2).WithMotionTime(x)
//                    .WithTag(""t"").Default();
//   Sub-machines var sub = fx.NewSubStateMachine(""Sub"").At(500, 50);  sub.NewState(...);
//   Transitions  s.TransitionsTo(other) / s.Exits() / fx.AnyTransitionsTo(s)
//                    / fx.EntryTransitionsTo(s) / sub.TransitionsTo(s), then chain:
//                .When(go.IsTrue()).And(x.IsGreaterThan(0.5f))      // conditions AND together
//                .AfterAnimationFinishes() .AfterAnimationIsAtLeastAtNormalized(0.9f)
//                .WithTransitionDurationSeconds(0.15f) .WithTransitionToSelf()
//                .WithInterruption(TransitionInterruptionSource.Destination)
//   Blend trees  var t = c.NewBlendTree(""Move"").Simple1D(x)
//                    .WithAnimation(idleClip, 0).WithAnimation(runClip, 1);
//                s.WithAnimation(t);   2D: .FreeformDirectional2D(x, y) + .WithAnimation(clip, 0, 1)
//                Direct: .Direct() + .WithAnimation(clip, weightParam);  extras: t.LastChild.TimeScale(2)
//   Drivers      s.Drives(n, 1).DrivingIncreases(x, 0.1f).DrivingCopies(a, b).DrivingLocally()
//                    .DrivingRemaps(a, 0, 1, b, -1, 1).DrivingRandomizes(x, 0, 1);
//   Gadgets      c.Gadgets(""DBT"").Multiply(a, b, ""A*B"").Remap(x, ""X01"", -1, 1, 0, 1)
//                    .Smooth(x, ""X/Smoothed"", ""X/Smoothing"").Buffer(x, ""X/Late"", 2);
//                (the per-frame float math from the Add menu; its layer is rebuilt each time)
//   Async sync   c.AsyncSync().Targets(""Hue"", ""Outfit"").Rate(""Hue"", 2).Requestable(""Hue"")
//                    .Schedule(""Hue"", ""Outfit"", ""Hue"");   // explicit cycle, wizard has none
//   Fallbacks    s.BehaviourJson(typeName, json);   c.Raw(controller => { /* full API */ });
// Assets are the [SerializeField] fields below — assign them on the recipe asset.
// A build body is ordinary C#: loops, helpers and interpolation all work in your half.";

        /// <summary>
        /// The exporter's half: fields and BuildGenerated, rewritten whole on every export.
        /// It is deliberately the file nobody edits — that is what lets the other half be
        /// reshaped freely, and what makes its own git diff a clean report of what changed in
        /// the controller since the last export.
        /// </summary>
        static string ComposeGenerated(RecipeScript script, string className, string namespaceName,
            AnimatorController controller, Result result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated> Exported from \"" + controller.name
                + "\" by DaerD. </auto-generated>");
            sb.AppendLine("// DO NOT EDIT — every export overwrites this file. Your half is "
                + className + ".cs:");
            sb.AppendLine("// its Build() is what Generate runs, and DaerD never touches it. After a re-export,");
            sb.AppendLine("// diff this file, carry what changed into yours, then press Compare on the recipe");
            sb.AppendLine("// asset — it passes when both halves declare the same controller.");
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

            sb.AppendLine(indent + "public partial class " + className + " : ControllerRecipe");
            sb.AppendLine(indent + "{");
            foreach (var field in result.fields)
                sb.AppendLine(indent + "    [SerializeField] " + field.fieldType + " "
                    + field.fieldName + ";");
            if (result.fields.Count > 0) sb.AppendLine();

            sb.AppendLine(indent + "    protected override void BuildGenerated(ControllerBuilder c)");
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

        /// <summary>First line of a hand half, so the exporter can tell one from a recipe
        /// written before the split (which carries the fields and Build the generated half
        /// now owns, and would collide with it).</summary>
        public const string HandHalfMarker = "// <daerd-recipe>";

        /// <summary>
        /// Your half: a Build that delegates to the generated one, and nothing else. Written
        /// once, at the first export, and never overwritten afterwards — whatever it grows
        /// into (loops, helpers, an AI's reshaping) is yours to keep. Delegating is the honest
        /// starting point: a fresh export generates the right controller before anyone has
        /// touched anything.
        /// </summary>
        static string ComposeHandHalf(string className, string namespaceName)
        {
            var sb = new StringBuilder();
            sb.AppendLine(HandHalfMarker + " Hand half of " + className
                + " — DaerD never overwrites this file. </daerd-recipe>");
            sb.AppendLine("// " + className + ".Generated.cs is the exporter's half: rewritten on every export,");
            sb.AppendLine("// with an API cheat sheet at the top. Shape this Build() however you like — loops,");
            sb.AppendLine("// helpers, your own names — and press Compare on the recipe asset to check that it");
            sb.AppendLine("// still declares the same controller as the export it came from.");
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

            sb.AppendLine(indent + "public partial class " + className);
            sb.AppendLine(indent + "{");
            sb.AppendLine(indent + "    protected override void Build(ControllerBuilder c) => BuildGenerated(c);");
            sb.AppendLine(indent + "}");
            if (hasNamespace) sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>Drops "var t = " from declarations nothing refers back to (one-shot
        /// transitions), leaving plain fluent statements.</summary>
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

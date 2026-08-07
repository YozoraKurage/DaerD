using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Authoring;

namespace Yozolab.DaerD.Tests
{
    public class RecipeExporterTests
    {
        readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup)
                if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        AnimatorController BuildSample(out AnimationClip clipA)
        {
            var controller = new AnimatorController();
            _cleanup.Add(controller);
            clipA = new AnimationClip { name = "Clip A" };
            _cleanup.Add(clipA);

            controller.AddParameter(new AnimatorControllerParameter
            { name = "Blend", type = AnimatorControllerParameterType.Float, defaultFloat = 0.5f });
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Unused", AnimatorControllerParameterType.Int);

            controller.AddLayer("Main");
            controller.AddLayer("Second");
            var sm = controller.layers[0].stateMachine;

            var a = sm.AddState("A", new Vector3(100f, 50f, 0f));
            a.motion = clipA;
            a.speed = 2f;
            a.writeDefaultValues = false;

            var b = sm.AddState("B", new Vector3(100f, 150f, 0f));
            var tree = new BlendTree
            {
                name = "Move",
                blendType = BlendTreeType.Simple1D,
                blendParameter = "Blend",
                useAutomaticThresholds = false,
            };
            tree.children = new[]
            {
                new ChildMotion { motion = clipA, threshold = 0.25f, timeScale = 1f },
                new ChildMotion { motion = clipA, threshold = 0.75f, timeScale = 1f },
            };
            b.motion = tree;

            var behaviour = (IRTestBehaviour)b.AddStateMachineBehaviour(typeof(IRTestBehaviour));
            behaviour.payload = "data";

            var t = a.AddTransition(b);
            t.AddCondition(AnimatorConditionMode.If, 0f, "Go");
            t.hasExitTime = false;   // pinned: the exact-line assertion below depends on it
            t.duration = 0.1f;
            b.AddExitTransition().AddCondition(AnimatorConditionMode.IfNot, 0f, "Go");
            sm.AddAnyStateTransition(a).AddCondition(AnimatorConditionMode.Greater, 0.9f, "Blend");

            var second = controller.layers[1].stateMachine;
            second.AddState("Only", new Vector3(30f, 30f, 0f));
            var layers = controller.layers;
            layers[1].defaultWeight = 0.25f;
            controller.layers = layers;
            return controller;
        }

        [Test]
        public void Export_ReplayedCalls_RebuildTheExactController()
        {
            var controller = BuildSample(out _);
            var result = RecipeExporter.Export(controller, null, "SampleRecipe", null);
            Assert.IsEmpty(result.warnings, string.Join("\n", result.warnings));

            // The recorded text is exactly this call sequence — so if the replayed builder's
            // IR matches the controller, the generated code is right by construction.
            result.replayed.Bake();
            var diffs = ControllerIRDiff.Compare(ControllerIR.Parse(controller), result.replayed.IR);
            Assert.IsEmpty(diffs, string.Join("\n", diffs));
        }

        [Test]
        public void Export_CodeShape_FieldsChainsAndCollapsedTransitions()
        {
            var controller = BuildSample(out _);
            var result = RecipeExporter.Export(controller, null, "SampleRecipe", "My.Space");
            string code = result.code;

            StringAssert.Contains("namespace My.Space", code);
            StringAssert.Contains("public class SampleRecipe : ControllerRecipe", code);
            StringAssert.Contains("[SerializeField] AnimationClip clipA;", code);
            StringAssert.Contains("protected override void Build(ControllerBuilder c)", code);

            // Parameters: headed section at the very top, one per line.
            StringAssert.Contains("// ---- Parameters", code);
            StringAssert.Contains("c.Float(\"Blend\", 0.5f);", code);
            StringAssert.Contains("c.Bool(\"Go\");", code);
            StringAssert.Contains("c.Int(\"Unused\");", code);

            // Every layer opens with its own divider.
            StringAssert.Contains("// ---- Layer: Main ", code);
            StringAssert.Contains("// ---- Layer: Second ", code);
            StringAssert.Contains("var main = c.Layer(\"Main\");", code);
            // Node positions collapse to one chained line at the end of the layer's build.
            StringAssert.Contains(").ExitAt(", code);
            StringAssert.Contains("var a = main.State(\"A\", clipA).At(100f, 50f).Speed(2f).WriteDefaults(false);", code);
            StringAssert.Contains("var second = c.Layer(\"Second\").Weight(0.25f);", code);

            // One-shot transitions collapse to plain fluent statements — no dangling vars.
            StringAssert.Contains("a.To(b).If(\"Go\").Duration(0.1f);", code);
            StringAssert.Contains("b.ToExit().IfNot(\"Go\")", code);
            StringAssert.Contains("main.AnyTo(a).IfGreater(\"Blend\", 0.9f)", code);
            StringAssert.DoesNotContain("var t = ", code);

            // The blend tree and the JSON-fallback behaviour both made it in.
            StringAssert.Contains(".Blend1D(\"Blend\").AutoThresholds(false)", code);
            StringAssert.Contains("BehaviourJson(\"IRTestBehaviour\"", code);
            // No GUIDs anywhere — assets are fields.
            StringAssert.DoesNotContain("guid", code.ToLowerInvariant());
        }

        [Test]
        public void Export_Subset_TakesOnlyNamedLayers_AndReferencedParameters()
        {
            var controller = BuildSample(out _);
            var result = RecipeExporter.Export(controller, new[] { "Second" }, "PartRecipe", null);
            string code = result.code;

            StringAssert.Contains("c.Layer(\"Second\")", code);
            StringAssert.DoesNotContain("c.Layer(\"Main\")", code);
            // "Second" references no parameters at all: none are exported.
            StringAssert.DoesNotContain("c.Float(", code);
            StringAssert.DoesNotContain("c.Bool(", code);
            StringAssert.DoesNotContain("\"Unused\"", code);

            result.replayed.Bake();
            var expected = ControllerIR.Parse(controller).FilterTo(
                new HashSet<string> { "Second" }, new HashSet<string>());
            var diffs = ControllerIRDiff.Compare(expected, result.replayed.IR);
            Assert.IsEmpty(diffs, string.Join("\n", diffs));
        }

        [Test]
        public void StripUnusedVariables_DropsOneShotDeclarations_KeepsReferencedOnes()
        {
            var lines = new List<string>
            {
                "var a = c.Layer(\"L\");",
                "var t = a.To(b).If(\"Go\");",
                "a.AnyTo(x);",
            };
            var stripped = RecipeExporter.StripUnusedVariables(lines);
            Assert.AreEqual("var a = c.Layer(\"L\");", stripped[0], "a is referenced below");
            Assert.AreEqual("a.To(b).If(\"Go\");", stripped[1], "t is never used again");
        }
    }
}

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
        public void Export_CodeShape_HandlesChainsAndCollapsedTransitions()
        {
            var controller = BuildSample(out _);
            var result = RecipeExporter.Export(controller, null, "SampleRecipe", "My.Space");
            string code = result.code;

            StringAssert.Contains("namespace My.Space", code);
            StringAssert.Contains("public class SampleRecipe : ControllerRecipe", code);
            StringAssert.Contains("[SerializeField] AnimationClip clipA;", code);
            StringAssert.Contains("protected override void Build(ControllerBuilder c)", code);

            // Parameters: headed section at the very top, one typed handle per line.
            // A default of zero stays unstated; an unreferenced handle keeps no variable.
            StringAssert.Contains("// ---- Parameters", code);
            StringAssert.Contains("var blend = c.FloatParameter(\"Blend\", 0.5f);", code);
            StringAssert.Contains("var go = c.BoolParameter(\"Go\");", code);
            StringAssert.Contains("c.IntParameter(\"Unused\");", code);

            // Every layer opens with its own divider and reads in blocks:
            // states, then "// transitions", then "// layout".
            StringAssert.Contains("// ---- Layer: Main ", code);
            StringAssert.Contains("// ---- Layer: Second ", code);
            StringAssert.Contains("var main = c.Layer(\"Main\");", code);
            StringAssert.Contains("// transitions", code);
            StringAssert.Contains("// layout", code);
            // AAC shape, whole numbers as plain ints; positions live in the layout block,
            // packed several to a line, with the machine nodes chained after them.
            StringAssert.Contains(
                "var a = main.NewState(\"A\").WithAnimation(clipA).WithSpeedSetTo(2).WithWriteDefaultsSetTo(false);",
                code);
            StringAssert.Contains("a.At(100, 50);  b.At(100, 150);", code);
            StringAssert.Contains(").ExitAt(", code);
            StringAssert.Contains("var second = c.Layer(\"Second\").WithWeight(0.25f);", code);

            // One-shot transitions collapse to plain fluent statements — no dangling vars.
            StringAssert.Contains("a.TransitionsTo(b).When(go.IsTrue()).WithTransitionDurationSeconds(0.1f);", code);
            StringAssert.Contains("b.Exits().When(go.IsFalse())", code);
            StringAssert.Contains("main.AnyTransitionsTo(a).When(blend.IsGreaterThan(0.9f))", code);
            StringAssert.DoesNotContain("var t = ", code);

            // The blend tree (a NewBlendTree variable the state references) and the
            // JSON-fallback behaviour both made it in.
            StringAssert.Contains("var move = c.NewBlendTree(\"Move\").Simple1D(blend).AutoThresholds(false)", code);
            StringAssert.Contains(".WithAnimation(clipA, 0.25f)", code);
            StringAssert.Contains(".WithAnimation(move)", code);
            StringAssert.Contains("BehaviourJson(\"IRTestBehaviour\"", code);
            // No GUIDs anywhere — assets are fields.
            StringAssert.DoesNotContain("guid", code.ToLowerInvariant());
        }

        [Test]
        public void Export_FoldsUniformStateSettings_IntoForeach()
        {
            var controller = new AnimatorController();
            _cleanup.Add(controller);
            var clip = new AnimationClip { name = "Empty" };
            _cleanup.Add(clip);
            controller.AddLayer("Fold");
            var sm = controller.layers[0].stateMachine;
            for (int i = 1; i <= 3; i++)
            {
                var s = sm.AddState("S" + i, new Vector3(0f, i * 100f, 0f));
                s.motion = clip;
                s.writeDefaultValues = false;
            }

            var result = RecipeExporter.Export(controller, null, "FoldRecipe", null);
            string code = result.code;
            StringAssert.Contains(
                "foreach (var s in new[] { s1, s2, s3 }) s.WithAnimation(empty);", code);
            StringAssert.Contains(
                "foreach (var s in new[] { s1, s2, s3 }) s.WithWriteDefaultsSetTo(false);", code);
            // Positions pack into the layout block instead of one line per state.
            StringAssert.Contains("s1.At(0, 100);  s2.At(0, 200);  s3.At(0, 300);", code);

            // Folded calls still drove the real builders — the replay proves it.
            result.replayed.Bake();
            var diffs = ControllerIRDiff.Compare(ControllerIR.Parse(controller), result.replayed.IR);
            Assert.IsEmpty(diffs, string.Join("\n", diffs));
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
            StringAssert.DoesNotContain("c.FloatParameter(", code);
            StringAssert.DoesNotContain("c.BoolParameter(", code);
            StringAssert.DoesNotContain("\"Unused\"", code);

            result.replayed.Bake();
            var expected = ControllerIR.Parse(controller).FilterTo(
                new HashSet<string> { "Second" }, new HashSet<string>());
            var diffs = ControllerIRDiff.Compare(expected, result.replayed.IR);
            Assert.IsEmpty(diffs, string.Join("\n", diffs));
        }

        /// <summary>Regression: a mangled output path ("chara/Animation/…", no Assets/
        /// prefix) reached CreateAsset and threw. The normalizer must only accept real
        /// project folders — and never match "Assets" as a substring inside a name.</summary>
        [Test]
        public void NormalizeProjectFolder_AcceptsProjectPaths_RejectsEverythingElse()
        {
            Assert.AreEqual("Assets", RecipeExportQueue.NormalizeProjectFolder("Assets"));
            Assert.AreEqual("Assets/A/B", RecipeExportQueue.NormalizeProjectFolder("Assets/A/B/"));
            Assert.AreEqual("Assets/A", RecipeExportQueue.NormalizeProjectFolder("Assets\\A"));
            Assert.AreEqual("Assets/Chara/FX",
                RecipeExportQueue.NormalizeProjectFolder("/Users/me/Project/Assets/Chara/FX"));
            Assert.AreEqual("Assets",
                RecipeExportQueue.NormalizeProjectFolder("/Users/me/Project/Assets"));

            Assert.IsNull(RecipeExportQueue.NormalizeProjectFolder(null));
            Assert.IsNull(RecipeExportQueue.NormalizeProjectFolder(""));
            Assert.IsNull(RecipeExportQueue.NormalizeProjectFolder("chara/Animation/FX/Editor"));
            Assert.IsNull(RecipeExportQueue.NormalizeProjectFolder("/Users/me/MyAssetsPile/Foo"),
                "'Assets' inside a folder name is not the Assets folder");
            Assert.IsNull(RecipeExportQueue.NormalizeProjectFolder("AssetsExtra/Foo"));
        }

        [Test]
        public void StripUnusedVariables_DropsOneShotDeclarations_KeepsReferencedOnes()
        {
            var lines = new List<string>
            {
                "var a = c.Layer(\"L\");",
                "var t = a.TransitionsTo(b).When(go.IsTrue());",
                "a.AnyTransitionsTo(x);",
            };
            var stripped = RecipeExporter.StripUnusedVariables(lines);
            Assert.AreEqual("var a = c.Layer(\"L\");", stripped[0], "a is referenced below");
            Assert.AreEqual("a.TransitionsTo(b).When(go.IsTrue());", stripped[1],
                "t is never used again");
        }
    }
}

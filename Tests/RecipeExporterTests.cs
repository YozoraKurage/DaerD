using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
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
            // On the machine itself: the round-trip test below is what proves it survives.
            ((IRTestBehaviour)sm.AddStateMachineBehaviour(typeof(IRTestBehaviour))).payload = "machine";

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
            StringAssert.Contains("public partial class SampleRecipe : ControllerRecipe", code);
            StringAssert.Contains("[SerializeField] AnimationClip clipA;", code);
            StringAssert.Contains("protected override void BuildGenerated(ControllerBuilder c)", code);

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
            // The machine's own behaviour is emitted on the layer, not on a state.
            StringAssert.Contains("main.BehaviourJson(\"IRTestBehaviour\"", code);
            // No GUIDs anywhere — assets are fields.
            StringAssert.DoesNotContain("guid", code.ToLowerInvariant());
        }

        /// <summary>The two halves are the round trip's whole point: the export lands in the
        /// generated one, the hand half only delegates until someone reshapes it, and the
        /// class is partial so both belong to the same recipe.</summary>
        [Test]
        public void Export_SplitsIntoAGeneratedHalfAndAHandHalf()
        {
            var controller = BuildSample(out _);
            var result = RecipeExporter.Export(controller, null, "SampleRecipe", "My.Space");

            StringAssert.StartsWith("// <auto-generated>", result.code);
            StringAssert.Contains("DO NOT EDIT", result.code);
            StringAssert.Contains("SampleRecipe.cs", result.code, "it points at the hand half");

            string hand = result.handHalf;
            StringAssert.StartsWith(RecipeExporter.HandHalfMarker, hand);
            StringAssert.Contains("namespace My.Space", hand);
            StringAssert.Contains("public partial class SampleRecipe", hand);
            StringAssert.Contains(
                "protected override void Build(ControllerBuilder c) => BuildGenerated(c);", hand);
            // The half that is never rewritten carries none of the export's own material.
            StringAssert.DoesNotContain("[SerializeField]", hand);
            StringAssert.DoesNotContain("c.Layer(", hand);
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
                // Identical behaviour on every state: the whole sequence folds too.
                var behaviour = (IRTestBehaviour)s.AddStateMachineBehaviour(typeof(IRTestBehaviour));
                behaviour.payload = "same";
            }

            var result = RecipeExporter.Export(controller, null, "FoldRecipe", null);
            string code = result.code;
            StringAssert.Contains(
                "foreach (var s in new[] { s1, s2, s3 }) s.WithAnimation(empty);", code);
            StringAssert.Contains(
                "foreach (var s in new[] { s1, s2, s3 }) s.WithWriteDefaultsSetTo(false);", code);
            StringAssert.Contains("foreach (var s in new[] { s1, s2, s3 })", code);
            StringAssert.Contains("s.BehaviourJson(\"IRTestBehaviour\"", code);
            // Positions pack into the layout block instead of one line per state.
            StringAssert.Contains("s1.At(0, 100);  s2.At(0, 200);  s3.At(0, 300);", code);

            // Folded calls still drove the real builders — the replay proves it.
            result.replayed.Bake();
            var diffs = ControllerIRDiff.Compare(ControllerIR.Parse(controller), result.replayed.IR);
            Assert.IsEmpty(diffs, string.Join("\n", diffs));
        }

        /// <summary>A statement that outgrows the line limit wraps onto indented
        /// continuation lines — it must not split into separate statements, so transition
        /// and state definitions each stay one readable statement.</summary>
        [Test]
        public void ChainOverflow_WrapsWithContinuationLines_KeepingOneStatement()
        {
            var script = new RecipeScript();
            var root = new object();
            script.RegisterRoot(root);
            var t = new object();
            script.Declare(t, "t", root, "TransitionsTo(x)");
            for (int i = 0; i < 3; i++)
                script.Call(t, "And(someParameterName.IsGreaterThan(0.123456f))");

            var lines = new List<string>(script.Lines);
            Assert.AreEqual(3, lines.Count, string.Join("\n", lines));
            Assert.IsTrue(lines[0].StartsWith("var t = c.TransitionsTo(x)"), lines[0]);
            Assert.IsFalse(lines[0].EndsWith(";"), "the statement continues on the next line");
            Assert.IsTrue(lines[1].StartsWith("    .And("), lines[1]);
            Assert.IsTrue(lines[2].EndsWith(";"), "only the final line closes the statement");
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

        // ---- gadget layers -----------------------------------------------------

        /// <summary>Runs the body against a controller that really exists on disk: the gadget
        /// records the exporter reads live in a hidden sub-asset of the .controller, and an
        /// in-memory controller has nowhere to keep one.</summary>
        static void WithSavedController(System.Action<AnimatorController> body)
        {
            const string path = "Assets/DaerDRecipeGadgetExportTest.controller";
            AssetDatabase.CreateAsset(new AnimatorController(), path);
            try
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                controller.AddLayer("Base");
                controller.layers[0].stateMachine.AddState("Idle", new Vector3(0f, 0f, 0f));
                controller.AddParameter("A", AnimatorControllerParameterType.Float);
                controller.AddParameter("B", AnimatorControllerParameterType.Float);
                body(controller);
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        static int IndexOfLayer(AnimatorController controller, string name)
        {
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].name == name) return i;
            return -1;
        }

        /// <summary>One gadget into the "Math" layer, creating it on the first call.</summary>
        static void ApplyGadget(AnimatorController controller, AapGadgets.Kind kind,
            System.Action<AapGadgets.Request> tweak)
        {
            var request = new AapGadgets.Request
            {
                controller = controller,
                kind = kind,
                inputA = "A",
                inputB = "B",
                layerIndex = IndexOfLayer(controller, "Math"),
                newLayerName = "Math",
            };
            tweak(request);
            Assert.IsTrue(AapGadgets.Apply(request), kind.ToString());
        }

        static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            for (int i = text.IndexOf(needle); i >= 0; i = text.IndexOf(needle, i + needle.Length))
                count++;
            return count;
        }

        /// <summary>The build body alone. The file opens with an API cheat sheet that names
        /// half the API in comments, so "the export did not write X" has to be asked of the
        /// code the export actually decided on.</summary>
        static string Body(string code)
        {
            int start = code.IndexOf("BuildGenerated(ControllerBuilder c)");
            Assert.Greater(start, 0, "the generated half has no build body");
            return code.Substring(start);
        }

        /// <summary>A layer whose every child has a saved config comes back as the gadget calls
        /// that built it instead of the wall of trees they expand into — and the parameters
        /// those calls recreate stop being declared, while the shared ones stay.</summary>
        [Test]
        public void Export_CoveredGadgetLayer_ComesBackAsGadgetCalls()
        {
            WithSavedController(controller =>
            {
                ApplyGadget(controller, AapGadgets.Kind.Multiply, r => r.output = "A*B");
                ApplyGadget(controller, AapGadgets.Kind.Buffer, r =>
                {
                    r.output = "A/Late";
                    r.bufferFrames = 2;
                });
                ApplyGadget(controller, AapGadgets.Kind.Smooth, r =>
                {
                    r.output = "A/Smoothed";
                    r.smoothing = "A/Smoothing";
                });

                var result = RecipeExporter.Export(controller, null, "GadgetRecipe", null);
                Assert.IsEmpty(result.warnings, string.Join("\n", result.warnings));
                string code = result.code;

                StringAssert.Contains("// ---- Layer: Math (DBT gadgets) ", code);
                StringAssert.Contains("c.Gadgets(\"Math\")", code);
                StringAssert.Contains(".Multiply(\"A\", \"B\", \"A*B\")", code);
                // Trailing arguments that match the method's own defaults stay out of the call.
                StringAssert.Contains(".Buffer(\"A\", \"A/Late\", 2)", code);
                StringAssert.Contains(".Smooth(\"A\", \"A/Smoothed\", \"A/Smoothing\")", code);

                // None of the trees, states or clips those calls stand for.
                StringAssert.DoesNotContain("NewBlendTree(", Body(code));
                StringAssert.DoesNotContain("c.Layer(\"Math\")", code);
                Assert.IsEmpty(result.fields, "every clip in the layer is minted by the calls");

                // The gadgets own their outputs and everything under them, and rebuild those
                // themselves; the constant One and the smoothing amount are shared, and stay.
                StringAssert.DoesNotContain("FloatParameter(\"A*B\")", code);
                StringAssert.DoesNotContain("FloatParameter(\"A/Late/1\")", code);
                StringAssert.Contains("c.FloatParameter(\"A\")", code);
                StringAssert.Contains("FloatParameter(\"One\", 1)", code);
                StringAssert.Contains("FloatParameter(\"A/Smoothing\", 0.9f)", code);
            });
        }

        /// <summary>A child somebody added to the tree by hand has no call to stand for it, so
        /// the layer falls back to the raw tree it is — and the export says why.</summary>
        [Test]
        public void Export_GadgetLayerWithAnUnaccountedChild_FallsBackToTheRawTree()
        {
            WithSavedController(controller =>
            {
                ApplyGadget(controller, AapGadgets.Kind.Multiply, r => r.output = "A*B");

                var root = (BlendTree)controller.layers[IndexOfLayer(controller, "Math")]
                    .stateMachine.states[0].state.motion;
                DbtBuilder.AddDirectChild(root, DbtBuilder.ParameterClip(controller, "B", 1f), "One");

                var result = RecipeExporter.Export(controller, null, "GadgetRecipe", null);
                Assert.AreEqual(1, result.warnings.Count, string.Join("\n", result.warnings));
                StringAssert.Contains("Math", result.warnings[0]);
                StringAssert.Contains("c.Layer(\"Math\")", result.code);
                StringAssert.Contains("NewBlendTree(", Body(result.code));
                Assert.IsNotEmpty(result.fields, "a raw tree needs its clips as fields");
            });
        }

        /// <summary>The clock layer FrameTime brings is rebuilt by the gadget call; exporting
        /// its states as well would only add a second copy under a numbered name.</summary>
        [Test]
        public void Export_SupportingLayerOfACoveredGadget_IsLeftToTheGadgetCall()
        {
            WithSavedController(controller =>
            {
                ApplyGadget(controller, AapGadgets.Kind.FrameTime, r =>
                {
                    r.inputA = null;
                    r.inputB = null;
                    r.output = "FrameTime";
                });

                var result = RecipeExporter.Export(controller, null, "GadgetRecipe", null);
                Assert.IsEmpty(result.warnings, string.Join("\n", result.warnings));
                StringAssert.Contains(".FrameTime(\"FrameTime\")", result.code);
                StringAssert.Contains("(regenerated by the gadget layer above)", result.code);
                StringAssert.DoesNotContain("c.Layer(\"FrameTime Clock\")", result.code);
            });
        }

        /// <summary>A LUT's curve has to survive as source, which means one Keyframe literal
        /// per key.</summary>
        [Test]
        public void Export_Lut1DGadget_WritesTheCurveAsALiteral()
        {
            WithSavedController(controller =>
            {
                ApplyGadget(controller, AapGadgets.Kind.Lut1D, r =>
                {
                    r.output = "A/Lut";
                    r.curve = new AnimationCurve(
                        new Keyframe(0f, 0f, 1f, 1f),
                        new Keyframe(0.5f, 0.25f, 1f, 1f),
                        new Keyframe(1f, 1f, 1f, 1f));
                    r.lutSamples = 9;
                });

                var result = RecipeExporter.Export(controller, null, "GadgetRecipe", null);
                Assert.IsEmpty(result.warnings, string.Join("\n", result.warnings));
                string code = result.code;
                StringAssert.Contains(".Lut1D(\"A\", \"A/Lut\", new AnimationCurve(new Keyframe(", code);
                Assert.AreEqual(3, CountOccurrences(code, "new Keyframe("), "one literal per key");
                StringAssert.Contains("), 9)", code,
                    "the sample count differs from the default, so it is written too");
            });
        }

        /// <summary>Tangent weights have no place in a four-argument Keyframe, so a curve that
        /// carries them comes out flat — quietly changing the values the LUT bakes unless the
        /// export says so.</summary>
        [Test]
        public void Export_Lut1DGadgetWithWeightedTangents_WarnsThatTheWeightsAreDropped()
        {
            WithSavedController(controller =>
            {
                ApplyGadget(controller, AapGadgets.Kind.Lut1D, r =>
                {
                    r.output = "A/Lut";
                    r.curve = new AnimationCurve(new[]
                    {
                        new Keyframe(0f, 0f, 1f, 1f)
                        {
                            weightedMode = WeightedMode.Both, inWeight = 0.5f, outWeight = 0.5f,
                        },
                        new Keyframe(1f, 1f, 1f, 1f),
                    });
                });

                var result = RecipeExporter.Export(controller, null, "GadgetRecipe", null);
                Assert.AreEqual(1, result.warnings.Count, string.Join("\n", result.warnings));
                StringAssert.Contains("A/Lut", result.warnings[0]);
            });
        }

        /// <summary>Exporting the gadget layer alone still declares what it reads: the input
        /// parameters and the constant weight come from the tree, not from the configs.</summary>
        [Test]
        public void Export_GadgetLayerOnItsOwn_StillDeclaresTheParametersItReads()
        {
            WithSavedController(controller =>
            {
                ApplyGadget(controller, AapGadgets.Kind.Multiply, r => r.output = "A*B");

                var result = RecipeExporter.Export(controller, new[] { "Math" }, "GadgetRecipe", null);
                string code = result.code;
                StringAssert.Contains("c.FloatParameter(\"A\")", code);
                StringAssert.Contains("c.FloatParameter(\"B\")", code);
                StringAssert.Contains("FloatParameter(\"One\", 1)", code);
                StringAssert.DoesNotContain("FloatParameter(\"A*B\")", code);
                StringAssert.Contains(".Multiply(\"A\", \"B\", \"A*B\")", code);
                StringAssert.DoesNotContain("c.Layer(\"Base\")", code);
            });
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

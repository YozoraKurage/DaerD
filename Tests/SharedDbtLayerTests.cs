using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Authoring;
#if DAERD_MA && DAERD_VRC
using MaMergeAnimator = nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator;
#endif

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// One Direct blend tree layer, several owners: AAP gadgets, tree-wired object toggles and
    /// whatever somebody hung there by hand all share the root tree's children.
    ///
    /// The bug these pin: an export that asked "does EVERY child of this layer have a call?"
    /// sent a mixed layer to raw states, which put it in the recipe's DECLARATION — and the next
    /// Generate rebuilt that machinery, leaving every saved record pointing at a state machine
    /// and a tree that no longer existed. They were pruned. The toggles went on working as
    /// states and DaerD had forgotten it made them, which is the worst shape a loss can take:
    /// nothing looks broken.
    ///
    /// Everything here writes a real prefab and a real controller to disk, for the reason
    /// <see cref="ObjectRecipeTests"/> gives: a record lives in a sub-asset of the .controller
    /// and the pin resolves references into a prefab ASSET.
    /// </summary>
    public class SharedDbtLayerTests
    {
        const string Folder = "Assets/DDSharedDbt";
        const string ControllerPath = Folder + "/Gimmick.controller";
        const string PortedPath = Folder + "/Ported.controller";
        const string GimmickPrefab = Folder + "/Gimmick.prefab";
        const string PortedPrefab = Folder + "/Ported.prefab";
        const string HandClipPath = Folder + "/Hand.anim";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", "DDSharedDbt");
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            PrefabLinks.ForgetCandidates();
            GraphFrameData.ForgetHolders();
        }

#if DAERD_MA && DAERD_VRC
        /// <summary>A controller on disk pinned to a gimmick prefab whose merge names it — the
        /// only shape a pin ever reads as healthy in.</summary>
        static AnimatorController Pinned(string path, string prefabPath)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var built = new GameObject("Root");
            var merge = new GameObject("Merge");
            merge.transform.SetParent(built.transform);
            merge.AddComponent<MaMergeAnimator>().animator = controller;
            var hat = new GameObject("Hat");
            hat.transform.SetParent(merge.transform);

            var gimmick = PrefabUtility.SaveAsPrefabAsset(built, prefabPath);
            Object.DestroyImmediate(built);
            GraphFrameData.SetPrefabLink(controller, gimmick,
                gimmick.GetComponentInChildren<MaMergeAnimator>(true));
            return controller;
        }

        /// <summary>What ControllerRecipe.Generate does, without a ScriptableObject to hang it
        /// on: apply the declared layers, then run the post steps that build the rest.</summary>
        static List<string> Generate(ControllerBuilder builder, AnimatorController controller)
        {
            var warnings = new List<string>(builder.Bake());
            warnings.AddRange(ControllerIRBuilder.Rebuild(builder.IR, controller, false));
            foreach (var op in builder.PostOps)
                warnings.AddRange(op(controller));
            return warnings;
        }

        static int LayerIndex(AnimatorController controller, string name)
        {
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].name == name) return i;
            return -1;
        }

        /// <summary>The root Direct tree of the shared layer.</summary>
        static BlendTree Shared(AnimatorController controller, string layer = "DBT")
        {
            int index = LayerIndex(controller, layer);
            Assert.GreaterOrEqual(index, 0, "no layer named '" + layer + "'");
            return (BlendTree)controller.layers[index].stateMachine.states[0].state.motion;
        }

        static bool HoldsChild(BlendTree tree, Motion motion)
        {
            foreach (var child in tree.children)
                if (child.motion == motion) return true;
            return false;
        }

        /// <summary>The build body alone: the generated file opens with a cheat sheet naming half
        /// the API in comments, so "the export did not write X" has to be asked of the code the
        /// export actually decided on.</summary>
        static string Body(string code)
        {
            int start = code.IndexOf("BuildGenerated(ControllerBuilder c)");
            Assert.Greater(start, 0, "the generated half has no build body");
            return code.Substring(start);
        }

        /// <summary>A tree-wired toggle in the shared layer, plus a child nobody generated hung
        /// beside it — the shape the loss was reported in.</summary>
        static AnimationClip MixedLayer(AnimatorController controller)
        {
            var first = new ControllerBuilder();
            first.Objects().Toggle("Hat").AsTree().Shows("Hat");
            Assert.IsEmpty(Generate(first, controller));

            var hand = new AnimationClip { name = "Hand" };
            AssetDatabase.CreateAsset(hand, HandClipPath);
            DbtBuilder.AddDirectChild(Shared(controller), hand, "One");
            return hand;
        }
#endif

        /// <summary>
        /// The export claims the toggle's child and declares the rest, so one layer comes back as
        /// both: a raw tree holding what nobody accounts for, and the call that rebuilds the
        /// toggle into it.
        /// </summary>
        [Test]
        public void AMixedLayer_ExportsTheToggleAsACallAndDeclaresOnlyTheRest()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath, GimmickPrefab);
            MixedLayer(controller);

            var result = RecipeExporter.Export(controller, null, "GimmickRecipe", null);

            Assert.IsEmpty(result.warnings, string.Join("\n", result.warnings));
            string body = Body(result.code);
            StringAssert.Contains("c.Objects()", body,
                "the toggle has a call of its own and does not have to ride the raw tree");
            StringAssert.Contains(".Toggle(\"Hat\")", body);
            StringAssert.Contains("c.Layer(\"DBT\")", body, "the hand child still needs declaring");
            Assert.IsNotEmpty(result.fields, "and its clip travels as a field");

            // One child declared, not two: the toggle's own is the call's to rebuild, and
            // declaring it as well would build the same tree twice.
            var declared = result.replayed.IR.layers.Find(l => l.name == "DBT");
            Assert.IsNotNull(declared);
            Assert.AreEqual(1, declared.machine.states[0].tree.children.Count);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>
        /// The regression itself: generating what that export describes keeps the record. The
        /// declaration rebuilds the layer around the toggle and the call puts the toggle back
        /// into it — the hand child intact, one child per owner, and nothing pruned.
        /// </summary>
        [Test]
        public void RegeneratingAMixedLayer_KeepsTheRecordAndTheHandBuiltChild()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath, GimmickPrefab);
            var hand = MixedLayer(controller);
            var result = RecipeExporter.Export(controller, null, "GimmickRecipe", null);
            Assert.IsEmpty(result.warnings, string.Join("\n", result.warnings));

            var warnings = Generate(result.replayed, controller);

            Assert.IsEmpty(warnings, string.Join("\n", warnings));
            var saved = GraphFrameData.GetObjectGadgets(controller);
            Assert.AreEqual(1, saved.Count, "the record was pruned by the rebuild");
            Assert.AreEqual("Hat", saved[0].name);

            var root = Shared(controller);
            Assert.AreEqual(2, root.children.Length, "one child per owner");
            Assert.IsTrue(HoldsChild(root, hand), "the hand child was rebuilt from the declaration");
            Assert.IsTrue(HoldsChild(root, saved[0].tree), "and the record points into the layer");
            Assert.Less(LayerIndex(controller, "DBT 1"), 0, "the toggle landed beside itself");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>
        /// AAP gadgets and tree-wired toggles in one layer, exported and replayed onto another
        /// gimmick. Both post steps have to land in the SAME layer there: neither has a record to
        /// inherit one from on a fresh controller, so the name on the call is all they share, and
        /// a second "DBT" beside the first would split the gimmick in two.
        /// </summary>
        [Test]
        public void AapGadgetsAndTreeToggles_ShareOneLayerAcrossARoundTrip()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath, GimmickPrefab);
            var builder = new ControllerBuilder();
            builder.FloatParameter("A");
            builder.FloatParameter("B");
            builder.Gadgets("DBT").Multiply("A", "B", "A*B");
            builder.Objects().Toggle("Hat").AsTree().Shows("Hat");
            Assert.IsEmpty(Generate(builder, controller));
            Assert.AreEqual(2, Shared(controller).children.Length, "they shared a layer at once");

            var result = RecipeExporter.Export(controller, null, "GimmickRecipe", null);
            Assert.IsEmpty(result.warnings, string.Join("\n", result.warnings));
            string body = Body(result.code);
            StringAssert.Contains("// ---- Layer: DBT (DBT gadgets, object gadgets) ", body,
                "one layer, named as what both post steps rebuild");
            StringAssert.Contains("c.Gadgets().Multiply(\"A\", \"B\", \"A*B\")", body);
            StringAssert.Contains("c.Objects()", body);
            StringAssert.DoesNotContain("c.Layer(\"DBT\")", body,
                "every child has a call, so the layer is not declared at all");

            var ported = Pinned(PortedPath, PortedPrefab);
            var replayWarnings = Generate(result.replayed, ported);

            Assert.IsEmpty(replayWarnings, string.Join("\n", replayWarnings));
            Assert.Less(LayerIndex(ported, "DBT 1"), 0, "the two post steps built a layer each");
            Assert.AreEqual(2, Shared(ported).children.Length);
            Assert.AreEqual(1, GraphFrameData.GetGadgets(ported).Count, "the DBT gadget's record");
            Assert.AreEqual(1, GraphFrameData.GetObjectGadgets(ported).Count, "the toggle's record");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>
        /// A shared layer under a name of its own is named on the call. Without that the replay
        /// would build the toggle into a fresh "DBT" while the rest of the layer was declared
        /// under its real name — a gimmick split in two by the port that was meant to carry it.
        /// </summary>
        [Test]
        public void AToggleLayerThatIsNotCalledDbt_IsNamedOnTheCall()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath, GimmickPrefab);
            var builder = new ControllerBuilder();
            builder.Objects().Toggle("Hat").AsTree().Shows("Hat");
            Assert.IsEmpty(Generate(builder, controller));

            var layers = controller.layers;
            layers[LayerIndex(controller, "DBT")].name = "Math";
            controller.layers = layers;

            var result = RecipeExporter.Export(controller, null, "GimmickRecipe", null);

            Assert.IsEmpty(result.warnings, string.Join("\n", result.warnings));
            StringAssert.Contains("c.Objects(\"Math\")", Body(result.code));

            var ported = Pinned(PortedPath, PortedPrefab);
            Assert.IsEmpty(Generate(result.replayed, ported));
            Assert.GreaterOrEqual(LayerIndex(ported, "Math"), 0, "the name travelled with the call");
            Assert.AreEqual(1, Shared(ported, "Math").children.Length);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>Running the shared layer twice regenerates it in place: the gadget step
        /// sweeps its own children out of a layer it no longer owns whole, instead of taking the
        /// layer away and the toggle with it.</summary>
        [Test]
        public void AapGadgetsAndTreeToggles_RegenerateInPlaceRatherThanStacking()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath, GimmickPrefab);
            for (int run = 0; run < 2; run++)
            {
                var builder = new ControllerBuilder();
                builder.FloatParameter("A");
                builder.FloatParameter("B");
                builder.Gadgets("DBT").Multiply("A", "B", "A*B");
                builder.Objects().Toggle("Hat").AsTree().Shows("Hat");
                var warnings = Generate(builder, controller);

                Assert.IsEmpty(warnings, "run " + run + ":\n" + string.Join("\n", warnings));
                Assert.AreEqual(2, Shared(controller).children.Length, "run " + run);
                Assert.AreEqual(1, GraphFrameData.GetGadgets(controller).Count, "run " + run);
                Assert.AreEqual(1, GraphFrameData.GetObjectGadgets(controller).Count, "run " + run);
            }
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }
    }
}

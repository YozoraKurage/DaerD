using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
#if DAERD_MA && DAERD_VRC
using MaMergeAnimator = nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator;
#endif
using Yozolab.DaerD.Authoring;
using Yozolab.DaerD.Bridge;
using Yozolab.DaerD.Engine;
using Yozolab.DaerD.IR;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Object gadgets as a recipe: the calls that rebuild the controller side, and the two
    /// things a run does to the prefab — read it, and stop by name when what it says is not
    /// there (ADR 0047).
    ///
    /// Everything here writes a real prefab and a real controller to disk. A record lives in a
    /// sub-asset of the .controller and the pin resolves references into a prefab ASSET, so an
    /// in-memory pair would quietly export an empty plan and prove nothing. Modular Avatar is
    /// what makes a pin possible at all, so the file skips itself where MA is absent.
    /// </summary>
    public class ObjectRecipeTests
    {
        const string Folder = "Assets/DDObjectRecipe";
        const string ControllerPath = Folder + "/Gimmick.controller";
        const string PortedPath = Folder + "/Ported.controller";
        const string GimmickPrefab = Folder + "/Gimmick.prefab";
        const string PortedPrefab = Folder + "/Ported.prefab";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", "DDObjectRecipe");
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            PrefabLinks.ForgetCandidates();
            GraphFrameData.ForgetHolders();
        }

#if DAERD_MA && DAERD_VRC
        /// <summary>The gimmick: a root, the merge one object down, and two objects under it.
        /// The same shape the wizard's tests use, so a path here means what it means there.
        /// </summary>
        static GameObject Prefab(AnimatorController merged, string path = GimmickPrefab,
            bool withStore = false)
        {
            var built = new GameObject("Root");
            if (withStore)
                built.AddComponent<nadena.dev.modular_avatar.core.ModularAvatarParameters>();
            var merge = new GameObject("Merge");
            merge.transform.SetParent(built.transform);
            merge.AddComponent<MaMergeAnimator>().animator = merged;
            var hat = new GameObject("Hat");
            hat.transform.SetParent(merge.transform);
            var cape = new GameObject("Cape");
            cape.transform.SetParent(merge.transform);
            cape.AddComponent<Light>();

            var saved = PrefabUtility.SaveAsPrefabAsset(built, path);
            Object.DestroyImmediate(built);
            return saved;
        }

        static GameObject In(GameObject prefab, string path) =>
            prefab.transform.Find(path).gameObject;

        /// <summary>A controller on disk, pinned to a gimmick prefab whose merge names it —
        /// which is the only shape a pin ever reads as healthy in.</summary>
        static AnimatorController Pinned(string path, string prefabPath = GimmickPrefab)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var gimmick = Prefab(controller, prefabPath);
            GraphFrameData.SetPrefabLink(controller, gimmick,
                gimmick.GetComponentInChildren<MaMergeAnimator>(true));
            return controller;
        }

        /// <summary>The build body alone: the generated file opens with a cheat sheet that names
        /// half the API in comments, so "the export did not write X" has to be asked of the code
        /// the export actually decided on.</summary>
        static string Body(string code)
        {
            int start = code.IndexOf("BuildGenerated(ControllerBuilder c)");
            Assert.Greater(start, 0, "the generated half has no build body");
            return code.Substring(start);
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

        static bool HasCurve(AnimationClip clip, string path, System.Type type, string property)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                if (binding.path == path && binding.type == type && binding.propertyName == property)
                    return true;
            return false;
        }

        static AnimationClip StateClip(AnimatorController controller, string layer, string state)
        {
            foreach (var child in controller.layers[LayerIndex(controller, layer)].stateMachine.states)
                if (child.state != null && child.state.name == state)
                    return child.state.motion as AnimationClip;
            return null;
        }
#endif

        // ---- running -----------------------------------------------------------

        [Test]
        public void AToggleIsBuiltAgainstThePinnedPrefab()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath);
            var builder = new ControllerBuilder();
            builder.Objects().Toggle("Hat").Shows("Hat").Enables("Cape", "Light");

            var warnings = Generate(builder, controller);

            Assert.IsEmpty(warnings, string.Join("\n", warnings));
            Assert.GreaterOrEqual(LayerIndex(controller, "Hat"), 0, "the toggle's own layer");
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Hat"));
            var on = StateClip(controller, "Hat", "Hat ON");
            Assert.IsTrue(HasCurve(on, "Hat", typeof(GameObject), "m_IsActive"),
                "the path was resolved inside the prefab and derived back out again");
            Assert.IsTrue(HasCurve(on, "Cape", typeof(Light), "m_Enabled"));

            var saved = GraphFrameData.GetObjectGadgets(controller);
            Assert.AreEqual(1, saved.Count, "the recipe registers the record the wizard would");
            Assert.AreSame(
                In(AssetDatabase.LoadAssetAtPath<GameObject>(GimmickPrefab), "Merge/Hat"),
                saved[0].targets[0].target,
                "and what it stores is the reference the lookup found, not the path");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>Running twice regenerates in place. A recipe that added a second layer every
        /// Generate would be a recipe nobody can run twice, which is the whole point of one.
        /// </summary>
        [Test]
        public void RunningTheSameRecipeAgainRebuildsInPlace()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath);
            var first = new ControllerBuilder();
            first.Objects().Toggle("Hat").Shows("Hat");
            Assert.IsEmpty(Generate(first, controller));
            int layers = controller.layers.Length;

            var again = new ControllerBuilder();
            again.Objects().Toggle("Hat").Shows("Hat");
            var warnings = Generate(again, controller);

            Assert.IsEmpty(warnings, string.Join("\n", warnings));
            Assert.AreEqual(layers, controller.layers.Length);
            Assert.AreEqual(1, GraphFrameData.GetObjectGadgets(controller).Count);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>Tree-wired toggles share the one layer, on the first run and on every one
        /// after it — that sharing is the reason the wiring exists.</summary>
        [Test]
        public void TreeWiredTogglesShareOneLayerAcrossRuns()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath);

            for (int run = 0; run < 2; run++)
            {
                var builder = new ControllerBuilder();
                var objects = builder.Objects();
                objects.Toggle("Hat").AsTree().Shows("Hat");
                objects.Toggle("Cape").AsTree().Shows("Cape");
                var warnings = Generate(builder, controller);
                Assert.IsEmpty(warnings, string.Join("\n", warnings));

                int dbt = LayerIndex(controller, "DBT");
                Assert.GreaterOrEqual(dbt, 0, "run " + run);
                var root = (BlendTree)controller.layers[dbt].stateMachine.states[0].state.motion;
                Assert.AreEqual(2, root.children.Length, "one child per toggle, run " + run);
                Assert.AreEqual(2, GraphFrameData.GetObjectGadgets(controller).Count);
            }
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>Without a healthy pin there is nothing for a path to be relative to, so the
        /// step builds NOTHING — a toggle wired to an object it never found is a switch that
        /// silently does nothing and looks exactly like a working one.</summary>
        [Test]
        public void AnUnpinnedControllerBuildsNothingAndSaysWhy()
        {
#if DAERD_MA && DAERD_VRC
            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            var builder = new ControllerBuilder();
            builder.Objects().Toggle("Hat").Shows("Hat");

            var warnings = Generate(builder, controller);

            Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
            StringAssert.Contains("not linked", warnings[0]);
            Assert.AreEqual(1, controller.layers.Length, "nothing was built");
            Assert.IsEmpty(GraphFrameData.GetObjectGadgets(controller));
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>
        /// Every missing target is listed, not the first one. This list is what porting a
        /// gimmick to another prefab is worked through — re-pin, run, read the difference — and
        /// stopping at the first name would make that one rebuild per object.
        /// </summary>
        [Test]
        public void EveryTargetThePrefabDoesNotHaveIsListedAtOnce()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath);
            var builder = new ControllerBuilder();
            var objects = builder.Objects();
            objects.Toggle("Hat").Shows("Hat").Shows("Nowhere");
            objects.Toggle("Cape").Shows("AlsoNowhere");

            var warnings = Generate(builder, controller);

            Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
            StringAssert.Contains("Nowhere", warnings[0]);
            StringAssert.Contains("AlsoNowhere", warnings[0]);
            StringAssert.Contains("Gimmick", warnings[0], "the prefab that was looked in");
            Assert.AreEqual(1, controller.layers.Length,
                "one missing target stops the whole step, including the toggles that would work");
            Assert.IsEmpty(GraphFrameData.GetObjectGadgets(controller));
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>A supplied clip is named by asset path, and what the record ends up holding
        /// is the asset that path loads — with this gadget's rows in it and its file intact.
        /// </summary>
        [Test]
        public void ASuppliedClipIsResolvedByItsAssetPath()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath);
            var clip = new AnimationClip { name = "Hand" };
            AssetDatabase.CreateAsset(clip, Folder + "/Hand.anim");

            var builder = new ControllerBuilder();
            builder.Objects().Toggle("Hat").Shows("Hat").OnClip(Folder + "/Hand.anim");
            var warnings = Generate(builder, controller);

            Assert.IsEmpty(warnings, string.Join("\n", warnings));
            var saved = GraphFrameData.GetObjectGadgets(controller)[0];
            Assert.IsTrue(saved.onClip.userProvided);
            Assert.AreSame(clip, saved.onClip.clip);
            Assert.IsTrue(HasCurve(clip, "Hat", typeof(GameObject), "m_IsActive"));
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>A clip path this project has nothing at stops the run the same way a missing
        /// target does: a toggle whose ON side went missing is not a toggle.</summary>
        [Test]
        public void AClipPathThisProjectHasNothingAtStopsTheRun()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath);
            var builder = new ControllerBuilder();
            builder.Objects().Toggle("Hat").Shows("Hat").OnClip(Folder + "/Missing.anim");

            var warnings = Generate(builder, controller);

            Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
            StringAssert.Contains("Missing.anim", warnings[0]);
            Assert.AreEqual(1, controller.layers.Length);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>
        /// The prefab is read, never written — and the declaration is where that could go wrong,
        /// because a gimmick's parameter store is usually a component INSIDE the pinned prefab.
        /// So declaring is opt-in here, unlike in the wizard: a recipe that generated a
        /// controller and silently added a row to somebody's prefab would be exactly the write
        /// path ADR 0047 exists to refuse.
        /// </summary>
        [Test]
        public void DeclaringIntoThePrefabsStoreIsOptIn()
        {
#if DAERD_MA && DAERD_VRC
            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            var prefab = Prefab(controller, GimmickPrefab, withStore: true);
            GraphFrameData.SetPrefabLink(controller, prefab,
                prefab.GetComponentInChildren<MaMergeAnimator>(true));
            GraphFrameData.SetParameterStore(controller,
                prefab.GetComponent<nadena.dev.modular_avatar.core.ModularAvatarParameters>());

            var quiet = new ControllerBuilder();
            quiet.Objects().Toggle("Hat").Shows("Hat");
            Assert.IsEmpty(Generate(quiet, controller));
            Assert.IsNull(ParameterStore.Of(controller).Find("Hat"),
                "generating a controller does not write to the prefab that hosts the store");

            var asked = new ControllerBuilder();
            asked.Objects().Toggle("Hat").Shows("Hat").Declare();
            Assert.IsEmpty(Generate(asked, controller));
            Assert.IsNotNull(ParameterStore.Of(controller).Find("Hat"),
                "and one line of code is what asks for it");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        // ---- exporting ---------------------------------------------------------

        [Test]
        public void AnObjectGadgetLayerComesBackAsObjectCalls()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath);
            var builder = new ControllerBuilder();
            builder.Objects().Toggle("Hat", "Hat/Shown")
                .Shows("Hat").Hides("Cape").Enables("Cape", "Light").DefaultOn();
            Assert.IsEmpty(Generate(builder, controller));

            var result = RecipeExporter.Export(controller, null, "GimmickRecipe", null);

            Assert.IsEmpty(result.warnings, string.Join("\n", result.warnings));
            string code = result.code;
            StringAssert.Contains("// ---- Layer: Hat (object gadgets) ", code);
            StringAssert.Contains("c.Objects()", code);
            StringAssert.Contains(".Toggle(\"Hat\", \"Hat/Shown\")", code);
            StringAssert.Contains(".Shows(\"Hat\")", code);
            StringAssert.Contains(".Hides(\"Cape\")", code);
            StringAssert.Contains(".Enables(\"Cape\", \"Light\")", code);
            StringAssert.Contains(".DefaultOn()", code);
            StringAssert.DoesNotContain("c.Layer(\"Hat\")", code,
                "the layer is the call's to rebuild, not a wall of states");
            Assert.IsEmpty(result.fields, "and its clips are minted by the call");
            StringAssert.DoesNotContain("BoolParameter(\"Hat/Shown\")", code,
                "the parameter the toggle created is created again by the toggle");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>
        /// The round trip that makes an export worth having, in the shape it is actually used:
        /// the recorded calls replayed against ANOTHER controller, pinned to another copy of the
        /// gimmick, rebuild the same gadget there. (Another prefab and not the same one, because
        /// a pin only reads as healthy when the merge names the controller it is pinned to —
        /// which is exactly what a port is: a second gimmick with its own merge.)
        ///
        /// The clips are compared by what they key rather than by identity: they are freshly
        /// minted sub-assets of a different controller, so nothing else could be true of them.
        /// </summary>
        [Test]
        public void TheExportedCallsRebuildTheGadgetOnAnotherCopyOfTheGimmick()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath);
            var builder = new ControllerBuilder();
            builder.Objects().Toggle("Hat").Shows("Hat").BlendShape("Cape", "Fold", 0f, 100f);
            Assert.IsEmpty(Generate(builder, controller));

            var result = RecipeExporter.Export(controller, null, "GimmickRecipe", null);
            Assert.IsEmpty(result.warnings, string.Join("\n", result.warnings));

            var ported = Pinned(PortedPath, PortedPrefab);
            var replayWarnings = Generate(result.replayed, ported);

            Assert.IsEmpty(replayWarnings, string.Join("\n", replayWarnings));
            Assert.AreEqual(controller.layers.Length, ported.layers.Length);
            Assert.GreaterOrEqual(LayerIndex(ported, "Hat"), 0);
            Assert.IsNotNull(DbtBuilder.FindParameter(ported, "Hat"));

            foreach (var state in new[] { "Hat ON", "Hat OFF" })
            {
                var mine = StateClip(controller, "Hat", state);
                var theirs = StateClip(ported, "Hat", state);
                Assert.IsNotNull(theirs, state);
                Assert.AreEqual(AnimationUtility.GetCurveBindings(mine).Length,
                    AnimationUtility.GetCurveBindings(theirs).Length, state);
                Assert.IsTrue(HasCurve(theirs, "Hat", typeof(GameObject), "m_IsActive"), state);
                Assert.IsTrue(HasCurve(theirs, "Cape", typeof(SkinnedMeshRenderer),
                    "blendShape.Fold"), state);
            }

            var saved = GraphFrameData.GetObjectGadgets(ported);
            Assert.AreEqual(1, saved.Count);
            Assert.AreSame(
                In(AssetDatabase.LoadAssetAtPath<GameObject>(PortedPrefab), "Merge/Hat"),
                saved[0].targets[0].target,
                "the replay resolved the exported path inside the prefab IT is pinned to");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>An object whose reference no longer resolves has no path to export, so the
        /// layer falls back to the states it really is — and the export says which gadget and
        /// which layer, rather than writing a call that describes less than the controller
        /// holds.</summary>
        [Test]
        public void AGadgetWhoseTargetIsGoneFallsBackToTheRawStates()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(ControllerPath);
            var builder = new ControllerBuilder();
            builder.Objects().Toggle("Hat").Shows("Hat");
            Assert.IsEmpty(Generate(builder, controller));

            var contents = PrefabUtility.LoadPrefabContents(GimmickPrefab);
            Object.DestroyImmediate(contents.transform.Find("Merge/Hat").gameObject);
            PrefabUtility.SaveAsPrefabAsset(contents, GimmickPrefab);
            PrefabUtility.UnloadPrefabContents(contents);

            var result = RecipeExporter.Export(controller, null, "GimmickRecipe", null);

            Assert.AreEqual(1, result.warnings.Count, string.Join("\n", result.warnings));
            StringAssert.Contains("Hat", result.warnings[0]);
            StringAssert.Contains("c.Layer(\"Hat\")", result.code);
            StringAssert.DoesNotContain("c.Objects()", Body(result.code));
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }
    }
}

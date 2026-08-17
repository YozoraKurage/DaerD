using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
#if DAERD_MA && DAERD_VRC
using MaMergeAnimator = nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator;
using MaPathMode = nadena.dev.modular_avatar.core.MergeAnimatorPathMode;
#endif

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The object gadget family's half of a toggle: deciding whether it may be built at all,
    /// turning references into the pinned prefab into the paths a curve needs, and owning what
    /// it built well enough to take it back out again.
    ///
    /// These tests write a real prefab and a real controller to disk, because the two claims
    /// worth most cannot be made in memory. One is that a target survives being RENAMED — the
    /// whole reason targets are references and paths are derived (ADR 0044) — and the other is
    /// that generated clips end up inside the .controller where the Project window can list
    /// them, which is a question about the imported artifact rather than about objects held in
    /// memory.
    ///
    /// Everything here needs a Merge Animator to exist, so the whole file skips by name where
    /// Modular Avatar is absent — which is the honest answer: without MA a controller cannot be
    /// pinned to a prefab, and an object gadget has nothing to be relative to.
    /// </summary>
    public class ObjectGadgetTests
    {
        const string Folder = "Assets/DDObjectGadget";
        const string ControllerPath = Folder + "/Gimmick.controller";
        const string OtherControllerPath = Folder + "/Other.controller";
        const string GimmickPrefab = Folder + "/Gimmick.prefab";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", "DDObjectGadget");
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            PrefabLinks.ForgetCandidates();
            GraphFrameData.ForgetHolders();
        }

        /// <summary>A controller on disk — the gadget's clips are sub-assets of it, so an
        /// in-memory one would have nowhere to keep them. It comes with the one layer Unity
        /// gives it, which is why everything a gadget adds lands at index 1.</summary>
        static AnimatorController Controller(string path) =>
            AnimatorController.CreateAnimatorControllerAtPath(path);

#if DAERD_MA && DAERD_VRC
        /// <summary>
        /// The ordinary gimmick shape: a root, the merge one object down, two things under the
        /// merge to animate, and one object OUTSIDE the merge's subtree. The last is not
        /// decoration — an object beside the merge has no path relative to it, and refusing that
        /// by name is one of the rules here.
        /// </summary>
        static GameObject Prefab(AnimatorController merged, bool withStore = false)
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
            var outside = new GameObject("Outside");
            outside.transform.SetParent(built.transform);

            var saved = PrefabUtility.SaveAsPrefabAsset(built, GimmickPrefab);
            Object.DestroyImmediate(built);
            return saved;
        }

        static GameObject In(GameObject prefab, string path) =>
            prefab.transform.Find(path).gameObject;

        static MaMergeAnimator MergeIn(GameObject prefab) =>
            prefab.GetComponentInChildren<MaMergeAnimator>(true);

        static void Pin(AnimatorController controller, GameObject prefab) =>
            GraphFrameData.SetPrefabLink(controller, prefab, MergeIn(prefab));

        /// <summary>A controller pinned to a fresh gimmick prefab — the state every gadget
        /// starts from.</summary>
        static AnimatorController Pinned(out GameObject prefab, bool withStore = false)
        {
            var controller = Controller(ControllerPath);
            prefab = Prefab(controller, withStore);
            Pin(controller, prefab);
            return controller;
        }

        static GraphFrameData.ObjectGadgetConfig NewConfig(ToggleBuilder.Mode mode,
            params GameObject[] targets)
        {
            var config = new GraphFrameData.ObjectGadgetConfig
            {
                kind = (int)ObjectGadgets.Kind.Toggle,
                name = "Hat",
                parameter = "Hat",
                mode = (int)mode,
                declare = false,
            };
            foreach (var target in targets)
                config.targets.Add(new GraphFrameData.ObjectTargetRecord { target = target });
            return config;
        }

        /// <summary>The clips a state or a tree is playing, read back off the imported
        /// artifact.</summary>
        static int SubAssetClips(AnimatorController controller)
        {
            int count = 0;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(
                         AssetDatabase.GetAssetPath(controller)))
                if (asset is AnimationClip) count++;
            return count;
        }

        static bool HasCurve(AnimationClip clip, string path, System.Type type, string property)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                if (binding.path == path && binding.type == type && binding.propertyName == property)
                    return true;
            return false;
        }
#endif

        // ---- what may be built ------------------------------------------------

        [Test]
        public void AnUnpinnedControllerIsRefusedByName()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            var prefab = Prefab(controller);

            var refusal = ObjectGadgets.Validate(controller,
                NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat")));

            Assert.IsNotNull(refusal, "there is nothing for the paths to be relative to");
            StringAssert.Contains("not linked", refusal);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void EveryBrokenPinStateIsRefusedInItsOwnWords()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            var target = In(prefab, "Merge/Hat");
            var config = NewConfig(ToggleBuilder.Mode.Layer, target);
            Assert.IsNull(ObjectGadgets.Validate(controller, config), "the healthy pin builds");

            var other = Controller(OtherControllerPath);
            MergeIn(prefab).animator = other;
            var diverged = ObjectGadgets.Validate(controller, config);
            Assert.IsNotNull(diverged);
            StringAssert.Contains("Gimmick", diverged, "a refusal names what it is talking about");

            MergeIn(prefab).animator = controller;
            AssetDatabase.DeleteAsset(GimmickPrefab);
            Assert.IsNotNull(ObjectGadgets.Validate(controller, config),
                "the prefab is gone, so its objects cannot be resolved");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>
        /// Absolute paths address the avatar's hierarchy, and a gimmick prefab does not know
        /// where in an avatar it will be dropped. There is no approximation to offer, so the
        /// limit is named rather than guessed around.
        /// </summary>
        [Test]
        public void AnAbsoluteMergeIsRefusedRatherThanGuessedAt()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            MergeIn(prefab).pathMode = MaPathMode.Absolute;

            var refusal = ObjectGadgets.Validate(controller,
                NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat")));

            Assert.IsNotNull(refusal);
            StringAssert.Contains("Absolute", refusal);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void ATargetOutsideTheMergeIsRefusedByName()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);

            var refusal = ObjectGadgets.Validate(controller,
                NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Outside")));

            Assert.IsNotNull(refusal, "nothing beside the merge has a path relative to it");
            StringAssert.Contains("Outside", refusal);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void TheWiringDecidesWhatTypeTheParameterMustBe()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            controller.AddParameter("Hat", AnimatorControllerParameterType.Float);
            var target = In(prefab, "Merge/Hat");

            Assert.IsNotNull(ObjectGadgets.Validate(controller,
                NewConfig(ToggleBuilder.Mode.Layer, target)), "a Bool layer needs a Bool");
            Assert.IsNull(ObjectGadgets.Validate(controller,
                NewConfig(ToggleBuilder.Mode.DirectBlendTree, target)),
                "and the tree is blended by exactly this Float");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void ABindingThisProjectHasNoComponentForIsRefusedByName()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            var config = NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat"));
            config.targets[0].bindings.Add(new GraphFrameData.BindingRecord
            {
                typeName = "NoSuchComponent",
                property = "m_Enabled",
            });

            var refusal = ObjectGadgets.Validate(controller, config);

            Assert.IsNotNull(refusal, "a curve bound to nothing would be written silently");
            StringAssert.Contains("NoSuchComponent", refusal);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void TwoGadgetsCannotDriveTheSameParameter()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            Assert.IsTrue(ObjectGadgets.Apply(controller,
                NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat"))));

            var second = NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Cape"));
            second.name = "Cape";
            var refusal = ObjectGadgets.Validate(controller, second);

            Assert.IsNotNull(refusal, "the parameter is the key a record is saved under");
            StringAssert.Contains("Hat", refusal);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        // ---- applying ---------------------------------------------------------

        [Test]
        public void AGadgetIsRecordedWithTheControllerAndComesBackOffDisk()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            var config = NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat"));
            Assert.IsTrue(ObjectGadgets.Apply(controller, config));

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ControllerPath, ImportAssetOptions.ForceUpdate);
            GraphFrameData.ForgetHolders();
            var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);

            var saved = GraphFrameData.GetObjectGadgets(reloaded);
            Assert.AreEqual(1, saved.Count);
            Assert.AreEqual("Hat", saved[0].parameter);
            Assert.AreEqual((int)ObjectGadgets.Kind.Toggle, saved[0].kind);
            Assert.IsTrue(saved[0].createdParameter);
            Assert.AreSame(In(AssetDatabase.LoadAssetAtPath<GameObject>(GimmickPrefab), "Merge/Hat"),
                saved[0].targets[0].target,
                "the target is stored as a reference INTO the prefab, not as a path");
            Assert.IsNotNull(saved[0].layer, "and the layer it built is identified by its machine");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void TheDerivedPathIsWhatTheClipsAreKeyedBy()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            var config = NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat"));
            Assert.IsTrue(ObjectGadgets.Apply(controller, config));

            var states = controller.layers[1].stateMachine.states;
            Assert.IsTrue(HasCurve((AnimationClip)states[0].state.motion, "Hat",
                typeof(GameObject), "m_IsActive"),
                "the path is relative to the merge, not to the prefab root");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>The merge's own object is a legitimate target, and its path is "" — a gadget
        /// that hides the object the merge sits on is a normal thing to want.</summary>
        [Test]
        public void TheMergesOwnObjectIsAnEmptyPath()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            var merge = In(prefab, "Merge");
            Assert.AreEqual(string.Empty, ObjectGadgets.PathOf(merge.transform, merge));

            var config = NewConfig(ToggleBuilder.Mode.Layer, merge);
            Assert.IsNull(ObjectGadgets.Validate(controller, config));
            Assert.IsTrue(ObjectGadgets.Apply(controller, config));

            var states = controller.layers[1].stateMachine.states;
            Assert.IsTrue(HasCurve((AnimationClip)states[0].state.motion, string.Empty,
                typeof(GameObject), "m_IsActive"));
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>
        /// The reason a target is a reference. The object is renamed inside the prefab and the
        /// gadget is regenerated: the record still points at the same object, and the curve
        /// follows it. A saved path would have kept keying an object that no longer exists —
        /// silently, since nothing about a stale path looks wrong.
        /// </summary>
        [Test]
        public void RenamingATargetMovesTheDerivedPathWithIt()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            var config = NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat"));
            Assert.IsTrue(ObjectGadgets.Apply(controller, config));

            var contents = PrefabUtility.LoadPrefabContents(GimmickPrefab);
            contents.transform.Find("Merge/Hat").name = "Cap";
            PrefabUtility.SaveAsPrefabAsset(contents, GimmickPrefab);
            PrefabUtility.UnloadPrefabContents(contents);

            var saved = GraphFrameData.GetObjectGadgets(controller)[0];
            Assert.IsNotNull(saved.targets[0].target, "the reference survived the rename");
            Assert.IsTrue(ObjectGadgets.Apply(controller, saved, saved));

            var states = controller.layers[1].stateMachine.states;
            var clip = (AnimationClip)states[0].state.motion;
            Assert.IsTrue(HasCurve(clip, "Cap", typeof(GameObject), "m_IsActive"),
                "the path is derived again on every apply");
            Assert.IsFalse(HasCurve(clip, "Hat", typeof(GameObject), "m_IsActive"),
                "and nothing is left keying where the object used to be");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void TheClipsAreSubAssetsOfTheController()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            Assert.IsTrue(ObjectGadgets.Apply(controller,
                NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat"))));

            Assert.AreEqual(2, SubAssetClips(controller),
                "both clips are inside the .controller, listed from the imported artifact — "
                + "AddObjectToAsset alone leaves them invisible there");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void TheRowsThatWereWrittenAreBooked()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            var config = NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat"));
            config.targets[0].bindings.Add(new GraphFrameData.BindingRecord
            {
                typeName = "Light",
                property = "m_Enabled",
            });
            Assert.IsTrue(ObjectGadgets.Apply(controller, config));

            var saved = GraphFrameData.GetObjectGadgets(controller)[0];
            foreach (var output in new[] { saved.onClip, saved.offClip })
            {
                Assert.AreEqual(2, output.written.Count,
                    "the ledger is kept even for a clip DaerD owns, so there is one bookkeeping");
                Assert.AreEqual("Hat", output.written[0].path);
                Assert.AreEqual("GameObject", output.written[0].typeName);
                Assert.AreEqual("m_IsActive", output.written[0].property);
                Assert.AreEqual("Light", output.written[1].typeName);
                Assert.IsFalse(output.userProvided);
            }
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void RegeneratingReplacesTheGadgetInsteadOfJoiningIt()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            var config = NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat"));
            Assert.IsTrue(ObjectGadgets.Apply(controller, config));

            var saved = GraphFrameData.GetObjectGadgets(controller)[0];
            Assert.IsNull(ObjectGadgets.Validate(controller, saved, saved),
                "a gadget's own parameter is not a collision with itself");
            Assert.IsTrue(ObjectGadgets.Apply(controller, saved, saved));

            Assert.AreEqual(2, controller.layers.Length, "one layer, not two");
            Assert.AreEqual(1, GraphFrameData.GetObjectGadgets(controller).Count);
            Assert.AreEqual(2, SubAssetClips(controller), "and the old clips went with it");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        // ---- the tree wiring --------------------------------------------------

        [Test]
        public void TreeWiredGadgetsShareOneLayerAndRemoveOnlyTheirOwnChild()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            var first = NewConfig(ToggleBuilder.Mode.DirectBlendTree, In(prefab, "Merge/Hat"));
            Assert.IsTrue(ObjectGadgets.Apply(controller, first));

            var second = NewConfig(ToggleBuilder.Mode.DirectBlendTree, In(prefab, "Merge/Cape"));
            second.name = "Cape";
            second.parameter = "Cape";
            second.layer = first.layer;   // what the wizard's layer choice hands over
            Assert.IsTrue(ObjectGadgets.Apply(controller, second));

            Assert.AreEqual(2, controller.layers.Length, "both toggles share one DBT layer");
            var root = (BlendTree)controller.layers[1].stateMachine.states[0].state.motion;
            Assert.AreEqual(2, root.children.Length);

            ObjectGadgets.Remove(controller, second);
            DbtBuilder.CommitSubAssets(controller);

            root = (BlendTree)controller.layers[1].stateMachine.states[0].state.motion;
            Assert.AreEqual(1, root.children.Length, "one child fewer, and the layer stays");
            Assert.AreSame(first.tree, root.children[0].motion);
            Assert.IsNull(DbtBuilder.FindParameter(controller, "Cape"));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Hat"));
            Assert.AreEqual(1, GraphFrameData.GetObjectGadgets(controller).Count);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void ALayerThatCannotHostATreeIsRefusedByName()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            controller.layers[0].stateMachine.AddState("Busy");
            var config = NewConfig(ToggleBuilder.Mode.DirectBlendTree, In(prefab, "Merge/Hat"));
            config.layer = controller.layers[0].stateMachine;

            Assert.IsNotNull(ObjectGadgets.Validate(controller, config),
                "a layer carrying states that are not Direct trees would be joined, not shared");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        // ---- removing ---------------------------------------------------------

        [Test]
        public void RemovingTakesTheLayerTheClipsAndTheParameterItCreated()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            var config = NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat"));
            Assert.IsTrue(ObjectGadgets.Apply(controller, config));
            Assert.AreEqual(2, controller.layers.Length);

            ObjectGadgets.Remove(controller, config);
            DbtBuilder.CommitSubAssets(controller);

            Assert.AreEqual(1, controller.layers.Length, "the layer it added is gone");
            Assert.AreEqual(0, SubAssetClips(controller), "and so are the clips it generated");
            Assert.IsNull(DbtBuilder.FindParameter(controller, "Hat"));
            Assert.IsEmpty(GraphFrameData.GetObjectGadgets(controller));
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>A parameter the gadget merely reused belongs to somebody else, and leaving
        /// it is what lets two things be driven by one switch.</summary>
        [Test]
        public void RemovingLeavesAParameterItDidNotCreate()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            controller.AddParameter("Hat", AnimatorControllerParameterType.Bool);
            var config = NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat"));
            Assert.IsTrue(ObjectGadgets.Apply(controller, config));
            Assert.IsFalse(config.createdParameter);

            ObjectGadgets.Remove(controller, config);
            DbtBuilder.CommitSubAssets(controller);

            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Hat"),
                "the gadget found this parameter rather than making it");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        // ---- declaring --------------------------------------------------------

        [Test]
        public void DeclaringGoesThroughTheControllersParameterStore()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab, withStore: true);
            var store = prefab.GetComponent<nadena.dev.modular_avatar.core.ModularAvatarParameters>();
            GraphFrameData.SetParameterStore(controller, store);

            var config = NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat"));
            config.declare = true;
            Assert.IsNull(ObjectGadgets.Note(controller, config), "there is a store to declare in");
            Assert.IsTrue(ObjectGadgets.Apply(controller, config));

            var row = ParameterStore.Of(controller).Find("Hat");
            Assert.IsNotNull(row, "the one route to a declaration is the store");
            Assert.AreEqual(VrcExpressionParameters.ValueType.Bool, row.valueType,
                "the animator's own type is what is copied");
            Assert.IsTrue(row.synced);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void AGadgetWithNowhereToDeclareIsNotedRatherThanRefused()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Pinned(out var prefab);
            var config = NewConfig(ToggleBuilder.Mode.Layer, In(prefab, "Merge/Hat"));
            config.declare = true;

            Assert.IsNull(ObjectGadgets.Validate(controller, config),
                "the gadget works; its parameter simply reaches no avatar");
            Assert.IsNotNull(ObjectGadgets.Note(controller, config));
            Assert.IsTrue(ObjectGadgets.Apply(controller, config));
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }
    }
}

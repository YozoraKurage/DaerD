using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// DaerD's saved state living in a file of its own, so a controller can be handed to
    /// somebody with none of it inside. The thing worth pinning is not that a file appears —
    /// it is that everything about the holder keeps working once it has moved: the lookup finds
    /// it (including after the controller is moved out from under the mirrored path), writes
    /// reach it, and the references it holds into the controller survive as cross-asset ones.
    /// </summary>
    public class SidecarTests
    {
        const string Folder = "Assets/DaerDSidecarTest";
        const string MovedFolder = "Assets/DaerDSidecarTestMoved";
        const string ControllerPath = Folder + "/DaerDSidecar.controller";
        const string ClipPath = Folder + "/DaerDSidecarEmpty.anim";
        const string MirrorFolder = GraphFrameData.SidecarRoot + "/DaerDSidecarTest";
        const string MovedMirrorFolder = GraphFrameData.SidecarRoot + "/DaerDSidecarTestMoved";
        const string ExpectedSidecar = MirrorFolder + "/DaerDSidecar.asset";

        static void WithSavedController(System.Action<AnimatorController> body)
        {
            AssetDatabase.CreateFolder("Assets", "DaerDSidecarTest");
            AssetDatabase.CreateAsset(new AnimatorController(), ControllerPath);
            try
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
                controller.AddLayer("Base");
                body(controller);
            }
            finally
            {
                // Every path this suite can leave a file at, whether or not the body got that
                // far: a failed assertion must not leave a zz_DaerD folder behind for the next
                // test to trip over.
                GraphFrameData.ForgetHolders();
                AssetDatabase.DeleteAsset(Folder);
                AssetDatabase.DeleteAsset(MovedFolder);
                AssetDatabase.DeleteAsset(MirrorFolder);
                AssetDatabase.DeleteAsset(MovedMirrorFolder);
                DeleteSidecarRootIfEmpty();
            }
        }

        /// <summary>The root is shared with whatever else the project keeps there, so it goes
        /// only when this suite left it empty — the same rule the production prune follows.</summary>
        static void DeleteSidecarRootIfEmpty()
        {
            if (!AssetDatabase.IsValidFolder(GraphFrameData.SidecarRoot)) return;
            var full = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length)
                + GraphFrameData.SidecarRoot;
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                AssetDatabase.DeleteAsset(GraphFrameData.SidecarRoot);
        }

        static bool HolderIsInside(string assetPath) =>
            AssetDatabase.LoadAllAssetsAtPath(assetPath).Any(asset => asset is GraphFrameData);

        [Test]
        public void SidecarPathFor_MirrorsTheControllersFoldersUnderTheRoot()
        {
            Assert.AreEqual(GraphFrameData.SidecarRoot + "/FX.asset",
                GraphFrameData.SidecarPathFor("Assets/FX.controller"),
                "a controller directly under Assets/ mirrors to the root itself");
            Assert.AreEqual(GraphFrameData.SidecarRoot + "/Chara/Gimmick/FX.asset",
                GraphFrameData.SidecarPathFor("Assets/Chara/Gimmick/FX.controller"),
                "every folder between Assets/ and the controller is mirrored");
            Assert.AreEqual(GraphFrameData.SidecarRoot + "/Chara/FX.v2.asset",
                GraphFrameData.SidecarPathFor("Assets/Chara/FX.v2.controller"),
                "only the last extension is replaced");

            // Outside Assets/ there is nowhere to write one, and saying so here is what keeps
            // the refusal a stated answer instead of an asset pipeline error later.
            Assert.IsNull(GraphFrameData.SidecarPathFor("Packages/com.example.thing/FX.controller"));
            Assert.IsNull(GraphFrameData.SidecarPathFor(""));
            Assert.IsNull(GraphFrameData.SidecarPathFor(null));
        }

        [Test]
        public void Detach_MovesTheHolderOut_AndItStillPointsIntoTheController()
        {
            WithSavedController(controller =>
            {
                var machine = controller.layers[0].stateMachine;
                var data = GraphFrameData.GetOrCreate(controller);
                data.AddFrame(machine, new Rect(0f, 0f, 100f, 100f), "Group");

                Assert.IsNull(GraphFrameData.Detach(controller));

                Assert.IsFalse(HolderIsInside(ControllerPath),
                    "the controller file still carries the holder");
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GraphFrameData>(ExpectedSidecar),
                    "no sidecar at the path the controller's own path derives");

                var found = GraphFrameData.Find(controller);
                Assert.IsNotNull(found, "the lookup lost the holder when it moved");
                Assert.AreEqual(ExpectedSidecar, AssetDatabase.GetAssetPath(found));
                Assert.AreEqual(1, found.frames.Count);
                Assert.AreEqual("Group", found.frames[0].title);
                Assert.AreEqual(GraphFrameData.SidecarPathOf(controller), ExpectedSidecar);

                // The frame's reference into the controller is the whole question: it was a
                // reference between two objects in one file and is now one between two files.
                Assert.AreSame(machine, found.frames[0].stateMachine);
                Assert.Contains(ControllerPath, AssetDatabase.GetDependencies(ExpectedSidecar, false),
                    "the reference was not written to the file as a cross-asset one");
            });
        }

        [Test]
        public void AWriteAfterDetaching_LandsInTheSidecarFile()
        {
            WithSavedController(controller =>
            {
                var clip = new AnimationClip();
                AssetDatabase.CreateAsset(clip, ClipPath);
                GraphFrameData.GetOrCreate(controller);
                Assert.IsNull(GraphFrameData.Detach(controller));

                GraphFrameData.SetEmptyClip(controller, AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath));
                AssetDatabase.SaveAssets();

                Assert.AreEqual(clip, GraphFrameData.GetEmptyClip(controller));
                Assert.Contains(ClipPath, AssetDatabase.GetDependencies(ExpectedSidecar, false),
                    "the assignment never reached the sidecar file");
                Assert.IsFalse(HolderIsInside(ControllerPath),
                    "the write recreated a holder inside the controller");
            });
        }

        [Test]
        public void MovingTheController_LeavesTheSidecarWhereItIs_AndTheSweepFindsIt()
        {
            WithSavedController(controller =>
            {
                var data = GraphFrameData.GetOrCreate(controller);
                data.AddFrame(controller.layers[0].stateMachine, new Rect(0f, 0f, 10f, 10f), "Kept");
                Assert.IsNull(GraphFrameData.Detach(controller));

                AssetDatabase.CreateFolder("Assets", "DaerDSidecarTestMoved");
                var moved = MovedFolder + "/DaerDSidecar.controller";
                Assert.IsEmpty(AssetDatabase.MoveAsset(ControllerPath, moved));
                GraphFrameData.ForgetHolders();

                // The mirror is where a sidecar is BORN, not where it is kept: nothing moved it,
                // and the derived path now names a file that does not exist.
                Assert.IsNull(AssetDatabase.LoadAssetAtPath<GraphFrameData>(
                    GraphFrameData.SidecarPathFor(moved)));
                var found = GraphFrameData.Find(
                    AssetDatabase.LoadAssetAtPath<AnimatorController>(moved));
                Assert.IsNotNull(found, "the sweep did not recover the sidecar after the move");
                Assert.AreEqual(ExpectedSidecar, AssetDatabase.GetAssetPath(found));
                Assert.AreEqual("Kept", found.frames[0].title);

                AssetDatabase.MoveAsset(moved, ControllerPath);
            });
        }

        [Test]
        public void Embed_PutsTheDataBackInside_AndTakesTheFileAway()
        {
            WithSavedController(controller =>
            {
                var machine = controller.layers[0].stateMachine;
                GraphFrameData.GetOrCreate(controller).AddFrame(machine, new Rect(1f, 2f, 3f, 4f), "Group");
                Assert.IsNull(GraphFrameData.Detach(controller));

                Assert.IsNull(GraphFrameData.Embed(controller));

                Assert.IsNull(AssetDatabase.LoadAssetAtPath<GraphFrameData>(ExpectedSidecar),
                    "the sidecar file outlived the data it held");
                Assert.IsFalse(AssetDatabase.IsValidFolder(MirrorFolder),
                    "the mirrored folder was left behind empty");
                Assert.IsTrue(HolderIsInside(ControllerPath),
                    "the holder did not come back into the controller file");

                var found = GraphFrameData.Find(controller);
                Assert.IsNotNull(found);
                Assert.AreEqual(ControllerPath, AssetDatabase.GetAssetPath(found));
                Assert.IsNull(found.owner, "an embedded holder claims an owner it does not need");
                Assert.AreEqual(1, found.frames.Count);
                Assert.AreEqual("Group", found.frames[0].title);
                Assert.AreEqual(new Rect(1f, 2f, 3f, 4f), found.frames[0].bounds);
                Assert.AreSame(machine, found.frames[0].stateMachine);
                Assert.IsNull(GraphFrameData.SidecarPathOf(controller));
            });
        }

        [Test]
        public void Discard_WhileDetached_TakesTheFileWithIt()
        {
            WithSavedController(controller =>
            {
                GraphFrameData.GetOrCreate(controller);
                Assert.IsNull(GraphFrameData.Detach(controller));

                GraphFrameData.Discard(controller);

                Assert.IsNull(GraphFrameData.Find(controller));
                Assert.IsNull(AssetDatabase.LoadAssetAtPath<GraphFrameData>(ExpectedSidecar),
                    "discarding left the sidecar file on disk");
                Assert.IsFalse(AssetDatabase.IsValidFolder(MirrorFolder));
                Assert.IsFalse(HolderIsInside(ControllerPath));
            });
        }

        [Test]
        public void AnInMemoryController_IsToldWhyItCannotDetach()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");

            var reason = GraphFrameData.Detach(controller);

            Assert.IsNotEmpty(reason, "the refusal has to say why — it is what a log carries");
            Assert.IsNull(GraphFrameData.SidecarPathFor(AssetDatabase.GetAssetPath(controller)));

            Object.DestroyImmediate(controller);
        }
    }
}

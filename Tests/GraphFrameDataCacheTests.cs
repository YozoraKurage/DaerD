using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Remembering where a controller's DaerD data lives. The lookup underneath loads every
    /// sub-asset of the .controller, and panels ask during their repaint — but a remembered
    /// answer is only worth having if it is never the wrong one, and the two ways it could be
    /// are both here: a holder created after the answer "there is none", and a holder that has
    /// since been destroyed.
    /// </summary>
    public class GraphFrameDataCacheTests
    {
        const string Path = "Assets/DaerDFrameDataCacheTest.controller";

        static void WithSavedController(System.Action<AnimatorController> body)
        {
            AssetDatabase.CreateAsset(new AnimatorController(), Path);
            try
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(Path);
                controller.AddLayer("Base");
                body(controller);
            }
            finally
            {
                AssetDatabase.DeleteAsset(Path);
            }
        }

        [Test]
        public void Find_KeepsHandingBackTheSameHolder()
        {
            WithSavedController(controller =>
            {
                var created = GraphFrameData.GetOrCreate(controller);

                Assert.AreSame(created, GraphFrameData.Find(controller));
                Assert.AreSame(created, GraphFrameData.Find(controller));
            });
        }

        [Test]
        public void AHolderCreatedAfterTheAnswerWasNo_IsFoundWithoutBeingToldTwice()
        {
            WithSavedController(controller =>
            {
                // The answer "this controller has none" is remembered like any other. Creating
                // one has to write through, or the lookup keeps repeating an answer that has
                // stopped being true — with nothing to prompt it to look again.
                Assert.IsNull(GraphFrameData.Find(controller));

                var created = GraphFrameData.GetOrCreate(controller);

                Assert.AreSame(created, GraphFrameData.Find(controller));
            });
        }

        [Test]
        public void ADestroyedHolder_IsNotHandedBack()
        {
            WithSavedController(controller =>
            {
                var holder = GraphFrameData.GetOrCreate(controller);
                Assert.AreSame(holder, GraphFrameData.Find(controller));

                Object.DestroyImmediate(holder, true);
                var again = GraphFrameData.Find(controller);

                Assert.IsFalse(!ReferenceEquals(again, null) && again == null,
                    "the destroyed holder came back instead of the lookup running again");
            });
        }

        [Test]
        public void Discard_TakesTheHolderOutOfTheFile_AndLeavesTheControllerAlone()
        {
            WithSavedController(controller =>
            {
                GraphFrameData.GetOrCreate(controller);
                int layers = controller.layers.Length;

                GraphFrameData.Discard(controller);

                Assert.IsNull(GraphFrameData.Find(controller));
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(Path))
                    Assert.IsFalse(asset is GraphFrameData,
                        "the holder is still inside the .controller file");
                Assert.AreEqual(layers, controller.layers.Length);
            });
        }

        [Test]
        public void Discard_WhenThereIsNothing_DoesNothing()
        {
            WithSavedController(controller =>
            {
                Assert.IsNull(GraphFrameData.Find(controller));
                Assert.DoesNotThrow(() => GraphFrameData.Discard(controller));
                Assert.IsNull(GraphFrameData.Find(controller));
            });
        }

        [Test]
        public void ForgettingEverything_LosesNothingButTheShortcut()
        {
            WithSavedController(controller =>
            {
                var created = GraphFrameData.GetOrCreate(controller);

                GraphFrameData.ForgetHolders();

                Assert.AreSame(created, GraphFrameData.Find(controller),
                    "the holder is on the asset; the table is only a shortcut to it");
            });
        }

        [Test]
        public void TwoControllers_KeepTheirOwnHolders()
        {
            const string second = "Assets/DaerDFrameDataCacheTest2.controller";
            WithSavedController(controller =>
            {
                AssetDatabase.CreateAsset(new AnimatorController(), second);
                try
                {
                    var other = AssetDatabase.LoadAssetAtPath<AnimatorController>(second);
                    var first = GraphFrameData.GetOrCreate(controller);
                    var theirs = GraphFrameData.GetOrCreate(other);

                    Assert.AreNotSame(first, theirs);
                    Assert.AreSame(first, GraphFrameData.Find(controller));
                    Assert.AreSame(theirs, GraphFrameData.Find(other));
                }
                finally
                {
                    AssetDatabase.DeleteAsset(second);
                }
            });
        }

        [Test]
        public void AnInMemoryController_HasNowhereToKeepAnything()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");

            // Not a gap in the cache — there is no asset to hang a holder on, so nothing
            // written to one is ever read back. It is why anything testing saved configs has
            // to build a controller on disk.
            Assert.IsNull(GraphFrameData.Find(controller));
            Assert.IsEmpty(GraphFrameData.GetAsyncSyncs(controller));

            Object.DestroyImmediate(controller);
        }
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Turning the saved records round: which layers of a controller DaerD generated, and on
    /// whose behalf. Every claim is read off the same records the home screen lists, so what is
    /// pinned here is that the turn-round is COMPLETE — a kind of record that stopped being read
    /// would take a layer's mark away silently, and the layer list is where somebody learns that
    /// hand-editing there will be overwritten.
    ///
    /// The controller is on disk because that is the only shape that keeps a saved holder: an
    /// in-memory one builds a fresh holder per call and nothing written to it is read back.
    /// </summary>
    public class LayerOwnersTests
    {
        const string Path = "Assets/DaerDLayerOwnersTest.controller";

        /// <summary>Motions and recipe stand-ins the records point at. They are only there to be
        /// non-null, but they are Unity objects and are cleaned up like any other.</summary>
        readonly List<Object> _made = new List<Object>();

        void WithSavedController(System.Action<AnimatorController> body)
        {
            AssetDatabase.CreateAsset(new AnimatorController(), Path);
            try
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(Path);
                GraphFrameData.ForgetHolders();
                body(controller);
            }
            finally
            {
                foreach (var made in _made)
                    if (made != null) Object.DestroyImmediate(made);
                _made.Clear();
                AssetDatabase.DeleteAsset(Path);
                GraphFrameData.ForgetHolders();
            }
        }

        static AnimatorStateMachine Layer(AnimatorController controller, string name)
        {
            controller.AddLayer(name);
            return controller.layers[controller.layers.Length - 1].stateMachine;
        }

        T Made<T>(string name) where T : Object, new()
        {
            var made = new T { name = name };
            _made.Add(made);
            return made;
        }

        static List<LayerOwnerKind> Kinds(AnimatorController controller, AnimatorStateMachine machine)
        {
            var kinds = new List<LayerOwnerKind>();
            foreach (var owner in LayerOwners.Of(controller, machine))
                kinds.Add(owner.kind);
            return kinds;
        }

        [Test]
        public void EverySavedRecordMarksTheLayerItNames()
        {
            WithSavedController(controller =>
            {
                var cycle = Layer(controller, "Cycle");
                var ready = Layer(controller, "Ready");
                var stale = Layer(controller, "Stale");
                var group = Layer(controller, "Group");
                var toggle = Layer(controller, "Toggle");
                var host = Layer(controller, "Host");
                var recipeLayer = Layer(controller, "Recipe");
                var untouched = Layer(controller, "Hand-made");

                GraphFrameData.SaveAsyncSync(controller, new GraphFrameData.AsyncSyncConfig
                {
                    layer = cycle,
                    readyLayer = ready,
                    staleLayer = stale,
                    baseName = "Cyc",
                    groups =
                    {
                        new GraphFrameData.AsyncSyncConfig.SyncGroup { name = "Pair", layer = group },
                    },
                });
                GraphFrameData.SaveObjectGadget(controller, new GraphFrameData.ObjectGadgetConfig
                {
                    parameter = "Hat",
                    name = "Hat",
                    mode = (int)ToggleBuilder.Mode.Layer,
                    layer = toggle,
                });
                GraphFrameData.SaveObjectGadget(controller, new GraphFrameData.ObjectGadgetConfig
                {
                    parameter = "Cape",
                    name = "Cape",
                    mode = (int)ToggleBuilder.Mode.DirectBlendTree,
                    layer = host,
                });
                GraphFrameData.SaveGadget(controller, new GraphFrameData.AapGadgetConfig
                {
                    layer = host,
                    output = "Sum",
                    tree = Made<AnimationClip>("Sum tree"),
                });
                GraphFrameData.SetCodeOwned(controller,
                    new List<AnimatorStateMachine> { recipeLayer }, Made<AnimationClip>("Recipe"));
                LayerOwners.Forget();

                CollectionAssert.AreEqual(new[] { LayerOwnerKind.AsyncSyncCycle },
                    Kinds(controller, cycle));
                CollectionAssert.AreEqual(new[] { LayerOwnerKind.AsyncSyncReady },
                    Kinds(controller, ready));
                CollectionAssert.AreEqual(new[] { LayerOwnerKind.AsyncSyncStale },
                    Kinds(controller, stale));
                CollectionAssert.AreEqual(new[] { LayerOwnerKind.AsyncSyncGroup },
                    Kinds(controller, group));
                CollectionAssert.AreEqual(new[] { LayerOwnerKind.ObjectGadget },
                    Kinds(controller, toggle));
                CollectionAssert.AreEqual(new[] { LayerOwnerKind.Recipe },
                    Kinds(controller, recipeLayer));

                // A shared blend tree host answers for everything hung in it, which is the whole
                // reason one layer's answer is a list rather than one kind.
                CollectionAssert.AreEquivalent(
                    new[] { LayerOwnerKind.DbtGadgetHost, LayerOwnerKind.ObjectGadgetHost },
                    Kinds(controller, host));

                Assert.IsEmpty(LayerOwners.Of(controller, untouched),
                    "a layer no record names belongs to nobody");
                Assert.IsEmpty(LayerOwners.Of(controller, null));
                Assert.IsEmpty(LayerOwners.Of(null, untouched));
            });
        }

        [Test]
        public void TheTooltipNamesWhatOwnsTheLayerAndCountsWhatMerelyHangsInIt()
        {
            WithSavedController(controller =>
            {
                var host = Layer(controller, "Host");
                GraphFrameData.SaveAsyncSync(controller, new GraphFrameData.AsyncSyncConfig
                {
                    layer = host,
                    baseName = "Cyc",
                });
                for (int i = 0; i < 3; i++)
                    GraphFrameData.SaveGadget(controller, new GraphFrameData.AapGadgetConfig
                    {
                        layer = host,
                        output = "Sum" + i,
                        tree = Made<AnimationClip>("Sum tree " + i),
                    });
                LayerOwners.Forget();

                string described = LayerOwners.Describe(LayerOwners.Of(controller, host));
                StringAssert.Contains("'Cyc'", described,
                    "what OWNS the layer is named, because that is where to go and edit it");
                StringAssert.Contains("3", described,
                    "what merely hangs in it is counted — a host is meant to accumulate gadgets");
                Assert.AreEqual(2, described.Split('\n').Length,
                    "one line per claim, with the three gadgets sharing one of them");

                Assert.IsEmpty(LayerOwners.Describe(LayerOwners.Of(controller, null)));
            });
        }

        [Test]
        public void TheMapIsRememberedBetweenAsksAndDroppedWithTheHolders()
        {
            WithSavedController(controller =>
            {
                var owned = Layer(controller, "Owned");
                var later = Layer(controller, "Later");
                GraphFrameData.SaveAsyncSync(controller, new GraphFrameData.AsyncSyncConfig
                {
                    layer = owned,
                    baseName = "Cyc",
                });

                LayerOwners.Forget();
                int builds = LayerOwners.Builds;
                Assert.AreEqual(1, LayerOwners.Of(controller, owned).Count);
                Assert.AreEqual(builds + 1, LayerOwners.Builds);
                Assert.AreEqual(1, LayerOwners.Of(controller, owned).Count);
                Assert.AreEqual(builds + 1, LayerOwners.Builds,
                    "the next row asking during the same repaint must not walk the records again");

                // A record written while the map is held is invisible until something says so.
                // That is the bargain, and it is why the panel drops the map on the structural
                // notifications it already repaints on.
                GraphFrameData.SaveObjectGadget(controller, new GraphFrameData.ObjectGadgetConfig
                {
                    parameter = "Hat",
                    name = "Hat",
                    mode = (int)ToggleBuilder.Mode.Layer,
                    layer = later,
                });
                Assert.IsEmpty(LayerOwners.Of(controller, later));

                // ForgetHolders is the other end of that bargain: whatever reaches the saved data
                // without passing through the window — an import, a recipe's Generate — drops the
                // holder table and this map together.
                AssetDatabase.SaveAssets();
                GraphFrameData.ForgetHolders();
                CollectionAssert.AreEqual(new[] { LayerOwnerKind.ObjectGadget },
                    Kinds(controller, later));
                Assert.AreEqual(builds + 2, LayerOwners.Builds);
            });
        }
    }
}

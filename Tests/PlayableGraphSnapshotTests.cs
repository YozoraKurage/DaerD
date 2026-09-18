using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// What DD PlayableGraph Viewer reads off a graph, asserted on graphs built here by hand.
    ///
    /// The model half of the viewer is deliberately free of GraphView, and this is why: every
    /// claim the picture makes — the tree it found, the weight on each connection, where the
    /// walk stops — is a claim about data, and a test that had to open a window to check one
    /// would be checking the window.
    ///
    /// <para>Half of this is measurement of the Playable API rather than of our own code, in
    /// the same spirit as DynamicAnalyzeRecTests: the two facts the walk is shaped around (a
    /// controller playable is several playables inside, and a graph handed out by Unity may
    /// already be dead) were checked by running them, and are pinned here so a Unity upgrade
    /// that changes one fails loudly instead of quietly.</para>
    ///
    /// <para>Nothing here touches the VRChat SDK, so it runs the same with it installed and
    /// without.</para>
    /// </summary>
    public class PlayableGraphSnapshotTests
    {
        readonly List<PlayableGraph> _graphs = new List<PlayableGraph>();
        readonly List<Object> _assets = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            // A graph left behind is not only a leak: Unity hands it to the next test's
            // Discover() as a graph of its own.
            foreach (var graph in _graphs)
                if (graph.IsValid()) graph.Destroy();
            _graphs.Clear();
            foreach (var asset in _assets)
                if (asset != null) Object.DestroyImmediate(asset);
            _assets.Clear();
        }

        PlayableGraph NewGraph(string name)
        {
            var graph = PlayableGraph.Create(name);
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            _graphs.Add(graph);
            return graph;
        }

        T Own<T>(T asset) where T : Object
        {
            asset.hideFlags = HideFlags.HideAndDontSave;
            _assets.Add(asset);
            return asset;
        }

        AnimationClip Clip(string name) => Own(new AnimationClip { name = name });

        Animator Target()
        {
            var host = Own(new GameObject("DaerD Graph Rig"));
            return host.AddComponent<Animator>();
        }

        static PlayableNodeInfo Find(PlayableGraphSnapshot snapshot, PlayableKind kind)
        {
            foreach (var node in snapshot.Nodes)
                if (node.Kind == kind) return node;
            return null;
        }

        static List<PlayableNodeInfo> ChildrenOf(PlayableGraphSnapshot snapshot, PlayableNodeInfo parent)
        {
            var found = new List<PlayableNodeInfo>();
            foreach (var node in snapshot.Nodes)
                if (node.ParentIndex == parent.Index) found.Add(node);
            return found;
        }

        // ---- the shape that is found ------------------------------------------

        /// <summary>
        /// The walk starts at the output, not at the playables, so the picture is rooted in the
        /// thing being written to. Depth counts away from there, which is what the layout turns
        /// into columns.
        /// </summary>
        [Test]
        public void TheTreeUnderAnOutput_IsFoundWithItsDepths()
        {
            var graph = NewGraph("DaerD Shape");
            var mixer = AnimationMixerPlayable.Create(graph, 2);
            var a = AnimationClipPlayable.Create(graph, Clip("A"));
            var b = AnimationClipPlayable.Create(graph, Clip("B"));
            graph.Connect(a, 0, mixer, 0);
            graph.Connect(b, 0, mixer, 1);
            var output = AnimationPlayableOutput.Create(graph, "DaerD Out", Target());
            output.SetSourcePlayable(mixer);

            var snapshot = PlayableGraphSnapshot.Of(graph);

            Assert.AreEqual("DaerD Shape", snapshot.Name);
            Assert.AreEqual(4, snapshot.Nodes.Count, "output, mixer, two clips");

            var outputNode = snapshot.Nodes[0];
            Assert.AreEqual(PlayableKind.Output, outputNode.Kind);
            Assert.AreEqual("DaerD Out", outputNode.Label);
            Assert.AreEqual(0, outputNode.Depth);
            Assert.AreEqual(-1, outputNode.ParentIndex, "an output is fed, and feeds nothing");

            var mixerNode = snapshot.Nodes[1];
            Assert.AreEqual(PlayableKind.AnimationMixer, mixerNode.Kind);
            Assert.AreEqual(1, mixerNode.Depth);
            Assert.AreEqual(outputNode.Index, mixerNode.ParentIndex);
            Assert.AreEqual(2, mixerNode.InputCount);

            var children = ChildrenOf(snapshot, mixerNode);
            Assert.AreEqual(2, children.Count);
            Assert.AreEqual(new[] { "A", "B" }, new[] { children[0].Label, children[1].Label },
                "a clip playable is the one node that carries a name of its own");
            Assert.AreEqual(new[] { 0, 1 }, new[] { children[0].InputIndex, children[1].InputIndex });
            Assert.AreEqual(new[] { 2, 2 }, new[] { children[0].Depth, children[1].Depth });
            Assert.AreEqual(PlayableKind.AnimationClip, children[0].Kind);
        }

        /// <summary>The weight belongs to the parent's input; each node carries the one that
        /// reaches it, which is the number the edge's colour is made of.</summary>
        [Test]
        public void EachConnectionCarries_TheWeightOfTheInputItArrivesOn()
        {
            var graph = NewGraph("DaerD Weights");
            var mixer = AnimationMixerPlayable.Create(graph, 3);
            for (int i = 0; i < 3; i++)
                graph.Connect(AnimationClipPlayable.Create(graph, Clip("C" + i)), 0, mixer, i);
            mixer.SetInputWeight(0, 1f);
            mixer.SetInputWeight(1, 0.25f);
            mixer.SetInputWeight(2, 0f);
            var output = AnimationPlayableOutput.Create(graph, "DaerD Out", Target());
            output.SetSourcePlayable(mixer);

            var snapshot = PlayableGraphSnapshot.Of(graph);
            var children = ChildrenOf(snapshot, Find(snapshot, PlayableKind.AnimationMixer));

            Assert.AreEqual(3, children.Count);
            Assert.AreEqual(1f, children[0].InputWeight, 0.0001f);
            Assert.AreEqual(0.25f, children[1].InputWeight, 0.0001f);
            Assert.AreEqual(0f, children[2].InputWeight, 0.0001f);
        }

        /// <summary>A graph is somebody else's data structure, so the walk is bounded rather
        /// than trusting. The bound is reached here by a chain far deeper than anything real;
        /// a cycle would be the same situation without an end.</summary>
        [Test]
        public void TheWalkStops_AtTheDepthBound()
        {
            var graph = NewGraph("DaerD Deep");
            var top = AnimationMixerPlayable.Create(graph, 1);
            var previous = top;
            const int chain = PlayableGraphSnapshot.MaxDepth + 16;
            for (int i = 0; i < chain; i++)
            {
                var next = AnimationMixerPlayable.Create(graph, 1);
                graph.Connect(next, 0, previous, 0);
                previous = next;
            }
            var output = AnimationPlayableOutput.Create(graph, "DaerD Out", Target());
            output.SetSourcePlayable(top);

            var snapshot = PlayableGraphSnapshot.Of(graph);

            int deepest = 0;
            PlayableNodeInfo last = null;
            foreach (var node in snapshot.Nodes)
                if (node.Depth > deepest) { deepest = node.Depth; last = node; }

            Assert.AreEqual(PlayableGraphSnapshot.MaxDepth, deepest,
                "the walk went past its own bound");
            Assert.Less(snapshot.Nodes.Count, chain, "every playable was drawn after all");
            Assert.IsTrue(last.InputsHidden,
                "a node the walk stopped at must say there is more below it");
        }

        // ---- graphs that are not there any more --------------------------------

        /// <summary>
        /// Measured, and the reason the model checks IsValid before anything else: a graph
        /// handle may be dead. Note what this does NOT claim — that Unity stops handing a
        /// destroyed graph to Discover(). It does not, for the rest of the tick in which the
        /// graph died; only the handle here, which the destroy invalidated, reads as dead.
        /// </summary>
        [Test]
        public void ADeadGraph_IsSkippedRatherThanRead()
        {
            var graph = NewGraph("DaerD Doomed");
            var output = AnimationPlayableOutput.Create(graph, "DaerD Out", Target());
            output.SetSourcePlayable(AnimationMixerPlayable.Create(graph, 1));
            Assert.IsNotNull(PlayableGraphSnapshot.Of(graph), "alive, and readable");

            graph.Destroy();

            Assert.IsNull(PlayableGraphSnapshot.Of(graph));
            Assert.IsNull(PlayableGraphSnapshot.Of(new PlayableGraph()),
                "a handle that was never a graph is the same case");
        }

        /// <summary>Unity hands out every graph in the editor, including one built a moment ago
        /// by nobody in particular — the single fact discovery stands on.</summary>
        [Test]
        public void Discover_FindsAGraphNobodyRegistered()
        {
            var graph = NewGraph("DaerD Discoverable");
            var output = AnimationPlayableOutput.Create(graph, "DaerD Out", Target());
            output.SetSourcePlayable(AnimationMixerPlayable.Create(graph, 1));

            bool found = false;
            foreach (var snapshot in PlayableGraphSnapshot.Discover())
                if (snapshot.Name == "DaerD Discoverable") found = true;

            Assert.IsTrue(found);
        }

        // ---- where the walk deliberately stops ---------------------------------

        /// <summary>
        /// The viewer draws an AnimatorControllerPlayable as a leaf and says so on the node.
        ///
        /// Measured here rather than assumed: one controller of three layers with a single
        /// state each already expands into more than twenty playables — an internal layer
        /// mixer, then a nameless mixer pair and a pose playable per layer — none of which
        /// carries a name the reader could match to anything they authored. PlayRecorder
        /// refuses the same descent for the neighbouring reason (the parts are not layers); a
        /// viewer's reason is that drawing them would bury the nodes that mean something.
        /// </summary>
        [Test]
        public void AControllerPlayable_IsDrawnAsALeafThatAdmitsToHidingSomething()
        {
            var controller = Own(new AnimatorController { name = "DaerD Ctrl" });
            controller.AddLayer("Base");
            controller.AddLayer("Extra");
            controller.AddLayer("Third");
            foreach (var layer in controller.layers)
                layer.stateMachine.AddState("S").motion = Clip("M");

            var graph = NewGraph("DaerD Controller");
            var playable = AnimatorControllerPlayable.Create(graph, controller);
            var output = AnimationPlayableOutput.Create(graph, "DaerD Out", Target());
            output.SetSourcePlayable(playable);

            var folded = PlayableGraphSnapshot.Of(graph, insideControllers: false);

            Assert.AreEqual(2, folded.Nodes.Count, "the output and the controller playable, and nothing under it");
            var node = folded.Nodes[1];
            Assert.AreEqual(PlayableKind.AnimatorController, node.Kind);
            Assert.IsTrue(node.InputsHidden, "the node has to admit there is more inside it");
            Assert.Greater(node.InputCount, 0, "the handle does report inputs — they are simply not drawn");
            Assert.Greater(folded.PlayableCount, folded.Nodes.Count,
                "the graph holds far more playables than the viewer draws");
        }

        [Test]
        public void ByDefault_WhatMecanimRunsInsideAControllerIsDrawnToo()
        {
            var controller = Own(new AnimatorController { name = "DaerD Ctrl" });
            controller.AddLayer("Base");
            controller.layers[0].stateMachine.AddState("S").motion = Clip("M");

            var graph = NewGraph("DaerD Controller");
            var playable = AnimatorControllerPlayable.Create(graph, controller);
            var output = AnimationPlayableOutput.Create(graph, "DaerD Out", Target());
            output.SetSourcePlayable(playable);

            var snapshot = PlayableGraphSnapshot.Of(graph);

            // The clips are inside the controller playable and nowhere else, so a viewer that
            // stopped at it could never show what is actually being played.
            Assert.Greater(snapshot.Nodes.Count, 2, "the controller's own playables are drawn");
            var controllerNode = snapshot.Nodes[1];
            Assert.AreEqual(PlayableKind.AnimatorController, controllerNode.Kind);
            Assert.IsFalse(controllerNode.InputsHidden, "nothing is hidden when the walk goes in");
        }

        /// <summary>A playable carrying a behaviour of somebody's own is recognised as one
        /// rather than falling through to the unnamed case.</summary>
        [Test]
        public void AScriptPlayable_IsRecognisedByItsBehaviour()
        {
            var graph = NewGraph("DaerD Script");
            var mixer = AnimationMixerPlayable.Create(graph, 1);
            var script = ScriptPlayable<Marker>.Create(graph, 0);
            graph.Connect(script, 0, mixer, 0);
            var output = AnimationPlayableOutput.Create(graph, "DaerD Out", Target());
            output.SetSourcePlayable(mixer);

            var snapshot = PlayableGraphSnapshot.Of(graph);

            Assert.IsNotNull(Find(snapshot, PlayableKind.Script));
        }

        /// <summary>A behaviour with nothing in it — ScriptPlayable needs a concrete type and
        /// this test needs nothing else from it.</summary>
        sealed class Marker : PlayableBehaviour
        {
        }

        // ---- the one thing the viewer guesses ----------------------------------

        /// <summary>
        /// A graph named the way GestureManager names its own, and shaped the way it shapes its
        /// own, gets its mixer's inputs labelled — input 0 left bare because GestureManager
        /// never connects it.
        ///
        /// The order is the one read out of GestureManager 3.9's sources, where the avatar's
        /// layers are sorted into a fixed sequence before being connected from input 1 upwards.
        /// </summary>
        [Test]
        public void AGestureManagerShapedGraph_HasItsSlotsNamed()
        {
            var snapshot = PlayableGraphSnapshot.Of(LayerStack("Gesture Manager 3.9", 9));

            Assert.IsNotNull(snapshot.Layout, "the layout was not recognised");
            Assert.AreEqual("Gesture Manager", snapshot.Layout.Tool);

            var mixer = Find(snapshot, PlayableKind.AnimationLayerMixer);
            var named = new Dictionary<int, string>();
            foreach (var node in ChildrenOf(snapshot, mixer))
                named[node.InputIndex] = node.SlotName;

            Assert.AreEqual(8, named.Count, "input 0 is never connected, so nothing is drawn in it");
            Assert.IsFalse(named.ContainsKey(0));
            var expected = new[] { "Base", "Additive", "Sitting", "TPose", "IKPose", "Gesture", "Action", "FX" };
            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], named[i + 1], "slot " + (i + 1));
        }

        /// <summary>
        /// The same shape under any other name is left with bare indices.
        ///
        /// This is the case the whole design turns on: Av3Emulator builds a mixer of exactly
        /// this shape — layers + 1 inputs, input 0 unconnected — in an order of its own, so a
        /// match on the count would put GestureManager's names on somebody else's slots. A
        /// wrong name reads as a fact; a bare index reads as an index.
        /// </summary>
        [Test]
        public void TheSameShapeUnderAnotherName_IsNotLabelled()
        {
            var snapshot = PlayableGraphSnapshot.Of(LayerStack("LyumaAvatarRuntime - rig", 9));

            Assert.IsNull(snapshot.Layout);
            foreach (var node in snapshot.Nodes)
                Assert.IsNull(node.SlotName);
        }

        /// <summary>The name alone is not enough either: the expected input count is the second
        /// half of the match, so a version that changes shape stops being labelled instead of
        /// being labelled wrongly.</summary>
        [Test]
        public void TheRightNameWithTheWrongShape_IsNotLabelled()
        {
            var snapshot = PlayableGraphSnapshot.Of(LayerStack("Gesture Manager 3.9", 6));

            Assert.IsNull(snapshot.Layout);
            foreach (var node in snapshot.Nodes)
                Assert.IsNull(node.SlotName);
        }

        /// <summary>A layer mixer driving an output, with a controller playable on every input
        /// but the first — the shape both avatar tools build.</summary>
        PlayableGraph LayerStack(string graphName, int inputs)
        {
            var graph = NewGraph(graphName);
            var mixer = AnimationLayerMixerPlayable.Create(graph, inputs);
            for (int i = 1; i < inputs; i++)
            {
                var controller = Own(new AnimatorController { name = "DaerD L" + i });
                controller.AddLayer("Base");
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                graph.Connect(playable, 0, mixer, i);
                mixer.SetInputWeight(i, 1f);
            }
            var output = AnimationPlayableOutput.Create(graph, "DaerD Out", Target());
            output.SetSourcePlayable(mixer);
            return graph;
        }
    }
}

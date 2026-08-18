using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class GraphFrameDataTests
    {
        [Test]
        public void AddFrame_StoresIt_AndFramesInFiltersByStateMachine()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddLayer("Other");
            var smA = controller.layers[0].stateMachine;
            var smB = controller.layers[1].stateMachine;

            var data = GraphFrameData.GetOrCreate(controller);
            var frameA = data.AddFrame(smA, new Rect(10f, 20f, 300f, 200f), "Group A");
            data.AddFrame(smB, new Rect(0f, 0f, 100f, 100f), "Group B");

            Assert.AreEqual(2, data.frames.Count);
            var inA = data.FramesIn(smA);
            Assert.AreEqual(1, inA.Count);
            Assert.AreSame(frameA, inA[0]);
            Assert.AreEqual("Group A", inA[0].title);
            Assert.AreEqual(new Rect(10f, 20f, 300f, 200f), inA[0].bounds);

            data.RemoveFrame(frameA);
            Assert.AreEqual(0, data.FramesIn(smA).Count);
            Assert.AreEqual(1, data.FramesIn(smB).Count);

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        /// <summary>Regression: regenerating a layer from a recipe destroys and recreates
        /// its state machine — every machine-keyed record (async-sync setup and its SYNC
        /// badge, frames, notes, C# ownership) must follow to the successor, matched by the
        /// old instance ID even after the machine object is destroyed.</summary>
        [Test]
        public void RemapMachineReferences_MovesRecordsToTheSuccessor_EvenAfterDestroy()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Zip");
            controller.AddLayer("Other");
            var old = controller.layers[0].stateMachine;
            var other = controller.layers[1].stateMachine;
            int oldId = old.GetInstanceID();

            var data = GraphFrameData.GetOrCreate(controller);
            data.AddFrame(old, new Rect(0f, 0f, 100f, 100f), "OnZip");
            data.AddNote(old, new Rect(0f, 0f, 50f, 50f));
            var foreignFrame = data.AddFrame(other, new Rect(0f, 0f, 10f, 10f), "Elsewhere");
            data.asyncSyncs.Add(new GraphFrameData.AsyncSyncConfig { layer = old, baseName = "Zip" });
            data.codeOwned.Add(new GraphFrameData.CodeOwnedLayer { layer = old, recipe = controller });
            var gadgetTree = new BlendTree { name = "Mul Hue, Gain" };
            data.aapGadgets.Add(new GraphFrameData.AapGadgetConfig
            { layer = old, tree = gadgetTree, output = "Hue*Gain" });

            // The real-world sequence: the old machine dies before the remap runs.
            Object.DestroyImmediate(old);
            var successor = new AnimatorStateMachine { name = "Zip" };

            Assert.IsTrue(data.RemapMachineReferences(oldId, successor));
            Assert.AreSame(successor, data.frames[0].stateMachine);
            Assert.AreSame(successor, data.notes[0].stateMachine);
            Assert.AreSame(successor, data.asyncSyncs[0].layer);
            Assert.AreSame(successor, data.codeOwned[0].layer);
            Assert.AreSame(successor, data.aapGadgets[0].layer);
            Assert.AreSame(other, foreignFrame.stateMachine, "records of other layers stay put");

            Assert.IsFalse(data.RemapMachineReferences(123456789, successor),
                "an unknown id must not touch anything");

            Object.DestroyImmediate(gadgetTree);
            Object.DestroyImmediate(successor);
            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        /// <summary>
        /// An object gadget's record deliberately does NOT follow a rebuilt machine, and that is
        /// the decision this pins rather than an omission somebody would "fix".
        ///
        /// A Layer-wired object gadget IS its layer. Re-pointing its record at the machine a
        /// recipe just built in its place would make the next regenerate sweep that recipe's
        /// layer as if it were the gadget's own — destroying somebody else's work to save a
        /// record. Losing the record costs one gadget that has to be rebuilt, which is the
        /// cheaper of the two mistakes; the entry is pruned on the next read, so nothing is left
        /// pointing at a machine that is gone.
        /// </summary>
        [Test]
        public void RemapMachineReferences_LeavesObjectGadgetsBehind_SoARecipesLayerIsNotSwept()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Hat");
            var old = controller.layers[0].stateMachine;
            int oldId = old.GetInstanceID();

            var data = GraphFrameData.GetOrCreate(controller);
            data.objectGadgets.Add(new GraphFrameData.ObjectGadgetConfig
            { layer = old, name = "Hat", parameter = "Hat" });

            Object.DestroyImmediate(old);
            var successor = new AnimatorStateMachine { name = "Hat" };

            Assert.IsFalse(data.RemapMachineReferences(oldId, successor),
                "nothing here is keyed by a machine that may be replaced under it");
            Assert.IsEmpty(data.ObjectGadgetRecords(),
                "and the record whose layer is gone is pruned rather than re-pointed");

            Object.DestroyImmediate(successor);
            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        /// <summary>A gadget is filed under the parameter it writes: regenerating one replaces
        /// its own record instead of leaving a second one describing the same output.</summary>
        [Test]
        public void SaveGadget_ReplacesTheEntryForTheSameOutput()
        {
            var controller = new AnimatorController();
            controller.AddLayer("DBT");
            var machine = controller.layers[0].stateMachine;
            var data = GraphFrameData.GetOrCreate(controller);

            var before = new BlendTree { name = "Mul Hue, Gain" };
            var after = new BlendTree { name = "Mul Hue, Gain" };
            data.SaveGadget(new GraphFrameData.AapGadgetConfig
            { layer = machine, tree = before, output = "Hue*Gain" });
            data.SaveGadget(new GraphFrameData.AapGadgetConfig
            { layer = machine, tree = after, output = "Hue*Gain" });
            data.SaveGadget(new GraphFrameData.AapGadgetConfig
            { layer = machine, tree = before, output = "Hue+Gain" });

            var live = data.Gadgets();
            Assert.AreEqual(2, live.Count);
            Assert.AreSame(after, live[0].tree, "the regenerated gadget took its own entry over");
            Assert.AreEqual("Hue+Gain", live[1].output);

            data.RemoveGadget("Hue+Gain");
            Assert.AreEqual(1, data.Gadgets().Count);
            Assert.AreEqual("Hue*Gain", data.Gadgets()[0].output);

            Object.DestroyImmediate(before);
            Object.DestroyImmediate(after);
            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        /// <summary>Deleting the layer, or the tree inside it, deletes the gadget — the record
        /// then describes nothing and must not be offered for regeneration.</summary>
        [Test]
        public void Gadgets_PrunesEntriesWhoseLayerOrTreeIsGone()
        {
            var controller = new AnimatorController();
            controller.AddLayer("DBT");
            controller.AddLayer("Other");
            var machine = controller.layers[0].stateMachine;
            var otherMachine = controller.layers[1].stateMachine;
            var data = GraphFrameData.GetOrCreate(controller);

            var tree = new BlendTree { name = "Mul Hue, Gain" };
            var otherTree = new BlendTree { name = "Add Hue, Gain" };
            data.SaveGadget(new GraphFrameData.AapGadgetConfig
            { layer = machine, tree = tree, output = "Hue*Gain" });
            data.SaveGadget(new GraphFrameData.AapGadgetConfig
            { layer = otherMachine, tree = otherTree, output = "Hue+Gain" });
            Assert.AreEqual(2, data.Gadgets().Count);

            Object.DestroyImmediate(tree);
            Object.DestroyImmediate(otherMachine);
            Assert.IsEmpty(data.Gadgets());
            Assert.IsEmpty(data.aapGadgets, "the prune is a write, not a filtered view");

            Object.DestroyImmediate(otherTree);
            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Find_ReturnsNull_ForInMemoryControllers()
        {
            var controller = new AnimatorController();
            Assert.IsNull(GraphFrameData.Find(controller));
            Object.DestroyImmediate(controller);
        }
    }
}

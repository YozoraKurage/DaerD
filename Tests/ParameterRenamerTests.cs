using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Analyze;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD.Tests
{
    public class ParameterRenamerTests
    {
        static AnimatorController NewController(out AnimatorStateMachine sm)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            sm = controller.layers[0].stateMachine;
            return controller;
        }

        [Test]
        public void Rename_CascadesIntoDirectBlendTreeChildWeights_IncludingNestedTrees()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("Weight", AnimatorControllerParameterType.Float);
            var state = sm.AddState("Tree");

            var clip = new AnimationClip();
            var root = new BlendTree { name = "Root", blendType = BlendTreeType.Direct };
            var nested = new BlendTree { name = "Nested", blendType = BlendTreeType.Direct };
            root.AddChild(nested);
            root.AddChild(clip);
            nested.AddChild(clip);

            var rootChildren = root.children;
            rootChildren[0].directBlendParameter = "Weight";
            rootChildren[1].directBlendParameter = "Weight";
            root.children = rootChildren;
            var nestedChildren = nested.children;
            nestedChildren[0].directBlendParameter = "Weight";
            nested.children = nestedChildren;

            state.motion = root;

            Assert.IsTrue(ParameterRenamer.Rename(controller, "Weight", "Wt"));

            Assert.AreEqual("Wt", root.children[0].directBlendParameter);
            Assert.AreEqual("Wt", root.children[1].directBlendParameter);
            Assert.AreEqual("Wt", nested.children[0].directBlendParameter);

            Object.DestroyImmediate(controller);
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(nested);
            Object.DestroyImmediate(clip);
        }

        [Test]
        public void Rename_CascadesIntoSyncedLayerOverrideTrees()
        {
            var controller = NewController(out var sm);
            controller.AddLayer("Synced");
            controller.AddParameter("Blend", AnimatorControllerParameterType.Float);
            var state = sm.AddState("S");

            var tree = new BlendTree { name = "Override", blendParameter = "Blend" };

            // AnimatorControllerLayer instances are copies — mutate and write the array back.
            var layers = controller.layers;
            layers[1].syncedLayerIndex = 0;
            layers[1].SetOverrideMotion(state, tree);
            controller.layers = layers;

            Assert.IsTrue(ParameterRenamer.Rename(controller, "Blend", "Blend2"));

            Assert.AreEqual("Blend2", tree.blendParameter);

            Object.DestroyImmediate(controller);
            Object.DestroyImmediate(tree);
        }

        [Test]
        public void Rename_CascadesIntoVrcParameterDriverEntries()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("Val", AnimatorControllerParameterType.Float);
            var state = sm.AddState("S");

            var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.parameters.Add(new VRCAvatarParameterDriver.Parameter { name = "Val", source = "" });
            driver.parameters.Add(new VRCAvatarParameterDriver.Parameter { name = "Other", source = "Val" });

            Assert.IsTrue(ParameterRenamer.Rename(controller, "Val", "Val2"));

            Assert.AreEqual("Val2", driver.parameters[0].name);       // Set/Add destination
            Assert.AreEqual("Other", driver.parameters[1].name);      // untouched
            Assert.AreEqual("Val2", driver.parameters[1].source);     // Copy source

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Rename_CascadesIntoDriversOnStateMachines()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("Val", AnimatorControllerParameterType.Float);

            var driver = sm.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.parameters.Add(new VRCAvatarParameterDriver.Parameter { name = "Val" });

            Assert.IsTrue(ParameterRenamer.Rename(controller, "Val", "Val2"));

            Assert.AreEqual("Val2", driver.parameters[0].name);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void DriverReferencedParameter_IsNotReportedUnused()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("Driven", AnimatorControllerParameterType.Float);
            var state = sm.AddState("S");

            var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.parameters.Add(new VRCAvatarParameterDriver.Parameter { name = "Driven" });

            Assert.IsEmpty(ControllerAnalyzer.FindUnusedParameters(controller));

            Object.DestroyImmediate(controller);
        }
    }
}

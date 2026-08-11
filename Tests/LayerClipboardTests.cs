using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class LayerClipboardTests
    {
        /// <summary>Controller with a "Source" layer: A → B on Go, A drives a 1D blend tree
        /// on Blend, B carries a test behaviour.</summary>
        static AnimatorController NewSource()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddLayer("Source");
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Blend", AnimatorControllerParameterType.Float);
            controller.AddParameter("Unrelated", AnimatorControllerParameterType.Int);

            var layers = controller.layers;
            layers[1].defaultWeight = 0.5f;
            layers[1].iKPass = true;
            controller.layers = layers;

            var sm = controller.layers[1].stateMachine;
            var a = sm.AddState("A", new Vector3(0f, 0f, 0f));
            var b = sm.AddState("B", new Vector3(0f, 100f, 0f));
            var transition = a.AddTransition(b);
            transition.AddCondition(AnimatorConditionMode.If, 0f, "Go");

            var tree = new BlendTree
            {
                name = "Tree",
                blendType = BlendTreeType.Simple1D,
                blendParameter = "Blend",
                useAutomaticThresholds = false,
            };
            tree.AddChild(new AnimationClip { name = "C0" }, 0f);
            tree.AddChild(new AnimationClip { name = "C1" }, 1f);
            a.motion = tree;

            var behaviour = (ClipboardTestBehaviour)b.AddStateMachineBehaviour(
                typeof(ClipboardTestBehaviour));
            behaviour.payload = "hello";
            return controller;
        }

        [Test]
        public void CopyPaste_RebuildsLayerInAnotherController()
        {
            var source = NewSource();
            LayerClipboard.Copy(source, 1);
            Assert.IsTrue(LayerClipboard.HasData);
            Assert.AreEqual("Source", LayerClipboard.CopiedLayerName);

            var target = new AnimatorController();
            target.AddLayer("Base");
            int index = LayerClipboard.Paste(target);
            Assert.AreEqual(1, index);

            var layer = target.layers[1];
            Assert.AreEqual("Source", layer.name);
            Assert.AreEqual(0.5f, layer.defaultWeight);
            Assert.IsTrue(layer.iKPass);

            var states = layer.stateMachine.states;
            Assert.AreEqual(2, states.Length);
            var a = states[0].state;
            var b = states[1].state;
            Assert.AreEqual("A", a.name);
            Assert.AreEqual("B", b.name);
            Assert.AreEqual(b, a.transitions[0].destinationState);
            Assert.AreEqual("Go", a.transitions[0].conditions[0].parameter);

            // Referenced parameters came along; unrelated ones didn't.
            Assert.IsNotNull(DbtBuilder.FindParameter(target, "Go"));
            Assert.IsNotNull(DbtBuilder.FindParameter(target, "Blend"));
            Assert.IsNull(DbtBuilder.FindParameter(target, "Unrelated"));

            // Behaviour cloned with its data.
            Assert.AreEqual(1, b.behaviours.Length);
            Assert.AreEqual("hello", ((ClipboardTestBehaviour)b.behaviours[0]).payload);
        }

        [Test]
        public void Paste_DeepCopiesBlendTrees()
        {
            var source = NewSource();
            var sourceTree = (BlendTree)source.layers[1].stateMachine.states[0].state.motion;
            LayerClipboard.Copy(source, 1);

            var target = new AnimatorController();
            target.AddLayer("Base");
            LayerClipboard.Paste(target);

            var pastedTree = (BlendTree)target.layers[1].stateMachine.states[0].state.motion;
            Assert.AreNotEqual(sourceTree, pastedTree);
            Assert.AreEqual("Blend", pastedTree.blendParameter);
            Assert.AreEqual(2, pastedTree.children.Length);
            // Clip leaves stay shared.
            Assert.AreEqual(sourceTree.children[0].motion, pastedTree.children[0].motion);
        }

        [Test]
        public void PasteSettings_TouchesOnlyLayerSettings()
        {
            var source = NewSource();
            LayerClipboard.Copy(source, 1);

            var target = new AnimatorController();
            target.AddLayer("Base");
            target.AddLayer("Existing");
            target.layers[1].stateMachine.AddState("Keep");

            Assert.IsTrue(LayerClipboard.PasteSettings(target, 1));
            Assert.AreEqual(0.5f, target.layers[1].defaultWeight);
            Assert.IsTrue(target.layers[1].iKPass);
            Assert.AreEqual("Existing", target.layers[1].name);
            Assert.AreEqual(1, target.layers[1].stateMachine.states.Length);
        }

        [Test]
        public void CollectParameterNames_FindsAllReferenceKinds()
        {
            var source = NewSource();
            var names = LayerClipboard.CollectParameterNames(source.layers[1].stateMachine);
            Assert.IsTrue(names.Contains("Go"));
            Assert.IsTrue(names.Contains("Blend"));
            Assert.IsFalse(names.Contains("Unrelated"));
        }
    }

    public class LayerParameterRemapperTests
    {
        [Test]
        public void Remap_RewritesConditionsStatesAndTrees()
        {
            var controller = new AnimatorController();
            controller.AddLayer("L");
            var sm = controller.layers[0].stateMachine;
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var transition = a.AddTransition(b);
            transition.AddCondition(AnimatorConditionMode.If, 0f, "Old");
            a.speedParameterActive = true;
            a.speedParameter = "Old";

            var direct = new BlendTree { name = "D", blendType = BlendTreeType.Direct };
            direct.AddChild(new AnimationClip());
            var children = direct.children;
            children[0].directBlendParameter = "Old";
            direct.children = children;
            b.motion = direct;

            LayerParameterRemapper.Remap(sm, new Dictionary<string, string> { ["Old"] = "New" });

            Assert.AreEqual("New", a.transitions[0].conditions[0].parameter);
            Assert.AreEqual("New", a.speedParameter);
            Assert.AreEqual("New", ((BlendTree)b.motion).children[0].directBlendParameter);
        }
    }

    public class LayerTemplateTests
    {
        [Test]
        public void Import_CreatesLayerWithRemappedParameters()
        {
            // Template assembled in memory (never saved) — Import doesn't need persistence.
            var source = new AnimatorController();
            source.AddLayer("Toggle");
            source.AddParameter("Switch", AnimatorControllerParameterType.Bool);
            var sm = source.layers[0].stateMachine;
            var off = sm.AddState("Off");
            var on = sm.AddState("On");
            var t = off.AddTransition(on);
            t.AddCondition(AnimatorConditionMode.If, 0f, "Switch");

            var template = ScriptableObject.CreateInstance<DaerDLayerTemplate>();
            template.name = "Toggle";
            template.layerName = "Toggle";
            template.stateMachine = new AnimatorStateMachine { name = "Toggle" };
            StateMachineCloner.Clone(sm, template.stateMachine);
            template.parameters.Add(new LayerClipboard.ParameterSnapshot
            {
                name = "Switch",
                type = AnimatorControllerParameterType.Bool,
                defaultBool = true,
            });

            var target = new AnimatorController();
            target.AddLayer("Base");
            int index = template.Import(target,
                new Dictionary<string, string> { ["Switch"] = "HatSwitch" });

            Assert.AreEqual(1, index);
            Assert.AreEqual("Toggle", target.layers[1].name);
            var parameter = DbtBuilder.FindParameter(target, "HatSwitch");
            Assert.IsNotNull(parameter);
            Assert.AreEqual(AnimatorControllerParameterType.Bool, parameter.type);
            Assert.IsTrue(parameter.defaultBool);

            var imported = target.layers[1].stateMachine.states[0].state;
            Assert.AreEqual("HatSwitch", imported.transitions[0].conditions[0].parameter);
        }

        [Test]
        public void Import_MappingToExistingParameterAddsNothing()
        {
            var template = ScriptableObject.CreateInstance<DaerDLayerTemplate>();
            template.layerName = "T";
            template.stateMachine = new AnimatorStateMachine { name = "T" };
            template.stateMachine.AddState("S");
            template.parameters.Add(new LayerClipboard.ParameterSnapshot
            {
                name = "Switch",
                type = AnimatorControllerParameterType.Bool,
            });

            var target = new AnimatorController();
            target.AddLayer("Base");
            target.AddParameter("Existing", AnimatorControllerParameterType.Bool);
            template.Import(target, new Dictionary<string, string> { ["Switch"] = "Existing" });

            Assert.AreEqual(1, target.parameters.Length);   // nothing new was created
        }
    }
}

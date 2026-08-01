using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class ToggleBuilderTests
    {
        static AnimatorController NewController()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            return controller;
        }

        static ToggleBuilder.Request NewRequest(AnimatorController controller,
            ToggleBuilder.Mode mode, params string[] paths)
        {
            var request = new ToggleBuilder.Request
            {
                controller = controller,
                mode = mode,
                toggleName = "Hat",
                parameter = "Hat",
                layerIndex = -1,
                newLayerName = "DBT",
            };
            foreach (var path in paths)
                request.targets.Add(new ToggleBuilder.Target { path = path });
            return request;
        }

        static float ActiveValue(Motion motion, string path)
        {
            var clip = (AnimationClip)motion;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.path != path) continue;
                Assert.AreEqual(typeof(GameObject), binding.type);
                Assert.AreEqual("m_IsActive", binding.propertyName);
                return AnimationUtility.GetEditorCurve(clip, binding).keys[0].value;
            }
            Assert.Fail("No m_IsActive curve for path '" + path + "' in clip '" + clip.name + "'.");
            return -1f;
        }

        // ---- validation ----------------------------------------------------

        [Test]
        public void Validate_RejectsMissingPieces()
        {
            var controller = NewController();
            Assert.IsNotNull(ToggleBuilder.Validate(NewRequest(null, ToggleBuilder.Mode.Layer, "A")));
            Assert.IsNotNull(ToggleBuilder.Validate(NewRequest(controller, ToggleBuilder.Mode.Layer)));

            var noName = NewRequest(controller, ToggleBuilder.Mode.Layer, "A");
            noName.toggleName = string.Empty;
            Assert.IsNotNull(ToggleBuilder.Validate(noName));

            var noParameter = NewRequest(controller, ToggleBuilder.Mode.Layer, "A");
            noParameter.parameter = string.Empty;
            Assert.IsNotNull(ToggleBuilder.Validate(noParameter));

            var emptyPath = NewRequest(controller, ToggleBuilder.Mode.Layer, "A", "  ");
            Assert.IsNotNull(ToggleBuilder.Validate(emptyPath));

            var duplicate = NewRequest(controller, ToggleBuilder.Mode.Layer, "A", "A");
            Assert.IsNotNull(ToggleBuilder.Validate(duplicate));
        }

        [Test]
        public void Validate_RejectsParameterTypeMismatch()
        {
            var controller = NewController();
            controller.AddParameter("Hat", AnimatorControllerParameterType.Float);
            Assert.IsNotNull(ToggleBuilder.Validate(NewRequest(controller, ToggleBuilder.Mode.Layer, "A")));

            var dbt = NewController();
            dbt.AddParameter("Hat", AnimatorControllerParameterType.Bool);
            Assert.IsNotNull(ToggleBuilder.Validate(NewRequest(dbt, ToggleBuilder.Mode.DirectBlendTree, "A")));
        }

        [Test]
        public void Validate_AcceptsMatchingExistingParameter()
        {
            var controller = NewController();
            controller.AddParameter("Hat", AnimatorControllerParameterType.Bool);
            Assert.IsNull(ToggleBuilder.Validate(NewRequest(controller, ToggleBuilder.Mode.Layer, "A")));
        }

        // ---- layer mode ----------------------------------------------------

        [Test]
        public void Layer_BuildsTwoStatesWithInstantTransitions()
        {
            var controller = NewController();
            Assert.IsTrue(ToggleBuilder.Apply(NewRequest(controller, ToggleBuilder.Mode.Layer, "Body/Hat")));

            Assert.AreEqual(2, controller.layers.Length);
            var layer = controller.layers[1];
            Assert.AreEqual("Hat", layer.name);
            Assert.AreEqual(1f, layer.defaultWeight);

            var states = layer.stateMachine.states;
            Assert.AreEqual(2, states.Length);
            var offState = states[0].state;
            var onState = states[1].state;
            Assert.AreEqual("Hat OFF", offState.name);
            Assert.AreEqual("Hat ON", onState.name);
            Assert.IsFalse(offState.writeDefaultValues);
            Assert.IsFalse(onState.writeDefaultValues);
            Assert.AreEqual(offState, layer.stateMachine.defaultState);

            Assert.AreEqual(0f, ActiveValue(offState.motion, "Body/Hat"));
            Assert.AreEqual(1f, ActiveValue(onState.motion, "Body/Hat"));

            var toOn = offState.transitions[0];
            Assert.AreEqual(onState, toOn.destinationState);
            Assert.IsFalse(toOn.hasExitTime);
            Assert.AreEqual(0f, toOn.duration);
            Assert.AreEqual(AnimatorConditionMode.If, toOn.conditions[0].mode);
            Assert.AreEqual("Hat", toOn.conditions[0].parameter);

            var toOff = onState.transitions[0];
            Assert.AreEqual(offState, toOff.destinationState);
            Assert.AreEqual(AnimatorConditionMode.IfNot, toOff.conditions[0].mode);

            var parameter = DbtBuilder.FindParameter(controller, "Hat");
            Assert.AreEqual(AnimatorControllerParameterType.Bool, parameter.type);
            Assert.IsFalse(parameter.defaultBool);
        }

        [Test]
        public void Layer_DefaultOnStartsOnTheOnState()
        {
            var controller = NewController();
            var request = NewRequest(controller, ToggleBuilder.Mode.Layer, "Hat");
            request.defaultOn = true;
            Assert.IsTrue(ToggleBuilder.Apply(request));

            var layer = controller.layers[1];
            Assert.AreEqual("Hat ON", layer.stateMachine.defaultState.name);
            Assert.IsTrue(DbtBuilder.FindParameter(controller, "Hat").defaultBool);
        }

        [Test]
        public void Layer_InvertedTargetSwapsClipValues()
        {
            var controller = NewController();
            var request = NewRequest(controller, ToggleBuilder.Mode.Layer, "Hat");
            request.targets.Add(new ToggleBuilder.Target { path = "Cape", activeWhenOn = false });
            Assert.IsTrue(ToggleBuilder.Apply(request));

            var states = controller.layers[1].stateMachine.states;
            var offMotion = states[0].state.motion;
            var onMotion = states[1].state.motion;
            Assert.AreEqual(0f, ActiveValue(offMotion, "Hat"));
            Assert.AreEqual(1f, ActiveValue(offMotion, "Cape"));
            Assert.AreEqual(1f, ActiveValue(onMotion, "Hat"));
            Assert.AreEqual(0f, ActiveValue(onMotion, "Cape"));
        }

        [Test]
        public void Layer_ReusesExistingBoolParameterWithoutDuplicating()
        {
            var controller = NewController();
            controller.AddParameter("Hat", AnimatorControllerParameterType.Bool);
            Assert.IsTrue(ToggleBuilder.Apply(NewRequest(controller, ToggleBuilder.Mode.Layer, "Hat")));

            int count = 0;
            foreach (var p in controller.parameters)
                if (p.name == "Hat") count++;
            Assert.AreEqual(1, count);
        }

        [Test]
        public void Layer_ReusedParameterDefaultPicksTheStartState()
        {
            var controller = NewController();
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = "Hat",
                type = AnimatorControllerParameterType.Bool,
                defaultBool = true,
            });
            // defaultOn is false, but the reused parameter defaults to true — the layer must
            // start ON so nothing transitions on the first frame.
            Assert.IsTrue(ToggleBuilder.Apply(NewRequest(controller, ToggleBuilder.Mode.Layer, "Hat")));
            Assert.AreEqual("Hat ON", controller.layers[1].stateMachine.defaultState.name);
        }

        [Test]
        public void Layer_UniquifiesTheLayerName()
        {
            var controller = NewController();
            controller.AddLayer("Hat");
            Assert.IsTrue(ToggleBuilder.Apply(NewRequest(controller, ToggleBuilder.Mode.Layer, "Hat")));
            Assert.AreEqual("Hat 1", controller.layers[2].name);
        }

        // ---- direct blend tree mode ---------------------------------------

        [Test]
        public void Dbt_Builds1DTreeInsideDirectLayer()
        {
            var controller = NewController();
            Assert.IsTrue(ToggleBuilder.Apply(NewRequest(controller, ToggleBuilder.Mode.DirectBlendTree, "Body/Hat")));

            Assert.AreEqual(2, controller.layers.Length);
            var layer = controller.layers[1];
            Assert.AreEqual("DBT", layer.name);
            var root = (BlendTree)layer.stateMachine.states[0].state.motion;
            Assert.AreEqual(BlendTreeType.Direct, root.blendType);
            Assert.IsTrue(layer.stateMachine.states[0].state.writeDefaultValues);

            Assert.AreEqual(1, root.children.Length);
            Assert.AreEqual("One", root.children[0].directBlendParameter);

            var toggle = (BlendTree)root.children[0].motion;
            Assert.AreEqual(BlendTreeType.Simple1D, toggle.blendType);
            Assert.AreEqual("Hat", toggle.blendParameter);
            Assert.AreEqual(2, toggle.children.Length);
            Assert.AreEqual(0f, toggle.children[0].threshold);
            Assert.AreEqual(1f, toggle.children[1].threshold);
            Assert.AreEqual(0f, ActiveValue(toggle.children[0].motion, "Body/Hat"));
            Assert.AreEqual(1f, ActiveValue(toggle.children[1].motion, "Body/Hat"));

            var parameter = DbtBuilder.FindParameter(controller, "Hat");
            Assert.AreEqual(AnimatorControllerParameterType.Float, parameter.type);
            Assert.AreEqual(0f, parameter.defaultFloat);
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "One"));
        }

        [Test]
        public void Dbt_DefaultOnSetsFloatDefaultToOne()
        {
            var controller = NewController();
            var request = NewRequest(controller, ToggleBuilder.Mode.DirectBlendTree, "Hat");
            request.defaultOn = true;
            Assert.IsTrue(ToggleBuilder.Apply(request));
            Assert.AreEqual(1f, DbtBuilder.FindParameter(controller, "Hat").defaultFloat);
        }

        [Test]
        public void Dbt_SecondToggleSharesTheExistingLayer()
        {
            var controller = NewController();
            Assert.IsTrue(ToggleBuilder.Apply(NewRequest(controller, ToggleBuilder.Mode.DirectBlendTree, "Hat")));

            var second = NewRequest(controller, ToggleBuilder.Mode.DirectBlendTree, "Cape");
            second.toggleName = "Cape";
            second.parameter = "Cape";
            second.layerIndex = 1;
            Assert.IsTrue(ToggleBuilder.Apply(second));

            Assert.AreEqual(2, controller.layers.Length);
            var root = (BlendTree)controller.layers[1].stateMachine.states[0].state.motion;
            Assert.AreEqual(2, root.children.Length);
        }

        [Test]
        public void Dbt_RejectsNonDbtTargetLayer()
        {
            var controller = NewController();
            controller.layers[0].stateMachine.AddState("Busy");
            var request = NewRequest(controller, ToggleBuilder.Mode.DirectBlendTree, "Hat");
            request.layerIndex = 0;
            Assert.IsNotNull(ToggleBuilder.Validate(request));
            Assert.IsFalse(ToggleBuilder.Apply(request));
        }

        // ---- clips ---------------------------------------------------------

        [Test]
        public void Clips_AreNamedAfterTheToggle()
        {
            var controller = NewController();
            Assert.IsTrue(ToggleBuilder.Apply(NewRequest(controller, ToggleBuilder.Mode.Layer, "Hat")));
            var states = controller.layers[1].stateMachine.states;
            Assert.AreEqual("Hat OFF", states[0].state.motion.name);
            Assert.AreEqual("Hat ON", states[1].state.motion.name);
        }

        [Test]
        public void Clips_KeyEveryTargetInBothClips()
        {
            var controller = NewController();
            var request = NewRequest(controller, ToggleBuilder.Mode.Layer, "A", "B/C");
            Assert.IsTrue(ToggleBuilder.Apply(request));

            var states = controller.layers[1].stateMachine.states;
            foreach (var child in states)
            {
                var clip = (AnimationClip)child.state.motion;
                Assert.AreEqual(2, AnimationUtility.GetCurveBindings(clip).Length);
            }
        }
    }
}

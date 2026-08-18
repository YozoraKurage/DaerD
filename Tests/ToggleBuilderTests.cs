using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The toggle generation core: given targets whose paths are already derived, what lands in
    /// the controller. Nothing here validates anything and nothing is recorded — deciding
    /// whether a toggle may be built, working the paths out of prefab references and keeping the
    /// record are <see cref="ObjectGadgets"/>' half, and are tested against a real prefab in
    /// <c>ObjectGadgetTests</c>. Which is why these tests need neither a prefab nor Modular
    /// Avatar: the core is the part of a toggle that is the same wherever the paths came from.
    /// </summary>
    public class ToggleBuilderTests
    {
        static AnimatorController NewController()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            return controller;
        }

        static ToggleBuilder.Plan NewPlan(AnimatorController controller,
            ToggleBuilder.Mode mode, params string[] paths)
        {
            var plan = new ToggleBuilder.Plan
            {
                controller = controller,
                mode = mode,
                name = "Hat",
                parameter = "Hat",
                layerIndex = -1,
                newLayerName = "DBT",
            };
            foreach (var path in paths)
                plan.targets.Add(new ToggleBuilder.Target { path = path });
            return plan;
        }

        /// <summary>Both clips and the wiring, the way <see cref="ObjectGadgets"/> puts them
        /// together — minus the sub-asset attaching, which an in-memory controller has nowhere
        /// to do.</summary>
        static void Build(ToggleBuilder.Plan plan)
        {
            var onClip = ToggleBuilder.BuildClip(plan, on: true);
            var offClip = ToggleBuilder.BuildClip(plan, on: false);
            if (plan.mode == ToggleBuilder.Mode.Layer)
                ToggleBuilder.BuildLayer(plan, onClip, offClip, out _);
            else
                ToggleBuilder.BuildDirectBlendTree(plan, onClip, offClip, out _);
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

        // ---- rows ------------------------------------------------------------

        /// <summary>The rows are enumerated once and both clips are written from them, so what a
        /// record books as "written" cannot disagree with what the curves say (ADR 0046).</summary>
        [Test]
        public void Rows_DescribeEveryCurveBothClipsGet()
        {
            var plan = NewPlan(null, ToggleBuilder.Mode.Layer, "Body/Hat");
            plan.targets[0].bindings.Add(ToggleBuilder.Binding.Enabled(typeof(Light)));

            var rows = ToggleBuilder.Rows(plan);

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("Body/Hat", rows[0].binding.path);
            Assert.AreEqual(typeof(GameObject), rows[0].binding.type);
            Assert.AreEqual("m_IsActive", rows[0].binding.propertyName);
            Assert.AreEqual(0f, rows[0].Value(false));
            Assert.AreEqual(1f, rows[0].Value(true));
            Assert.AreEqual(typeof(Light), rows[1].binding.type);
            Assert.AreEqual("m_Enabled", rows[1].binding.propertyName);
        }

        [Test]
        public void Rows_InvertedTargetSwapsTheTwoSides()
        {
            var plan = NewPlan(null, ToggleBuilder.Mode.Layer, "Hat");
            plan.targets[0].activeWhenOn = false;

            var rows = ToggleBuilder.Rows(plan);

            Assert.AreEqual(1f, rows[0].Value(false), "the OFF clip shows an inverted target");
            Assert.AreEqual(0f, rows[0].Value(true));
        }

        /// <summary>The merge's own object is "" and is a legitimate target — a gadget that
        /// hides the object the merge sits on is a normal thing to build.</summary>
        [Test]
        public void Rows_TakeTheEmptyPathAsItComes()
        {
            var plan = NewPlan(null, ToggleBuilder.Mode.Layer, string.Empty);
            var rows = ToggleBuilder.Rows(plan);
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(string.Empty, rows[0].binding.path);
        }

        // ---- layer mode ----------------------------------------------------

        [Test]
        public void Layer_BuildsTwoStatesWithInstantTransitions()
        {
            var controller = NewController();
            Build(NewPlan(controller, ToggleBuilder.Mode.Layer, "Body/Hat"));

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
        public void Layer_ReturnsTheMachineTheRecordIsKeyedBy()
        {
            var controller = NewController();
            var plan = NewPlan(controller, ToggleBuilder.Mode.Layer, "Hat");
            var machine = ToggleBuilder.BuildLayer(plan,
                ToggleBuilder.BuildClip(plan, true), ToggleBuilder.BuildClip(plan, false), out _);

            Assert.AreSame(controller.layers[1].stateMachine, machine,
                "the record identifies its layer by the root machine, so the builder has to "
                + "hand that back rather than an index that a reorder would invalidate");
        }

        [Test]
        public void Layer_DefaultOnStartsOnTheOnState()
        {
            var controller = NewController();
            var plan = NewPlan(controller, ToggleBuilder.Mode.Layer, "Hat");
            plan.defaultOn = true;
            Build(plan);

            var layer = controller.layers[1];
            Assert.AreEqual("Hat ON", layer.stateMachine.defaultState.name);
            Assert.IsTrue(DbtBuilder.FindParameter(controller, "Hat").defaultBool);
        }

        [Test]
        public void Layer_InvertedTargetSwapsClipValues()
        {
            var controller = NewController();
            var plan = NewPlan(controller, ToggleBuilder.Mode.Layer, "Hat");
            plan.targets.Add(new ToggleBuilder.Target { path = "Cape", activeWhenOn = false });
            Build(plan);

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
            var plan = NewPlan(controller, ToggleBuilder.Mode.Layer, "Hat");
            ToggleBuilder.BuildLayer(plan, ToggleBuilder.BuildClip(plan, true),
                ToggleBuilder.BuildClip(plan, false), out bool created);

            Assert.IsFalse(created, "which is what stops removing the gadget from taking it away");
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
            Build(NewPlan(controller, ToggleBuilder.Mode.Layer, "Hat"));
            Assert.AreEqual("Hat ON", controller.layers[1].stateMachine.defaultState.name);
        }

        [Test]
        public void Layer_UniquifiesTheLayerName()
        {
            var controller = NewController();
            controller.AddLayer("Hat");
            Build(NewPlan(controller, ToggleBuilder.Mode.Layer, "Hat"));
            Assert.AreEqual("Hat 1", controller.layers[2].name);
        }

        // ---- direct blend tree mode ---------------------------------------

        [Test]
        public void Dbt_Builds1DTreeInsideDirectLayer()
        {
            var controller = NewController();
            Build(NewPlan(controller, ToggleBuilder.Mode.DirectBlendTree, "Body/Hat"));

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
        public void Dbt_ReturnsTheChildItHung()
        {
            var controller = NewController();
            var plan = NewPlan(controller, ToggleBuilder.Mode.DirectBlendTree, "Hat");
            var tree = ToggleBuilder.BuildDirectBlendTree(plan,
                ToggleBuilder.BuildClip(plan, true), ToggleBuilder.BuildClip(plan, false), out _);

            var root = (BlendTree)controller.layers[1].stateMachine.states[0].state.motion;
            Assert.AreSame(root.children[0].motion, tree,
                "the record holds the child by reference — that is the whole of what sweeping "
                + "it is allowed to remove");
        }

        [Test]
        public void Dbt_DefaultOnSetsFloatDefaultToOne()
        {
            var controller = NewController();
            var plan = NewPlan(controller, ToggleBuilder.Mode.DirectBlendTree, "Hat");
            plan.defaultOn = true;
            Build(plan);
            Assert.AreEqual(1f, DbtBuilder.FindParameter(controller, "Hat").defaultFloat);
        }

        [Test]
        public void Dbt_SecondToggleSharesTheExistingLayer()
        {
            var controller = NewController();
            Build(NewPlan(controller, ToggleBuilder.Mode.DirectBlendTree, "Hat"));

            var second = NewPlan(controller, ToggleBuilder.Mode.DirectBlendTree, "Cape");
            second.name = "Cape";
            second.parameter = "Cape";
            second.layerIndex = 1;
            Build(second);

            Assert.AreEqual(2, controller.layers.Length);
            var root = (BlendTree)controller.layers[1].stateMachine.states[0].state.motion;
            Assert.AreEqual(2, root.children.Length);
        }

        // ---- component / blendshape bindings --------------------------------

        static float BindingValue(Motion motion, string path, System.Type type, string property)
        {
            var clip = (AnimationClip)motion;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                if (binding.path == path && binding.type == type && binding.propertyName == property)
                    return AnimationUtility.GetEditorCurve(clip, binding).keys[0].value;
            Assert.Fail("No curve for " + type.Name + "." + property + " at '" + path + "'.");
            return -1f;
        }

        [Test]
        public void Bindings_EnabledCurvesFollowTheToggle()
        {
            var controller = NewController();
            var plan = NewPlan(controller, ToggleBuilder.Mode.Layer, "Hat");
            plan.targets[0].bindings.Add(ToggleBuilder.Binding.Enabled(typeof(Light)));
            Build(plan);

            var states = controller.layers[1].stateMachine.states;
            Assert.AreEqual(0f, BindingValue(states[0].state.motion, "Hat", typeof(Light), "m_Enabled"));
            Assert.AreEqual(1f, BindingValue(states[1].state.motion, "Hat", typeof(Light), "m_Enabled"));
        }

        [Test]
        public void Bindings_BlendShapeUsesOffOnValues()
        {
            var controller = NewController();
            var plan = NewPlan(controller, ToggleBuilder.Mode.Layer, "Face");
            plan.targets[0].toggleActive = false;
            plan.targets[0].bindings.Add(ToggleBuilder.Binding.BlendShape("Smile", 10f, 90f));
            Build(plan);

            var states = controller.layers[1].stateMachine.states;
            Assert.AreEqual(10f, BindingValue(states[0].state.motion, "Face",
                typeof(SkinnedMeshRenderer), "blendShape.Smile"));
            Assert.AreEqual(90f, BindingValue(states[1].state.motion, "Face",
                typeof(SkinnedMeshRenderer), "blendShape.Smile"));
            // toggleActive off: no m_IsActive curve at all
            foreach (var binding in AnimationUtility.GetCurveBindings((AnimationClip)states[0].state.motion))
                Assert.AreNotEqual("m_IsActive", binding.propertyName);
        }

        [Test]
        public void Bindings_InvertedTargetSwapsComponentValuesToo()
        {
            var controller = NewController();
            var plan = NewPlan(controller, ToggleBuilder.Mode.Layer, "Hat");
            plan.targets[0].activeWhenOn = false;
            plan.targets[0].bindings.Add(ToggleBuilder.Binding.BlendShape("Smile", 10f, 90f));
            Build(plan);

            var states = controller.layers[1].stateMachine.states;
            // OFF clip carries the ON values because the target is inverted.
            Assert.AreEqual(90f, BindingValue(states[0].state.motion, "Hat",
                typeof(SkinnedMeshRenderer), "blendShape.Smile"));
            Assert.AreEqual(10f, BindingValue(states[1].state.motion, "Hat",
                typeof(SkinnedMeshRenderer), "blendShape.Smile"));
        }

        /// <summary>A binding whose type could not be resolved is dropped rather than written as
        /// a curve bound to nothing. <see cref="ObjectGadgets"/> refuses such a record by name
        /// before it gets here; this is what happens if anything ever gets past that.</summary>
        [Test]
        public void Bindings_WithNoTypeAreLeftOutOfTheClips()
        {
            var plan = NewPlan(null, ToggleBuilder.Mode.Layer, "Hat");
            plan.targets[0].bindings.Add(new ToggleBuilder.Binding { type = null, property = "m_Enabled" });
            Assert.AreEqual(1, ToggleBuilder.Rows(plan).Count, "only the m_IsActive row");
        }

        // ---- clips ---------------------------------------------------------

        [Test]
        public void Clips_AreNamedAfterTheToggle()
        {
            var controller = NewController();
            Build(NewPlan(controller, ToggleBuilder.Mode.Layer, "Hat"));
            var states = controller.layers[1].stateMachine.states;
            Assert.AreEqual("Hat OFF", states[0].state.motion.name);
            Assert.AreEqual("Hat ON", states[1].state.motion.name);
        }

        [Test]
        public void Clips_KeyEveryTargetInBothClips()
        {
            var controller = NewController();
            Build(NewPlan(controller, ToggleBuilder.Mode.Layer, "A", "B/C"));

            var states = controller.layers[1].stateMachine.states;
            foreach (var child in states)
            {
                var clip = (AnimationClip)child.state.motion;
                Assert.AreEqual(2, AnimationUtility.GetCurveBindings(clip).Length);
            }
        }
    }
}

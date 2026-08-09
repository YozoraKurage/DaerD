using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>Behaviour with data, standing in for an SDK type (found via TypeCache).</summary>
    class IRTestBehaviour : StateMachineBehaviour
    {
        public string payload;
        public int number;
    }

    public class ControllerIRTests
    {
        /// <summary>
        /// A controller exercising everything the IR models: four parameter types, blend
        /// trees (nested, 2D, direct child, normalized flag), behaviours on a state and on a
        /// state machine, parameter-driven state fields, a sub-state machine with
        /// entry/exit/any/state-machine transitions, cross-machine destinations, and a synced
        /// layer with a motion override and a behaviour override.
        /// </summary>
        static AnimatorController BuildSample(out AnimationClip clipA, out AnimationClip clipB)
        {
            var controller = new AnimatorController();
            controller.AddParameter(new AnimatorControllerParameter
            { name = "F", type = AnimatorControllerParameterType.Float, defaultFloat = 0.5f });
            controller.AddParameter(new AnimatorControllerParameter
            { name = "B", type = AnimatorControllerParameterType.Bool, defaultBool = true });
            controller.AddParameter(new AnimatorControllerParameter
            { name = "I", type = AnimatorControllerParameterType.Int, defaultInt = 3 });
            controller.AddParameter("T", AnimatorControllerParameterType.Trigger);

            clipA = new AnimationClip { name = "ClipA" };
            clipB = new AnimationClip { name = "ClipB" };

            controller.AddLayer("Base");
            controller.AddLayer("Main");
            var layers = controller.layers;
            layers[1].defaultWeight = 0.5f;
            layers[1].blendingMode = AnimatorLayerBlendingMode.Additive;
            layers[1].iKPass = true;
            controller.layers = layers;

            var sm = controller.layers[1].stateMachine;
            sm.entryPosition = new Vector3(10f, 20f, 0f);
            sm.exitPosition = new Vector3(900f, 20f, 0f);
            sm.anyStatePosition = new Vector3(10f, 200f, 0f);

            var a = sm.AddState("A", new Vector3(200f, 0f, 0f));
            a.motion = clipA;
            a.speed = 2f;
            a.tag = "combat";
            a.writeDefaultValues = false;
            a.speedParameterActive = true;
            a.speedParameter = "F";

            var b = sm.AddState("B", new Vector3(200f, 100f, 0f));
            var tree = new BlendTree
            {
                name = "Move",
                blendType = BlendTreeType.FreeformDirectional2D,
                blendParameter = "F",
                blendParameterY = "F",
                useAutomaticThresholds = false,
            };
            var inner = new BlendTree
            {
                name = "Inner",
                blendType = BlendTreeType.Direct,
                useAutomaticThresholds = false,
            };
            inner.children = new[]
            {
                new ChildMotion { motion = clipA, directBlendParameter = "F", timeScale = 1.5f },
            };
            DbtBuilder.SetNormalizedBlendValues(inner, true);
            tree.children = new[]
            {
                new ChildMotion { motion = clipB, position = new Vector2(0.3f, 0.7f), mirror = true },
                new ChildMotion { motion = inner, position = new Vector2(-0.5f, 0.1f), cycleOffset = 0.25f },
            };
            b.motion = tree;

            var behaviour = (IRTestBehaviour)a.AddStateMachineBehaviour(typeof(IRTestBehaviour));
            behaviour.payload = "hello";
            behaviour.number = 42;

            var toB = a.AddTransition(b);
            toB.AddCondition(AnimatorConditionMode.If, 0f, "B");
            toB.AddCondition(AnimatorConditionMode.Greater, 0.25f, "F");
            toB.hasExitTime = true;
            toB.exitTime = 0.9f;
            toB.duration = 0.15f;
            toB.interruptionSource = TransitionInterruptionSource.Destination;
            toB.canTransitionToSelf = true;
            var exit = b.AddExitTransition();
            exit.AddCondition(AnimatorConditionMode.IfNot, 0f, "B");
            exit.solo = true;

            var sub = sm.AddStateMachine("Sub", new Vector3(500f, 50f, 0f));
            // On the machine itself, not on a state.
            var subBehaviour = (IRTestBehaviour)sub.AddStateMachineBehaviour(typeof(IRTestBehaviour));
            subBehaviour.payload = "machine";
            subBehaviour.number = 7;

            var d = sub.AddState("D", new Vector3(40f, 40f, 0f));
            d.motion = clipB;
            sub.defaultState = d;
            var crossing = d.AddTransition(a);                     // cross-machine destination
            crossing.AddCondition(AnimatorConditionMode.Equals, 2f, "I");
            a.AddTransition(sub).AddCondition(AnimatorConditionMode.If, 0f, "T");

            var fromSub = sm.AddStateMachineTransition(sub, a);    // Sub node → A in parent view
            fromSub.AddCondition(AnimatorConditionMode.NotEqual, 7f, "I");
            sm.AddStateMachineExitTransition(sub);

            var any = sm.AddAnyStateTransition(d);
            any.AddCondition(AnimatorConditionMode.Less, -1f, "F");
            any.canTransitionToSelf = false;
            var entry = sm.AddEntryTransition(b);
            entry.AddCondition(AnimatorConditionMode.If, 0f, "B");
            sm.defaultState = a;

            // Synced layer over "Main" with a motion override and a behaviour override.
            controller.AddLayer("MainSync");
            layers = controller.layers;
            layers[2].syncedLayerIndex = 1;
            layers[2].syncedLayerAffectsTiming = true;
            layers[2].SetOverrideMotion(a, clipB);
            var overridden = ScriptableObject.CreateInstance<IRTestBehaviour>();
            overridden.payload = "synced";
            layers[2].SetOverrideBehaviours(a, new StateMachineBehaviour[] { overridden });
            controller.layers = layers;
            return controller;
        }

        static string Join(List<string> diffs) => string.Join("\n", diffs);

        [Test]
        public void RoundTrip_RebuildsAnIdenticalController()
        {
            var source = BuildSample(out var clipA, out var clipB);
            var ir = ControllerIR.Parse(source);

            var rebuilt = new AnimatorController();
            var warnings = ControllerIRBuilder.Rebuild(ir, rebuilt, exclusive: true);
            Assert.IsEmpty(warnings, Join(warnings));

            var diffs = ControllerIRDiff.Compare(ir, ControllerIR.Parse(rebuilt));
            Assert.IsEmpty(diffs, Join(diffs));

            Object.DestroyImmediate(source);
            Object.DestroyImmediate(rebuilt);
            Object.DestroyImmediate(clipA);
            Object.DestroyImmediate(clipB);
        }

        /// <summary>Behaviours on a state machine were invisible to the IR: an export dropped
        /// them and Generate deleted them from the controller, with no diff to show for it
        /// because neither side modelled the field. Both halves of that are guarded here.</summary>
        [Test]
        public void MachineBehaviours_SurviveARebuild_AndAreDiffed()
        {
            var source = new AnimatorController();
            source.AddLayer("L");
            var sm = source.layers[0].stateMachine;
            sm.AddState("S", Vector3.zero);
            ((IRTestBehaviour)sm.AddStateMachineBehaviour(typeof(IRTestBehaviour))).payload = "root";
            var sub = sm.AddStateMachine("Sub", new Vector3(300f, 0f, 0f));
            ((IRTestBehaviour)sub.AddStateMachineBehaviour(typeof(IRTestBehaviour))).number = 7;

            var ir = ControllerIR.Parse(source);
            var rebuilt = new AnimatorController();
            var warnings = ControllerIRBuilder.Rebuild(ir, rebuilt, exclusive: true);
            Assert.IsEmpty(warnings, Join(warnings));

            var root = rebuilt.layers[0].stateMachine;
            Assert.AreEqual(1, root.behaviours.Length);
            Assert.AreEqual("root", ((IRTestBehaviour)root.behaviours[0]).payload);
            Assert.AreEqual(7,
                ((IRTestBehaviour)root.stateMachines[0].stateMachine.behaviours[0]).number);

            // And losing one is now a reported difference rather than a silent one.
            var stripped = ControllerIR.Parse(source);
            stripped.layers[0].machine.behaviours.Clear();
            Assert.IsNotEmpty(ControllerIRDiff.Compare(ir, stripped));

            Object.DestroyImmediate(source);
            Object.DestroyImmediate(rebuilt);
        }

        [Test]
        public void Diff_ReportsAnIntroducedChange()
        {
            var source = BuildSample(out var clipA, out var clipB);
            var before = ControllerIR.Parse(source);

            var state = controllerState(source, 1, "A");
            state.speed = 9f;
            var toB = state.transitions[0];
            toB.duration = 0.5f;

            var diffs = ControllerIRDiff.Compare(before, ControllerIR.Parse(source));
            Assert.IsTrue(diffs.Exists(d => d.Contains("State 'A'") && d.Contains("speed")), Join(diffs));
            Assert.IsTrue(diffs.Exists(d => d.Contains("duration")), Join(diffs));

            Object.DestroyImmediate(source);
            Object.DestroyImmediate(clipA);
            Object.DestroyImmediate(clipB);
        }

        static AnimatorState controllerState(AnimatorController controller, int layer, string name)
        {
            foreach (var child in controller.layers[layer].stateMachine.states)
                if (child.state.name == name) return child.state;
            Assert.Fail("state not found");
            return null;
        }

        [Test]
        public void PartialRebuild_ReplacesOnlyTheNamedLayer_AndNeverRetypesParameters()
        {
            // Destination: two layers, one parameter whose type clashes with the recipe's.
            var destination = new AnimatorController();
            destination.AddParameter("Speed", AnimatorControllerParameterType.Int);
            destination.AddLayer("Keep");
            destination.AddLayer("Target");
            destination.layers[0].stateMachine.AddState("Untouched", Vector3.zero);
            destination.layers[1].stateMachine.AddState("Old", Vector3.zero);

            // Recipe IR: a fresh "Target" layer plus a new and a clashing parameter.
            var ir = new ControllerIR();
            ir.parameters.Add(new ControllerIR.Param
            { name = "Speed", type = AnimatorControllerParameterType.Float });
            ir.parameters.Add(new ControllerIR.Param
            { name = "Fresh", type = AnimatorControllerParameterType.Bool, defaultBool = true });
            var layer = new ControllerIR.Layer { name = "Target", machine = new ControllerIR.Machine() };
            var state = new ControllerIR.State { name = "New", position = new Vector3(30f, 40f, 0f) };
            layer.machine.states.Add(state);
            layer.machine.defaultState = "New";
            ir.layers.Add(layer);

            var warnings = ControllerIRBuilder.Rebuild(ir, destination, exclusive: false);

            Assert.AreEqual(2, destination.layers.Length, "no layer added or lost");
            Assert.AreEqual("Keep", destination.layers[0].name);
            Assert.AreEqual("Target", destination.layers[1].name, "replaced in place");
            Assert.AreEqual("Untouched", destination.layers[0].stateMachine.states[0].state.name);
            Assert.AreEqual("New", destination.layers[1].stateMachine.states[0].state.name);

            Assert.AreEqual(AnimatorControllerParameterType.Int,
                DbtBuilder.FindParameter(destination, "Speed").type, "clash left untouched");
            Assert.IsTrue(warnings.Exists(w => w.Contains("Speed")), Join(warnings));
            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(destination, "Fresh").type, "missing parameter added");

            Object.DestroyImmediate(destination);
        }

        [Test]
        public void RoundTrip_SurvivesASecondGeneration()
        {
            // Regenerating from the same IR twice (the recipe workflow) must stay stable —
            // no drift from Unity default-value quirks accumulating pass over pass.
            var source = BuildSample(out var clipA, out var clipB);
            var ir = ControllerIR.Parse(source);

            var rebuilt = new AnimatorController();
            ControllerIRBuilder.Rebuild(ir, rebuilt, exclusive: true);
            ControllerIRBuilder.Rebuild(ir, rebuilt, exclusive: true);

            var diffs = ControllerIRDiff.Compare(ir, ControllerIR.Parse(rebuilt));
            Assert.IsEmpty(diffs, Join(diffs));

            Object.DestroyImmediate(source);
            Object.DestroyImmediate(rebuilt);
            Object.DestroyImmediate(clipA);
            Object.DestroyImmediate(clipB);
        }
    }
}

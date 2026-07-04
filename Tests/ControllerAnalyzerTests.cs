using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class ControllerAnalyzerTests
    {
        static AnimatorController NewController(out AnimatorStateMachine sm)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            sm = controller.layers[0].stateMachine;
            return controller;
        }

        static List<ControllerAnalyzer.Issue> Terminal(AnimatorController controller) =>
            ControllerAnalyzer.FindTerminalStateGroups(controller.layers[0]);

        [Test]
        public void TrappedCycle_OffTheMainLoop_IsFlagged()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");   // default
            var b = sm.AddState("B");
            var c = sm.AddState("C");
            a.AddTransition(b);
            b.AddTransition(c);
            c.AddTransition(b);         // B↔C loop with no way out

            var issues = Terminal(controller);

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("'B', 'C'", issues[0].message);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void CycleWithAnExit_IsNotFlagged()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var c = sm.AddState("C");
            a.AddTransition(b);
            b.AddTransition(c);
            c.AddTransition(b);
            c.AddExitTransition();      // the loop can leave via Exit

            Assert.AreEqual(0, Terminal(controller).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void TheDefaultStatesOwnLoop_IsNotFlagged()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");   // default
            var b = sm.AddState("B");
            a.AddTransition(b);
            b.AddTransition(a);         // main loop — normal animator shape

            Assert.AreEqual(0, Terminal(controller).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void SingleDeadEndState_IsFlagged()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var stuck = sm.AddState("Stuck");
            a.AddTransition(stuck);     // nothing ever leaves Stuck

            var issues = Terminal(controller);

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("'Stuck'", issues[0].message);
            Assert.AreEqual(ControllerAnalyzer.Severity.Info, issues[0].severity);

            Object.DestroyImmediate(controller);
        }

        static List<ControllerAnalyzer.Issue> OfKind(AnimatorController controller, ControllerAnalyzer.Kind kind) =>
            ControllerAnalyzer.Analyze(controller).FindAll(i => i.kind == kind);

        [Test]
        public void MissingMotion_StateWithoutMotion_IsFlagged()
        {
            var controller = NewController(out var sm);
            sm.AddState("Bare");

            var issues = OfKind(controller, ControllerAnalyzer.Kind.MissingMotion);

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("'Bare'", issues[0].message);
            Assert.AreEqual(ControllerAnalyzer.Severity.Warning, issues[0].severity);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void MissingMotion_StateWithClip_IsNotFlagged()
        {
            var controller = NewController(out var sm);
            var clip = new AnimationClip();
            sm.AddState("HasClip").motion = clip;

            Assert.AreEqual(0, OfKind(controller, ControllerAnalyzer.Kind.MissingMotion).Count);

            Object.DestroyImmediate(clip);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void MissingMotion_BlendTreeWithEmptyChildSlot_IsFlagged()
        {
            var controller = NewController(out var sm);
            var clip = new AnimationClip();
            var tree = new BlendTree { name = "Move" };
            tree.children = new[]
            {
                new ChildMotion { motion = clip, timeScale = 1f },
                new ChildMotion { motion = null, timeScale = 1f },
            };
            sm.AddState("Locomotion").motion = tree;

            var issues = OfKind(controller, ControllerAnalyzer.Kind.MissingMotion);

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("'Move'", issues[0].message);
            Assert.AreEqual(tree, issues[0].context);

            Object.DestroyImmediate(tree);
            Object.DestroyImmediate(clip);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void UnusedParameter_FixDeletesIt()
        {
            var controller = NewController(out _);
            controller.AddParameter("Ghost", AnimatorControllerParameterType.Bool);

            var issues = OfKind(controller, ControllerAnalyzer.Kind.UnusedParameter);

            Assert.AreEqual(1, issues.Count);
            Assert.IsNotNull(issues[0].fix);
            issues[0].fix();
            Assert.AreEqual(0, controller.parameters.Length);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void DeadTransition_FixDeletesIt()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var t = a.AddTransition(b);
            t.hasExitTime = false;   // no conditions + no exit time → can never fire

            var issues = OfKind(controller, ControllerAnalyzer.Kind.DeadTransition);

            Assert.AreEqual(1, issues.Count);
            Assert.IsNotNull(issues[0].fix);
            issues[0].fix();
            Assert.AreEqual(0, a.transitions.Length);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void DuplicateCondition_FixKeepsOneOfEach()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("P", AnimatorControllerParameterType.Bool);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var t = a.AddTransition(b);
            t.AddCondition(AnimatorConditionMode.If, 0f, "P");
            t.AddCondition(AnimatorConditionMode.If, 0f, "P");

            var issues = OfKind(controller, ControllerAnalyzer.Kind.DuplicateCondition);

            Assert.AreEqual(1, issues.Count);
            Assert.IsNotNull(issues[0].fix);
            issues[0].fix();
            Assert.AreEqual(1, t.conditions.Length);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void EmptyLayer_IsFlagged()
        {
            var controller = NewController(out var sm);
            sm.AddState("A");
            controller.AddLayer("Hollow");

            var issues = OfKind(controller, ControllerAnalyzer.Kind.EmptyLayer);

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("'Hollow'", issues[0].message);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ZeroWeightLayer_IsFlagged_UntilWeightIsRaised()
        {
            var controller = NewController(out var sm);
            sm.AddState("A");
            controller.AddLayer("Fx");
            controller.layers[1].stateMachine.AddState("B");

            var layers = controller.layers;
            layers[1].defaultWeight = 0f;
            controller.layers = layers;
            Assert.AreEqual(1, OfKind(controller, ControllerAnalyzer.Kind.LayerWeight).Count);

            layers = controller.layers;
            layers[1].defaultWeight = 1f;
            controller.layers = layers;
            Assert.AreEqual(0, OfKind(controller, ControllerAnalyzer.Kind.LayerWeight).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void MissingBehaviour_FixStripsNullEntries()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            a.behaviours = new StateMachineBehaviour[] { null };

            var issues = OfKind(controller, ControllerAnalyzer.Kind.MissingBehaviour);

            Assert.AreEqual(1, issues.Count);
            Assert.IsNotNull(issues[0].fix);
            issues[0].fix();
            Assert.AreEqual(0, a.behaviours.Length);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void TransitionIntoSubStateMachine_CountsAsLeavingViaItsDefaultState()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var child = sm.AddStateMachine("Child");
            var p = child.AddState("P");
            child.defaultState = p;
            a.AddTransition(b);
            b.AddTransition(child);     // B leaves through the sub-state machine...
            p.AddTransition(a);         // ...and P returns to the main loop

            Assert.AreEqual(0, Terminal(controller).Count);

            Object.DestroyImmediate(controller);
        }
    }
}

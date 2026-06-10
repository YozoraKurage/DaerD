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

using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
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

        static List<AnalyzerIssue> Terminal(AnimatorController controller) =>
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
            Assert.AreEqual(IssueSeverity.Info, issues[0].severity);

            Object.DestroyImmediate(controller);
        }

        static List<AnalyzerIssue> OfKind(AnimatorController controller, IssueKind kind) =>
            ControllerAnalyzer.Analyze(controller).FindAll(i => i.kind == kind);

        [Test]
        public void MissingMotion_StateWithoutMotion_IsFlagged()
        {
            var controller = NewController(out var sm);
            sm.AddState("Bare").writeDefaultValues = true;

            var issues = OfKind(controller, IssueKind.MissingMotion);

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("'Bare'", issues[0].message);
            Assert.AreEqual(IssueSeverity.Warning, issues[0].severity);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void MissingMotion_OnWriteDefaultsOffState_IsAnError()
        {
            var controller = NewController(out var sm);
            sm.AddState("Frozen").writeDefaultValues = false;

            var issues = OfKind(controller, IssueKind.MissingMotion);

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("'Frozen'", issues[0].message);
            Assert.AreEqual(IssueSeverity.Error, issues[0].severity);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void MissingMotion_StateWithClip_IsNotFlagged()
        {
            var controller = NewController(out var sm);
            var clip = new AnimationClip();
            sm.AddState("HasClip").motion = clip;

            Assert.AreEqual(0, OfKind(controller, IssueKind.MissingMotion).Count);

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

            var issues = OfKind(controller, IssueKind.MissingMotion);

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

            var issues = OfKind(controller, IssueKind.UnusedParameter);

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

            var issues = OfKind(controller, IssueKind.DeadTransition);

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

            var issues = OfKind(controller, IssueKind.DuplicateCondition);

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

            var issues = OfKind(controller, IssueKind.EmptyLayer);

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
            Assert.AreEqual(1, OfKind(controller, IssueKind.LayerWeight).Count);

            layers = controller.layers;
            layers[1].defaultWeight = 1f;
            controller.layers = layers;
            Assert.AreEqual(0, OfKind(controller, IssueKind.LayerWeight).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void MissingBehaviour_FixStripsNullEntries()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            a.behaviours = new StateMachineBehaviour[] { null };

            var issues = OfKind(controller, IssueKind.MissingBehaviour);

            Assert.AreEqual(1, issues.Count);
            Assert.IsNotNull(issues[0].fix);
            issues[0].fix();
            Assert.AreEqual(0, a.behaviours.Length);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AssignEmptyClip_FillsOnlyMotionlessStates()
        {
            var controller = NewController(out var sm);
            var empty = new AnimationClip { name = "Empty" };
            var other = new AnimationClip { name = "Other" };
            var bare = sm.AddState("Bare");
            var filled = sm.AddState("Filled");
            filled.motion = other;

            ControllerAnalyzer.AssignEmptyClip(bare, empty);
            ControllerAnalyzer.AssignEmptyClip(filled, empty);

            Assert.AreSame(empty, bare.motion);
            Assert.AreSame(other, filled.motion);   // an assigned motion is never overwritten

            Object.DestroyImmediate(controller);
            Object.DestroyImmediate(empty);
            Object.DestroyImmediate(other);
        }

        [Test]
        public void FillEmptySlots_FillsOnlyTheEmptyChildren()
        {
            var empty = new AnimationClip { name = "Empty" };
            var other = new AnimationClip { name = "Other" };
            var tree = new BlendTree { name = "T" };
            tree.children = new[]
            {
                new ChildMotion { motion = other, timeScale = 1f },
                new ChildMotion { motion = null, timeScale = 1f },
            };

            ControllerAnalyzer.FillEmptySlots(tree, empty);

            Assert.AreSame(other, tree.children[0].motion);
            Assert.AreSame(empty, tree.children[1].motion);

            Object.DestroyImmediate(tree);
            Object.DestroyImmediate(empty);
            Object.DestroyImmediate(other);
        }

        // ---- direct blend tree health -----------------------------------------

        static List<AnalyzerIssue> DirectTreeIssues(AnimatorController controller)
        {
            var result = new List<AnalyzerIssue>();
            foreach (var issue in ControllerAnalyzer.Analyze(controller))
                if (issue.kind == IssueKind.DirectBlendTree)
                    result.Add(issue);
            return result;
        }

        [Test]
        public void DirectTreeStateWithWriteDefaultsOff_IsFlagged_AndTheFixTurnsItOn()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("One", UnityEngine.AnimatorControllerParameterType.Float);
            var state = sm.AddState("DBT");
            state.writeDefaultValues = false;
            var tree = new BlendTree { name = "Root", blendType = BlendTreeType.Direct };
            state.motion = tree;

            var issues = DirectTreeIssues(controller);
            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(IssueSeverity.Error, issues[0].severity);
            Assert.IsNotNull(issues[0].fix);

            issues[0].fix();
            Assert.IsTrue(state.writeDefaultValues);
            Assert.AreEqual(0, DirectTreeIssues(controller).Count);

            Object.DestroyImmediate(controller);
            Object.DestroyImmediate(tree);
        }

        [Test]
        public void DirectChildWeights_MissingEmptyAndNonFloat_AreFlagged()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("IntWeight", UnityEngine.AnimatorControllerParameterType.Int);
            var state = sm.AddState("DBT");
            state.writeDefaultValues = true;
            var clip = new AnimationClip();
            var tree = new BlendTree { name = "Root", blendType = BlendTreeType.Direct };
            tree.AddChild(clip);
            tree.AddChild(clip);
            tree.AddChild(clip);
            var children = tree.children;
            children[0].directBlendParameter = "";           // never plays → Warning
            children[1].directBlendParameter = "Missing";    // no such parameter → Error
            children[2].directBlendParameter = "IntWeight";  // wrong type → Warning
            tree.children = children;
            state.motion = tree;

            var issues = DirectTreeIssues(controller);
            Assert.AreEqual(3, issues.Count);

            Object.DestroyImmediate(controller);
            Object.DestroyImmediate(tree);
            Object.DestroyImmediate(clip);
        }

        [Test]
        public void GeneratedDbtGadget_PassesTheDirectTreeChecks()
        {
            var controller = NewController(out _);
            controller.AddParameter("A", UnityEngine.AnimatorControllerParameterType.Float);
            controller.AddParameter("B", UnityEngine.AnimatorControllerParameterType.Float);

            Assert.IsTrue(AapGadgets.Apply(new AapGadgets.Request
            {
                controller = controller,
                kind = AapGadgets.Kind.AddRanged,
                inputA = "A",
                inputB = "B",
                output = "Out",
                layerIndex = -1,
                newLayerName = "DBT",
            }));

            Assert.AreEqual(0, DirectTreeIssues(controller).Count);

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

        // ---- AAP vs Parameter Driver ------------------------------------------

        static List<AnalyzerIssue> AapDriverIssues(AnimatorController controller)
        {
            var issues = ControllerAnalyzer.Analyze(controller);
            return issues.FindAll(i => i.kind == IssueKind.AapDriver);
        }

        [Test]
        public void DriverTouchingAnAnimatedParameter_IsFlaggedOncePerDirection()
        {
            // The driver is a VRChat SDK behaviour; without it there is nothing to read.
            if (!VrcParameterDriver.SdkAvailable)
                Assert.Ignore("The VRChat SDK is not present in this project.");

            var controller = NewController(out var sm);
            controller.AddParameter("Aap", AnimatorControllerParameterType.Float);
            controller.AddParameter("Plain", AnimatorControllerParameterType.Float);

            var clip = new AnimationClip { name = "Aap" };
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), "Aap"),
                AnimationCurve.Constant(0f, 1f, 1f));
            sm.AddState("Write").motion = clip;

            // Two states driving the same parameter the same way collapse into one issue —
            // an async-sync layer holds dozens of these.
            foreach (var name in new[] { "A", "B" })
            {
                var driver = VrcParameterDriver.AddTo(sm.AddState(name));
                VrcParameterDriver.AddCopyEntry(driver, "Aap", "Plain");   // reads the AAP
                VrcParameterDriver.AddSetEntry(driver, "Aap", 1f);         // writes over it
            }

            var issues = AapDriverIssues(controller);

            Assert.AreEqual(2, issues.Count, "one for the reads, one for the writes");
            Assert.IsTrue(issues.Exists(i => i.message.Contains("copy from it")));
            Assert.IsTrue(issues.Exists(i => i.message.Contains("write it too")));
            foreach (var issue in issues)
            {
                StringAssert.Contains("'Aap'", issue.message);
                StringAssert.Contains("2 Parameter Driver", issue.message);
                // Warning, not Error: a clip on a weight-0 layer counts as "animation writes
                // it" without ever running, so the finding isn't certain enough to be one.
                Assert.AreEqual(IssueSeverity.Warning, issue.severity);
            }

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void DriverOnAPlainParameter_IsNotFlagged()
        {
            if (!VrcParameterDriver.SdkAvailable)
                Assert.Ignore("The VRChat SDK is not present in this project.");

            var controller = NewController(out var sm);
            controller.AddParameter("Plain", AnimatorControllerParameterType.Float);
            var driver = VrcParameterDriver.AddTo(sm.AddState("Drive"));
            VrcParameterDriver.AddSetEntry(driver, "Plain", 1f);

            Assert.AreEqual(0, AapDriverIssues(controller).Count);

            Object.DestroyImmediate(controller);
        }
    }
}

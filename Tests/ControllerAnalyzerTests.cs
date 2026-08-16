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

        /// <summary>An exit time of 0 is not "leave at once" — it is Unity's way of writing
        /// "leave at the end of a lap", the same as 1. Measured in PlayModeProbeTests; these
        /// only pin what the analyzer says about it.</summary>
        static AnimatorStateTransition ExitTimeTransition(AnimatorState from, AnimatorState to, float exitTime)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = true;
            t.exitTime = exitTime;
            return t;
        }

        [Test]
        public void ExitTimeZero_WithAConditionOnIt_IsAWarningTheFixClears()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var t = ExitTimeTransition(a, b, 0f);
            t.AddCondition(AnimatorConditionMode.If, 0f, "Go");

            var issues = OfKind(controller, IssueKind.ExitTimeZero);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(IssueSeverity.Warning, issues[0].severity);
            Assert.IsNotNull(issues[0].fix);
            issues[0].fix();

            Assert.IsFalse(t.hasExitTime);
            Assert.IsEmpty(OfKind(controller, IssueKind.ExitTimeZero));
            // The condition is what keeps the repair honest: clearing exit time off a
            // transition that had none would only trade this row for a dead-transition one.
            Assert.IsEmpty(OfKind(controller, IssueKind.DeadTransition));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ExitTimeZero_WithNoCondition_IsInfoWithNoFixToOffer()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            ExitTimeTransition(a, b, 0f);

            var issues = OfKind(controller, IssueKind.ExitTimeZero);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(IssueSeverity.Info, issues[0].severity);
            Assert.IsNull(issues[0].fix, "which of the two repairs is right depends on the intent");

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AnExitTimePartWayThroughTheLap_IsNothingToSayAnythingAbout()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var c = sm.AddState("C");
            ExitTimeTransition(a, b, 0.5f).AddCondition(AnimatorConditionMode.If, 0f, "Go");
            ExitTimeTransition(b, c, 0.5f);

            Assert.IsEmpty(OfKind(controller, IssueKind.ExitTimeZero));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ATransitionWithExitTimeSwitchedOff_IsNotAnExitTimeZero()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var t = a.AddTransition(b);
            t.hasExitTime = false;
            t.exitTime = 0f;            // the field keeps its value; nothing reads it
            t.AddCondition(AnimatorConditionMode.If, 0f, "Go");

            Assert.IsEmpty(OfKind(controller, IssueKind.ExitTimeZero));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ExitTimeZero_OnAnAnyStateTransition_IsLeftAlone()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            sm.AddState("A");
            var z = sm.AddState("Z");
            var t = sm.AddAnyStateTransition(z);
            t.hasExitTime = true;
            t.exitTime = 0f;
            t.AddCondition(AnimatorConditionMode.If, 0f, "Go");

            // What exit time counts against with no source state of its own was never
            // measured, so the analyzer says nothing rather than guessing.
            Assert.IsEmpty(OfKind(controller, IssueKind.ExitTimeZero));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ASoloedTransition_IsReportedWithWhatItShutsOut()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var c = sm.AddState("C");
            var d = sm.AddState("D");
            var soloed = a.AddTransition(b);
            soloed.solo = true;
            a.AddTransition(c);
            a.AddTransition(d);

            var issues = OfKind(controller, IssueKind.SoloTransition);

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("'A'", issues[0].message);
            StringAssert.Contains("2", issues[0].message, "the two transitions solo shuts out");

            issues[0].fix();
            Assert.IsFalse(soloed.solo);
            Assert.IsEmpty(OfKind(controller, IssueKind.SoloTransition));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void SoloOnTheOnlyTransitionThatCouldRun_ShutsNothingOut()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var c = sm.AddState("C");
            a.AddTransition(b).solo = true;
            a.AddTransition(c).mute = true;   // already disabled, so solo takes nothing from it

            Assert.IsEmpty(OfKind(controller, IssueKind.SoloTransition));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ASoloThatIsAlsoMuted_KeepsNothingAlive()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var c = sm.AddState("C");
            var soloed = a.AddTransition(b);
            soloed.solo = true;
            soloed.mute = true;   // muting beats soloing
            a.AddTransition(c);

            // With the solo muted, nothing is soloed any more and A→C runs as usual.
            Assert.IsEmpty(OfKind(controller, IssueKind.SoloTransition));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void SoloOnAnAnyStateTransition_IsReportedAgainstTheStateMachine()
        {
            var controller = NewController(out var sm);
            var b = sm.AddState("B");
            var c = sm.AddState("C");
            sm.AddAnyStateTransition(b).solo = true;
            sm.AddAnyStateTransition(c);

            var issues = OfKind(controller, IssueKind.SoloTransition);

            Assert.AreEqual(1, issues.Count);
            Assert.AreSame(sm, issues[0].context);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ATransitionThatAsksForTwoIncompatibleThings_CanNeverFire()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("Wet", AnimatorControllerParameterType.Float);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var t = a.AddTransition(b);
            t.AddCondition(AnimatorConditionMode.Greater, 0.8f, "Wet");
            t.AddCondition(AnimatorConditionMode.Less, 0.2f, "Wet");   // window never opens

            var issues = OfKind(controller, IssueKind.DeadTransition);

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("Wet > 0.8", issues[0].message);
            StringAssert.Contains("Wet < 0.2", issues[0].message);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ARangeThatDoesOpen_IsLeftAlone()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("Wet", AnimatorControllerParameterType.Float);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var t = a.AddTransition(b);
            t.AddCondition(AnimatorConditionMode.Greater, 0.2f, "Wet");
            t.AddCondition(AnimatorConditionMode.Less, 0.8f, "Wet");

            Assert.IsEmpty(OfKind(controller, IssueKind.DeadTransition));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ContradictionsAreOnlyReadWithinOneParameter()
        {
            var wet = new AnimatorCondition { parameter = "Wet", mode = AnimatorConditionMode.Greater, threshold = 1f };
            var dry = new AnimatorCondition { parameter = "Dry", mode = AnimatorConditionMode.Less, threshold = 0f };
            Assert.IsFalse(ControllerAnalyzer.Contradict(wet, dry));

            var on = new AnimatorCondition { parameter = "On", mode = AnimatorConditionMode.If };
            var off = new AnimatorCondition { parameter = "On", mode = AnimatorConditionMode.IfNot };
            Assert.IsTrue(ControllerAnalyzer.Contradict(on, off));
            Assert.IsTrue(ControllerAnalyzer.Contradict(off, on), "and the other way round");

            var isTwo = new AnimatorCondition { parameter = "N", mode = AnimatorConditionMode.Equals, threshold = 2f };
            var isThree = new AnimatorCondition { parameter = "N", mode = AnimatorConditionMode.Equals, threshold = 3f };
            var notTwo = new AnimatorCondition { parameter = "N", mode = AnimatorConditionMode.NotEqual, threshold = 2f };
            Assert.IsTrue(ControllerAnalyzer.Contradict(isTwo, isThree));
            Assert.IsTrue(ControllerAnalyzer.Contradict(isTwo, notTwo));
            Assert.IsFalse(ControllerAnalyzer.Contradict(isTwo, isTwo), "the same twice over is a duplicate, not a contradiction");

            // Reported for neither type: impossible for an Int, ordinary for a Float, and the
            // check deliberately does not know which one this is.
            var over = new AnimatorCondition { parameter = "N", mode = AnimatorConditionMode.Greater, threshold = 0f };
            var under = new AnimatorCondition { parameter = "N", mode = AnimatorConditionMode.Less, threshold = 1f };
            Assert.IsFalse(ControllerAnalyzer.Contradict(over, under));
        }

        [Test]
        public void AnyStateTransitionsThatCanInterruptThemselves_AreReportedTogether()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("On", AnimatorControllerParameterType.Bool);
            var b = sm.AddState("B");
            var c = sm.AddState("C");
            foreach (var target in new[] { b, c })
            {
                var t = sm.AddAnyStateTransition(target);
                t.canTransitionToSelf = true;
                t.AddCondition(AnimatorConditionMode.If, 0f, "On");
            }

            var issues = OfKind(controller, IssueKind.AnyStateRetrigger);

            Assert.AreEqual(1, issues.Count, "one row per state machine, not per transition");
            StringAssert.Contains("2", issues[0].message);

            issues[0].fix();
            Assert.IsEmpty(OfKind(controller, IssueKind.AnyStateRetrigger));
            foreach (var t in sm.anyStateTransitions)
                Assert.IsFalse(t.canTransitionToSelf);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AnyStateOnATrigger_IsLeftAlone()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("Fire", AnimatorControllerParameterType.Trigger);
            var b = sm.AddState("B");
            var t = sm.AddAnyStateTransition(b);
            t.canTransitionToSelf = true;
            t.AddCondition(AnimatorConditionMode.If, 0f, "Fire");

            // A Trigger is consumed when it is read, so the condition stops holding by itself.
            Assert.IsEmpty(OfKind(controller, IssueKind.AnyStateRetrigger));

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
            // Assigning an array with a null in it does not produce a null entry — the setter
            // drops it, and the state comes back with no behaviours at all. The way a real
            // controller ends up with one is the script behind a behaviour going away, so
            // that is what this reproduces: add a behaviour, then destroy the object. The
            // slot stays, holding a reference to something no longer there.
            var doomed = a.AddStateMachineBehaviour(typeof(IRTestBehaviour));
            Object.DestroyImmediate(doomed);
            Assert.AreEqual(1, a.behaviours.Length, "expected a surviving slot with nothing in it");

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
            var write = sm.AddState("Write");   // the layer's default state
            write.motion = clip;

            // Two states driving the same parameter the same way collapse into one issue —
            // an async-sync layer holds dozens of these. Both are wired up: a driver on a
            // state the layer can never enter is not a finding.
            foreach (var name in new[] { "A", "B" })
            {
                var state = sm.AddState(name);
                write.AddTransition(state);
                var driver = VrcParameterDriver.AddTo(state);
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
                // Warning, not Error: the walk knows which states can be entered, but not
                // whether a weight-0 layer gets raised by a Layer Control living in some other
                // controller — so the finding is still short of certain.
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

        [Test]
        public void DriverOnAStateTheLayerCanNeverEnter_IsNotFlagged()
        {
            if (!VrcParameterDriver.SdkAvailable)
                Assert.Ignore("The VRChat SDK is not present in this project.");

            var controller = NewController(out var sm);
            controller.AddParameter("Aap", AnimatorControllerParameterType.Float);
            controller.AddParameter("Plain", AnimatorControllerParameterType.Float);
            sm.AddState("Write").motion = AapClip("Aap");   // default state, so this one runs

            // Nothing transitions to Parked, so the driver on it never executes and the
            // collision it would cause never happens.
            var driver = VrcParameterDriver.AddTo(sm.AddState("Parked"));
            VrcParameterDriver.AddCopyEntry(driver, "Aap", "Plain");
            VrcParameterDriver.AddSetEntry(driver, "Aap", 1f);

            Assert.AreEqual(0, AapDriverIssues(controller).Count);

            Object.DestroyImmediate(controller);
        }

        // ---- AAP written from more than one layer ------------------------------

        static AnimationClip AapClip(string parameter)
        {
            var clip = new AnimationClip { name = parameter + " AAP" };
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), parameter),
                AnimationCurve.Constant(0f, 1f, 1f));
            return clip;
        }

        /// <summary>Adds a layer whose default state writes <paramref name="parameter"/> as an AAP.</summary>
        static void AddAapLayer(AnimatorController controller, string name, string parameter, float weight)
        {
            controller.AddLayer(name);
            var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = weight;
            controller.layers = layers;
            controller.layers[controller.layers.Length - 1].stateMachine
                .AddState("Write").motion = AapClip(parameter);
        }

        static List<AnalyzerIssue> AapLayerIssues(AnimatorController controller) =>
            ControllerAnalyzer.Analyze(controller).FindAll(i => i.kind == IssueKind.AapLayers);

        [Test]
        public void OneAapWrittenFromTwoPlayingLayers_IsFlaggedWithTheLayerThatWins()
        {
            var controller = new AnimatorController();
            controller.AddParameter("Aap", AnimatorControllerParameterType.Float);
            AddAapLayer(controller, "Gadgets", "Aap", 1f);
            AddAapLayer(controller, "Gadgets 2", "Aap", 1f);

            var issues = AapLayerIssues(controller);

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("'Aap'", issues[0].message);
            StringAssert.Contains("'Gadgets'", issues[0].message);
            StringAssert.Contains("'Gadgets 2'", issues[0].message);
            Assert.AreEqual(IssueSeverity.Warning, issues[0].severity);
            // Ping opens the layer that actually reaches the parameter — the last one.
            Assert.AreEqual(1, issues[0].layerIndex);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void TwoLayersWritingDifferentAaps_AreNotAConflict()
        {
            var controller = new AnimatorController();
            controller.AddParameter("A", AnimatorControllerParameterType.Float);
            controller.AddParameter("B", AnimatorControllerParameterType.Float);
            AddAapLayer(controller, "Gadgets", "A", 1f);
            AddAapLayer(controller, "Gadgets 2", "B", 1f);

            Assert.AreEqual(0, AapLayerIssues(controller).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ASecondWriterOnAWeightZeroLayer_IsNotAConflict()
        {
            var controller = new AnimatorController();
            controller.AddParameter("Aap", AnimatorControllerParameterType.Float);
            AddAapLayer(controller, "Gadgets", "Aap", 1f);
            // A weight-0 layer holding an override is how you build a deliberate one.
            AddAapLayer(controller, "Override", "Aap", 0f);

            Assert.AreEqual(0, AapLayerIssues(controller).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void TwoStatesOfOneLayerWritingTheSameAap_AreNotAConflict()
        {
            var controller = new AnimatorController();
            controller.AddParameter("Aap", AnimatorControllerParameterType.Float);
            AddAapLayer(controller, "Gadgets", "Aap", 1f);
            var sm = controller.layers[0].stateMachine;
            var second = sm.AddState("Write 2");
            second.motion = AapClip("Aap");
            sm.defaultState.AddTransition(second);

            Assert.AreEqual(0, AapLayerIssues(controller).Count);

            Object.DestroyImmediate(controller);
        }

        // ---- unreachable states -------------------------------------------------

        [Test]
        public void AnIslandOfStates_IsReportedAsUnreachable_NotAsHavingNoIncomingTransition()
        {
            var controller = NewController(out var sm);
            sm.AddState("A");           // default
            var island = sm.AddState("Island");
            var partner = sm.AddState("Partner");
            island.AddTransition(partner);
            partner.AddTransition(island);

            var issues = OfKind(controller, IssueKind.UnreachableState);

            Assert.AreEqual(2, issues.Count);
            foreach (var issue in issues)
                StringAssert.Contains("themselves unreachable", issue.message,
                    "both have an incoming transition — the old wording would be wrong");

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AStateNothingPointsAt_KeepsTheOlderWording()
        {
            var controller = NewController(out var sm);
            sm.AddState("A");           // default
            sm.AddState("Loose");

            var issues = OfKind(controller, IssueKind.UnreachableState);

            Assert.AreEqual(1, issues.Count);
            StringAssert.Contains("no incoming transition", issues[0].message);

            Object.DestroyImmediate(controller);
        }

        // ---- dead entry branches ------------------------------------------------

        /// <summary>
        /// A layer begins at its default state however its Entry conditions read; the
        /// conditions are only read on the way back through Entry. Measured in
        /// PlayModeProbeTests — these pin what the analyzer makes of it.
        /// </summary>
        [Test]
        public void ConditionalRootEntries_InALayerThatNeverReachesExit_AreReportedTogether()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("P", AnimatorControllerParameterType.Bool);
            sm.AddState("Start");       // default — where the layer begins either way
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            sm.AddEntryTransition(a).AddCondition(AnimatorConditionMode.If, 0f, "P");
            sm.AddEntryTransition(b).AddCondition(AnimatorConditionMode.IfNot, 0f, "P");

            var issues = OfKind(controller, IssueKind.DeadEntryBranch);

            Assert.AreEqual(1, issues.Count, "one row per layer, not one per branch");
            Assert.AreEqual(IssueSeverity.Warning, issues[0].severity);
            StringAssert.Contains("2", issues[0].message, "the branches it counted");
            Assert.AreSame(sm, issues[0].context);
            Assert.AreEqual(0, issues[0].layerIndex);
            Assert.IsNull(issues[0].fix, "the repair depends on which of the two the author meant");

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void TheSameEntryBranch_InALayerThatCanPassExit_IsLeftAlone()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("P", AnimatorControllerParameterType.Bool);
            var start = sm.AddState("Start");   // default
            var a = sm.AddState("A");
            sm.AddEntryTransition(a).AddCondition(AnimatorConditionMode.If, 0f, "P");
            start.AddExitTransition();          // ...and the layer comes back round to Entry

            Assert.IsEmpty(OfKind(controller, IssueKind.DeadEntryBranch));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AConditionalEntryInsideASubMachine_IsNotARootEntry()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("P", AnimatorControllerParameterType.Bool);
            var start = sm.AddState("Start");   // default
            var child = sm.AddStateMachine("Child");
            var p = child.AddState("P1");
            var q = child.AddState("Q");
            child.defaultState = p;
            child.AddEntryTransition(q).AddCondition(AnimatorConditionMode.If, 0f, "P");
            start.AddTransition(child);

            // A sub machine's Entry is read on every visit, so this branch does decide things.
            Assert.IsEmpty(OfKind(controller, IssueKind.DeadEntryBranch));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void RootEntriesWithNoConditions_DecideNothingToBeginWith()
        {
            var controller = NewController(out var sm);
            sm.AddState("Start");       // default
            var a = sm.AddState("A");
            sm.AddEntryTransition(a);   // unconditional: the fall-through, not a branch

            Assert.IsEmpty(OfKind(controller, IssueKind.DeadEntryBranch));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AnExitOnlyAnUnreachableIslandCouldTake_IsNoWayBackToEntry()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("P", AnimatorControllerParameterType.Bool);
            sm.AddState("Start");       // default
            var a = sm.AddState("A");
            sm.AddEntryTransition(a).AddCondition(AnimatorConditionMode.If, 0f, "P");
            var island = sm.AddState("Island");
            var partner = sm.AddState("Partner");
            island.AddTransition(partner);
            partner.AddTransition(island);
            island.AddExitTransition();   // an Exit nothing can ever walk to

            Assert.AreEqual(1, OfKind(controller, IssueKind.DeadEntryBranch).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ASyncedLayer_DoesNotRepeatTheSourceLayersEntryFinding()
        {
            var controller = NewController(out var sm);
            controller.AddParameter("P", AnimatorControllerParameterType.Bool);
            sm.AddState("Start");       // default
            var a = sm.AddState("A");
            sm.AddEntryTransition(a).AddCondition(AnimatorConditionMode.If, 0f, "P");
            controller.AddLayer("Mirror");
            var layers = controller.layers;
            layers[1].syncedLayerIndex = 0;
            controller.layers = layers;

            var issues = OfKind(controller, IssueKind.DeadEntryBranch);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(0, issues[0].layerIndex, "reported against the layer that owns the machine");

            Object.DestroyImmediate(controller);
        }

        // ---- built-in parameter types -------------------------------------------

        static List<AnalyzerIssue> BuiltInTypeIssues(AnimatorController controller, string name,
            AnimatorControllerParameterType type)
        {
            controller.AddParameter(name, type);
            return OfKind(controller, IssueKind.VrcParameters);
        }

        [Test]
        public void AnIntBuiltInDeclaredFloat_IsReportedWithoutAnyStore()
        {
            var controller = NewController(out _);

            // No expression parameter store anywhere: this check reads VRChat's own table, so
            // it has to run on a controller that belongs to no avatar.
            var issues = BuiltInTypeIssues(controller, "GestureLeft", AnimatorControllerParameterType.Float);

            Assert.AreEqual(1, issues.Count);
            Assert.AreEqual(IssueSeverity.Info, issues[0].severity);
            StringAssert.Contains("GestureLeft", issues[0].message);
            Assert.IsNull(issues[0].fix, "retyping a parameter would invalidate the conditions on it");

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ABuiltInDeclaredTheWayVrChatWritesIt_IsNothingToReport()
        {
            var controller = NewController(out _);

            Assert.IsEmpty(BuiltInTypeIssues(controller, "GestureLeft", AnimatorControllerParameterType.Int));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AFloatBuiltInDeclaredInt_IsReportedToo()
        {
            var controller = NewController(out _);

            Assert.AreEqual(1,
                BuiltInTypeIssues(controller, "VelocityX", AnimatorControllerParameterType.Int).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ABuiltInDeclaredTrigger_IsReported()
        {
            var controller = NewController(out _);

            // Nothing in the official table is a Trigger, so this disagrees by construction.
            Assert.AreEqual(1,
                BuiltInTypeIssues(controller, "Grounded", AnimatorControllerParameterType.Trigger).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AParameterTheAvatarInventedItself_HasNoOfficialTypeToDisagreeWith()
        {
            var controller = NewController(out _);
            controller.AddParameter("Costume", AnimatorControllerParameterType.Int);
            // VRChat matches built-in names exactly, so this one is the avatar's own too.
            controller.AddParameter("gestureleft", AnimatorControllerParameterType.Float);

            Assert.IsEmpty(OfKind(controller, IssueKind.VrcParameters));

            Object.DestroyImmediate(controller);
        }

        // ---- what the builders write, read back by the analyzer ----------------

        /// <summary>
        /// The one category a generator is never allowed to produce: a transition with neither
        /// a condition nor an exit time is a transition that is never taken, so a watcher
        /// wired that way is simply deaf — it looks right in the graph and does nothing at
        /// runtime. 0e91fc3 fixed a handful of those by hand; this is the wiring that says so
        /// the next time, for every setup a generator can be asked to build.
        ///
        /// Only this category. The rest of the analyzer's lint is about shapes an author might
        /// mean (a motion-less machinery state, a layer at weight 0), and holding generated
        /// output to all of it would fail on things that are correct.
        /// </summary>
        static void AssertNothingIsWiredDeaf(AnimatorController controller, string what)
        {
            var deaf = new List<string>();
            foreach (var issue in OfKind(controller, IssueKind.DeadTransition))
                if (issue.message.Contains("no conditions and no exit time"))
                    deaf.Add("  " + issue.message);

            Assert.IsEmpty(deaf, what + " built " + deaf.Count
                + " transition(s) nothing can ever fire:\n" + string.Join("\n", deaf));
        }

        static AnimatorController MultiplexedController(
            System.Action<AsyncSyncBuilder.Request> tweak)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("F", AnimatorControllerParameterType.Float);
            controller.AddParameter("G", AnimatorControllerParameterType.Float);
            controller.AddParameter("B", AnimatorControllerParameterType.Bool);
            controller.AddParameter("I", AnimatorControllerParameterType.Int);

            var request = new AsyncSyncBuilder.Request
            {
                controller = controller,
                baseName = "Async",
                encoding = AsyncSyncBuilder.IndexEncoding.Int,
                stepSeconds = 0.3f,
                assignEmptyClip = false,
                addToStore = false,
                // The drivers carry values, not routing — every transition this test is about
                // is built either way, and skipping them is what lets the check run with the
                // VRChat SDK absent as well as present.
                skipDrivers = true,
                layerIndex = -1,
            };
            request.targets.AddRange(new[] { "F", "G", "B", "I" });
            tweak?.Invoke(request);

            Assert.IsNull(AsyncSyncBuilder.Validate(request), "the setup itself is not buildable");
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));
            return controller;
        }

        static GraphFrameData.AsyncSyncConfig.SyncGroup Grouped(string name,
            params string[] members)
        {
            var group = new GraphFrameData.AsyncSyncConfig.SyncGroup { name = name };
            group.members.AddRange(members);
            return group;
        }

        [Test]
        public void AsyncSync_BuildsNoTransitionThatCanNeverFire()
        {
            // The plain pass, and then each of the things that add a layer or a route of their
            // own — the watcher layers are where a deaf transition has actually happened.
            var cases = new Dictionary<string, System.Action<AsyncSyncBuilder.Request>>
            {
                { "a plain cycle", null },
                { "the remote initialized flag", r => r.ready = true },
                { "the drift flag", r => r.stale = true },
                { "both flags", r => { r.ready = true; r.stale = true; } },
                { "sync requests", r => r.requestTargets.AddRange(new[] { "B", "I" }) },
                {
                    "requests and both flags",
                    r =>
                    {
                        r.ready = true;
                        r.stale = true;
                        r.requestTargets.Add("I");
                    }
                },
                { "a group", r => r.groups.Add(Grouped("Outfit", "F", "I")) },
                {
                    // The commit guard leans on the flag here, which is a condition on a
                    // transition that is otherwise the one route out of Idle.
                    "a group and the remote initialized flag",
                    r =>
                    {
                        r.ready = true;
                        r.groups.Add(Grouped("Outfit", "F", "I"));
                    }
                },
                { "a repeat-step clock", r => r.allowRepeatSteps = true },
                { "two float channels", r => r.floatChannels = 2 },
                { "a weight no control hands out", r => r.rates["F"] = 3 },
            };

            foreach (var pair in cases)
            {
                var controller = MultiplexedController(pair.Value);
                AssertNothingIsWiredDeaf(controller, "async sync with " + pair.Key);
                Object.DestroyImmediate(controller);
            }
        }

        [Test]
        public void NetworkSync_BuildsNoTransitionThatCanNeverFire()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddLayer("Target");
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            var machine = controller.layers[1].stateMachine;
            var first = machine.AddState("S0");
            var second = machine.AddState("S1");
            first.AddTransition(second).AddCondition(AnimatorConditionMode.If, 0f, "Go");

            var request = new NetworkSyncBuilder.Request
            {
                controller = controller,
                layerIndex = 1,
                syncParameter = "Target/Sync",
                packIntoSubMachine = false,
                skipDrivers = true,
            };
            Assert.IsNull(NetworkSyncBuilder.Validate(request));
            Assert.IsTrue(NetworkSyncBuilder.Apply(request));

            AssertNothingIsWiredDeaf(controller, "network sync");

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ObjectToggle_BuildsNoTransitionThatCanNeverFire()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");

            var request = new ToggleBuilder.Request
            {
                controller = controller,
                mode = ToggleBuilder.Mode.Layer,
                toggleName = "Hat",
                parameter = "Hat",
                layerIndex = -1,
                newLayerName = "Toggles",
            };
            request.targets.Add(new ToggleBuilder.Target { path = "Armature/Head/Hat" });
            Assert.IsNull(ToggleBuilder.Validate(request));
            Assert.IsTrue(ToggleBuilder.Apply(request));

            AssertNothingIsWiredDeaf(controller, "the object toggle");

            Object.DestroyImmediate(controller);
        }
    }
}

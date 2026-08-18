using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD.Tests
{
    public class EdgeCommandsTests
    {
        AnimatorController _controller;
        DaerDContext _context;
        EdgeCommands _edges;
        AnimatorStateMachine _sm;
        AnimatorState _a, _b, _c, _d;

        [SetUp]
        public void SetUp()
        {
            _controller = new AnimatorController();
            _controller.AddLayer("Base");
            _controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            _context = new DaerDContext();
            _context.SetController(_controller);
            _sm = _context.CurrentStateMachine;
            _a = _sm.AddState("A", new Vector3(0f, 0f, 0f));
            _b = _sm.AddState("B", new Vector3(100f, 0f, 0f));
            _c = _sm.AddState("C", new Vector3(200f, 0f, 0f));
            _d = _sm.AddState("D", new Vector3(300f, 0f, 0f));
            _edges = new EdgeCommands(_context);
        }

        [TearDown]
        public void TearDown()
        {
            // Static clipboard: seeded batches read from it, so it must not leak between tests.
            TransitionClipboard.Clear();
            Object.DestroyImmediate(_controller);
        }

        static List<TransitionEnd> Ends(params AnimatorState[] states)
        {
            var ends = new List<TransitionEnd>();
            foreach (var state in states)
                ends.Add(TransitionEnd.Of(state));
            return ends;
        }

        [Test]
        public void Chain_OverNStates_MakesNMinusOneTransitions()
        {
            var created = _edges.Chain(Ends(_a, _b, _c, _d), false);

            Assert.AreEqual(3, created.Count);
            Assert.AreEqual(1, _a.transitions.Length);
            Assert.AreEqual(_b, _a.transitions[0].destinationState);
            Assert.AreEqual(1, _b.transitions.Length);
            Assert.AreEqual(_c, _b.transitions[0].destinationState);
            Assert.AreEqual(1, _c.transitions.Length);
            Assert.AreEqual(_d, _c.transitions[0].destinationState);
            Assert.AreEqual(0, _d.transitions.Length, "the last link has nowhere to go");
        }

        [Test]
        public void Chain_NeedsAtLeastTwoEnds()
        {
            Assert.AreEqual(0, _edges.Chain(Ends(_a), false).Count);
            Assert.AreEqual(0, _edges.Chain(null, false).Count);
        }

        [Test]
        public void FanOut_PointsOneSourceAtEveryTarget()
        {
            var created = _edges.FanOut(TransitionEnd.Of(_a), Ends(_b, _c, _d), false);

            Assert.AreEqual(3, created.Count);
            Assert.AreEqual(3, _a.transitions.Length);
        }

        [Test]
        public void FanOut_FromAnyState_LandsOnTheStateMachine()
        {
            var created = _edges.FanOut(TransitionEnd.AnyState, Ends(_b, _c), false);

            Assert.AreEqual(2, created.Count);
            Assert.AreEqual(2, _sm.anyStateTransitions.Length);
            Assert.AreEqual(0, _a.transitions.Length);
        }

        [Test]
        public void FanIn_PointsEverySourceAtOneTarget()
        {
            var created = _edges.FanIn(Ends(_a, _b, _c), TransitionEnd.Of(_d), false);

            Assert.AreEqual(3, created.Count);
            Assert.AreEqual(_d, _a.transitions[0].destinationState);
            Assert.AreEqual(_d, _b.transitions[0].destinationState);
            Assert.AreEqual(_d, _c.transitions[0].destinationState);
        }

        [Test]
        public void CrossProduct_MakesEveryPair_SkippingSelfLoops()
        {
            Assert.AreEqual(4, _edges.CrossProduct(Ends(_a, _b), Ends(_c, _d), false).Count);

            var overlapping = _edges.CrossProduct(Ends(_a, _b), Ends(_a, _b), false);
            Assert.AreEqual(2, overlapping.Count, "A→A and B→B are dropped as self-loops");
        }

        [Test]
        public void Batches_RefuseTheCombinationsTheGraphRefuses()
        {
            // Entry and Any State cannot transition straight to Exit, and nothing starts at Exit.
            Assert.AreEqual(0, _edges.Chain(new List<TransitionEnd> { TransitionEnd.Entry, TransitionEnd.Exit }, false).Count);
            Assert.AreEqual(0, _edges.Chain(new List<TransitionEnd> { TransitionEnd.AnyState, TransitionEnd.Exit }, false).Count);
            Assert.AreEqual(0, _edges.Chain(new List<TransitionEnd> { TransitionEnd.Exit, TransitionEnd.Of(_a) }, false).Count);
            Assert.AreEqual(0, _edges.Chain(new List<TransitionEnd> { TransitionEnd.Of(_a), TransitionEnd.None }, false).Count);
            Assert.AreEqual(0, _a.transitions.Length);
        }

        [Test]
        public void CreateTransition_ToExit_MarksTheTransitionAsExiting()
        {
            var created = _edges.CreateTransition(TransitionEnd.Of(_a), TransitionEnd.Exit);

            Assert.IsNotNull(created);
            Assert.IsTrue(created.isExit);
            Assert.AreEqual(1, _a.transitions.Length);
        }

        [Test]
        public void Reverse_SwapsTheEndsAndKeepsTheConditions()
        {
            var original = _a.AddTransition(_b);
            original.AddCondition(AnimatorConditionMode.If, 0f, "Go");
            original.duration = 0.42f;
            original.hasExitTime = false;

            var created = _edges.Reverse(TransitionEnd.Of(_a), TransitionEnd.Of(_b),
                new List<AnimatorTransitionBase> { original });

            Assert.IsNotNull(created);
            Assert.AreEqual(1, created.Count);
            Assert.AreEqual(0, _a.transitions.Length, "the original is gone");
            Assert.AreEqual(1, _b.transitions.Length);

            var reversed = _b.transitions[0];
            Assert.AreEqual(_a, reversed.destinationState);
            Assert.AreEqual(1, reversed.conditions.Length);
            Assert.AreEqual("Go", reversed.conditions[0].parameter);
            Assert.AreEqual(AnimatorConditionMode.If, reversed.conditions[0].mode);
            Assert.AreEqual(0.42f, reversed.duration, 0.0001f);
            Assert.IsFalse(reversed.hasExitTime);
        }

        [Test]
        public void Reverse_WithoutAStateMachine_ReportsThatNothingRan()
        {
            var bare = new EdgeCommands(new DaerDContext());
            Assert.IsNull(bare.Reverse(TransitionEnd.Of(_a), TransitionEnd.Of(_b),
                new List<AnimatorTransitionBase>()));
        }

        [Test]
        public void Replicate_AddsACopyAlongsideTheOriginal()
        {
            var original = _a.AddTransition(_b);
            original.AddCondition(AnimatorConditionMode.If, 0f, "Go");

            var created = _edges.Replicate(TransitionEnd.Of(_a), TransitionEnd.Of(_b),
                new List<AnimatorTransitionBase> { original });

            Assert.AreEqual(1, created.Count);
            Assert.AreEqual(2, _a.transitions.Length, "the original stays, the copy joins it");
            Assert.AreEqual(_b, _a.transitions[1].destinationState);
            Assert.AreEqual("Go", _a.transitions[1].conditions[0].parameter);
        }

        [Test]
        public void Redirect_PointsEveryTransitionAtTheNewDestination()
        {
            var first = _a.AddTransition(_b);
            var second = _a.AddTransition(_b);

            _edges.Redirect(new List<AnimatorTransitionBase> { first, second }, TransitionEnd.Of(_c));

            Assert.AreEqual(_c, first.destinationState);
            Assert.AreEqual(_c, second.destinationState);
            Assert.IsFalse(first.isExit);

            _edges.Redirect(new List<AnimatorTransitionBase> { first }, TransitionEnd.Exit);
            Assert.IsTrue(first.isExit);
            Assert.IsNull(first.destinationState);
        }

        [Test]
        public void RemoveTransitionFrom_TakesTheTransitionOffItsOwner()
        {
            var stateTransition = _a.AddTransition(_b);
            var anyTransition = _sm.AddAnyStateTransition(_b);
            var entryTransition = _sm.AddEntryTransition(_b);

            EdgeCommands.RemoveTransitionFrom(TransitionEnd.Of(_a), stateTransition, _sm);
            EdgeCommands.RemoveTransitionFrom(TransitionEnd.AnyState, anyTransition, _sm);
            EdgeCommands.RemoveTransitionFrom(TransitionEnd.Entry, entryTransition, _sm);

            Assert.AreEqual(0, _a.transitions.Length);
            Assert.AreEqual(0, _sm.anyStateTransitions.Length);
            Assert.AreEqual(0, _sm.entryTransitions.Length);
        }

        [Test]
        public void CanConnect_MirrorsWhatTheGraphAllows()
        {
            var state = TransitionEnd.Of(_a);
            var machine = TransitionEnd.Of(_sm);

            Assert.IsTrue(TransitionEnd.CanConnect(state, TransitionEnd.Of(_b)));
            Assert.IsTrue(TransitionEnd.CanConnect(state, machine));
            Assert.IsTrue(TransitionEnd.CanConnect(state, TransitionEnd.Exit));
            Assert.IsTrue(TransitionEnd.CanConnect(machine, TransitionEnd.Exit));
            Assert.IsTrue(TransitionEnd.CanConnect(TransitionEnd.Entry, state));
            Assert.IsTrue(TransitionEnd.CanConnect(TransitionEnd.AnyState, state));

            Assert.IsFalse(TransitionEnd.CanConnect(TransitionEnd.Entry, TransitionEnd.Exit));
            Assert.IsFalse(TransitionEnd.CanConnect(TransitionEnd.AnyState, TransitionEnd.Exit));
            Assert.IsFalse(TransitionEnd.CanConnect(TransitionEnd.Exit, state));
            Assert.IsFalse(TransitionEnd.CanConnect(state, TransitionEnd.Entry));
            Assert.IsFalse(TransitionEnd.CanConnect(state, TransitionEnd.None));
            Assert.IsFalse(TransitionEnd.CanConnect(TransitionEnd.None, state));
        }

        [Test]
        public void SameAs_ComparesTheAnimatorObject_NotTheWrapper()
        {
            Assert.IsTrue(TransitionEnd.Of(_a).SameAs(TransitionEnd.Of(_a)));
            Assert.IsFalse(TransitionEnd.Of(_a).SameAs(TransitionEnd.Of(_b)));
            Assert.IsTrue(TransitionEnd.Entry.SameAs(TransitionEnd.Entry));
            Assert.IsFalse(TransitionEnd.Entry.SameAs(TransitionEnd.Exit));
        }
        // ---- order ------------------------------------------------------------

        static List<string> Destinations(AnimatorTransitionBase[] transitions)
        {
            var names = new List<string>();
            foreach (var t in transitions)
                names.Add(t.destinationState != null ? t.destinationState.name : "?");
            return names;
        }

        [Test]
        public void Reorder_ChangesWhichTransitionTheAnimatorTriesFirst()
        {
            _a.AddTransition(_b);
            _a.AddTransition(_c);
            _a.AddTransition(_d);
            var source = TransitionEnd.Of(_a);

            Assert.IsTrue(EdgeCommands.Reorder(source, _sm, 2, 0));

            CollectionAssert.AreEqual(new[] { "D", "B", "C" },
                Destinations(EdgeCommands.TransitionsFrom(source, _sm)));
        }

        [Test]
        public void Reorder_KeepsTheSameTransitionObjects()
        {
            var first = _a.AddTransition(_b);
            var second = _a.AddTransition(_c);

            EdgeCommands.Reorder(TransitionEnd.Of(_a), _sm, 0, 1);

            // The conditions and settings live on these objects, and the graph edges point at
            // them: rebuilding the list out of copies would silently drop both.
            Assert.AreSame(second, _a.transitions[0]);
            Assert.AreSame(first, _a.transitions[1]);
        }

        [Test]
        public void Reorder_OfAnyStateTransitions_RewritesTheStateMachinesList()
        {
            _sm.AddAnyStateTransition(_b);
            _sm.AddAnyStateTransition(_c);
            var source = TransitionEnd.AnyState;

            Assert.IsTrue(EdgeCommands.Reorder(source, _sm, 1, 0));

            CollectionAssert.AreEqual(new[] { "C", "B" },
                Destinations(EdgeCommands.TransitionsFrom(source, _sm)));
        }

        [Test]
        public void Reorder_OfEntryTransitions_RewritesTheEntryList()
        {
            _sm.AddEntryTransition(_b);
            _sm.AddEntryTransition(_c);
            _sm.AddEntryTransition(_d);
            var source = TransitionEnd.Entry;

            // Entry transitions are AnimatorTransition, not AnimatorStateTransition — writing
            // them back through the wrong array type would empty the list instead of ordering it.
            Assert.IsTrue(EdgeCommands.Reorder(source, _sm, 0, 2));

            CollectionAssert.AreEqual(new[] { "C", "D", "B" },
                Destinations(EdgeCommands.TransitionsFrom(source, _sm)));
        }

        [Test]
        public void Reorder_OfASubStateMachinesTransitions_RewritesThatMachinesList()
        {
            var nested = _sm.AddStateMachine("Nested");
            _sm.AddStateMachineTransition(nested, _b);
            _sm.AddStateMachineTransition(nested, _c);
            var source = TransitionEnd.Of(nested);

            Assert.IsTrue(EdgeCommands.Reorder(source, _sm, 1, 0));

            CollectionAssert.AreEqual(new[] { "C", "B" },
                Destinations(EdgeCommands.TransitionsFrom(source, _sm)));
        }

        [Test]
        public void Reorder_ThatMovesNothing_SaysSo()
        {
            _a.AddTransition(_b);
            _a.AddTransition(_c);
            var source = TransitionEnd.Of(_a);

            Assert.IsFalse(EdgeCommands.Reorder(source, _sm, 1, 1), "onto its own slot");
            Assert.IsFalse(EdgeCommands.Reorder(source, _sm, 0, 5), "past the end");
            Assert.IsFalse(EdgeCommands.Reorder(source, _sm, -1, 0), "from nowhere");
            CollectionAssert.AreEqual(new[] { "B", "C" },
                Destinations(EdgeCommands.TransitionsFrom(source, _sm)));
        }

        [Test]
        public void TransitionsFrom_AnEndThatCannotBeASource_IsEmpty()
        {
            Assert.AreEqual(0, EdgeCommands.TransitionsFrom(TransitionEnd.Exit, _sm).Length);
            Assert.AreEqual(0, EdgeCommands.TransitionsFrom(TransitionEnd.None, _sm).Length);
        }

        // ---- rows ---------------------------------------------------------------

        [Test]
        public void ARowNumbersATransitionByWhereItReallyIs_NotByWhereItIsDrawn()
        {
            _a.AddTransition(_b);
            var second = _a.AddTransition(_c);
            _a.AddTransition(_d);
            var fourth = _a.AddTransition(_b);

            // What the inspector had before: one edge's transitions, which for A→B is the
            // first and the fourth of A's four. Numbering them 1 and 2 would say the second
            // one is tried before A→C and A→D, and it is not.
            var group = new TransitionGroup(TransitionEnd.Of(_a), false,
                new List<AnimatorTransitionBase> { _a.transitions[0], fourth });
            var rows = TransitionListGui.RowsFor(group.Transitions,
                new List<TransitionGroup> { group }, _sm);

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual(1, rows[0].Priority);
            Assert.AreEqual(4, rows[1].Priority);
            Assert.AreNotSame(second, rows[1].Transition);
        }

        [Test]
        public void RowsOfASource_AreItsWholeListInOrder()
        {
            _a.AddTransition(_b);
            _a.AddTransition(_c);

            var rows = TransitionListGui.RowsOf(TransitionEnd.Of(_a), _sm);

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual(1, rows[0].Priority);
            Assert.AreEqual(2, rows[1].Priority);
            Assert.AreEqual("A", rows[0].Source.Label);
        }
    }
}

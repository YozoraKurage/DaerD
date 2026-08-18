using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD.Tests
{
    public class GraphLayoutTests
    {
        static AnimatorState StateAt(AnimatorStateMachine sm, string name, Vector3 position)
        {
            var state = sm.AddState(name, position);
            return state;
        }

        static Vector3 PositionOf(AnimatorStateMachine sm, AnimatorState state)
        {
            foreach (var cs in sm.states)
                if (cs.state == state) return cs.position;
            return Vector3.negativeInfinity;
        }

        [Test]
        public void Align_Row_EqualizesYToAverage_AndKeepsX()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var a = StateAt(sm, "A", new Vector3(10f, 0f, 0f));
            var b = StateAt(sm, "B", new Vector3(50f, 60f, 0f));
            var c = StateAt(sm, "C", new Vector3(90f, 120f, 0f));

            GraphLayout.Align(sm, new List<AnimatorState> { a, b, c }, GraphLayout.AlignAxis.Row);

            // Average Y of 0, 60, 120 is 60; X is left untouched.
            Assert.AreEqual(60f, PositionOf(sm, a).y, 1e-4f);
            Assert.AreEqual(60f, PositionOf(sm, b).y, 1e-4f);
            Assert.AreEqual(60f, PositionOf(sm, c).y, 1e-4f);
            Assert.AreEqual(10f, PositionOf(sm, a).x, 1e-4f);
            Assert.AreEqual(50f, PositionOf(sm, b).x, 1e-4f);
            Assert.AreEqual(90f, PositionOf(sm, c).x, 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Align_Column_EqualizesXToAverage_AndKeepsY()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var a = StateAt(sm, "A", new Vector3(0f, 10f, 0f));
            var b = StateAt(sm, "B", new Vector3(40f, 50f, 0f));

            GraphLayout.Align(sm, new List<AnimatorState> { a, b }, GraphLayout.AlignAxis.Column);

            // Average X of 0 and 40 is 20; Y is left untouched.
            Assert.AreEqual(20f, PositionOf(sm, a).x, 1e-4f);
            Assert.AreEqual(20f, PositionOf(sm, b).x, 1e-4f);
            Assert.AreEqual(10f, PositionOf(sm, a).y, 1e-4f);
            Assert.AreEqual(50f, PositionOf(sm, b).y, 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Align_OnlyMovesSelectedStates()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var a = StateAt(sm, "A", new Vector3(0f, 0f, 0f));
            var b = StateAt(sm, "B", new Vector3(0f, 100f, 0f));
            var untouched = StateAt(sm, "C", new Vector3(0f, 999f, 0f));

            GraphLayout.Align(sm, new List<AnimatorState> { a, b }, GraphLayout.AlignAxis.Row);

            Assert.AreEqual(50f, PositionOf(sm, a).y, 1e-4f);
            Assert.AreEqual(50f, PositionOf(sm, b).y, 1e-4f);
            Assert.AreEqual(999f, PositionOf(sm, untouched).y, 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Align_WithFewerThanTwoStates_IsNoOp()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var a = StateAt(sm, "A", new Vector3(7f, 13f, 0f));

            Assert.DoesNotThrow(() =>
                GraphLayout.Align(sm, new List<AnimatorState> { a }, GraphLayout.AlignAxis.Row));
            Assert.DoesNotThrow(() =>
                GraphLayout.Align(sm, new List<AnimatorState>(), GraphLayout.AlignAxis.Column));

            Assert.AreEqual(13f, PositionOf(sm, a).y, 1e-4f);
            Assert.AreEqual(7f, PositionOf(sm, a).x, 1e-4f);

            Object.DestroyImmediate(controller);
        }
    }
}

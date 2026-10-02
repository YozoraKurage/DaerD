using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.IR;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The IR rebuild makes its states, sub-machines, transitions and behaviours itself and
    /// hands each owner one array, instead of going through AddState / AddTransition. These pin
    /// that nothing those calls used to do was lost on the way — the controller read back is the
    /// controller described, the sub-assets land in the file, and the side effects Add* had
    /// (unique names, the first state becoming the default) still happen — and what a detached
    /// <see cref="GenerationTarget"/> changes.
    /// </summary>
    public class BatchedRebuildTests
    {
        const string AssetPath = "Assets/DD Batched Rebuild Probe.controller";
        readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void Destroy()
        {
            foreach (var made in _cleanup)
                if (made != null) Object.DestroyImmediate(made);
            _cleanup.Clear();
            AssetDatabase.DeleteAsset(AssetPath);
        }

        AnimatorController Memory(string name)
        {
            var controller = new AnimatorController { name = name };
            _cleanup.Add(controller);
            return controller;
        }

        /// <summary>A controller using every owner of transitions there is: a state, Any State,
        /// Entry, a sub-machine's own exits, and Exit itself.</summary>
        AnimatorController Source()
        {
            var controller = Memory("Source");
            controller.AddParameter("p", AnimatorControllerParameterType.Int);
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var idle = sm.AddState("Idle", new Vector3(200, 0));
            var walk = sm.AddState("Walk", new Vector3(200, 80));
            var sub = sm.AddStateMachine("Sub", new Vector3(400, 0));
            var inner = sub.AddState("Inner", new Vector3(200, 0));
            var t = idle.AddTransition(walk);
            t.AddCondition(AnimatorConditionMode.Equals, 1, "p");
            t.duration = 0.3f;
            idle.AddTransition(sub).AddCondition(AnimatorConditionMode.Equals, 2, "p");
            walk.AddExitTransition().hasExitTime = true;
            sm.AddAnyStateTransition(idle).AddCondition(AnimatorConditionMode.Equals, 0, "p");
            sm.AddEntryTransition(walk).AddCondition(AnimatorConditionMode.Equals, 5, "p");
            sm.AddStateMachineExitTransition(sub);
            sm.AddStateMachineTransition(sub, walk);
            inner.AddExitTransition();
            return controller;
        }

        [Test]
        public void ARebuiltController_ReadsBackAsTheOneDescribed()
        {
            var ir = ControllerIR.Parse(Source());
            var rebuilt = Memory("Rebuilt");

            var warnings = ControllerIRBuilder.Rebuild(ir, rebuilt, exclusive: true);

            CollectionAssert.IsEmpty(warnings);
            CollectionAssert.IsEmpty(ControllerIRDiff.Compare(ir, ControllerIR.Parse(rebuilt)));
        }

        [Test]
        public void OnDisk_EveryMadeObjectIsFiledInTheController()
        {
            var ir = ControllerIR.Parse(Source());
            var controller = AnimatorController.CreateAnimatorControllerAtPath(AssetPath);

            ControllerIRBuilder.Rebuild(ir, controller, exclusive: true);
            AssetDatabase.SaveAssets();

            var filed = new HashSet<Object>(AssetDatabase.LoadAllAssetsAtPath(AssetPath));
            var sm = controller.layers[0].stateMachine;
            var made = new List<Object> { sm };
            made.AddRange(sm.states.Select(s => (Object)s.state));
            made.AddRange(sm.stateMachines.Select(m => (Object)m.stateMachine));
            made.AddRange(sm.states.SelectMany(s => s.state.transitions));
            made.AddRange(sm.anyStateTransitions);
            made.AddRange(sm.entryTransitions);
            var sub = sm.stateMachines[0].stateMachine;
            made.AddRange(sm.GetStateMachineTransitions(sub));
            made.AddRange(sub.states.Select(s => (Object)s.state));
            made.AddRange(sub.states.SelectMany(s => s.state.transitions));
            Assert.AreEqual(5 + 8, made.Count, "the probe's own count: 5 objects + 8 transitions");
            foreach (var obj in made)
                Assert.IsTrue(filed.Contains(obj), obj.GetType().Name + " '" + obj.name + "' is not in the file");
        }

        [Test]
        public void DuplicateNamesAndTheDefaultState_ComeOutAsAddStateMadeThem()
        {
            // What AddState itself does with a second "A" and an empty machine's first state.
            var reference = Memory("Reference");
            reference.AddLayer("Base");
            var refSm = reference.layers[0].stateMachine;
            refSm.AddState("A");
            refSm.AddState("A");

            var source = Memory("Duplicates");
            source.AddLayer("Base");
            var sm = source.layers[0].stateMachine;
            sm.AddState("A");
            sm.AddState("B").name = "A";   // Unity allows a duplicate set by hand
            var ir = ControllerIR.Parse(source);
            ir.layers[0].machine.defaultState = null;
            var rebuilt = Memory("Rebuilt");

            ControllerIRBuilder.Rebuild(ir, rebuilt, exclusive: true);

            var built = rebuilt.layers[0].stateMachine;
            CollectionAssert.AreEqual(refSm.states.Select(s => s.state.name).ToArray(),
                built.states.Select(s => s.state.name).ToArray());
            Assert.AreSame(built.states[0].state, built.defaultState);
        }

        [Test]
        public void ADetachedRun_HoldsItsRecordsForTheScopeOnly_AndOnlyForItsController()
        {
            var controller = Memory("Detached");
            var other = Memory("Other");

            using (var target = GenerationTarget.BeginDetached(controller))
            {
                Assert.AreSame(target, GenerationTarget.Of(controller));
                Assert.AreNotSame(target, GenerationTarget.Of(other));
                Assert.IsFalse(target.RecordsUndo);
                Assert.IsFalse(target.Persisted);

                var held = GraphFrameData.GetOrCreate(controller);
                Assert.AreSame(held, GraphFrameData.Find(controller), "read back within the run");
                Assert.IsNull(GraphFrameData.Find(other));
                ControllerIRBuilder.Rebuild(ControllerIR.Parse(Source()), controller, exclusive: true);
            }

            Assert.IsNull(GraphFrameData.Find(controller), "gone with the scope");
            Assert.IsTrue(GenerationTarget.Of(controller).RecordsUndo, "outside a scope: the editor default");
            Assert.AreEqual(2, controller.layers[0].stateMachine.states.Length);
        }

        [Test]
        public void ADetachedRun_RecordsNoUndo()
        {
            var controller = Memory("No Undo");
            Undo.IncrementCurrentGroup();
            int before = Undo.GetCurrentGroup();
            string beforeName = Undo.GetCurrentGroupName();

            using (GenerationTarget.BeginDetached(controller))
                ControllerIRBuilder.Rebuild(ControllerIR.Parse(Source()), controller, exclusive: true);

            Assert.AreEqual(before, Undo.GetCurrentGroup(), "no Undo group was opened");
            Assert.AreEqual(beforeName, Undo.GetCurrentGroupName());
        }
    }
}

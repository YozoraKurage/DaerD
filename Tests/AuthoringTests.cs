using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Authoring;

namespace Yozolab.DaerD.Tests
{
    /// <summary>Recipe whose Build body is injected per test.</summary>
    class TestRecipe : ControllerRecipe
    {
        public Action<ControllerBuilder> body;
        protected override void Build(ControllerBuilder c) => body?.Invoke(c);
    }

    public class AuthoringTests
    {
        TestRecipe NewRecipe(AnimatorController target, Action<ControllerBuilder> body,
            bool exclusive = false)
        {
            var recipe = ScriptableObject.CreateInstance<TestRecipe>();
            recipe.targetController = target;
            recipe.exclusive = exclusive;
            recipe.body = body;
            _cleanup.Add(recipe);
            return recipe;
        }

        readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        AnimatorController Track(AnimatorController controller)
        {
            _cleanup.Add(controller);
            return controller;
        }

        [Test]
        public void Generate_BuildsParametersStatesTransitionsAndTree()
        {
            var controller = Track(new AnimatorController());
            var clip = new AnimationClip { name = "Idle" };
            _cleanup.Add(clip);

            var recipe = NewRecipe(controller, c =>
            {
                c.Float("Blend", 0.5f).Bool("Go").Int("Step", 2).Trigger("Fire");

                var fx = c.Layer("Hand").Weight(0.8f).Additive().IkPass();
                var idle = fx.State("Idle", clip).At(260f, 60f).Speed(1.5f).Tag("t")
                    .WriteDefaults(false).SpeedBy("Blend");
                var move = fx.State("Move").At(260f, 140f);
                var tree = move.Tree("Move").Blend2D("Blend", "Blend").NormalizedBlendValues();
                tree.Add(clip).Position(0f, 1f).TimeScale(2f);
                var inner = tree.AddTree("Inner").Direct();
                inner.Add(clip).DirectParameter("Blend");
                inner.Slot.Position(1f, 0f);

                idle.To(move).If("Go").IfGreater("Blend", 0.25f).Duration(0.15f);
                move.ToExit().IfNot("Go").ExitTime(0.9f);
                fx.AnyTo(idle).IfIntEquals("Step", 3).CanTransitionToSelf(false);
                fx.EntryTo(move).IfIntNotEquals("Step", 0);
                move.Default();
            });

            var warnings = recipe.Generate();
            Assert.IsEmpty(warnings, string.Join("\n", warnings));

            Assert.AreEqual(AnimatorControllerParameterType.Float,
                DbtBuilder.FindParameter(controller, "Blend").type);
            Assert.AreEqual(0.5f, DbtBuilder.FindParameter(controller, "Blend").defaultFloat);
            Assert.AreEqual(2, DbtBuilder.FindParameter(controller, "Step").defaultInt);

            var layer = controller.layers[controller.layers.Length - 1];
            Assert.AreEqual("Hand", layer.name);
            Assert.AreEqual(0.8f, layer.defaultWeight, 0.0001f);
            Assert.AreEqual(AnimatorLayerBlendingMode.Additive, layer.blendingMode);
            Assert.IsTrue(layer.iKPass);

            var sm = layer.stateMachine;
            Assert.AreEqual(2, sm.states.Length);
            var idleState = FindState(sm, "Idle");
            var moveState = FindState(sm, "Move");
            Assert.AreSame(clip, idleState.motion);
            Assert.AreEqual(1.5f, idleState.speed);
            Assert.IsFalse(idleState.writeDefaultValues);
            Assert.IsTrue(idleState.speedParameterActive);
            Assert.AreEqual("Blend", idleState.speedParameter);
            Assert.AreSame(moveState, sm.defaultState, ".Default() wins over first-state rule");

            var moveTree = moveState.motion as BlendTree;
            Assert.IsNotNull(moveTree);
            Assert.AreEqual(BlendTreeType.FreeformDirectional2D, moveTree.blendType);
            Assert.AreEqual(2, moveTree.children.Length);
            Assert.AreEqual(new Vector2(0f, 1f), moveTree.children[0].position);
            Assert.AreEqual(2f, moveTree.children[0].timeScale);
            var innerTree = moveTree.children[1].motion as BlendTree;
            Assert.IsNotNull(innerTree);
            Assert.AreEqual(BlendTreeType.Direct, innerTree.blendType);
            Assert.AreEqual("Blend", innerTree.children[0].directBlendParameter);
            Assert.AreEqual(new Vector2(1f, 0f), moveTree.children[1].position);

            var toMove = idleState.transitions[0];
            Assert.AreSame(moveState, toMove.destinationState);
            Assert.IsFalse(toMove.hasExitTime);
            Assert.AreEqual(0.15f, toMove.duration);
            Assert.AreEqual(2, toMove.conditions.Length);
            Assert.AreEqual(AnimatorConditionMode.Greater, toMove.conditions[1].mode);

            var exit = moveState.transitions[0];
            Assert.IsTrue(exit.isExit);
            Assert.IsTrue(exit.hasExitTime);
            Assert.AreEqual(0.9f, exit.exitTime);

            Assert.AreEqual(1, sm.anyStateTransitions.Length);
            Assert.IsFalse(sm.anyStateTransitions[0].canTransitionToSelf);
            Assert.AreEqual(AnimatorConditionMode.Equals, sm.anyStateTransitions[0].conditions[0].mode);
            Assert.AreEqual(1, sm.entryTransitions.Length);
            Assert.AreEqual(AnimatorConditionMode.NotEqual, sm.entryTransitions[0].conditions[0].mode);
        }

        [Test]
        public void Generate_PartialKeepsOtherLayers_ExclusiveOwnsEverything()
        {
            var controller = Track(new AnimatorController());
            controller.AddLayer("Existing");
            controller.AddParameter("Old", AnimatorControllerParameterType.Bool);

            var partial = NewRecipe(controller, c => c.Layer("Mine").State("A"));
            partial.Generate();
            Assert.AreEqual(2, controller.layers.Length);
            Assert.AreEqual("Existing", controller.layers[0].name);
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Old"));

            // Regenerating replaces in place, not appends.
            partial.Generate();
            Assert.AreEqual(2, controller.layers.Length);

            var exclusive = NewRecipe(controller, c => c.Layer("Only").State("B"), exclusive: true);
            exclusive.Generate();
            Assert.AreEqual(1, controller.layers.Length);
            Assert.AreEqual("Only", controller.layers[0].name);
            Assert.IsNull(DbtBuilder.FindParameter(controller, "Old"),
                "exclusive recipes own the parameter list");
        }

        [Test]
        public void Generate_SubMachines_CrossMachineTransitions_AndSyncedLayer()
        {
            var controller = Track(new AnimatorController());
            var clip = new AnimationClip { name = "Over" };
            _cleanup.Add(clip);

            var recipe = NewRecipe(controller, c =>
            {
                var main = c.Layer("Main");
                var a = main.State("A");
                var sub = main.AddMachine("Sub").At(500f, 50f);
                var d = sub.State("D");
                d.To(a);                      // cross-machine, upward
                a.To(sub);                    // into the machine
                sub.To(a).IfIntEquals("X", 1); // from the machine node
                sub.ToExit();
                c.Int("X");

                c.SyncedLayer("MainSync", "Main").Weight(0.7f).AffectsTiming()
                    .Override("A", clip);
            }, exclusive: true);

            var warnings = recipe.Generate();
            Assert.IsEmpty(warnings, string.Join("\n", warnings));

            var sm = controller.layers[0].stateMachine;
            var aState = FindState(sm, "A");
            var subSm = sm.stateMachines[0].stateMachine;
            Assert.AreEqual("Sub", subSm.name);
            var dState = FindState(subSm, "D");
            Assert.AreSame(aState, dState.transitions[0].destinationState);
            Assert.AreSame(subSm, aState.transitions[0].destinationStateMachine);
            var fromSub = sm.GetStateMachineTransitions(subSm);
            Assert.AreEqual(2, fromSub.Length);

            var synced = controller.layers[1];
            Assert.AreEqual(0, synced.syncedLayerIndex);
            Assert.IsTrue(synced.syncedLayerAffectsTiming);
            Assert.AreSame(clip, synced.GetOverrideMotion(aState));
        }

        [Test]
        public void Verify_IsClean_AfterGenerate_AndReportsHandEdits()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                c.Bool("Go");
                var fx = c.Layer("Mine");
                var a = fx.State("A").At(100f, 100f);
                var b = fx.State("B").At(100f, 200f);
                a.To(b).If("Go");
            });

            recipe.Generate();
            var clean = recipe.Verify();
            Assert.IsEmpty(clean, string.Join("\n", clean));

            // Hand-edit the generated layer: Verify must call it out.
            var sm = controller.layers[controller.layers.Length - 1].stateMachine;
            FindState(sm, "A").speed = 3f;
            var drift = recipe.Verify();
            Assert.IsTrue(drift.Exists(d => d.Contains("State 'A'") && d.Contains("speed")),
                string.Join("\n", drift));
        }

        [Test]
        public void Verify_IgnoresLayersTheRecipeDoesNotOwn()
        {
            var controller = Track(new AnimatorController());
            controller.AddLayer("Foreign");
            controller.layers[0].stateMachine.AddState("Noise", Vector3.zero);

            var recipe = NewRecipe(controller, c => c.Layer("Mine").State("A"));
            recipe.Generate();
            controller.layers[0].stateMachine.AddState("MoreNoise", Vector3.zero);

            Assert.IsEmpty(recipe.Verify(), "foreign layers are out of scope for a partial recipe");
        }

        [Test]
        public void Raw_RunsAfterDeclaredLayers_AndVerifyMentionsIt()
        {
            var controller = Track(new AnimatorController());
            bool ran = false;
            var recipe = NewRecipe(controller, c =>
            {
                c.Layer("Mine").State("A");
                c.Raw(target =>
                {
                    ran = target.layers.Length > 0;   // declared layer exists already
                    target.AddParameter("FromRaw", AnimatorControllerParameterType.Bool);
                });
            });

            recipe.Generate();
            Assert.IsTrue(ran);
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "FromRaw"));
            Assert.IsTrue(recipe.Verify().Exists(m => m.Contains("Raw")),
                "Verify discloses that Raw steps are not covered");
        }

        [Test]
        public void RoundTrip_GeneratedControllerParsesBackToTheDeclaredIR()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                c.Float("X").Bool("Go");
                var fx = c.Layer("L1");
                var a = fx.State("A").At(10f, 20f);
                var b = fx.State("B").At(10f, 90f);
                var tree = b.Tree("T").Blend1D("X").AutoThresholds(false);
                tree.Add(null).Threshold(0.2f);
                a.To(b).If("Go").Duration(0.1f).Offset(0.05f)
                    .Interruption(TransitionInterruptionSource.Source, ordered: false).Solo();
                fx.AnyTo(b).IfLess("X", 0.5f);
            }, exclusive: true);

            recipe.Generate();

            var builder = recipe.BuildDeclaration();
            builder.Bake();
            builder.IR.layers[0].defaultWeight = 1f;   // parse normalizes the base layer
            var diffs = ControllerIRDiff.Compare(builder.IR, ControllerIR.Parse(controller));
            Assert.IsEmpty(diffs, string.Join("\n", diffs));
        }

        [Test]
        public void AsyncSync_FromARecipe_GeneratesOnce_AndRegeneratesInPlace()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                c.Float("Hue").Int("Outfit").Bool("Tail");
                c.Layer("Base").State("S");
                c.AsyncSync("Zip")
                    .Targets("Hue", "Outfit", "Tail")
                    .Schedule("Hue", "Outfit", "Hue", "Tail")
                    .SkipDriversForTest();
            });

            var warnings = recipe.Generate();
            Assert.IsFalse(warnings.Exists(w => w.Contains("Async Sync 'Zip':")),
                string.Join("\n", warnings));

            int LayerIndex(string name)
            {
                for (int i = 0; i < controller.layers.Length; i++)
                    if (controller.layers[i].name == name) return i;
                return -1;
            }
            Assert.GreaterOrEqual(LayerIndex("Zip"), 0);
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Zip/Index"));
            var zip = controller.layers[LayerIndex("Zip")].stateMachine;
            // Explicit schedule: 4 send steps (Hue twice) + idle + 3 recv.
            Assert.AreEqual(8, zip.states.Length);

            int layersAfterFirst = controller.layers.Length;
            recipe.Generate();
            Assert.AreEqual(layersAfterFirst, controller.layers.Length,
                "regenerating must rebuild the Zip layer in place, not stack another");
        }

        static AnimatorState FindState(AnimatorStateMachine sm, string name)
        {
            foreach (var child in sm.states)
                if (child.state != null && child.state.name == name) return child.state;
            Assert.Fail("state '" + name + "' not found");
            return null;
        }
    }
}

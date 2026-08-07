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
                var blend = c.FloatParameter("Blend", 0.5f);
                var go = c.BoolParameter("Go");
                var step = c.IntParameter("Step", 2);
                c.TriggerParameter("Fire");

                var fx = c.Layer("Hand").WithWeight(0.8f).Additive().WithIkPass();
                var idle = fx.NewState("Idle").WithAnimation(clip).At(260f, 60f)
                    .WithSpeedSetTo(1.5f).WithTag("t").WithWriteDefaultsSetTo(false)
                    .WithSpeed(blend);

                var inner = c.NewBlendTree("Inner").Direct().WithAnimation(clip, blend);
                var tree = c.NewBlendTree("Move").FreeformDirectional2D(blend, blend)
                    .NormalizedBlendValues()
                    .WithAnimation(clip, 0f, 1f);
                tree.LastChild.TimeScale(2f);
                tree.WithAnimation(inner, 1f, 0f);
                var move = fx.NewState("Move").WithAnimation(tree).At(260f, 140f);

                idle.TransitionsTo(move).When(go.IsTrue()).And(blend.IsGreaterThan(0.25f))
                    .WithTransitionDurationSeconds(0.15f);
                move.Exits().When(go.IsFalse()).AfterAnimationIsAtLeastAtNormalized(0.9f);
                fx.AnyTransitionsTo(idle).When(step.IsEqualTo(3));
                fx.EntryTransitionsTo(move).When(step.IsNotEqualTo(0));
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

            var partial = NewRecipe(controller, c => c.Layer("Mine").NewState("A"));
            partial.Generate();
            Assert.AreEqual(2, controller.layers.Length);
            Assert.AreEqual("Existing", controller.layers[0].name);
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Old"));

            // Regenerating replaces in place, not appends.
            partial.Generate();
            Assert.AreEqual(2, controller.layers.Length);

            var exclusive = NewRecipe(controller, c => c.Layer("Only").NewState("B"), exclusive: true);
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
                var x = c.IntParameter("X");
                var main = c.Layer("Main");
                var a = main.NewState("A");
                var sub = main.NewSubStateMachine("Sub").At(500f, 50f);
                var d = sub.NewState("D");
                d.TransitionsTo(a);                      // cross-machine, upward
                a.TransitionsTo(sub);                    // into the machine
                sub.TransitionsTo(a).When(x.IsEqualTo(1)); // from the machine node
                sub.Exits();

                c.SyncedLayer("MainSync", "Main").WithWeight(0.7f).AffectsTiming()
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
                var go = c.BoolParameter("Go");
                var fx = c.Layer("Mine");
                var a = fx.NewState("A").At(100f, 100f);
                var b = fx.NewState("B").At(100f, 200f);
                a.TransitionsTo(b).When(go.IsTrue());
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

            var recipe = NewRecipe(controller, c => c.Layer("Mine").NewState("A"));
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
                c.Layer("Mine").NewState("A");
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
                var x = c.FloatParameter("X");
                var go = c.BoolParameter("Go");
                var fx = c.Layer("L1");
                var a = fx.NewState("A").At(10f, 20f);
                var tree = c.NewBlendTree("T").Simple1D(x).AutoThresholds(false)
                    .WithAnimation((Motion)null, 0.2f);
                var b = fx.NewState("B").WithAnimation(tree).At(10f, 90f);
                a.TransitionsTo(b).When(go.IsTrue()).WithTransitionDurationSeconds(0.1f)
                    .WithOffset(0.05f).WithInterruption(TransitionInterruptionSource.Source)
                    .WithNoOrderedInterruption().Solo();
                fx.AnyTransitionsTo(b).When(x.IsLessThan(0.5f));
            }, exclusive: true);

            recipe.Generate();

            var builder = recipe.BuildDeclaration();
            builder.Bake();
            builder.IR.layers[0].defaultWeight = 1f;   // parse normalizes the base layer
            var diffs = ControllerIRDiff.Compare(builder.IR, ControllerIR.Parse(controller));
            Assert.IsEmpty(diffs, string.Join("\n", diffs));
        }

        [Test]
        public void Drivers_DrivesFamily_WritesTypedEntries()
        {
            // Declaration-level only: the VRC SDK (and so the driver type itself) is not
            // available in the test environment — Generate would skip the behaviours.
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                var n = c.IntParameter("N");
                var x = c.FloatParameter("X");
                var tail = c.BoolParameter("Tail");
                var fx = c.Layer("Mine");
                var s = fx.NewState("S");
                s.Drives(n, 2).Drives(tail, true)
                    .DrivingIncreases(x, 0.25f).DrivingDecreases(n, 1)
                    .DrivingRandomizes(x, 0f, 1f).DrivingRandomizes(tail, 0.5f)
                    .DrivingCopies(x, n).DrivingRemaps(x, 0f, 1f, n, 0f, 7f)
                    .DrivingLocally();
                s.NewDriver("Second").Drives(x, 3);
            });

            var declared = recipe.BuildDeclaration();
            declared.Bake();
            var state = declared.IR.layers[0].machine.states[0];
            Assert.AreEqual(2, state.behaviours.Count, "one driver plus the named second one");

            var first = state.behaviours[0].driver;
            Assert.IsTrue(first.localOnly);
            Assert.AreEqual(8, first.entries.Count);
            Assert.AreEqual(0, first.entries[0].kind);
            Assert.AreEqual(2f, first.entries[0].value);
            Assert.AreEqual(1f, first.entries[1].value, 0.0001f, "Drives(bool, true) writes 1");
            Assert.AreEqual(0.25f, first.entries[2].value);
            Assert.AreEqual(-1f, first.entries[3].value, 0.0001f, "DrivingDecreases negates");
            Assert.AreEqual(2, first.entries[4].kind);
            Assert.AreEqual(0.5f, first.entries[5].chance);
            Assert.AreEqual("X", first.entries[6].source);
            Assert.IsTrue(first.entries[7].convertRange);
            Assert.AreEqual(7f, first.entries[7].destMax);

            Assert.AreEqual("Second", state.behaviours[1].instanceName);
            Assert.AreEqual(3f, state.behaviours[1].driver.entries[0].value);
        }

        [Test]
        public void AsyncSync_FromARecipe_GeneratesOnce_AndRegeneratesInPlace()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                c.FloatParameter("Hue");
                c.IntParameter("Outfit");
                c.BoolParameter("Tail");
                c.Layer("Base").NewState("S");
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

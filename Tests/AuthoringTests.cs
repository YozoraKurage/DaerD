using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Authoring;

namespace Yozolab.DaerD.Tests
{
    /// <summary>Recipe whose halves are injected per test: <see cref="body"/> is the hand
    /// half that runs, <see cref="generated"/> stands in for the exported one.</summary>
    class TestRecipe : ControllerRecipe
    {
        public Action<ControllerBuilder> body;
        public Action<ControllerBuilder> generated;

        protected override void Build(ControllerBuilder c) => body?.Invoke(c);

        protected override void BuildGenerated(ControllerBuilder c)
        {
            if (generated != null) generated(c);
            else base.BuildGenerated(c);
        }
    }

    /// <summary>A hand-written recipe — no exported half to compare against.</summary>
    class PlainTestRecipe : ControllerRecipe
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
            // Auto encoding on 3 slots resolves to the 2-bit Bool index, not the flat Int one.
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Zip/Index/b0"));
            var zip = controller.layers[LayerIndex("Zip")].stateMachine;
            // Explicit schedule: 4 send steps (Hue twice) + idle + 3 recv.
            Assert.AreEqual(8, zip.states.Length);

            int layersAfterFirst = controller.layers.Length;
            recipe.Generate();
            Assert.AreEqual(layersAfterFirst, controller.layers.Length,
                "regenerating must rebuild the Zip layer in place, not stack another");
        }

        [Test]
        public void AsyncSync_Requestable_BuildsTheRequestRoutes()
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
                    .Requestable("Hue")
                    .SkipDriversForTest();
            });

            var warnings = recipe.Generate();
            Assert.IsFalse(warnings.Exists(w => w.Contains("Async Sync 'Zip':")),
                string.Join("\n", warnings));

            var flag = DbtBuilder.FindParameter(controller, "Zip/Req/Hue");
            Assert.IsNotNull(flag);
            Assert.AreEqual(AnimatorControllerParameterType.Bool, flag.type);

            AnimatorStateMachine zip = null;
            foreach (var layer in controller.layers)
                if (layer.name == "Zip")
                    zip = layer.stateMachine;
            Assert.IsNotNull(zip);

            // From a step the origins rule allows, the flag sends the cycle on a detour ahead
            // of the ring transition. Send Tail is not one: it is followed by Send Hue, and a
            // detour returning there would repeat the index it had just written.
            var sendOutfit = FindState(zip, "Send Outfit");
            Assert.AreEqual(2, sendOutfit.transitions.Length);
            Assert.AreEqual("Send Hue (req)", sendOutfit.transitions[0].destinationState.name);
            bool conditioned = false;
            foreach (var condition in sendOutfit.transitions[0].conditions)
                if (condition.parameter == "Zip/Req/Hue"
                    && condition.mode == AnimatorConditionMode.If)
                    conditioned = true;
            Assert.IsTrue(conditioned);
        }

        [Test]
        public void AsyncSync_Ready_BuildsTheWatcherAndRegeneratesItInPlace()
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
                    .Ready()
                    .SkipDriversForTest();
            });

            var warnings = recipe.Generate();
            Assert.IsFalse(warnings.Exists(w => w.Contains("Async Sync 'Zip':")),
                string.Join("\n", warnings));

            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Zip/Ready"));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Zip/Seen/Hue"));

            AnimatorStateMachine watcher = null;
            foreach (var layer in controller.layers)
                if (layer.name == "Zip Ready")
                    watcher = layer.stateMachine;
            Assert.IsNotNull(watcher);
            Assert.AreEqual(3, watcher.states.Length);

            // The watcher belongs to the same call, so a second Generate rebuilds it rather
            // than adding another one beside it.
            int layersAfterFirst = controller.layers.Length;
            recipe.Generate();
            Assert.AreEqual(layersAfterFirst, controller.layers.Length);
        }

        [Test]
        public void AsyncSync_AllowRepeats_LetsOneSlotHoldTwoStepsRunning()
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
                    .AllowRepeats()
                    .Schedule("Hue", "Hue", "Outfit", "Tail")
                    .SkipDriversForTest();
            });

            var warnings = recipe.Generate();
            Assert.IsFalse(warnings.Exists(w => w.Contains("Async Sync 'Zip':")),
                string.Join("\n", warnings));

            AnimatorStateMachine zip = null;
            foreach (var layer in controller.layers)
                if (layer.name == "Zip")
                    zip = layer.stateMachine;
            Assert.IsNotNull(zip);
            // 4 send steps + idle + 3 recv, plus the second Recv the repeated slot decodes in.
            Assert.AreEqual(9, zip.states.Length);
            Assert.IsNotNull(FindState(zip, "Recv Hue (2)"));
        }

        [Test]
        public void AsyncSync_Unnamed_KeepsTheLegacyBaseName_WithoutAnAssetGuid()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                c.FloatParameter("Hue");
                c.IntParameter("Outfit");
                c.BoolParameter("Tail");
                c.Layer("Base").NewState("S");
                c.AsyncSync()
                    .Targets("Hue", "Outfit", "Tail")
                    .SkipDriversForTest();
            });

            var warnings = recipe.Generate();
            Assert.IsFalse(warnings.Exists(w => w.Contains("Async Sync 'Async':")),
                string.Join("\n", warnings));

            // An in-memory controller has no GUID to derive a per-controller name from, so the
            // default is still the historical "Async" — the layer and the channels prove it.
            int layers = controller.layers.Length;
            bool named = false;
            foreach (var layer in controller.layers)
                if (layer.name == "Async") named = true;
            Assert.IsTrue(named, "the unnamed setup names its layer after the base name");
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Async/Float"));

            // And the name has to be resolved the same way on the next run, or the second
            // Generate would build a second cycle beside the first.
            recipe.Generate();
            Assert.AreEqual(layers, controller.layers.Length,
                "regenerating must rebuild the same layer in place, not stack another");
        }

        [Test]
        public void Gadgets_FromARecipe_CollectIntoOneLayer_AndRegenerateInPlace()
        {
            var controller = Track(new AnimatorController());
            // As an older version would have left it: the reciprocal's half used to be a
            // motion-time layer, and a controller in the wild still carries one. The sweep only
            // knows it by the name AapGadgets.SupportingLayerNames still reports.
            controller.AddLayer("Y/Inverse 1/x");
            var recipe = NewRecipe(controller, c =>
            {
                var x = c.FloatParameter("X");
                var y = c.FloatParameter("Y");
                // Declared because the rest of the recipe reads it back; the gadget still
                // owns the name, and the layer's rebuild recreates it.
                var product = c.FloatParameter("X*Y");
                var fx = c.Layer("Base");
                var idle = fx.NewState("Idle");
                var lit = fx.NewState("Lit");
                idle.TransitionsTo(lit).When(product.IsGreaterThan(0.5f));

                c.Gadgets("Math")
                    .Multiply(x, y, product)        // a handle …
                    .Buffer(x, "X/Late", 2)         // … or a bare name
                    .Reciprocal(y, "Y/Inverse");
            });

            var warnings = recipe.Generate();
            Assert.IsEmpty(warnings, string.Join("\n", warnings));

            int math = IndexOfLayer(controller, "Math");
            Assert.GreaterOrEqual(math, 0);
            // The reciprocal computes both halves inside the tree now, so the layer the older
            // version left behind is reclaimed rather than joined by a numbered twin.
            Assert.AreEqual(-1, IndexOfLayer(controller, "Y/Inverse 1/x"));
            Assert.AreEqual(-1, IndexOfLayer(controller, "Y/Inverse 1/x 1"));

            var root = (BlendTree)controller.layers[math].stateMachine.states[0].state.motion;
            Assert.AreEqual(BlendTreeType.Direct, root.blendType);
            Assert.AreEqual(3, root.children.Length, "one child per gadget, all in one layer");
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "X*Y"));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "X/Late/1"), "the buffer's stage");
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Y/Inverse/Shift"));
            Assert.IsTrue(new List<string>(recipe.OwnedLayers).Contains("Math"),
                "a generated gadget layer belongs to the recipe like a declared one");

            // Repeatability is the point of the sweep: gadgets refuse to write an output that
            // already exists, and the layers would otherwise stack a copy per Generate.
            int layers = controller.layers.Length;
            var second = recipe.Generate();
            Assert.IsEmpty(second, string.Join("\n", second));
            Assert.AreEqual(layers, controller.layers.Length);
            root = (BlendTree)controller.layers[IndexOfLayer(controller, "Math")]
                .stateMachine.states[0].state.motion;
            Assert.AreEqual(3, root.children.Length, "…and the layer's contents don't stack either");
        }

        [Test]
        public void Gadgets_ReportAFailedRequest_AndRunTheRestOfTheLayer()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                var x = c.FloatParameter("X");
                c.BoolParameter("Flag");
                c.Layer("Base").NewState("S");
                c.Gadgets("Math")
                    .Not("Flag", "Flag/Not")                  // Bool input: gadgets read Floats
                    .Remap(x, "X01", -1f, 1f, 0f, 1f);
            });

            var warnings = recipe.Generate();
            Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
            Assert.IsTrue(warnings[0].Contains("Flag/Not") && warnings[0].Contains("Math"),
                warnings[0]);

            Assert.IsNull(DbtBuilder.FindParameter(controller, "Flag/Not"));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "X01"),
                "one bad request must not take the whole layer down with it");
        }

        /// <summary>
        /// The frame count a recipe can read while it is being written. Every gadget's cost is
        /// fixed and they add along a chain, so the builder can carry a running age for each
        /// parameter it produces — and a recipe that wants to line two branches up can ask for
        /// the numbers instead of counting hops by hand.
        /// </summary>
        [Test]
        public void Gadgets_ReportHowManyFramesBehindEachParameterIs()
        {
            var controller = Track(new AnimatorController());
            GadgetRecipeBuilder math = null;
            var recipe = NewRecipe(controller, c =>
            {
                var x = c.FloatParameter("X");
                var y = c.FloatParameter("Y");
                c.Layer("Base").NewState("S");
                math = c.Gadgets("Math")
                    .Not(x, "X/Not")                 // one frame
                    .Not("X/Not", "X/Back")          // two
                    .Divide(y, y, "Y/Q")             // three
                    .Buffer(x, "X/Late", 3)          // three, one per stage
                    .SeparateDigits(x, "X/Digits");  // five
            });
            recipe.Generate();

            Assert.AreEqual(0, math.FramesBehind("X"), "an input the chain did not produce");
            Assert.AreEqual(1, math.FramesBehind("X/Not"));
            Assert.AreEqual(2, math.FramesBehind("X/Back"), "latencies add along a chain");
            Assert.AreEqual(3, math.FramesBehind("Y/Q"), "a divide costs three on its own");

            Assert.AreEqual(1, math.FramesBehind("X/Late/1"), "a buffer's stages are readable");
            Assert.AreEqual(2, math.FramesBehind("X/Late/2"), "one frame apart");
            Assert.AreEqual(3, math.FramesBehind("X/Late"));

            Assert.AreEqual(5, math.FramesBehind("X/Digits/Tenths"), "all three digits together");
            Assert.AreEqual(5, math.FramesBehind("X/Digits/Thousandths"));

            Assert.AreEqual(0, math.FramesBehind("Nothing/Named/This"), "an unknown name");
        }

        /// <summary>
        /// Two branches off one input, reaching the same gadget at different ages: it is being
        /// handed two different frames of X, and no arithmetic fixes that after the fact. The
        /// builder says so, names both ages, and spells out the buffer that closes the gap.
        /// </summary>
        [Test]
        public void Gadgets_ReportAGadgetReadingTwoDifferentFramesOfItsInputs()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                var x = c.FloatParameter("X");
                c.Layer("Base").NewState("S");
                c.Gadgets("Math")
                    .Not(x, "X/Not")                    // one frame behind X
                    .Add("X/Not", x, "X/Sum");          // …added to X itself, which is current
            });

            var warnings = recipe.Generate();
            Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
            Assert.IsTrue(warnings[0].Contains("X/Sum"), warnings[0]);
            Assert.IsTrue(warnings[0].Contains("Buffer(\"X\""), "it should name the fix: " + warnings[0]);

            // Reported, but still built: a difference in age is sometimes what the author meant.
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "X/Sum"));
        }

        /// <summary>Buffering the newer branch is the fix, and the builder then has nothing to
        /// say — which is the only way to tell a recipe that the alignment is right rather than
        /// merely unreported.</summary>
        [Test]
        public void Gadgets_SayNothingOnceTheShallowBranchIsBuffered()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                var x = c.FloatParameter("X");
                c.Layer("Base").NewState("S");
                c.Gadgets("Math")
                    .Not(x, "X/Not")
                    .Buffer(x, "X/Aligned", 1)
                    .Add("X/Not", "X/Aligned", "X/Sum");
            });

            Assert.IsEmpty(recipe.Generate());
        }

        /// <summary>Opting in to the strict reading: where the two inputs really are the same
        /// signal down two paths, generating the gadget anyway is generating something that is
        /// wrong every frame the input moves.</summary>
        [Test]
        public void Gadgets_RequireAligned_RefusesTheMisalignedGadgetAndKeepsTheRest()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                var x = c.FloatParameter("X");
                c.Layer("Base").NewState("S");
                c.Gadgets("Math")
                    .RequireAligned()
                    .Not(x, "X/Not")
                    .Add("X/Not", x, "X/Sum")
                    .Remap(x, "X01", -1f, 1f, 0f, 1f);
            });

            var warnings = recipe.Generate();
            Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
            Assert.IsNull(DbtBuilder.FindParameter(controller, "X/Sum"), "refused");
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "X/Not"), "and the rest still ran");
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "X01"));
        }

        /// <summary>
        /// Two inputs of different ages that did not come from the same place are not a
        /// misalignment. A scaled reading two frames deep multiplied by an unrelated parameter
        /// is two signals, not one signal down two paths, and there is no frame of anything to
        /// line up — reporting it would bury the case that matters.
        /// </summary>
        [Test]
        public void Gadgets_DoNotReportInputsThatCameFromDifferentPlaces()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                var raw = c.FloatParameter("Raw");
                var other = c.FloatParameter("Other");
                c.Layer("Base").NewState("S");
                c.Gadgets("Math")
                    .RequireAligned()
                    .Not(raw, "Raw/Not")            // one frame behind Raw
                    .Not("Raw/Not", "Raw/Back")     // two
                    .Multiply("Raw/Back", other, "Mixed");   // …times something unrelated
            });

            Assert.IsEmpty(recipe.Generate());
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Mixed"));
        }

        /// <summary>The canonical frame-rate independent chain, which has to stay quiet on both
        /// counts: the rate is unrelated to the clock, and the step size is a coefficient the
        /// smoothing is tuned by rather than a sample of what it is filtering.</summary>
        [Test]
        public void Gadgets_AcceptTheFrameRateIndependentSmoothing()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                var target = c.FloatParameter("Target");
                var rate = c.FloatParameter("Rate");
                c.Layer("Base").NewState("S");
                c.Gadgets("Math")
                    .RequireAligned()
                    .FrameTime("Dt")
                    .Multiply("Dt", rate, "Step")
                    .SmoothLinear(target, "Tracked", "Step", 0.05f, -1f, 1f);
            });

            Assert.IsEmpty(recipe.Generate());
        }

        /// <summary>
        /// A chain that feeds back into itself says so, because the frame counts stop describing
        /// it the moment it does. The ages are computed by walking the chain once from its
        /// inputs; a gadget reading what a later gadget writes closes a loop, and a parameter in
        /// a loop holds a little of every past frame rather than information of one age.
        ///
        /// It is a note and not a refusal — an integrator or an iteration is a loop on purpose.
        /// </summary>
        [Test]
        public void Gadgets_SayWhenTheChainFeedsBackIntoItself()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                var x = c.FloatParameter("X");
                c.FloatParameter("Loop");
                c.Layer("Base").NewState("S");
                // Loop is read here and written two lines down: an integrator, in miniature.
                c.Gadgets("Math")
                    .Add(x, "Loop", "Loop/Next")
                    .Remap("Loop/Next", "Loop", 0f, 2f, 0f, 2f);
            });

            var warnings = recipe.Generate();
            Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
            Assert.IsTrue(warnings[0].Contains("feeds back"), warnings[0]);
            Assert.IsTrue(warnings[0].Contains("Loop"), warnings[0]);

            // Built regardless — the loop is the point, and it needs the parameter it reads to
            // exist before the gadget that writes it has run.
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Loop/Next"));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Loop"));
            var root = (BlendTree)controller.layers[IndexOfLayer(controller, "Math")]
                .stateMachine.states[0].state.motion;
            Assert.AreEqual(2, root.children.Length, "both gadgets are in the layer");
        }

        /// <summary>
        /// And it runs: an integrator built as a loop through the gadget chain adds its input to
        /// itself once a frame. Two gadgets, so one round of the loop takes two frames — which
        /// is the whole reason the frame arithmetic bows out here and a measurement takes over.
        /// </summary>
        [Test]
        public void Gadgets_AChainThatFeedsBackActuallyRuns()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                var step = c.FloatParameter("Step");
                c.FloatParameter("Total");
                c.Layer("Base").NewState("S");
                c.Gadgets("Math")
                    .Add(step, "Total", "Total/Next")
                    .Remap("Total/Next", "Total", 0f, 100f, 0f, 100f);
            });
            recipe.Generate();

            using (var rig = new AnimatorRig(controller))
            {
                rig.Set("Step", 1f);
                // Two gadgets in the ring, so the total goes up by one every two frames.
                rig.Step(20);
                float after20 = rig.Get("Total");
                Assert.Greater(after20, 5f, "it is accumulating");
                Assert.Less(after20, 15f, "at about half a step a frame");

                rig.Step(20);
                Assert.AreEqual(after20 * 2f, rig.Get("Total"), 2f, "and steadily");
            }
        }

        /// <summary>One note per chain, not one per gadget in the loop: the useful fact is that
        /// the arithmetic has stopped applying, and repeating it for every edge would bury the
        /// misalignment reports that are still worth reading.</summary>
        [Test]
        public void Gadgets_SayItOnceHoweverManyWaysTheChainLoops()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                var x = c.FloatParameter("X");
                c.FloatParameter("P");
                c.FloatParameter("Q");
                c.Layer("Base").NewState("S");
                c.Gadgets("Math")
                    .Add(x, "P", "A1")
                    .Add(x, "Q", "A2")
                    .Remap("A1", "P", 0f, 2f, 0f, 2f)
                    .Remap("A2", "Q", 0f, 2f, 0f, 2f);
            });

            var warnings = recipe.Generate();
            Assert.AreEqual(1, warnings.Count, string.Join("\n", warnings));
        }

        /// <summary>A one-way chain says nothing, which is what makes the note above worth
        /// reading when it does appear.</summary>
        [Test]
        public void Gadgets_SayNothingAboutAChainThatOnlyFlowsForward()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                var x = c.FloatParameter("X");
                c.Layer("Base").NewState("S");
                c.Gadgets("Math")
                    .Not(x, "X/Not")
                    .Not("X/Not", "X/Back")
                    .ReciprocalRanged("X/Back", "X/Inv", 0.01f, 1f);
            });

            Assert.IsEmpty(recipe.Generate());
        }

        /// <summary>A behaviour can sit on a state machine as well as on a state, and the
        /// recipe API has to be able to say so — otherwise regenerating a layer silently
        /// strips whatever the controller had there.</summary>
        [Test]
        public void MachineBehaviours_AreDeclarableOnALayerRootAndOnASubMachine()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                var layer = c.Layer("L");
                layer.BehaviourJson("IRTestBehaviour", Snapshot<IRTestBehaviour>(b => b.payload = "root"));
                layer.NewState("S");
                var sub = layer.NewSubStateMachine("Sub");
                sub.BehaviourJson("IRTestBehaviour", Snapshot<IRTestBehaviour>(b => b.number = 7));
                sub.NewState("Inner");
            });

            var warnings = recipe.Generate();
            Assert.IsEmpty(warnings, string.Join("\n", warnings));

            var machine = controller.layers[IndexOfLayer(controller, "L")].stateMachine;
            Assert.AreEqual(1, machine.behaviours.Length);
            Assert.AreEqual("root", ((IRTestBehaviour)machine.behaviours[0]).payload);
            Assert.AreEqual(7,
                ((IRTestBehaviour)machine.stateMachines[0].stateMachine.behaviours[0]).number);
        }

        /// <summary>Reshaping the hand half is the point of the split, so the check has to
        /// pass on code that reads nothing like the export and fail on code that builds
        /// something else — it compares what the halves declare, not how they read.</summary>
        [Test]
        public void Compare_PassesOnAReshapedHalf_AndCatchesRealDrift()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, c =>
            {
                // The hand half, reshaped into a loop.
                c.FloatParameter("X");
                var layer = c.Layer("L");
                for (int i = 1; i <= 3; i++)
                    layer.NewState("S" + i).At(0f, i * 100f);
            });
            recipe.generated = c =>
            {
                // What the exporter would have written: every state spelled out.
                c.FloatParameter("X");
                var layer = c.Layer("L");
                var s1 = layer.NewState("S1");
                var s2 = layer.NewState("S2");
                var s3 = layer.NewState("S3");
                s1.At(0f, 100f);
                s2.At(0f, 200f);
                s3.At(0f, 300f);
            };

            var clean = recipe.Compare();
            Assert.IsEmpty(clean, string.Join("\n", clean));

            // A half that declares something else does not slip through.
            recipe.body = c =>
            {
                c.FloatParameter("X");
                var layer = c.Layer("L");
                for (int i = 1; i <= 3; i++)
                    layer.NewState(i == 2 ? "Oops" : "S" + i).At(0f, i * 100f);
            };
            Assert.IsNotEmpty(recipe.Compare());
        }

        [Test]
        public void Compare_SaysSoWhenTheRecipeHasNoExportedHalf()
        {
            var recipe = ScriptableObject.CreateInstance<PlainTestRecipe>();
            _cleanup.Add(recipe);
            recipe.body = c => c.Layer("L").NewState("S");

            Assert.IsFalse(recipe.HasGeneratedHalf);
            Assert.AreEqual(1, recipe.Compare().Count);
        }

        /// <summary>
        /// The JSON a BehaviourJson call takes is an EditorJsonUtility snapshot, the same
        /// thing the exporter writes — not a bare field bag. FromJsonOverwrite reads the
        /// type-wrapped shape and silently does nothing with anything else, so hand-writing
        /// `{"payload":"root"}` here would leave the behaviour at its defaults and the test
        /// would be asserting against a rebuild that never happened.
        /// </summary>
        static string Snapshot<T>(Action<T> configure) where T : StateMachineBehaviour
        {
            var template = ScriptableObject.CreateInstance<T>();
            configure(template);
            var json = UnityEditor.EditorJsonUtility.ToJson(template);
            UnityEngine.Object.DestroyImmediate(template);
            return json;
        }

        static int IndexOfLayer(AnimatorController controller, string name)
        {
            for (int i = 0; i < controller.layers.Length; i++)
                if (controller.layers[i].name == name) return i;
            return -1;
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

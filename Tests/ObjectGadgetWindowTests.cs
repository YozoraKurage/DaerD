using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The editing surfaces of the object gadget family as the model sees them: a saved record
    /// goes into the window through <see cref="ObjectGadgetWindow.LoadConfig"/> and a record
    /// comes back out of <see cref="ObjectGadgetWindow.BuildConfig"/>, and what happens in
    /// between must not lose anything the form has no control for. Only that path is exercised —
    /// the drawing needs an IMGUI event loop — but it is the path a Regenerate runs through,
    /// which is where a dropped binding turns into a rebuilt layer that animates one thing less.
    ///
    /// No prefab and no Modular Avatar: moving a record through a form is the same work wherever
    /// the targets live, and what makes a record legal is <c>ObjectGadgets.Validate</c>'s
    /// business, tested against a real prefab elsewhere.
    /// </summary>
    public class ObjectGadgetWindowTests
    {
        static AnimatorController NewController()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            return controller;
        }

        static GraphFrameData.ObjectGadgetConfig Config(params GameObject[] targets)
        {
            var config = new GraphFrameData.ObjectGadgetConfig
            {
                kind = (int)ObjectGadgets.Kind.Toggle,
                name = "Hat",
                parameter = "Hat/Shown",
                mode = (int)ToggleBuilder.Mode.Layer,
                defaultOn = true,
                declare = false,
            };
            foreach (var target in targets)
                config.targets.Add(new GraphFrameData.ObjectTargetRecord { target = target });
            return config;
        }

        /// <summary>The round trip, with the window created but never shown.</summary>
        static GraphFrameData.ObjectGadgetConfig Reopened(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config)
        {
            var window = ScriptableObject.CreateInstance<ObjectGadgetWindow>();
            try
            {
                window.Bind(controller, null);
                window.LoadConfig(config);
                return window.BuildConfig();
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void EditingAGadgetGivesBackEverythingItWasLoadedWith()
        {
            var controller = NewController();
            var hat = new GameObject("Hat");
            var cape = new GameObject("Cape");
            var config = Config(hat, cape);
            config.targets[1].activeWhenOn = false;
            config.targets[1].toggleActive = false;
            config.targets[1].bindings.Add(new GraphFrameData.BindingRecord
            {
                typeName = "Light",
                property = "m_Enabled",
            });
            config.targets[1].bindings.Add(new GraphFrameData.BindingRecord
            {
                typeName = "SkinnedMeshRenderer",
                property = "blendShape.Smile",
                offValue = 10f,
                onValue = 90f,
            });

            var again = Reopened(controller, config);

            Assert.AreEqual("Hat", again.name);
            Assert.AreEqual("Hat/Shown", again.parameter);
            Assert.AreEqual((int)ToggleBuilder.Mode.Layer, again.mode);
            Assert.IsTrue(again.defaultOn);
            Assert.IsFalse(again.declare);
            Assert.AreEqual(2, again.targets.Count);
            Assert.AreSame(hat, again.targets[0].target);
            Assert.AreSame(cape, again.targets[1].target);
            Assert.IsFalse(again.targets[1].activeWhenOn);
            Assert.IsFalse(again.targets[1].toggleActive);
            Assert.AreEqual(2, again.targets[1].bindings.Count);
            Assert.AreEqual("blendShape.Smile", again.targets[1].bindings[1].property);
            Assert.AreEqual(10f, again.targets[1].bindings[1].offValue);
            Assert.AreEqual(90f, again.targets[1].bindings[1].onValue,
                "the values a blendshape row was given are the record's, not the form's defaults");

            Object.DestroyImmediate(hat);
            Object.DestroyImmediate(cape);
            Object.DestroyImmediate(controller);
        }

        /// <summary>
        /// The case the form is shaped for: a binding it draws no button for — a PhysBone in a
        /// project without the SDK, a component a later version of DaerD knows about — is carried
        /// through an edit untouched. A form built out of checkboxes would write back only what
        /// it had boxes for, and the gadget would quietly animate one thing less.
        /// </summary>
        [Test]
        public void ABindingTheFormHasNoButtonForSurvivesAnEdit()
        {
            var controller = NewController();
            var hat = new GameObject("Hat");
            var config = Config(hat);
            config.targets[0].bindings.Add(new GraphFrameData.BindingRecord
            {
                typeName = "SomeComponentThisProjectDoesNotHave",
                property = "m_Enabled",
            });

            var again = Reopened(controller, config);

            Assert.AreEqual(1, again.targets[0].bindings.Count);
            Assert.AreEqual("SomeComponentThisProjectDoesNotHave",
                again.targets[0].bindings[0].typeName);

            Object.DestroyImmediate(hat);
            Object.DestroyImmediate(controller);
        }

        /// <summary>A target whose object is gone comes back as a row of its own rather than
        /// being dropped: a regenerate that quietly forgets an object is the one outcome a
        /// missing reference must not have.</summary>
        [Test]
        public void ATargetWhoseObjectIsGoneIsKeptAsAnEmptyRow()
        {
            var controller = NewController();
            var config = Config();
            config.targets.Add(new GraphFrameData.ObjectTargetRecord { target = null });

            var again = Reopened(controller, config);

            Assert.AreEqual(1, again.targets.Count);
            Assert.IsNull(again.targets[0].target);

            Object.DestroyImmediate(controller);
        }

        /// <summary>The tree wiring's layer choice is a machine reference, and re-opening a
        /// gadget has to come back pointing at the layer it is actually in — otherwise applying
        /// would move it to a new one.</summary>
        [Test]
        public void TheChosenHostLayerComesBackAsTheSameMachine()
        {
            var controller = NewController();
            controller.AddLayer("DBT");
            var host = controller.layers[1].stateMachine;
            var hat = new GameObject("Hat");
            var config = Config(hat);
            config.mode = (int)ToggleBuilder.Mode.DirectBlendTree;
            config.layer = host;

            var again = Reopened(controller, config);

            Assert.AreEqual((int)ToggleBuilder.Mode.DirectBlendTree, again.mode);
            Assert.AreSame(host, again.layer);

            Object.DestroyImmediate(hat);
            Object.DestroyImmediate(controller);
        }

        /// <summary>A Bool toggle is a layer of its own, so there is nothing to choose and
        /// nothing to carry: the builder fills the record in with the layer it added.</summary>
        [Test]
        public void ALayerWiredGadgetChoosesNoHost()
        {
            var controller = NewController();
            controller.AddLayer("DBT");
            var hat = new GameObject("Hat");

            var again = Reopened(controller, Config(hat));

            Assert.IsNull(again.layer);

            Object.DestroyImmediate(hat);
            Object.DestroyImmediate(controller);
        }

        // ---- the delete preview -----------------------------------------------

        /// <summary>
        /// Deleting says what goes with it, and the sentence is read off the record — the same
        /// references the sweep removes. Only what it enumerates is pinned here, not how it is
        /// worded: the wording is translated, the list is a claim about behaviour.
        /// </summary>
        [Test]
        public void TheDeletePreviewNamesTheLayerTheClipsAndACreatedParameter()
        {
            var controller = NewController();
            controller.AddLayer("Hat");
            var config = Config();
            config.layer = controller.layers[1].stateMachine;
            config.createdParameter = true;
            config.onClip = new GraphFrameData.ClipOutput { clip = new AnimationClip() };
            config.offClip = new GraphFrameData.ClipOutput { clip = new AnimationClip() };

            string loss = HomePanel.ObjectGadgetLoss(controller, config);

            StringAssert.Contains("Hat", loss, "the layer it added, by the name it has now");
            StringAssert.Contains("2", loss, "both generated clips");
            StringAssert.Contains("Hat/Shown", loss, "and the parameter it created");

            Object.DestroyImmediate(config.onClip.clip);
            Object.DestroyImmediate(config.offClip.clip);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void TheDeletePreviewLeavesOutAParameterTheGadgetOnlyBorrowed()
        {
            var controller = NewController();
            controller.AddLayer("Hat");
            var config = Config();
            config.layer = controller.layers[1].stateMachine;
            config.createdParameter = false;

            string loss = HomePanel.ObjectGadgetLoss(controller, config);

            StringAssert.DoesNotContain("Hat/Shown", loss,
                "a parameter the gadget found rather than made is not removed, so promising it "
                + "would be a lie in the one direction that matters");

            Object.DestroyImmediate(controller);
        }

        /// <summary>A clip the user supplied loses this gadget's rows and keeps its file, so it
        /// is named apart from the generated ones instead of being counted with them. Both other
        /// answers are wrong in their own way: counted in, the dialog promises to delete
        /// somebody's asset; left out, it says nothing about a clip that is about to change.
        /// </summary>
        [Test]
        public void TheDeletePreviewDoesNotPromiseToDeleteAUsersOwnClip()
        {
            var controller = NewController();
            controller.AddLayer("Hat");
            var config = Config();
            config.layer = controller.layers[1].stateMachine;
            config.onClip = new GraphFrameData.ClipOutput
            {
                clip = new AnimationClip(),
                userProvided = true,
            };
            config.offClip = new GraphFrameData.ClipOutput { clip = new AnimationClip() };

            string loss = HomePanel.ObjectGadgetLoss(controller, config);

            StringAssert.Contains("1 generated clip", loss, "one generated clip, not two");
            StringAssert.Contains("you supplied", loss,
                "and the other one is named as rows leaving a file that stays");

            Object.DestroyImmediate(config.onClip.clip);
            Object.DestroyImmediate(config.offClip.clip);
            Object.DestroyImmediate(controller);
        }

        // ---- the clip slots ----------------------------------------------------

        /// <summary>A supplied clip is part of the record, so it has to survive the form the
        /// same way a binding does — and it has to come back marked as the user's, which is the
        /// flag that decides whether sweeping the gadget deletes an asset.</summary>
        [Test]
        public void ASuppliedClipComesBackThroughTheForm()
        {
            var controller = NewController();
            var hat = new GameObject("Hat");
            var config = Config(hat);
            var supplied = new AnimationClip();
            config.onClip = new GraphFrameData.ClipOutput
            {
                clip = supplied,
                userProvided = true,
                written = { new GraphFrameData.WrittenRow { path = "Hat", typeName = "GameObject", property = "m_IsActive" } },
            };

            var again = Reopened(controller, config);

            Assert.AreSame(supplied, again.onClip.clip);
            Assert.IsTrue(again.onClip.userProvided);
            Assert.IsEmpty(again.onClip.written,
                "what was written last time is the saved record's claim, not the form's");

            Object.DestroyImmediate(supplied);
            Object.DestroyImmediate(hat);
            Object.DestroyImmediate(controller);
        }

        /// <summary>A generated clip must NOT come back in the slot. Showing it there would
        /// invite somebody to leave it in place, and a clip DaerD minted that is then marked as
        /// the user's is a clip nothing ever sweeps — one leaked per regenerate.</summary>
        [Test]
        public void AGeneratedClipIsNotOfferedBackAsIfItWereTheUsers()
        {
            var controller = NewController();
            var hat = new GameObject("Hat");
            var config = Config(hat);
            var generated = new AnimationClip();
            config.onClip = new GraphFrameData.ClipOutput { clip = generated };

            var again = Reopened(controller, config);

            Assert.IsNull(again.onClip.clip);
            Assert.IsFalse(again.onClip.userProvided);

            Object.DestroyImmediate(generated);
            Object.DestroyImmediate(hat);
            Object.DestroyImmediate(controller);
        }
    }
}

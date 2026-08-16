using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Yozolab.DaerD.DynamicAnalyze;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// What DD DynamicAnalyze knows about the two tools an avatar is really worn with, and how
    /// it behaves in a project that has neither.
    ///
    /// <para>Both halves are the point.</para> The Rec mood reads through Unity's Playable API
    /// and names no tool (<see cref="DynamicAnalyzeRecTests"/> covers that side and installs
    /// nothing); this file covers the thin layer above it that does name them — which avatar a
    /// person is wearing, which copies are Av3Emulator's own rendering doubles, and which are
    /// other people's views of the same avatar. None of that is in a PlayableGraph, so the tool
    /// is asked, by type, behind a versionDefine each.
    ///
    /// Every test here runs in both projects. With the tools installed it asserts; without them
    /// it is skipped by name, so the count of tests does not change between the two runs and a
    /// test that vanished cannot be mistaken for one that passed. The absent case is not left
    /// untested either — <see cref="TheDefineIsOnExactlyWhenTheToolIsInstalled"/> checks the
    /// define itself against what is actually loaded, and everything not behind a
    /// <c>#if</c> below is the tool-free behaviour asserted directly.
    ///
    /// Nothing here starts either tool. GestureManager's controlled avatars are a public static
    /// dictionary and Av3Emulator's runtime component is public fields, so the states this cares
    /// about are set by hand — which is also the only way to produce a mirror clone or a third
    /// non-local copy on demand. What it costs is stated rather than hidden: these are the
    /// SHAPES the tools produce, pinned from their sources, and a release that changes one is
    /// caught by the compiler here rather than by this file going red.
    /// </summary>
    public class DynamicAnalyzeToolsTests
    {
        readonly List<GameObject> _made = new List<GameObject>();
        readonly List<IDisposable> _rigs = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            ForgetControlledAvatars();
            foreach (var rig in _rigs) rig.Dispose();
            _rigs.Clear();
            foreach (var go in _made)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _made.Clear();
        }

        Animator Avatar(string name)
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.HideAndDontSave;
            _made.Add(go);
            return go.AddComponent<Animator>();
        }

        static List<Animator> List(params Animator[] animators) =>
            new List<Animator>(animators);

        // ---- the defines --------------------------------------------------------

        /// <summary>
        /// The one fact everything else in this file rests on: <c>DAERD_GM</c> and
        /// <c>DAERD_AV3E</c> are on when — and only when — the package is in the project. They
        /// come from the .asmdef's versionDefines rather than from anything a person sets, so
        /// the failure this catches is the silent one: a package renamed upstream leaves the
        /// define off, every <c>#if</c> block disappears, and the feature quietly stops existing
        /// while every test still passes.
        ///
        /// Asked of the loaded assemblies rather than of the package manager, because the
        /// assembly is what the define is FOR: a package present but not compiled would leave
        /// the typed code unable to link, which is the same failure with a longer explanation.
        /// </summary>
        [Test]
        public void TheDefineIsOnExactlyWhenTheToolIsInstalled()
        {
            bool gm = Loaded("vrchat.blackstartx.gesture-manager");
            bool av3e = Loaded("lyuma.av3emulator");
#if DAERD_GM
            Assert.IsTrue(gm, "DAERD_GM is defined but GestureManager's assembly is not loaded");
#else
            Assert.IsFalse(gm,
                "GestureManager is installed and DAERD_GM is not defined — the versionDefine's "
                + "package name has stopped matching the package");
#endif
#if DAERD_AV3E
            Assert.IsTrue(av3e, "DAERD_AV3E is defined but Av3Emulator's assembly is not loaded");
#else
            Assert.IsFalse(av3e,
                "Av3Emulator is installed and DAERD_AV3E is not defined — the versionDefine's "
                + "package name has stopped matching the package");
#endif
        }

        static bool Loaded(string assembly)
        {
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                if (loaded.GetName().Name == assembly) return true;
            return false;
        }

        // ---- an avatar nobody has hold of ---------------------------------------

        /// <summary>
        /// The ordinary project, which is most of them: no tool installed, or one installed and
        /// holding nothing. An avatar in that state is named after itself, is a candidate, and
        /// is nobody's preference — the behaviour the Rec mood had before any of this existed,
        /// asserted rather than assumed, because it is what every project without both packages
        /// gets.
        /// </summary>
        [Test]
        public void AnAvatarNoToolHasHoldOfIsItsOwnNameAndIsStillACandidate()
        {
            var animator = Avatar("Nobody's");

            var hold = PlayTools.On(animator);
            Assert.AreEqual(PlayTools.Tool.None, hold.tool);
            Assert.AreEqual(PlayTools.Role.None, hold.role);
            Assert.IsFalse(hold.Known);
            Assert.IsFalse(hold.Hidden);

            Assert.AreEqual("Nobody's", PlayTools.Label(animator));
            CollectionAssert.Contains(PlayTools.Candidates(List(animator)), animator);
            Assert.IsNull(PlayTools.Preferred(List(animator)));
        }

        /// <summary>Nothing at all, asked of every entry point. A null Animator turns up
        /// whenever the field in the window is empty, which is how it opens.</summary>
        [Test]
        public void NothingIsHeldByNobodyAndAnswersWithoutThrowing()
        {
            Assert.AreEqual(PlayTools.Tool.None, PlayTools.On(null).tool);
            Assert.AreEqual(string.Empty, PlayTools.Label(null));
            Assert.IsEmpty(PlayTools.ClonesOf(null));
            Assert.IsEmpty(PlayTools.Candidates(null));
            Assert.IsNull(PlayTools.Preferred(null));
        }

        // ---- GestureManager -----------------------------------------------------

        /// <summary>
        /// The avatar GestureManager has hold of: named after the tool, and the one picked when
        /// nobody said which.
        ///
        /// The dictionary is written to directly, with no module behind the entry. That is the
        /// shape the detection was designed around — the KEY is the avatar, and reading the
        /// module would make this depend on a second thing being true at the same moment — so a
        /// null value is a fair test of it rather than a shortcut around one.
        /// </summary>
        [Test]
        public void GestureManagersAvatarIsNamedAfterTheToolAndIsThePreferredOne()
        {
#if DAERD_GM
            var plain = Avatar("Plain");
            var held = Avatar("Held");
            GestureManagerTakes(held);

            var hold = PlayTools.On(held);
            Assert.AreEqual(PlayTools.Tool.GestureManager, hold.tool);
            Assert.AreEqual(PlayTools.Role.Worn, hold.role);
            Assert.AreEqual("GestureManager: Held", PlayTools.Label(held));
            Assert.AreEqual("Plain", PlayTools.Label(plain));

            Assert.AreSame(held, PlayTools.Preferred(List(plain, held)),
                "the avatar somebody picked by hand is the one to record");
            CollectionAssert.AreEqual(List(plain, held),
                PlayTools.Candidates(List(plain, held)),
                "being held is not a reason to leave anything out of the list");
#else
            Assert.Ignore("GestureManager is not installed in this project.");
#endif
        }

        /// <summary>GestureManager letting go puts the avatar back among the ordinary ones. The
        /// dictionary is read on every ask rather than cached, and this is what that buys.</summary>
        [Test]
        public void GestureManagerLettingGoLeavesAnOrdinaryAvatarBehind()
        {
#if DAERD_GM
            var held = Avatar("Held");
            GestureManagerTakes(held);
            Assert.AreEqual(PlayTools.Tool.GestureManager, PlayTools.On(held).tool);

            ForgetControlledAvatars();
            Assert.AreEqual(PlayTools.Tool.None, PlayTools.On(held).tool);
            Assert.IsNull(PlayTools.Preferred(List(held)));
#else
            Assert.Ignore("GestureManager is not installed in this project.");
#endif
        }

        /// <summary>
        /// The write side of the same dictionary: an avatar GestureManager is holding, whose
        /// entry is not a VRChat 3 module, takes no input.
        ///
        /// What this pins is the SHAPE — that the module is asked for by type and that anything
        /// else answers "there is nowhere to send this" rather than throwing. What it cannot
        /// pin is a real ModuleVrc3, which the tool builds out of a live avatar descriptor
        /// inside Play mode; that the radial menu really moves when an input is sent is a thing
        /// somebody checks with a headset on, and is written down as such rather than faked
        /// here with a mock of somebody else's class.
        /// </summary>
        [Test]
        public void GestureManagerTakesNoInputForAnAvatarItIsNotRunningAsAVrc3One()
        {
#if DAERD_GM
            var held = Avatar("Held");
            GestureManagerTakes(held);
            Assert.AreEqual(PlayTools.Tool.GestureManager, PlayTools.On(held).tool,
                "the tool does have it — which is exactly the case worth asking about");

            // Held, and the entry is not a module that has parameters. An input sent here would
            // land nowhere, so it is refused rather than dropped quietly.
            Assert.IsFalse(PlayTools.CanWrite(held));
            Assert.IsFalse(PlayTools.Write(held, "Go", 1f));

            ForgetControlledAvatars();
            Assert.IsFalse(PlayTools.CanWrite(held));
#else
            Assert.Ignore("GestureManager is not installed in this project.");
#endif
        }

        // ---- Av3Emulator --------------------------------------------------------

        /// <summary>The local copy — the one the person in the editor is wearing — is named
        /// after the tool and is what gets recorded when nobody says which.</summary>
        [Test]
        public void Av3EmulatorsLocalAvatarIsNamedAfterTheToolAndIsThePreferredOne()
        {
#if DAERD_AV3E
            var plain = Avatar("Plain");
            var worn = Avatar("Worn");
            Wearer(worn);

            var hold = PlayTools.On(worn);
            Assert.AreEqual(PlayTools.Tool.Av3Emulator, hold.tool);
            Assert.AreEqual(PlayTools.Role.Worn, hold.role);
            Assert.AreEqual("Av3Emulator: Worn", PlayTools.Label(worn));
            Assert.AreSame(worn, PlayTools.Preferred(List(plain, worn)));
#else
            Assert.Ignore("Av3Emulator is not installed in this project.");
#endif
        }

        /// <summary>
        /// Mirror and shadow copies are left out of the candidate list entirely. They exist to
        /// answer a question about rendering — what you see in a mirror, what casts a shadow —
        /// and they run the same controller, so they are indistinguishable from the real avatar
        /// to everything below this layer. Offering one would be offering the wrong avatar under
        /// a name that looked right.
        /// </summary>
        [Test]
        public void Av3EmulatorsMirrorAndShadowCopiesAreLeftOutOfTheList()
        {
#if DAERD_AV3E
            var worn = Avatar("Worn");
            var source = Wearer(worn);
            var mirror = Avatar("Worn (Mirror)");
            var shadow = Avatar("Worn (Shadow)");
            var mirrorRuntime = Runtime(mirror);
            mirrorRuntime.AvatarSyncSource = source;
            mirrorRuntime.IsMirrorClone = true;
            var shadowRuntime = Runtime(shadow);
            shadowRuntime.AvatarSyncSource = source;
            shadowRuntime.IsShadowClone = true;

            Assert.AreEqual(PlayTools.Role.Aside, PlayTools.On(mirror).role);
            Assert.AreEqual(PlayTools.Role.Aside, PlayTools.On(shadow).role);
            Assert.IsTrue(PlayTools.On(mirror).Hidden);

            CollectionAssert.AreEqual(List(worn),
                PlayTools.Candidates(List(worn, mirror, shadow)));
            Assert.IsEmpty(PlayTools.ClonesOf(mirror), "a rendering copy has no other people");
#else
            Assert.Ignore("Av3Emulator is not installed in this project.");
#endif
        }

        /// <summary>
        /// A non-local clone is somebody else's view of the same avatar. It stays in the list —
        /// recording one on purpose is a reasonable thing to want — and is never what gets
        /// chosen for you, because what it shows is what crossed the wire rather than what the
        /// wearer did.
        /// </summary>
        [Test]
        public void Av3EmulatorsNonLocalCloneIsListedAndNeverChosenForYou()
        {
#if DAERD_AV3E
            var worn = Avatar("Worn");
            var source = Wearer(worn);
            var other = Avatar("Worn (Non-Local 1)");
            var clone = Runtime(other);
            clone.AvatarSyncSource = source;
            source.NonLocalClones.Add(clone);

            Assert.AreEqual(PlayTools.Role.Copy, PlayTools.On(other).role);
            Assert.IsFalse(PlayTools.On(other).Hidden);
            Assert.AreEqual("Av3Emulator: Worn (Non-Local 1)", PlayTools.Label(other));
            CollectionAssert.AreEqual(List(worn, other),
                PlayTools.Candidates(List(worn, other)));
            Assert.AreSame(worn, PlayTools.Preferred(List(other, worn)),
                "the wearer is the default even when a copy is listed first");
            Assert.IsNull(PlayTools.Preferred(List(other)),
                "a copy on its own is nobody's default");
#else
            Assert.Ignore("Av3Emulator is not installed in this project.");
#endif
        }

        /// <summary>
        /// The other people's copies of one avatar, in the order the tool made them — and none
        /// for anybody who is not the source, which is what keeps a copy's copies out of a
        /// recording of the copy.
        /// </summary>
        [Test]
        public void TheClonesOfAnAvatarAreTheNonLocalOnesTheSourceHolds()
        {
#if DAERD_AV3E
            var worn = Avatar("Worn");
            var source = Wearer(worn);
            var first = Avatar("Worn (Non-Local 1)");
            var second = Avatar("Worn (Non-Local 2)");
            var mirror = Avatar("Worn (Mirror)");
            foreach (var animator in new[] { first, second })
            {
                var runtime = Runtime(animator);
                runtime.AvatarSyncSource = source;
                source.NonLocalClones.Add(runtime);
            }
            var aside = Runtime(mirror);
            aside.AvatarSyncSource = source;
            aside.IsMirrorClone = true;
            source.NonLocalClones.Add(aside);

            CollectionAssert.AreEqual(List(first, second), PlayTools.ClonesOf(worn),
                "the rendering copy is not one of the other people");
            Assert.IsEmpty(PlayTools.ClonesOf(first),
                "a copy's own list is not somebody else's copies");
            Assert.IsEmpty(PlayTools.ClonesOf(Avatar("Untouched")));
#else
            Assert.Ignore("Av3Emulator is not installed in this project.");
#endif
        }

        /// <summary>
        /// The whole path, end to end: Av3Emulator says who the other people's copies are, and
        /// the recorder puts them in one trace beside the wearer under a scope each.
        ///
        /// The graphs are built by hand, so what this proves about a REAL session is bounded and
        /// worth saying: a copy whose graph runs the same controller matches it and gets its
        /// state rows, which is the case Av3Emulator produces because a clone is a copy of the
        /// avatar. A copy that somehow ran something else would fall back to parameters only,
        /// like any unmatched avatar, rather than borrowing the wearer's labels.
        /// </summary>
        [Test]
        public void Av3EmulatorsCopiesAreRecordedBesideTheWearerUnderTheirOwnScopes()
        {
#if DAERD_AV3E
            var controller = Controller("Base");
            var worn = Avatar("Worn");
            var other = Avatar("Worn (Non-Local 1)");
            var source = Wearer(worn);
            var clone = Runtime(other);
            clone.AvatarSyncSource = source;
            source.NonLocalClones.Add(clone);
            Drive(worn, controller);
            Drive(other, controller);

            var copies = PlayTools.ClonesOf(worn);
            CollectionAssert.AreEqual(List(other), copies);

            var recorder = PlayRecorder.On(worn, controller, copies);
            Assert.AreEqual(2, recorder.Sources);
            Assert.IsTrue(recorder.Matched);
            Assert.IsTrue(recorder.Sample(1, 0f));
            Assert.IsNotNull(recorder.Trace.Find(Simulation.PlayScope, "Base/state"));
            Assert.IsNotNull(recorder.Trace.Find(Simulation.PlayRemoteScopeAt(0), "Base/state"),
                "the copy was named but its rows were not recorded");
#else
            Assert.Ignore("Av3Emulator is not installed in this project.");
#endif
        }

        // ---- the two tools together, and the controller above both ---------------

        /// <summary>GestureManager wins when both have the same avatar: it holds exactly one at
        /// a time and somebody chose it by hand, which is the strongest statement of intent on
        /// offer.</summary>
        [Test]
        public void GestureManagerWinsOverAv3EmulatorOnTheSameAvatar()
        {
#if DAERD_GM && DAERD_AV3E
            var both = Avatar("Both");
            Wearer(both);
            GestureManagerTakes(both);

            Assert.AreEqual(PlayTools.Tool.GestureManager, PlayTools.On(both).tool);
            Assert.AreEqual("GestureManager: Both", PlayTools.Label(both));
#else
            Assert.Ignore("Both tools are needed in the project to tell which one wins.");
#endif
        }

        /// <summary>
        /// Running the controller beats being held by a tool, which is the one ordering that
        /// could not be got wrong quietly: arming refuses an avatar that is not running this
        /// controller, so a preference strong enough to pick one over an avatar that IS running
        /// it would be a preference for never starting a recording at all.
        /// </summary>
        [Test]
        public void LikeliestPrefersTheAvatarRunningTheController_OverTheOneAToolHolds()
        {
#if DAERD_GM
            var controller = Controller("ToolPick");
            var running = Avatar("Running It");
            var held = Avatar("Held");
            Drive(running, controller);
            Drive(held, Controller("Something Else"));
            GestureManagerTakes(held);

            Assert.AreSame(running, PlayRecorder.Likeliest(controller));
#else
            Assert.Ignore("GestureManager is not installed in this project.");
#endif
        }

        /// <summary>And underneath that, the tool breaks the tie that used to be broken by
        /// whichever graph Unity happened to hand out first.</summary>
        [Test]
        public void LikeliestFallsBackToTheToolsAvatarWhenNobodyRunsTheController()
        {
#if DAERD_GM
            var controller = Controller("ToolPick");
            var plain = Avatar("Plain");
            var held = Avatar("Held");
            Drive(plain, Controller("Something Else"));
            Drive(held, Controller("Something Else Again"));
            GestureManagerTakes(held);

            Assert.AreSame(held, PlayRecorder.Likeliest(controller));
#else
            Assert.Ignore("GestureManager is not installed in this project.");
#endif
        }

        // ---- the tools, set up by hand -------------------------------------------

#if DAERD_GM
        void GestureManagerTakes(Animator animator)
        {
            global::BlackStartX.GestureManager.GestureManager
                .ControlledAvatars[animator.gameObject] = null;
        }
#endif

        /// <summary>Everything this file put in GestureManager's dictionary, taken back out. It
        /// is static and lives as long as the editor does, so an entry left behind would be an
        /// avatar the next test finds held by a tool that never ran.</summary>
        void ForgetControlledAvatars()
        {
#if DAERD_GM
            foreach (var go in _made)
                if (go != null)
                    global::BlackStartX.GestureManager.GestureManager
                        .ControlledAvatars.Remove(go);
#endif
        }

#if DAERD_AV3E
        static global::Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime Runtime(Animator animator) =>
            animator.gameObject
                .AddComponent<global::Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime>();

        /// <summary>The tool's own state for "this is the copy the person is wearing", set by
        /// hand: its Awake is what normally sets these, and Awake does not run outside Play
        /// mode — measured, the component has neither ExecuteAlways nor ExecuteInEditMode, so
        /// adding one here starts nothing and touches nothing else in the scene.</summary>
        static global::Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime Wearer(Animator animator)
        {
            var runtime = Runtime(animator);
            runtime.AvatarSyncSource = runtime;
            runtime.IsLocal = true;
            return runtime;
        }
#endif

        // ---- a graph to be driven by ----------------------------------------------

        /// <summary>A controller with one layer of the given name and one state to sit in. Only
        /// the layer names matter to what is being tested — they are what a recorder matches a
        /// running graph by — but a layer with nowhere to be is not a thing Mecanim is willing
        /// to run.</summary>
        AnimatorController Controller(string layer)
        {
            var controller = new AnimatorController();
            controller.name = "Tools " + layer;
            controller.hideFlags = HideFlags.HideAndDontSave;
            controller.AddLayer(layer);
            var machine = controller.layers[0].stateMachine;
            machine.defaultState = machine.AddState("Idle");
            _rigs.Add(new Wreck(controller));
            return controller;
        }

        /// <summary>An avatar as a tool hands one to Mecanim: a graph writing to the Animator,
        /// with the controller inside the graph rather than on the component.</summary>
        void Drive(Animator animator, AnimatorController controller)
        {
            var graph = PlayableGraph.Create("DaerD Tools Graph");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var playable = AnimatorControllerPlayable.Create(graph, controller);
            var output = AnimationPlayableOutput.Create(graph, "DaerD Tools", animator);
            output.SetSourcePlayable(playable);
            graph.Play();
            _rigs.Add(new Wreck(graph));
        }

        /// <summary>Something to be taken down again. A graph left running is not only a leak:
        /// Unity goes on handing it out, so it is a candidate the next test would find.</summary>
        sealed class Wreck : IDisposable
        {
            readonly PlayableGraph _graph;
            readonly UnityEngine.Object _object;

            public Wreck(PlayableGraph graph) { _graph = graph; }

            public Wreck(UnityEngine.Object thing) { _object = thing; }

            public void Dispose()
            {
                if (_graph.IsValid()) _graph.Destroy();
                if (_object != null) UnityEngine.Object.DestroyImmediate(_object);
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Yozolab.DaerD.DynamicAnalyze;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// What DD DynamicAnalyze sees by standing in an avatar's build, and how it behaves in a
    /// project that has no build framework at all.
    ///
    /// <para>Both halves are the point.</para> Most projects have neither NDMF nor Modular
    /// Avatar, and in those the capture has to be a thing that exists, answers "no build has
    /// been seen" and costs nothing — which is asserted directly, without a <c>#if</c> in
    /// sight. With both installed, the assertions are about somebody else's machinery: that a
    /// build really does hand over a merged controller, that the rename table can be had, and
    /// exactly how long what it hands over lives. Every test here exists in both projects; the
    /// ones that need a build are skipped by name when there is none, so the count does not
    /// change between the two runs and a test that vanished cannot be mistaken for one that
    /// passed.
    ///
    /// <para>Half of this file is measurement rather than assertion of our own code.</para>
    /// Where the capture passes sit, whether the pieces are still alive by the time they are
    /// read, and what a build leaves behind afterwards are all facts about NDMF and Modular
    /// Avatar that were checked by running them before <see cref="BuildCapture"/> was designed
    /// around them. They are pinned here rather than remembered: if an upgrade changes one, the
    /// thing that fails is this file, and the answer is to re-read the result and re-argue the
    /// design.
    ///
    /// Nothing here names a Modular Avatar type. The components it needs are added by type name
    /// and configured through SerializedObject, which is how the rest of DaerD reaches somebody
    /// else's package — and it is what lets these tests be written without the product assembly
    /// gaining a reference the design deliberately refuses.
    /// </summary>
    public class DynamicAnalyzeBuildTests
    {
        const string DescriptorType = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";
        const string ExpressionParametersType =
            "VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters";
        const string MaParametersType = "nadena.dev.modular_avatar.core.ModularAvatarParameters";
        const string MaMergeAnimatorType =
            "nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator";
        /// <summary>The one avatar here that is an ordinary scene object rather than a hidden
        /// one, because entering Play mode carries the scene and leaves the rest behind.</summary>
        const string OnPlay = "DD Build On Play";
        /// <summary>And the one whose pieces are files rather than objects in memory. Measured:
        /// entering Play mode serialises the scene, and a reference from it to a controller that
        /// is not an asset cannot be written down — the avatar comes back with an empty playable
        /// layer. Real avatars are made of files, so this is the rig catching up with them
        /// rather than a concession.</summary>
        const string OnPlayFx = "Assets/DDBuildOnPlayFx.controller";
        const string OnPlayParameters = "Assets/DDBuildOnPlayParams.asset";

        readonly List<GameObject> _made = new List<GameObject>();
        readonly List<UnityEngine.Object> _assets = new List<UnityEngine.Object>();

        /// <summary>Deliberately no [SetUp] that empties the registry.
        ///
        /// Measured: when an EditMode test enters Play mode, the domain reload it causes makes
        /// the test framework run this class's SetUp AGAIN before resuming the test — and by
        /// then the build has already happened, so a SetUp that cleared the registry would throw
        /// away the very thing the probe below is there to look at. Clearing on the way OUT is
        /// enough, and it is the half that keeps one test's leftovers out of the next.</summary>

        /// <summary>Everything back: Play mode left, the scene emptied of what was put in it —
        /// including the one object that is deliberately an ordinary scene object and so cannot
        /// be found in a list a domain reload took — and the build's own leftovers cleaned.</summary>
        [TearDown]
        public void TearDown()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                EditorApplication.isPlaying = false;
            BuildCapture.Forget();
            foreach (var go in _made)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _made.Clear();
            var kept = GameObject.Find(OnPlay);
            if (kept != null) UnityEngine.Object.DestroyImmediate(kept);
            CleanBuildLeftovers();
            AssetDatabase.DeleteAsset(OnPlayFx);
            AssetDatabase.DeleteAsset(OnPlayParameters);
            foreach (var asset in _assets)
                // Measured: with nothing installed that clones controllers first, a build files
                // the very objects it was handed into its own asset folder — so a rig's own
                // controller can come back out of one as an asset on disk. Cleaning the build's
                // leftovers above takes those with it; destroying what is left by hand is for
                // the ones that never became files.
                if (asset != null && !AssetDatabase.Contains(asset))
                    UnityEngine.Object.DestroyImmediate(asset);
            _assets.Clear();
        }

        // ---- what a project without any of this gets ---------------------------

        /// <summary>
        /// The whole of the tool-free behaviour, asserted rather than skipped: every entry point
        /// answers, none of them throws, and what they answer is "nothing was built".
        ///
        /// Null and empty are kept apart deliberately. <see cref="BuildCapture.SyncedFor"/>
        /// answering null means no build of this avatar was watched; answering an empty list
        /// would mean a build that syncs nothing, and the panel offering to fill a list from
        /// the second is right where offering to fill it from the first is not.
        /// </summary>
        [Test]
        public void AnAvatarNoBuildHasSeenIsAnsweredForRatherThanThrownAt()
        {
            var animator = Avatar("DD Build Unbuilt");
            Assert.IsFalse(BuildCapture.Has(animator));
            Assert.IsNull(BuildCapture.For(animator));
            Assert.IsEmpty(BuildCapture.ControllersFor(animator));
            Assert.IsNull(BuildCapture.SyncedFor(animator));
            Assert.IsEmpty(BuildCapture.KindOf(animator, null));
            Assert.IsEmpty(BuildCapture.RemapOf(animator.gameObject));
            Assert.IsEmpty(BuildCapture.PrefixRemapOf(animator.gameObject));
            Assert.AreEqual(0, BuildCapture.Count);

            Assert.IsNull(BuildCapture.For(null));
            Assert.IsNull(BuildCapture.Of(null));
            Assert.IsFalse(BuildCapture.Has(null));
            Assert.IsEmpty(BuildCapture.ControllersFor(null));
            Assert.IsEmpty(BuildCapture.RemapOf(null));
        }

        /// <summary>
        /// The one fact everything else rests on: <c>DAERD_NDMF</c> and <c>DAERD_VRC</c> are on
        /// when — and only when — the package is in the project. They come from the .asmdef's
        /// versionDefines rather than from anything a person sets, so the failure this catches
        /// is the silent one: a package renamed upstream leaves the define off, the plugin
        /// disappears from the build, and the feature quietly stops existing while every test
        /// still passes.
        ///
        /// Both defines matter and neither implies the other. NDMF builds avatars for platforms
        /// that are not VRChat, and everything captured here — playable layers, expression
        /// parameters — is VRChat's shape, so the capture is compiled only when both are there.
        /// </summary>
        [Test]
        public void TheDefinesAreOnExactlyWhenThePackagesAre()
        {
            bool ndmf = Loaded("nadena.dev.ndmf");
            bool vrc = Loaded("VRCSDK3A");
#if DAERD_NDMF
            Assert.IsTrue(ndmf, "DAERD_NDMF is defined but NDMF's assembly is not loaded");
#else
            Assert.IsFalse(ndmf,
                "NDMF is installed and DAERD_NDMF is not defined — the versionDefine's package "
                + "name has stopped matching the package");
#endif
#if DAERD_VRC
            Assert.IsTrue(vrc, "DAERD_VRC is defined but the VRChat avatars SDK is not loaded");
#else
            Assert.IsFalse(vrc,
                "the VRChat avatars SDK is installed and DAERD_VRC is not defined");
#endif
        }

        static bool Loaded(string assemblyName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == assemblyName) return true;
            return false;
        }

        // ---- and what a project with one gets ----------------------------------

        /// <summary>
        /// The end-to-end: an avatar whose FX is assembled out of two pieces by Modular Avatar,
        /// built the way a person bakes one, and everything the capture promises taken off the
        /// registry afterwards.
        ///
        /// The controller in the registry is the MERGED one — the layer the gimmick contributed
        /// is in it, and it is not the object anybody has a reference to in the editor. That is
        /// the whole reason this feature exists: a recording matched against the FX in the
        /// window's field would find nothing on this avatar.
        /// </summary>
        [Test]
        public void ABuildLeavesTheMergedControllerAndWhatItSyncsInTheRegistry()
        {
#if DAERD_NDMF && DAERD_VRC
            Skip.WithoutModularAvatar();
            var avatar = Rig("DD Build Merged");
            Build(avatar.root);

            var built = BuildCapture.For(avatar.animator);
            Assert.IsNotNull(built, "the build ran and nothing was captured off it");
            Assert.AreEqual("DD Build Merged", built.avatar);
            Assert.AreEqual("Optimizing", built.phase,
                "the last capture point is the end of the last phase a pass was put in");

            var controllers = BuildCapture.ControllersFor(avatar.animator);
            Assert.AreEqual(1, controllers.Count, "one playable layer was set up on this avatar");
            var fx = controllers[0];
            Assert.AreEqual("FX", BuildCapture.KindOf(avatar.animator, fx));
            Assert.AreNotSame(avatar.fx, fx,
                "the built controller is the one in the field, so nothing was assembled");
            // Contains rather than equals: an assembler puts layers of its own in beside the
            // ones anybody asked for (Modular Avatar adds a pair for MMD dances), and a test
            // that pinned the whole list would be pinning somebody else's release notes.
            var layers = LayerNames(fx);
            CollectionAssert.Contains(layers, "Base");
            CollectionAssert.Contains(layers, "Gimmick",
                "the gimmick's layer is not in the built FX");
            Assert.AreEqual(1, avatar.fx.layers.Length,
                "the controller in the field is still the single-layer one it was, which is the "
                + "whole reason a recording has to be matched against the built one instead");
#else
            Assert.Ignore("NDMF and the VRChat avatars SDK are not both installed.");
#endif
        }

        /// <summary>
        /// The bridge itself: the name a person typed, and the name the avatar ended up wearing.
        ///
        /// Modular Avatar's internal parameters are renamed to <c>name$<i>hash</i></c>, where
        /// the hash is derived from the declaring component's path inside the avatar. The exact
        /// name is asserted rather than a pattern, and computed here from the same rule rather
        /// than copied off a run: what it is really pinning is that the capture reads the SAME
        /// mapping the build applies, and a pattern would pass just as happily against a table
        /// that had drifted a suffix away from what the controller says.
        /// </summary>
        [Test]
        public void TheRenameTableSaysWhatEachNameBecame()
        {
#if DAERD_NDMF && DAERD_VRC
            Skip.WithoutModularAvatar();
            var avatar = Rig("DD Build Renamed");
            Build(avatar.root);

            string became = Internal("Wag", "Gimmick");
            var renames = BuildCapture.RemapOf(avatar.root);
            Assert.IsTrue(renames.ContainsKey("Wag"),
                "the parameter the gimmick declares is not in the rename table");
            Assert.AreEqual(became, renames["Wag"]);

            var fx = BuildCapture.ControllersFor(avatar.animator)[0];
            CollectionAssert.Contains(ParameterNames(fx), became,
                "the built controller does not use the name the table says it would");
            CollectionAssert.DoesNotContain(ParameterNames(fx), "Wag",
                "the editing-time name survived into the build, so nothing was renamed and this "
                + "test is measuring the wrong thing");

            var built = BuildCapture.For(avatar.animator);
            CollectionAssert.Contains(built.synced, became,
                "the built expression parameters do not carry the renamed parameter");
            CollectionAssert.DoesNotContain(built.synced, "Wag");
#else
            Assert.Ignore("NDMF and the VRChat avatars SDK are not both installed.");
#endif
        }

        /// <summary>
        /// Why the rename table is taken at the end of Resolving and not beside the controllers.
        ///
        /// Measured: the components that DECLARE the renames are destroyed by the pass that
        /// applies them, which runs in Transforming. NDMF's rename API answers by asking those
        /// components, so by the time the finished controllers exist there is nothing left to
        /// ask. This is the fact that shape rests on, so it is checked rather than trusted — if
        /// a release starts leaving the components in place, the capture could move and this is
        /// what would say so.
        /// </summary>
        [Test]
        public void TheComponentsThatDeclareRenamesDoNotSurviveTheBuild()
        {
#if DAERD_NDMF && DAERD_VRC
            Skip.WithoutModularAvatar();
            var avatar = Rig("DD Build Declarers");
            Assert.IsNotNull(Find(avatar.root, MaParametersType),
                "the rig did not set up the component this is about");
            Build(avatar.root);
            Assert.IsNull(Find(avatar.root, MaParametersType),
                "the declaring components are still there after the build — the rename table "
                + "could be read later than it is, and the reason for the Resolving pass has "
                + "gone away");
            Assert.IsNotEmpty(BuildCapture.RemapOf(avatar.root),
                "and the table was captured anyway, which is what the early pass is for");
#else
            Assert.Ignore("NDMF and the VRChat avatars SDK are not both installed.");
#endif
        }

        /// <summary>
        /// How long a built controller lives, which is the reason nothing here is cloned and
        /// everything that has to outlive a session is copied out as text.
        ///
        /// The controllers a build produces are temporary assets in a folder NDMF owns, and NDMF
        /// deletes that folder — on leaving Play mode, and whenever anything asks it to. So the
        /// reference in the registry is alive for exactly as long as the build's leftovers are,
        /// and a reader that comes back later finds a destroyed object. Everything that has to
        /// answer afterwards is a string by then: the rename table, what is synced, the
        /// parameters standing at each phase.
        /// </summary>
        [Test]
        public void TheBuiltControllerIsTemporaryAndTheTextCapturedBesideItIsNot()
        {
#if DAERD_NDMF && DAERD_VRC
            Skip.WithoutModularAvatar();
            var avatar = Rig("DD Build Lifetime");
            Build(avatar.root);

            var built = BuildCapture.For(avatar.animator);
            var fx = BuildCapture.ControllersFor(avatar.animator)[0];
            Assert.IsTrue(AssetDatabase.Contains(fx),
                "the built controller is not an asset, so what follows is not measuring what it "
                + "says it is");
            int names = built.synced.Count, renames = built.renames.Count;
            Assert.Greater(names, 0);
            Assert.Greater(renames, 0);

            nadena.dev.ndmf.AvatarProcessor.CleanTemporaryAssets();

            Assert.IsTrue(fx == null,
                "the build's leftovers were cleaned and the controller survived — cloning it "
                + "would be unnecessary and this design note is wrong");
            Assert.AreEqual(names, built.synced.Count,
                "what the build syncs was copied out as text and should not depend on the asset");
            Assert.AreEqual(renames, built.renames.Count);
            Assert.AreEqual("DD Build Lifetime", built.avatar);
#else
            Assert.Ignore("NDMF and the VRChat avatars SDK are not both installed.");
#endif
        }

        /// <summary>
        /// A second build of the same avatar replaces the first rather than sitting beside it.
        ///
        /// Which matters because entering Play mode, leaving it and entering it again is the
        /// ordinary way to use this, and each of those is a build of an object that is new every
        /// time. Two entries for one avatar would be a reader choosing between two answers to
        /// one question, and the later one is always the one meant.
        /// </summary>
        [Test]
        public void BuildingTheSameAvatarAgainReplacesWhatWasCapturedOfIt()
        {
#if DAERD_NDMF && DAERD_VRC
            Skip.WithoutModularAvatar();
            var first = Rig("DD Build Twice");
            Build(first.root);
            var was = BuildCapture.ControllersFor(first.animator)[0];
            UnityEngine.Object.DestroyImmediate(first.root);
            _made.Remove(first.root);

            var again = Rig("DD Build Twice");
            Build(again.root);
            Assert.AreEqual(1, BuildCapture.Count,
                "the same avatar built twice left two entries behind");
            var now = BuildCapture.ControllersFor(again.animator)[0];
            Assert.AreNotSame(was, now, "the entry still holds the first build's controller");
#else
            Assert.Ignore("NDMF and the VRChat avatars SDK are not both installed.");
#endif
        }

        /// <summary>
        /// The passes are ordered after Modular Avatar by NAME, which is what lets this file
        /// avoid referencing it — and a name is a thing that can be absent. Measured here in the
        /// only way this project can measure it while MA is installed: an avatar with no MA
        /// components on it at all still gets captured, so the ordering constraint is satisfied
        /// by a plugin that has nothing to do. The stronger case — MA not installed — is
        /// measured by running this suite with it taken out of the project.
        /// </summary>
        [Test]
        public void AnAvatarWithNothingAssemblingItIsCapturedJustTheSame()
        {
#if DAERD_NDMF && DAERD_VRC
            var avatar = Rig("DD Build Plain", gimmick: false);
            Build(avatar.root);

            var built = BuildCapture.For(avatar.animator);
            Assert.IsNotNull(built, "a plain avatar's build was not captured");
            Assert.AreEqual(1, BuildCapture.ControllersFor(avatar.animator).Count);
            CollectionAssert.AreEquivalent(new[] { "Base" },
                LayerNames(BuildCapture.ControllersFor(avatar.animator)[0]));
            CollectionAssert.Contains(built.synced, "Wave",
                "the avatar's own synced parameter is not in the capture");
            Assert.IsEmpty(built.renames, "nothing renamed anything and a table appeared anyway");
#else
            Assert.Ignore("NDMF and the VRChat avatars SDK are not both installed.");
#endif
        }

        /// <summary>
        /// A parameter set per phase, which is the material for saying what a build added,
        /// renamed or took away. Nothing shows it yet — this wave collects it — so what is
        /// asserted is that the collecting happens at every point a pass was put, and that the
        /// sets really do differ across the phase the assembling happens in.
        /// </summary>
        [Test]
        public void EveryCapturePointLeavesTheParametersAsTheyStoodInIt()
        {
#if DAERD_NDMF && DAERD_VRC
            Skip.WithoutModularAvatar();
            var avatar = Rig("DD Build Phases");
            Build(avatar.root);

            var built = BuildCapture.For(avatar.animator);
            CollectionAssert.AreEquivalent(new[] { "Resolving", "Transforming", "Optimizing" },
                new List<string>(built.parametersAt.Keys));
            CollectionAssert.DoesNotContain(built.parametersAt["Resolving"],
                Internal("Wag", "Gimmick"),
                "the renamed parameter was already there before anything was assembled");
            CollectionAssert.Contains(built.parametersAt["Transforming"],
                Internal("Wag", "Gimmick"),
                "the assembling phase did not add the parameter the merge brings with it");
#else
            Assert.Ignore("NDMF and the VRChat avatars SDK are not both installed.");
#endif
        }

        // ---- in a real Play mode ------------------------------------------------

        /// <summary>
        /// The order the whole design rests on, in the mode it actually happens in.
        ///
        /// Entering Play mode reloads the domain, which empties every static in the editor —
        /// including this registry. The build that ApplyOnPlay runs happens in the Awake of the
        /// scene that comes back AFTER that reload, so the registry is filled rather than
        /// emptied, and a plain static field is enough; nothing has to be squirrelled away in
        /// SessionState. Leaving Play mode does NOT reload, which is the other half: a recording
        /// is read after Play mode ends, and what it was matched against has to still be there.
        ///
        /// Everything is therefore built BEFORE entering — an avatar in the scene is what
        /// ApplyOnPlay looks for — and read after. The object is found by name on the way out
        /// rather than held in a variable: a domain reload takes the test's own locals with it.
        /// </summary>
        [UnityTest]
        [Category("PlayModeProbe")]
        public IEnumerator EnteringPlayModeFillsTheRegistryAndLeavingItDoesNotEmptyIt()
        {
#if DAERD_NDMF && DAERD_VRC
            var avatar = Rig(OnPlay, keep: true, saved: true);
            Assert.AreEqual(0, BuildCapture.Count, "something was captured before Play mode");

            yield return new EnterPlayMode();

            Assert.IsTrue(Application.isPlaying);
            Assert.AreEqual(1, BuildCapture.Count,
                "entering Play mode did not build the avatar, or the capture landed before the "
                + "domain reload that empties this registry");
            var animator = GameObject.Find(OnPlay)?.GetComponent<Animator>();
            Assert.IsNotNull(animator, "the avatar did not survive into Play mode");
            var built = BuildCapture.For(animator);
            Assert.IsNotNull(built, "the avatar in the Play mode scene is not the one captured");
            Assert.AreEqual(1, BuildCapture.ControllersFor(animator).Count);
            int synced = built.synced.Count;
            Assert.Greater(synced, 0);

            yield return new ExitPlayMode();

            Assert.AreEqual(1, BuildCapture.Count,
                "leaving Play mode emptied the registry, so a recording made in it can no longer "
                + "be told what it was matched against");
            Assert.AreEqual(synced, BuildCapture.For(GameObject.Find("DD Build On Play")
                ?.GetComponent<Animator>())?.synced.Count ?? -1,
                "the avatar came back out of Play mode as a different object than the registry "
                + "has, so the entry can no longer be found for it");
#else
            Assert.Ignore("NDMF and the VRChat avatars SDK are not both installed.");
            yield break;
#endif
        }

        // ---- the rig -------------------------------------------------------------

        /// <summary>An avatar with nothing on it but an Animator — enough to ask the capture
        /// about, and nothing a build would look at twice.</summary>
        Animator Avatar(string name)
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.HideAndDontSave;
            _made.Add(go);
            return go.AddComponent<Animator>();
        }

        /// <summary>Idle → On when the named parameter goes up, on a layer of the given name —
        /// in memory, or as a file when a path is given. The file is built after the asset
        /// exists rather than saved afterwards, which is what puts the state machine inside it
        /// instead of leaving it a dangling object.</summary>
        AnimatorController Controller(string layer, string parameter, string path = null)
        {
            AnimatorController controller;
            if (path == null)
            {
                controller = new AnimatorController();
                controller.name = "DD Build " + layer;
                controller.hideFlags = HideFlags.HideAndDontSave;
                _assets.Add(controller);
            }
            else
            {
                AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(new AnimatorController(), path);
                controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            }
            controller.AddLayer(layer);
            controller.AddParameter(parameter, AnimatorControllerParameterType.Bool);
            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            var on = machine.AddState("On");
            machine.defaultState = idle;
            var transition = idle.AddTransition(on);
            transition.hasExitTime = false;
            transition.duration = 0f;
            transition.AddCondition(AnimatorConditionMode.If, 0f, parameter);
            return controller;
        }

        internal struct Avatars
        {
            public GameObject root;
            public Animator animator;
            public AnimatorController fx;
            public AnimatorController gimmick;
        }

        static List<string> LayerNames(AnimatorController controller)
        {
            var names = new List<string>();
            foreach (var layer in controller.layers) names.Add(layer.name);
            return names;
        }

        static List<string> ParameterNames(AnimatorController controller)
        {
            var names = new List<string>();
            foreach (var parameter in controller.parameters) names.Add(parameter.name);
            return names;
        }

        static Component Find(GameObject root, string typeName)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
                if (component != null && component.GetType().FullName == typeName) return component;
            return null;
        }

        static Type ByName(string fullName)
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<MonoBehaviour>())
                if (type.FullName == fullName) return type;
            foreach (var type in TypeCache.GetTypesDerivedFrom<ScriptableObject>())
                if (type.FullName == fullName) return type;
            return null;
        }

        /// <summary>Skips a test that needs a package this project has not got, by name, so the
        /// two runs have the same number of tests in them.</summary>
        static class Skip
        {
            public static void WithoutModularAvatar()
            {
                if (ByName(MaParametersType) == null)
                    Assert.Ignore("Modular Avatar is not installed in this project.");
            }
        }

        /// <summary>
        /// An avatar of the shape this whole feature is about: a descriptor with an FX layer and
        /// an expression parameters asset of its own, and — unless asked otherwise — a child
        /// carrying a gimmick that merges a second layer in and declares an internal parameter
        /// that has to be renamed for it.
        /// </summary>
        Avatars Rig(string name, bool gimmick = true, bool keep = false, bool saved = false)
        {
            var made = new Avatars();
            made.root = new GameObject(name);
            // A build looks for avatars in the SCENE, and an object flagged not to be saved is
            // not carried into Play mode — so the probe that enters Play mode asks for an
            // ordinary object and cleans up after itself.
            if (!keep) made.root.hideFlags = HideFlags.DontSave;
            _made.Add(made.root);
            made.animator = made.root.AddComponent<Animator>();
            made.fx = Controller("Base", "Wave", saved ? OnPlayFx : null);

            var descriptorType = ByName(DescriptorType);
            if (descriptorType == null) return made;
            var descriptor = made.root.AddComponent(descriptorType);
            var so = new SerializedObject(descriptor);
            so.FindProperty("customizeAnimationLayers").boolValue = true;
            so.FindProperty("customExpressions").boolValue = true;
            so.FindProperty("expressionParameters").objectReferenceValue =
                Parameters("Wave", saved ? OnPlayParameters : null);
            Slot(so, "FX").FindPropertyRelative("animatorController").objectReferenceValue = made.fx;
            Slot(so, "FX").FindPropertyRelative("isDefault").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();

            // No gimmick without the package that provides one. The tests that are ABOUT the
            // gimmick skip themselves by name (see Skip); the ones that are not — the plain
            // capture, the Play mode probe — are worth running in a project that has NDMF and
            // nothing else, because that is where the ordering constraints are pointed at
            // plugins nobody has installed.
            if (!gimmick || ByName(MaMergeAnimatorType) == null
                || ByName(MaParametersType) == null) return made;
            made.gimmick = Controller("Gimmick", "Wag");
            var child = new GameObject("Gimmick");
            child.transform.SetParent(made.root.transform, false);

            var merge = child.AddComponent(ByName(MaMergeAnimatorType));
            var mergeSo = new SerializedObject(merge);
            mergeSo.FindProperty("animator").objectReferenceValue = made.gimmick;
            mergeSo.ApplyModifiedPropertiesWithoutUndo();

            var parameters = child.AddComponent(ByName(MaParametersType));
            var parametersSo = new SerializedObject(parameters);
            var list = parametersSo.FindProperty("parameters");
            list.arraySize = 1;
            var row = list.GetArrayElementAtIndex(0);
            row.FindPropertyRelative("nameOrPrefix").stringValue = "Wag";
            row.FindPropertyRelative("internalParameter").boolValue = true;
            row.FindPropertyRelative("isPrefix").boolValue = false;
            row.FindPropertyRelative("localOnly").boolValue = false;
            // NotSynced = 0, Int = 1, Float = 2, Bool = 3 — MA's own order, the one DaerD's
            // parameter store already reads.
            row.FindPropertyRelative("syncType").enumValueIndex = 3;
            parametersSo.ApplyModifiedPropertiesWithoutUndo();
            return made;
        }

        /// <summary>The playable layer slot of this kind, added if the descriptor came without
        /// one. The kind is matched against the serialized enum's own names rather than an
        /// index, for the same reason the capture reads it that way.</summary>
        static SerializedProperty Slot(SerializedObject descriptor, string kind)
        {
            var layers = descriptor.FindProperty("baseAnimationLayers");
            for (int i = 0; i < layers.arraySize; i++)
            {
                var element = layers.GetArrayElementAtIndex(i);
                var type = element.FindPropertyRelative("type");
                if (type != null && type.enumValueIndex >= 0
                    && type.enumNames[type.enumValueIndex] == kind) return element;
            }
            layers.arraySize++;
            var added = layers.GetArrayElementAtIndex(layers.arraySize - 1);
            var kinds = added.FindPropertyRelative("type");
            for (int i = 0; i < kinds.enumNames.Length; i++)
                if (kinds.enumNames[i] == kind) kinds.enumValueIndex = i;
            return added;
        }

        /// <summary>An expression parameters asset syncing one named bool, in memory or as a
        /// file — see <see cref="OnPlayFx"/> for why the difference matters.</summary>
        UnityEngine.Object Parameters(string name, string path = null)
        {
            var type = ByName(ExpressionParametersType);
            if (type == null) return null;
            var asset = ScriptableObject.CreateInstance(type);
            asset.name = "DD Build Params";
            // Left savable on purpose: a build instantiates this asset and writes the copy to
            // disk, and Instantiate carries the flags over — an asset marked not to be saved
            // would make the build fail on a detail of the test rig.
            if (path != null)
            {
                AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(asset, path);
            }
            else _assets.Add(asset);
            var so = new SerializedObject(asset);
            var list = so.FindProperty("parameters");
            list.arraySize = 1;
            var row = list.GetArrayElementAtIndex(0);
            row.FindPropertyRelative("name").stringValue = name;
            // Bool = 2 in the SDK's own value type enum, which DaerD's parameter store reads the
            // same way everywhere else.
            row.FindPropertyRelative("valueType").enumValueIndex = 2;
            // Absent on very old SDKs, where everything is synced — the same allowance DaerD's
            // own reader of this asset makes.
            var synced = row.FindPropertyRelative("networkSynced");
            if (synced != null) synced.boolValue = true;
            row.FindPropertyRelative("saved").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
            return asset;
        }

        /// <summary>What Modular Avatar renames an internal parameter to: the name, a dollar,
        /// and the first six bytes of the SHA-256 of the declaring component's path inside the
        /// avatar. Written out here rather than read off a run — see
        /// <see cref="TheRenameTableSaysWhatEachNameBecame"/> for why.</summary>
        static string Internal(string name, string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path));
                var text = new System.Text.StringBuilder();
                for (int i = 0; i < 6; i++) text.AppendFormat("{0:x2}", hash[i]);
                return name + "$" + text;
            }
        }

#if DAERD_NDMF && DAERD_VRC
        /// <summary>Builds the avatar the way pressing bake does, in place.</summary>
        static void Build(GameObject root) => nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(root);
#endif

        /// <summary>The build's temporary assets, which are real files in a folder NDMF owns.
        /// Cleared after every test that made any, so one test's leftovers cannot be found by
        /// the next.</summary>
        static void CleanBuildLeftovers()
        {
#if DAERD_NDMF && DAERD_VRC
            nadena.dev.ndmf.AvatarProcessor.CleanTemporaryAssets();
#endif
        }
    }
}

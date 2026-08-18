using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Select Sync pushes the selected State's clip into the Animation window on every graph
    /// rebuild — and a rebuild happens on <c>Undo.undoRedoPerformed</c>, which is global: it
    /// fires for an undo that had nothing to do with the controller, such as undoing an
    /// animation key. Unity's clip setter has no equality guard, so re-pushing the clip that is
    /// already there runs OnSelectionChanged → StopPreview → StopRecording. The user pressed
    /// Ctrl+Z to take back one key and got dropped out of Rec mode instead.
    ///
    /// The first test pins that Unity behaviour, because it is the whole reason the second one's
    /// guard exists; if a Unity upgrade ever makes the write harmless, the first test says so.
    /// </summary>
    public class RecordingSurvivesRebuildTests
    {
        const string Path = "Assets/DaerDRecordingRebuildTest.controller";
        const BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>Builds a saved controller with one clip-state, a scene Animator running it,
        /// and an Animation window recording that clip. Hands the pieces to <paramref name="body"/>.</summary>
        static void WhileRecording(Action<DaerDContext, AnimatorController, AnimatorState, EditorWindow> body)
        {
            // Showing / closing the Animation window logs a device error under -nographics.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            AssetDatabase.CreateAsset(new AnimatorController(), Path);
            GameObject rig = null;
            try
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(Path);
                controller.AddLayer("Base");
                var clip = new AnimationClip { name = "Recorded" };
                AssetDatabase.AddObjectToAsset(clip, controller);
                var state = controller.layers[0].stateMachine.AddState("S");
                state.motion = clip;
                AssetDatabase.SaveAssets();

                // The Animation window records against the selected GameObject, not the asset.
                rig = new GameObject("RecordingRig");
                rig.AddComponent<Animator>().runtimeAnimatorController = controller;
                Selection.activeGameObject = rig;

                var window = AnimationWindowAccess.EnsureOpen();
                Assume.That(window, Is.Not.Null, "no Animation window to record in");
                Assume.That(AnimationWindowAccess.TrySetClip(window, clip), Is.True, "clip never landed");
                Assume.That(StartRecording(window), Is.True, "this editor cannot record here");

                var context = new DaerDContext();
                context.SetController(controller);
                context.Select(state);
                body(context, controller, state, window);
            }
            finally
            {
                var open = AnimationWindowAccess.FindOpen();
                if (open != null)
                {
                    StopRecording(open);
                    open.Close();
                }
                if (rig != null) UnityEngine.Object.DestroyImmediate(rig);
                AssetDatabase.DeleteAsset(Path);
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        public void UnityStopsRecording_WhenTheClipItAlreadyHasIsWrittenAgain()
        {
            WhileRecording((context, controller, state, window) =>
            {
                var alreadyThere = AnimationWindowAccess.TryGetClip(window);
                Assume.That(alreadyThere, Is.Not.Null);

                AnimationWindowAccess.TrySetClip(window, alreadyThere);

                Assert.That(IsRecording(window), Is.False,
                    "Unity now tolerates a same-clip write — the guard in AnimationWindowSync " +
                    "can go, and this test with it");
            });
        }

        [Test]
        public void ARebuild_LeavesRecordingAlone()
        {
            WhileRecording((context, controller, state, window) =>
            {
                var sync = new AnimationWindowSync(context);
                sync.Start();
                try
                {
                    sync.SetEnabled(true);
                    Assert.That(IsRecording(window), Is.True,
                        "enabling Select Sync dropped the window out of Rec mode");

                    // What a global Ctrl+Z reaches Select Sync as: DaerDWindow.OnUndoRedo asks
                    // the graph to rebuild, and the finished rebuild raises this.
                    context.NotifyGraphRebuilt();

                    Assert.That(IsRecording(window), Is.True,
                        "a rebuild knocked the Animation window out of Rec mode");
                    Assert.That(AnimationWindowAccess.TryGetClip(window), Is.EqualTo(state.motion),
                        "the rebuild left the wrong clip in the Animation window");
                }
                finally
                {
                    sync.Stop();
                }
            });
        }

        // ---- Animation window recording, over reflection ---------------------

        static object ResolveState(EditorWindow window)
        {
            var animEditor = window.GetType().GetField("m_AnimEditor", All)?.GetValue(window);
            return animEditor?.GetType().GetProperty("state", All)?.GetValue(animEditor);
        }

        static bool StartRecording(EditorWindow window)
        {
            var state = ResolveState(window);
            state?.GetType().GetMethod("StartRecording", All, null, Type.EmptyTypes, null)?.Invoke(state, null);
            return IsRecording(window);
        }

        static void StopRecording(EditorWindow window)
        {
            var state = ResolveState(window);
            state?.GetType().GetMethod("StopPreview", All, null, Type.EmptyTypes, null)?.Invoke(state, null);
        }

        static bool IsRecording(EditorWindow window)
        {
            var state = ResolveState(window);
            return state?.GetType().GetProperty("recording", All)?.GetValue(state) is bool on && on;
        }
    }
}

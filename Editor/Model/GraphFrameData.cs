using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Persistent storage for graph frames (comment/group boxes drawn behind nodes) and memo
    /// notes. One hidden sub-asset per controller, created lazily on first use — controllers
    /// that never use frames or notes are left untouched.
    /// </summary>
    class GraphFrameData : ScriptableObject
    {
        [Serializable]
        public class Frame
        {
            public string title = "Frame";
            public Color color = new Color(0.32f, 0.45f, 0.60f, 1f);
            public Rect bounds;
            public bool moveNodesWithFrame = true;
            /// A locked frame cannot be moved, resized, renamed or deleted from the graph.
            public bool locked;
            public AnimatorStateMachine stateMachine;
        }

        /// <summary>A free-floating memo (sticky note) drawn among the nodes.</summary>
        [Serializable]
        public class Note
        {
            public string text = "Memo";
            public Color color = new Color(0.93f, 0.86f, 0.51f, 1f);
            public Rect bounds;
            public int fontSize = 12;
            public AnimatorStateMachine stateMachine;
        }

        public List<Frame> frames = new List<Frame>();
        public List<Note> notes = new List<Note>();

        /// <summary>
        /// This controller's designated placeholder clip. Assigned to new states on creation
        /// and offered by the analyzer as the fill-in fix for states (and blend tree slots)
        /// with no motion.
        /// </summary>
        public AnimationClip emptyClip;

        /// <summary>
        /// The parameter store this controller is explicitly associated with — a
        /// VRCExpressionParameters asset or an MA Parameters component. DaerD never guesses
        /// this from the scene on its own (gimmick controllers aren't wired to any avatar);
        /// the user assigns it, optionally via an explicit Detect action.
        /// </summary>
        public UnityEngine.Object parameterStore;

        /// <summary>The VRC Expressions Menu this controller is explicitly associated with.</summary>
        public UnityEngine.Object expressionsMenu;

        /// <summary>
        /// One generated async (round-robin) sync setup: the layer that hosts it plus the
        /// wizard inputs, so the wizard can re-open the setup and regenerate the layer in
        /// place instead of piling up new ones.
        /// </summary>
        [Serializable]
        public class AsyncSyncConfig
        {
            /// <summary>Root state machine of the generated layer (identifies the layer
            /// across renames and reorders).</summary>
            public AnimatorStateMachine layer;
            public string baseName = "Async";
            public int encoding;
            public float stepSeconds = 0.3f;
            /// <summary>Synced Float channels. 0 in data saved before the field existed —
            /// read it through <see cref="FloatChannelsOrDefault"/>.</summary>
            public int floatChannels = 1;
            public List<string> targets = new List<string>();
            /// <summary>Targets refreshed every other step of the cycle.</summary>
            public List<string> priorities = new List<string>();

            public int FloatChannelsOrDefault => floatChannels < 1 ? 1 : floatChannels;
        }

        public List<AsyncSyncConfig> asyncSyncs = new List<AsyncSyncConfig>();

        /// <summary>Live configs (entries whose layer was deleted are pruned).</summary>
        public List<AsyncSyncConfig> AsyncSyncs()
        {
            asyncSyncs.RemoveAll(config => config == null || config.layer == null);
            return new List<AsyncSyncConfig>(asyncSyncs);
        }

        /// <summary>Adds or replaces the config for its layer.</summary>
        public void SaveAsyncSync(AsyncSyncConfig config)
        {
            if (config == null || config.layer == null) return;
            Undo.RegisterCompleteObjectUndo(this, "Save Async Sync Config");
            asyncSyncs.RemoveAll(existing => existing == null || existing.layer == null
                || existing.layer == config.layer);
            asyncSyncs.Add(config);
            EditorUtility.SetDirty(this);
        }

        public static List<AsyncSyncConfig> GetAsyncSyncs(AnimatorController controller)
        {
            var data = Find(controller);
            return data != null ? data.AsyncSyncs() : new List<AsyncSyncConfig>();
        }

        public static void SaveAsyncSync(AnimatorController controller, AsyncSyncConfig config)
        {
            var data = GetOrCreate(controller);
            if (data != null) data.SaveAsyncSync(config);
        }

        public static AnimationClip GetEmptyClip(AnimatorController controller)
        {
            var data = Find(controller);
            return data != null ? data.emptyClip : null;
        }

        public static void SetEmptyClip(AnimatorController controller, AnimationClip clip)
        {
            // Clearing must not create the holder on controllers that never had one.
            var data = clip == null ? Find(controller) : GetOrCreate(controller);
            if (data == null || data.emptyClip == clip) return;
            Undo.RegisterCompleteObjectUndo(data, "Set Empty Clip");
            data.emptyClip = clip;
            EditorUtility.SetDirty(data);
        }

        public static UnityEngine.Object GetParameterStore(AnimatorController controller)
        {
            var data = Find(controller);
            return data != null ? data.parameterStore : null;
        }

        public static void SetParameterStore(AnimatorController controller, UnityEngine.Object store)
        {
            var data = store == null ? Find(controller) : GetOrCreate(controller);
            if (data == null || data.parameterStore == store) return;
            Undo.RegisterCompleteObjectUndo(data, "Set Parameter Store");
            data.parameterStore = store;
            EditorUtility.SetDirty(data);
        }

        public static UnityEngine.Object GetExpressionsMenu(AnimatorController controller)
        {
            var data = Find(controller);
            return data != null ? data.expressionsMenu : null;
        }

        public static void SetExpressionsMenu(AnimatorController controller, UnityEngine.Object menu)
        {
            var data = menu == null ? Find(controller) : GetOrCreate(controller);
            if (data == null || data.expressionsMenu == menu) return;
            Undo.RegisterCompleteObjectUndo(data, "Set Expressions Menu");
            data.expressionsMenu = menu;
            EditorUtility.SetDirty(data);
        }

        /// <summary>The frame holder already stored on the controller, or null when none exists.</summary>
        public static GraphFrameData Find(AnimatorController controller)
        {
            if (controller == null) return null;
            var path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is GraphFrameData data)
                    return data;
            return null;
        }

        /// <summary>Finds or creates the frame holder. In-memory controllers get a non-persisted instance.</summary>
        public static GraphFrameData GetOrCreate(AnimatorController controller)
        {
            var existing = Find(controller);
            if (existing != null) return existing;

            var data = CreateInstance<GraphFrameData>();
            data.name = "DaerD Frames";
            data.hideFlags = HideFlags.HideInHierarchy;
            var path = controller != null ? AssetDatabase.GetAssetPath(controller) : null;
            if (!string.IsNullOrEmpty(path))
            {
                AssetDatabase.AddObjectToAsset(data, controller);
                EditorUtility.SetDirty(controller);
            }
            return data;
        }

        /// <summary>The frames belonging to one state machine view (frames are per-graph, not per-layer).</summary>
        public List<Frame> FramesIn(AnimatorStateMachine sm)
        {
            var result = new List<Frame>();
            if (sm == null) return result;
            foreach (var frame in frames)
                if (frame != null && frame.stateMachine == sm)
                    result.Add(frame);
            return result;
        }

        public Frame AddFrame(AnimatorStateMachine sm, Rect bounds, string title = "Frame")
        {
            Undo.RegisterCompleteObjectUndo(this, "Create Frame");
            var frame = new Frame { title = title, bounds = bounds, stateMachine = sm };
            frames.Add(frame);
            EditorUtility.SetDirty(this);
            return frame;
        }

        public void RemoveFrame(Frame frame)
        {
            if (frame == null) return;
            Undo.RegisterCompleteObjectUndo(this, "Delete Frame");
            frames.Remove(frame);
            EditorUtility.SetDirty(this);
        }

        /// <summary>The notes belonging to one state machine view.</summary>
        public List<Note> NotesIn(AnimatorStateMachine sm)
        {
            var result = new List<Note>();
            if (sm == null) return result;
            foreach (var note in notes)
                if (note != null && note.stateMachine == sm)
                    result.Add(note);
            return result;
        }

        public Note AddNote(AnimatorStateMachine sm, Rect bounds)
        {
            Undo.RegisterCompleteObjectUndo(this, "Create Note");
            var note = new Note { bounds = bounds, stateMachine = sm };
            notes.Add(note);
            EditorUtility.SetDirty(this);
            return note;
        }

        public void RemoveNote(Note note)
        {
            if (note == null) return;
            Undo.RegisterCompleteObjectUndo(this, "Delete Note");
            notes.Remove(note);
            EditorUtility.SetDirty(this);
        }
    }
}

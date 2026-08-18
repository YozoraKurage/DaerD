using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Edit;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Creating states and sub-state machines, and packing / unpacking them. Everything here
    /// touches only the controller asset, so it runs without a graph view: <see cref="GraphSync"/>
    /// keeps the thin wrappers that follow up with a rebuild or a selection change.
    /// </summary>
    class NodeCommands
    {
        readonly DaerDContext _context;

        public NodeCommands(DaerDContext context)
        {
            _context = context;
        }

        public AnimatorState CreateState(Vector2 position, string mode)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null) return null;

            AnimatorState state;
            bool attachedSubAsset = false;
            using (new UndoScope("Create State"))
            {
                Undo.RegisterCompleteObjectUndo(sm, "Create State");
                state = sm.AddState(MakeUniqueName(sm, "New State"), new Vector3(position.x, position.y, 0f));
                DaerDSettings.ApplyStateDefaultsTo(state);

                // The controller's Empty clip fills the motion slot so a brand-new state never
                // plays as a WD-OFF "freeze" state; the modes below overwrite it with a real motion.
                var empty = GraphFrameData.GetEmptyClip(_context.Controller);
                if (empty != null)
                    state.motion = empty;

                if (mode == "state-clip" && Selection.activeObject is AnimationClip clip)
                {
                    state.motion = clip;
                    state.name = MakeUniqueName(sm, clip.name);
                }
                else if (mode == "state-blendtree")
                {
                    var blendTree = new BlendTree { name = "Blend Tree", hideFlags = HideFlags.HideInHierarchy };
                    var path = AssetDatabase.GetAssetPath(_context.Controller);
                    if (!string.IsNullOrEmpty(path))
                    {
                        AssetDatabase.AddObjectToAsset(blendTree, _context.Controller);
                        attachedSubAsset = true;
                    }
                    state.motion = blendTree;
                }
                EditorUtility.SetDirty(sm);
            }
            // Written and reimported once the state holds the tree, so the Project window lists
            // the new sub-asset instead of waiting for the next save.
            if (attachedSubAsset)
                DbtBuilder.CommitSubAssets(_context.Controller);
            return state;
        }

        /// <summary>
        /// Creates a state at <paramref name="position"/> using <paramref name="motion"/> as its
        /// motion. Used when an AnimationClip or BlendTree asset is dropped onto empty graph space.
        /// </summary>
        public AnimatorState CreateStateWithMotion(Vector2 position, Motion motion)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null || motion == null) return null;

            AnimatorState state;
            using (new UndoScope("Create State"))
            {
                Undo.RegisterCompleteObjectUndo(sm, "Create State");
                state = sm.AddState(MakeUniqueName(sm, motion.name), new Vector3(position.x, position.y, 0f));
                DaerDSettings.ApplyStateDefaultsTo(state);
                state.motion = motion;
                EditorUtility.SetDirty(sm);
            }
            return state;
        }

        /// <summary>Replaces a state's motion, used when an AnimationClip is dropped onto its node.</summary>
        public void AssignMotion(AnimatorState state, Motion motion)
        {
            if (state == null) return;
            using (new UndoScope("Assign Motion"))
            {
                Undo.RegisterCompleteObjectUndo(state, "Assign Motion");
                state.motion = motion;
                EditorUtility.SetDirty(state);
                if (_context.Controller != null)
                    EditorUtility.SetDirty(_context.Controller);
            }
        }

        public AnimatorStateMachine CreateSubStateMachine(Vector2 position)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null) return null;

            AnimatorStateMachine child;
            using (new UndoScope("Create Sub-State Machine"))
            {
                Undo.RegisterCompleteObjectUndo(sm, "Create Sub-State Machine");
                child = sm.AddStateMachine(MakeUniqueName(sm, "New Sub-State Machine"), new Vector3(position.x, position.y, 0f));
                EditorUtility.SetDirty(sm);
            }
            return child;
        }

        /// <summary>Returns false when there was nothing to do, so the caller can skip the rebuild.</summary>
        public bool SetDefaultState(AnimatorState state)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null || state == null) return false;
            Undo.RegisterCompleteObjectUndo(sm, "Set Default State");
            sm.defaultState = state;
            EditorUtility.SetDirty(sm);
            return true;
        }

        static string MakeUniqueName(AnimatorStateMachine sm, string baseName) =>
            StateDuplicator.MakeUniqueName(sm, baseName);

        // ---- pack / unpack -----------------------------------------------------

        /// <summary>The new sub-state machine holding the states, or null when nothing was packed.</summary>
        public AnimatorStateMachine PackStates(List<AnimatorState> states)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null || states == null || states.Count == 0) return null;
            return StatePacker.Pack(sm, states);
        }

        /// <summary>Returns false when there was nothing to do, so the caller can skip the rebuild.</summary>
        public bool UnpackSubStateMachine(AnimatorStateMachine child)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null || child == null) return false;
            var warnings = StatePacker.Unpack(sm, child, _context.Controller);
            foreach (var warning in warnings)
                Debug.LogWarning("DaerD: " + warning);
            return true;
        }
    }
}

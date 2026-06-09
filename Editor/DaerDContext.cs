using System;
using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Shared editing state for one open controller window. The graph and the side panels
    /// subscribe to the events here instead of referencing each other directly.
    /// </summary>
    class DaerDContext
    {
        public AnimatorController Controller { get; private set; }
        public int LayerIndex { get; private set; }

        /// Index 0 is the current layer's root state machine; the last element is the one shown.
        public readonly List<AnimatorStateMachine> StateMachinePath = new List<AnimatorStateMachine>();

        /// Empty unless the user double-clicked into a BlendTree state. Index 0 is the
        /// state's root BlendTree; further elements are nested blend trees drilled into.
        public readonly List<BlendTree> BlendTreePath = new List<BlendTree>();

        /// The state whose BlendTree we are viewing. Null when not in blend tree mode.
        public AnimatorState BlendTreeOriginState { get; private set; }

        public object Selection { get; private set; }

        public event Action ControllerChanged;
        public event Action LayerChanged;
        public event Action StateMachinePathChanged;
        public event Action BlendTreePathChanged;
        public event Action BlendTreeChanged;
        public event Action GraphStructureChanged;
        public event Action GraphRebuilt;
        public event Action ParametersChanged;
        public event Action LayersChanged;
        public event Action SelectionChanged;
        public event Action<object> FrameRequested;

        public bool HasController => Controller != null;

        public AnimatorControllerLayer CurrentLayer
        {
            get
            {
                if (Controller == null) return null;
                var layers = Controller.layers;
                return LayerIndex >= 0 && LayerIndex < layers.Length ? layers[LayerIndex] : null;
            }
        }

        public AnimatorStateMachine CurrentStateMachine =>
            StateMachinePath.Count > 0 ? StateMachinePath[StateMachinePath.Count - 1] : null;

        public BlendTree CurrentBlendTree =>
            BlendTreePath.Count > 0 ? BlendTreePath[BlendTreePath.Count - 1] : null;

        public bool IsViewingBlendTree => BlendTreePath.Count > 0;

        public void SetController(AnimatorController controller)
        {
            Controller = controller;
            LayerIndex = 0;
            Selection = null;
            ClearBlendTreePath();
            RebuildPath();
            ControllerChanged?.Invoke();
            LayerChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        public void SetLayer(int index)
        {
            if (Controller == null) return;
            var count = Controller.layers.Length;
            // With zero layers there is nothing to show, but the path / selection / listeners
            // must still be reset so the UI doesn't keep displaying a layer that no longer exists.
            LayerIndex = count == 0 ? 0 : Mathf.Clamp(index, 0, count - 1);
            Selection = null;
            ClearBlendTreePath();
            RebuildPath();
            LayerChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        public void EnterStateMachine(AnimatorStateMachine sm)
        {
            if (sm == null || StateMachinePath.Contains(sm)) return;
            StateMachinePath.Add(sm);
            Selection = null;
            ClearBlendTreePath();
            StateMachinePathChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        public void GoToBreadcrumb(int depth)
        {
            if (depth < 0 || depth >= StateMachinePath.Count - 1) return;
            StateMachinePath.RemoveRange(depth + 1, StateMachinePath.Count - depth - 1);
            Selection = null;
            ClearBlendTreePath();
            StateMachinePathChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        /// <summary>Enters tree-view mode for the BlendTree owned by <paramref name="state"/>.</summary>
        public void EnterBlendTree(AnimatorState state)
        {
            if (state == null || !(state.motion is BlendTree root)) return;
            BlendTreePath.Clear();
            BlendTreePath.Add(root);
            BlendTreeOriginState = state;
            Selection = root;
            BlendTreePathChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        /// <summary>Drills further into a nested BlendTree inside the current blend tree view.</summary>
        public void EnterNestedBlendTree(BlendTree nested)
        {
            if (nested == null || !IsViewingBlendTree || BlendTreePath.Contains(nested)) return;
            BlendTreePath.Add(nested);
            Selection = nested;
            BlendTreePathChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        /// <summary>Leaves blend tree mode entirely and returns to the state machine view.</summary>
        public void ExitBlendTree()
        {
            if (!IsViewingBlendTree) return;
            // Restore selection to the originating state so the inspector lands there
            // instead of going blank, matching how breadcrumb navigation feels elsewhere.
            var origin = BlendTreeOriginState;
            ClearBlendTreePath();
            Selection = origin;
            BlendTreePathChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        /// <summary>Pops the blend tree path back to the given depth (root = 0).</summary>
        public void GoToBlendTreeBreadcrumb(int depth)
        {
            if (depth < 0 || depth >= BlendTreePath.Count - 1) return;
            BlendTreePath.RemoveRange(depth + 1, BlendTreePath.Count - depth - 1);
            Selection = BlendTreePath[BlendTreePath.Count - 1];
            BlendTreePathChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        void ClearBlendTreePath()
        {
            BlendTreePath.Clear();
            BlendTreeOriginState = null;
        }

        public void Select(object target)
        {
            Selection = target;
            SelectionChanged?.Invoke();
        }

        public void NotifyGraphStructureChanged() => GraphStructureChanged?.Invoke();
        public void NotifyGraphRebuilt() => GraphRebuilt?.Invoke();
        public void NotifyParametersChanged() => ParametersChanged?.Invoke();

        /// <summary>Fires when a BlendTree's fields or children are edited from any panel.</summary>
        public void NotifyBlendTreeChanged() => BlendTreeChanged?.Invoke();

        /// <summary>Asks the current graph view to center on <paramref name="model"/>.</summary>
        public void RequestFrameOn(object model) => FrameRequested?.Invoke(model);

        public void NotifyLayersChanged()
        {
            if (Controller != null && LayerIndex >= Controller.layers.Length)
                LayerIndex = Mathf.Max(0, Controller.layers.Length - 1);
            ValidatePath();
            LayersChanged?.Invoke();
        }

        void RebuildPath()
        {
            StateMachinePath.Clear();
            var layer = CurrentLayer;
            if (layer != null && layer.stateMachine != null)
                StateMachinePath.Add(layer.stateMachine);
        }

        /// <summary>Drops state machines that were deleted from under us so navigation stays valid.</summary>
        public bool ValidatePath()
        {
            bool changed = false;
            for (int i = StateMachinePath.Count - 1; i >= 1; i--)
            {
                if (StateMachinePath[i] == null)
                {
                    StateMachinePath.RemoveAt(i);
                    changed = true;
                }
            }
            if (StateMachinePath.Count == 0)
            {
                RebuildPath();
                changed = true;
            }
            // If the originating state or any blend tree along the path has been
            // destroyed (e.g. by an Undo), pop back to a still-valid level.
            for (int i = BlendTreePath.Count - 1; i >= 0; i--)
            {
                if (BlendTreePath[i] == null)
                {
                    BlendTreePath.RemoveAt(i);
                    changed = true;
                }
            }
            if (BlendTreePath.Count > 0 && BlendTreeOriginState == null)
            {
                ClearBlendTreePath();
                changed = true;
            }
            return changed;
        }
    }
}

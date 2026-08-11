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

        /// <summary>
        /// The home screen — the controller-wide view that replaces the graph in the centre
        /// pane — is showing instead of a layer. A flag of its own rather than a sentinel
        /// <see cref="LayerIndex"/>: every piece of index arithmetic (clamping, the per-tab
        /// remembered layer, Shift scrolling) would otherwise need an exception, and the
        /// index is exactly what home has to keep — it is where leaving home returns to.
        /// </summary>
        public bool IsHomeSelected { get; private set; }

        /// <summary>The running Animator this controller can be read from while the editor
        /// plays. Shared state rather than the panels' own, because whoever polls it is not
        /// whoever displays it.</summary>
        public readonly LiveAnimator Live = new LiveAnimator();

        public event Action ControllerChanged;
        public event Action LayerChanged;
        public event Action StateMachinePathChanged;
        public event Action BlendTreePathChanged;
        public event Action BlendTreeChanged;
        public event Action GraphStructureChanged;
        public event Action GraphRebuilt;
        public event Action ParametersChanged;
        public event Action LayersChanged;
        /// <summary>Fires whenever <see cref="IsHomeSelected"/> flips either way, so the
        /// centre pane, the breadcrumb and the layer list's tint stay in step with it.</summary>
        public event Action HomeChanged;
        public event Action SelectionChanged;
        public event Action<object> FrameRequested;
        public event Action<object> GraphVisualsChanged;
        public event Action<GraphFrameData.Note> NoteEditRequested;

        /// <summary>
        /// The bulk repaints a <see cref="GraphVisualsChanged"/> notification can ask for, for the
        /// call sites that touch every state or every transition at once rather than one object.
        /// </summary>
        public enum GraphVisuals
        {
            /// Every state node's labels and badges (e.g. after a bulk Write Defaults).
            AllStateNodes,
            /// Every transition edge's badge and colour (e.g. after a mute / solo or condition edit).
            AllEdges,
        }

        /// <summary>
        /// Reads the states currently selected in the graph. A provider rather than a "selection
        /// changed" event on purpose: the inspector asks for this during its IMGUI repaint and must
        /// see the live selection. A pushed copy would go stale, because the graph restores its
        /// selection after every rebuild through
        /// <see cref="AnimatorGraphView.SetSelectionSilently"/>, which deliberately bypasses the
        /// notifying overrides. Registered by the graph view; null until then (and for a window
        /// whose graph was never built), which reads as "nothing selected".
        /// </summary>
        public Func<List<AnimatorState>> SelectedStatesProvider;

        /// <summary>
        /// Reads the transitions behind the current graph selection, one entry per selected edge.
        /// Model data only, so no graph element type has to cross into a panel. A provider for the
        /// same reason as <see cref="SelectedStatesProvider"/> — the transition inspector queries
        /// it live while repainting.
        /// </summary>
        public Func<List<TransitionGroup>> SelectedTransitionGroupsProvider;

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
            // Cleared without a HomeChanged of its own: everything that listens to it also
            // listens to ControllerChanged, which is a full refresh anyway.
            IsHomeSelected = false;
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
            // Picking a layer is also the gesture that leaves home; nothing else has to ask.
            bool wasHome = IsHomeSelected;
            IsHomeSelected = false;
            var count = Controller.layers.Length;
            // With zero layers there is nothing to show, but the path / selection / listeners
            // must still be reset so the UI doesn't keep displaying a layer that no longer exists.
            LayerIndex = count == 0 ? 0 : Mathf.Clamp(index, 0, count - 1);
            Selection = null;
            ClearBlendTreePath();
            RebuildPath();
            LayerChanged?.Invoke();
            SelectionChanged?.Invoke();
            // Last, so listeners see a settled layer before they are told home is over.
            if (wasHome) HomeChanged?.Invoke();
        }

        /// <summary>
        /// Shows the home screen. The layer stays selected underneath (<see cref="LayerIndex"/>
        /// is untouched) so any layer click — or a Shift scroll — comes straight back to it,
        /// but the drill path is popped to the layer root: home is not a place inside a
        /// sub-state machine, and returning should land where <see cref="SetLayer"/> lands.
        /// </summary>
        public void SelectHome()
        {
            if (Controller == null || IsHomeSelected) return;
            IsHomeSelected = true;
            Selection = null;
            ClearBlendTreePath();
            RebuildPath();
            HomeChanged?.Invoke();
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

        /// <summary>
        /// Jumps to a specific layer + state-machine drill path and selects (and frames)
        /// <paramref name="target"/>. Used by the parameter "find usages" pings and any other
        /// feature that locates something deep in the controller.
        ///
        /// When the layer or sub-state-machine path actually changes, the graph rebuild is
        /// scheduled asynchronously and the new transition edges don't exist yet — selecting
        /// the target immediately would just no-op (edges are stale) and the line wouldn't
        /// highlight blue. Instead, defer Select + Frame until the next
        /// <see cref="GraphRebuilt"/> notification so the new edge is in place by then.
        /// </summary>
        public void NavigateTo(int layerIndex, IList<AnimatorStateMachine> stateMachinePath, object target)
        {
            if (Controller == null) return;

            bool willRebuild = false;
            if (layerIndex >= 0 && layerIndex < Controller.layers.Length && layerIndex != LayerIndex)
            {
                SetLayer(layerIndex);
                willRebuild = true;
            }
            if (stateMachinePath != null && !PathEquals(stateMachinePath))
            {
                // Make the drill path exactly the requested one. Pop back to the layer root
                // first — the current view may be deeper than, or a sibling of, the target
                // (in which case just Entering the missing machines would corrupt the path).
                if (StateMachinePath.Count > 1)
                    GoToBreadcrumb(0);
                // Skip index 0 — that's the layer's root SM which SetLayer / RebuildPath
                // already lands us on.
                for (int i = 1; i < stateMachinePath.Count; i++)
                {
                    if (stateMachinePath[i] != null)
                        EnterStateMachine(stateMachinePath[i]);
                }
                willRebuild = true;
            }

            if (target == null) return;

            if (!willRebuild)
            {
                Select(target);
                RequestFrameOn(target);
                return;
            }

            // Defer until the rebuild fires so the new graph contains the edge/node we want
            // to highlight; otherwise Select runs against stale edges and the new transition
            // would stay un-blue until the user clicks somewhere else.
            Action handler = null;
            handler = () =>
            {
                GraphRebuilt -= handler;
                Select(target);
                RequestFrameOn(target);
            };
            GraphRebuilt += handler;
        }

        /// <summary>True when the current drill path already matches <paramref name="path"/> exactly.</summary>
        bool PathEquals(IList<AnimatorStateMachine> path)
        {
            if (path.Count != StateMachinePath.Count) return false;
            for (int i = 0; i < path.Count; i++)
                if (path[i] != StateMachinePath[i]) return false;
            return true;
        }

        public void NotifyGraphStructureChanged() => GraphStructureChanged?.Invoke();
        public void NotifyGraphRebuilt() => GraphRebuilt?.Invoke();
        public void NotifyParametersChanged() => ParametersChanged?.Invoke();

        /// <summary>Fires when a BlendTree's fields or children are edited from any panel.</summary>
        public void NotifyBlendTreeChanged() => BlendTreeChanged?.Invoke();

        /// <summary>Asks the current graph view to center on <paramref name="model"/>.</summary>
        public void RequestFrameOn(object model) => FrameRequested?.Invoke(model);

        /// <summary>
        /// Asks the graph to repaint what it draws for <paramref name="target"/>: an
        /// <see cref="AnimatorState"/>'s node, a <see cref="GraphFrameData.Frame"/> box, a
        /// <see cref="GraphFrameData.Note"/>, or one of the <see cref="GraphVisuals"/> bulk
        /// targets. Nothing structural changed, so this is a repaint and not a rebuild.
        /// </summary>
        public void NotifyGraphVisualsChanged(object target) => GraphVisualsChanged?.Invoke(target);

        /// <summary>Asks the graph to open the in-place text editor on <paramref name="note"/> —
        /// the same one a double-click (or F2) on the note starts.</summary>
        public void NotifyNoteEditRequested(GraphFrameData.Note note) => NoteEditRequested?.Invoke(note);

        /// <summary>The states selected in the graph; empty when no graph has registered a provider.</summary>
        public List<AnimatorState> GetSelectedStates() =>
            SelectedStatesProvider?.Invoke() ?? new List<AnimatorState>();

        /// <summary>The transitions of the currently selected edges, grouped per edge; empty when
        /// no graph has registered a provider.</summary>
        public List<TransitionGroup> GetSelectedTransitionGroups() =>
            SelectedTransitionGroupsProvider?.Invoke() ?? new List<TransitionGroup>();

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

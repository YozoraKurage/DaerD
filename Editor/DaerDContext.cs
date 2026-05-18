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

        public object Selection { get; private set; }

        public event Action ControllerChanged;
        public event Action LayerChanged;
        public event Action StateMachinePathChanged;
        public event Action GraphStructureChanged;
        public event Action GraphRebuilt;
        public event Action ParametersChanged;
        public event Action LayersChanged;
        public event Action SelectionChanged;

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

        public void SetController(AnimatorController controller)
        {
            Controller = controller;
            LayerIndex = 0;
            Selection = null;
            RebuildPath();
            ControllerChanged?.Invoke();
            LayerChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        public void SetLayer(int index)
        {
            if (Controller == null) return;
            var count = Controller.layers.Length;
            if (count == 0) { LayerIndex = 0; return; }
            LayerIndex = Mathf.Clamp(index, 0, count - 1);
            Selection = null;
            RebuildPath();
            LayerChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        public void EnterStateMachine(AnimatorStateMachine sm)
        {
            if (sm == null || StateMachinePath.Contains(sm)) return;
            StateMachinePath.Add(sm);
            Selection = null;
            StateMachinePathChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        public void GoToBreadcrumb(int depth)
        {
            if (depth < 0 || depth >= StateMachinePath.Count - 1) return;
            StateMachinePath.RemoveRange(depth + 1, StateMachinePath.Count - depth - 1);
            Selection = null;
            StateMachinePathChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        public void Select(object target)
        {
            Selection = target;
            SelectionChanged?.Invoke();
        }

        public void NotifyGraphStructureChanged() => GraphStructureChanged?.Invoke();
        public void NotifyGraphRebuilt() => GraphRebuilt?.Invoke();
        public void NotifyParametersChanged() => ParametersChanged?.Invoke();

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
            return changed;
        }
    }
}

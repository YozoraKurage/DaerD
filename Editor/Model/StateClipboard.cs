using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// In-memory clipboard for whole states. Copies state settings, behaviours and any
    /// transitions whose source and destination are both inside the copied set. Nothing here
    /// refers to the source state machine, so a copy pastes into any layer — and into any
    /// controller, where the parameters the states reference are recreated if missing.
    /// Cleared on domain reload.
    /// </summary>
    static class StateClipboard
    {
        class Entry
        {
            public string name;
            public Motion motion;
            public float speed = 1f;
            public float cycleOffset;
            public bool mirror;
            public bool ikOnFeet;
            public bool writeDefaultValues = true;
            public string tag = string.Empty;
            public bool speedParameterActive;
            public string speedParameter = string.Empty;
            public bool mirrorParameterActive;
            public string mirrorParameter = string.Empty;
            public bool cycleOffsetParameterActive;
            public string cycleOffsetParameter = string.Empty;
            public bool timeParameterActive;
            public string timeParameter = string.Empty;
            public Vector2 position;
            /// Detached deep copies, so the paste still works after the source state (or its
            /// whole controller) is gone.
            public readonly List<StateMachineBehaviour> behaviours = new List<StateMachineBehaviour>();
        }

        class InternalTransition
        {
            public int from;
            public int to;
            public TransitionClipboard.Snapshot snapshot;
        }

        /// <summary>
        /// A transition between a copied state and one of the state machine's own singletons.
        /// Entry / Exit / AnyState exist once per state machine, so the paste hooks the copies up
        /// to the destination's own nodes instead of trying to reproduce anything.
        /// </summary>
        enum SpecialLink { ToExit, FromEntry, FromAnyState }

        class SpecialTransition
        {
            public int state;              // index into _entries
            public SpecialLink link;
            public TransitionClipboard.Snapshot snapshot;
        }

        /// <summary>A parameter the copied states reference, carried along so a paste into a
        /// controller that doesn't have it can recreate it.</summary>
        class ParameterEntry
        {
            public string name;
            public AnimatorControllerParameterType type;
            public float defaultFloat;
            public int defaultInt;
            public bool defaultBool;
        }

        static readonly List<Entry> _entries = new List<Entry>();
        static readonly List<InternalTransition> _transitions = new List<InternalTransition>();
        static readonly List<SpecialTransition> _specialTransitions = new List<SpecialTransition>();
        static readonly List<ParameterEntry> _parameters = new List<ParameterEntry>();
        static Vector2 _anchor;

        public static bool HasData => _entries.Count > 0;
        public static int Count => _entries.Count;

        /// <summary>Top-left corner the copy was taken from — paste there to land the states in
        /// the same spot of another layer.</summary>
        public static Vector2 Anchor => _anchor;

        public static void Clear()
        {
            foreach (var entry in _entries)
                foreach (var behaviour in entry.behaviours)
                    if (behaviour != null)
                        UnityEngine.Object.DestroyImmediate(behaviour);
            _entries.Clear();
            _transitions.Clear();
            _specialTransitions.Clear();
            _parameters.Clear();
        }

        /// <summary>
        /// Replaces the clipboard with the given states. <paramref name="anchorOverride"/> lets a
        /// caller that copies frames or notes in the same gesture share one anchor across the
        /// clipboards, so the group's internal layout survives the paste.
        /// <paramref name="sourceController"/> is what the referenced parameters are read from —
        /// pass it so a paste into another controller can recreate the ones it lacks.
        /// <paramref name="sourceStateMachine"/> is the machine the states live in; it is what
        /// the Entry → state and AnyState → state transitions are read from (a state only knows
        /// its own outgoing ones).
        /// </summary>
        public static void Copy(IList<AnimatorState> sourceStates, Func<AnimatorState, Vector2> positionOf,
            Vector2? anchorOverride = null, AnimatorController sourceController = null,
            AnimatorStateMachine sourceStateMachine = null)
        {
            Clear();
            if (sourceStates == null) return;

            var states = new List<AnimatorState>();
            foreach (var s in sourceStates)
                if (s != null) states.Add(s);
            if (states.Count == 0) return;

            var index = new Dictionary<AnimatorState, int>();
            for (int i = 0; i < states.Count; i++)
                index[states[i]] = i;

            _anchor = new Vector2(float.MaxValue, float.MaxValue);
            foreach (var s in states)
            {
                var pos = positionOf(s);
                _anchor = new Vector2(Mathf.Min(_anchor.x, pos.x), Mathf.Min(_anchor.y, pos.y));
                var entry = new Entry
                {
                    name = s.name,
                    motion = s.motion,
                    speed = s.speed,
                    cycleOffset = s.cycleOffset,
                    mirror = s.mirror,
                    ikOnFeet = s.iKOnFeet,
                    writeDefaultValues = s.writeDefaultValues,
                    tag = s.tag,
                    speedParameterActive = s.speedParameterActive,
                    speedParameter = s.speedParameter,
                    mirrorParameterActive = s.mirrorParameterActive,
                    mirrorParameter = s.mirrorParameter,
                    cycleOffsetParameterActive = s.cycleOffsetParameterActive,
                    cycleOffsetParameter = s.cycleOffsetParameter,
                    timeParameterActive = s.timeParameterActive,
                    timeParameter = s.timeParameter,
                    position = pos,
                };

                // Behaviours are Unity objects owned by the source controller — copy them into
                // detached instances so drivers, tracking control and the rest survive a paste
                // into another layer (and outlive the original state).
                foreach (var behaviour in s.behaviours)
                {
                    if (behaviour == null) continue;
                    var copy = UnityEngine.Object.Instantiate(behaviour);
                    copy.name = behaviour.name;
                    copy.hideFlags = HideFlags.HideAndDontSave;
                    entry.behaviours.Add(copy);
                }

                _entries.Add(entry);
            }

            if (anchorOverride.HasValue) _anchor = anchorOverride.Value;

            foreach (var s in states)
                foreach (var t in s.transitions)
                {
                    // state → Exit rides along: Exit is a per-state-machine singleton, so the
                    // copy just points at the destination machine's own Exit node. Transitions
                    // leaving the copied set (or aimed at a sub-state machine) are still dropped —
                    // their other end doesn't exist where the paste lands.
                    if (t.isExit)
                        _specialTransitions.Add(new SpecialTransition
                        {
                            state = index[s],
                            link = SpecialLink.ToExit,
                            snapshot = TransitionClipboard.Capture(t),
                        });
                    else if (t.destinationState != null && index.TryGetValue(t.destinationState, out int to))
                        _transitions.Add(new InternalTransition
                        {
                            from = index[s],
                            to = to,
                            snapshot = TransitionClipboard.Capture(t),
                        });
                }

            // Entry → state and AnyState → state are owned by the state machine, not by the
            // state, so they only travel when the caller says which machine to read.
            if (sourceStateMachine != null)
            {
                foreach (var t in sourceStateMachine.entryTransitions)
                    if (t != null && t.destinationState != null
                        && index.TryGetValue(t.destinationState, out int destination))
                        _specialTransitions.Add(new SpecialTransition
                        {
                            state = destination,
                            link = SpecialLink.FromEntry,
                            snapshot = TransitionClipboard.Capture(t),
                        });

                foreach (var t in sourceStateMachine.anyStateTransitions)
                    if (t != null && t.destinationState != null
                        && index.TryGetValue(t.destinationState, out int destination))
                        _specialTransitions.Add(new SpecialTransition
                        {
                            state = destination,
                            link = SpecialLink.FromAnyState,
                            snapshot = TransitionClipboard.Capture(t),
                        });
            }

            if (sourceController == null) return;
            var referenced = LayerClipboard.CollectParameterNames(states);
            // Entry / AnyState conditions hang off the state machine, so the walk over the states
            // above never saw them — a parameter used only there would go missing on a paste into
            // another controller.
            foreach (var special in _specialTransitions)
                foreach (var condition in special.snapshot.conditions)
                    if (!string.IsNullOrEmpty(condition.parameter))
                        referenced.Add(condition.parameter);

            foreach (var parameter in sourceController.parameters)
                if (referenced.Contains(parameter.name))
                    _parameters.Add(new ParameterEntry
                    {
                        name = parameter.name,
                        type = parameter.type,
                        defaultFloat = parameter.defaultFloat,
                        defaultInt = parameter.defaultInt,
                        defaultBool = parameter.defaultBool,
                    });
        }

        /// <summary>
        /// Recreates the copied states in <paramref name="target"/> — any layer, any controller.
        /// Pass <paramref name="destinationController"/> to have the parameters the states
        /// reference added when the destination is missing them.
        /// </summary>
        public static List<AnimatorState> Paste(AnimatorStateMachine target, Vector2 pasteAt,
            AnimatorController destinationController = null)
        {
            var created = new List<AnimatorState>();
            if (target == null || _entries.Count == 0) return created;

            using (new UndoScope("Paste States"))
            {
                Undo.RegisterCompleteObjectUndo(target, "Paste States");

                if (destinationController != null && _parameters.Count > 0)
                {
                    Undo.RegisterCompleteObjectUndo(destinationController, "Paste States");
                    foreach (var parameter in _parameters)
                        if (DbtBuilder.FindParameter(destinationController, parameter.name) == null)
                            destinationController.AddParameter(new AnimatorControllerParameter
                            {
                                name = parameter.name,
                                type = parameter.type,
                                defaultFloat = parameter.defaultFloat,
                                defaultInt = parameter.defaultInt,
                                defaultBool = parameter.defaultBool,
                            });
                }

                foreach (var e in _entries)
                {
                    var offset = e.position - _anchor;
                    // Keep the name unless the destination already uses it. Pasting into another
                    // layer normally keeps the states named exactly as they were; only a real
                    // clash gets a suffix, because two states sharing a name inside one machine
                    // makes the graph (and Animator.Play by name) ambiguous.
                    var state = target.AddState(StateDuplicator.MakeUniqueName(target, e.name),
                        new Vector3(pasteAt.x + offset.x, pasteAt.y + offset.y, 0f));
                    state.motion = e.motion;
                    state.speed = e.speed;
                    state.cycleOffset = e.cycleOffset;
                    state.mirror = e.mirror;
                    state.iKOnFeet = e.ikOnFeet;
                    state.writeDefaultValues = e.writeDefaultValues;
                    state.tag = e.tag;
                    state.speedParameterActive = e.speedParameterActive;
                    state.speedParameter = e.speedParameter;
                    state.mirrorParameterActive = e.mirrorParameterActive;
                    state.mirrorParameter = e.mirrorParameter;
                    state.cycleOffsetParameterActive = e.cycleOffsetParameterActive;
                    state.cycleOffsetParameter = e.cycleOffsetParameter;
                    state.timeParameterActive = e.timeParameterActive;
                    state.timeParameter = e.timeParameter;

                    foreach (var behaviour in e.behaviours)
                    {
                        if (behaviour == null) continue;
                        var added = state.AddStateMachineBehaviour(behaviour.GetType());
                        if (added == null) continue;
                        EditorUtility.CopySerialized(behaviour, added);
                        added.name = behaviour.name;
                        VrcBehaviours.MarkAsSubAsset(added);
                    }

                    created.Add(state);
                }

                foreach (var it in _transitions)
                {
                    if (it.from >= created.Count || it.to >= created.Count) continue;
                    var transition = created[it.from].AddTransition(created[it.to]);
                    TransitionClipboard.Apply(transition, it.snapshot);
                }

                foreach (var special in _specialTransitions)
                {
                    if (special.state >= created.Count) continue;
                    var state = created[special.state];
                    AnimatorTransitionBase transition;
                    switch (special.link)
                    {
                        case SpecialLink.ToExit: transition = state.AddExitTransition(); break;
                        case SpecialLink.FromEntry: transition = target.AddEntryTransition(state); break;
                        case SpecialLink.FromAnyState: transition = target.AddAnyStateTransition(state); break;
                        default: continue;
                    }
                    if (transition != null)
                        TransitionClipboard.Apply(transition, special.snapshot);
                }

                // A blend tree is a sub-asset of the controller it was authored in. Pasting into
                // another layer of the same controller can keep sharing it (like Ctrl+D does),
                // but a paste into a different controller has to take its own copy.
                if (destinationController != null)
                {
                    string destinationPath = AssetDatabase.GetAssetPath(destinationController);
                    var foreignTrees = new List<AnimatorState>();
                    foreach (var state in created)
                        if (state.motion is BlendTree tree
                            && AssetDatabase.GetAssetPath(tree) != destinationPath)
                            foreignTrees.Add(state);
                    if (foreignTrees.Count > 0)
                        LayerClipboard.DeepCopyBlendTrees(destinationController, foreignTrees);
                }

                EditorUtility.SetDirty(target);
            }
            return created;
        }
    }
}

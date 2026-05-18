using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// In-memory clipboard for whole states. Copies state settings plus any transitions
    /// whose source and destination are both inside the copied set. Cleared on domain reload.
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
        }

        class InternalTransition
        {
            public int from;
            public int to;
            public TransitionClipboard.Snapshot snapshot;
        }

        static readonly List<Entry> _entries = new List<Entry>();
        static readonly List<InternalTransition> _transitions = new List<InternalTransition>();
        static Vector2 _anchor;

        public static bool HasData => _entries.Count > 0;
        public static int Count => _entries.Count;

        public static void Copy(IList<AnimatorState> states, Func<AnimatorState, Vector2> positionOf)
        {
            _entries.Clear();
            _transitions.Clear();
            if (states == null || states.Count == 0) return;

            var index = new Dictionary<AnimatorState, int>();
            for (int i = 0; i < states.Count; i++)
                index[states[i]] = i;

            _anchor = new Vector2(float.MaxValue, float.MaxValue);
            foreach (var s in states)
            {
                var pos = positionOf(s);
                _anchor = new Vector2(Mathf.Min(_anchor.x, pos.x), Mathf.Min(_anchor.y, pos.y));
                _entries.Add(new Entry
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
                });
            }

            foreach (var s in states)
                foreach (var t in s.transitions)
                    if (t.destinationState != null && index.TryGetValue(t.destinationState, out int to))
                        _transitions.Add(new InternalTransition
                        {
                            from = index[s],
                            to = to,
                            snapshot = TransitionClipboard.Capture(t),
                        });
        }

        public static List<AnimatorState> Paste(AnimatorStateMachine target, Vector2 pasteAt)
        {
            var created = new List<AnimatorState>();
            if (target == null || _entries.Count == 0) return created;

            using (new UndoScope("Paste States"))
            {
                Undo.RegisterCompleteObjectUndo(target, "Paste States");
                foreach (var e in _entries)
                {
                    var offset = e.position - _anchor;
                    var state = target.AddState(e.name, new Vector3(pasteAt.x + offset.x, pasteAt.y + offset.y, 0f));
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
                    created.Add(state);
                }

                foreach (var it in _transitions)
                {
                    if (it.from >= created.Count || it.to >= created.Count) continue;
                    var transition = created[it.from].AddTransition(created[it.to]);
                    TransitionClipboard.Apply(transition, it.snapshot);
                }

                EditorUtility.SetDirty(target);
            }
            return created;
        }
    }
}

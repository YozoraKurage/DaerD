using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Session-scoped clipboard for transition settings. Survives domain reloads via
    /// <see cref="SessionState"/> so a copy stays usable after a script recompile.
    /// </summary>
    static class TransitionClipboard
    {
        const string SessionKey = "Yozolab.DaerD.TransitionClipboard";

        [Serializable]
        public class ConditionData
        {
            public AnimatorConditionMode mode;
            public string parameter;
            public float threshold;
        }

        [Serializable]
        public class Snapshot
        {
            public bool isStateTransition;
            public bool hasExitTime;
            public float exitTime = 0.75f;
            public bool hasFixedDuration = true;
            public float duration = 0.25f;
            public float offset;
            public TransitionInterruptionSource interruptionSource = TransitionInterruptionSource.None;
            public bool orderedInterruption = true;
            public bool canTransitionToSelf;
            public bool mute;
            public bool solo;
            public bool isExit;
            public string sourceLabel;
            public List<ConditionData> conditions = new List<ConditionData>();
        }

        [Serializable]
        class SnapshotList { public List<Snapshot> items = new List<Snapshot>(); }

        static SnapshotList _data;

        static SnapshotList Data
        {
            get
            {
                if (_data == null)
                {
                    var json = SessionState.GetString(SessionKey, string.Empty);
                    _data = string.IsNullOrEmpty(json)
                        ? new SnapshotList()
                        : (JsonUtility.FromJson<SnapshotList>(json) ?? new SnapshotList());
                }
                return _data;
            }
        }

        static void Save() => SessionState.SetString(SessionKey, JsonUtility.ToJson(Data));

        public static bool HasData => Data.items.Count > 0;
        public static int Count => Data.items.Count;
        public static IReadOnlyList<Snapshot> Snapshots => Data.items;

        public static void Copy(IEnumerable<AnimatorTransitionBase> transitions)
        {
            Data.items.Clear();
            foreach (var t in transitions)
                if (t != null) Data.items.Add(Capture(t));
            Save();
        }

        public static Snapshot Capture(AnimatorTransitionBase transition)
        {
            var snap = new Snapshot { sourceLabel = ParameterConverter.DescribeTransition(transition) };
            foreach (var c in transition.conditions)
                snap.conditions.Add(new ConditionData { mode = c.mode, parameter = c.parameter, threshold = c.threshold });
            snap.isExit = transition.isExit;
            snap.mute = transition.mute;
            snap.solo = transition.solo;
            if (transition is AnimatorStateTransition st)
            {
                snap.isStateTransition = true;
                snap.hasExitTime = st.hasExitTime;
                snap.exitTime = st.exitTime;
                snap.hasFixedDuration = st.hasFixedDuration;
                snap.duration = st.duration;
                snap.offset = st.offset;
                snap.interruptionSource = st.interruptionSource;
                snap.orderedInterruption = st.orderedInterruption;
                snap.canTransitionToSelf = st.canTransitionToSelf;
            }
            return snap;
        }

        /// <summary>Writes a snapshot's settings onto an existing transition; its destination is kept.</summary>
        public static void Apply(AnimatorTransitionBase transition, Snapshot snap, bool includeConditions = true)
        {
            if (transition == null || snap == null) return;
            Undo.RegisterCompleteObjectUndo(transition, "Paste Transition Settings");
            if (includeConditions)
                SetConditions(transition, snap.conditions);
            transition.mute = snap.mute;
            transition.solo = snap.solo;
            if (transition is AnimatorStateTransition st)
            {
                st.hasExitTime = snap.hasExitTime;
                st.exitTime = snap.exitTime;
                st.hasFixedDuration = snap.hasFixedDuration;
                st.duration = snap.duration;
                st.offset = snap.offset;
                st.interruptionSource = snap.interruptionSource;
                st.orderedInterruption = snap.orderedInterruption;
                st.canTransitionToSelf = snap.canTransitionToSelf;
            }
            EditorUtility.SetDirty(transition);
        }

        /// <summary>Replaces a transition's whole condition list (AnimatorCondition has no public constructor).</summary>
        public static void SetConditions(AnimatorTransitionBase transition, IList<ConditionData> conditions)
        {
            var existing = transition.conditions;
            for (int i = existing.Length - 1; i >= 0; i--)
                transition.RemoveCondition(existing[i]);
            foreach (var c in conditions)
                transition.AddCondition(c.mode, c.threshold, c.parameter);
        }

        public static void Clear()
        {
            Data.items.Clear();
            Save();
        }
    }
}

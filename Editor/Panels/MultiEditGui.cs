using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Rows that edit one field across a whole selection: each shows the shared value (or Unity's
    /// "mixed value" placeholder when the items disagree) and writes back to every item in a
    /// single undo step. Used by the multi-state and multi-transition editors.
    /// </summary>
    static class MultiEditGui
    {
        public static void Bool<T>(string label, List<T> items, Func<T, bool> getter, Action<T, bool> setter,
            Action afterApply = null, string undoName = "Edit Transitions",
            Action<T> postApply = null) where T : UnityEngine.Object
        {
            Edit<T, bool>(items, getter, setter, (a, b) => a == b,
                first => EditorGUILayout.Toggle(label, first),
                undoName, undoName, postApply, afterApply);
        }

        public static void Float<T>(string label, List<T> items, Func<T, float> getter, Action<T, float> setter,
            string undoName = "Edit Transitions") where T : UnityEngine.Object
        {
            Edit<T, float>(items, getter, setter, Mathf.Approximately,
                first => EditorGUILayout.FloatField(label, first),
                undoName, undoName, null, null);
        }

        public static void Text<T>(string label, List<T> items, Func<T, string> getter, Action<T, string> setter,
            string undoName = "Edit States") where T : UnityEngine.Object
        {
            // Null and "" are the same value in a text field, so the reading is normalized before
            // both the comparison and the draw — an unset tag must not read as "mixed".
            Edit<T, string>(items, item => getter(item) ?? string.Empty, setter, (a, b) => a == b,
                first => EditorGUILayout.DelayedTextField(label, first),
                undoName, undoName, null, null);
        }

        public static void ObjectField<TOwner, TObject>(string label, List<TOwner> items,
            Func<TOwner, TObject> getter, Action<TOwner, TObject> setter,
            string undoName = "Edit States", Action<TOwner> postApply = null)
            where TOwner : UnityEngine.Object
            where TObject : UnityEngine.Object
        {
            Edit<TOwner, TObject>(items, getter, setter, (a, b) => ReferenceEquals(a, b),
                first => (TObject)EditorGUILayout.ObjectField(label, first, typeof(TObject), false),
                undoName, undoName, postApply, null);
        }

        /// <summary>Interruption source of the selected transitions. The undo group is named in
        /// the plural and the per-item entry in the singular, as this row has always recorded it.</summary>
        public static void Interruption(List<AnimatorStateTransition> items)
        {
            Edit<AnimatorStateTransition, TransitionInterruptionSource>(items,
                x => x.interruptionSource, (x, v) => x.interruptionSource = v, (a, b) => a == b,
                first => (TransitionInterruptionSource)EditorGUILayout.EnumPopup(L.Tr("Interruption"), first),
                "Edit Transitions", "Edit Transition", null, null);
        }

        /// <summary>
        /// The shared skeleton: read the first item's value, mark the control as mixed when the
        /// others disagree, and on an edit write the new value to every item inside one undo
        /// group. <paramref name="postApply"/> runs per item (node repaints), <paramref
        /// name="afterApply"/> once for the whole edit.
        /// </summary>
        static void Edit<TOwner, TValue>(List<TOwner> items, Func<TOwner, TValue> getter,
            Action<TOwner, TValue> setter, Func<TValue, TValue, bool> same, Func<TValue, TValue> draw,
            string undoName, string recordName, Action<TOwner> postApply, Action afterApply)
            where TOwner : UnityEngine.Object
        {
            if (items.Count == 0) return;
            TValue first = getter(items[0]);
            bool mixed = false;
            foreach (var item in items)
                if (!same(getter(item), first)) { mixed = true; break; }

            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            TValue value = draw(first);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                using (new UndoScope(undoName))
                    foreach (var item in items)
                    {
                        Undo.RegisterCompleteObjectUndo(item, recordName);
                        setter(item, value);
                        EditorUtility.SetDirty(item);
                        postApply?.Invoke(item);
                    }
                afterApply?.Invoke();
            }
        }
    }
}

using System.Collections.Generic;

namespace Yozolab.DaerD
{
    /// <summary>
    /// How a set of selected items moves within an ordered list — the evaluation order of one
    /// source's transitions, for the inspector's drag and its Evaluation Order menu. Pure list
    /// arithmetic, so the rules can be pinned without a controller; writing the result back is
    /// <see cref="EdgeCommands"/>'s.
    ///
    /// The rules are the ones list editors have taught everybody: a selection moves as one
    /// block and keeps its own relative order, and a step moves each selected item past the
    /// nearest unselected neighbour — so a selection already at the top stops there instead of
    /// wrapping or reshuffling itself.
    /// </summary>
    static class TransitionOrder
    {
        internal enum Step { Up, Down, First, Last }

        /// <summary>
        /// The list after the row at <paramref name="from"/> is dragged to <paramref name="to"/>.
        /// When that row is one of two or more selected, the whole selection comes along: the
        /// dragged row lands where a single drag would put it and the other selected rows close
        /// up around it, in their original order. Otherwise it is a single-row move.
        /// </summary>
        internal static List<T> Drag<T>(IList<T> list, ICollection<T> selected, int from, int to)
        {
            var result = new List<T>(list);
            if (from < 0 || to < 0 || from >= list.Count || to >= list.Count) return result;

            var dragged = list[from];
            result.RemoveAt(from);
            result.Insert(to, dragged);
            if (selected == null || !selected.Contains(dragged) || CountIn(list, selected) < 2)
                return result;

            // Where the block goes: after as many unselected rows as precede the dragged one
            // once it has moved.
            int insertAt = 0;
            foreach (var item in result)
            {
                if (ReferenceEquals(item, dragged)) break;
                if (!selected.Contains(item)) insertAt++;
            }
            var block = new List<T>();
            var rest = new List<T>();
            foreach (var item in list)
                (selected.Contains(item) ? block : rest).Add(item);
            rest.InsertRange(insertAt, block);
            return rest;
        }

        /// <summary>The list after the selected items take one <paramref name="step"/>.</summary>
        internal static List<T> Move<T>(IList<T> list, ICollection<T> selected, Step step)
        {
            var result = new List<T>(list);
            if (selected == null) return result;
            switch (step)
            {
                case Step.Up:
                    for (int i = 1; i < result.Count; i++)
                        if (selected.Contains(result[i]) && !selected.Contains(result[i - 1]))
                            Swap(result, i, i - 1);
                    break;
                case Step.Down:
                    for (int i = result.Count - 2; i >= 0; i--)
                        if (selected.Contains(result[i]) && !selected.Contains(result[i + 1]))
                            Swap(result, i, i + 1);
                    break;
                case Step.First:
                case Step.Last:
                    var block = new List<T>();
                    var rest = new List<T>();
                    foreach (var item in list)
                        (selected.Contains(item) ? block : rest).Add(item);
                    result.Clear();
                    if (step == Step.First) { result.AddRange(block); result.AddRange(rest); }
                    else { result.AddRange(rest); result.AddRange(block); }
                    break;
            }
            return result;
        }

        /// <summary>Whether two orders differ — a move that changes nothing writes nothing.</summary>
        internal static bool Differs<T>(IList<T> a, IList<T> b)
        {
            if (a.Count != b.Count) return true;
            for (int i = 0; i < a.Count; i++)
                if (!ReferenceEquals(a[i], b[i])) return true;
            return false;
        }

        static int CountIn<T>(IList<T> list, ICollection<T> selected)
        {
            int n = 0;
            foreach (var item in list) if (selected.Contains(item)) n++;
            return n;
        }

        static void Swap<T>(List<T> list, int a, int b)
        {
            var t = list[a];
            list[a] = list[b];
            list[b] = t;
        }
    }
}

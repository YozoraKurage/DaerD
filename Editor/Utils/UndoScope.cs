using System;
using UnityEditor;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Groups every Undo entry recorded inside the scope into a single, named undo step.
    /// Usage: <c>using (new UndoScope("Add State")) { ... }</c>
    /// </summary>
    readonly struct UndoScope : IDisposable
    {
        readonly int _group;

        public UndoScope(string name)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(name);
            _group = Undo.GetCurrentGroup();
        }

        public void Dispose() => Undo.CollapseUndoOperations(_group);
    }
}

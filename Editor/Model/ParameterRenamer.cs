using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Renames a parameter and cascades the new name into every condition, blend tree
    /// (including Direct child weights and synced-layer override trees), state field and
    /// VRC Parameter Driver entry that referenced it — something Unity's built-in editor
    /// does not do.
    /// </summary>
    static class ParameterRenamer
    {
        public static bool Rename(AnimatorController controller, string oldName, string newName)
        {
            if (controller == null || string.IsNullOrEmpty(newName) || oldName == newName) return false;
            foreach (var p in controller.parameters)
                if (p.name == newName) return false; // target name already taken

            using (new UndoScope("Rename Parameter"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "Rename Parameter");
                RewriteReferences(controller, oldName, newName);

                var parameters = controller.parameters;
                foreach (var p in parameters)
                    if (p.name == oldName) { p.name = newName; break; }
                controller.parameters = parameters;
                EditorUtility.SetDirty(controller);
            }
            return true;
        }

        /// <summary>
        /// Redirects every reference from <paramref name="oldName"/> to the existing parameter
        /// <paramref name="targetName"/>; both parameters stay in the list. Type differences
        /// are the caller's concern (the analyzer flags invalid conditions afterwards).
        /// </summary>
        public static bool RedirectReferences(AnimatorController controller, string oldName, string targetName)
        {
            if (controller == null || string.IsNullOrEmpty(targetName) || oldName == targetName)
                return false;
            bool targetExists = false;
            foreach (var p in controller.parameters)
                if (p.name == targetName) { targetExists = true; break; }
            if (!targetExists) return false;

            using (new UndoScope("Redirect Parameter References"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "Redirect Parameter References");
                RewriteReferences(controller, oldName, targetName);
                EditorUtility.SetDirty(controller);
            }
            return true;
        }

        /// <summary>
        /// Deletes the parameter after removing every reference: conditions mentioning it are
        /// dropped from their transitions, state parameter overrides are disabled, and driver
        /// entries writing it are removed. Blend-tree references can't be blanked and are left
        /// for the analyzer to flag.
        /// </summary>
        public static bool DeleteAndClean(AnimatorController controller, string name)
        {
            if (controller == null || string.IsNullOrEmpty(name)) return false;

            using (new UndoScope("Delete And Clean Parameter"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "Delete And Clean Parameter");

                foreach (var t in controller.AllTransitions())
                {
                    var conditions = t.conditions;
                    var kept = new List<TransitionClipboard.ConditionData>(conditions.Length);
                    foreach (var c in conditions)
                        if (c.parameter != name)
                            kept.Add(new TransitionClipboard.ConditionData
                            {
                                mode = c.mode,
                                parameter = c.parameter,
                                threshold = c.threshold,
                            });
                    if (kept.Count == conditions.Length) continue;
                    Undo.RegisterCompleteObjectUndo(t, "Delete And Clean Parameter");
                    TransitionClipboard.SetConditions(t, kept);
                    EditorUtility.SetDirty(t);
                }

                foreach (var s in controller.AllStates())
                {
                    bool dirty = s.speedParameter == name || s.timeParameter == name
                              || s.cycleOffsetParameter == name || s.mirrorParameter == name;
                    if (!dirty) continue;
                    Undo.RegisterCompleteObjectUndo(s, "Delete And Clean Parameter");
                    if (s.speedParameter == name) { s.speedParameterActive = false; s.speedParameter = string.Empty; }
                    if (s.timeParameter == name) { s.timeParameterActive = false; s.timeParameter = string.Empty; }
                    if (s.cycleOffsetParameter == name) { s.cycleOffsetParameterActive = false; s.cycleOffsetParameter = string.Empty; }
                    if (s.mirrorParameter == name) { s.mirrorParameterActive = false; s.mirrorParameter = string.Empty; }
                    EditorUtility.SetDirty(s);
                }

                foreach (var behaviour in controller.AllBehaviours())
                    VrcParameterDriver.RemoveEntriesReferencing(behaviour, name);

                var parameters = controller.parameters;
                for (int i = 0; i < parameters.Length; i++)
                    if (parameters[i].name == name)
                    {
                        controller.RemoveParameter(i);
                        break;
                    }
                EditorUtility.SetDirty(controller);
            }
            return true;
        }

        /// <summary>The rewrite walk shared by rename and redirect: conditions, blend trees,
        /// state parameter overrides and driver behaviours.</summary>
        static void RewriteReferences(AnimatorController controller, string oldName, string newName)
        {
            {
                foreach (var t in controller.AllTransitions())
                {
                    var conditions = t.conditions;
                    bool dirty = false;
                    var rebuilt = new List<TransitionClipboard.ConditionData>(conditions.Length);
                    foreach (var c in conditions)
                    {
                        bool match = c.parameter == oldName;
                        dirty |= match;
                        rebuilt.Add(new TransitionClipboard.ConditionData
                        {
                            mode = c.mode,
                            parameter = match ? newName : c.parameter,
                            threshold = c.threshold,
                        });
                    }
                    if (dirty)
                    {
                        Undo.RegisterCompleteObjectUndo(t, "Rename Parameter");
                        TransitionClipboard.SetConditions(t, rebuilt);
                        EditorUtility.SetDirty(t);
                    }
                }

                foreach (var bt in controller.AllBlendTrees())
                {
                    var children = bt.children;
                    bool childDirty = false;
                    for (int i = 0; i < children.Length; i++)
                        if (children[i].directBlendParameter == oldName) { childDirty = true; break; }

                    bool dirty = bt.blendParameter == oldName || bt.blendParameterY == oldName || childDirty;
                    if (!dirty) continue;

                    Undo.RegisterCompleteObjectUndo(bt, "Rename Parameter");
                    if (bt.blendParameter == oldName) bt.blendParameter = newName;
                    if (bt.blendParameterY == oldName) bt.blendParameterY = newName;
                    if (childDirty)
                    {
                        for (int i = 0; i < children.Length; i++)
                            if (children[i].directBlendParameter == oldName)
                            {
                                var ch = children[i];
                                ch.directBlendParameter = newName;
                                children[i] = ch;
                            }
                        bt.children = children;
                    }
                    EditorUtility.SetDirty(bt);
                }

                foreach (var s in controller.AllStates())
                {
                    bool dirty = s.speedParameter == oldName || s.timeParameter == oldName
                              || s.cycleOffsetParameter == oldName || s.mirrorParameter == oldName;
                    if (!dirty) continue;

                    Undo.RegisterCompleteObjectUndo(s, "Rename Parameter");
                    if (s.speedParameter == oldName) s.speedParameter = newName;
                    if (s.timeParameter == oldName) s.timeParameter = newName;
                    if (s.cycleOffsetParameter == oldName) s.cycleOffsetParameter = newName;
                    if (s.mirrorParameter == oldName) s.mirrorParameter = newName;
                    EditorUtility.SetDirty(s);
                }

                // StateMachineBehaviours that reference parameters by name — currently the
                // VRC Parameter Driver (Set/Add/Random/Copy destination and Copy source).
                foreach (var behaviour in controller.AllBehaviours())
                    VrcParameterDriver.RenameReferences(behaviour, oldName, newName);
            }
        }
    }
}

using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Renames the AnimationClip backing a state. A standalone .anim asset is renamed on disk;
    /// an editable sub-asset or an in-memory clip just has its object name changed; a clip
    /// imported from a model (e.g. an FBX) is read-only and must be renamed from the importer.
    /// </summary>
    static class ClipRenamer
    {
        public static void Rename(AnimationClip clip, string newName, DaerDContext context)
        {
            if (clip == null || string.IsNullOrEmpty(newName) || newName == clip.name) return;

            var path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path))
            {
                // Lives only in memory (never written to an asset).
                Undo.RegisterCompleteObjectUndo(clip, "Rename Clip");
                clip.name = newName;
                EditorUtility.SetDirty(clip);
            }
            else if (AssetDatabase.IsMainAsset(clip))
            {
                string error = AssetDatabase.RenameAsset(path, newName);
                if (!string.IsNullOrEmpty(error))
                {
                    Debug.LogWarning("DaerD: could not rename clip — " + error);
                    return;
                }
            }
            else if (AssetImporter.GetAtPath(path) is ModelImporter)
            {
                EditorUtility.DisplayDialog("Rename Clip",
                    "This clip is imported from a model (e.g. an FBX) and cannot be renamed here. " +
                    "Rename it from the model's Import Settings (Animation tab).",
                    "OK");
                return;
            }
            else
            {
                // Editable sub-asset of another asset (a controller, a .asset, ...).
                Undo.RegisterCompleteObjectUndo(clip, "Rename Clip");
                clip.name = newName;
                EditorUtility.SetDirty(clip);
                AssetDatabase.SaveAssets();
            }

            context?.NotifyGraphStructureChanged();
        }
    }
}

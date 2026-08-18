using UnityEditor;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Making a project folder exist. Two features that share nothing else both have to: the
    /// recipe exporter writing an asset into a folder somebody typed, and a detached holder
    /// writing one into a mirror of its controller's folders. It lives out here rather than
    /// beside either of them because Persist does not depend on Authoring, and a second copy of
    /// the ceremony below would be a second chance to get it wrong.
    /// </summary>
    static class AssetFolders
    {
        /// <summary>
        /// Makes sure a project folder exists AND is imported, creating the chain through the
        /// AssetDatabase (Directory.CreateDirectory alone leaves folders the asset pipeline
        /// hasn't seen, which corrupts GenerateUniqueAssetPath).
        ///
        /// The argument must already be a project path ("Assets/…"). Coercing user input into
        /// one is a separate job with its own failure mode, and it stays with the code that
        /// takes the input — see <c>RecipeExportQueue.NormalizeProjectFolder</c>.
        /// </summary>
        internal static bool Ensure(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            if (AssetDatabase.IsValidFolder(folder)) return true;

            var parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)
                    && string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i])))
                    return false;
                current = next;
            }
            return AssetDatabase.IsValidFolder(folder);
        }
    }
}

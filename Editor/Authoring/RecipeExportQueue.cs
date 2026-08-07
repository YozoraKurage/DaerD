using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// Finishes an export after the domain reload. Writing the generated .cs triggers a
    /// recompile, and the recipe .asset can only be created once its class exists — so the
    /// window queues a pending record here (SessionState survives the reload) and this class
    /// picks it up afterwards: instantiate the recipe, assign the target controller and every
    /// asset field by recorded GlobalObjectId (works for sub-assets too), save, ping.
    /// </summary>
    static class RecipeExportQueue
    {
        const string Key = "Yozolab.DaerD.PendingRecipeExports";
        const int MaxAttempts = 3;

        [Serializable]
        class Pending
        {
            public string typeName;          // namespace-qualified class name
            public string assetPath;
            public string controllerId;      // GlobalObjectId strings
            public bool exclusive;
            public List<string> fieldNames = new List<string>();
            public List<string> fieldIds = new List<string>();
            public int attempts;
        }

        [Serializable]
        class PendingList
        {
            public List<Pending> items = new List<Pending>();
        }

        public static void Enqueue(string typeName, string assetPath,
            AnimatorController controller, bool exclusive,
            IEnumerable<RecipeExporter.FieldRef> fields)
        {
            var list = Load();
            var pending = new Pending
            {
                typeName = typeName,
                assetPath = assetPath,
                controllerId = GlobalObjectId.GetGlobalObjectIdSlow(controller).ToString(),
                exclusive = exclusive,
            };
            foreach (var field in fields)
            {
                if (field.asset == null) continue;
                pending.fieldNames.Add(field.fieldName);
                pending.fieldIds.Add(GlobalObjectId.GetGlobalObjectIdSlow(field.asset).ToString());
            }
            list.items.Add(pending);
            Save(list);
        }

        [InitializeOnLoadMethod]
        static void ProcessAfterReload()
        {
            // Deferred: the asset database isn't ready inside InitializeOnLoad itself.
            EditorApplication.delayCall += Process;
        }

        static void Process()
        {
            var list = Load();
            if (list.items.Count == 0) return;

            var remaining = new List<Pending>();
            foreach (var pending in list.items)
            {
                var type = FindRecipeType(pending.typeName);
                if (type == null)
                {
                    // Still compiling (or the script has an error) — retry a few reloads.
                    pending.attempts++;
                    if (pending.attempts < MaxAttempts)
                        remaining.Add(pending);
                    else
                        Debug.LogWarning("DaerD: recipe class '" + pending.typeName
                            + "' never appeared (compile error?) — the recipe asset was not created. "
                            + "Fix the script and use Assets > Create > DaerD > Recipe Asset From Script.");
                    continue;
                }
                try
                {
                    CreateAsset(pending, type);
                }
                catch (Exception e)
                {
                    // One bad record must not take the queue down with it — an escaped
                    // exception here used to skip the save below and replay everything.
                    Debug.LogError("DaerD: creating the recipe asset at '" + pending.assetPath
                        + "' failed: " + e.Message);
                }
            }
            Save(new PendingList { items = remaining });
        }

        static Type FindRecipeType(string typeName)
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<ControllerRecipe>())
                if (!type.IsAbstract && type.FullName == typeName)
                    return type;
            return null;
        }

        static void CreateAsset(Pending pending, Type type)
        {
            var recipe = (ControllerRecipe)ScriptableObject.CreateInstance(type);
            recipe.exclusive = pending.exclusive;
            recipe.targetController = Resolve(pending.controllerId) as AnimatorController;

            var serialized = new SerializedObject(recipe);
            for (int i = 0; i < pending.fieldNames.Count; i++)
            {
                var property = serialized.FindProperty(pending.fieldNames[i]);
                if (property != null
                    && property.propertyType == SerializedPropertyType.ObjectReference)
                    property.objectReferenceValue = Resolve(pending.fieldIds[i]);
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // GenerateUniqueAssetPath (and CreateAsset) return garbage for folders the
            // AssetDatabase doesn't know — validate and materialize the chain first, and
            // fall back to Assets/ rather than losing the configured instance.
            string path = pending.assetPath.Replace('\\', '/');
            int lastSlash = path.LastIndexOf('/');
            string folder = lastSlash > 0 ? path.Substring(0, lastSlash) : "Assets";
            string file = lastSlash > 0 ? path.Substring(lastSlash + 1) : path;
            if (!EnsureAssetFolder(folder))
            {
                Debug.LogWarning("DaerD: '" + folder + "' is not a usable project folder — "
                    + "the recipe asset goes to Assets/ instead.");
                folder = "Assets";
            }
            AssetDatabase.CreateAsset(recipe,
                AssetDatabase.GenerateUniqueAssetPath(folder + "/" + file));
            AssetDatabase.SaveAssets();
            Selection.activeObject = recipe;
            EditorGUIUtility.PingObject(recipe);
            Debug.Log("DaerD: recipe asset created at '" + AssetDatabase.GetAssetPath(recipe)
                + "' — asset references are pre-assigned; press Generate to test the round trip.");
        }

        /// <summary>Makes sure a project folder exists AND is imported, creating the chain
        /// through the AssetDatabase (Directory.CreateDirectory alone leaves folders the
        /// asset pipeline hasn't seen, which corrupts GenerateUniqueAssetPath).</summary>
        internal static bool EnsureAssetFolder(string folder)
        {
            folder = NormalizeProjectFolder(folder);
            if (folder == null) return false;
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

        /// <summary>
        /// Coerces user input (typed text, an absolute picker path) into an "Assets/…"
        /// project folder, or null when it can't be — the export window refuses to run on
        /// null instead of letting a mangled path reach the asset pipeline.
        /// </summary>
        internal static string NormalizeProjectFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            folder = folder.Replace('\\', '/').Trim().TrimEnd('/');
            if (folder == "Assets" || folder.StartsWith("Assets/")) return folder;

            // Absolute path into this project? Cut at the project's own Assets folder.
            string dataPath = Application.dataPath.Replace('\\', '/');   // ".../Project/Assets"
            if (folder.StartsWith(dataPath))
            {
                string tail = folder.Substring(dataPath.Length).TrimStart('/');
                return tail.Length == 0 ? "Assets" : "Assets/" + tail;
            }
            // Last resort: the last "/Assets/" segment boundary (never a substring match
            // inside a name — that's exactly the bug this guards against).
            int boundary = folder.LastIndexOf("/Assets/", StringComparison.Ordinal);
            if (boundary >= 0) return folder.Substring(boundary + 1);
            if (folder.EndsWith("/Assets")) return "Assets";
            return null;
        }

        static UnityEngine.Object Resolve(string id) =>
            GlobalObjectId.TryParse(id, out var parsed)
                ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed)
                : null;

        static PendingList Load()
        {
            var json = SessionState.GetString(Key, string.Empty);
            if (string.IsNullOrEmpty(json)) return new PendingList();
            try
            {
                return JsonUtility.FromJson<PendingList>(json) ?? new PendingList();
            }
            catch (Exception)
            {
                return new PendingList();
            }
        }

        static void Save(PendingList list) =>
            SessionState.SetString(Key, list.items.Count == 0 ? string.Empty : JsonUtility.ToJson(list));
    }
}

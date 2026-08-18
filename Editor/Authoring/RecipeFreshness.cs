using System;
using System.IO;
using UnityEditor;
// UnityEditor has a PackageInfo of its own (the asset-store kind), so the package manager's has
// to be named outright — the same collision PrefabWriter walks into.
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// Whether the code that would run for a recipe is the code its .cs currently says.
    ///
    /// <para>THE ACCIDENT THIS EXISTS FOR.</para>
    /// A recipe's Build runs out of a LOADED assembly. Editing the .cs changes a file; what runs
    /// changes only after a compile and a domain reload, and between the two there is a window
    /// where Generate happily rebuilds the controller from the previous version — silently, with
    /// a clean result and no warnings. A compile error widens that window indefinitely: Unity
    /// keeps the last assembly that built, so the recipe goes on generating last week's
    /// controller for as long as the error stands. Every symptom reads as "Generate produces the
    /// same old content every time", and nothing on screen says why.
    ///
    /// <para>WHY A PURE JUDGE OVER FACTS.</para>
    /// The same split <c>PrefabWriter.Judge</c> uses. The states worth pinning are the ones
    /// nobody can reproduce on demand — a compile in flight, a failed compile, a .cs saved one
    /// second ago — so the decision takes timestamps and flags rather than an editor.
    ///
    /// <para>WHAT THIS DOES NOT GUARANTEE.</para>
    /// It is best-effort and it FAILS OPEN. A fact that cannot be collected — an assembly with no
    /// file on disk, a script Unity will not name, a timestamp that throws — reads as Fresh, and
    /// the run goes ahead. The guarantee is only that the frequent, silent freezes get a sentence
    /// instead of a wrong result; it is not "the code you are reading is the code that ran". In
    /// particular a hand half edited inside a still-warm assembly, a recipe compiled elsewhere
    /// and copied in, and any change that leaves the .cs timestamp alone are all invisible here.
    /// Two seconds of slack sit between the assembly and the sources so that a save and the
    /// compile it triggers, which routinely land in the same second, do not read as a conflict.
    /// </summary>
    static class RecipeFreshness
    {
        /// <summary>Why the loaded code may not be the code on disk. Every value but
        /// <see cref="Fresh"/> stops the action and is said out loud.</summary>
        internal enum Staleness
        {
            /// <summary>Nothing suggests a mismatch — which is not the same as proving there
            /// is none (see the class doc).</summary>
            Fresh,
            /// <summary>A compile or an asset import is in flight.</summary>
            Compiling,
            /// <summary>The last compile failed, so the loaded assembly is an older one.</summary>
            CompileFailed,
            /// <summary>A half of the recipe's source is newer than the assembly holding it.</summary>
            SourceNewer,
        }

        /// <summary>How far the sources may run ahead of the assembly before it counts. A save and
        /// the compile it starts land in the same second all the time; without slack the guard
        /// would refuse the very run it was meant to make safe.</summary>
        internal static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The decision itself, over facts rather than over the editor. Nulls are missing facts,
        /// and every one of them fails open — a guard that refuses to run because it could not
        /// read a timestamp is a guard that gets switched off.
        ///
        /// The compile error outranks the compile in flight because it is the one that needs a
        /// person: a recompile finishes on its own, an error does not. The cost is that a
        /// recompile started right after fixing an error is announced as the error until the
        /// compile lands. Both refuse the run either way, so what is at stake is only which
        /// sentence is shown.
        /// </summary>
        internal static Staleness Judge(DateTime? assemblyTime, DateTime? handCsTime,
            DateTime? generatedCsTime, bool isCompiling, bool compilationFailed)
        {
            if (compilationFailed) return Staleness.CompileFailed;
            if (isCompiling) return Staleness.Compiling;
            if (!assemblyTime.HasValue) return Staleness.Fresh;

            DateTime? newest = handCsTime;
            if (generatedCsTime.HasValue
                && (!newest.HasValue || generatedCsTime.Value > newest.Value))
                newest = generatedCsTime;
            if (!newest.HasValue) return Staleness.Fresh;

            return newest.Value > assemblyTime.Value + Slack
                ? Staleness.SourceNewer : Staleness.Fresh;
        }

        /// <summary>The verdict for one recipe, with the editor's facts collected for
        /// <see cref="Judge"/>. Anything that cannot be read is passed as null.</summary>
        internal static Staleness Check(ControllerRecipe recipe)
        {
            if (recipe == null) return Staleness.Fresh;
            DateTime? hand = null;
            DateTime? generated = null;
            // The script Unity files this instance under is the hand half: it is the file named
            // after the class, and the generated half is its partner in the same folder. Asked
            // for the generated one only when the recipe still declares one — a hand-written
            // recipe has no second file, and a missing one would read as a missing fact.
            var script = MonoScript.FromScriptableObject(recipe);
            var scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
            if (!string.IsNullOrEmpty(scriptPath))
            {
                hand = FileTime(ToDiskPath(scriptPath));
                if (recipe.HasGeneratedHalf)
                {
                    var folder = Path.GetDirectoryName(scriptPath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(folder))
                        generated = FileTime(ToDiskPath(
                            folder + "/" + recipe.GetType().Name + ".Generated.cs"));
                }
            }
            return Judge(AssemblyTime(recipe), hand, generated,
                EditorApplication.isCompiling || EditorApplication.isUpdating,
                EditorUtility.scriptCompilationFailed);
        }

        /// <summary>The sentence for a verdict, or null for <see cref="Staleness.Fresh"/>. Each
        /// one names what is running instead of what was written, because "try again later" on
        /// its own leaves the reader thinking the recipe is broken.</summary>
        internal static string Reason(Staleness staleness)
        {
            switch (staleness)
            {
                case Staleness.Compiling:
                    return L.Tr("Scripts are still compiling — run this again once the compile has finished.");
                case Staleness.CompileFailed:
                    return L.Tr("A compile error means the new code was never loaded — what would run is the last version that compiled successfully. Check the Console.");
                case Staleness.SourceNewer:
                    return L.Tr("This recipe's .cs is newer than the compiled code — wait for the recompile before running this.");
                default:
                    return null;
            }
        }

        /// <summary>When the assembly holding this recipe's class was last written. Null when it
        /// has no file (a dynamic assembly) or the path cannot be read — both fail open.</summary>
        static DateTime? AssemblyTime(ControllerRecipe recipe)
        {
            try
            {
                var location = recipe.GetType().Assembly.Location;
                return string.IsNullOrEmpty(location) ? null : FileTime(location);
            }
            catch (Exception)
            {
                // Assembly.Location throws for assemblies loaded from memory. A recipe compiled
                // that way is somebody else's arrangement, and refusing to run it would be this
                // guard inventing a rule of its own.
                return null;
            }
        }

        static DateTime? FileTime(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>An asset path as something <see cref="File"/> can open. "Assets/…" is already
        /// relative to the working directory; "Packages/…" is a virtual path whose real folder is
        /// wherever the package manager resolved it, which for a local package is anywhere on the
        /// disk. Unresolvable paths are left as they are and simply fail to open — fail-open, like
        /// every other missing fact here.</summary>
        static string ToDiskPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)
                || !assetPath.StartsWith("Packages/", StringComparison.Ordinal))
                return assetPath;
            var package = PackageInfo.FindForAssetPath(assetPath);
            if (package == null || string.IsNullOrEmpty(package.resolvedPath)) return assetPath;
            int slash = assetPath.IndexOf('/', "Packages/".Length);
            string rest = slash >= 0 ? assetPath.Substring(slash + 1) : string.Empty;
            string root = package.resolvedPath.Replace('\\', '/').TrimEnd('/');
            return rest.Length == 0 ? root : root + "/" + rest;
        }
    }
}

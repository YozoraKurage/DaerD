using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// The scripted way into the recipe exporter: the same export the window runs, reachable
    /// from tests, editor scripts and batch mode. The window is one caller of this, not the
    /// other way around — everything it does that isn't a dialog lives here, so a scripted
    /// export and a clicked one cannot drift apart.
    ///
    /// Three depths, because the callers want different things:
    /// <see cref="ToSource"/> hands back the text and touches nothing (reading a controller as
    /// C# is a read-only act — no folders, no asset pipeline, no domain reload);
    /// <see cref="ToDirectory"/> drops the two files anywhere on disk, including outside the
    /// project, for an analysis that shouldn't leave anything behind in Assets;
    /// <see cref="ToProject"/> is the full export — asmdef, recipe .asset, reimport — and is
    /// what the window calls.
    ///
    /// Only this class and its data are public. The exporter, its IR and the builder it
    /// replays stay internal so their shape can keep changing.
    /// </summary>
    public static class RecipeExport
    {
        /// <summary>A clip, mask or other asset the code refers to by field instead of by
        /// GUID. On a project export these are pre-assigned on the recipe .asset.</summary>
        public sealed class Field
        {
            public string name;
            public string typeName;
            public UnityEngine.Object asset;
        }

        /// <summary>The exported text. <see cref="code"/> is regenerated on every export;
        /// <see cref="handHalf"/> is the author's half and is only ever written once.</summary>
        public sealed class Source
        {
            public string code;
            public string handHalf;
            public string className;
            public readonly List<string> warnings = new List<string>();
            public readonly List<Field> fields = new List<Field>();

            /// <summary>The exporter's own result — kept so the recipe .asset can be filled
            /// in and <see cref="Verify"/> can reach the replayed builder.</summary>
            internal RecipeExporter.Result inner;
            internal List<string> differences;
        }

        /// <summary>What an export decides beyond the controller itself. Every field has the
        /// same default the window starts from, so `new Options()` is the window's behaviour
        /// minus the typing.</summary>
        public sealed class Options
        {
            /// <summary>Null takes the controller's name (the window's default).</summary>
            public string className;
            public string namespaceName;
            /// <summary>Null (or every layer) exports the whole controller as an exclusive
            /// recipe; a subset exports those layers plus only the parameters they use.</summary>
            public IEnumerable<string> layerNames;
            /// <summary>Project exports only: create the recipe .asset once the class compiles.</summary>
            public bool createAsset = true;
            /// <summary>Project exports only: give the recipe folder its own small assembly.</summary>
            public bool createAsmdef = true;
            /// <summary>Allow rewriting a pre-split single-file recipe into the two halves
            /// (its contents are backed up next to it first). Off by default: an unattended
            /// export should stop rather than reshape a file a human still owns.</summary>
            public bool migrateSingleFile;
        }

        /// <summary>Where an export landed. <see cref="codeUnchanged"/> means the generated
        /// half was byte-identical and was not rewritten — no reimport, no recompile.</summary>
        public sealed class Written
        {
            public Source source;
            public string generatedPath;
            public string handHalfPath;
            public bool wroteHandHalf;
            public bool codeUnchanged;
        }

        // ---- source ------------------------------------------------------------

        /// <summary>The class name an export defaults to: the controller's name as an
        /// identifier, plus "Recipe".</summary>
        public static string DefaultClassName(AnimatorController controller) =>
            RecipeScript.Identifier(controller != null ? controller.name : string.Empty,
                lowerFirst: false) + "Recipe";

        /// <summary>
        /// Exports to text and nothing else. <paramref name="options"/> null means the
        /// defaults; only className, namespaceName and layerNames are read here.
        /// </summary>
        public static Source ToSource(AnimatorController controller, Options options = null)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            options = options ?? new Options();

            string className = RecipeScript.Identifier(
                string.IsNullOrEmpty(options.className)
                    ? DefaultClassName(controller) : options.className, lowerFirst: false);
            string ns = string.IsNullOrEmpty(options.namespaceName)
                ? null : options.namespaceName.Trim();

            var result = RecipeExporter.Export(controller, Subset(controller, options.layerNames),
                className, ns);

            var source = new Source
            {
                code = result.code,
                handHalf = result.handHalf,
                className = result.className,
                inner = result,
            };
            source.warnings.AddRange(result.warnings);
            foreach (var field in result.fields)
                source.fields.Add(new Field
                {
                    name = field.fieldName, typeName = field.fieldType, asset = field.asset,
                });
            return source;
        }

        /// <summary>
        /// What the exported code declares, compared against the controller it came from —
        /// empty means the recipe rebuilds the controller exactly. The check is free of the
        /// compiler: the exporter drove a real builder while recording, so comparing that
        /// builder's IR against the controller's tests the emitted call sequence itself.
        ///
        /// This is the assertion an automated analysis wants: it says the C# it is about to
        /// read is the controller, not a lossy summary of it. Cached per <paramref name="source"/>.
        /// </summary>
        public static List<string> Verify(AnimatorController controller, Source source)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.differences != null) return source.differences;

            source.differences = source.inner?.replayed == null
                ? new List<string> { "export produced no replayed builder" }
                : ControllerIRDiff.Compare(ControllerIR.Parse(controller), source.inner.replayed.IR);
            return source.differences;
        }

        /// <summary>Null when every layer is in — the exporter reads that as "this recipe owns
        /// the whole controller", which is also what the window sends when all boxes are
        /// ticked.</summary>
        static ICollection<string> Subset(AnimatorController controller,
            IEnumerable<string> layerNames)
        {
            if (layerNames == null) return null;
            var subset = new List<string>(layerNames);
            return subset.Count == controller.layers.Length ? null : subset;
        }

        // ---- files -------------------------------------------------------------

        /// <summary>
        /// Writes the two halves into any directory on disk, project or not. Nothing else
        /// happens: no assembly definition, no recipe .asset, no reimport — outside Assets
        /// there is no pipeline to tell, and inside it those are <see cref="ToProject"/>'s job.
        /// </summary>
        public static Written ToDirectory(AnimatorController controller, string directory,
            Options options = null)
        {
            if (string.IsNullOrEmpty(directory))
                throw new ArgumentException("directory is required", nameof(directory));
            options = options ?? new Options();
            var source = ToSource(controller, options);
            // Both writing routes report through the console, so a caller that only asked for
            // files still hears about a layer the exporter couldn't express.
            foreach (var warning in source.warnings)
                Debug.LogWarning("DaerD: " + warning);
            Directory.CreateDirectory(directory);
            return WriteHalves(source, directory.Replace('\\', '/').TrimEnd('/'),
                migrated: false);
        }

        /// <summary>
        /// The full export into the project: both halves, the small recipe assembly, and the
        /// recipe .asset with every clip reference pre-assigned (queued — the class has to
        /// compile before an instance of it can exist).
        ///
        /// <paramref name="folder"/> is a project folder ("Assets/…" or an absolute path
        /// inside it); a recipe is editor code, so an "/Editor" leaf is appended when the
        /// folder isn't already under one. Throws when the folder is outside the project,
        /// can't be created, or holds a pre-split recipe that
        /// <see cref="Options.migrateSingleFile"/> hasn't licensed it to rewrite.
        /// </summary>
        public static Written ToProject(AnimatorController controller, string folder,
            Options options = null)
        {
            options = options ?? new Options();
            string resolved = ResolveProjectFolder(folder);
            if (resolved == null)
                throw new ArgumentException(
                    "'" + folder + "' is outside this project — use ToDirectory for that",
                    nameof(folder));

            var source = ToSource(controller, options);
            foreach (var warning in source.warnings)
                Debug.LogWarning("DaerD: " + warning);

            // Through the AssetDatabase, not Directory.CreateDirectory: the pipeline must
            // know the folder or GenerateUniqueAssetPath/CreateAsset mangle their paths.
            if (!RecipeExportQueue.EnsureAssetFolder(resolved))
                throw new IOException("could not create the output folder '" + resolved + "'");

            string csPath = resolved + "/" + source.className + ".cs";

            // A recipe exported before the split carries the fields and the Build the
            // generated half now owns — leaving it as it is would be a duplicate definition.
            bool migrated = false;
            if (IsSingleFileRecipe(csPath))
            {
                if (!options.migrateSingleFile)
                    throw new InvalidOperationException(
                        "'" + csPath + "' is a single-file recipe from an earlier DaerD; set "
                        + "Options.migrateSingleFile to let the export rewrite it (its "
                        + "contents are backed up beside it first)");
                // Copied, not moved: the file is rewritten in place below so it keeps its
                // .meta — and with it the GUID every existing recipe .asset points its script
                // reference at.
                File.Copy(csPath, csPath + ".bak", true);
                migrated = true;
                Debug.Log("DaerD: '" + csPath + "' was a single-file recipe — its contents are"
                    + " backed up at '" + csPath + ".bak', and the file itself becomes your half.");
            }

            var written = WriteHalves(source, resolved, migrated);

            if (options.createAsmdef)
                EnsureRecipesAsmdef(resolved);

            if (options.createAsset)
            {
                string typeName = string.IsNullOrEmpty(options.namespaceName)
                    ? source.className
                    : options.namespaceName.Trim() + "." + source.className;
                RecipeExportQueue.Enqueue(typeName, resolved + "/" + source.className + ".asset",
                    controller, Subset(controller, options.layerNames) == null,
                    source.inner.fields);
            }

            if (!written.codeUnchanged)
                AssetDatabase.ImportAsset(written.generatedPath);
            if (written.wroteHandHalf)
                AssetDatabase.ImportAsset(written.handHalfPath);

            Debug.Log("DaerD: recipe exported to '" + written.generatedPath + "'"
                + (written.codeUnchanged ? " (code unchanged — no recompile)" : string.Empty)
                + (written.wroteHandHalf
                    ? " — your half is '" + csPath + "', and no export will overwrite it."
                    : " — '" + csPath + "' is yours and was left untouched; diff the generated"
                        + " half, carry the change over, then press Compare.")
                + (options.createAsset ? " The recipe asset follows." : string.Empty));
            return written;
        }

        /// <summary>The write both routes share: the generated half every time (unless it
        /// would be byte-identical), the hand half only when there isn't one yet.</summary>
        static Written WriteHalves(Source source, string folder, bool migrated)
        {
            string generatedPath = folder + "/" + source.className + ".Generated.cs";
            string csPath = folder + "/" + source.className + ".cs";

            // Byte-identical re-export: skip the write entirely — no reimport, no compile,
            // no domain reload.
            bool identical = File.Exists(generatedPath)
                && File.ReadAllText(generatedPath) == source.code;
            if (!identical)
                File.WriteAllText(generatedPath, source.code);

            // The half that is yours: written once, then left alone forever — that is the
            // whole point of the split.
            bool wroteHandHalf = migrated || !File.Exists(csPath);
            if (wroteHandHalf)
                File.WriteAllText(csPath, source.handHalf);

            return new Written
            {
                source = source,
                generatedPath = generatedPath,
                handHalfPath = csPath,
                wroteHandHalf = wroteHandHalf,
                codeUnchanged = identical,
            };
        }

        /// <summary>
        /// The folder a project export actually writes to: the input coerced to "Assets/…",
        /// with "/Editor" appended when it isn't already under one — recipes are editor code.
        /// Null when the input names nothing inside this project.
        /// </summary>
        public static string ResolveProjectFolder(string folder)
        {
            string projectFolder = RecipeExportQueue.NormalizeProjectFolder(folder);
            if (projectFolder == null) return null;
            return ("/" + projectFolder + "/").Contains("/Editor/")
                ? projectFolder : projectFolder + "/Editor";
        }

        /// <summary>
        /// Whether a "&lt;Name&gt;.cs" is a whole recipe from before the two-file split rather
        /// than a hand half. The marker line is the intended signal; "partial class" covers a
        /// hand half whose header was edited away, which is likely — that file is meant to be
        /// rewritten.
        /// </summary>
        public static bool IsSingleFileRecipe(string csPath)
        {
            if (!File.Exists(csPath)) return false;
            string text = File.ReadAllText(csPath);
            return !text.StartsWith(RecipeExporter.HandHalfMarker)
                && !text.Contains("partial class");
        }

        /// <summary>
        /// Gives the recipe folder its own tiny editor assembly, so exporting recompiles a
        /// handful of recipe files instead of the project's whole editor assembly. Only when
        /// it is safe: no asmdef already governs the folder, and every script in it is one
        /// DaerD generated (an asmdef changes which assembly neighbours compile into).
        /// </summary>
        static void EnsureRecipesAsmdef(string folder)
        {
            // Walk up to Assets: an existing asmdef anywhere above already governs us.
            for (string current = folder; !string.IsNullOrEmpty(current);
                current = current == "Assets" ? null : Path.GetDirectoryName(current)?.Replace('\\', '/'))
                if (Directory.Exists(current) && Directory.GetFiles(current, "*.asmdef").Length > 0)
                    return;

            foreach (var script in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                using (var reader = new StreamReader(script))
                {
                    // Both halves of an exported recipe count as DaerD's own.
                    string first = reader.ReadLine() ?? string.Empty;
                    if (first.Contains("<auto-generated> Exported from")
                        || first.StartsWith(RecipeExporter.HandHalfMarker))
                        continue;
                }
                Debug.Log("DaerD: '" + folder + "' contains scripts DaerD didn't generate — "
                    + "not adding an assembly definition (it would move them to another assembly).");
                return;
            }

            // Unique, deterministic assembly name per folder — asmdef names are global.
            uint hash = 2166136261;
            foreach (char c in folder)
                hash = (hash ^ c) * 16777619;
            string name = "DaerD.Recipes." + hash.ToString("x8");
            string path = folder + "/" + name + ".asmdef";
            File.WriteAllText(path,
                "{\n"
                + "    \"name\": \"" + name + "\",\n"
                + "    \"rootNamespace\": \"\",\n"
                + "    \"references\": [\"Yozolab.DaerD.Editor\"],\n"
                + "    \"includePlatforms\": [\"Editor\"],\n"
                + "    \"excludePlatforms\": [],\n"
                + "    \"allowUnsafeCode\": false,\n"
                + "    \"overrideReferences\": false,\n"
                + "    \"precompiledReferences\": [],\n"
                + "    \"autoReferenced\": false,\n"
                + "    \"defineConstraints\": [],\n"
                + "    \"versionDefines\": [],\n"
                + "    \"noEngineReferences\": false\n"
                + "}\n");
            AssetDatabase.ImportAsset(path);
            Debug.Log("DaerD: created '" + path + "' — future recipe exports recompile only this small assembly.");
        }
    }
}

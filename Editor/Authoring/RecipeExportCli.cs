using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// Batch-mode entry point for the recipe export — the shape a CI job or an analysis
    /// script can call without writing an editor script of its own first:
    ///
    /// <code>
    /// Unity -batchmode -quit -projectPath P \
    ///   -executeMethod Yozolab.DaerD.Authoring.RecipeExportCli.Run \
    ///   -daerdController Assets/Foo.controller -daerdController Assets/Bar.controller \
    ///   -daerdOut /tmp/recipes -daerdVerify
    /// </code>
    ///
    /// Where <c>-daerdOut</c> points decides which export runs: a folder inside the project
    /// gets the full treatment (assembly definition, recipe .asset), anywhere else gets the
    /// two .cs files and nothing more. Omitting it writes beside each controller.
    ///
    /// Reading a controller as C# is the point of this route, so it defaults to being
    /// harmless: it never rewrites a hand half, and refuses a pre-split recipe unless
    /// <c>-daerdMigrate</c> says otherwise.
    /// </summary>
    public static class RecipeExportCli
    {
        internal class Args
        {
            public readonly List<string> controllers = new List<string>();
            public string outFolder;
            public string namespaceName;
            public string className;
            public List<string> layers;
            public bool createAsset = true;
            public bool createAsmdef = true;
            public bool migrate;
            public bool verify;
        }

        /// <summary>
        /// Parses the DaerD flags out of a full command line (Unity's own arguments are
        /// ignored). Split out from <see cref="Run"/> so the misuse rules are testable
        /// without an editor process.
        /// </summary>
        internal static Args Parse(IList<string> argv)
        {
            var args = new Args();
            for (int i = 0; i < argv.Count; i++)
            {
                switch (argv[i])
                {
                    case "-daerdController": args.controllers.Add(Value(argv, ref i)); break;
                    case "-daerdOut": args.outFolder = Value(argv, ref i); break;
                    case "-daerdNamespace": args.namespaceName = Value(argv, ref i); break;
                    case "-daerdClass": args.className = Value(argv, ref i); break;
                    case "-daerdLayers":
                        args.layers = new List<string>(
                            Value(argv, ref i).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
                        break;
                    case "-daerdNoAsset": args.createAsset = false; break;
                    case "-daerdNoAsmdef": args.createAsmdef = false; break;
                    case "-daerdMigrate": args.migrate = true; break;
                    case "-daerdVerify": args.verify = true; break;
                }
            }

            if (args.controllers.Count == 0)
                throw new ArgumentException("at least one -daerdController <assetPath> is required");
            // A class name or a layer subset describes one controller; silently applying
            // either to a batch would write several exports over the same pair of files.
            if (args.controllers.Count > 1 && args.className != null)
                throw new ArgumentException("-daerdClass takes a single -daerdController");
            if (args.controllers.Count > 1 && args.layers != null)
                throw new ArgumentException("-daerdLayers takes a single -daerdController");
            return args;
        }

        static string Value(IList<string> argv, ref int i)
        {
            if (i + 1 >= argv.Count)
                throw new ArgumentException(argv[i] + " needs a value");
            return argv[++i];
        }

        /// <summary>
        /// The -executeMethod target. Exits the editor with 1 on any failure and 0 on success
        /// — but only in batch mode, so calling this from a running editor reports through the
        /// console instead of closing it.
        /// </summary>
        public static void Run()
        {
            try
            {
                Export(Parse(Environment.GetCommandLineArgs()));
            }
            catch (Exception e)
            {
                Debug.LogError("DaerD: recipe export failed — " + e.Message);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        internal static void Export(Args args)
        {
            foreach (string path in args.controllers)
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                if (controller == null)
                    throw new ArgumentException("no AnimatorController at '" + path + "'");

                var options = new RecipeExport.Options
                {
                    className = args.className,
                    namespaceName = args.namespaceName,
                    layerNames = args.layers,
                    createAsset = args.createAsset,
                    createAsmdef = args.createAsmdef,
                    migrateSingleFile = args.migrate,
                };

                string folder = args.outFolder;
                if (string.IsNullOrEmpty(folder))
                    folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                bool inProject = RecipeExport.ResolveProjectFolder(folder) != null;

                var written = inProject
                    ? RecipeExport.ToProject(controller, folder, options)
                    : RecipeExport.ToDirectory(controller, folder, options);

                // Warnings are the export's own to report — this line is what ties them to a
                // controller when several were named on one command line.
                Debug.Log("DaerD: exported '" + path + "' → '" + written.generatedPath + "'"
                    + (written.codeUnchanged ? " (unchanged)" : string.Empty));

                if (!args.verify) continue;
                var differences = RecipeExport.Verify(controller, written.source);
                if (differences.Count > 0)
                    throw new InvalidOperationException(
                        "the exported recipe does not rebuild '" + path + "': "
                        + string.Join("; ", differences.ToArray()));
                Debug.Log("DaerD: verified '" + path + "' — the recipe rebuilds it exactly.");
            }
        }
    }
}

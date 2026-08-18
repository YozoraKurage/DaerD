using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Compilation;
using UnityEngine;
using Yozolab.DaerD.Authoring;
using Object = UnityEngine.Object;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// What "public" means in this package, checked mechanically.
    ///
    /// <para>A recipe lives in the user's own assembly, so it reaches DaerD across an assembly
    /// boundary and sees the public types and nothing else. Asserting on the exported text
    /// cannot show that the export still works there: it can spell every call correctly and
    /// still fail to build, because what breaks a recipe author is accessibility, not
    /// spelling. So this compiles the real thing — both halves the exporter writes — with the
    /// real compiler, referencing the built assembly the way an outsider does.</para>
    ///
    /// <para>The probe assembly is deliberately NOT called Yozolab.DaerD.Tests. That name is
    /// in <c>InternalsVisibleTo</c>, and borrowing it would hide exactly the failure this test
    /// exists to catch.</para>
    ///
    /// <para>The compiler is Unity's own Roslyn, found under the editor's contents path, and
    /// the reference set and defines come from <see cref="CompilationPipeline"/> so they match
    /// what Unity itself would compile against. Neither needs a licence. Where either is
    /// missing the test says so and skips rather than passing quietly.</para>
    /// </summary>
    public class RecipeCompileTests
    {
        const string ClassName = "DaerDExportedRecipeProbe";
        const string Namespace = "DaerD.ExportProbe";

        readonly List<Object> _cleanup = new List<Object>();
        string _dir;

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup)
                if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
            if (_dir != null && Directory.Exists(_dir))
                try { Directory.Delete(_dir, true); } catch (IOException) { /* left for the OS */ }
            _dir = null;
        }

        [Test]
        public void ExportedRecipe_CompilesOutsideTheAssembly()
        {
            string runtime = DotnetRuntime();
            string compiler = RoslynCompiler();
            if (runtime == null || compiler == null)
                Assert.Ignore("no Roslyn under " + EditorApplication.applicationContentsPath
                    + " — the public surface cannot be checked here");

            var controller = BuildSample();
            var result = RecipeExporter.Export(controller, null, ClassName, Namespace);

            _dir = Path.Combine(Path.GetTempPath(), "DaerDRecipeCompile" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            // The two files a user gets: the half every export overwrites, and the half they
            // then edit. Both are compiled, because both are theirs to keep working.
            string generated = Path.Combine(_dir, ClassName + ".Generated.cs");
            string hand = Path.Combine(_dir, ClassName + ".cs");
            File.WriteAllText(generated, result.code);
            File.WriteAllText(hand, result.handHalf);

            var errors = Compile(runtime, compiler, new[] { generated, hand });
            Assert.IsEmpty(errors,
                "an exported recipe no longer compiles from outside Yozolab.DaerD.Editor — a type "
                + "the export names has left the public surface:\n" + string.Join("\n", errors));
        }

        /// <summary>
        /// The list written down in AssemblyInfo.cs, enforced.
        ///
        /// An API surface grows by accident — one <c>public</c> typed out of habit and the
        /// package has promised a type nobody decided to promise, with no moment at which
        /// anyone noticed. Naming the surface in a comment does not stop that; comparing the
        /// comment against what the assembly actually exports does. Adding to the promise
        /// means editing this list, which is the deliberate act the promise deserves.
        /// </summary>
        [Test]
        public void PublicSurface_IsExactlyTheRecipeApi()
        {
            const string ns = "Yozolab.DaerD.Authoring.";
            var promised = new[]
            {
                ns + "ControllerRecipe", ns + "ControllerBuilder",
                ns + "LayerBuilder", ns + "SyncedLayerBuilder", ns + "MachineBuilder",
                ns + "MachineScope", ns + "StateBuilder", ns + "TransitionBuilder",
                ns + "Condition",
                ns + "TreeBuilder", ns + "TreeChildBuilder",
                ns + "ParamHandle", ns + "BoolParam", ns + "IntParam", ns + "FloatParam",
                ns + "TriggerParam",
                ns + "GadgetRecipeBuilder", ns + "ParamRef", ns + "ObjectRecipeBuilder",
                ns + "ObjectToggleBuilder", ns + "AsyncSyncRecipeBuilder",
                ns + "RecipeExport", ns + "RecipeExport+Field", ns + "RecipeExport+Source",
                ns + "RecipeExport+Options", ns + "RecipeExport+Written",
                ns + "RecipeExportCli",
            };

            var exported = typeof(ControllerRecipe).Assembly.GetExportedTypes()
                .Select(type => type.FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(
                promised.OrderBy(name => name, StringComparer.Ordinal).ToArray(), exported,
                "the assembly's public surface and the list in Editor/AssemblyInfo.cs have "
                + "drifted apart — whichever moved, the other has to agree before the promise "
                + "means anything");
        }

        /// <summary>A controller wide enough that the export names most of the recipe API:
        /// all three parameter types, two layers, a state with a clip and settings, a blend
        /// tree, behaviours on a state and on a machine, and the three shapes of transition.
        /// </summary>
        AnimatorController BuildSample()
        {
            var controller = new AnimatorController { name = "CompileProbe" };
            _cleanup.Add(controller);
            var clip = new AnimationClip { name = "Probe Clip" };
            _cleanup.Add(clip);

            controller.AddParameter(new AnimatorControllerParameter
            { name = "Blend", type = AnimatorControllerParameterType.Float, defaultFloat = 0.5f });
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Count", AnimatorControllerParameterType.Int);
            controller.AddParameter("Fire", AnimatorControllerParameterType.Trigger);

            controller.AddLayer("Main");
            controller.AddLayer("Second");
            var machine = controller.layers[0].stateMachine;

            var a = machine.AddState("A", new Vector3(100f, 50f, 0f));
            a.motion = clip;
            a.speed = 2f;
            a.writeDefaultValues = false;
            ((IRTestBehaviour)a.AddStateMachineBehaviour(typeof(IRTestBehaviour))).payload = "state";
            ((IRTestBehaviour)machine.AddStateMachineBehaviour(typeof(IRTestBehaviour))).payload = "machine";

            var b = machine.AddState("B", new Vector3(100f, 150f, 0f));
            var tree = new BlendTree
            {
                name = "Move",
                blendType = BlendTreeType.Simple1D,
                blendParameter = "Blend",
                useAutomaticThresholds = false,
            };
            tree.children = new[]
            {
                new ChildMotion { motion = clip, threshold = 0.25f, timeScale = 1f },
                new ChildMotion { motion = clip, threshold = 0.75f, timeScale = 1f },
            };
            b.motion = tree;

            var t = a.AddTransition(b);
            t.AddCondition(AnimatorConditionMode.If, 0f, "Go");
            t.hasExitTime = false;
            t.duration = 0.1f;
            b.AddExitTransition().AddCondition(AnimatorConditionMode.IfNot, 0f, "Go");
            machine.AddAnyStateTransition(a).AddCondition(AnimatorConditionMode.Greater, 0.9f, "Blend");

            var second = controller.layers[1].stateMachine;
            second.AddState("Only", new Vector3(30f, 30f, 0f));
            var layers = controller.layers;
            layers[1].defaultWeight = 0.25f;
            controller.layers = layers;
            return controller;
        }

        /// <summary>Compiles the sources into a throwaway library and returns the compiler's
        /// error lines. References and defines are taken from the editor assembly's own
        /// compilation settings, plus the built assembly itself — which is the whole point:
        /// referencing the DLL rather than the sources is what makes internals invisible.
        /// </summary>
        IList<string> Compile(string runtime, string compiler, string[] sources)
        {
            var editor = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .FirstOrDefault(a => a.name == "Yozolab.DaerD.Editor");
            Assert.IsNotNull(editor, "Yozolab.DaerD.Editor is not among the editor assemblies");

            var references = new List<string>(editor.allReferences) { editor.outputPath };
            // Unity normally hands the compiler its own framework reference assemblies through
            // allReferences. If a future version stops doing so, find them rather than emit a
            // few thousand misleading errors about missing primitive types.
            if (!references.Any(r => string.Equals(Path.GetFileName(r), "mscorlib.dll",
                    StringComparison.OrdinalIgnoreCase)))
            {
                string api = Path.Combine(EditorApplication.applicationContentsPath,
                    "UnityReferenceAssemblies");
                if (Directory.Exists(api))
                    references.AddRange(Directory.GetFiles(api, "*.dll", SearchOption.AllDirectories));
            }

            var rsp = new StringBuilder();
            rsp.AppendLine("-target:library");
            rsp.AppendLine("-langversion:9.0");
            rsp.AppendLine("-nostdlib+");
            // The exporter's [SerializeField] fields are assigned by Unity, never in code.
            rsp.AppendLine("-nowarn:0649");
            rsp.AppendLine("-out:\"" + Path.Combine(_dir, "probe.dll") + "\"");
            foreach (var define in editor.defines)
                rsp.AppendLine("-define:" + define);
            foreach (var reference in references.Distinct())
                rsp.AppendLine("-r:\"" + Path.GetFullPath(reference) + "\"");
            foreach (var source in sources)
                rsp.AppendLine("\"" + source + "\"");

            string rspPath = Path.Combine(_dir, "probe.rsp");
            File.WriteAllText(rspPath, rsp.ToString());

            var start = new ProcessStartInfo(runtime,
                "\"" + compiler + "\" -nologo \"@" + rspPath + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Directory.GetCurrentDirectory(),
            };

            string output;
            using (var process = Process.Start(start))
            {
                Assert.IsNotNull(process, "could not start " + runtime);
                output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                if (!process.WaitForExit(180000))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    Assert.Fail("the compiler did not finish within three minutes");
                }
            }

            return output.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Contains("error CS"))
                .Distinct()
                .ToList();
        }

        static string RoslynCompiler()
        {
            string path = Path.Combine(EditorApplication.applicationContentsPath,
                "DotNetSdkRoslyn", "csc.dll");
            return File.Exists(path) ? path : null;
        }

        static string DotnetRuntime()
        {
            string dir = Path.Combine(EditorApplication.applicationContentsPath, "NetCoreRuntime");
            foreach (string name in new[] { "dotnet", "dotnet.exe" })
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// DD DynamicAnalyze is meant to stay liftable: everything under <c>Editor/DynamicAnalyze</c>
    /// can become an assembly of its own on the day somebody wants the simulator without the
    /// rest of DaerD, or the rest of DaerD without a simulator. That is a promise about the
    /// EDGE between the two, and an edge is exactly the thing nobody can see while writing a
    /// line of code — a using that resolves, a call that compiles, and the promise is gone
    /// without a single test going red.
    ///
    /// Both halves of it are checked here, and they are not the same check. Nothing in the core
    /// may name the module at all, in any way, comments included: the core is what would stay
    /// behind, and a mention of a module that is no longer there is at best a dangling sentence
    /// and at worst a reference. The module may name the core, because it plainly has to —
    /// it reads a controller, applies VRChat's drivers and speaks the product's language — but
    /// only through names that are written down below with a reason each.
    ///
    /// A scan over source text rather than over compiled types, and deliberately: the assembly
    /// these live in today is one assembly, so a reflection test would be asking the compiler
    /// about a boundary the compiler does not have. Text is what the boundary is made of until
    /// the day there are two .asmdefs, and on that day this test becomes redundant rather than
    /// wrong.
    ///
    /// <para>What the second test cannot see, stated rather than papered over:</para>
    /// <list type="bullet">
    /// <item>A core type used only as a declaration — <c>void F(CoreThing x)</c> with x never
    /// touched — is invisible, because a bare identifier is indistinguishable from a local's
    /// name without parsing C#. Anything the module actually USES gets dereferenced, constructed
    /// or called, and those are the three shapes below.</item>
    /// <item>A core name the module also gives to something of its own (see <c>Shadowed</c>)
    /// drops out: the scan cannot tell <c>Row row</c> from <c>Row row</c>. Two names colliding
    /// is a smaller problem than the false alarm every reader would learn to ignore.</item>
    /// </list>
    /// </summary>
    public class DynamicAnalyzeIndependenceTests
    {
        /// <summary>
        /// Every core name the module is allowed to reach for, and why it is worth reaching for.
        /// The list is meant to shrink and never to grow quietly: adding a line here is the
        /// moment to ask whether the module is still liftable, which is the whole point of
        /// having to add one.
        /// </summary>
        static readonly Dictionary<string, string> Allowed = new Dictionary<string, string>
        {
            // The one behaviour this module simulates rather than notes. Reached through the
            // type-name-based accessor on purpose (see CLAUDE.md): the VRChat SDK may or may not
            // be installed, so the driver is read as a spec rather than cast to.
            { "VrcParameterDriver", "reads a driver's entries so a run can apply them" },
            // The controller, already read once, in a shape the simulator can step. Re-deriving
            // it here would be a second reader of the same asset that could disagree with the
            // first.
            { "ControllerIR", "the controller as data — driver specs, states, conditions" },
            // Which parameters the avatar says travel. The wire takes NAMES, so this is the one
            // call that turns a project's storage into names and the only thing the module knows
            // about how a project stores them.
            { "ParameterStore", "the avatar's own answer to what is synced" },
            // VRChat's built-ins, which cross by VRChat's arrangement rather than the avatar's.
            // A run that carried them like anything else would invent bugs.
            { "VrcParameters", "which names VRChat syncs by itself, and how" },
            // What a run cannot promise: the behaviours this module does not model. Naming them
            // means knowing what they are called.
            { "VrcBehaviours", "the behaviour names SimNotes reports it does not simulate" },
            // The product's language. A module with its own string table would be a second
            // catalogue for a user to find half-translated.
            { "L", "the string table every DaerD window speaks through" },
            // AnimatorControllerExtensions', all three. They never appear as a type name — an
            // extension method is called on the thing it extends — so the scan looks for the
            // call shape and the allowance is per method rather than per class.
            { "SelfAndDescendants", "walks a state machine's tree — AnimatorControllerExtensions" },
            { "AllStateMachines", "every state machine of a controller — same" },
            { "AllBehaviours", "every behaviour of a controller — same" },
        };

        // ---- one: the core does not know the module exists ---------------------

        const string ModuleFolder = "DynamicAnalyze";

        [Test]
        public void NothingOutsideTheModuleNamesTheModule()
        {
            var offenders = new List<string>();
            int scanned = 0;
            foreach (string file in CoreSources())
            {
                scanned++;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Contains(ModuleFolder))
                        offenders.Add(Path.GetFileName(file) + ":" + (i + 1) + "  "
                            + lines[i].Trim());
            }

            Assert.Greater(scanned, 50, "found almost no sources — the scan is broken, not the code");
            // And that the word is one this scan could actually see, which the sources it is
            // aimed at cannot prove: the module names itself constantly.
            Assert.Greater(Mentions(ModuleSources()), 0,
                "the module does not name itself either — the reader is broken, not the code");

            Assert.IsEmpty(offenders,
                "the core would not compile without DD DynamicAnalyze any more, and a comment "
                + "counts: a sentence about a module that has been lifted out is the next "
                + "reader's wild goose chase.\n  " + string.Join("\n  ", offenders));
        }

        static int Mentions(IEnumerable<string> files)
        {
            int found = 0;
            foreach (string file in files)
                if (File.ReadAllText(file).Contains(ModuleFolder)) found++;
            return found;
        }

        // ---- two: the module reaches the core only through the allowed names ----

        [Test]
        public void TheModuleReachesTheCoreOnlyThroughTheNamesItIsAllowed()
        {
            var types = new HashSet<string>();
            var extensions = new HashSet<string>();
            int core = 0;
            foreach (string file in CoreSources())
            {
                core++;
                string code = Code(File.ReadAllText(file));
                foreach (Match match in TypeDeclaration.Matches(code))
                    types.Add(match.Groups[1].Value);
                foreach (Match match in ExtensionDeclaration.Matches(code))
                    extensions.Add(match.Groups[1].Value);
            }

            var shadowed = Shadowed(out int module);
            types.ExceptWith(shadowed);
            extensions.ExceptWith(shadowed);

            Assert.Greater(core, 50, "found almost no core sources — the scan is broken");
            Assert.Greater(module, 10, "found almost no module sources — the scan is broken");
            Assert.Greater(types.Count, 50, "found almost no core types — the scan is broken");
            Assert.Greater(extensions.Count, 5,
                "found almost no core extension methods — the scan is broken");

            // The reader, proved on source written for the purpose rather than trusted. Each of
            // the three shapes it knows, against a real core name picked out of what was just
            // collected — hard-coding one here would make this pass on the day it was written
            // and mean nothing on the day the name changes.
            string type = Unallowed(types), extension = Unallowed(extensions);
            var probe = Uses("probe",
                "class Probe {\n"
                + "  void A() { var made = new " + type + "(); }\n"
                + "  void B() { " + type + ".Anything(1); }\n"
                + "  void C() { thing." + extension + "(); }\n"
                + "}\n", types, extensions, shadowed);
            CollectionAssert.AreEquivalent(new[] { type, extension }, new List<string>(probe.Keys),
                "the scan cannot see a dependency it is looking straight at");

            var found = new SortedDictionary<string, string>();
            foreach (string file in ModuleSources())
                Uses(Path.GetFileName(file), File.ReadAllText(file), types, extensions, shadowed,
                    found);

            // The module does reach the core, so a scan finding nothing has stopped working
            // rather than found a module that stopped needing one.
            Assert.Greater(found.Count, 4,
                "the module is known to depend on the core in several places and the scan sees "
                + "almost none of them");

            var offenders = new List<string>();
            foreach (var pair in found)
                if (!Allowed.ContainsKey(pair.Key))
                    offenders.Add(pair.Key + "  (" + pair.Value + ")");

            Assert.IsEmpty(offenders,
                offenders.Count + " core name(s) are reached from Editor/DynamicAnalyze that the "
                + "module is not allowed to reach. Either use something the module already owns, "
                + "or add the name to Allowed with the reason it is worth being unable to lift "
                + "this module without:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>A core name that is neither allowed nor shadowed — something this scan is
        /// supposed to catch, so it can be asked to catch one.</summary>
        static string Unallowed(HashSet<string> names)
        {
            var sorted = new List<string>(names);
            sorted.Sort();
            foreach (string name in sorted)
                if (!Allowed.ContainsKey(name)) return name;
            Assert.Fail("every core name is allowed, which cannot be right");
            return null;
        }

        /// <summary>
        /// Names the module declares itself. Subtracted from the core's, because a scan of text
        /// cannot tell one <c>Row</c> from another — the module's own <c>Entry</c>, <c>Find</c>,
        /// <c>Frame</c>, <c>Handle</c>, <c>Note</c> and <c>Row</c> all collide with a core name
        /// today, and every one of those collisions would be a false alarm.
        /// </summary>
        static HashSet<string> Shadowed(out int files)
        {
            var own = new HashSet<string>();
            files = 0;
            foreach (string file in ModuleSources())
            {
                files++;
                string code = Code(File.ReadAllText(file));
                foreach (Match match in TypeDeclaration.Matches(code))
                    own.Add(match.Groups[1].Value);
                foreach (Match match in MethodDeclaration.Matches(code))
                    own.Add(match.Groups[1].Value);
            }
            return own;
        }

        /// <summary>
        /// The core names one piece of source reaches for, in the three shapes a dependency has
        /// to take to be worth anything: a name dereferenced (<c>Thing.Member</c>, which is also
        /// how a nested type and a static are spelled), a name constructed, and an extension
        /// method called on something. A name preceded by a dot is somebody's member and never
        /// the head of the expression, which is what keeps <c>VrcParameters.Sync</c> from
        /// reading as a second dependency called Sync.
        /// </summary>
        static SortedDictionary<string, string> Uses(string where, string source,
            HashSet<string> types, HashSet<string> extensions, HashSet<string> shadowed,
            SortedDictionary<string, string> found = null)
        {
            found = found ?? new SortedDictionary<string, string>();
            var lines = Code(source).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Note(Qualified.Matches(lines[i]), types, shadowed, where, i + 1, found);
                Note(Constructed.Matches(lines[i]), types, shadowed, where, i + 1, found);
                Note(Called.Matches(lines[i]), extensions, shadowed, where, i + 1, found);
            }
            return found;
        }

        static void Note(MatchCollection matches, HashSet<string> names, HashSet<string> shadowed,
            string where, int line, SortedDictionary<string, string> found)
        {
            foreach (Match match in matches)
            {
                string name = match.Groups[1].Value;
                if (shadowed.Contains(name) || !names.Contains(name) || found.ContainsKey(name))
                    continue;
                found[name] = where + ":" + line;
            }
        }

        // ---- reading source as source ------------------------------------------

        static readonly Regex TypeDeclaration = new Regex(
            @"\b(?:class|struct|enum|interface)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

        /// <summary>An extension method: a static one whose first parameter is a <c>this</c>.
        /// Collected by name because that is how it is called — the class it is declared in
        /// never appears at the call site, which is the whole trick of one.</summary>
        static readonly Regex ExtensionDeclaration = new Regex(
            @"\bstatic\s+[\w<>\[\],\.\?]+\s+([A-Za-z_]\w*)\s*\(\s*this\s", RegexOptions.Compiled);

        /// <summary>A method declaration, which in this codebase starts its line. Line-anchored
        /// so that <c>return new Thing(</c> in the middle of an expression is not read as
        /// declaring a method called Thing — which would quietly excuse the exact dependency
        /// this is here to find.</summary>
        static readonly Regex MethodDeclaration = new Regex(
            @"^[ \t]*(?:(?:public|internal|private|protected|static|sealed|override|virtual"
            + @"|abstract|readonly|partial|extern|async|unsafe|new)\s+)*[\w<>\[\],\.\?]+\s+"
            + @"([A-Za-z_]\w*)\s*\(",
            RegexOptions.Compiled | RegexOptions.Multiline);

        static readonly Regex Qualified = new Regex(
            @"(?<![.\w])([A-Za-z_]\w*)\s*\.", RegexOptions.Compiled);

        static readonly Regex Constructed = new Regex(
            @"\bnew\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

        static readonly Regex Called = new Regex(
            @"\.([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

        static readonly Regex BlockComment = new Regex(@"/\*.*?\*/",
            RegexOptions.Compiled | RegexOptions.Singleline);

        static readonly Regex LineComment = new Regex(@"//[^\n]*", RegexOptions.Compiled);

        static readonly Regex Written = new Regex(
            @"""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)'", RegexOptions.Compiled);

        /// <summary>
        /// The source with everything that is not code blanked out. Comments go because the
        /// second test is about what the module DOES — a doc comment naming a core class it
        /// deliberately does not use (RunFindings names two, and says why for each) is the
        /// opposite of a dependency. Strings go because a run's rows are named in them.
        ///
        /// Newlines are kept so a report still points at a line. Comments are taken before
        /// strings, which gets a <c>//</c> inside a string wrong; there is none in this package,
        /// and the other order gets a quotation mark inside a comment wrong instead.
        /// </summary>
        static string Code(string source) =>
            Written.Replace(
                LineComment.Replace(BlockComment.Replace(source, Blank), Blank),
                "\"\"");

        /// <summary>The same span of text, minus everything but its line breaks.</summary>
        static string Blank(Match match) => Regex.Replace(match.Value, "[^\n]", " ");

        // ---- where the sources are ---------------------------------------------

        static IEnumerable<string> CoreSources()
        {
            string module = Path.Combine(SourceRoot(), ModuleFolder) + Path.DirectorySeparatorChar;
            foreach (string file in Directory.GetFiles(SourceRoot(), "*.cs",
                         SearchOption.AllDirectories))
                if (!Path.GetFullPath(file).StartsWith(module))
                    yield return file;
        }

        static IEnumerable<string> ModuleSources()
        {
            string module = Path.Combine(SourceRoot(), ModuleFolder);
            Assert.IsTrue(Directory.Exists(module), "could not find the module's sources");
            return Directory.GetFiles(module, "*.cs", SearchOption.AllDirectories);
        }

        /// <summary>The package's Editor folder, found through an asset DaerD owns — the tests
        /// run from a package path that is not the project's. Same trick as DaerDColorsTests.</summary>
        static string SourceRoot()
        {
            var anchor = ScriptableObject.CreateInstance<LocalizationAnchor>();
            var script = MonoScript.FromScriptableObject(anchor);
            string path = AssetDatabase.GetAssetPath(script);
            Object.DestroyImmediate(anchor);
            Assert.IsNotEmpty(path, "could not locate DaerD's own sources");
            // <package>/Editor/Localization/LocalizationAnchor.cs -> <package>/Editor
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), ".."));
        }
    }
}

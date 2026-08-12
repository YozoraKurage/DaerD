using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The colour vocabulary, and the two ways it went wrong before it was one: the same colour
    /// written out in four places, and a default written beside the palette it was supposed to
    /// come from. Neither is the kind of mistake anyone notices by looking — the second one only
    /// shows up months later, as a new frame that is a slightly different blue from the swatch
    /// that made it.
    /// </summary>
    public class DaerDColorsTests
    {
        [Test]
        public void TheDefaultFrameAndNoteColours_ComeFromThePalettesTheyAreOfferedIn()
        {
            // The point is not that they are equal today; it is that there is only one place to
            // change. Editing a palette entry moves the default with it.
            CollectionAssert.Contains(Palette(DaerDColors.FramePalette), DaerDColors.DefaultFrame);
            CollectionAssert.Contains(Palette(DaerDColors.NotePalette), DaerDColors.DefaultNote);

            Assert.AreEqual(DaerDColors.DefaultFrame, new GraphFrameData.Frame().color,
                "a new frame is born the palette's colour");
            Assert.AreEqual(DaerDColors.DefaultNote, new GraphFrameData.Note().color,
                "a new note is born the palette's colour");
        }

        static List<Color> Palette((string name, Color color)[] entries)
        {
            var colors = new List<Color>();
            foreach (var entry in entries) colors.Add(entry.color);
            return colors;
        }

        [Test]
        public void EachPalettesSwatches_AreDistinguishable()
        {
            AssertDistinct(DaerDColors.FramePalette);
            AssertDistinct(DaerDColors.NotePalette);
        }

        static void AssertDistinct((string name, Color color)[] entries)
        {
            for (int i = 0; i < entries.Length; i++)
                for (int j = i + 1; j < entries.Length; j++)
                    Assert.Greater(Distance(entries[i].color, entries[j].color), 0.1f,
                        $"'{entries[i].name}' and '{entries[j].name}' are the same swatch to the eye");
        }

        /// <summary>
        /// The two greens that used to be a shade apart while meaning different things: one says
        /// a dragged clip will land here, the other says this transition is running. Both can be
        /// on screen at once, so they have to be told apart at a glance.
        /// </summary>
        [Test]
        public void TheDropTargetAndTheRunningTransition_DoNotLookAlike()
        {
            Assert.Greater(Distance(DaerDColors.DropTarget, DaerDColors.PlayingEdge), 0.3f);
        }

        static float Distance(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);

        // ---- no scattering it back out ----------------------------------------

        /// <summary>A literal colour: three or four plain numbers. A colour derived from another
        /// one — <c>new Color(c.r, c.g, c.b, alpha)</c>, or the note border's darkening — has an
        /// identifier in it and is not what this is looking for.</summary>
        static readonly Regex Literal = new Regex(
            @"new\s+Color\(\s*-?[\d.]+f?\s*,\s*-?[\d.]+f?\s*,\s*-?[\d.]+f?\s*(,\s*-?[\d.]+f?\s*)?\)",
            RegexOptions.Compiled);

        [Test]
        public void NoColourIsWrittenOutAnywhereButTheOnePlaceForThem()
        {
            string root = SourceRoot();
            var offenders = new List<string>();
            int scanned = 0;

            foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // Two palettes, not a loophole: the product's, and DD DynamicAnalyze's own,
                // which it has because that module is meant to be liftable into its own
                // assembly and a palette it did not own would be what stopped it. The rule
                // both keep is the one this test is for — a colour is written down once,
                // under a name, and nowhere else.
                string leaf = Path.GetFileName(file);
                if (leaf == "DaerDColors.cs" || leaf == "WaveformColors.cs") continue;
                scanned++;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    if (Literal.IsMatch(lines[i]))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
            }

            Assert.Greater(scanned, 50, "found almost no sources — the scan is broken, not the code");
            Assert.IsEmpty(offenders,
                "these colours belong in DaerDColors, named for what they mean:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>The package's Editor folder, found through an asset DaerD owns — the tests
        /// run from a package path that is not the project's.</summary>
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

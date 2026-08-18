using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// The recipe link seen from the recipe's side: which .asset a controller should be
    /// re-exported into, and how strays are taken up.
    ///
    /// <para>WHY A SWEEP EXISTS AT ALL.</para>
    /// The link is written at three moments — an export creates the .asset, a Generate says "I
    /// wrote into this controller", and this. The first two only ever cover recipes made or run
    /// after the link existed; a project that already has recipes has them scattered wherever
    /// past exports put them, and those are exactly the ones the duplicate-pair accident is made
    /// of. So there is one sweep, on the one screen where a person is about to decide where a
    /// recipe goes, and it costs a <c>FindAssets</c> plus one load per recipe asset — not per
    /// asset in the project.
    ///
    /// It runs on an explicit user action and nowhere else (ADR 0028): opening the export form is
    /// that action. Nothing here searches on a repaint, and nothing searches on load.
    /// </summary>
    static class RecipeLinks
    {
        /// <summary>
        /// Links every recipe asset in the project that already names <paramref name="controller"/>
        /// as its target and is not linked yet. Returns how many were taken up.
        ///
        /// The test is the recipe's OWN target field, not a path or a name: a stray recipe is
        /// stray precisely because its path stopped agreeing with the controller's, and the field
        /// is the one thing about it that never lied.
        /// </summary>
        internal static int Adopt(AnimatorController controller)
        {
            if (controller == null) return 0;
            var linked = GraphFrameData.LinkedRecipes(controller);
            var adopted = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(ControllerRecipe)))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var recipe = AssetDatabase.LoadAssetAtPath<ControllerRecipe>(path);
                if (recipe == null || recipe.targetController != controller) continue;
                // Asked before linking rather than left to LinkRecipe's own idempotence: linking
                // creates the holder sub-asset, and a controller that has no recipes and no
                // holder should come out of this sweep with neither.
                if (Contains(linked, recipe)) continue;
                if (GraphFrameData.LinkRecipe(controller, recipe))
                    adopted.Add(recipe.name);
            }
            // Said out loud because it changed saved data without being asked to in so many
            // words, and because a stray recipe turning up is the thing somebody wants to know.
            if (adopted.Count > 0)
                Debug.Log("DaerD: linked " + adopted.Count + " existing recipe asset(s) to '"
                    + controller.name + "': " + string.Join(", ", adopted)
                    + " — a re-export now updates one of these instead of writing a second copy.");
            return adopted.Count;
        }

        /// <summary>
        /// Which linked recipe an export should default to updating: an index into
        /// <paramref name="links"/>, or -1 when there is nothing to update and the export is a
        /// new one.
        ///
        /// One link is the whole answer — that is the case the link exists for, and asking a
        /// second question about it would be theatre. With several, the layers somebody has
        /// ticked are the only evidence on the screen about which recipe they mean, so the first
        /// link that already owns one of them wins; a recipe that owns nothing of what is being
        /// exported is a poor guess however recently it was touched. Falling back to the first
        /// link rather than to "new" is deliberate: writing a second recipe is the accident this
        /// whole wave is about, so the default never proposes it while any link exists.
        ///
        /// A decision function rather than a shape the popup happens to have, because it is the
        /// one part of the form worth pinning — the IMGUI around it is checked by hand.
        /// </summary>
        internal static int PickDefault(IReadOnlyList<ControllerRecipe> links,
            ICollection<string> checkedLayers)
        {
            if (links == null || links.Count == 0) return -1;
            if (links.Count == 1) return 0;
            if (checkedLayers != null && checkedLayers.Count > 0)
                for (int i = 0; i < links.Count; i++)
                {
                    var owned = links[i] != null ? links[i].OwnedLayers : null;
                    if (owned == null) continue;
                    foreach (var name in owned)
                        if (checkedLayers.Contains(name)) return i;
                }
            return 0;
        }

        /// <summary>
        /// Where a linked recipe's code lives: the folder holding its hand half and the class
        /// name the two halves share. False when Unity cannot name the script — a recipe asset
        /// whose class is gone, or one made some way that left no MonoScript behind — in which
        /// case there is no pair to update and the form says so instead of guessing a folder.
        /// </summary>
        internal static bool ScriptLocation(ControllerRecipe recipe, out string folder,
            out string className)
        {
            folder = null;
            className = recipe != null ? recipe.GetType().Name : null;
            if (recipe == null) return false;
            var script = MonoScript.FromScriptableObject(recipe);
            var path = script != null ? AssetDatabase.GetAssetPath(script) : null;
            if (string.IsNullOrEmpty(path)) return false;
            folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
            return !string.IsNullOrEmpty(folder);
        }

        /// <summary>Membership through the <c>==</c> Unity overloads, spelled out rather than left
        /// to <c>List.Contains</c>: everything else in DaerD that asks whether an Object reference
        /// is a particular live asset asks it this way, and one place doing it through a default
        /// comparer instead is one place to have to re-derive the answer.</summary>
        static bool Contains(List<UnityEngine.Object> list, UnityEngine.Object item)
        {
            foreach (var entry in list)
                if (entry == item) return true;
            return false;
        }
    }
}

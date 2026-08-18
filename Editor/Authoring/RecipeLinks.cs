using System.Collections.Generic;
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

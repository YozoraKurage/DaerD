using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Authoring;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Which linked recipe a re-export defaults to updating.
    ///
    /// The decision is pulled out of the form because it is the one part of it worth pinning: the
    /// default is what decides, for somebody who does not read the popup, whether a re-export
    /// updates the recipe that already exists or writes a second one beside it — and writing a
    /// second one is the whole accident this wave is about. The IMGUI around it is checked by
    /// hand, as the other cards are.
    /// </summary>
    public class RecipePickDefaultTests
    {
        readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup)
                if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        /// <summary>A recipe that owns one named layer, or none at all when no name is given.
        /// OwnedLayers is filled by a Generate and by nothing else, so the fixture runs one
        /// against a throwaway in-memory controller rather than adding a test-only setter to the
        /// recipe API. Which layer it owns is the only fact the decision reads.</summary>
        LinkTestRecipe Owning(string layer = null)
        {
            var recipe = ScriptableObject.CreateInstance<LinkTestRecipe>();
            _cleanup.Add(recipe);
            if (layer == null) return recipe;

            var controller = new AnimatorController();
            _cleanup.Add(controller);
            recipe.targetController = controller;
            recipe.layerName = layer;
            recipe.Generate();
            Assert.Contains(layer, new List<string>(recipe.OwnedLayers),
                "the fixture failed to give this recipe a layer to own");
            return recipe;
        }

        static List<ControllerRecipe> Links(params ControllerRecipe[] recipes) =>
            new List<ControllerRecipe>(recipes);

        [Test]
        public void NoLinksMeansANewRecipe()
        {
            Assert.AreEqual(-1, RecipeLinks.PickDefault(Links(), new List<string> { "A" }));
            Assert.AreEqual(-1, RecipeLinks.PickDefault(null, null));
        }

        /// <summary>One link is the whole answer — asking a second question about it would be
        /// theatre, and it is the case the link exists for.</summary>
        [Test]
        public void ASingleLinkIsTheDefaultWhateverIsTicked()
        {
            var only = Owning("Something Else");
            Assert.AreEqual(0, RecipeLinks.PickDefault(Links(only),
                new List<string> { "Unrelated" }));
        }

        /// <summary>With several, the ticked layers are the only evidence on screen about which
        /// recipe is meant, so the first link that already owns one of them wins.</summary>
        [Test]
        public void TheFirstLinkOwningATickedLayerWins()
        {
            var hands = Owning("Hands");
            var face = Owning("Face");
            Assert.AreEqual(1, RecipeLinks.PickDefault(Links(hands, face),
                new List<string> { "Face" }));
            Assert.AreEqual(0, RecipeLinks.PickDefault(Links(hands, face),
                new List<string> { "Hands", "Face" }),
                "with both ticked the earlier link should win");
        }

        /// <summary>Falling back to the first link rather than to "new" is deliberate: writing a
        /// second recipe beside an existing one is the accident this exists to prevent, so the
        /// default never proposes it while any link exists.</summary>
        [Test]
        public void NoIntersectionFallsBackToTheFirstLinkAndNotToNew()
        {
            var hands = Owning("Hands");
            var face = Owning("Face");
            Assert.AreEqual(0, RecipeLinks.PickDefault(Links(hands, face),
                new List<string> { "Nothing In Common" }));
            Assert.AreEqual(0, RecipeLinks.PickDefault(Links(hands, face), new List<string>()));
            Assert.AreEqual(0, RecipeLinks.PickDefault(Links(hands, face), null));
        }

        /// <summary>A recipe that has never generated owns nothing. It is a normal state — a
        /// freshly exported recipe looks exactly like this — so it must not throw and must not
        /// win over one that does own a ticked layer.</summary>
        [Test]
        public void ALinkThatOwnsNothingIsSkippedRatherThanCrashing()
        {
            var fresh = Owning();
            var face = Owning("Face");
            Assert.AreEqual(1, RecipeLinks.PickDefault(Links(fresh, face),
                new List<string> { "Face" }));
        }
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Authoring;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The link that says where a controller's C# recipes are.
    ///
    /// The accident it exists for: with no link, an export decides the recipe's folder and class
    /// name from scratch every time, so moving the controller (or having two with the same name)
    /// writes a SECOND pair of files under the same class name. Either they land in one assembly
    /// and compilation stops, or they land in two and Generate keeps running whichever copy the
    /// loaded assembly holds — both of which look like "the same old content every time".
    ///
    /// Three moments record the link, and each is pinned here: the export queue creating the
    /// .asset, a Generate adopting itself, and the sweep that takes up recipes made before any of
    /// this existed. The point of having three is that the second and third are no-ops once the
    /// first has run, so the storage has to be idempotent — which is the first test below.
    /// </summary>
    public class RecipeLinkTests
    {
        const string Folder = "Assets/DDRecipeLink";
        const string ControllerPath = Folder + "/Linked.controller";
        const string OtherPath = Folder + "/Other.controller";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", "DDRecipeLink");
            GraphFrameData.ForgetHolders();
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            GraphFrameData.ForgetHolders();
        }

        /// <summary>A controller as a real file: the link lives in a hidden sub-asset of the
        /// .controller, so an in-memory one would store nothing and prove nothing.</summary>
        static AnimatorController NewController(string path)
        {
            AssetDatabase.CreateAsset(new AnimatorController(), path);
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        }

        /// <summary>A recipe as a real file, loaded back through the AssetDatabase so the
        /// reference under test is the one the project holds rather than the instance the test
        /// happens to have made.</summary>
        static LinkTestRecipe SavedRecipe(string path, AnimatorController target)
        {
            var recipe = ScriptableObject.CreateInstance<LinkTestRecipe>();
            recipe.targetController = target;
            AssetDatabase.CreateAsset(recipe, path);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<LinkTestRecipe>(path);
        }

        [Test]
        public void LinkingTheSameRecipeTwiceRecordsItOnce()
        {
            var controller = NewController(ControllerPath);
            var recipe = SavedRecipe(Folder + "/A.asset", controller);

            Assert.IsTrue(GraphFrameData.LinkRecipe(controller, recipe), "the first link wrote nothing");
            Assert.IsFalse(GraphFrameData.LinkRecipe(controller, recipe),
                "the second link claimed to have written something");

            var linked = GraphFrameData.LinkedRecipes(controller);
            Assert.AreEqual(1, linked.Count, "the same recipe was recorded twice");
            Assert.AreEqual(recipe, linked[0]);
        }

        /// <summary>A deleted recipe is gone for good, so its entry is dropped on read — the rule
        /// the code-owned marks already keep. What is NOT dropped is a link whose recipe now
        /// names another controller: which of the two is the mistake is not DaerD's to decide.
        /// </summary>
        [Test]
        public void ReadingDropsRecipesWhoseAssetWasDeleted()
        {
            var controller = NewController(ControllerPath);
            var gone = SavedRecipe(Folder + "/Gone.asset", controller);
            var kept = SavedRecipe(Folder + "/Kept.asset", controller);
            GraphFrameData.LinkRecipe(controller, gone);
            GraphFrameData.LinkRecipe(controller, kept);
            AssetDatabase.SaveAssets();
            Assert.AreEqual(2, GraphFrameData.LinkedRecipes(controller).Count);

            AssetDatabase.DeleteAsset(Folder + "/Gone.asset");

            var linked = GraphFrameData.LinkedRecipes(controller);
            Assert.AreEqual(1, linked.Count, "the dead reference was handed back");
            Assert.AreEqual(kept, linked[0]);
        }

        /// <summary>
        /// The export's own recording point, driven at the one step a test can reach: the queue
        /// finishing an export after the compile. The reload before it is what makes the whole
        /// path untestable, and it is not what this claim is about.
        /// </summary>
        [Test]
        public void TheExportQueueLinksTheRecipeItCreates()
        {
            var controller = NewController(ControllerPath);
            RecipeExportQueue.Enqueue(typeof(LinkTestRecipe).FullName,
                Folder + "/Queued.asset", controller, false,
                new List<RecipeExporter.FieldRef>());

            RecipeExportQueue.Process();

            var recipe = AssetDatabase.LoadAssetAtPath<ControllerRecipe>(Folder + "/Queued.asset");
            Assert.IsNotNull(recipe, "the queue never created the recipe asset");
            var linked = GraphFrameData.LinkedRecipes(controller);
            Assert.AreEqual(1, linked.Count, "the created recipe was not linked to its controller");
            Assert.AreEqual(recipe, linked[0]);
        }

        /// <summary>The self-healing point: a recipe that writes into a controller belongs to it,
        /// whether or not it was made by an export that knew about links. This is the migration
        /// path for every .asset that already exists.</summary>
        [Test]
        public void GeneratingLinksTheRecipeToTheControllerItWroteInto()
        {
            var controller = NewController(ControllerPath);
            var recipe = SavedRecipe(Folder + "/Generating.asset", controller);
            Assert.IsEmpty(GraphFrameData.LinkedRecipes(controller));

            recipe.Generate();

            var linked = GraphFrameData.LinkedRecipes(controller);
            Assert.AreEqual(1, linked.Count, "Generate did not adopt its own recipe");
            Assert.AreEqual(recipe, linked[0]);
        }

        /// <summary>
        /// The sweep, which is how a project full of recipes made before the link existed becomes
        /// visible at all. It matches on the recipe's own target field rather than on a path: a
        /// stray recipe is stray exactly because its folder stopped agreeing with the
        /// controller's, and the field is the half of it that never lied.
        /// </summary>
        [Test]
        public void AdoptTakesUpStrayRecipesAndLeavesOtherControllersAlone()
        {
            var controller = NewController(ControllerPath);
            var other = NewController(OtherPath);
            var mine = SavedRecipe(Folder + "/Stray.asset", controller);
            SavedRecipe(Folder + "/Foreign.asset", other);
            AssetDatabase.SaveAssets();

            Assert.AreEqual(1, RecipeLinks.Adopt(controller), "the stray recipe was not found");

            var linked = GraphFrameData.LinkedRecipes(controller);
            Assert.AreEqual(1, linked.Count);
            Assert.AreEqual(mine, linked[0]);
            Assert.IsEmpty(GraphFrameData.LinkedRecipes(other),
                "a recipe was linked to a controller it does not target");
            Assert.AreEqual(0, RecipeLinks.Adopt(controller),
                "sweeping again took the same recipe up a second time");
        }
    }
}

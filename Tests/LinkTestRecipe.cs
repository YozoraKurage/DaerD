using Yozolab.DaerD.Authoring;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// A recipe that can exist as a real .asset on disk.
    ///
    /// It gets a file of its own, unlike the doubles declared inside AuthoringTests, because Unity
    /// files a MonoScript only under the class its FILE is named after. A ScriptableObject asset
    /// saved for a class with no MonoScript behind it comes back as null on the next load — which
    /// is precisely the thing the link tests need to store and read back.
    /// </summary>
    class LinkTestRecipe : ControllerRecipe
    {
        /// <summary>The one layer this recipe declares, so a Generate has something to own.</summary>
        public string layerName = "Recipe Layer";

        protected override void Build(ControllerBuilder c) => c.Layer(layerName).NewState("Idle");
    }
}

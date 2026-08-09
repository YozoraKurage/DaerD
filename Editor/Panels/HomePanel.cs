namespace Yozolab.DaerD
{
    /// <summary>
    /// The controller-wide screen, shown in the centre pane instead of the graph while Home is
    /// picked in the layer list. Everything here is about the controller rather than about any
    /// one layer — the assets it is associated with, the generated things saved with it
    /// (gadgets, sync setups, recipe-owned layers) and the tools that act on all of it — which
    /// is exactly what a layer's graph has no room for.
    /// </summary>
    class HomePanel : PanelBase
    {
        public HomePanel(DaerDContext context) : base(context, "Home") { }

        protected override void DrawContent()
        {
        }
    }
}

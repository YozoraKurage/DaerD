namespace Yozolab.DaerD
{
    /// <summary>Notifications that always travel together, kept in one place so every caller
    /// fires the same set in the same order.</summary>
    static class DaerDContextExtensions
    {
        /// <summary>
        /// A wizard (toggle builder, async sync, layer template, DBT gadget) added parameters,
        /// clips and possibly a whole layer — let every panel and the graph pick that up, and
        /// show the layer it landed in. Omit <paramref name="layerIndex"/> when the change has
        /// no layer to open.
        /// </summary>
        public static void NotifyLayerStructureChanged(this DaerDContext context, int layerIndex = -1)
        {
            var controller = context.Controller;
            context.NotifyParametersChanged();
            context.NotifyLayersChanged();
            context.NotifyGraphStructureChanged();
            if (controller != null && layerIndex >= 0 && layerIndex < controller.layers.Length)
                context.SetLayer(layerIndex);
        }
    }
}

using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The built-in editor icons DaerD marks its own rows with, named for what they MEAN rather
    /// than for the string Unity files them under — the same bargain <see cref="DaerDColors"/>
    /// makes with colour, and for the same reason: "d_CustomTool" says nothing about why a row
    /// has it, and the day a better glyph turns up there is one place to change.
    ///
    /// The <c>d_</c> variants are the dark-theme ones. DaerD is dark-theme-only today (ADR 0034),
    /// stated here rather than discovered later by somebody on the light skin.
    ///
    /// What is NOT here: the icons that identify an ASSET — a blend tree, a clip, a prefab. Those
    /// are Unity's own type icons, asked for where the type is known, and they mean the same
    /// thing in DaerD as everywhere else in the editor. This file is for the marks DaerD invents.
    /// </summary>
    static class DaerDIcons
    {
        static Texture s_generated;
        static Texture s_settings;

        /// <summary>The mark on a layer DaerD generated, with the sentence that says on whose
        /// behalf. A content per call — the tooltip is the layer's own, and the translation has
        /// to follow a language change — over an image that is looked up once.</summary>
        public static GUIContent Generated(string tooltip) =>
            new GUIContent(s_generated ??= EditorGUIUtility.IconContent("d_CustomTool").image, tooltip);

        /// <summary>The gear on a layer row that opens its settings.</summary>
        public static GUIContent LayerSettings =>
            new GUIContent(s_settings ??= EditorGUIUtility.IconContent("_Popup").image,
                L.Tr("Layer settings"));
    }
}

using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Marker type whose only job is to point at this folder. <see cref="PoCatalog"/> asks the
    /// AssetDatabase where this script lives to find the .po files next to it, which keeps the
    /// lookup working wherever the package is installed (Assets, Packages, a VPM cache).
    /// </summary>
    class LocalizationAnchor : ScriptableObject
    {
    }
}

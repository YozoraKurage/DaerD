using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Callbacks;

namespace Yozolab.DaerD
{
    /// <summary>Routes .controller assets to the custom editor window via menus and (optionally) double-click.</summary>
    static class ControllerOpenHandler
    {
        const string AssetMenuPath = "Assets/Open in DaerD";

        [OnOpenAsset(0)]
        static bool OnOpenController(int instanceID, int line)
        {
            if (!DaerDSettings.InterceptDoubleClick)
                return false;
            if (EditorUtility.InstanceIDToObject(instanceID) is AnimatorController controller)
            {
                DaerDWindow.Open(controller);
                return true;
            }
            return false;
        }

        [MenuItem("YozoLab/DaerD")]
        static void OpenFromMenu()
        {
            DaerDWindow.Open(Selection.activeObject as AnimatorController);
        }

        [MenuItem(AssetMenuPath, false, 20)]
        static void OpenFromAssetContext()
        {
            DaerDWindow.Open(Selection.activeObject as AnimatorController);
        }

        [MenuItem(AssetMenuPath, true)]
        static bool ValidateOpenFromAssetContext()
        {
            return Selection.activeObject is AnimatorController;
        }

        [MenuItem("CONTEXT/AnimatorController/Open in DaerD")]
        static void OpenFromContext(MenuCommand command)
        {
            DaerDWindow.Open(command.context as AnimatorController);
        }
    }
}

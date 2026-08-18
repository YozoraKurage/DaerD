using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Yozolab.DaerD.Bridge;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Stand-in for the VRC SDK menu asset: same type name and serialized field layout, so
    /// the SerializedObject-based accessor works against it without the SDK installed.
    /// The tests build menus by filling these fields directly — the authoring API this
    /// fixture used to drive was deleted with the menu editor (2026-08-18), and what is
    /// left to test is the reading half and the rename cascade that live features call.
    /// </summary>
    class VRCExpressionsMenu : ScriptableObject
    {
        public const int MAX_CONTROLS = 8;

        [System.Serializable]
        public class Parameter
        {
            public string name = string.Empty;
        }

        [System.Serializable]
        public class Label
        {
            public string name = string.Empty;
            public Texture2D icon;
        }

        [System.Serializable]
        public class Control
        {
            public string name = string.Empty;
            public Texture2D icon;
            public int type = 101;
            public Parameter parameter = new Parameter();
            public float value = 1f;
            public Object subMenu;
            public Parameter[] subParameters = new Parameter[0];
            public Label[] labels = new Label[0];
        }

        public List<Control> controls = new List<Control>();
    }

    public class VrcMenuAccessTests
    {
        static VRCExpressionsMenu NewMenu() =>
            ScriptableObject.CreateInstance<VRCExpressionsMenu>();

        static VRCExpressionsMenu.Control Control(string name,
            VrcMenuAccess.ControlType type = VrcMenuAccess.ControlType.Toggle,
            string parameter = "", Object subMenu = null, params string[] subParameters)
        {
            var control = new VRCExpressionsMenu.Control
            {
                name = name,
                type = (int)type,
                parameter = new VRCExpressionsMenu.Parameter { name = parameter },
                subMenu = subMenu,
            };
            var slots = new List<VRCExpressionsMenu.Parameter>();
            foreach (var sub in subParameters)
                slots.Add(new VRCExpressionsMenu.Parameter { name = sub });
            control.subParameters = slots.ToArray();
            return control;
        }

        [Test]
        public void Read_HandsBackEveryFieldTheLiveFeaturesAsk()
        {
            var menu = NewMenu();
            menu.controls.Add(Control("Hat", VrcMenuAccess.ControlType.Toggle, "Hat"));
            menu.controls.Add(Control("Spin", VrcMenuAccess.ControlType.RadialPuppet,
                subParameters: "Spin/Amount"));

            var controls = VrcMenuAccess.Read(menu);

            Assert.AreEqual(2, controls.Count);
            Assert.AreEqual("Hat", controls[0].parameter);
            Assert.AreEqual(VrcMenuAccess.ControlType.Toggle, controls[0].type);
            Assert.AreEqual(VrcMenuAccess.ControlType.RadialPuppet, controls[1].type);
            Assert.AreEqual("Spin/Amount", controls[1].subParameters[0]);
        }

        [Test]
        public void RenameParameterReferences_TraversesSubMenusCycleSafe()
        {
            var root = NewMenu();
            var child = NewMenu();

            root.controls.Add(Control("Old User", parameter: "Old"));
            root.controls.Add(Control("More", VrcMenuAccess.ControlType.SubMenu, subMenu: child));
            child.controls.Add(Control("Spin", VrcMenuAccess.ControlType.RadialPuppet,
                subParameters: "Old"));
            // Cycle: the child links back to the root.
            child.controls.Add(Control("Back", VrcMenuAccess.ControlType.SubMenu, subMenu: root));

            int touched = VrcMenuAccess.RenameParameterReferences(root, "Old", "New");

            Assert.AreEqual(2, touched);
            Assert.AreEqual("New", VrcMenuAccess.Read(root)[0].parameter);
            Assert.AreEqual("New", VrcMenuAccess.Read(child)[0].subParameters[0]);
        }
    }
}

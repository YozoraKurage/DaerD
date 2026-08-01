using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Stand-in for the VRC SDK menu asset: same type name and serialized field layout, so
    /// the SerializedObject-based accessor works against it without the SDK installed.
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

        [Test]
        public void AddControl_UsesDefaultsAndRespectsTheCap()
        {
            var menu = NewMenu();
            Assert.AreEqual(8, VrcMenuAccess.MaxControls(menu));

            int index = VrcMenuAccess.AddControl(menu);
            Assert.AreEqual(0, index);
            var controls = VrcMenuAccess.Read(menu);
            Assert.AreEqual("New Control", controls[0].name);
            Assert.AreEqual(VrcMenuAccess.ControlType.Toggle, controls[0].type);

            for (int i = 1; i < 8; i++)
                Assert.AreEqual(i, VrcMenuAccess.AddControl(menu));
            Assert.AreEqual(-1, VrcMenuAccess.AddControl(menu));   // cap reached
        }

        [Test]
        public void SetType_GrowsPuppetArrays()
        {
            var menu = NewMenu();
            int index = VrcMenuAccess.AddControl(menu);
            VrcMenuAccess.SetType(menu, index, VrcMenuAccess.ControlType.FourAxisPuppet);

            var control = VrcMenuAccess.Read(menu)[index];
            Assert.AreEqual(VrcMenuAccess.ControlType.FourAxisPuppet, control.type);
            Assert.GreaterOrEqual(control.subParameters.Count, 4);
            Assert.GreaterOrEqual(control.labels.Count, 4);
        }

        [Test]
        public void EditMoveRemove_Work()
        {
            var menu = NewMenu();
            VrcMenuAccess.AddControl(menu);
            VrcMenuAccess.AddControl(menu);
            VrcMenuAccess.SetName(menu, 0, "A");
            VrcMenuAccess.SetName(menu, 1, "B");
            VrcMenuAccess.SetParameter(menu, 0, "Hat");
            VrcMenuAccess.SetValue(menu, 0, 3f);

            var controls = VrcMenuAccess.Read(menu);
            Assert.AreEqual("Hat", controls[0].parameter);
            Assert.AreEqual(3f, controls[0].value);

            Assert.IsTrue(VrcMenuAccess.MoveControl(menu, 0, 1));
            Assert.AreEqual("B", VrcMenuAccess.Read(menu)[0].name);

            Assert.IsTrue(VrcMenuAccess.RemoveControl(menu, 0));
            controls = VrcMenuAccess.Read(menu);
            Assert.AreEqual(1, controls.Count);
            Assert.AreEqual("A", controls[0].name);
        }

        [Test]
        public void RenameParameterReferences_TraversesSubMenusCycleSafe()
        {
            var root = NewMenu();
            var child = NewMenu();

            int rootControl = VrcMenuAccess.AddControl(root);
            VrcMenuAccess.SetParameter(root, rootControl, "Old");
            int link = VrcMenuAccess.AddControl(root);
            VrcMenuAccess.SetType(root, link, VrcMenuAccess.ControlType.SubMenu);
            VrcMenuAccess.SetSubMenu(root, link, child);

            int puppet = VrcMenuAccess.AddControl(child);
            VrcMenuAccess.SetType(child, puppet, VrcMenuAccess.ControlType.RadialPuppet);
            VrcMenuAccess.SetSubParameter(child, puppet, 0, "Old");
            // Cycle: the child links back to the root.
            int back = VrcMenuAccess.AddControl(child);
            VrcMenuAccess.SetType(child, back, VrcMenuAccess.ControlType.SubMenu);
            VrcMenuAccess.SetSubMenu(child, back, root);

            int touched = VrcMenuAccess.RenameParameterReferences(root, "Old", "New");
            Assert.AreEqual(2, touched);
            Assert.AreEqual("New", VrcMenuAccess.Read(root)[rootControl].parameter);
            Assert.AreEqual("New", VrcMenuAccess.Read(child)[puppet].subParameters[0]);
        }
    }
}

using NUnit.Framework;
using UnityEditor.Animations;
using UnityEditor.PackageManager;
using UnityEngine;
using Yozolab.DaerD.Bridge;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// <see cref="SavedByVersion.Format"/> as a pure function over every source/`.git` combination
    /// that matters, plus one real write through <see cref="GraphFrameData.GetOrCreate"/> — the
    /// stamping call site — to pin that a save actually ends up carrying a version.
    /// </summary>
    public class SavedByVersionTests
    {
        [Test]
        public void Format_EmptyOrNullVersion_IsEmpty_FailOpen()
        {
            Assert.AreEqual("", SavedByVersion.Format("", PackageSource.Registry, false));
            Assert.AreEqual("", SavedByVersion.Format(null, PackageSource.Local, true));
        }

        [Test]
        public void Format_Registry_WithoutDotGit_IsTheBareVersion()
        {
            Assert.AreEqual("1.2.3", SavedByVersion.Format("1.2.3", PackageSource.Registry, false));
        }

        [Test]
        public void Format_Embedded_WithoutDotGit_IsTheBareVersion()
        {
            // A VPM/VCC install lands as a Packages/ copy Unity reports as Embedded — same as a
            // release archive unless a .git marker says this copy can drift from the tag.
            Assert.AreEqual("1.2.3", SavedByVersion.Format("1.2.3", PackageSource.Embedded, false));
        }

        [Test]
        public void Format_Embedded_WithDotGit_IsDev()
        {
            Assert.AreEqual("1.2.3+dev", SavedByVersion.Format("1.2.3", PackageSource.Embedded, true));
        }

        [Test]
        public void Format_Local_IsAlwaysDev()
        {
            Assert.AreEqual("1.2.3+dev", SavedByVersion.Format("1.2.3", PackageSource.Local, false));
        }

        [Test]
        public void Format_Git_IsAlwaysDev()
        {
            Assert.AreEqual("1.2.3+dev", SavedByVersion.Format("1.2.3", PackageSource.Git, false));
        }

        [Test]
        public void GetOrCreate_StampsSavedByVersion_AndInThisEnvironmentItEndsWithDev()
        {
            // This test project references DaerD as file:/workspace — a Local package source —
            // so the stamp GetOrCreate writes is assertable down to the +dev suffix here.
            var controller = new AnimatorController();
            controller.AddLayer("Base");

            var data = GraphFrameData.GetOrCreate(controller);

            Assert.IsFalse(string.IsNullOrEmpty(data.savedByVersion));
            Assert.IsTrue(data.savedByVersion.EndsWith("+dev"),
                $"expected a +dev stamp in this Local-package test environment, got '{data.savedByVersion}'");

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }
    }
}

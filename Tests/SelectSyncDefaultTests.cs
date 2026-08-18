using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Select Sync ships ON, and "ON" has to mean the subsystem is running — not just that the
    /// toolbar draws a checked box. The two came apart once: the default was applied by setting
    /// the toggle's value inside BuildToolbar, before the toolbar had a panel, and UIElements
    /// drops a ChangeEvent on a panel-less element. The toggle read ON, AnimationWindowSync was
    /// never enabled, and the sync only started working after the user toggled it off and on.
    ///
    /// Opening the Animation window is the visible half of enabling the sync, so it is what this
    /// asserts: no window opened means SetEnabled never ran.
    /// </summary>
    public class SelectSyncDefaultTests
    {
        [Test]
        public void OpeningTheWindow_StartsTheSync_NotJustItsToggle()
        {
            // Start from "no Animation window", so its presence afterwards can only come from
            // the enable path.
            for (var open = AnimationWindowAccess.FindOpen(); open != null; open = AnimationWindowAccess.FindOpen())
                open.Close();

            // Showing (and closing) a window that hosts a GraphView logs a device error under
            // -nographics. The UI tree is still built, which is all this asserts on.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try
            {
                var window = ScriptableObject.CreateInstance<DaerDWindow>();
                try
                {
                    window.Show();

                    var toggle = window.rootVisualElement.Q<ToolbarToggle>();
                    Assert.That(toggle, Is.Not.Null, "the toolbar was never built");
                    Assert.That(toggle.value, Is.True, "Select Sync is supposed to ship ON");
                    Assert.That(AnimationWindowAccess.FindOpen(), Is.Not.Null,
                        "the toggle reads ON but the sync was never enabled");
                }
                finally
                {
                    window.Close();
                    Object.DestroyImmediate(window);
                    for (var open = AnimationWindowAccess.FindOpen(); open != null; open = AnimationWindowAccess.FindOpen())
                        open.Close();
                }
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }
        }
    }
}

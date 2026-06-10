using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class AnimatorControllerExtensionsTests
    {
        [Test]
        public void AllBlendTrees_TerminatesAndDeduplicates_OnCyclicTree()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var state = sm.AddState("S");

            var a = new BlendTree { name = "A" };
            var b = new BlendTree { name = "B" };
            a.children = new[] { new ChildMotion { motion = b, timeScale = 1f } };
            // Force the cycle that the UI prevents, to prove traversal still terminates.
            b.children = new[] { new ChildMotion { motion = a, timeScale = 1f } };
            state.motion = a;

            var seen = new List<BlendTree>();
            foreach (var tree in controller.AllBlendTrees())
                seen.Add(tree);

            Assert.AreEqual(2, seen.Count);
            CollectionAssert.Contains(seen, a);
            CollectionAssert.Contains(seen, b);

            Object.DestroyImmediate(a);
            Object.DestroyImmediate(b);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ContainsTree_FindsSelfDirectAndNestedReferences()
        {
            var root = new BlendTree { name = "Root" };
            var mid = new BlendTree { name = "Mid" };
            var leaf = new BlendTree { name = "Leaf" };
            var unrelated = new BlendTree { name = "Unrelated" };
            root.children = new[] { new ChildMotion { motion = mid, timeScale = 1f } };
            mid.children = new[] { new ChildMotion { motion = leaf, timeScale = 1f } };

            Assert.IsTrue(root.ContainsTree(root));
            Assert.IsTrue(root.ContainsTree(mid));
            Assert.IsTrue(root.ContainsTree(leaf));
            Assert.IsFalse(root.ContainsTree(unrelated));
            Assert.IsFalse(leaf.ContainsTree(root));

            Object.DestroyImmediate(root);
            Object.DestroyImmediate(mid);
            Object.DestroyImmediate(leaf);
            Object.DestroyImmediate(unrelated);
        }

        [Test]
        public void ContainsTree_TerminatesOnExistingCycle()
        {
            var a = new BlendTree { name = "A" };
            var b = new BlendTree { name = "B" };
            a.children = new[] { new ChildMotion { motion = b, timeScale = 1f } };
            b.children = new[] { new ChildMotion { motion = a, timeScale = 1f } };

            Assert.IsTrue(a.ContainsTree(b));
            Assert.IsTrue(b.ContainsTree(a));

            Object.DestroyImmediate(a);
            Object.DestroyImmediate(b);
        }
    }
}

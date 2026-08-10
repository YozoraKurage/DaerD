using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class AnimatorControllerExtensionsTests
    {
        /// <summary>
        /// Sharing, not cycling, is what the visited set in the walk is for. A cycle cannot
        /// be built: closing one makes the engine sever the link into it (see
        /// <see cref="ContainsTree_ReadsWhatUnityLeavesWhenACycleIsAttempted"/>). Sharing is
        /// ordinary — one tree hung off two states, and nested twice under a third — and
        /// without the guard each route would yield it again.
        /// </summary>
        [Test]
        public void AllBlendTrees_YieldsASharedTreeOnce()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;

            var shared = new BlendTree { name = "Shared" };
            var root = new BlendTree { name = "Root" };
            root.children = new[]
            {
                new ChildMotion { motion = shared, timeScale = 1f },
                new ChildMotion { motion = shared, timeScale = 1f },
            };
            sm.AddState("A").motion = root;
            sm.AddState("B").motion = shared;

            var seen = new List<BlendTree>();
            foreach (var tree in controller.AllBlendTrees())
                seen.Add(tree);

            Assert.AreEqual(2, seen.Count);
            CollectionAssert.Contains(seen, root);
            CollectionAssert.Contains(seen, shared);

            Object.DestroyImmediate(root);
            Object.DestroyImmediate(shared);
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

        /// <summary>
        /// There is no way to hand this method a cyclic tree. Whatever closes the loop — the
        /// children setter, a SerializedObject write, a self-reference, a longer ring — the
        /// engine answers by nulling the child that pointed back in, so the shape left over
        /// is a chain. The guard in ContainsTree stays as insurance against data that never
        /// went through the engine; what can be asserted is that A really does lose its link
        /// the moment B reaches back for it, and that both answers follow from that.
        /// </summary>
        [Test]
        public void ContainsTree_ReadsWhatUnityLeavesWhenACycleIsAttempted()
        {
            var a = new BlendTree { name = "A" };
            var b = new BlendTree { name = "B" };
            a.children = new[] { new ChildMotion { motion = b, timeScale = 1f } };
            Assert.IsTrue(a.ContainsTree(b), "A → B before the loop is closed");

            b.children = new[] { new ChildMotion { motion = a, timeScale = 1f } };

            Assert.IsTrue(b.ContainsTree(a));
            Assert.IsFalse(a.ContainsTree(b), "closing the loop severs A's child");
            Assert.IsNull(a.children[0].motion);

            Object.DestroyImmediate(a);
            Object.DestroyImmediate(b);
        }
    }
}

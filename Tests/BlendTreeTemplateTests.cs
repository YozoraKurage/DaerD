using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class BlendTreeTemplateTests
    {
        static BlendTree NewSubtree()
        {
            var inner = new BlendTree
            {
                name = "Inner",
                blendType = BlendTreeType.Direct,
            };
            inner.AddChild(new AnimationClip { name = "Leaf" });
            var children = inner.children;
            children[0].directBlendParameter = "Weight";
            inner.children = children;

            var root = new BlendTree
            {
                name = "Root",
                blendType = BlendTreeType.Simple1D,
                blendParameter = "Blend",
                useAutomaticThresholds = false,
            };
            root.AddChild(inner, 0f);
            return root;
        }

        [Test]
        public void Import_AppendsRemappedDeepCopy()
        {
            var template = ScriptableObject.CreateInstance<DaerDBlendTreeTemplate>();
            template.name = "T";
            template.tree = NewSubtree();
            template.parameters.Add(new LayerClipboard.ParameterSnapshot
            {
                name = "Blend",
                type = AnimatorControllerParameterType.Float,
            });
            template.parameters.Add(new LayerClipboard.ParameterSnapshot
            {
                name = "Weight",
                type = AnimatorControllerParameterType.Float,
            });

            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("ExistingWeight", AnimatorControllerParameterType.Float);
            var parent = new BlendTree { name = "Parent", blendType = BlendTreeType.Direct };

            var imported = template.Import(controller, parent, new Dictionary<string, string>
            {
                ["Blend"] = "MyBlend",
                ["Weight"] = "ExistingWeight",
            });

            Assert.IsNotNull(imported);
            Assert.AreNotEqual(template.tree, imported);   // deep copy, not the template's own tree
            Assert.AreEqual(1, parent.children.Length);
            Assert.AreEqual(imported, parent.children[0].motion);

            Assert.AreEqual("MyBlend", imported.blendParameter);
            var inner = (BlendTree)imported.children[0].motion;
            Assert.AreEqual("ExistingWeight", inner.children[0].directBlendParameter);
            // The template's own tree was not remapped.
            Assert.AreEqual("Blend", template.tree.blendParameter);

            // "MyBlend" was created; "ExistingWeight" was reused, "Weight" never added.
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "MyBlend"));
            Assert.IsNull(DbtBuilder.FindParameter(controller, "Weight"));
        }

        [Test]
        public void RemapTree_TouchesOnlyTheGivenSubtree()
        {
            var tree = NewSubtree();
            LayerParameterRemapper.RemapTree(tree,
                new Dictionary<string, string> { ["Blend"] = "B2", ["Weight"] = "W2" });
            Assert.AreEqual("B2", tree.blendParameter);
            Assert.AreEqual("W2", ((BlendTree)tree.children[0].motion).children[0].directBlendParameter);
        }

        [Test]
        public void CollectBlendTreeParameterNames_SeesBlendAndDirectWeights()
        {
            var names = LayerClipboard.CollectBlendTreeParameterNames(NewSubtree());
            Assert.IsTrue(names.Contains("Blend"));
            Assert.IsTrue(names.Contains("Weight"));
            Assert.AreEqual(2, names.Count);
        }
    }
}

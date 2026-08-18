using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD.Tests
{
    public class ClipRepatherTests
    {
        static AnimationClip NewClip(params string[] paths)
        {
            var clip = new AnimationClip();
            foreach (var path in paths)
                AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive"),
                    new AnimationCurve(new Keyframe(0f, 1f)));
            return clip;
        }

        static List<string> Paths(AnimationClip clip)
        {
            var paths = new List<string>();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                paths.Add(binding.path);
            paths.Sort();
            return paths;
        }

        [Test]
        public void TryMapPath_MatchesExactAndChildren()
        {
            Assert.IsTrue(ClipRepather.TryMapPath("Body/Hat", "Body/Hat", "Head/Hat", out var exact));
            Assert.AreEqual("Head/Hat", exact);
            Assert.IsTrue(ClipRepather.TryMapPath("Body/Hat/Brim", "Body/Hat", "Head/Hat", out var child));
            Assert.AreEqual("Head/Hat/Brim", child);
            Assert.IsFalse(ClipRepather.TryMapPath("Body/Hatter", "Body/Hat", "X", out _));
            Assert.IsFalse(ClipRepather.TryMapPath("Other", "Body/Hat", "X", out _));
        }

        [Test]
        public void Repath_MovesCurvesToTheNewPath()
        {
            var clip = NewClip("Body/Hat", "Body/Hat/Brim", "Body/Cape");
            int rewritten = ClipRepather.Repath(new[] { clip }, "Body/Hat", "Head/Hat");
            Assert.AreEqual(2, rewritten);
            CollectionAssert.AreEqual(
                new[] { "Body/Cape", "Head/Hat", "Head/Hat/Brim" }, Paths(clip));

            // The moved curve kept its data.
            var moved = AnimationUtility.GetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Head/Hat", typeof(GameObject), "m_IsActive"));
            Assert.AreEqual(1f, moved.keys[0].value);
        }

        [Test]
        public void Repath_IgnoresNonMatchingAndIdentity()
        {
            var clip = NewClip("Body/Hat");
            Assert.AreEqual(0, ClipRepather.Repath(new[] { clip }, "Nope", "X"));
            Assert.AreEqual(0, ClipRepather.Repath(new[] { clip }, "Body/Hat", "Body/Hat"));
            Assert.AreEqual(0, ClipRepather.Repath(new[] { clip }, "", "X"));
        }

        [Test]
        public void ScanBroken_FindsPathsMissingUnderRoot()
        {
            var root = new GameObject("Avatar");
            try
            {
                var body = new GameObject("Body");
                body.transform.SetParent(root.transform);
                var hat = new GameObject("Hat");
                hat.transform.SetParent(body.transform);

                var clip = NewClip("Body/Hat", "Body/Gone", "");
                var broken = ClipRepather.ScanBroken(new[] { clip }, root);
                Assert.AreEqual(1, broken.Count);
                Assert.AreEqual("Body/Gone", broken[0].binding.path);

                var paths = ClipRepather.DistinctBrokenPaths(broken);
                Assert.AreEqual(1, paths.Count);
                Assert.AreEqual("Body/Gone", paths[0]);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}

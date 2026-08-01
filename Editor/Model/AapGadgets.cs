using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Pure-C# ports of the ylaac AAP gadget templates — no AnimatorAsCode
    /// dependency. Each gadget is a blend tree that computes a float operation once per
    /// frame inside a Write-Defaults-ON Direct blend tree layer, writing its result through
    /// AAP clips (one-key clips animating an Animator parameter).
    ///
    /// Direct blend trees sum weight × value over their children, which gives:
    ///   Add       output = A + B            (weights are the inputs; positive values only)
    ///   Sub       output = A - B            (B weighs a "-1" clip; positive values only)
    ///   Multiply  output = A × B            (nested Direct trees multiply their weights)
    ///   Ranged Add/Sub remap both inputs through 1D trees first, so negatives work too.
    /// Logic gadgets assume 0..1 inputs. Reference:
    /// https://vrc.school/docs/Other/Advanced-BlendTrees
    /// </summary>
    static class AapGadgets
    {
        public enum Kind
        {
            Smooth,
            Add,
            AddRanged,
            Sub,
            SubRanged,
            Multiply,
            And,
            Or,
            Not,
            FloatAsBool,
            Remap,
        }

        public static bool IsBinary(Kind kind) =>
            kind == Kind.Add || kind == Kind.AddRanged || kind == Kind.Sub
            || kind == Kind.SubRanged || kind == Kind.Multiply
            || kind == Kind.And || kind == Kind.Or;

        public static bool UsesRange(Kind kind) =>
            kind == Kind.Smooth || kind == Kind.AddRanged || kind == Kind.SubRanged || kind == Kind.Remap;

        public class Request
        {
            public AnimatorController controller;
            public Kind kind;
            public string inputA;
            /// <summary>Second input; only read for binary kinds.</summary>
            public string inputB;
            /// <summary>Result parameter written by the gadget. Must be new.</summary>
            public string output;
            /// <summary>Value range: input+output range for ranged Add/Sub, output range for Remap.</summary>
            public float rangeMin = -1f;
            public float rangeMax = 1f;
            /// <summary>Input range mapped from — Remap only.</summary>
            public float inMin = 0f;
            public float inMax = 1f;
            /// <summary>FloatAsBool: values at or above this become 1.</summary>
            public float threshold = 0.5f;
            /// <summary>Smooth: the smoothing-amount parameter; may exist as a shared Float.</summary>
            public string smoothing;
            /// <summary>Smooth: default value stored on the smoothing parameter.</summary>
            public float smoothingDefault = 0.9f;
            /// <summary>Existing DBT (or empty) layer to add the gadget to, or -1 to create one.</summary>
            public int layerIndex = -1;
            public string newLayerName = "DBT";
        }

        /// <summary>Human-readable reason the request can't run, or null when it can.</summary>
        public static string Validate(Request r)
        {
            var controller = r.controller;
            if (controller == null) return L.Tr("No controller.");

            // Smoothing has its own parameter rules (feedback output, shared smoothing
            // amount); the whole request is delegated to AapSmoothing.
            if (r.kind == Kind.Smooth)
                return AapSmoothing.Validate(ToSmoothingRequest(r));

            if (!IsFloat(controller, r.inputA))
                return L.Tr("The source must be an existing Float parameter.");
            if (IsBinary(r.kind))
            {
                if (!IsFloat(controller, r.inputB))
                    return L.Tr("The second input must be an existing Float parameter.");
            }

            if (string.IsNullOrEmpty(r.output) || r.output == r.inputA || r.output == r.inputB)
                return L.Tr("The output parameter needs a name different from the inputs.");
            if (DbtBuilder.FindParameter(controller, r.output) != null)
                return L.Tr("A parameter named '{0}' already exists.", r.output);

            if ((r.kind == Kind.AddRanged || r.kind == Kind.SubRanged) && !(r.rangeMin < r.rangeMax))
                return L.Tr("Range Min must be smaller than Range Max.");
            if (r.kind == Kind.Remap && !(r.inMin < r.inMax))
                return L.Tr("Input Min must be smaller than Input Max.");

            return AapSmoothing.ValidateLayerChoice(controller, r.layerIndex, r.newLayerName);
        }

        static AapSmoothing.Request ToSmoothingRequest(Request r) => new AapSmoothing.Request
        {
            controller = r.controller,
            source = r.inputA,
            output = r.output,
            smoothing = r.smoothing,
            smoothingDefault = r.smoothingDefault,
            rangeMin = r.rangeMin,
            rangeMax = r.rangeMax,
            layerIndex = r.layerIndex,
            newLayerName = r.newLayerName,
        };

        /// <summary>Runs the (pre-validated) request; returns false when validation fails.</summary>
        public static bool Apply(Request r)
        {
            if (r.kind == Kind.Smooth)
                return AapSmoothing.Apply(ToSmoothingRequest(r));

            if (Validate(r) != null) return false;
            var controller = r.controller;

            using (new UndoScope("DBT Gadget"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "DBT Gadget");

                string one = DbtBuilder.EnsureConstantOneParameter(controller);
                DbtBuilder.EnsureFloatParameter(controller, r.output, 0f);

                var root = DbtBuilder.EnsureDirectBlendTreeLayer(controller, r.layerIndex, r.newLayerName);
                DbtBuilder.AddDirectChild(root, Build(r, controller, one), one);
                EditorUtility.SetDirty(controller);
            }
            return true;
        }

        static BlendTree Build(Request r, AnimatorController c, string one)
        {
            string a = r.inputA, b = r.inputB, output = r.output;
            switch (r.kind)
            {
                case Kind.Add: return Add(c, a, b, output);
                case Kind.AddRanged: return AddRanged(c, a, b, output, one, r.rangeMin, r.rangeMax);
                case Kind.Sub: return Sub(c, a, b, output);
                case Kind.SubRanged: return SubRanged(c, a, b, output, one, r.rangeMin, r.rangeMax);
                case Kind.Multiply: return Multiply(c, a, b, output);
                case Kind.And: return And(c, a, b, output);
                case Kind.Or: return Or(c, a, b, output);
                case Kind.Not: return Not(c, a, output);
                case Kind.FloatAsBool: return FloatAsBool(c, a, output, r.threshold);
                case Kind.Remap: return Remap(c, a, output, r.rangeMin, r.rangeMax, r.inMin, r.inMax);
                default: return null;
            }
        }

        // ---- arithmetic --------------------------------------------------------

        /// <summary>output = A + B. Direct weights can't go negative, so positive values only;
        /// use <see cref="AddRanged"/> for signed inputs.</summary>
        public static BlendTree Add(AnimatorController c, string a, string b, string output)
        {
            var one = DbtBuilder.ParameterClip(c, output, 1f);
            var tree = DbtBuilder.DirectTree(c, Name("Add", a, b));
            DbtBuilder.AddDirectChild(tree, one, a);
            DbtBuilder.AddDirectChild(tree, one, b);
            return tree;
        }

        /// <summary>output = A - B. Positive values only; use <see cref="SubRanged"/> for signed inputs.</summary>
        public static BlendTree Sub(AnimatorController c, string a, string b, string output)
        {
            var plus = DbtBuilder.ParameterClip(c, output, 1f);
            var minus = DbtBuilder.ParameterClip(c, output, -1f);
            var tree = DbtBuilder.DirectTree(c, Name("Sub", a, b));
            DbtBuilder.AddDirectChild(tree, plus, a);
            DbtBuilder.AddDirectChild(tree, minus, b);
            return tree;
        }

        /// <summary>Signed add: both inputs are remapped through 1D trees whose leaves write
        /// the output, and the Direct parent sums the two contributions.</summary>
        public static BlendTree AddRanged(AnimatorController c, string a, string b, string output,
            string one, float min, float max)
        {
            var clipMin = DbtBuilder.ParameterClip(c, output, min);
            var clipMax = DbtBuilder.ParameterClip(c, output, max);

            var treeA = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(a) + " (Ranged)", a);
            treeA.AddChild(clipMin, min);
            treeA.AddChild(clipMax, max);
            var treeB = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(b) + " (Ranged)", b);
            treeB.AddChild(clipMin, min);
            treeB.AddChild(clipMax, max);

            var tree = DbtBuilder.DirectTree(c, Name("Add", a, b));
            DbtBuilder.AddDirectChild(tree, treeA, one);
            DbtBuilder.AddDirectChild(tree, treeB, one);
            return tree;
        }

        /// <summary>Signed subtract: like <see cref="AddRanged"/>, but B's leaves are swapped
        /// so its contribution is negated. Inherited from the template: the negation is exact
        /// only for a symmetric range (min = -max); an asymmetric range shifts the result by
        /// (min + max).</summary>
        public static BlendTree SubRanged(AnimatorController c, string a, string b, string output,
            string one, float min, float max)
        {
            var clipMin = DbtBuilder.ParameterClip(c, output, min);
            var clipMax = DbtBuilder.ParameterClip(c, output, max);

            var treeA = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(a) + " (Ranged)", a);
            treeA.AddChild(clipMin, min);
            treeA.AddChild(clipMax, max);
            var treeB = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(b) + " (Ranged, negated)", b);
            treeB.AddChild(clipMax, min);
            treeB.AddChild(clipMin, max);

            var tree = DbtBuilder.DirectTree(c, Name("Sub", a, b));
            DbtBuilder.AddDirectChild(tree, treeA, one);
            DbtBuilder.AddDirectChild(tree, treeB, one);
            return tree;
        }

        /// <summary>output = A × B via nested Direct trees (weights multiply down the tree).
        /// Positive values only.</summary>
        public static BlendTree Multiply(AnimatorController c, string a, string b, string output)
        {
            var one = DbtBuilder.ParameterClip(c, output, 1f);
            var inner = DbtBuilder.DirectTree(c, Name("Mul", a, b) + " (Inner)");
            DbtBuilder.AddDirectChild(inner, one, b);
            var tree = DbtBuilder.DirectTree(c, Name("Mul", a, b));
            DbtBuilder.AddDirectChild(tree, inner, a);
            return tree;
        }

        /// <summary>Linear remap: input over [inMin, inMax] → output over [outMin, outMax]
        /// (reversed output ranges are allowed and invert the slope).</summary>
        public static BlendTree Remap(AnimatorController c, string input, string output,
            float outMin, float outMax, float inMin, float inMax)
        {
            var tree = DbtBuilder.Tree1D(c, Name("Remap", input, null), input);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, outMin), inMin);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, outMax), inMax);
            return tree;
        }

        // ---- logic (inputs assumed 0..1) ---------------------------------------

        public static BlendTree And(AnimatorController c, string a, string b, string output)
        {
            var zero = DbtBuilder.ParameterClip(c, output, 0f);
            var one = DbtBuilder.ParameterClip(c, output, 1f);

            var inner = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(b) + " (And)", b);
            inner.AddChild(zero, 0f);
            inner.AddChild(one, 1f);
            var tree = DbtBuilder.Tree1D(c, Name("And", a, b), a);
            tree.AddChild(zero, 0f);
            tree.AddChild(inner, 1f);
            return tree;
        }

        public static BlendTree Or(AnimatorController c, string a, string b, string output)
        {
            var zero = DbtBuilder.ParameterClip(c, output, 0f);
            var one = DbtBuilder.ParameterClip(c, output, 1f);

            var inner = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(b) + " (Or)", b);
            inner.AddChild(zero, 0f);
            inner.AddChild(one, 1f);
            var tree = DbtBuilder.Tree1D(c, Name("Or", a, b), a);
            tree.AddChild(inner, 0f);
            tree.AddChild(one, 1f);
            return tree;
        }

        public static BlendTree Not(AnimatorController c, string input, string output)
        {
            var tree = DbtBuilder.Tree1D(c, Name("Not", input, null), input);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, 1f), 0f);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, 0f), 1f);
            return tree;
        }

        /// <summary>0 below the threshold, 1 from the threshold up. The template's version
        /// stacked two children on one threshold; a narrow ramp just below it is equivalent
        /// and keeps the tree well-formed.</summary>
        public static BlendTree FloatAsBool(AnimatorController c, string input, string output, float threshold)
        {
            const float epsilon = 0.01f;
            var tree = DbtBuilder.Tree1D(c, Name("Bool", input, null), input);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, 0f), threshold - epsilon);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, 1f), threshold);
            return tree;
        }

        static string Name(string op, string a, string b) =>
            b != null
                ? op + " " + DbtBuilder.Sanitize(a) + ", " + DbtBuilder.Sanitize(b)
                : op + " " + DbtBuilder.Sanitize(a);

        static bool IsFloat(AnimatorController controller, string name)
        {
            var p = DbtBuilder.FindParameter(controller, name);
            return p != null && p.type == AnimatorControllerParameterType.Float;
        }
    }
}

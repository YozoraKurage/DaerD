using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
#if DAERD_VRCSDK_AVATARS
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
#endif

namespace Yozolab.DaerD
{
    /// <summary>How one controller parameter relates to the avatar's VRC Expression Parameters.</summary>
    enum VrcParamStatus
    {
        /// Not listed in the Expression Parameters asset.
        NotInExpressions,
        /// Listed and network-synced (costs bits of the sync budget).
        Synced,
        /// Listed but not synced (local only, costs nothing).
        Local,
        /// Listed, but with a value type that doesn't match the controller parameter.
        TypeMismatch,
    }

    /// <summary>Snapshot of the Expression Parameters asset linked to the open controller.</summary>
    class VrcExpressionsInfo
    {
        public string AvatarName;
        /// The VRCExpressionParameters asset, exposed as a plain Object so callers can ping it.
        public Object ParametersAsset;
        public int UsedBits;
        public int MaxBits;
        /// Status per controller parameter name.
        public readonly Dictionary<string, VrcParamStatus> Status = new Dictionary<string, VrcParamStatus>();
        /// Sync cost in bits per expression parameter name (synced ones only).
        public readonly Dictionary<string, int> BitCost = new Dictionary<string, int>();
    }

    /// <summary>
    /// Optional VRChat SDK integration. The SDK is referenced only when the
    /// com.vrchat.avatars package is installed (via the DAERD_VRCSDK_AVATARS version define),
    /// so DaerD keeps working in plain Unity projects. Links the open controller to a scene
    /// avatar whose descriptor references it and reads that avatar's Expression Parameters.
    /// </summary>
    static class VrcExpressions
    {
        public static bool SdkPresent =>
#if DAERD_VRCSDK_AVATARS
            true;
#else
            false;
#endif

#if DAERD_VRCSDK_AVATARS
        // Scanning open scenes for avatar descriptors is too heavy to run per IMGUI repaint,
        // so the result is cached per controller and refreshed on a short timer.
        const double CacheSeconds = 2.0;
        static VrcExpressionsInfo s_cache;
        static AnimatorController s_cacheController;
        static double s_cacheTime;
#endif

        /// <summary>
        /// The Expression Parameters info for the avatar whose descriptor references
        /// <paramref name="controller"/>, or null when the SDK is missing or no loaded
        /// avatar uses the controller.
        /// </summary>
        public static VrcExpressionsInfo GetInfo(AnimatorController controller)
        {
#if DAERD_VRCSDK_AVATARS
            if (controller == null) return null;
            double now = EditorApplication.timeSinceStartup;
            if (controller == s_cacheController && now - s_cacheTime < CacheSeconds)
                return s_cache;

            s_cacheController = controller;
            s_cacheTime = now;
            s_cache = Build(controller);
            return s_cache;
#else
            return null;
#endif
        }

        public static void InvalidateCache()
        {
#if DAERD_VRCSDK_AVATARS
            s_cacheController = null;
            s_cache = null;
#endif
        }

        /// <summary>
        /// Adds <paramref name="parameter"/> to the linked avatar's Expression Parameters as a
        /// network-synced entry (Trigger becomes Bool; the controller's default value carries
        /// over). Returns false when the SDK is missing, no avatar is linked, or the name is
        /// already listed.
        /// </summary>
        public static bool AddToExpressions(AnimatorController controller, AnimatorControllerParameter parameter)
        {
#if DAERD_VRCSDK_AVATARS
            if (controller == null || parameter == null) return false;
            var expressions = FindExpressionParameters(controller, out _);
            if (expressions == null || expressions.parameters == null) return false;
            foreach (var existing in expressions.parameters)
                if (existing != null && existing.name == parameter.name)
                    return false;

            var entry = new VRCExpressionParameters.Parameter
            {
                name = parameter.name,
                valueType = ToValueType(parameter.type),
                networkSynced = true,
                saved = false,
                defaultValue = DefaultValueOf(parameter),
            };

            Undo.RegisterCompleteObjectUndo(expressions, "Add Expression Parameter");
            var list = new List<VRCExpressionParameters.Parameter>(expressions.parameters) { entry };
            expressions.parameters = list.ToArray();
            EditorUtility.SetDirty(expressions);
            InvalidateCache();
            return true;
#else
            return false;
#endif
        }

#if DAERD_VRCSDK_AVATARS
        static VrcExpressionsInfo Build(AnimatorController controller)
        {
            var expressions = FindExpressionParameters(controller, out var descriptor);
            if (expressions == null || expressions.parameters == null) return null;

            var info = new VrcExpressionsInfo
            {
                AvatarName = descriptor != null ? descriptor.gameObject.name : "?",
                ParametersAsset = expressions,
                UsedBits = expressions.CalcTotalCost(),
                MaxBits = VRCExpressionParameters.MAX_PARAMETER_COST,
            };

            var byName = new Dictionary<string, VRCExpressionParameters.Parameter>();
            foreach (var p in expressions.parameters)
                if (p != null && !string.IsNullOrEmpty(p.name))
                {
                    byName[p.name] = p;
                    if (p.networkSynced)
                        info.BitCost[p.name] = VRCExpressionParameters.TypeCost(p.valueType);
                }

            foreach (var cp in controller.parameters)
            {
                if (!byName.TryGetValue(cp.name, out var ep))
                {
                    info.Status[cp.name] = VrcParamStatus.NotInExpressions;
                    continue;
                }
                if (ep.valueType != ToValueType(cp.type))
                    info.Status[cp.name] = VrcParamStatus.TypeMismatch;
                else
                    info.Status[cp.name] = ep.networkSynced ? VrcParamStatus.Synced : VrcParamStatus.Local;
            }
            return info;
        }

        /// <summary>
        /// The Expression Parameters of the first loaded avatar whose descriptor (animation
        /// layers or Animator component) references <paramref name="controller"/>.
        /// </summary>
        static VRCExpressionParameters FindExpressionParameters(AnimatorController controller,
            out VRCAvatarDescriptor matched)
        {
            matched = null;
            var descriptors = Object.FindObjectsByType<VRCAvatarDescriptor>(
                FindObjectsInactive.Include, FindObjectsSortingMode.None);
            foreach (var descriptor in descriptors)
            {
                if (descriptor == null || !References(descriptor, controller)) continue;
                if (descriptor.expressionParameters == null) continue;
                matched = descriptor;
                return descriptor.expressionParameters;
            }
            return null;
        }

        static bool References(VRCAvatarDescriptor descriptor, AnimatorController controller)
        {
            foreach (var layer in descriptor.baseAnimationLayers)
                if (layer.animatorController == controller) return true;
            foreach (var layer in descriptor.specialAnimationLayers)
                if (layer.animatorController == controller) return true;
            var animator = descriptor.GetComponent<Animator>();
            return animator != null && animator.runtimeAnimatorController == controller;
        }

        static VRCExpressionParameters.ValueType ToValueType(AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Int: return VRCExpressionParameters.ValueType.Int;
                case AnimatorControllerParameterType.Float: return VRCExpressionParameters.ValueType.Float;
                // Expression parameters have no Trigger; a synced Bool is the usual stand-in.
                default: return VRCExpressionParameters.ValueType.Bool;
            }
        }

        static float DefaultValueOf(AnimatorControllerParameter parameter)
        {
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Int: return parameter.defaultInt;
                case AnimatorControllerParameterType.Float: return parameter.defaultFloat;
                default: return parameter.defaultBool ? 1f : 0f;
            }
        }
#endif
    }
}

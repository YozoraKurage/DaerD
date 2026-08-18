using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Bridge;

namespace Yozolab.DaerD.Engine
{
    /// <summary>
    /// The per-state "sync request" — DaerD's component-like attachment for async sync
    /// (<see cref="AsyncSyncBuilder"/>): while the avatar sits in a state, the chosen targets
    /// of a saved setup are synced out of turn instead of waiting for their slot to come
    /// around. The authoring record lives in <see cref="GraphFrameData.SyncRequest"/>; the
    /// runtime side this class materializes is an ordinary VRCAvatarParameterDriver on the
    /// state (localOnly, one Set per "base/Req/target" flag), so the result works in VRChat
    /// with no DaerD code present. Applying also makes sure the setup itself accepts the
    /// requests — targets missing from the setup's requestable list are added and the sync
    /// layer is regenerated in place.
    /// </summary>
    static class SyncRequestBuilder
    {
        /// <summary>Instance name of the managed driver: "Sync Request (base)". The base name
        /// keeps drivers apart when one state requests from several setups, and marks the
        /// driver as DaerD-managed — Apply rewrites it wholesale.</summary>
        public static string DriverName(string baseName) => "Sync Request (" + baseName + ")";

        /// <summary>The managed driver for this setup already on the state, or null.</summary>
        public static StateMachineBehaviour FindDriver(AnimatorState state, string baseName)
        {
            if (state == null) return null;
            foreach (var behaviour in state.behaviours)
                if (VrcParameterDriver.Is(behaviour) && behaviour != null
                    && behaviour.name == DriverName(baseName))
                    return behaviour;
            return null;
        }

        /// <summary>Human-readable reason the request can't be applied, or null when it can.</summary>
        public static string Validate(AnimatorController controller,
            GraphFrameData.AsyncSyncConfig config, AnimatorState state, List<string> targets)
        {
            if (controller == null || state == null) return L.Tr("No controller.");
            if (config == null)
                return L.Tr("This controller has no saved async sync setup — create one first.");
            if (targets == null || targets.Count == 0)
                return L.Tr("Pick at least one parameter to request.");
            foreach (var name in targets)
                if (config.targets == null || !config.targets.Contains(name))
                    return L.Tr("'{0}' is not multiplexed by async sync '{1}'.", name, config.baseName);
            if (!VrcParameterDriver.SdkAvailable)
                return L.Tr("VRChat SDK not found — the Parameter Driver behaviour is required.");
            return null;
        }

        /// <summary>
        /// Creates or rewrites the sync request on <paramref name="state"/>. Returns false
        /// when <see cref="Validate"/> refuses, or when enabling the requests required
        /// regenerating the sync layer and that regeneration failed (the setup's own
        /// validation is the authority there — nothing is half-applied).
        /// </summary>
        public static bool Apply(AnimatorController controller,
            GraphFrameData.AsyncSyncConfig config, AnimatorState state, List<string> targets)
        {
            if (Validate(controller, config, state, targets) != null) return false;

            // Keep the stored list in the setup's cycle order regardless of tick order.
            var requested = new List<string>();
            foreach (var name in config.targets)
                if (targets.Contains(name) && !requested.Contains(name))
                    requested.Add(name);

            using (new UndoScope("Sync Request"))
            {
                // The setup must accept what this state requests: grow its requestable list
                // and rebuild the sync layer so the flags and redirect transitions exist.
                bool missing = false;
                foreach (var name in requested)
                    if (config.requests == null || !config.requests.Contains(name))
                        missing = true;
                if (missing)
                {
                    var request = AsyncSyncBuilder.FromConfig(controller, config);
                    foreach (var name in requested)
                        if (!request.requestTargets.Contains(name))
                            request.requestTargets.Add(name);
                    if (!AsyncSyncBuilder.Apply(request)) return false;
                }

                var driver = FindDriver(state, config.baseName);
                if (driver != null)
                    VrcBehaviours.RemoveFrom(state, driver);
                driver = VrcParameterDriver.AddTo(state, DriverName(config.baseName));
                if (driver == null) return false;
                Undo.RegisterCompleteObjectUndo(driver, "Sync Request");
                // Local-only: a request is raised and serviced on the wearer's client; the
                // send cycle never runs on remotes, so the flag would just sit there.
                VrcParameterDriver.SetLocalOnly(driver, true);
                foreach (var name in requested)
                    VrcParameterDriver.AddSetEntry(driver,
                        AsyncSyncBuilder.RequestParameter(config.baseName, name), 1f);

                GraphFrameData.SaveSyncRequest(controller, new GraphFrameData.SyncRequest
                {
                    state = state,
                    baseName = config.baseName,
                    targets = requested,
                });
                EditorUtility.SetDirty(state);
            }
            return true;
        }

        /// <summary>
        /// Removes the state's sync request for one setup: the managed driver and the stored
        /// record. The setup's requestable list is left alone — other states (or hand-built
        /// drivers) may still raise the same flags, and unused request machinery costs no
        /// synced bits.
        /// </summary>
        public static void Remove(AnimatorController controller, AnimatorState state,
            string baseName)
        {
            if (state == null) return;
            using (new UndoScope("Remove Sync Request"))
            {
                var driver = FindDriver(state, baseName);
                if (driver != null)
                {
                    Undo.RegisterCompleteObjectUndo(state, "Remove Sync Request");
                    VrcBehaviours.RemoveFrom(state, driver);
                    EditorUtility.SetDirty(state);
                }
                GraphFrameData.RemoveSyncRequest(controller, state, baseName);
            }
        }
    }
}

using System.Collections.Generic;
using UnityEditor.Animations;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// Async Sync (巡回同期) from a recipe, with everything the wizard offers plus the one
    /// thing it doesn't: an explicit schedule. Runs after the declared layers are applied;
    /// regenerates its own layer in place on every Generate (matched by base name through
    /// the saved setup, exactly like the wizard's layer choice). Unnamed, it runs under the
    /// controller's derived default base name — a flat "Async" would collide with the next
    /// distribution that multiplexes on the same avatar.
    ///
    ///   c.AsyncSync()
    ///    .Targets("Hue", "Outfit", "TailState")
    ///    .Rate("Hue", 2)                      // or spell the cycle out yourself:
    ///    .Schedule("Hue", "Outfit", "Hue", "TailState")
    ///    .FloatChannels(2).Step(0.3f);
    /// </summary>
    public sealed class AsyncSyncRecipeBuilder
    {
        readonly AsyncSyncBuilder.Request _request = new AsyncSyncBuilder.Request();

        internal AsyncSyncRecipeBuilder(ControllerBuilder root, string baseName)
        {
            _request.baseName = baseName;
            root.PostOps.Add(controller =>
            {
                var warnings = new List<string>();
                _request.controller = controller;
                _request.store = ParameterStore.Of(controller);
                _request.emptyClip = GraphFrameData.GetEmptyClip(controller);

                // The controller is only known here, and everything below — the saved setup,
                // the layer name, the error message — keys on the base name, so resolve it first.
                if (string.IsNullOrEmpty(_request.baseName))
                    _request.baseName = ResolveDefaultBaseName(controller);

                // Regenerate in place when a setup with this base name already owns a layer —
                // a recipe must be repeatable without stacking sync layers.
                _request.layerIndex = -1;
                foreach (var config in GraphFrameData.GetAsyncSyncs(controller))
                    if (config.baseName == _request.baseName)
                    {
                        var layers = controller.layers;
                        for (int i = 0; i < layers.Length; i++)
                            if (layers[i].stateMachine == config.layer)
                                _request.layerIndex = i;
                    }
                // No saved setup (fresh checkout, in-memory controller): the layer named
                // after this sync is still ours to regenerate — recipes key everything by
                // name, and stacking a new layer per Generate would be strictly worse.
                if (_request.layerIndex < 0)
                {
                    string expected = string.IsNullOrEmpty(_request.layerName)
                        ? _request.baseName : _request.layerName;
                    var layers = controller.layers;
                    for (int i = 0; i < layers.Length; i++)
                        if (layers[i].name == expected)
                            _request.layerIndex = i;
                }

                var error = AsyncSyncBuilder.Validate(_request);
                if (error != null)
                {
                    warnings.Add(L.Tr("Async Sync '{0}': {1}", _request.baseName, error));
                    return warnings;
                }
                AsyncSyncBuilder.Apply(_request);
                warnings.AddRange(AsyncSyncBuilder.Warnings(_request));
                return warnings;
            });
        }

        /// <summary>
        /// The base name an unnamed <c>AsyncSync()</c> runs under. A setup already generated
        /// under the old flat "Async" default keeps it: renaming it on the next Generate would
        /// leave the previous layer and its synced parameters behind, unowned. Everything else
        /// gets the controller's own default, which no other distribution can collide with.
        /// </summary>
        static string ResolveDefaultBaseName(AnimatorController controller)
        {
            foreach (var config in GraphFrameData.GetAsyncSyncs(controller))
                if (config.baseName == "Async") return "Async";
            return AsyncSyncBuilder.DefaultBaseName(controller);
        }

        /// <summary>The parameters to multiplex, in cycle order.</summary>
        public AsyncSyncRecipeBuilder Targets(params string[] parameters)
        {
            _request.targets.AddRange(parameters);
            return this;
        }

        /// <summary>Sync this parameter <paramref name="timesPerPass"/> times per pass
        /// (spread as evenly as the other slots allow). Ignored under an explicit Schedule.</summary>
        public AsyncSyncRecipeBuilder Rate(string parameter, int timesPerPass)
        {
            _request.rates[parameter] = timesPerPass;
            return this;
        }

        /// <summary>
        /// Accept on-demand sync requests for these targets: each gets a local, unsynced
        /// Bool ("base/Req/target"), and the cycle jumps to a requested target's slot at the
        /// next step boundary instead of waiting out the pass. Raise the flag from a state's
        /// Sync Request (or any Parameter Driver); the send cycle clears it on service.
        /// </summary>
        public AsyncSyncRecipeBuilder Requestable(params string[] targets)
        {
            _request.requestTargets.AddRange(targets);
            return this;
        }

        /// <summary>
        /// Spell the cycle out step by step — the control the wizard doesn't expose. Every
        /// multiplexed parameter must appear at least once and no slot may occupy adjacent
        /// steps (including the wrap); Generate reports violations instead of applying.
        /// </summary>
        public AsyncSyncRecipeBuilder Schedule(params string[] stepsInOrder)
        {
            _request.scheduleOverride.Clear();
            _request.scheduleOverride.AddRange(stepsInOrder);
            return this;
        }

        /// <summary>Synced Float channels (1–8): each step carries up to this many Floats.</summary>
        public AsyncSyncRecipeBuilder FloatChannels(int channels)
        {
            _request.floatChannels = channels;
            return this;
        }

        /// <summary>Dwell per step in seconds (VRChat syncs roughly every 0.3 s).</summary>
        public AsyncSyncRecipeBuilder Step(float seconds)
        {
            _request.stepSeconds = seconds;
            return this;
        }

        public AsyncSyncRecipeBuilder EncodingInt()
        {
            _request.encoding = AsyncSyncBuilder.IndexEncoding.Int;
            return this;
        }

        public AsyncSyncRecipeBuilder EncodingBool()
        {
            _request.encoding = AsyncSyncBuilder.IndexEncoding.Bool;
            return this;
        }

        public AsyncSyncRecipeBuilder EncodingAuto()
        {
            _request.encoding = AsyncSyncBuilder.IndexEncoding.Auto;
            return this;
        }

        /// <summary>Name the generated layer (defaults to the base name).</summary>
        public AsyncSyncRecipeBuilder LayerName(string name)
        {
            _request.layerName = name;
            return this;
        }

        /// <summary>Don't add the generated synced parameters to the parameter store.</summary>
        public AsyncSyncRecipeBuilder NoStore()
        {
            _request.addToStore = false;
            return this;
        }

        /// <summary>Leave the generated states motion-less instead of filling them with the
        /// controller's Empty clip.</summary>
        public AsyncSyncRecipeBuilder NoEmptyClip()
        {
            _request.assignEmptyClip = false;
            return this;
        }

        /// <summary>Tests only: build the structure without the VRC Parameter Driver.</summary>
        internal AsyncSyncRecipeBuilder SkipDriversForTest()
        {
            _request.skipDrivers = true;
            return this;
        }
    }
}

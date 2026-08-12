using System.Collections.Generic;
using UnityEditor.Animations;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// Async Sync (巡回同期) from a recipe: everything the wizard offers, in the order the
    /// wizard offers it. Runs after the declared layers are applied;
    /// regenerates its own layer in place on every Generate (matched by base name through
    /// the saved setup, exactly like the wizard's layer choice). Unnamed, it runs under the
    /// controller's derived default base name — a flat "Async" would collide with the next
    /// distribution that multiplexes on the same avatar.
    ///
    ///   c.AsyncSync()
    ///    .Targets("Hue", "Outfit", "TailState")
    ///    .Rate("Hue", 2)                      // weight: two of the pass's places. Or spell
    ///    .Schedule("Hue", "Outfit", "Hue", "TailState")   // the cycle out yourself:
    ///    .FloatChannels(2).Step(0.3f);
    ///
    /// Sends() goes one deeper still, saying what each step carries rather than which slot it
    /// visits — the only way to send two types in one step, or to overlap two steps.
    /// AllowRepeats() lifts the one rule both of them are otherwise bound by: that no slot may
    /// occupy adjacent steps.
    /// </summary>
    public sealed class AsyncSyncRecipeBuilder
    {
        readonly AsyncSyncBuilder.Request _request = new AsyncSyncBuilder.Request();
        readonly ControllerBuilder _root;

        internal AsyncSyncRecipeBuilder(ControllerBuilder root, string baseName)
        {
            _root = root;
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
                // The generated layer is the recipe's: the next Generate rebuilds it, which
                // is what the layer list's ownership badge and the panels' "add it in the
                // recipe instead" hints are there to say.
                string layer = string.IsNullOrEmpty(_request.layerName)
                    ? _request.baseName : _request.layerName;
                if (!root.PostLayers.Contains(layer)) root.PostLayers.Add(layer);
                // The Ready watcher is the same call's output and is rebuilt with it. Read
                // back off the saved setup rather than derived from the name, which the
                // builder may have had to make unique.
                foreach (var watcher in WatcherLayerNames(controller, _request.baseName))
                    if (!root.PostLayers.Contains(watcher)) root.PostLayers.Add(watcher);

                warnings.AddRange(AsyncSyncBuilder.Warnings(_request));
                return warnings;
            });
        }

        /// <summary>The layer names the setup's watchers ended up with — the builder
        /// uniquifies the names it creates, so only the saved setup knows them.</summary>
        static List<string> WatcherLayerNames(AnimatorController controller, string baseName)
        {
            var names = new List<string>();
            var config = GraphFrameData.FindAsyncSync(controller, baseName);
            if (config == null) return names;
            foreach (var layer in controller.layers)
                if (layer.stateMachine != null
                    && (layer.stateMachine == config.readyLayer
                        || layer.stateMachine == config.staleLayer))
                    names.Add(layer.name);
            return names;
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

        // ---- recording -----------------------------------------------------------

        /// <summary>
        /// Records one call as source, the way <see cref="GadgetRecipeBuilder"/> does: on this
        /// builder, so a run of them comes back out as the single fluent chain the API is
        /// written to read as. Everything it is told is recorded — deciding that an argument
        /// matches its default and can be left out belongs to the caller, because only the
        /// caller knows whether the call happened at all.
        /// </summary>
        AsyncSyncRecipeBuilder Record(string method, params string[] args)
        {
            _root?.Script?.Call(this, method + "(" + string.Join(", ", args) + ")");
            return this;
        }

        static string[] Names(string[] values)
        {
            var literals = new string[values.Length];
            for (int i = 0; i < values.Length; i++) literals[i] = RecipeScript.S(values[i]);
            return literals;
        }

        /// <summary>The parameters to multiplex, in cycle order.</summary>
        public AsyncSyncRecipeBuilder Targets(params string[] parameters)
        {
            _request.targets.AddRange(parameters);
            return Record("Targets", Names(parameters));
        }

        /// <summary>
        /// Give this parameter <paramref name="timesPerPass"/> of the pass's places, spread as
        /// evenly as the other slots allow. A weight rather than a speed: the pass is however
        /// many places it has, so raising one target's share lengthens the pass for everyone
        /// else, and raising every target's changes nothing at all (a common factor is divided
        /// out). Ignored under an explicit <see cref="Schedule"/>.
        ///
        /// For "send this the moment it changes" reach for <see cref="Requestable"/> instead —
        /// a weight buys a target places in every pass, whether or not anything moved.
        /// </summary>
        public AsyncSyncRecipeBuilder Rate(string parameter, int timesPerPass)
        {
            _request.rates[parameter] = timesPerPass;
            return Record("Rate", RecipeScript.S(parameter), timesPerPass.ToString());
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
            return Record("Requestable", Names(targets));
        }

        /// <summary>
        /// Generate the remote-initialized flag: a local, unsynced Bool ("base/Ready") that
        /// turns on once a client has decoded every slot at least once, and never turns off
        /// again. It is what a remote has instead of a way to ask — nothing it observes can
        /// reach the wearer — so read it to hold back anything that would look wrong with
        /// half the values in place.
        ///
        /// The wearer reads it as on from the start: their own values were never anywhere
        /// else. Write <c>Ready &amp;&amp; !IsLocal</c> for "a remote that has finished
        /// initializing" specifically.
        /// </summary>
        public AsyncSyncRecipeBuilder Ready()
        {
            _request.ready = true;
            return Record("Ready");
        }

        /// <summary>
        /// Generate the drift-suspicion flag: a local, unsynced Bool ("base/Stale") that turns
        /// on when a lap did not bring every slot and off again when one does. Read it to hold
        /// back anything that would look wrong on values that may have stopped arriving.
        ///
        /// Judged when a slot the pass sends exactly once comes round, so it needs no timer
        /// and no margin, and a pass stretched by a request cannot make it wrong. A pass with
        /// no such slot — every slot sent more than once, or open to requests — cannot carry
        /// the flag, and Generate says so instead of applying.
        ///
        /// A remote that arrives mid-pass reads it as on for the rest of that pass; pair it
        /// with <see cref="Ready"/> to tell that apart from a cycle that has started dropping.
        /// </summary>
        public AsyncSyncRecipeBuilder Stale()
        {
            _request.stale = true;
            return Record("Stale");
        }

        /// <summary>
        /// Spell the cycle out step by step, naming one target per step (a batched target
        /// stands for its whole slot). Every multiplexed parameter must appear at least once
        /// and no slot may occupy adjacent steps (including the wrap); Generate reports
        /// violations instead of applying. Ignored entirely when <see cref="Sends"/> is used,
        /// which says the same thing and more.
        /// </summary>
        public AsyncSyncRecipeBuilder Schedule(params string[] stepsInOrder)
        {
            _request.scheduleOverride.Clear();
            _request.scheduleOverride.AddRange(stepsInOrder);
            return Record("Schedule", Names(stepsInOrder));
        }

        /// <summary>
        /// Spell one step out as the set of targets it sends, and call it once per step. This
        /// replaces the batching, the weights and <see cref="Schedule"/> together: the slots
        /// become the distinct sets, so the call says which targets share a step as well as
        /// when each step comes round.
        ///
        /// It is the only way to send targets of different types together (channels are per
        /// type, and the automatic batching only ever groups like with like) and the only way
        /// to have one target ride two steps in a row — neighbouring sets may overlap, they
        /// just may not be equal. Every target must be sent by some step, and a step may not
        /// carry more of a type than that type has channels.
        ///
        ///   c.AsyncSync("Zip").Targets("Hue", "Outfit", "Tail")
        ///    .Sends("Hue", "Outfit")
        ///    .Sends("Hue", "Tail");
        /// </summary>
        public AsyncSyncRecipeBuilder Sends(params string[] targets)
        {
            var step = new GraphFrameData.AsyncSyncConfig.StepSpec();
            step.targets.AddRange(targets);
            _request.steps.Add(step);
            return Record("Sends", Names(targets));
        }

        /// <summary>
        /// Let a step send what the step before it sent — the wrap included. The decoder fires
        /// on the index changing, so without this a repeated step is one nobody sees; with it,
        /// a clock phase folded into the index tells the two apart. The price is a decoder
        /// state per parameter set that actually repeats (and, under a Bool index, sometimes a
        /// synced bit), so it is asked for rather than assumed.
        ///
        ///   c.AsyncSync("Zip").Targets("Hue", "Outfit").AllowRepeats()
        ///    .Sends("Hue").Sends("Hue").Sends("Outfit");
        /// </summary>
        public AsyncSyncRecipeBuilder AllowRepeats()
        {
            _request.allowRepeatSteps = true;
            return Record("AllowRepeats");
        }

        /// <summary>Synced Float channels (1–8): each step carries up to this many Floats.</summary>
        public AsyncSyncRecipeBuilder FloatChannels(int channels)
        {
            _request.floatChannels = channels;
            return Record("FloatChannels", channels.ToString());
        }

        /// <summary>
        /// Give these targets a slot of their own instead of letting them share channels with
        /// the target listed before them. Batched targets are copied by one driver in one
        /// step, so they always go out together — split them when they need to reach remotes
        /// at different moments (and leave them batched when they belong together, which is
        /// what the extra channels are for).
        /// </summary>
        public AsyncSyncRecipeBuilder Split(params string[] targets)
        {
            _request.slotBreaks.AddRange(targets);
            return Record("Split", Names(targets));
        }

        /// <summary>Synced Bool channels (1–8): each step carries up to this many Bools, at
        /// one synced bit each — the cheapest way to shorten a Bool-heavy pass.</summary>
        public AsyncSyncRecipeBuilder BoolChannels(int channels)
        {
            _request.boolChannels = channels;
            return Record("BoolChannels", channels.ToString());
        }

        /// <summary>Dwell per step in seconds (VRChat syncs roughly every 0.3 s).</summary>
        public AsyncSyncRecipeBuilder Step(float seconds)
        {
            _request.stepSeconds = seconds;
            return Record("Step", RecipeScript.F(seconds));
        }

        public AsyncSyncRecipeBuilder EncodingInt()
        {
            _request.encoding = AsyncSyncBuilder.IndexEncoding.Int;
            return Record("EncodingInt");
        }

        public AsyncSyncRecipeBuilder EncodingBool()
        {
            _request.encoding = AsyncSyncBuilder.IndexEncoding.Bool;
            return Record("EncodingBool");
        }

        public AsyncSyncRecipeBuilder EncodingAuto()
        {
            _request.encoding = AsyncSyncBuilder.IndexEncoding.Auto;
            return Record("EncodingAuto");
        }

        /// <summary>Name the generated layer (defaults to the base name).</summary>
        public AsyncSyncRecipeBuilder LayerName(string name)
        {
            _request.layerName = name;
            return Record("LayerName", RecipeScript.S(name));
        }

        /// <summary>Don't add the generated synced parameters to the parameter store.</summary>
        public AsyncSyncRecipeBuilder NoStore()
        {
            _request.addToStore = false;
            return Record("NoStore");
        }

        /// <summary>Leave the generated states motion-less instead of filling them with the
        /// controller's Empty clip.</summary>
        public AsyncSyncRecipeBuilder NoEmptyClip()
        {
            _request.assignEmptyClip = false;
            return Record("NoEmptyClip");
        }

        /// <summary>Tests only: build the structure without the VRC Parameter Driver.</summary>
        internal AsyncSyncRecipeBuilder SkipDriversForTest()
        {
            _request.skipDrivers = true;
            return this;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
// Stated in AsyncSyncBuilder's vocabulary, like every other piece of the technique.
using Request = Yozolab.DaerD.AsyncSyncBuilder.Request;
using StepSpec = Yozolab.DaerD.GraphFrameData.AsyncSyncConfig.StepSpec;
using SyncGroup = Yozolab.DaerD.GraphFrameData.AsyncSyncConfig.SyncGroup;

namespace Yozolab.DaerD
{
    /// <summary>
    /// One ring per parameter type, out of a setup that runs them all through one.
    ///
    /// A slot carries a single type — the automatic batching groups by type, and the channels
    /// a step writes are typed — so the step that sends an Int leaves every Float and Bool
    /// channel idle, and the Float that is waiting for its turn is waiting behind steps that
    /// could not have carried it. Giving each type a ring of its own drops the other types'
    /// steps out of each pass, which shortens all of them at once; the bill is one more index
    /// (two more synced bits under a Bool index, eight under an Int one) per extra ring.
    ///
    /// The whole exercise is a proposal rather than a policy: it changes how many setups a
    /// controller has, and a split is one-way — nothing here can put two rings back into one.
    /// <see cref="ByType"/> therefore refuses more than it accepts, and refuses silently, so
    /// the wizard can ask it on every repaint and offer the button only when the answer is a
    /// setup that would actually build.
    /// </summary>
    static class AsyncSyncSplit
    {
        /// <summary>
        /// The setups this one would split into, one per type in first-appearance order, or an
        /// empty list when the split is not on offer. Empty for four different reasons, all of
        /// them "this would not be the same avatar afterwards":
        ///
        /// A step that mixes types has said outright that those targets travel together (one
        /// driver copies a step in one go), and no arrangement of two rings can promise it.
        /// A group whose members are of different types is the same promise made across steps,
        /// and it is the reason groups exist at all — a split would leave each ring holding
        /// half of it, committing halves independently.
        /// A ring one of the types cannot fill on its own does not build (a single target, or
        /// a single slot, is not a cycle), and that is read off <see cref="AsyncSyncBuilder.Validate"/>
        /// rather than guessed at here.
        /// And a split that does not actually shorten every pass is not worth an index.
        /// </summary>
        public static List<Request> ByType(Request r)
        {
            var split = new List<Request>();
            if (r?.controller == null || r.targets == null) return split;
            var types = AsyncSyncCost.ChannelTypes(r);
            if (types.Count < 2) return split;
            if (MixesTypes(r) || GroupsSpanTypes(r)) return split;

            // Names already spoken for on this controller: the setups it has saved, plus the
            // one being edited, whose name the first ring keeps.
            var taken = new List<string>();
            foreach (var config in GraphFrameData.GetAsyncSyncs(r.controller))
                if (!string.IsNullOrEmpty(config.baseName)) taken.Add(config.baseName);
            if (!string.IsNullOrEmpty(r.baseName)) taken.Add(r.baseName);

            float cycle = AsyncSyncCost.CycleSeconds(r);
            for (int i = 0; i < types.Count; i++)
            {
                var one = OfType(r, types[i], i == 0, taken);
                if (AsyncSyncBuilder.Validate(one) != null) return new List<Request>();
                // Every ring has to come out ahead. It always does when the other types were
                // spending steps — but weights are normalized by their own common factor, and
                // a pass that only looks longer after the split is not a proposal.
                if (AsyncSyncCost.CycleSeconds(one) >= cycle) return new List<Request>();
                if (!string.IsNullOrEmpty(one.baseName)) taken.Add(one.baseName);
                split.Add(one);
            }
            return split;
        }

        /// <summary>
        /// One type's ring. The first one keeps the setup's base name and its layer, so a split
        /// regenerates that layer in place instead of renaming every parameter the store
        /// already syncs; the rest are new setups on new layers, named after the type they
        /// carry.
        /// </summary>
        static Request OfType(Request r, AnimatorControllerParameterType type, bool first,
            List<string> taken)
        {
            var byName = DbtBuilder.ParametersByName(r.controller);
            string layerName = string.IsNullOrEmpty(r.layerName) ? r.baseName : r.layerName;
            var one = new Request
            {
                controller = r.controller,
                baseName = first ? r.baseName : UniqueBaseName(r.baseName, type, taken),
                encoding = r.encoding,
                stepSeconds = r.stepSeconds,
                floatChannels = r.floatChannels,
                boolChannels = r.boolChannels,
                allowRepeatSteps = r.allowRepeatSteps,
                ready = r.ready,
                stale = r.stale,
                store = r.store,
                addToStore = r.addToStore,
                assignEmptyClip = r.assignEmptyClip,
                emptyClip = r.emptyClip,
                layerName = first ? r.layerName : layerName + " " + type,
                layerIndex = first ? r.layerIndex : -1,
                skipDrivers = r.skipDrivers,
            };

            foreach (var name in r.targets)
                if (byName.Find(name)?.type == type) one.targets.Add(name);
            foreach (var name in one.targets)
            {
                int rate = r.RateOf(name);
                if (rate > 1) one.rates[name] = rate;
                if (r.requestTargets != null && r.requestTargets.Contains(name))
                    one.requestTargets.Add(name);
                if (r.slotBreaks != null && r.slotBreaks.Contains(name))
                    one.slotBreaks.Add(name);
            }
            foreach (var group in AsyncSyncBuilder.EffectiveGroups(r))
            {
                var kept = new SyncGroup { name = group.name };
                foreach (var name in group.members)
                    if (one.targets.Contains(name)) kept.members.Add(name);
                // Whole or not at all: a group that spanned types would have stopped ByType
                // before this, so a group with any member here has all of them here.
                if (kept.members.Count > 0) one.groups.Add(kept);
            }

            // A hand-written pass survives as the part of itself this ring can run: the steps
            // that named another type's targets are gone, and the repair the editors use puts
            // what is left back into a shape the decoder accepts. An empty result reads as
            // "use the rates", the same as everywhere else.
            if (r.steps != null && r.steps.Count > 0)
            {
                var kept = new List<StepSpec>();
                foreach (var step in r.steps)
                {
                    var members = new StepSpec();
                    if (step?.targets != null)
                        foreach (var name in step.targets)
                            if (one.targets.Contains(name)) members.targets.Add(name);
                    if (members.targets.Count > 0) kept.Add(members);
                }
                one.steps.AddRange(AsyncSyncBuilder.RepairSteps(one, kept));
            }
            else if (r.scheduleOverride != null && r.scheduleOverride.Count > 0)
            {
                var kept = new List<string>();
                foreach (var name in r.scheduleOverride)
                    if (one.targets.Contains(name)) kept.Add(name);
                one.scheduleOverride.AddRange(
                    AsyncSyncBuilder.RepairScheduleOverride(one, kept));
            }
            return one;
        }

        /// <summary>The base name a new ring answers to: the setup's, plus the type it carries.
        /// Suffixed with a number if that is taken, the way a second setup on one controller
        /// gets its name.</summary>
        static string UniqueBaseName(string baseName, AnimatorControllerParameterType type,
            ICollection<string> taken)
        {
            string stem = (string.IsNullOrEmpty(baseName) ? "Async" : baseName) + "_" + type;
            string name = stem;
            // Terminates: the taken set is finite and every candidate name is distinct.
            for (int n = 2; taken != null && taken.Contains(name); n++) name = stem + "_" + n;
            return name;
        }

        /// <summary>Whether any step carries more than one type — which only an explicit grid
        /// can build, and which is a promise that those targets are sent together.</summary>
        public static bool MixesTypes(Request r)
        {
            if (r?.controller == null) return false;
            var types = AsyncSyncCost.ChannelTypes(r);
            foreach (var slot in AsyncSyncBuilder.BuildSlots(r))
            {
                int kinds = 0;
                foreach (var type in types)
                    if (AsyncSyncCost.ChannelsInSlot(r, slot, type) > 0) kinds++;
                if (kinds > 1) return true;
            }
            return false;
        }

        /// <summary>Whether any group holds targets of more than one type — the case groups
        /// were built for, and the one a split cannot keep its promise through.</summary>
        public static bool GroupsSpanTypes(Request r)
        {
            var byName = DbtBuilder.ParametersByName(r.controller);
            foreach (var group in AsyncSyncBuilder.EffectiveGroups(r))
            {
                var types = new HashSet<AnimatorControllerParameterType>();
                foreach (var name in group.members)
                {
                    var parameter = byName.Find(name);
                    if (parameter != null) types.Add(parameter.type);
                }
                if (types.Count > 1) return true;
            }
            return false;
        }

        /// <summary>
        /// The proposal in numbers: what the one ring costs and takes now, against what the
        /// rings would. Both halves are said because they move in opposite directions — the
        /// passes get shorter and the synced bill gets bigger — and which of those matters is
        /// not something this code can know.
        /// </summary>
        public static string Advice(Request r, List<Request> split)
        {
            int bits = 0;
            var passes = new List<string>();
            foreach (var one in split)
            {
                bits += AsyncSyncCost.CompressedBits(one);
                passes.Add(string.Format("{0:0.#} s", AsyncSyncCost.CycleSeconds(one)));
            }
            return L.Tr(
                "A step carries one type, so at every step of this pass the other {0} type(s) of channel sit idle and the values waiting in them wait behind steps that could not have carried them. One ring per type would run passes of {1} instead of one pass of {2:0.#} s, for {3} synced bit(s) instead of {4}.",
                split.Count - 1, string.Join(" / ", passes.ToArray()),
                AsyncSyncCost.CycleSeconds(r), bits, AsyncSyncCost.CompressedBits(r));
        }

        /// <summary>
        /// Runs the split: every ring built, in one undo step. The setups are applied in the
        /// order <see cref="ByType"/> returns them, so the one keeping the layer regenerates it
        /// first and the new rings are added after it.
        ///
        /// Returns false without building anything when any of them has stopped validating
        /// since the proposal was drawn — a split half-applied would leave the controller with
        /// a ring missing its targets, which is worse than either end of the choice.
        /// </summary>
        public static bool Apply(List<Request> split)
        {
            if (split == null || split.Count < 2) return false;
            foreach (var one in split)
                if (AsyncSyncBuilder.Validate(one) != null) return false;
            using (new UndoScope("Split Async Sync By Type"))
                foreach (var one in split)
                    if (!AsyncSyncBuilder.Apply(one)) return false;
            return true;
        }
    }
}

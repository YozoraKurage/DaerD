using System.Collections.Generic;
using UnityEditor.Animations;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD.Analyze
{
    /// <summary>What kind of claim DaerD has on a layer. The distinction is the point: a
    /// generated layer, a shared host and a recipe's output are all "DaerD's", and what a person
    /// may do to them without losing work is different in each case.</summary>
    enum LayerOwnerKind
    {
        /// <summary>The layer an async sync setup's cycle runs in.</summary>
        AsyncSyncCycle,
        /// <summary>Its remote-initialized ("Ready") watcher layer.</summary>
        AsyncSyncReady,
        /// <summary>Its drift-suspicion ("Stale") watcher layer.</summary>
        AsyncSyncStale,
        /// <summary>The commit layer of one of its groups.</summary>
        AsyncSyncGroup,
        /// <summary>An object gadget wired as a layer of its own — the gadget IS the layer.</summary>
        ObjectGadget,
        /// <summary>A Direct blend tree layer an object gadget hung its tree in. Shared: the
        /// layer is not the gadget's, only one child of its root tree is.</summary>
        ObjectGadgetHost,
        /// <summary>A Direct blend tree layer a DBT (AAP) gadget hung its tree in — shared the
        /// same way, and the usual case, since gadgets are meant to pile into one host.</summary>
        DbtGadgetHost,
        /// <summary>A layer a C# recipe generates and regenerates.</summary>
        Recipe,
    }

    /// <summary>One claim, with enough of the record to name it in a sentence.</summary>
    readonly struct LayerOwner
    {
        /// <summary>The setup's base name, the gadget's name or output, the recipe's asset
        /// name — whatever identifies the record in the home screen's list.</summary>
        public readonly string subject;
        public readonly LayerOwnerKind kind;
        /// <summary>The group's name for <see cref="LayerOwnerKind.AsyncSyncGroup"/>, and null
        /// for every other kind.</summary>
        public readonly string detail;

        public LayerOwner(LayerOwnerKind kind, string subject, string detail = null)
        {
            this.kind = kind;
            this.subject = subject;
            this.detail = detail;
        }
    }

    /// <summary>
    /// Which layers of a controller DaerD generated, and on whose behalf.
    ///
    /// <para>NO NEW LEDGER.</para>
    /// Every claim here is already written down: an async sync setup names its cycle, Ready,
    /// Stale and group layers, a gadget names the layer it lives in, and a recipe names what it
    /// owns. This is those references turned round — layer first instead of record first — and
    /// nothing else. A second list of "layers DaerD owns" would be a second thing to keep
    /// correct while a gadget is deleted or a recipe regenerates, and the first one to be wrong.
    ///
    /// <para>WHY IT IS REMEMBERED.</para>
    /// The layer list asks about every row on every repaint, which is every time the pointer
    /// moves across it, and answering means walking four saved lists. So the answer is built
    /// once per controller and dropped when something that could change it happens: the panel
    /// says so on the structural notifications it already listens to, and
    /// <see cref="GraphFrameData.ForgetHolders"/> says so for everything that reaches the saved
    /// data without passing through the window — an import, a recipe's Generate, a test.
    /// Both are the invalidations that already exist; this adds no third one (ADR 0028).
    /// </summary>
    static class LayerOwners
    {
        // Keyed by reference identity for the reason GraphFrameData's holder table is: Unity
        // reports every destroyed Object as equal to null, and so as equal to each other.
        static readonly Dictionary<AnimatorController, Dictionary<AnimatorStateMachine, List<LayerOwner>>>
            s_maps = new Dictionary<AnimatorController, Dictionary<AnimatorStateMachine, List<LayerOwner>>>(
                GraphFrameData.ControllerIdentity.Instance);

        static readonly List<LayerOwner> None = new List<LayerOwner>();

        /// <summary>How many times a map has been built since the editor started, so a test can
        /// assert that the answer is remembered rather than recomputed per repaint.</summary>
        internal static int Builds { get; private set; }

        /// <summary>Drops every remembered map. Cheap by design — the next lookup refills the
        /// one controller that is being looked at.</summary>
        public static void Forget() => s_maps.Clear();

        /// <summary>The claims on one layer, newest record last; empty when DaerD generated
        /// nothing here. Empty is the common answer, so it is a shared list rather than a fresh
        /// one per row.</summary>
        public static IReadOnlyList<LayerOwner> Of(AnimatorController controller,
            AnimatorStateMachine machine)
        {
            if (controller == null || machine == null) return None;
            return Map(controller).TryGetValue(machine, out var owners) ? owners : None;
        }

        static Dictionary<AnimatorStateMachine, List<LayerOwner>> Map(AnimatorController controller)
        {
            if (s_maps.TryGetValue(controller, out var known)) return known;
            var map = Build(controller);
            s_maps[controller] = map;
            return map;
        }

        static Dictionary<AnimatorStateMachine, List<LayerOwner>> Build(AnimatorController controller)
        {
            Builds++;
            var map = new Dictionary<AnimatorStateMachine, List<LayerOwner>>();

            foreach (var config in GraphFrameData.GetAsyncSyncs(controller))
            {
                Add(map, config.layer, new LayerOwner(LayerOwnerKind.AsyncSyncCycle, config.baseName));
                Add(map, config.readyLayer, new LayerOwner(LayerOwnerKind.AsyncSyncReady, config.baseName));
                Add(map, config.staleLayer, new LayerOwner(LayerOwnerKind.AsyncSyncStale, config.baseName));
                if (config.groups == null) continue;
                foreach (var group in config.groups)
                    if (group != null)
                        Add(map, group.layer,
                            new LayerOwner(LayerOwnerKind.AsyncSyncGroup, config.baseName, group.name));
            }

            foreach (var config in GraphFrameData.GetGadgets(controller))
                Add(map, config.layer, new LayerOwner(LayerOwnerKind.DbtGadgetHost, config.output));

            foreach (var config in GraphFrameData.GetObjectGadgets(controller))
                Add(map, config.layer, new LayerOwner(
                    config.mode == (int)ToggleBuilder.Mode.Layer
                        ? LayerOwnerKind.ObjectGadget
                        : LayerOwnerKind.ObjectGadgetHost,
                    config.name));

            foreach (var entry in GraphFrameData.GetCodeOwned(controller))
                Add(map, entry.Key, new LayerOwner(LayerOwnerKind.Recipe, entry.Value.name));

            return map;
        }

        static void Add(Dictionary<AnimatorStateMachine, List<LayerOwner>> map,
            AnimatorStateMachine machine, LayerOwner owner)
        {
            if (machine == null) return;
            if (!map.TryGetValue(machine, out var owners))
                map[machine] = owners = new List<LayerOwner>();
            owners.Add(owner);
        }

        /// <summary>
        /// The claims as a sentence per line, for the tooltip on the generated-layer icon.
        ///
        /// Layers owned outright are named one by one, because which setup or which gadget it is
        /// decides where to go and edit it. Shared hosts are COUNTED instead: a Direct blend tree
        /// layer is meant to accumulate gadgets, so listing them would make the tooltip grow
        /// without bound, and the names are in the home screen's list where they can be acted on
        /// anyway. What the reader needs here is that the layer is a host and how much is in it.
        /// </summary>
        public static string Describe(IReadOnlyList<LayerOwner> owners)
        {
            if (owners == null || owners.Count == 0) return string.Empty;
            var lines = new List<string>();
            int dbtHosted = 0;
            int objectHosted = 0;
            foreach (var owner in owners)
                switch (owner.kind)
                {
                    case LayerOwnerKind.AsyncSyncCycle:
                        lines.Add(L.Tr("The cycle of the async sync setup '{0}'.", owner.subject));
                        break;
                    case LayerOwnerKind.AsyncSyncReady:
                        lines.Add(L.Tr("The Ready watcher of the async sync setup '{0}'.", owner.subject));
                        break;
                    case LayerOwnerKind.AsyncSyncStale:
                        lines.Add(L.Tr("The Stale watcher of the async sync setup '{0}'.", owner.subject));
                        break;
                    case LayerOwnerKind.AsyncSyncGroup:
                        lines.Add(L.Tr("The commit layer of the group '{0}' in the async sync setup '{1}'.",
                            owner.detail, owner.subject));
                        break;
                    case LayerOwnerKind.ObjectGadget:
                        lines.Add(L.Tr("The object gadget '{0}'.", owner.subject));
                        break;
                    case LayerOwnerKind.Recipe:
                        lines.Add(L.Tr("Generated by the recipe '{0}'.", owner.subject));
                        break;
                    case LayerOwnerKind.DbtGadgetHost:
                        dbtHosted++;
                        break;
                    default:
                        objectHosted++;
                        break;
                }
            // After the outright claims: a host line is about the layer's role, and reads as the
            // qualification it is once what owns the layer has been said.
            if (dbtHosted > 0)
                lines.Add(L.Tr("Hosts {0} DBT gadget(s) in its root Direct blend tree.", dbtHosted));
            if (objectHosted > 0)
                lines.Add(L.Tr("Hosts {0} object gadget(s) in its root Direct blend tree.", objectHosted));
            return string.Join("\n", lines);
        }
    }
}

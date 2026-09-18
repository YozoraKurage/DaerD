using System.Collections.Generic;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The one place in the viewer that guesses. A VRChat avatar is driven by a layer mixer
    /// whose inputs are the avatar's playable layers, and the mixer says nothing about which
    /// input is which — index 3 is index 3. This turns a recognised graph's indices into the
    /// slot names their author meant, so the picture reads as Gesture and FX rather than as 6
    /// and 8.
    ///
    /// <para>It is an inference and is labelled as one everywhere it surfaces. Nothing here is
    /// read off the graph; the names come from a table, and the table is chosen by recognising
    /// the tool that built the graph.</para>
    ///
    /// <para><b>Why the graph's NAME and not its shape.</b> The obvious test — a layer mixer
    /// with nine inputs whose first is empty — does not identify anything. Read from the two
    /// tools installed beside this package, both build exactly that: GestureManager
    /// (ModuleVrc3.InitForAvatar) and Av3Emulator (LyumaAv3Runtime) each make a mixer of
    /// layers + 1 inputs and start filling at input 1, and their orders are not the same.
    /// Matching on the count would label one of them with the other's names, which is worse
    /// than not labelling it: a wrong name reads as a fact. The name a graph is created with is
    /// a constant in the tool's own source, so that is what is matched, and the expected input
    /// count is then required as a second check so a future version that changes shape stops
    /// being labelled instead of being mislabelled.</para>
    ///
    /// <para><b>What is deliberately not here.</b> The VRChat client's own layer stack. It
    /// cannot be verified from anything in this repository, and a graph built by the client is
    /// out of reach anyway: <c>UnityEditor.Playables.Utility.GetAllGraphs</c> lists the graphs
    /// alive in this editor, and the client is not this editor. Av3Emulator is likewise not
    /// labelled — its order follows the avatar descriptor's own arrays rather than a sort of
    /// its own, so it was not confirmed here.</para>
    /// </summary>
    static class VrcLayerSlots
    {
        /// <summary>A recognised layout: which tool it belongs to, and what its mixer's inputs
        /// are called. A null entry is a slot the tool leaves empty and that keeps its
        /// index.</summary>
        internal sealed class Layout
        {
            /// <summary>The prefix the tool's graph name starts with — shown to the user as the
            /// reason the guess was made.</summary>
            public readonly string Tool;

            public readonly string[] Slots;

            public Layout(string tool, string[] slots)
            {
                Tool = tool;
                Slots = slots;
            }
        }

        /// <summary>
        /// GestureManager's order, read from version 3.9's sources: the mixer is created with
        /// the avatar's layer count + 1 inputs, input 0 is never connected, and the layers are
        /// sorted into a fixed order before being connected from input 1 upwards
        /// (ModuleVrc3Styles.Data.SortValue). The sort is why this layout can be stated at all
        /// — the order does not depend on how the avatar's descriptor happens to list its
        /// layers, only on which of the eight it has.
        /// </summary>
        static readonly Layout GestureManager = new Layout("Gesture Manager", new[]
        {
            null, "Base", "Additive", "Sitting", "TPose", "IKPose", "Gesture", "Action", "FX",
        });

        static readonly Layout[] Known = { GestureManager };

        /// <summary>
        /// Names the slots of the layer mixer that feeds the snapshot's output, if the graph is
        /// one of the recognised ones, and returns the layout that was used (or null).
        /// </summary>
        public static Layout Infer(PlayableGraphSnapshot snapshot)
        {
            if (snapshot == null) return null;
            var layout = Match(snapshot.Name);
            if (layout == null) return null;

            var mixer = LayerMixerUnderOutput(snapshot);
            // The count is the second half of the match: same tool, different shape, no labels.
            if (mixer == null || mixer.InputCount != layout.Slots.Length) return null;

            foreach (var node in snapshot.Nodes)
            {
                if (node.ParentIndex != mixer.Index) continue;
                if (node.InputIndex < 0 || node.InputIndex >= layout.Slots.Length) continue;
                node.SlotName = layout.Slots[node.InputIndex];
            }
            return layout;
        }

        static Layout Match(string graphName)
        {
            if (string.IsNullOrEmpty(graphName)) return null;
            foreach (var layout in Known)
                if (graphName.StartsWith(layout.Tool, System.StringComparison.Ordinal))
                    return layout;
            return null;
        }

        /// <summary>The layer mixer an output is driven by, which is the only mixer whose inputs
        /// are the avatar's playable layers. A mixer further down is somebody's blend and means
        /// nothing here.</summary>
        static PlayableNodeInfo LayerMixerUnderOutput(PlayableGraphSnapshot snapshot)
        {
            var outputs = new List<int>();
            foreach (var node in snapshot.Nodes)
                if (node.IsOutput) outputs.Add(node.Index);

            foreach (var node in snapshot.Nodes)
                if (node.Kind == PlayableKind.AnimationLayerMixer && outputs.Contains(node.ParentIndex))
                    return node;
            return null;
        }
    }
}

using System.Collections.Generic;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Yozolab.DaerD
{
    /// <summary>
    /// What a playable is, coarsely — only as fine as the one use the viewer has for the answer,
    /// which is picking the node's colour. A type the list does not name lands in
    /// <see cref="Other"/> and still draws; nothing here is a whitelist.
    /// </summary>
    enum PlayableKind
    {
        Output,
        AnimatorController,
        AnimationClip,
        AnimationMixer,
        AnimationLayerMixer,
        Script,
        Other,
    }

    /// <summary>
    /// One node of a snapshot: a playable, or the output that a playable feeds. Every field is
    /// an answer the handle gave at the moment the snapshot was taken — nothing here is a live
    /// view, and nothing here holds a <see cref="Playable"/>, so a snapshot outlives the graph
    /// it came from and can be asserted on in a headless test.
    /// </summary>
    sealed class PlayableNodeInfo
    {
        /// <summary>Position in <see cref="PlayableGraphSnapshot.Nodes"/>. The walk is
        /// depth-first in output order, so the same graph shape always numbers the same way and
        /// the view can address a node by index across a refresh.</summary>
        public int Index = -1;

        /// <summary>The node this one feeds, or -1 for an output (which feeds nothing).</summary>
        public int ParentIndex = -1;

        /// <summary>Which input of the parent this node occupies, or -1 for an output.</summary>
        public int InputIndex = -1;

        /// <summary>The weight the parent gives that input. For an output node it is the
        /// output's own weight instead.</summary>
        public float InputWeight;

        /// <summary>The inferred VRChat-style name of the slot this node sits in, or null when
        /// nothing was inferred. A guess about shape — see <see cref="VrcLayerSlots"/>.</summary>
        public string SlotName;

        /// <summary>Distance from the output, which is 0.</summary>
        public int Depth;

        public PlayableKind Kind = PlayableKind.Other;

        /// <summary>The short name of the type the handle reports, e.g. AnimationClipPlayable.</summary>
        public string TypeName = string.Empty;

        /// <summary>A name for the thing the playable is playing, when the handle offers one at
        /// all: the clip of an AnimationClipPlayable, the editor name of an output. Empty
        /// otherwise — most playables are anonymous.</summary>
        public string Label = string.Empty;

        public PlayState State;
        public double Time;
        public double Duration;
        public double Speed;
        public int InputCount;
        public int OutputCount;

        /// <summary>The object an AnimationPlayableOutput writes to, by name, or empty. Output
        /// nodes only.</summary>
        public string TargetName = string.Empty;

        /// <summary>True when the walk stopped here although the playable has inputs: the
        /// AnimatorControllerPlayable case, and the depth bound. Says "there is more below this"
        /// so the view can mark the node rather than quietly lying about a leaf.</summary>
        public bool InputsHidden;

        public bool IsOutput => Kind == PlayableKind.Output;
    }

    /// <summary>
    /// One PlayableGraph, read once into plain data: the outputs it drives and the tree of
    /// playables under each, with the weight on every connection.
    ///
    /// This half of the viewer knows nothing about GraphView, which is the point — the shape a
    /// graph is found to have is the thing worth pinning in a test, and a test that had to open
    /// a window to see it would pin the window instead. Same split as EdgeCommands under the
    /// animator graph.
    ///
    /// <para>What it is NOT: a live view. Every number is from the instant <see cref="Of"/>
    /// ran, so a running graph needs a new snapshot per refresh rather than a re-read of this
    /// one.</para>
    /// </summary>
    sealed class PlayableGraphSnapshot
    {
        /// <summary>
        /// Depth is bounded, at PlayRecorder's 24, because a graph is somebody else's data
        /// structure: a cycle in one is their bug, and without a bound it would be ours — a
        /// hung editor. The bound is generous next to the depth anything real reaches (a VRChat
        /// layer stack is 2).
        /// </summary>
        public const int MaxDepth = 24;

        /// <summary>The graph's editor name, which is whatever its author passed to
        /// <c>PlayableGraph.Create</c>.</summary>
        public string Name = string.Empty;

        /// <summary>How many playables the graph says it holds. Not the same as
        /// <see cref="Nodes"/>: the walk stops at an AnimatorControllerPlayable, and the
        /// several playables Mecanim keeps inside one are counted here and drawn nowhere.</summary>
        public int PlayableCount;

        /// <summary>Outputs first, then the playables under each, depth first.</summary>
        public readonly List<PlayableNodeInfo> Nodes = new List<PlayableNodeInfo>();

        bool _insideControllers = true;

        /// <summary>The VRChat-style layout that was recognised, or null. Presented as an
        /// inference everywhere it is shown — see <see cref="VrcLayerSlots"/>.</summary>
        public VrcLayerSlots.Layout Layout;

        /// <summary>
        /// A cheap identity for the graph's SHAPE, so a refresh can tell "the same graph, new
        /// weights" from "a different graph" and only rebuild the view for the second. Weights,
        /// times and play states are deliberately not in it — those are what changes every
        /// frame in a graph that is running normally.
        /// </summary>
        public string Signature
        {
            get
            {
                var text = new System.Text.StringBuilder(Name);
                foreach (var node in Nodes)
                    text.Append('|').Append((int)node.Kind).Append(':').Append(node.ParentIndex)
                        .Append(':').Append(node.InputIndex).Append(':').Append(node.Label);
                return text.ToString();
            }
        }

        /// <summary>
        /// Every graph alive in the editor, snapshotted.
        ///
        /// Measured: <c>Utility.GetAllGraphs</c> hands out a graph nobody registered, which is
        /// how a graph is found without knowing who built it — and it also keeps handing out a
        /// graph that has been destroyed, for the rest of the tick in which it died, still
        /// answering IsValid with true. Nothing here can catch that one; the window's periodic
        /// refresh is what drops it, because by the next tick Unity has stopped listing it.
        /// <see cref="Of"/>'s IsValid check does catch the other stale case — a handle from a
        /// previous play session.
        /// </summary>
        public static List<PlayableGraphSnapshot> Discover(bool insideControllers = true)
        {
            var found = new List<PlayableGraphSnapshot>();
            foreach (var graph in UnityEditor.Playables.Utility.GetAllGraphs())
            {
                var snapshot = Of(graph, insideControllers);
                if (snapshot != null) found.Add(snapshot);
            }
            return found;
        }

        /// <summary>
        /// Reads one graph, or returns null if the handle is not a live graph.
        ///
        /// <paramref name="insideControllers"/> decides what an AnimatorControllerPlayable is:
        /// the whole graph as Unity holds it (true — the clips and mixers Mecanim runs a
        /// controller with are playables like any other, and a viewer that hides them answers
        /// "what is animating" with a row of controller names), or one node standing for the
        /// controller (false). Measured: a three-layer controller with one state per layer is
        /// 26 playables inside, an internal layer mixer over a nameless mixer pair and a pose
        /// playable per layer — the crossfade slots — none of them carrying a name the reader
        /// wrote. A VRChat stack of eight controllers is a few hundred such nodes, which is why
        /// the window offers the other answer as well.
        /// </summary>
        public static PlayableGraphSnapshot Of(PlayableGraph graph, bool insideControllers = true)
        {
            if (!graph.IsValid()) return null;

            var snapshot = new PlayableGraphSnapshot
            {
                Name = graph.GetEditorName(),
                PlayableCount = graph.GetPlayableCount(),
                _insideControllers = insideControllers,
            };

            for (int i = 0; i < graph.GetOutputCount(); i++)
            {
                var output = graph.GetOutput(i);
                if (!output.IsOutputValid()) continue;
                var node = snapshot.Add(new PlayableNodeInfo
                {
                    Kind = PlayableKind.Output,
                    TypeName = ShortName(output.GetPlayableOutputType()),
                    Label = UnityEditor.Playables.PlayableOutputEditorExtensions.GetEditorName(output),
                    TargetName = TargetNameOf(output),
                    InputWeight = output.GetWeight(),
                    Depth = 0,
                });
                var source = output.GetSourcePlayable();
                node.InputCount = source.IsValid() ? 1 : 0;
                snapshot.Walk(source, node, inputIndex: 0, weight: output.GetWeight(), depth: 1);
            }

            snapshot.Layout = VrcLayerSlots.Infer(snapshot);
            return snapshot;
        }

        PlayableNodeInfo Add(PlayableNodeInfo node)
        {
            node.Index = Nodes.Count;
            Nodes.Add(node);
            return node;
        }

        /// <summary>
        /// One playable and everything feeding it.
        ///
        /// A playable that feeds two parents is visited — and drawn — once per parent: a
        /// PlayableGraph is a DAG and this is a tree, which is the trade the layout is built on.
        /// The depth bound is what keeps that trade safe when the DAG is in fact cyclic.
        /// </summary>
        void Walk(Playable playable, PlayableNodeInfo parent, int inputIndex, float weight, int depth)
        {
            if (!playable.IsValid()) return;

            var node = Add(new PlayableNodeInfo
            {
                ParentIndex = parent.Index,
                InputIndex = inputIndex,
                InputWeight = weight,
                Depth = depth,
                Kind = KindOf(playable),
                TypeName = ShortName(playable.GetPlayableType()),
                Label = LabelOf(playable),
                State = playable.GetPlayState(),
                Time = playable.GetTime(),
                Duration = playable.GetDuration(),
                Speed = playable.GetSpeed(),
                InputCount = playable.GetInputCount(),
                OutputCount = playable.GetOutputCount(),
            });

            // Two reasons to stop with inputs still under us, both marked so the node can say
            // so: the depth bound, which is defensive, and a controller playable when the
            // caller asked for one node per controller.
            if ((node.Kind == PlayableKind.AnimatorController && !_insideControllers) || depth >= MaxDepth)
            {
                node.InputsHidden = playable.GetInputCount() > 0;
                return;
            }

            // The weight belongs to the parent's input rather than to the child, so it is read
            // here and handed down.
            for (int i = 0; i < playable.GetInputCount(); i++)
                Walk(playable.GetInput(i), node, i, playable.GetInputWeight(i), depth + 1);
        }

        static string TargetNameOf(PlayableOutput output)
        {
            if (!output.IsPlayableOutputOfType<AnimationPlayableOutput>()) return string.Empty;
            var target = ((AnimationPlayableOutput)output).GetTarget();
            // A graph whose Animator has been destroyed answers a real null here, so this is
            // also how an output left over from a previous play session reads as anonymous
            // rather than throwing.
            return target == null ? string.Empty : target.name;
        }

        static string LabelOf(Playable playable)
        {
            if (playable.IsPlayableOfType<AnimationClipPlayable>())
            {
                var clip = ((AnimationClipPlayable)playable).GetAnimationClip();
                return clip == null ? string.Empty : clip.name;
            }
            return string.Empty;
        }

        static PlayableKind KindOf(Playable playable)
        {
            if (playable.IsPlayableOfType<AnimatorControllerPlayable>()) return PlayableKind.AnimatorController;
            if (playable.IsPlayableOfType<AnimationClipPlayable>()) return PlayableKind.AnimationClip;
            // Layer mixer first: it is not an AnimationMixerPlayable as far as the handle is
            // concerned (measured — IsPlayableOfType is exact, not an is-a), but the order
            // states the intent anyway.
            if (playable.IsPlayableOfType<AnimationLayerMixerPlayable>()) return PlayableKind.AnimationLayerMixer;
            if (playable.IsPlayableOfType<AnimationMixerPlayable>()) return PlayableKind.AnimationMixer;

            var type = playable.GetPlayableType();
            if (type == null) return PlayableKind.Other;
            // A ScriptPlayable is reported by the behaviour it carries rather than by the
            // wrapper, so both spellings are accepted rather than betting on one.
            if (typeof(PlayableBehaviour).IsAssignableFrom(type)) return PlayableKind.Script;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ScriptPlayable<>))
                return PlayableKind.Script;
            return PlayableKind.Other;
        }

        static string ShortName(System.Type type) => type == null ? string.Empty : type.Name;
    }
}

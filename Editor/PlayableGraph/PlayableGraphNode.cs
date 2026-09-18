using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>
    /// One playable, or one output, drawn. The node owns nothing: its position comes from the
    /// view's layout and its facts come from a <see cref="PlayableNodeInfo"/> that is replaced
    /// wholesale on every refresh, so the only state it keeps is the two labels it rewrites in
    /// place while the shape underneath stays the same.
    /// </summary>
    sealed class PlayableGraphNode : GraphNodeBase
    {
        public PlayableNodeInfo Info { get; private set; }
        public override object Model => Info;

        readonly Label _slot;
        readonly Label _live;

        public PlayableGraphNode(PlayableNodeInfo info, bool hasParent, bool hasChildren)
        {
            Info = info;

            if (hasChildren) { AddInputPort(); Input.pickingMode = PickingMode.Ignore; }
            if (hasParent) { AddOutputPort(); Output.pickingMode = PickingMode.Ignore; }

            // The layout owns positions, so the node is selectable and nothing else — dragging
            // one would only move it back on the next refresh.
            capabilities = Capabilities.Selectable;

            title = TitleOf(info);
            titleContainer.style.backgroundColor = ColorOf(info.Kind);

            var body = new VisualElement();
            body.style.paddingLeft = 8;
            body.style.paddingRight = 8;
            body.style.paddingTop = 4;
            body.style.paddingBottom = 4;

            _slot = new Label { pickingMode = PickingMode.Ignore };
            body.Add(_slot);
            _live = new Label { pickingMode = PickingMode.Ignore };
            body.Add(_live);

            extensionContainer.Add(body);
            RefreshExpandedState();
            RefreshPorts();

            SetInfo(info);
        }

        /// <summary>Takes the same node's facts from a newer snapshot. Only the two changing
        /// lines are rewritten; the title and colour belong to the shape, and a shape change
        /// rebuilds the whole view instead.</summary>
        public void SetInfo(PlayableNodeInfo info)
        {
            Info = info;

            string slot = info.SlotName;
            _slot.style.display = string.IsNullOrEmpty(slot) && info.InputIndex < 0
                ? DisplayStyle.None
                : DisplayStyle.Flex;
            if (!string.IsNullOrEmpty(slot))
                // The "?" is the whole honesty of the feature: this name was guessed from the
                // graph's shape, and the view's note says which layout was matched.
                _slot.text = L.Tr("slot {0}: {1}?", info.InputIndex, slot);
            else if (info.InputIndex >= 0)
                _slot.text = L.Tr("slot {0}", info.InputIndex);

            if (info.IsOutput)
                _live.text = L.Tr("weight {0}", info.InputWeight.ToString("0.00"));
            else
                _live.text = L.Tr("weight {0} · t {1}", info.InputWeight.ToString("0.00"),
                    PlayableFacts.Seconds(info.Time));

            tooltip = info.InputsHidden
                ? L.Tr("This playable has inputs the viewer does not draw.")
                : string.Empty;
        }

        static string TitleOf(PlayableNodeInfo info)
        {
            if (!string.IsNullOrEmpty(info.Label)) return info.Label;
            // "AnimationClipPlayable" reads as "AnimationClip" once the column is all
            // playables; the suffix is the one word every title would share.
            string name = info.TypeName;
            const string suffix = "Playable";
            if (name.Length > suffix.Length && name.EndsWith(suffix, System.StringComparison.Ordinal))
                name = name.Substring(0, name.Length - suffix.Length);
            return name;
        }

        public static Color ColorOf(PlayableKind kind)
        {
            switch (kind)
            {
                case PlayableKind.Output: return DaerDColors.PlayableOutput;
                case PlayableKind.AnimatorController: return DaerDColors.PlayableAnimatorController;
                case PlayableKind.AnimationClip: return DaerDColors.PlayableClip;
                case PlayableKind.AnimationLayerMixer: return DaerDColors.PlayableLayerMixer;
                case PlayableKind.AnimationMixer: return DaerDColors.PlayableMixer;
                case PlayableKind.Script: return DaerDColors.PlayableScript;
                default: return DaerDColors.PlayableOther;
            }
        }
    }
}

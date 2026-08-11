using System;
using UnityEditor.Animations;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    class StateNode : GraphNodeBase
    {
        public AnimatorState State { get; }
        public override object Model => State;

        readonly Label _nameLabel;
        readonly Label _motionLabel;
        readonly Action<AnimatorState> _onOpenBlendTree;

        bool _highlighted;
        bool _dropTarget;
        // The playback the node is currently showing; a fresh node shows none, which is what
        // these start as.
        bool _playing;
        bool _next;
        float _shownProgress = -1f;
        TextField _renameField;
        readonly VisualElement _nodeBorder;
        readonly Label _wdBadge;
        readonly Label _behaviourBadge;
        readonly VisualElement _badgeRow;
        readonly VisualElement _progressBar;

        public StateNode(AnimatorState state, Action<AnimatorState> onOpenBlendTree = null)
        {
            State = state;
            _onOpenBlendTree = onOpenBlendTree;
            AddToClassList("state-node");
            AddToClassList("compact-node");
            AddInputPort();
            AddOutputPort();

            // The title bar is dropped (see DaerD.uss); the name and motion show in a
            // text column between the input port (left) and output port (right). Every
            // state node uses the same fixed width.
            Input.tooltip = "Incoming transitions";
            Output.tooltip = "Drag from here to create a transition";

            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.AddToClassList("compact-node__text");

            _nameLabel = new Label { pickingMode = PickingMode.Ignore };
            _nameLabel.AddToClassList("compact-node__name");
            _motionLabel = new Label { pickingMode = PickingMode.Ignore };
            _motionLabel.AddToClassList("compact-node__motion");
            text.Add(_nameLabel);
            text.Add(_motionLabel);

            topContainer.Insert(1, text);

            // Badges: WD (Write Defaults) and B (behaviours), always shown as tiny text in the
            // node's top-right corner. Overlaid absolutely so the node keeps its compact two-line
            // layout; state is conveyed by colour (lit when active, grey when not).
            _badgeRow = new VisualElement { pickingMode = PickingMode.Ignore };
            _badgeRow.AddToClassList("state-node__badges");
            _wdBadge = new Label("WD") { pickingMode = PickingMode.Ignore };
            _wdBadge.AddToClassList("state-node__badge");
            _behaviourBadge = new Label("B") { pickingMode = PickingMode.Ignore };
            _behaviourBadge.AddToClassList("state-node__badge");
            _badgeRow.Add(_wdBadge);
            _badgeRow.Add(_behaviourBadge);
            Add(_badgeRow);

            // Play-mode readout: a hairline across the bottom of the node, hidden until the
            // layer is actually in this state.
            _progressBar = new VisualElement { pickingMode = PickingMode.Ignore };
            _progressBar.AddToClassList("state-node__progress");
            _progressBar.style.display = DisplayStyle.None;
            Add(_progressBar);

            capabilities |= Capabilities.Movable | Capabilities.Selectable | Capabilities.Deletable
                          | Capabilities.Copiable | Capabilities.Snappable;

            // Double-click on a state whose motion is a BlendTree drills into the tree view,
            // matching the affordance the sub-state machine node already offers.
            RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2 && evt.button == 0 && State?.motion is BlendTree)
                {
                    _onOpenBlendTree?.Invoke(State);
                    evt.StopPropagation();
                }
            });

            _nodeBorder = this.Q("node-border");
            ApplyBodyColor(DaerDColors.StateBody);

            RefreshLabels();
            RefreshExpandedState();
            RefreshPorts();
        }

        /// <summary>Paints the rounded body and the left/right port columns one opaque colour.</summary>
        void ApplyBodyColor(Color color)
        {
            if (_nodeBorder != null) _nodeBorder.style.backgroundColor = color;
            inputContainer.style.backgroundColor = color;
            outputContainer.style.backgroundColor = color;
        }

        // Suppress the stock node menu (its "Disconnect all" lands at the top, easy to mis-click);
        // AnimatorGraphView builds the full context menu and re-adds Disconnect at the bottom.
        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt) { }

        public void RefreshLabels()
        {
            title = State.name;
            _nameLabel.text = State.name;
            _motionLabel.text = DescribeMotion(State.motion);
            tooltip = State.motion is BlendTree
                ? "Double-click to open the blend tree view"
                : string.Empty;

            var behaviours = State.behaviours;
            int behaviourCount = behaviours != null ? behaviours.Length : 0;
            _badgeRow.style.display = DaerDSettings.ShowStateBadges ? DisplayStyle.Flex : DisplayStyle.None;
            _wdBadge.style.color = State.writeDefaultValues ? DaerDColors.BadgeWriteDefaultsOn : DaerDColors.BadgeOff;
            _wdBadge.tooltip = "Write Defaults: " + (State.writeDefaultValues ? "ON" : "OFF");
            _behaviourBadge.style.color = behaviourCount > 0 ? DaerDColors.BadgeBehavioursOn : DaerDColors.BadgeOff;
            _behaviourBadge.tooltip = behaviourCount > 0
                ? behaviourCount + " StateMachineBehaviour(s)"
                : "No StateMachineBehaviours";
        }

        /// <summary>
        /// Swaps the name (or motion) label for a one-shot text field so it can be renamed in place.
        /// Commits on Enter or focus-out, cancels on Escape. <paramref name="onCommit"/> receives the
        /// trimmed text and is responsible for the actual rename + undo.
        /// </summary>
        public void BeginInlineEdit(string initial, Action<string> onCommit, bool motionLabel)
        {
            if (_renameField != null) return;   // already editing
            var label = motionLabel ? _motionLabel : _nameLabel;
            var parent = label.parent;
            if (parent == null) return;

            var field = new TextField { value = initial ?? string.Empty };
            field.AddToClassList("compact-node__rename");
            _renameField = field;

            int index = parent.IndexOf(label);
            label.style.display = DisplayStyle.None;
            parent.Insert(index, field);

            bool finished = false;
            void Finish(bool commit)
            {
                if (finished) return;
                finished = true;
                string value = field.value;
                _renameField = null;
                field.RemoveFromHierarchy();
                label.style.display = DisplayStyle.Flex;
                if (commit) onCommit?.Invoke(value?.Trim());
            }

            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    Finish(true);
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    Finish(false);
                    evt.StopPropagation();
                }
            });
            // Focus-out commits, EXCEPT when the field was detached by a graph rebuild (panel == null):
            // that is a teardown, not a user confirmation, so cancel instead of writing a stray value.
            field.RegisterCallback<FocusOutEvent>(_ => Finish(field.panel != null));
            // Keep clicks inside the field from reaching the node (e.g. its double-click handler).
            field.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());

            // Focus once the field is laid out in the panel.
            field.schedule.Execute(() =>
            {
                field.Focus();
                field.SelectAll();
            }).ExecuteLater(0);
        }

        public void SetIsDefault(bool isDefault)
        {
            _nameLabel.style.backgroundColor =
                isDefault ? DaerDColors.DefaultState : (StyleColor)StyleKeyword.Null;
        }

        /// <summary>Highlights the node when the running Animator is in this state, or heading
        /// into it, and runs the bar along the bottom edge as the clip plays.</summary>
        public override void SetPlayback(bool playing, bool next, float progress)
        {
            // A Direct blend tree is played continuously and goes nowhere. Its normalized time
            // is a real number that means nothing, and a bar sweeping across a gadget layer
            // would read as progress through something.
            bool bar = playing
                && !(State.motion is BlendTree tree && tree.blendType == BlendTreeType.Direct);
            float shown = bar ? Mathf.Clamp01(progress) : -1f;

            // Called for every node on every editor tick while playing. Only the one node that
            // moved may touch its style; the rest would repaint the whole graph for nothing.
            if (playing == _playing && next == _next && Mathf.Approximately(shown, _shownProgress))
                return;
            _playing = playing;
            _next = next;
            _shownProgress = shown;

            ApplyBodyColor(playing ? DaerDColors.Playing : next ? DaerDColors.PlayingNext : DaerDColors.StateBody);
            _progressBar.style.display = bar ? DisplayStyle.Flex : DisplayStyle.None;
            if (bar) _progressBar.style.width = new Length(shown * 100f, LengthUnit.Percent);
        }

        /// <summary>Outlines the node when a find-usages query matches it.</summary>
        public void SetHighlight(bool on)
        {
            _highlighted = on;
            ApplyBorder();
        }

        /// <summary>Outlines the node while an AnimationClip is being dragged over it.</summary>
        public void SetDropTarget(bool on)
        {
            _dropTarget = on;
            ApplyBorder();
        }

        void ApplyBorder()
        {
            StyleColor color;
            StyleFloat width;
            if (_dropTarget)
            {
                color = DaerDColors.DropTarget;
                width = 2.5f;
            }
            else if (_highlighted)
            {
                color = DaerDColors.FoundByQuery;
                width = 2f;
            }
            else
            {
                color = StyleKeyword.Null;
                width = StyleKeyword.Null;
            }
            style.borderTopColor = color;
            style.borderBottomColor = color;
            style.borderLeftColor = color;
            style.borderRightColor = color;
            style.borderTopWidth = width;
            style.borderBottomWidth = width;
            style.borderLeftWidth = width;
            style.borderRightWidth = width;
        }

        static string DescribeMotion(Motion motion)
        {
            if (motion == null) return "(no motion)";
            if (motion is BlendTree bt) return "Blend Tree: " + bt.name;
            return motion.name;
        }
    }
}

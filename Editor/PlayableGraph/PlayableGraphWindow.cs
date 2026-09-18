using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>
    /// DD PlayableGraph Viewer — what is actually animating, drawn.
    ///
    /// A PlayableGraph is the layer underneath everything DaerD otherwise talks about: the
    /// controller a user edits ends up as one playable among several, mixed by weights nobody
    /// can see from the Animator window. This window finds every graph alive in the editor,
    /// draws one of them, and reports what the selected playable's own handle answers. It reads
    /// and never writes — the graph belongs to whoever built it.
    ///
    /// <para>Editor-only, and with no way for anything to register a graph with it: graphs are
    /// found by asking Unity for all of them, so a tool needs to do nothing to appear here.</para>
    /// </summary>
    sealed class PlayableGraphWindow : EditorWindow
    {
        [MenuItem("YozoLab/DD PlayableGraph Viewer")]
        public static void Open()
        {
            var window = GetWindow<PlayableGraphWindow>();
            window.titleContent = new GUIContent(L.Tr("DD PlayableGraph Viewer"));
            window.minSize = new Vector2(620f, 360f);
            window.Show();
        }

        /// <summary>How often the graphs are re-read. Weights move every frame in a running
        /// graph and nobody can read a number that does; ten times a second is fast enough to
        /// watch a crossfade and slow enough to cost nothing.</summary>
        const double RefreshInterval = 0.1;

        PlayableGraphView _view;
        ToolbarMenu _picker;
        Label _summary;
        Label _inference;
        Label _empty;
        VisualElement _details;
        readonly List<Label> _values = new List<Label>();
        int _shownIndex = -2;

        List<PlayableGraphSnapshot> _graphs = new List<PlayableGraphSnapshot>();
        bool _insideControllers = true;
        string _chosen;
        double _nextRefresh;

        void OnEnable() => EditorApplication.update += Tick;

        void OnDisable() => EditorApplication.update -= Tick;

        void CreateGUI()
        {
            rootVisualElement.Clear();

            var toolbar = new Toolbar();
            _picker = new ToolbarMenu { text = L.Tr("Graph") };
            toolbar.Add(_picker);
            _summary = new Label { pickingMode = PickingMode.Ignore };
            _summary.style.marginLeft = 8;
            _summary.style.unityTextAlign = TextAnchor.MiddleLeft;
            toolbar.Add(_summary);
            var spacer = new VisualElement { style = { flexGrow = 1 } };
            toolbar.Add(spacer);
            var inside = new ToolbarToggle { text = L.Tr("Inside controllers"), value = _insideControllers };
            // What lies inside a controller playable is Mecanim's own machinery — a few hundred
            // nameless nodes for a VRChat stack — but it is also where every clip is, and a
            // viewer that leaves it out answers "what is animating" with a row of controller
            // names. Drawn by default, foldable when the picture is what matters.
            inside.tooltip = L.Tr("Draw what Mecanim runs inside each controller: the clips, and the mixers that blend them.");
            inside.RegisterValueChangedCallback(evt =>
            {
                _insideControllers = evt.newValue;
                Refresh(force: true);
            });
            toolbar.Add(inside);
            toolbar.Add(new ToolbarButton(() => Refresh(force: true)) { text = L.Tr("Refresh") });
            rootVisualElement.Add(toolbar);

            var body = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1 } };
            rootVisualElement.Add(body);

            var left = new VisualElement { style = { flexGrow = 1 } };
            _view = new PlayableGraphView();
            left.Add(_view);

            // The one line that keeps the slot names honest. Always under the graph, never on
            // the nodes themselves, so it cannot be cropped out of sight by a pan.
            _inference = new Label { pickingMode = PickingMode.Ignore };
            _inference.style.paddingLeft = 6;
            _inference.style.paddingTop = 3;
            _inference.style.paddingBottom = 3;
            _inference.style.whiteSpace = WhiteSpace.Normal;
            left.Add(_inference);
            body.Add(left);

            _empty = new Label(L.Tr("No PlayableGraph is alive in the editor. Enter Play mode, or open a tool that drives an avatar."))
            {
                pickingMode = PickingMode.Ignore,
            };
            _empty.style.position = Position.Absolute;
            _empty.style.left = 12;
            _empty.style.top = 12;
            _empty.style.whiteSpace = WhiteSpace.Normal;
            left.Add(_empty);

            _details = new VisualElement();
            _details.style.width = 260;
            _details.style.paddingLeft = 8;
            _details.style.paddingRight = 8;
            _details.style.paddingTop = 6;
            _details.style.borderLeftWidth = 1;
            _details.style.borderLeftColor = DaerDColors.Separator;
            body.Add(_details);

            Refresh(force: true);
        }

        void Tick()
        {
            if (_view == null) return;
            if (EditorApplication.timeSinceStartup < _nextRefresh) return;
            _nextRefresh = EditorApplication.timeSinceStartup + RefreshInterval;
            Refresh(force: false);
        }

        void Refresh(bool force)
        {
            _graphs = PlayableGraphSnapshot.Discover(_insideControllers);

            var chosen = Pick();
            _view.Show(chosen);
            _empty.style.display = chosen == null ? DisplayStyle.Flex : DisplayStyle.None;

            _summary.text = chosen == null
                ? string.Empty
                : L.Tr("{0} — {1} playables, {2} drawn", chosen.Name, chosen.PlayableCount, chosen.Nodes.Count);

            // The picker is a picker only when there is something to pick; one graph names
            // itself in the summary beside it.
            _picker.style.display = _graphs.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
            if (force || _graphs.Count > 1) BuildPicker();

            _inference.text = chosen?.Layout == null
                ? string.Empty
                : L.Tr("Input names are inferred from the graph's shape ({0} layout) and marked '?' — a guess, not something the graph states.",
                    chosen.Layout.Tool);

            ShowDetails(_view.Selected);
        }

        PlayableGraphSnapshot Pick()
        {
            if (_graphs.Count == 0) return null;
            foreach (var graph in _graphs)
                if (graph.Name == _chosen) return graph;
            _chosen = _graphs[0].Name;
            return _graphs[0];
        }

        void BuildPicker()
        {
            _picker.menu.MenuItems().Clear();
            foreach (var graph in _graphs)
            {
                string name = graph.Name;
                _picker.menu.AppendAction(string.IsNullOrEmpty(name) ? "(unnamed)" : name,
                    _ => { _chosen = name; Refresh(force: true); },
                    _ => name == _chosen ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }
        }

        /// <summary>
        /// Everything the selected playable's handle answers, and nothing else. What a playable
        /// IS inside — a controller's layers, a mixer's masks — is a question for the thing that
        /// built it, not for the handle, and this pane deliberately does not pretend to know.
        ///
        /// <para>The rows are rebuilt only when the selection moves. A playable's time and
        /// weight change on every refresh, and throwing away ten Labels ten times a second to
        /// say so would rebuild the pane far more often than anything in it moves.</para>
        /// </summary>
        void ShowDetails(PlayableNodeInfo info)
        {
            var values = Values(info);
            if (_shownIndex != (info?.Index ?? -1) || _values.Count != values.Count)
                BuildDetails(info, values);
            else
                for (int i = 0; i < values.Count; i++) _values[i].text = values[i].value;
        }

        /// <summary>The pane's contents as text, in a fixed order — the one place that decides
        /// what a node has to say, whether the rows already exist or not.</summary>
        static List<(string key, string value)> Values(PlayableNodeInfo info)
        {
            var lines = new List<(string, string)>();
            if (info == null) return lines;

            lines.Add((L.Tr("Type"), info.TypeName));
            if (info.IsOutput)
            {
                if (!string.IsNullOrEmpty(info.TargetName)) lines.Add((L.Tr("Writes to"), info.TargetName));
                lines.Add((L.Tr("Weight"), info.InputWeight.ToString("0.###")));
                return lines;
            }

            lines.Add((L.Tr("Play state"), PlayableFacts.State(info.State)));
            lines.Add((L.Tr("Time (s)"), PlayableFacts.Seconds(info.Time)));
            lines.Add((L.Tr("Duration (s)"), PlayableFacts.Seconds(info.Duration)));
            lines.Add((L.Tr("Speed"), info.Speed.ToString("0.###")));
            lines.Add((L.Tr("Inputs"), info.InputCount.ToString()));
            lines.Add((L.Tr("Outputs"), info.OutputCount.ToString()));
            lines.Add((L.Tr("Incoming weight"), info.InputWeight.ToString("0.###")));
            lines.Add((L.Tr("Slot"), info.InputIndex.ToString()));
            if (!string.IsNullOrEmpty(info.SlotName))
                lines.Add((L.Tr("Slot name (guess)"), info.SlotName));
            if (info.InputsHidden)
                lines.Add((L.Tr("Not drawn"), L.Tr("this playable's own inputs")));
            return lines;
        }

        void BuildDetails(PlayableNodeInfo info, List<(string key, string value)> lines)
        {
            _details.Clear();
            _values.Clear();
            _shownIndex = info?.Index ?? -1;

            if (info == null)
            {
                _details.Add(new Label(L.Tr("Select a node.")) { style = { whiteSpace = WhiteSpace.Normal } });
                return;
            }

            var title = new Label(string.IsNullOrEmpty(info.Label) ? info.TypeName : info.Label);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.whiteSpace = WhiteSpace.Normal;
            title.style.marginBottom = 4;
            _details.Add(title);

            foreach (var line in lines) Line(line.key, line.value);
        }

        void Line(string label, string value)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            var key = new Label(label) { pickingMode = PickingMode.Ignore };
            key.style.width = 110;
            key.style.color = DaerDColors.Grip;
            row.Add(key);
            var text = new Label(value) { pickingMode = PickingMode.Ignore };
            text.style.flexGrow = 1;
            text.style.whiteSpace = WhiteSpace.Normal;
            row.Add(text);
            _details.Add(row);
            _values.Add(text);
        }
    }
}

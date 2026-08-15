using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The async-sync setup form, shared between the wizard (<see cref="AsyncSyncWindow"/>)
    /// and the sync layer's dedicated panel (AsyncSyncPanel): the generated-sync fields, the
    /// tick list, the drag-to-order cycle list with its Req(uestable) marks, and the
    /// cost/cycle preview. The host owns the layer choice and the apply button; the form owns
    /// every input that ends up in the <see cref="AsyncSyncBuilder.Request"/>.
    ///
    /// One plain round robin — every parameter once, in the order the list is in — is the
    /// whole of what this form authors. Everything else that decides WHEN a value goes out is
    /// drawn and not edited: a grid from <see cref="AsyncSyncBuilder.Request.steps"/>, a cycle
    /// from C#, and the per-target weights of a setup saved before this form stopped handing
    /// them out. All three stay buildable, loadable and exportable — what the wizard offers is
    /// the timeline showing what such a pass does, and the way back to a plain pass.
    ///
    /// They went one at a time and for the same reason. Each was a second vocabulary for "this
    /// value has to go out sooner", and a sync request says it better: at the next step
    /// boundary, for one step of the pass, and only when it is actually raised.
    /// </summary>
    class AsyncSyncForm
    {
        class Row
        {
            public string name;
            public AnimatorControllerParameterType type;
            public bool selected;
            public int rate = 1;
            /// <summary>Accept on-demand sync requests for this target.</summary>
            public bool request;
            /// <summary>Start a slot rather than share channels with the row above.</summary>
            public bool split;
            /// <summary>Name of the group this parameter is assigned with, or null. A group
            /// exists exactly as long as two rows point at it, which is why it is a name on
            /// the row rather than a list beside it.</summary>
            public string group;
        }

        AnimatorController _controller;

        readonly List<Row> _rows = new List<Row>();
        /// <summary>Selected rows in multiplex order — this list IS the cycle order.
        /// Ticking appends, unticking removes, dragging rearranges.</summary>
        readonly List<Row> _order = new List<Row>();
        readonly ListReorder _reorder = new ListReorder();
        string _baseName = "Async";
        AsyncSyncBuilder.IndexEncoding _encoding = AsyncSyncBuilder.IndexEncoding.Auto;
        float _stepSeconds = 0.3f;
        int _floatChannels = 1;
        int _boolChannels = 1;
        /// <summary>Let a step send what the step before it sent, paid for with a clock
        /// phase in the index (<see cref="AsyncSyncBuilder.Request.allowRepeatSteps"/>).</summary>
        bool _allowRepeatSteps;
        /// <summary>Generate the remote-initialized flag
        /// (<see cref="AsyncSyncBuilder.Request.ready"/>).</summary>
        bool _ready;
        /// <summary>Generate the drift-suspicion flag
        /// (<see cref="AsyncSyncBuilder.Request.stale"/>).</summary>
        bool _stale;
        bool _addToStore = true;
        bool _assignEmptyClip = true;
        string _search = string.Empty;
        Vector2 _pickScroll;
        bool _timelineOpen = true;
        /// <summary>A pass written out as a grid, if the setup carries one: one entry per step,
        /// naming what that step sends. Empty means the pass is derived from the weights, so
        /// this list doubles as "does this setup carry a hand-written pass". Not authored
        /// here any more — see the class summary.</summary>
        readonly List<GraphFrameData.AsyncSyncConfig.StepSpec> _steps =
            new List<GraphFrameData.AsyncSyncConfig.StepSpec>();
        /// <summary>Set by anything that can reshape what the grid refers to. The repair runs
        /// once per such edit rather than every repaint: it is stable on a valid grid, but
        /// running it unprompted still invites steps to move on their own.</summary>
        bool _stepsStale;
        /// <summary>Group names in row order, rebuilt each draw — the picker's options.</summary>
        readonly List<string> _groupNames = new List<string>();
        /// <summary>
        /// An explicit cycle the setup already carries: target names, one per step — the
        /// vocabulary <c>c.AsyncSync().Schedule(…)</c> writes. Carried through rather than
        /// edited, because the grid replaced the wizard's own cycle editor and there is no
        /// control here for one. A form that simply dropped what it cannot draw would rebuild
        /// the layer on the rates and then save THAT over its author's pass, which is a silent
        /// way to lose work. Empty for every setup that never had one, and ignored while
        /// <see cref="_steps"/> is present — a grid answers the same question with more of the
        /// picture.
        /// </summary>
        readonly List<string> _schedule = new List<string>();

        // Parameters animation writes (AAP). The scan walks every state, every blend tree and
        // every clip's curve bindings — far too much for the per-event redraw the warnings live
        // in, where dragging a row into place would pay for it dozens of times a second.
        // Cached per controller and dropped the way ParametersPanel drops its own copy: on the
        // structural edits that can change the answer.
        HashSet<string> _animated;
        AnimatorController _animatedController;

        // Rebuilt per draw so a language switch is picked up. Order matches the enum.
        static string[] EncodingLabels() => new[]
        {
            L.Tr("Int (8 bit)"),
            L.Tr("Bool × n (1 bit each)"),
            L.Tr("Auto (fewest synced bits)"),
        };

        public bool HasController => _controller != null;
        public string BaseName => _baseName;

        /// <summary>(Re)binds the form to a controller and rebuilds the candidate rows;
        /// selections are reset (load a config afterwards to restore one).</summary>
        public void SetController(AnimatorController controller)
        {
            _controller = controller;
            _rows.Clear();
            _order.Clear();
            _steps.Clear();
            _schedule.Clear();
            if (controller == null) return;
            foreach (var parameter in controller.parameters)
            {
                if (parameter.type == AnimatorControllerParameterType.Trigger) continue;
                // The machinery must not multiplex itself: IsLocal and the parameters an
                // async-sync setup generated never appear as candidates.
                if (AsyncSyncBuilder.IsReservedName(controller, parameter.name)) continue;
                _rows.Add(new Row { name = parameter.name, type = parameter.type });
            }
        }

        /// <summary>Prefills the base name for the generated parameters. Null / empty is
        /// ignored, so a caller can offer a suggestion without having to check it first; a
        /// <see cref="LoadConfig"/> afterwards still wins, the saved setup's own name being
        /// the more specific answer.</summary>
        public void SetBaseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            _baseName = name;
        }

        /// <summary>
        /// The AAP set for the bound controller, worked out at most once per invalidation.
        /// Hosts hand this to <see cref="AsyncSyncBuilder.Warnings"/> so the draw path never
        /// pays for the scan — see <see cref="InvalidateAnimatedParameters"/> for what makes
        /// it stale.
        /// </summary>
        public HashSet<string> AnimatedParameters()
        {
            if (_animated != null && _animatedController == _controller) return _animated;
            _animatedController = _controller;
            return _animated = AapWriteScan.CollectWrittenParameters(_controller);
        }

        /// <summary>Drops the cached AAP set, so the next draw scans again. Hosts call this on
        /// the structural changes that can add or remove an animated write; a clip edited in
        /// another window shows up on the next such change or when the host regains focus,
        /// which is the right trade for a warning banner against a per-frame walk of the whole
        /// controller.</summary>
        public void InvalidateAnimatedParameters() => _animated = null;

        public void LoadConfig(GraphFrameData.AsyncSyncConfig config)
        {
            _baseName = config.baseName;
            _encoding = (AsyncSyncBuilder.IndexEncoding)config.encoding;
            _stepSeconds = config.stepSeconds;
            _floatChannels = Mathf.Clamp(config.FloatChannelsOrDefault, 1, 8);
            _boolChannels = Mathf.Clamp(config.BoolChannelsOrDefault, 1, 8);
            _allowRepeatSteps = config.allowRepeatSteps;
            _ready = config.ready;
            _stale = config.stale;
            _steps.Clear();
            if (config.steps != null)
                foreach (var step in config.steps)
                    _steps.Add(StepOf(step?.targets));
            // Restored beside the grid rather than converted into one: the two are different
            // vocabularies, and a recipe that said Schedule(…) must still export as Schedule(…)
            // after a round trip through this form.
            _schedule.Clear();
            if (config.schedule != null) _schedule.AddRange(config.schedule);
            _stepsStale = true;

            var rates = config.RateMap();
            foreach (var row in _rows)
            {
                row.selected = false;
                row.rate = 1;
                row.request = false;
                row.split = false;
                row.group = null;
            }
            _order.Clear();
            // The saved target list is ordered — restoring it restores the cycle order.
            foreach (var name in config.targets)
                foreach (var row in _rows)
                    if (row.name == name)
                    {
                        row.selected = true;
                        // Kept as saved, to the model's own ceiling. It used to be clamped to
                        // what the ×N popup could show, which quietly turned a recipe's ×8 into
                        // a ×4 the moment its layer was opened and applied — a saved setup the
                        // form cannot author is still a saved setup it must not rewrite.
                        if (rates.TryGetValue(name, out int rate))
                            row.rate = Mathf.Clamp(rate, 1, AsyncSyncBuilder.MaxRate);
                        row.request = config.requests != null && config.requests.Contains(name);
                        row.split = config.slotBreaks != null && config.slotBreaks.Contains(name);
                        _order.Add(row);
                        break;
                    }

            // After the order, so a member listed in two groups lands in the first one the
            // same way AsyncSyncBuilder.EffectiveGroups would put it there.
            if (config.groups != null)
                foreach (var group in config.groups)
                    if (group?.members != null)
                        foreach (var row in _order)
                            if (string.IsNullOrEmpty(row.group) && group.members.Contains(row.name))
                                row.group = group.name;
        }

        public AsyncSyncBuilder.Request BuildRequest(int layerIndex)
        {
            var request = new AsyncSyncBuilder.Request
            {
                controller = _controller,
                baseName = _baseName != null ? _baseName.Trim() : string.Empty,
                encoding = _encoding,
                stepSeconds = _stepSeconds,
                floatChannels = _floatChannels,
                boolChannels = _boolChannels,
                allowRepeatSteps = _allowRepeatSteps,
                ready = _ready,
                stale = _stale,
                store = ParameterStore.Of(_controller),
                addToStore = _addToStore,
                assignEmptyClip = _assignEmptyClip,
                emptyClip = GraphFrameData.GetEmptyClip(_controller),
                layerIndex = layerIndex,
            };
            foreach (var row in _order)
            {
                request.targets.Add(row.name);
                if (row.rate > 1) request.rates[row.name] = row.rate;
                if (row.request) request.requestTargets.Add(row.name);
                if (row.split) request.slotBreaks.Add(row.name);
            }

            // Groups are read back off the rows: cycle order for the members, first mention
            // for the groups themselves.
            foreach (var row in _order)
            {
                if (string.IsNullOrEmpty(row.group)) continue;
                var group = request.groups.Find(entry => entry.name == row.group);
                if (group == null)
                {
                    group = new GraphFrameData.AsyncSyncConfig.SyncGroup { name = row.group };
                    request.groups.Add(group);
                }
                group.members.Add(row.name);
            }

            // The repair needs the slots, which need the request — hence here, once the
            // targets are in and before the grid goes on. A grid it cannot settle comes back
            // empty, which is exactly how this form spells "use the rates".
            if (_stepsStale && _steps.Count > 0)
            {
                var repaired = AsyncSyncBuilder.RepairSteps(request, _steps);
                _steps.Clear();
                _steps.AddRange(repaired);
            }
            _stepsStale = false;
            ApplyScheduleOverride(request);
            Snapshot(request);
            return request;
        }

        /// <summary>
        /// Puts the carried explicit cycle onto the request, so a setup that has one is
        /// rebuilt on it instead of on the rates. A grid outranks it and is left to win on its
        /// own — <see cref="AsyncSyncSchedule.EffectiveSchedule"/> reads the two in that order.
        ///
        /// A cycle the current slots can still run is passed through EXACTLY as written: a
        /// panel opened and applied without an edit has to give back the setup it was handed,
        /// and a repair that renamed a batched step's spokesman would show up as drift in the
        /// author's C#. Only one the slots have outgrown — a target unticked, channels widened
        /// until two steps merged — is repaired, and one the repair cannot settle comes back
        /// empty, which is how this form spells "use the rates".
        /// </summary>
        void ApplyScheduleOverride(AsyncSyncBuilder.Request request)
        {
            request.scheduleOverride.Clear();
            if (_steps.Count > 0 || _schedule.Count == 0) return;

            request.scheduleOverride.AddRange(_schedule);
            var slots = AsyncSyncBuilder.BuildSlots(request);
            if (AsyncSyncBuilder.ResolveScheduleOverride(request, slots, null) != null) return;

            var repaired = AsyncSyncBuilder.RepairScheduleOverride(request, _schedule);
            _schedule.Clear();
            _schedule.AddRange(repaired);
            request.scheduleOverride.Clear();
            request.scheduleOverride.AddRange(_schedule);
        }

        /// <summary>Copies the grid into the request the rest of the pass reads. Copied rather
        /// than shared: the request is handed to the model, which is entitled to assume it
        /// describes one moment, while this form goes on rewriting the grid as the mouse moves.</summary>
        void Snapshot(AsyncSyncBuilder.Request request)
        {
            request.steps.Clear();
            foreach (var step in _steps) request.steps.Add(StepOf(step.targets));
        }

        /// <summary>
        /// Which group this parameter is assigned with: none, one that already exists, or a
        /// new one. A group is nothing but a name two rows share, so creating one is picking a
        /// name and leaving one is clearing it — there is no list to keep in step.
        /// </summary>
        void DrawGroupPicker(Row row)
        {
            var labels = new GUIContent[_groupNames.Count + 2];
            labels[0] = new GUIContent(L.Tr("(no group)"));
            for (int i = 0; i < _groupNames.Count; i++)
                labels[i + 1] = new GUIContent(_groupNames[i]);
            labels[labels.Length - 1] = new GUIContent(L.Tr("New group…"));

            int current = string.IsNullOrEmpty(row.group)
                ? 0 : _groupNames.IndexOf(row.group) + 1;
            int picked = EditorGUILayout.Popup(current, labels, GUILayout.Width(84));
            if (picked == current) return;
            row.group = picked == 0 ? null
                : picked == labels.Length - 1 ? NextGroupName()
                : _groupNames[picked - 1];
        }

        /// <summary>"Group 1", "Group 2"… — the lowest number nothing answers to yet, so
        /// deleting one and making another does not leave a gap in the names.</summary>
        string NextGroupName()
        {
            // Terminates: the taken set is finite and every candidate name is distinct.
            for (int n = 1; ; n++)
            {
                string name = "Group " + n;
                if (!_groupNames.Contains(name)) return name;
            }
        }

        static GraphFrameData.AsyncSyncConfig.StepSpec StepOf(List<string> targets)
        {
            var step = new GraphFrameData.AsyncSyncConfig.StepSpec();
            if (targets != null) step.targets.AddRange(targets);
            return step;
        }

        // ---- sections --------------------------------------------------------

        public void DrawGeneratedSection()
        {
            EditorGUILayout.LabelField(L.Tr("Generated Sync"), EditorStyles.boldLabel);
            _baseName = EditorGUILayout.TextField(L.Tr("Base Name"), _baseName);
            _encoding = (AsyncSyncBuilder.IndexEncoding)EditorGUILayout.Popup(
                L.Tr("Index Encoding"), (int)_encoding, EncodingLabels());
            DrawChannelCounts();
            _stepSeconds = EditorGUILayout.FloatField(
                new GUIContent(L.Tr("Step Interval (s)"),
                    L.Tr("Dwell per slot. VRChat syncs roughly every 0.3 s — shorter steps risk remotes skipping slots.")),
                _stepSeconds);

            // Turning it off has to bring the pass back into line: a grid drawn with repeats
            // in it is not a pass the decoder can run once the phase stops paying for them.
            EditorGUI.BeginChangeCheck();
            _allowRepeatSteps = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Allow Repeated Steps"),
                    L.Tr("Let a step send what the step before it sent. A clock phase folded into the index tells the two apart, at one more decoder state per parameter set that actually repeats — and, under a Bool index, sometimes one more synced bit.")),
                _allowRepeatSteps);
            if (EditorGUI.EndChangeCheck()) _stepsStale = true;

            _ready = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Remote Initialized Flag"),
                    L.Tr("Generate a local Bool that turns on once this client has decoded every slot at least once — what a remote has instead of a way to ask. The wearer reads it as on from the start, so a remote that has finished initializing is Ready && !IsLocal. Costs one local Bool per slot and a second layer; nothing synced.")),
                _ready);

            _stale = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Drift Suspicion Flag"),
                    L.Tr("Generate a local Bool that turns on when a lap did not bring every slot, and off again when one does. Judged when a slot the pass sends exactly once comes round, so it needs no timer and no margin — and a pass with no such slot cannot carry it. Costs one local Bool per slot and a third layer; nothing synced.")),
                _stale);

            // The generated states are machinery, but Unity (and the analyzer) still want a
            // motion on them; the controller's Empty clip is exactly what that is for. Offered
            // even with no clip designated — applying creates one inside the controller.
            var emptyClip = GraphFrameData.GetEmptyClip(_controller);
            _assignEmptyClip = EditorGUILayout.Toggle(
                new GUIContent(
                    emptyClip != null
                        ? L.Tr("Fill States With '{0}'", emptyClip.name)
                        : L.Tr("Fill States With The Empty Clip"),
                    L.Tr("Fill the generated states with this controller's Empty clip. If none is set yet, a 1-second no-op clip is created inside the controller and registered as its Empty clip.")),
                _assignEmptyClip);

            if (ParameterStore.Of(_controller) != null)
                _addToStore = EditorGUILayout.Toggle(
                    new GUIContent(L.Tr("Add Synced Params To Store"),
                        L.Tr("Add the generated index and channel parameters to the associated parameter store as synced.")),
                    _addToStore);
        }

        /// <summary>
        /// Channel counts for the types actually being multiplexed. Only Floats and Bools
        /// batch, so a Bool-only setup has no business being asked about Float channels —
        /// and with only one of them on screen the count needs no type in its name.
        /// </summary>
        void DrawChannelCounts()
        {
            bool hasFloat = false, hasBool = false;
            foreach (var row in _order)
            {
                if (row.type == AnimatorControllerParameterType.Float) hasFloat = true;
                else if (row.type == AnimatorControllerParameterType.Bool) hasBool = true;
            }

            // Channel counts regroup the slots a hand-timed cycle refers to.
            EditorGUI.BeginChangeCheck();
            if (hasFloat)
                _floatChannels = EditorGUILayout.IntSlider(
                    new GUIContent(hasBool ? L.Tr("Float Channels") : L.Tr("Channels"),
                        L.Tr("Synced Float channels. Each step carries up to this many Float parameters at once — fewer slots and a faster cycle, at 8 synced bits per extra channel.")),
                    _floatChannels, 1, 8);
            if (hasBool)
                _boolChannels = EditorGUILayout.IntSlider(
                    new GUIContent(hasFloat ? L.Tr("Bool Channels") : L.Tr("Channels"),
                        L.Tr("Synced Bool channels. Each step carries up to this many Bool parameters at once — fewer slots and a faster cycle, at 1 synced bit per extra channel.")),
                    _boolChannels, 1, 8);
            if (EditorGUI.EndChangeCheck()) _stepsStale = true;
        }

        /// <summary>The tick list with its search filter. Ticking appends the parameter to
        /// the cycle order below; unticking removes it.</summary>
        public void DrawPickList()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                L.Tr("Parameters To Multiplex") + " (" + _order.Count + "/" + _rows.Count + ")",
                EditorStyles.boldLabel);
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField,
                GUILayout.MinWidth(80), GUILayout.MaxWidth(180));
            EditorGUILayout.EndHorizontal();

            _pickScroll = EditorGUILayout.BeginScrollView(_pickScroll, GUILayout.MinHeight(110),
                GUILayout.MaxHeight(160));
            int visible = 0;
            foreach (var row in _rows)
            {
                if (!string.IsNullOrEmpty(_search)
                    && row.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                visible++;
                bool selected = EditorGUILayout.ToggleLeft(
                    row.name + "  (" + row.type + ")", row.selected);
                if (selected != row.selected)
                    SetSelected(row, selected);
            }
            if (_rows.Count == 0)
                EditorGUILayout.HelpBox(L.Tr("This controller has no Float / Int / Bool parameters."),
                    MessageType.Info);
            else if (visible == 0)
                EditorGUILayout.LabelField(L.Tr("No parameter matches the search."),
                    EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndScrollView();
        }

        /// <summary>Ticks or unticks a target by name — what the pick list does to a row, from
        /// the model's side.</summary>
        internal void SetSelected(string name, bool selected)
        {
            foreach (var row in _rows)
                if (row.name == name)
                {
                    SetSelected(row, selected);
                    return;
                }
        }

        /// <summary>
        /// Unticking drops the row out of the cycle and clears the settings that only mean
        /// something inside one — but NOT the weight. A weight is the one thing on this row the
        /// form cannot hand out any more (the ×N popup went with 4fecc5d), so clearing it on the
        /// way out is a one-way door: two mis-clicks and an Apply turned a recipe's ×8 into a
        /// ×1 with no control touched and nothing said, which is the same thing the load path
        /// was fixed for. Kept on the row instead, so re-ticking gives it back.
        ///
        /// It costs nothing to carry: <see cref="BuildRequest"/> reads the selected rows only,
        /// so a weight sitting on an unticked row reaches neither the built layer nor the saved
        /// setup.
        /// </summary>
        void SetSelected(Row row, bool selected)
        {
            row.selected = selected;
            if (selected)
            {
                if (!_order.Contains(row)) _order.Add(row);
            }
            else
            {
                _order.Remove(row);
                row.request = false;
                row.split = false;
                row.group = null;
            }
            _stepsStale = true;
        }

        /// <summary>
        /// The cycle editor: selected parameters in multiplex order. Drag the handle to
        /// reorder; Req marks the row as requestable (states can ask for it out of turn); the
        /// label on the right is the refresh interval the current schedule actually delivers.
        ///
        /// The ×N column is a reading, not a control. A weight said "give this one a bigger
        /// share of the pass", which is what a sync request says better: a request puts the
        /// value on the wire at the next step boundary, costs the pass one step and only when
        /// it is actually raised, while a weight lengthens the pass for everybody else
        /// permanently — and cannot always be honoured even then. Weights already saved go on
        /// being read, built and exported; this form simply does not hand out new ones.
        /// </summary>
        public void DrawOrderSection(AsyncSyncBuilder.Request request)
        {
            EditorGUILayout.LabelField(L.Tr("Cycle Order"), EditorStyles.boldLabel);
            if (_order.Count == 0)
            {
                EditorGUILayout.LabelField(
                    L.Tr("Tick parameters above — drag them here into the order the cycle should visit them."),
                    EditorStyles.miniLabel);
                return;
            }
            EditorGUILayout.LabelField(
                Manual
                    ? L.Tr("This setup carries a pass written out step by step, so top to bottom is only the listing order. The pass itself is the timeline below.")
                    : _schedule.Count > 0
                        ? L.Tr("This setup carries an explicit cycle written in C#, so top to bottom is only the listing order. The pass itself is the timeline below.")
                        : L.Tr("Top to bottom is the cycle order, and every parameter gets one place in the pass. A value that has to reach remotes the moment it changes is marked Req instead — the cycle then sends it at the next step rather than when its turn comes."),
                EditorStyles.miniLabel);

            var intervals = AsyncSyncBuilder.RefreshIntervals(request);
            // Read off the pass either way: for a setup carrying saved weights this is where
            // they show up, and for one laid out here it is one apiece.
            var visits = VisitCounts(ColumnSets(request));
            // Batching is why two rows can move as one, and until now nothing said so. The
            // slot number is shown whenever channels could group anything — a condition that
            // cannot change mid-draw, unlike "is anything actually grouped", which the Split
            // buttons below would flip and take the layout's control count with it. A grid
            // shows the grouping cell by cell, so neither has anything left to say there.
            var slots = AsyncSyncBuilder.BuildSlots(request);
            bool grouping = !Manual && (_floatChannels > 1 || _boolChannels > 1);
            var slotOfRow = new Dictionary<string, int>();
            for (int i = 0; i < slots.Count; i++)
                foreach (var name in slots[i].targets)
                    slotOfRow[name] = i;

            _groupNames.Clear();
            foreach (var row in _order)
                if (!string.IsNullOrEmpty(row.group) && !_groupNames.Contains(row.group))
                    _groupNames.Add(row.group);

            _reorder.Begin();
            foreach (var row in _order)
            {
                var rowRect = EditorGUILayout.BeginHorizontal();
                _reorder.DrawHandle();

                EditorGUILayout.LabelField(row.name + "  (" + row.type + ")");

                if (grouping)
                {
                    slotOfRow.TryGetValue(row.name, out int slot);
                    EditorGUILayout.LabelField(
                        new GUIContent("#" + (slot + 1),
                            L.Tr("The step this parameter rides in. Rows sharing a number share a step, are copied by one driver, and so are always sent together.")),
                        EditorStyles.miniLabel, GUILayout.Width(26));

                    bool splittable = row.type == AnimatorControllerParameterType.Float
                        ? _floatChannels > 1 : row.type == AnimatorControllerParameterType.Bool
                            && _boolChannels > 1;
                    EditorGUI.BeginDisabledGroup(!splittable);
                    bool split = GUILayout.Toggle(row.split && splittable,
                        new GUIContent(L.Tr("Split"),
                            L.Tr("Give this parameter a step of its own instead of sharing one with the row above. Parameters sharing a step are sent together and cannot be timed apart.")),
                        EditorStyles.miniButton, GUILayout.Width(40));
                    // Only a toggle the user could actually reach writes back. A disabled one
                    // hands back the value it was drawn with — the greyed-out false — and
                    // taking that for an edit would clear a mark the channel count is merely
                    // too narrow for at this moment, silently and with no click involved.
                    if (splittable && split != row.split)
                    {
                        row.split = split;
                        _stepsStale = true;
                    }
                    EditorGUI.EndDisabledGroup();
                }

                // How often the pass sends this row, as a reading. Blank at one place, which
                // is what every row of a setup laid out here has: a column of ×1 would be
                // noise, and the number is only ever interesting where a saved weight put it.
                visits.TryGetValue(row.name, out int times);
                EditorGUILayout.LabelField(
                    new GUIContent(times > 1 ? "×" + times : string.Empty,
                        L.Tr("Steps of the pass that send this parameter. A setup saved with per-target weights keeps them; new ones give every parameter one place and use Req for the values that cannot wait.")),
                    EditorStyles.miniLabel, GUILayout.Width(48));

                row.request = GUILayout.Toggle(row.request,
                    new GUIContent("Req",
                        L.Tr("Accept sync requests: a state's Sync Request (or anything setting the '{0}' flag) makes the cycle send this parameter at the next step instead of waiting a full pass. Costs no synced bits.",
                            AsyncSyncBuilder.RequestParameter(request.baseName, row.name))),
                    EditorStyles.miniButton, GUILayout.Width(36));

                DrawGroupPicker(row);

                string interval = intervals.TryGetValue(row.name, out float seconds)
                    ? L.Tr("every {0:0.##} s", seconds)
                    : "—";
                EditorGUILayout.LabelField(interval, EditorStyles.miniLabel, GUILayout.Width(90));

                EditorGUILayout.EndHorizontal();
                _reorder.Row(rowRect);
            }
            _reorder.End((from, to) =>
            {
                var moved = _order[from];
                _order.RemoveAt(from);
                _order.Insert(to, moved);
                // Batches fill in listed order, so reordering can regroup the slots.
                _stepsStale = true;
            });

            DrawCycleTimeline(request);
        }

        // ---- cycle timeline --------------------------------------------------

        const float TimelineRowHeight = 13f;
        const float TimelineLabelWidth = 116f;
        const float TimelineLabelGap = 4f;

        /// <summary>True while the setup carries a pass written out step by step rather than
        /// one derived from the weights — the two are the same switch, a grid being what
        /// overrides.</summary>
        bool Manual => _steps.Count > 0;

        /// <summary>How many steps of the pass send each target, by name.</summary>
        static Dictionary<string, int> VisitCounts(List<List<string>> columns)
        {
            var counts = new Dictionary<string, int>();
            foreach (var column in columns)
                foreach (var name in column)
                {
                    counts.TryGetValue(name, out int times);
                    counts[name] = times + 1;
                }
            return counts;
        }

        /// <summary>
        /// What each step of the pass sends, in order — the columns of the view below. A grid
        /// is read straight from <see cref="_steps"/> rather than through the slots: nothing
        /// else can draw a step that is empty or overfull, which is exactly the state a saved
        /// grid comes back in once the targets under it have moved.
        /// </summary>
        List<List<string>> ColumnSets(AsyncSyncBuilder.Request request)
        {
            var columns = new List<List<string>>();
            if (Manual)
            {
                foreach (var step in _steps)
                    columns.Add(AsyncSyncBuilder.NormalizeStep(request, step));
                return columns;
            }
            var slots = AsyncSyncBuilder.BuildSlots(request);
            foreach (var step in AsyncSyncBuilder.EffectiveSchedule(request, slots))
                columns.Add(slots[step].targets);
            return columns;
        }

        /// <summary>
        /// One pass as a small timeline: a row per parameter, a mark wherever the cycle sends
        /// it. Rates are a statement about spacing — that a ×2 slot comes round near the
        /// opposite ends of the pass — and spacing is the one thing a line of names can't
        /// show. Names stay in a readable column on the left rather than inside the marks,
        /// so the view survives long parameter names and 60-step passes alike.
        ///
        /// The same picture serves a pass the setup carries rather than derives, which is the
        /// one thing left that such a pass needs from this wizard: seeing what it does. Steps
        /// the decoder could not run are drawn in red where they are, and said in words by the
        /// blocking problem below.
        /// </summary>
        void DrawCycleTimeline(AsyncSyncBuilder.Request request)
        {
            var columns = ColumnSets(request);
            if (columns.Count < 2)
            {
                // Too little left to draw, and for a carried pass that would take the way back
                // to the weights down with it. Ask for a repair instead: one that finds nothing
                // schedulable returns empty, which is the way back.
                if (Manual) _stepsStale = true;
                return;
            }

            _timelineOpen = EditorGUILayout.Foldout(_timelineOpen,
                L.Tr("Cycle Timeline ({0} steps, {1:0.#} s)",
                    columns.Count, columns.Count * request.stepSeconds),
                true);
            if (!_timelineOpen) return;

            DrawTimingMode(request, columns);
            // Going back to the weights above rewrites the pass under this.
            columns = ColumnSets(request);
            if (columns.Count < 2) return;

            var flagged = Violations(request, columns);
            var intervals = AsyncSyncBuilder.RefreshIntervals(request);
            var mark = DaerDColors.SyncMark;
            var clash = DaerDColors.SyncClash;
            var flag = DaerDColors.Fade(clash, 0.22f);
            var track = DaerDColors.SyncTrack;

            foreach (var row in _order)
            {
                // One control per row whatever the width — the layout and repaint passes
                // must agree on how many there are.
                var line = EditorGUILayout.GetControlRect(false, TimelineRowHeight);
                var lane = new Rect(line.x + TimelineLabelWidth + TimelineLabelGap, line.y,
                    Mathf.Max(1f, line.width - TimelineLabelWidth - TimelineLabelGap), line.height);

                bool sent = false;
                foreach (var column in columns)
                    if (column.Contains(row.name)) { sent = true; break; }
                GUI.Label(new Rect(line.x, line.y, TimelineLabelWidth, line.height),
                    new GUIContent(row.name, RowTooltip(row, intervals, sent)),
                    TimelineLabelStyle(sent ? (Color?)null : clash));
                EditorGUI.DrawRect(lane, track);

                float cell = lane.width / columns.Count;
                for (int k = 0; k < columns.Count; k++)
                {
                    var box = new Rect(lane.x + k * cell, lane.y + 1f,
                        Mathf.Max(2f, cell - 1f), lane.height - 2f);
                    // The whole column is tinted, not just its marks: an empty step and two
                    // steps sending the same set are both invisible in the marks alone.
                    if (flagged[k]) EditorGUI.DrawRect(box, flag);
                    if (columns[k].Contains(row.name))
                        EditorGUI.DrawRect(box, flagged[k] ? clash : mark);
                }
            }

            DrawTimelineAxis(columns.Count * request.stepSeconds);
        }

        /// <summary>
        /// The steps to draw in red: one that sends nothing, one that sends more of a type
        /// than the channels carry, and one that repeats its neighbour (the wrap included)
        /// while no clock is paying for it. All of them are refused by Validate, which says so
        /// in words under the form; colouring says WHERE, which is the part a sentence cannot
        /// carry — and a carried pass can still come out of a saved setup like this after the
        /// targets under it have moved.
        /// </summary>
        bool[] Violations(AsyncSyncBuilder.Request request, List<List<string>> columns)
        {
            var flagged = new bool[columns.Count];
            var byName = DbtBuilder.ParametersByName(_controller);
            for (int k = 0; k < columns.Count; k++)
            {
                int next = (k + 1) % columns.Count;
                if (columns[k].Count == 0) flagged[k] = true;
                if (!request.allowRepeatSteps && columns.Count > 1
                    && SameSet(columns[k], columns[next]))
                    flagged[k] = flagged[next] = true;
                foreach (var name in columns[k])
                {
                    var parameter = byName.Find(name);
                    // Room "for one more" is measured against the rest of the step, so this
                    // reads as "does this member fit at all".
                    if (parameter == null || Fits(request, columns[k], name, parameter.type))
                        continue;
                    flagged[k] = true;
                    break;
                }
            }
            return flagged;
        }

        static bool Fits(AsyncSyncBuilder.Request request, List<string> column, string name,
            AnimatorControllerParameterType type)
        {
            var others = new List<string>(column);
            others.Remove(name);
            return AsyncSyncBuilder.StepHasRoom(request, others, type);
        }

        static bool SameSet(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var name in a)
                if (!b.Contains(name)) return false;
            return true;
        }

        /// <summary>
        /// What a setup that carries a hand-written pass is told, and the one thing it is
        /// offered: the way back to the weights.
        ///
        /// Writing a pass out by hand is no longer authored here. It answered "this value has
        /// to go out at a particular moment", which a sync request now answers without a pass
        /// to keep in step with the target list — and it answered it in a second vocabulary
        /// that every other part of the wizard then had to speak. What was built with it goes
        /// on building: the grid and the C# cycle are still loaded, still regenerated, still
        /// exported, and the timeline below still shows exactly what they do. Only the editing
        /// went away, which is why the way out has to stay — without it, the list above would
        /// sit there overridden by something nothing on screen can reach.
        /// </summary>
        void DrawTimingMode(AsyncSyncBuilder.Request request, List<List<string>> columns)
        {
            if (!Manual && _schedule.Count == 0) return;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                Manual
                    ? L.Tr("This setup carries a pass written out step by step. It is shown here and still built as it stands, but hand timing is no longer set up in this wizard — sync requests do what it was for.")
                    : L.Tr("This setup carries an explicit cycle written in C#. It is shown here and still built as it stands, but a cycle is no longer written in this wizard — sync requests do what it was for."),
                WrappedMiniLabel());
            if (GUILayout.Button(new GUIContent(L.Tr("Back To Cycle Order"),
                    L.Tr("Discard the pass this setup carries and lay the cycle out from the list above again. There is no way back to it from here.")),
                    EditorStyles.miniButton, GUILayout.Width(110)))
            {
                // The grid reaches the request through Snapshot and the cycle through
                // ApplyScheduleOverride, so one of these needs the copy and the other does not;
                // clearing both and snapshotting is the same answer for either.
                _steps.Clear();
                _schedule.Clear();
                Snapshot(request);
                // Dropping the pass changes which controls the rest of this draw makes, and
                // IMGUI counts those across the layout and repaint passes both.
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Both ends of the pass, so the marks have a scale to read against.</summary>
        static void DrawTimelineAxis(float cycleSeconds)
        {
            var line = EditorGUILayout.GetControlRect(false, 12f);
            var lane = new Rect(line.x + TimelineLabelWidth + TimelineLabelGap, line.y,
                Mathf.Max(1f, line.width - TimelineLabelWidth - TimelineLabelGap), line.height);
            GUI.Label(lane, "0 s", EditorStyles.miniLabel);
            var end = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperRight };
            GUI.Label(lane, L.Tr("{0:0.#} s ⟳", cycleSeconds), end);
        }

        static GUIStyle TimelineLabelStyle(Color? textColor = null)
        {
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                clipping = TextClipping.Clip,
            };
            if (textColor.HasValue) style.normal.textColor = textColor.Value;
            return style;
        }

        static GUIStyle WrappedMiniLabel() =>
            new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };

        static string RowTooltip(Row row, Dictionary<string, float> intervals, bool sent)
        {
            if (!sent)
                return L.Tr("'{0}' is never sent — no step of the pass carries it.", row.name);
            string tooltip = row.name;
            if (intervals.TryGetValue(row.name, out float seconds))
                tooltip += " — " + L.Tr("every {0:0.##} s", seconds);
            if (row.request) tooltip += "  " + L.Tr("(requestable)");
            return tooltip;
        }

        /// <summary>
        /// The reason applying would be refused, shown while the setup is being edited rather
        /// than only in a dialog once the button is pressed: a rejected target — a Trigger, a
        /// parameter animation writes — is something to see at the moment it is ticked.
        /// Silent until there is a cycle to speak of, so an empty form doesn't open in red.
        /// </summary>
        public void DrawBlockingProblem(AsyncSyncBuilder.Request request)
        {
            if (request == null || request.targets.Count < 2) return;
            var problem = AsyncSyncBuilder.Validate(request);
            if (problem != null)
                EditorGUILayout.HelpBox(problem, MessageType.Error);
        }

        public void DrawPreview(AsyncSyncBuilder.Request request)
        {
            if (request.targets.Count < 2) return;

            int direct = AsyncSyncBuilder.DirectBits(request);
            int compressed = AsyncSyncBuilder.CompressedBits(request);
            EditorGUILayout.LabelField(
                L.Tr("Synced cost: {0} bit (direct sync would be {1} bit)", compressed, direct),
                EditorStyles.miniLabel);

            var slots = AsyncSyncBuilder.BuildSlots(request);
            if (_encoding == AsyncSyncBuilder.IndexEncoding.Auto)
                EditorGUILayout.LabelField(
                    L.Tr("Auto picked the {0} index for {1} slots.",
                        AsyncSyncBuilder.ResolveEncoding(request), slots.Count),
                    EditorStyles.miniLabel);

            // The Bool index only grows at powers of two, so the tail of a range is free —
            // worth saying out loud, since it changes how many parameters to put in.
            int free = AsyncSyncBuilder.FreeSlots(request);
            if (free > 0)
                EditorGUILayout.LabelField(
                    L.Tr("{0} more slot(s) fit in the current index at no extra synced cost.", free),
                    EditorStyles.miniLabel);

            // What the clock is costing right now. It charges only for the slots that actually
            // repeat, so the number moves as the pass is edited — and reading 0 is the answer
            // to "is this option doing anything yet".
            if (request.allowRepeatSteps)
            {
                var clock = AsyncSyncBuilder.BuildClock(request, slots,
                    AsyncSyncBuilder.EffectiveSchedule(request, slots));
                int phased = 0;
                foreach (var phases in clock.slotPhases)
                    if (phases > 1) phased++;
                EditorGUILayout.LabelField(
                    L.Tr("Repeated steps: {0} of {1} slot(s) need a second decoder state.",
                        phased, slots.Count),
                    EditorStyles.miniLabel);
            }

            int requests = AsyncSyncBuilder.RequestableTargets(request).Count;
            if (requests > 0)
                EditorGUILayout.LabelField(
                    L.Tr("Sync requests: {0} local Bool flag(s) and one Int, nothing synced.", requests),
                    EditorStyles.miniLabel);

            if (request.ready)
                EditorGUILayout.LabelField(
                    L.Tr("Remote initialized flag: {0} local Bool(s) and one layer, nothing synced.",
                        slots.Count + 1),
                    EditorStyles.miniLabel);

            var groups = AsyncSyncBuilder.EffectiveGroups(request);
            if (groups.Count > 0)
            {
                int members = 0;
                foreach (var group in groups) members += group.members.Count;
                // Counted from the list that is actually generated rather than from a factor
                // written here: a member costs a latch, a shadow and a flag today, and the
                // last time that number changed this label went on saying the old one.
                EditorGUILayout.LabelField(
                    L.Tr("Groups: {0}, holding {1} parameter(s) — {2} local parameters and {0} layer(s), nothing synced.",
                        groups.Count, members, AsyncSyncBuilder.GroupParameters(request).Count),
                    EditorStyles.miniLabel);
            }

            if (request.stale)
            {
                var clock = AsyncSyncBuilder.BuildClock(request, slots,
                    AsyncSyncBuilder.EffectiveSchedule(request, slots));
                EditorGUILayout.LabelField(
                    clock.markerSlot < 0
                        ? L.Tr("Drift suspicion flag: every step sends the same slot, so there is no lap to judge.")
                        : clock.markerDedicated
                            ? L.Tr("Drift suspicion flag: judged once a pass, on a marker of its own — one more index value ({0} in all).",
                                AsyncSyncBuilder.IndexValues(request))
                            : L.Tr("Drift suspicion flag: judged once a pass, when '{0}' comes round.",
                                slots[clock.markerSlot].targets[0]),
                    EditorStyles.miniLabel);
            }

            // The pass actually being built, not the one the rates would lay out — the seconds
            // beside it come from the same place, and the two disagreeing read as a bug.
            int steps = AsyncSyncBuilder.EffectiveSchedule(request, slots).Count;
            EditorGUILayout.LabelField(
                L.Tr("One full pass: {0:0.#} s ({1} steps × {2:0.##} s)",
                    AsyncSyncBuilder.CycleSeconds(request), steps, _stepSeconds),
                EditorStyles.miniLabel);

            // What requests cost, as a number rather than as advice to use them sparingly. A
            // detour spends a step and hands the ring its place back, and detours can't chain,
            // so this is the ceiling however often the flags are raised.
            if (requests > 0)
                EditorGUILayout.LabelField(
                    L.Tr("With requests running: up to {0:0.#} s per pass.",
                        AsyncSyncBuilder.WorstCycleSeconds(request)),
                    EditorStyles.miniLabel);
        }

        /// <summary>
        /// The button behind the split-by-type advice, drawn only while the advice is up:
        /// pressing it builds one setup per type, in one undo step. The warning says what it
        /// would cost and gain; this is the doing of it, because the alternative is a
        /// paragraph telling someone to untick two thirds of their parameters, apply, and then
        /// do the whole thing again twice.
        ///
        /// Returns true when the controller was rewritten, which the host reads as "everything
        /// you were showing is now a setup ago" — the wizard closes on it, and the panel
        /// rebinds.
        /// </summary>
        public bool DrawSplitProposal(AsyncSyncBuilder.Request request,
            List<AsyncSyncBuilder.Request> byType = null)
        {
            if (request == null || request.targets.Count < 2) return false;
            var split = byType ?? AsyncSyncSplit.ByType(request);
            if (split.Count < 2) return false;

            bool applied = false;
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent(L.Tr("Split By Type"),
                    L.Tr("Build one setup per parameter type instead of this one: each type gets its own index, its own channels and its own pass. This layer is regenerated as the first type's ring and the others are added beside it — a split cannot be undone by pressing anything, so read the numbers above first.")),
                    EditorStyles.miniButton, GUILayout.Width(120)))
            {
                applied = AsyncSyncSplit.Apply(split);
                if (!applied)
                    EditorUtility.DisplayDialog(L.Tr("Async Sync"),
                        L.Tr("The split could not be built and nothing was changed."), "OK");
            }
            EditorGUILayout.EndHorizontal();
            return applied;
        }

        /// <summary>
        /// The fix for the "targets are still synced in the store" warning: multiplexing only
        /// saves bits once the targets stop syncing directly, and that lives in the store, not
        /// in the controller. Drawn right under the warnings, and only while there is
        /// something to fix — no store, no store writes, or nothing synced draws nothing.
        /// </summary>
        public void DrawStoreFix(AsyncSyncBuilder.Request request)
        {
            if (request == null || request.store == null || !request.addToStore
                || request.targets == null)
                return;
            // Runs every repaint, so it stops at the first hit — same reading as the warning.
            bool anySynced = false;
            foreach (var name in request.targets)
            {
                var entry = request.store.Find(name);
                if (entry == null || !entry.synced) continue;
                anySynced = true;
                break;
            }
            if (!anySynced) return;

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent(L.Tr("Unsync Targets In Store"),
                    L.Tr("Clear the synced flag on the multiplexed targets in the parameter store. Their values travel through the generated channels instead, which is the whole point of the setup.")),
                    EditorStyles.miniButton, GUILayout.Width(190)))
            {
                using (new UndoScope("Unsync Async Sync Targets"))
                    request.store.SetSynced(request.targets, false);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}

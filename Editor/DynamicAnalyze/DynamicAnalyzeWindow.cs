using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// DD DynamicAnalyze's front door, in two moods.
    ///
    /// RUN computes an experiment whole from settings that are all data — a clock, a list of
    /// timed inputs, a wire — and the transport then walks a cursor along the finished result.
    /// Repeatable, comparable, and the only mood a seed means anything in.
    ///
    /// LIVE keeps the same clients open and steps them on the editor's own update, so a value
    /// changed here takes effect on the next frame and the waveform grows under it. Nothing
    /// about a controller is learned faster than by pushing on it and watching, and no amount
    /// of writing the pushes down in advance replaces having done that once.
    ///
    /// REC simulates nothing at all: it watches the avatar somebody is actually wearing in Play
    /// mode and writes down what it did. The other two moods answer "what would this controller
    /// do"; this one answers "what did it do", which is the question a bug report is made of.
    /// Nothing here can be poked — the avatar belongs to whoever is wearing it.
    ///
    /// All three produce the same trace, and the same viewer reads it.
    /// </summary>
    sealed class DynamicAnalyzeWindow : EditorWindow
    {
        [MenuItem("YozoLab/DD DynamicAnalyze")]
        public static void Open()
        {
            var window = GetWindow<DynamicAnalyzeWindow>();
            window.titleContent = new GUIContent(L.Tr("DD DynamicAnalyze"));
            window.minSize = new Vector2(620f, 360f);
            window.Show();
        }

        /// <summary>One line of the Timed inputs list — written by hand, loaded from a clip, or
        /// taken down from a live session. Internal only so the rule about which live writes
        /// become one can be tested; nothing outside this window edits the list.</summary>
        [System.Serializable]
        internal sealed class Poke
        {
            public float at;
            public string scope = string.Empty;
            public string parameter = string.Empty;
            public float value;
        }

        [SerializeField] AnimatorController _controller;
        [SerializeField] float _fps = 60f;
        [SerializeField] float _seconds = 10f;
        [SerializeField] float _jitter;
        [SerializeField] int _seed = 1;
        [SerializeField] bool _twoClients = true;
        [SerializeField] float _interval = 0.2f;
        /// <summary>Zero, and a saved layout that predates the field reads as zero too — which
        /// is the same run it was, because a delivery with no latency lands on the frame its
        /// sample went out on.</summary>
        [SerializeField] float _latency;
        [SerializeField] float _dropChance;
        /// <summary>Whether the wire rolls its losses from a seed of its own. Off is what this
        /// window has always done — the clock's seed handed to both — so a layout saved before
        /// this existed goes on producing the run it produced. The wire has always HAD its own
        /// seed; until now there was no way to reach it from here.</summary>
        [SerializeField] bool _ownWireSeed;
        [SerializeField] int _wireSeed = 1;
        [SerializeField] float _joinsAt;
        /// <summary>How many other people are in the instance. One is the run this window has
        /// always done; the rest turn up at times of their own.</summary>
        [SerializeField] int _remotes = 1;
        /// <summary>When everybody after the first arrives. Kept apart from
        /// <see cref="_joinsAt"/> so a saved window layout from before this existed still opens
        /// as the one-remote run it was.</summary>
        [SerializeField] List<float> _laterJoins = new List<float>();
        [SerializeField] bool _quantize = true;
        [SerializeField] bool _lagRows = true;
        [SerializeField] List<string> _synced = new List<string>();
        [SerializeField] List<Poke> _pokes = new List<Poke>();
        /// <summary>
        /// Which mood the window is in, as the two booleans that keep a saved layout meaning
        /// what it meant. <see cref="_live"/> is the flag this window has always had, and a
        /// layout saved before Rec existed carries it alone — so it goes on being read alone,
        /// and such a window opens in exactly the mood it was closed in.
        ///
        /// An enum would have been tidier and would have made every old layout open in whatever
        /// mood happened to be numbered the same as the old <c>false</c>. The invariant instead
        /// is that at most one of the two is up; <see cref="SwitchTo"/> is the only thing that
        /// sets either, and it sets both.
        /// </summary>
        [SerializeField] bool _live;
        [SerializeField] bool _rec;
        /// <summary>The Animator being recorded. A scene reference, so entering Play mode —
        /// which builds the scene again — leaves it pointing at nothing; that is what the
        /// candidate list and the arm toggle are for, rather than a bug to be fixed.</summary>
        [SerializeField] Animator _target;
        /// <summary>Whether to start recording as soon as an avatar running this controller
        /// turns up in Play mode. Serialized on purpose: entering Play mode reloads the domain,
        /// so anything that is to survive being told before the fact has to be.</summary>
        [SerializeField] bool _armed;
        /// <summary>Whether the other people's copies of the avatar go into the same recording.
        /// On, because a copy beside the wearer is the comparison the whole module is about and
        /// a recording taken without one cannot be given one afterwards. A layout saved before
        /// this existed opens with it on — the change that makes to such a window is a second
        /// scope in a recording, on an avatar that has copies, which is what somebody who set
        /// those copies up was after.</summary>
        [SerializeField] bool _recordClones = true;
        [SerializeField] bool _settingsOpen = true;
        [SerializeField] bool _inputsOpen = true;
        [SerializeField] bool _notesOpen = true;
        [SerializeField] bool _findingsOpen = true;
        /// <summary>Whether the list of what travels is showing its names. Folded by default —
        /// the count above it is the answer most of the time, and a real avatar's list is
        /// longer than everything else on the panel put together.</summary>
        [SerializeField] bool _syncedOpen;

        readonly WaveformView _view = new WaveformView();
        SimSession _session;

        // The recording, and whether it is still being written to. Not serialized, and it does
        // not need to be: entering Play mode reloads the domain and takes these with it, which
        // costs nothing because a recording only ever starts AFTER the enter. Leaving Play mode
        // reloads nothing, so a finished recording is still here to be read, saved and compared
        // — which is the whole reason the mood is worth having.
        PlayRecorder _recorder;
        bool _recording;

        // SimNotes.For walks every layer and opens a SerializedObject per driver, and OnGUI
        // asks several times a frame — on a real avatar's FX that is a steady cost for an
        // answer that only changes when the controller does. Re-read when the controller field
        // changes, when the window regains focus (the edit happened elsewhere), and on every
        // Run/Restart (the moment the answer is about to be trusted).
        List<string> _notes;
        AnimatorController _notesFor;
        bool _notesWithRemote = true;

        // What the avatar's own store says travels, read the same way and for the same reason:
        // it opens an asset and walks it, and the panel asks on every repaint whether the list
        // in hand still matches. Dropped where _notes is dropped — the store is edited in
        // another window, so regaining focus is the moment to look again.
        List<string> _stored;
        AnimatorController _storedFor;
        bool _storedRead;
        GUIStyle _warningStyle;

        // The other list, read off the finished trace instead of off the controller. It cannot
        // go stale on its own — a batch run's trace never changes once it exists — so it is
        // dropped where the trace is replaced rather than watched for. _ranWith is the
        // experiment that produced it, kept because the settings fields go on being edited
        // after a run and a finding about what was synced has to be about what WAS synced.
        // It is also what a Save writes into the clip, which is why a live session sets it too
        // even though findings never speak there. Null only when the trace in hand came from
        // somewhere that did not say — a clip saved before settings travelled.
        List<string> _findings;
        SimSettings _ranWith;

        /// <summary>The mood that computes an experiment whole — neither of the two that watch
        /// something. Spelt as a question rather than a third flag so there is nothing extra to
        /// keep in step: the timed inputs, the settings panel and the input list are all things
        /// only a batch run has.</summary>
        bool Batch => !_live && !_rec;

        bool _playing;
        bool _follow = true;
        double _lastTick;
        float _speed = 1f;
        static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f };
        static readonly string[] SpeedLabels = { "0.25x", "0.5x", "1x", "2x", "4x" };

        void OnEnable()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            if (_controller == null) _controller = Selection.activeObject as AnimatorController;
        }

        void OnFocus()
        {
            _notes = null;
            _storedRead = false;
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            // Before a domain reload rather than after: the clients own hidden GameObjects, and
            // one that outlives the C# holding it is a leak nothing can reach to clean up.
            DropSession();
        }

        void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            float elapsed = (float)(now - _lastTick);
            _lastTick = now;
            if (Watch()) return;
            if (!_playing) return;

            if (_live)
            {
                if (_session == null) { _playing = false; return; }
                if (_session.Advance(elapsed * _speed) == 0) return;
                _view.trace = _session.Trace;
                // Something quiet may have just moved, and the list is built from what has.
                _view.Invalidate();
                if (_follow) _view.cursorFrame = _view.Frames - 1;
                Repaint();
                return;
            }

            if (_view.trace == null || _view.Frames == 0) { _playing = false; return; }
            float target = _view.trace.TimeAt(_view.cursorFrame) + elapsed * _speed;
            int frame = _view.trace.FrameAt(target);
            if (frame <= _view.cursorFrame) frame = _view.cursorFrame + 1;
            if (frame >= _view.Frames)
            {
                _view.cursorFrame = _view.Frames - 1;
                _playing = false;
            }
            else _view.cursorFrame = frame;
            Repaint();
        }

        void OnGUI()
        {
            // A live row's value cell IS the way to poke it: the row already says whose value
            // it is and already shows what it did, so a panel repeating the same parameters
            // somewhere else was one list too many.
            //
            // Which is why a layer's weight is offered the same way rather than as a control of
            // its own: it is a row, so the cell beside it is where a reader already looks. Not
            // every weight row takes one — see SimSession.CanSetWeight.
            _view.editable = _live && _session != null ? (System.Func<SignalTrace.Signal, bool>)
                (signal => signal.kind != SignalKind.State
                    && Simulation.IsClient(signal.scope)
                    && (_session.Has(signal.name)
                        || _session.CanSetWeight(signal.scope, signal.name)))
                : null;
            _view.write = (signal, value) =>
            {
                if (_session == null) return;
                _session.Write(signal.scope, signal.name, value);
                Record(_pokes, _session, signal, value);
            };

            DrawToolbar();
            if (_settingsOpen)
            {
                if (_rec) DrawRec();
                else DrawSettings();
            }
            // Nothing SimNotes says applies to a recording: those are the things a SIMULATION
            // of this controller cannot promise, and a recording simulates nothing — Mecanim is
            // Mecanim and VRChat's behaviours are really running.
            if (!_rec) DrawNotes();
            DrawFindings();
            if (_inputsOpen && Batch) DrawStimulus();
            var rect = GUILayoutUtility.GetRect(0f, 100000f, 0f, 100000f);
            _view.Draw(rect);
            if (GUI.changed) Repaint();
        }

        // ---- toolbar --------------------------------------------------------

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            bool live = GUILayout.Toggle(_live, L.Tr("Live"), EditorStyles.toolbarButton,
                GUILayout.Width(44f));
            if (live != _live) SwitchTo(live, false);
            bool rec = GUILayout.Toggle(_rec, L.Tr("Rec"), EditorStyles.toolbarButton,
                GUILayout.Width(40f));
            if (rec != _rec) SwitchTo(false, rec);

            if (_rec)
            {
                // Only while something is running: there is no graph to find outside Play mode,
                // and a button that started a recording of nothing would be a button that lies.
                EditorGUI.BeginDisabledGroup(!EditorApplication.isPlaying);
                if (GUILayout.Button(_recording ? L.Tr("Stop") : L.Tr("Record"),
                        EditorStyles.toolbarButton, GUILayout.Width(56f)))
                {
                    if (_recording) StopRecording();
                    else StartRecording(false);
                }
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                EditorGUI.BeginDisabledGroup(_controller == null);
                if (GUILayout.Button(_live ? L.Tr("Restart") : L.Tr("Run"),
                        EditorStyles.toolbarButton, GUILayout.Width(56f)))
                {
                    if (_live) StartSession();
                    else RunNow();
                }
                EditorGUI.EndDisabledGroup();
            }

            bool has = _view.trace != null && _view.Frames > 0;
            // A recording that is still being written to is not something to scrub: the newest
            // frame moves under the cursor, and Follow is the only control that means anything.
            EditorGUI.BeginDisabledGroup((!has && !_live) || _recording);

            if (!_live && GUILayout.Button("|<", EditorStyles.toolbarButton, GUILayout.Width(26f)))
            { _view.cursorFrame = 0; _playing = false; }
            if (!_live && GUILayout.Button("<", EditorStyles.toolbarButton, GUILayout.Width(24f)))
            { _view.cursorFrame = Mathf.Max(0, _view.cursorFrame - 1); _playing = false; }

            if (GUILayout.Toggle(_playing, _playing ? L.Tr("Pause") : L.Tr("Play"),
                    EditorStyles.toolbarButton, GUILayout.Width(52f)) != _playing)
            {
                _playing = !_playing;
                _lastTick = EditorApplication.timeSinceStartup;
                if (_playing && _live && _session == null) StartSession();
                if (_playing && !_live && _view.cursorFrame >= _view.Frames - 1)
                    _view.cursorFrame = 0;
            }

            if (GUILayout.Button(">|", EditorStyles.toolbarButton, GUILayout.Width(26f)))
            {
                if (_live)
                {
                    // One frame of simulation rather than one frame of scrubbing: in a live
                    // session the newest frame does not exist until something makes it.
                    if (_session == null) StartSession();
                    _session.StepOnce();
                    _view.trace = _session.Trace;
                    _view.Invalidate();
                    if (_follow) _view.cursorFrame = _view.Frames - 1;
                }
                else _view.cursorFrame = Mathf.Max(0, _view.Frames - 1);
                _playing = false;
            }

            int speed = Mathf.Max(0, System.Array.IndexOf(Speeds, _speed));
            speed = EditorGUILayout.Popup(speed, SpeedLabels, EditorStyles.toolbarPopup,
                GUILayout.Width(50f));
            _speed = Speeds[speed];

            if (_live || _recording)
            {
                // Outside the disabled group above: while a recording runs this is the one
                // thing worth reaching for.
                EditorGUI.EndDisabledGroup();
                _follow = GUILayout.Toggle(_follow, L.Tr("Follow"), EditorStyles.toolbarButton,
                    GUILayout.Width(52f));
                EditorGUI.BeginDisabledGroup((!has && !_live) || _recording);
            }

            GUILayout.Space(6f);
            if (has)
                GUILayout.Label(L.Tr("frame {0} / {1}   {2:0.###} s",
                        _view.cursorFrame, _view.Frames - 1,
                        _view.trace.TimeAt(_view.cursorFrame)),
                    EditorStyles.miniLabel);
            // The measurement, beside the moment it is measured from. Only while there is a
            // mark: a Δ of nothing reading 0 s would look like an answer.
            if (has && _view.HasMark)
                GUILayout.Label(L.Tr("Δ {0:0.###} s", _view.Span()), EditorStyles.miniLabel);
            // And how far the picked-out row moved over that time, beside how long it took.
            // The pair is what a duration is usually wanted for — how fast did this ramp, how
            // much did the wire round off — and both halves of it were being read off the plot
            // by eye. Named, because a number with no row beside it belongs to whichever row
            // the reader last thought about.
            string moved = has ? _view.ValueDeltaText() : null;
            if (moved != null)
                GUILayout.Label(L.Tr("Δ {0} {1}", _view.SelectedName, moved),
                    EditorStyles.miniLabel);
            EditorGUI.EndDisabledGroup();

            GUILayout.FlexibleSpace();
            _view.filter = EditorGUILayout.TextField(_view.filter,
                EditorStyles.toolbarSearchField, GUILayout.Width(140f));
            if (GUILayout.Button(L.Tr("Fit"), EditorStyles.toolbarButton, GUILayout.Width(32f)))
                _view.Fit(position.width);
            _view.movedOnly = GUILayout.Toggle(_view.movedOnly, L.Tr("Moved"),
                EditorStyles.toolbarButton, GUILayout.Width(52f));
            DrawClipMenu(has);
            if (Batch)
                _inputsOpen = GUILayout.Toggle(_inputsOpen, L.Tr("Timed"),
                    EditorStyles.toolbarButton, GUILayout.Width(52f));
            _settingsOpen = GUILayout.Toggle(_settingsOpen, L.Tr("Settings"),
                EditorStyles.toolbarButton, GUILayout.Width(60f));
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// A run is a set of values over time, which is what an AnimationClip is, so that is
        /// what it saves as: openable in the Animation window, diffable, and — the point —
        /// loadable again either as a run to look at or as the input to the next one.
        /// </summary>
        void DrawClipMenu(bool has)
        {
            if (!GUILayout.Button(L.Tr("Clip"), EditorStyles.toolbarDropDown, GUILayout.Width(48f)))
                return;
            var menu = new GenericMenu();
            if (has)
                menu.AddItem(new GUIContent(L.Tr("Save Run…")), false, SaveClip);
            else menu.AddDisabledItem(new GUIContent(L.Tr("Save Run…")));
            menu.AddItem(new GUIContent(L.Tr("Open Run…")), false, OpenClip);
            menu.AddItem(new GUIContent(L.Tr("Load As Timed Inputs…")), false, LoadAsInputs);
            menu.AddSeparator(string.Empty);
            // A saved run laid under the one in hand. "It worked yesterday" and "did that
            // setting change anything" are both questions about two runs, and a viewer that
            // can only hold one answers neither.
            menu.AddItem(new GUIContent(L.Tr("Compare With…")), false, CompareWith);
            // And the same question without a file in it. "Did that setting change anything"
            // is asked far more often about the run on screen than about one on disk, and
            // going through Save Run… and Compare With… to ask it made a folder full of runs
            // nobody wanted to keep. A batch run REPLACES the trace rather than growing it, so
            // holding the reference is holding a snapshot.
            //
            // Never in a live session, which is the one mood where that is not true: there the
            // same object goes on being appended to, so the ghost would be the run itself and
            // the comparison would be of a thing with itself. A recording is a snapshot the
            // moment it stops, and not one before that, for exactly the same reason.
            if (has && !_live && !_recording)
                menu.AddItem(new GUIContent(L.Tr("Compare With This Run")), false,
                    () => _view.ghost = _view.trace);
            else menu.AddDisabledItem(new GUIContent(L.Tr("Compare With This Run")));
            if (_view.ghost != null)
                menu.AddItem(new GUIContent(L.Tr("Stop Comparing")), false,
                    () => _view.ghost = null);
            else menu.AddDisabledItem(new GUIContent(L.Tr("Stop Comparing")));
            menu.ShowAsContext();
        }

        void CompareWith()
        {
            var clip = PickClip(L.Tr("Compare With"));
            if (clip == null) return;
            _view.ghost = TraceClip.Load(clip);
            Repaint();
        }

        void SaveClip()
        {
            string path = EditorUtility.SaveFilePanelInProject(L.Tr("Save Run"),
                "DD Run", "anim", L.Tr("Where to keep this run."));
            if (string.IsNullOrEmpty(path)) return;
            // The experiment that produced this trace, not the fields as they stand: the panel
            // goes on being edited after a run, and settings saved beside a result they did not
            // produce would be a file that lies in the one way this one exists to prevent.
            TraceClip.Save(_view.trace, path, _ranWith);
        }

        void OpenClip()
        {
            var clip = PickClip(L.Tr("Open Run"));
            if (clip == null) return;
            _playing = false;
            SwitchTo(false, false);
            // The clip's own settings if it carries any, and then the findings that need to
            // know what the wire was can speak about it. Without them the window keeps the
            // settings it had and _ranWith stays null: the findings a trace answers on its own
            // still appear, and a finding about a wire that did not carry this run would be a
            // confident lie.
            _findings = null;
            _ranWith = TraceClip.SettingsOf(clip);
            if (_ranWith != null) Restore(_ranWith);
            _view.trace = TraceClip.Load(clip);
            _view.cursorFrame = 0;
            _view.Invalidate();
            _view.Fit(position.width);
        }

        /// <summary>
        /// The experiment a saved run was made with, back into the fields that make one.
        ///
        /// Quietly, and without offering not to. "The settings of the run I just opened are now
        /// in the form" is the expectation rather than the surprise — a reader opens yesterday's
        /// result to ask what it was, and the answer being one dialog away from the question is
        /// only a dialog. The cost is that unsaved settings in the form are lost, which is the
        /// same cost every Open has ever had.
        ///
        /// The timed inputs go with it: they are the experiment, not a preference. What a clip
        /// without a wire does NOT do is clear the wire fields — there is nothing to restore
        /// them to, the panel hides them anyway, and blanking a form on a run that had no
        /// opinion about it would lose settings for no reason.
        /// </summary>
        void Restore(SimSettings settings)
        {
            var clock = settings.clock ?? new SimClock();
            _fps = clock.fps;
            _seconds = clock.seconds;
            _jitter = clock.jitter;
            _seed = clock.seed;
            _lagRows = settings.lagRows;

            _pokes.Clear();
            if (settings.stimulus != null)
                foreach (var entry in settings.stimulus.InOrder())
                    _pokes.Add(new Poke
                    {
                        at = entry.atSeconds,
                        scope = entry.scope,
                        parameter = entry.parameter,
                        value = entry.value,
                    });

            var wire = settings.wire;
            _twoClients = wire != null;
            if (wire == null) return;
            _interval = wire.intervalSeconds;
            _latency = wire.latencySeconds;
            _dropChance = wire.dropChance;
            _quantize = wire.quantize;
            _joinsAt = wire.remoteJoinsAt;
            // The tick box is worked out from the number rather than saved beside it: a wire
            // seeded the same as the clock IS the unticked run, so a run restored this way
            // reproduces itself either way. What it cannot restore is a number typed into the
            // box and then unticked — which changed nothing, and is not part of the run.
            _ownWireSeed = wire.seed != clock.seed;
            _wireSeed = wire.seed;
            _remotes = wire.Remotes;
            _laterJoins.Clear();
            _laterJoins.AddRange(wire.laterJoins);
            _synced.Clear();
            _synced.AddRange(wire.parameters);
        }

        /// <summary>The other direction: what one run recorded becomes what the next one is
        /// told to do.</summary>
        void LoadAsInputs()
        {
            var clip = PickClip(L.Tr("Load As Timed Inputs"));
            if (clip == null) return;
            var stimulus = TraceClip.ToStimulus(clip, string.Empty, ParameterNames());
            _pokes.Clear();
            foreach (var entry in stimulus.InOrder())
                _pokes.Add(new Poke
                {
                    at = entry.atSeconds,
                    parameter = entry.parameter,
                    value = entry.value,
                    scope = entry.scope,
                });
            _inputsOpen = true;
        }

        static AnimationClip PickClip(string title)
        {
            string path = EditorUtility.OpenFilePanel(title, "Assets", "anim");
            if (string.IsNullOrEmpty(path)) return null;
            string relative = FileUtil.GetProjectRelativePath(path);
            return AssetDatabase.LoadAssetAtPath<AnimationClip>(
                string.IsNullOrEmpty(relative) ? path : relative);
        }

        /// <summary>
        /// What this run cannot promise about this controller, above the result rather than
        /// buried under it. A simulator that is silently wrong is worth less than none, so the
        /// places it is known to part company with a headset are stated where the answer is
        /// read — and the count stays visible even when the list is folded away.
        /// </summary>
        void DrawNotes()
        {
            if (_notes == null || _notesFor != _controller || _notesWithRemote != _twoClients)
            {
                _notes = SimNotes.For(_controller, _twoClients);
                _notesFor = _controller;
                _notesWithRemote = _twoClients;
            }
            var notes = _notes;
            if (notes.Count == 0) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _notesOpen = EditorGUILayout.Foldout(_notesOpen,
                L.Tr("What this run does not promise ({0})", notes.Count), true);
            if (_notesOpen)
                foreach (var note in notes)
                    EditorGUILayout.LabelField("• " + note, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// And what it did find, directly under what it does not promise: the two are one
        /// thought, and a reader who has just been told what a run cannot say is the reader
        /// ready to be told what this one did. Its own frame so either can be folded away —
        /// the notes are about the controller and stay put, the findings change with every run.
        ///
        /// Never in a live session. A live trace is trimmed to the history the window keeps, so
        /// every finding here that begins with "never" would be a claim about the last few
        /// seconds dressed as a claim about the whole run — and a finding that is quietly about
        /// less than it says is worse than no finding at all.
        /// </summary>
        void DrawFindings()
        {
            // And not while a recording is being written either: the trace grows under the
            // reader, so every finding beginning with "never" would be about however much of it
            // had arrived when the panel last drew itself.
            if (_live || _recording || _view.trace == null || _view.Frames == 0) return;
            if (_findings == null) _findings = RunFindings.For(_view.trace, _ranWith);
            if (_findings.Count == 0) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _findingsOpen = EditorGUILayout.Foldout(_findingsOpen,
                L.Tr("What this run found ({0})", _findings.Count), true);
            if (_findingsOpen)
                foreach (var finding in _findings)
                    EditorGUILayout.LabelField("• " + finding, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        // ---- settings -------------------------------------------------------

        void DrawSettings()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.BeginChangeCheck();
            _controller = (AnimatorController)EditorGUILayout.ObjectField(
                L.Tr("Controller"), _controller, typeof(AnimatorController), false);

            EditorGUILayout.BeginHorizontal();
            _fps = EditorGUILayout.FloatField(new GUIContent(L.Tr("FPS"),
                L.Tr("Frames per simulated second. The frame count is this times the length; jitter varies how long each one is, not how many there are.")), _fps);
            _seconds = EditorGUILayout.FloatField(new GUIContent(L.Tr("Seconds"),
                L.Tr("How long a run covers. In a live session it is how much history the window keeps.")), _seconds);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _jitter = EditorGUILayout.Slider(new GUIContent(L.Tr("Jitter"),
                L.Tr("How far a frame may stray from its nominal length, as a fraction of it. A controller that only works at exactly this frame rate works nowhere.")),
                _jitter, 0f, SimClock.MaximumJitter);
            _seed = EditorGUILayout.IntField(new GUIContent(L.Tr("Seed"),
                L.Tr("Fixes the noise. Same settings and same seed are the same run; a new seed is the same question asked again.")), _seed);
            EditorGUILayout.EndHorizontal();

            _twoClients = EditorGUILayout.Toggle(new GUIContent(L.Tr("Wearer And A Remote"),
                L.Tr("Run two copies of the avatar off one clock, with only the synced parameters crossing between them. Off runs one, which answers questions about the Animator rather than about VRChat.")),
                _twoClients);
            if (_twoClients)
            {
                EditorGUILayout.BeginHorizontal();
                _interval = EditorGUILayout.FloatField(new GUIContent(L.Tr("Sync Every (s)"),
                    L.Tr("Seconds between samples. The wearer's synced values are read whole and handed over together, so a change that comes and goes inside one interval never leaves them.")), _interval);
                _latency = EditorGUILayout.FloatField(new GUIContent(L.Tr("Latency (s)"),
                    L.Tr("How long a sample spends on its way. The wearer's values are read when the sample goes and land this much later, so the other person acts on what was true when they were read. Zero means deliveries land the way they have always landed here — not a claim that a real trip takes no time.")), _latency);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                _dropChance = EditorGUILayout.Slider(L.Tr("Loss"), _dropChance, 0f, 1f);
                _ownWireSeed = EditorGUILayout.Toggle(new GUIContent(L.Tr("Own Loss Seed"),
                    L.Tr("Roll the lost samples from a seed of the wire's own instead of the clock's. Off is what this window has always done. On lets the frame timing be asked a new question without reshuffling which samples go missing, and the other way round.")), _ownWireSeed);
                EditorGUILayout.EndHorizontal();
                if (_ownWireSeed)
                    _wireSeed = EditorGUILayout.IntField(new GUIContent(L.Tr("Loss Seed"),
                        L.Tr("Fixes which samples are lost, and nothing else. Same wire and same seed lose the same samples however the clock is changed.")), _wireSeed);

                DrawRemotes();

                EditorGUILayout.BeginHorizontal();
                _quantize = EditorGUILayout.Toggle(new GUIContent(L.Tr("Round Like The Wire"),
                    L.Tr("Floats to 8 bits across -1..1, Ints to a byte, Bools to a bit. On, because that is what a remote actually holds.")), _quantize);
                _lagRows = EditorGUILayout.Toggle(new GUIContent(L.Tr("Remote Lag Rows"),
                    L.Tr("A row per parameter saying how long the other person has been looking at a different value. For a multiplexed target that is the age of their copy — the remote view, as a number.")), _lagRows);
                EditorGUILayout.EndHorizontal();

                DrawSynced();
            }
            // A live session was built from these; changing one has to rebuild it or the
            // window would be showing a run nobody asked for.
            if (EditorGUI.EndChangeCheck() && _live) DropSession();
            EditorGUILayout.EndVertical();
        }

        /// <summary>How many other people this window will run without saying anything about it.
        /// One remote is one more real Animator stepping a real controller, so the cost is
        /// linear and visible; past this the run is warned about rather than refused, because
        /// the number that finds a bug is not for this window to decide.</summary>
        const int ComfortableRemotes = 4;

        /// <summary>
        /// The other people, and when each of them turned up. One remote is drawn exactly as it
        /// always was — one field, the same label — because that is still what most runs are and
        /// a list of one is a worse way to say it.
        /// </summary>
        void DrawRemotes()
        {
            _remotes = Mathf.Max(1, EditorGUILayout.IntField(new GUIContent(L.Tr("Remotes"),
                L.Tr("How many other people are in the instance. Each one is another copy of the avatar running a real Animator, so the run costs about that much more — and each of them arrives at a time of their own, which is where the failures that only happen to the second person live.")),
                _remotes));
            while (_laterJoins.Count < _remotes - 1) _laterJoins.Add(0f);
            if (_laterJoins.Count > _remotes - 1)
                _laterJoins.RemoveRange(_remotes - 1, _laterJoins.Count - (_remotes - 1));
            if (_remotes > ComfortableRemotes)
                EditorGUILayout.HelpBox(
                    L.Tr("{0} remotes is {0} more Animators stepped every frame. Nothing stops you; it will simply take about that much longer.", _remotes),
                    MessageType.Info);

            _joinsAt = EditorGUILayout.FloatField(new GUIContent(L.Tr("Remote Joins At (s)"),
                L.Tr("When the other person turns up. Zero is everybody loading together, which is the one case that stops happening after the first minute of an instance. Somebody who arrives later is handed every synced value at once and has to work the rest out from there — which is what the flags about being caught up are for.")),
                _joinsAt);
            for (int i = 0; i < _laterJoins.Count; i++)
                _laterJoins[i] = EditorGUILayout.FloatField(
                    new GUIContent(L.Tr("Remote {0} Joins At (s)", i + 2),
                        L.Tr("When this one turns up. Somebody who walks in while a cycle is already running is the case a single remote can only ever ask about at the very first frame.")),
                    _laterJoins[i]);
        }

        // ---- what travels ---------------------------------------------------

        /// <summary>
        /// The list of what crosses the wire, by name.
        ///
        /// It used to be a count, which is the right answer to "is this set up" and no answer
        /// at all to "is it set up right" — and the second question is the one asked, because
        /// every difference a two-client run shows comes out of this list. A count cannot say
        /// that a name is spelled the way the controller spelled it last month, and a list
        /// whose entries could only be replaced wholesale meant that trying a run without one
        /// parameter was not a thing the window could do.
        ///
        /// Folded away by default: the count is still the answer most of the time, and on a
        /// real avatar this is longer than the rest of the panel together.
        /// </summary>
        void DrawSynced()
        {
            var declared = ParameterNames();
            EditorGUILayout.BeginHorizontal();
            // Folding is not a setting. The panel rebuilds a live session whenever one of its
            // fields changes, and a session dropped because somebody opened a list would take
            // the history they opened it to read away with it.
            bool changed = GUI.changed;
            _syncedOpen = EditorGUILayout.Foldout(_syncedOpen,
                L.Tr("Synced: {0} parameter(s)", _synced.Count), true);
            GUI.changed = changed;
            GUILayout.FlexibleSpace();
            // Beside the button that would fix it rather than in a HelpBox of its own: a list
            // that differs from the store is sometimes the experiment — taking one parameter
            // off the wire to see what breaks is a run whose list SHOULD differ — so this says
            // where the two stand and leaves the deciding alone.
            if (RunWarnings.DiffersFromStore(_synced, StoredSynced()))
                GUILayout.Label(new GUIContent(L.Tr("≠ store"),
                        L.Tr("This is not the set the avatar's parameter store calls synced. Deliberate for a run asking what happens without one of them; stale otherwise.")),
                    Warning, GUILayout.Width(46f));
            if (GUILayout.Button(L.Tr("From The Store"), EditorStyles.miniButton,
                    GUILayout.Width(110f)))
            {
                FillFromStore();
                GUI.changed = true;
            }
            EditorGUILayout.EndHorizontal();

            if (_syncedOpen)
            {
                int remove = -1;
                for (int i = 0; i < _synced.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(16f);
                    bool missing = RunWarnings.Missing(_synced[i], declared);
                    GUILayout.Label(new GUIContent(_synced[i], missing
                            ? L.Tr("This controller has no parameter by this name.")
                            : string.Empty),
                        missing ? Warning : EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("-", EditorStyles.miniButton, GUILayout.Width(22f)))
                        remove = i;
                    EditorGUILayout.EndHorizontal();
                }
                if (remove >= 0)
                {
                    _synced.RemoveAt(remove);
                    GUI.changed = true;
                }
            }

            // What is already wrong with the experiment, above the run rather than after it.
            // Not in SimNotes: that list is what a run of this CONTROLLER cannot promise and is
            // true whatever the settings say, and these are all fixed by a field on this panel.
            foreach (var warning in RunWarnings.For(_twoClients, _synced, declared,
                         BuildStimulus()))
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
        }

        /// <summary>A name that will not do what it says, in the one ink this module keeps for
        /// that. Built once — a style allocated per repaint is a style allocated per row.</summary>
        GUIStyle Warning
        {
            get
            {
                if (_warningStyle == null)
                    _warningStyle = new GUIStyle(EditorStyles.miniLabel);
                // Re-set rather than set once: the skin can change under a window that is
                // already open, and a style built on the other one keeps the other one's ink.
                _warningStyle.normal.textColor = WaveformColors.Wrong;
                return _warningStyle;
            }
        }

        /// <summary>What the avatar's store says travels, or null when there is no store to
        /// ask. Null and empty are different answers: nothing to compare against is not the
        /// same as a store that syncs nothing.</summary>
        List<string> StoredSynced()
        {
            if (_storedRead && _storedFor == _controller) return _stored;
            _storedRead = true;
            _storedFor = _controller;
            var store = _controller != null ? ParameterStore.Of(_controller) : null;
            if (store == null) return _stored = null;
            _stored = new List<string>();
            foreach (var entry in store.Read())
                if (entry != null && entry.synced && !string.IsNullOrEmpty(entry.name))
                    _stored.Add(entry.name);
            return _stored;
        }

        // ---- inputs ---------------------------------------------------------

        /// <summary>
        /// The same pokes, written down in advance. What a run has instead of hands — and what
        /// makes an experiment repeatable, which hands are not.
        ///
        /// The count is in the header because a live session adds to this list without the list
        /// being on screen — Timed inputs are a batch mood's panel — and a number that grew
        /// while somebody was busy pressing things is the whole of what they need to know about
        /// it. Nothing here is ever trimmed to fit; see <see cref="Record"/>.
        /// </summary>
        void DrawStimulus()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L.Tr("Timed inputs ({0})", _pokes.Count),
                EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Add"), EditorStyles.miniButton, GUILayout.Width(46f)))
                _pokes.Add(new Poke { at = _pokes.Count > 0 ? _pokes[_pokes.Count - 1].at : 0f });
            EditorGUILayout.EndHorizontal();

            var names = ParameterNames();
            int remove = -1;
            for (int i = 0; i < _pokes.Count; i++)
            {
                var poke = _pokes[i];
                EditorGUILayout.BeginHorizontal();
                poke.at = EditorGUILayout.FloatField(poke.at, GUILayout.Width(52f));
                GUILayout.Label(L.Tr("s"), GUILayout.Width(12f));

                int at = Mathf.Max(0, names.IndexOf(poke.parameter));
                if (names.Count > 0)
                {
                    at = EditorGUILayout.Popup(at, names.ToArray(), GUILayout.Width(150f));
                    poke.parameter = names[at];
                }
                else poke.parameter = EditorGUILayout.TextField(poke.parameter,
                    GUILayout.Width(150f));

                var type = TypeOf(poke.parameter);
                if (type == AnimatorControllerParameterType.Trigger)
                    // A written-down trigger is the same press the live button makes: 1 sets it
                    // and 0 takes it back down, which is all a trigger can be told. Named
                    // rather than a checkbox, because "off" is not a state a trigger sits in.
                    poke.value = EditorGUILayout.Popup(poke.value != 0f ? 0 : 1,
                        new[] { L.Tr("Fire"), L.Tr("Clear") }, GUILayout.Width(60f)) == 0
                        ? 1f : 0f;
                else if (type == AnimatorControllerParameterType.Bool)
                    poke.value = EditorGUILayout.Toggle(poke.value != 0f,
                        GUILayout.Width(40f)) ? 1f : 0f;
                else
                    poke.value = EditorGUILayout.FloatField(poke.value, GUILayout.Width(60f));

                poke.scope = PokeScope(poke.scope);

                if (GUILayout.Button("-", EditorStyles.miniButton, GUILayout.Width(22f)))
                    remove = i;
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
            if (remove >= 0) _pokes.RemoveAt(remove);
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Who an input is aimed at. Two buttons while there is one other person — the shape
        /// this row has always had — and a list once there are several, because five buttons
        /// side by side stop being a choice and start being a wall.
        /// </summary>
        string PokeScope(string scope)
        {
            if (_remotes <= 1)
                return GUILayout.Toolbar(scope == Simulation.RemoteScope ? 1 : 0,
                    new[] { L.Tr("Wearer"), L.Tr("Remote") }, GUILayout.Width(120f)) == 1
                    ? Simulation.RemoteScope : string.Empty;

            var names = new string[_remotes + 1];
            names[0] = L.Tr("Wearer");
            for (int i = 0; i < _remotes; i++) names[i + 1] = Simulation.RemoteScopeAt(i);
            int at = 0;
            for (int i = 0; i < _remotes; i++)
                if (scope == Simulation.RemoteScopeAt(i)) at = i + 1;
            at = EditorGUILayout.Popup(at, names, GUILayout.Width(120f));
            return at == 0 ? string.Empty : Simulation.RemoteScopeAt(at - 1);
        }

        List<string> ParameterNames()
        {
            var names = new List<string>();
            if (_controller == null) return names;
            foreach (var parameter in _controller.parameters) names.Add(parameter.name);
            return names;
        }

        AnimatorControllerParameterType TypeOf(string parameter)
        {
            if (_controller != null)
                foreach (var declared in _controller.parameters)
                    if (declared.name == parameter) return declared.type;
            return AnimatorControllerParameterType.Float;
        }

        // ---- recording ------------------------------------------------------

        /// <summary>
        /// Into another mood, and out of whatever the last one was holding.
        ///
        /// The one place either flag is written, which is what keeps "at most one of them is
        /// up" true (see <see cref="_live"/>). A live session's trace goes with the session it
        /// belonged to; a recording's does not, because a recording that has stopped is a
        /// finished thing and stays worth looking at wherever the window is standing.
        /// </summary>
        void SwitchTo(bool live, bool rec)
        {
            bool leavingLive = _live && !live;
            _live = live;
            _rec = rec;
            _playing = false;
            StopRecording();
            _recorder = null;
            DropSession();
            _findings = null;
            _ranWith = null;
            if (leavingLive) _view.trace = null;
        }

        /// <summary>
        /// One look at the avatar, on the editor's own update — and the arm toggle waiting for
        /// one to appear.
        ///
        /// Returns whether this update belonged to the recording, so the playback transport
        /// below does not also run on it. The sample itself is refused when the frame has
        /// already been seen (see <see cref="PlayRecorder.Sample"/>), which is most of the
        /// calls: the editor updates more often than the game draws.
        /// </summary>
        bool Watch()
        {
            if (!_rec) return false;
            // Armed, in Play mode, and nothing recorded yet: keep looking. The graph does not
            // exist at the instant Play begins — the tool builds it a few frames in — so
            // arming has to be a wait rather than a single try. Stopping by hand leaves the
            // recorder in place, which is what keeps this from starting again behind you.
            if (_armed && !_recording && _recorder == null && EditorApplication.isPlaying)
                StartRecording(true);
            if (!_recording || _recorder == null) return false;
            if (!_recorder.Alive)
            {
                StopRecording();
                Repaint();
                return true;
            }
            if (!_recorder.Sample(Time.frameCount, Time.time)) return false;
            _view.trace = _recorder.Trace;
            // Something quiet may have just moved, and the row list is built from what has.
            _view.Invalidate();
            if (_follow) _view.cursorFrame = _view.Frames - 1;
            Repaint();
            return true;
        }

        /// <param name="waiting">Whether the arm toggle is looking rather than somebody
        /// pressing the button. It declines to start on an avatar that is not running THIS
        /// controller, because the whole point of arming is to catch the moment the avatar
        /// turns up — and for the first few frames of Play mode the scene is full of Animators
        /// that are not it. A person pressing the button is answered rather than second-guessed:
        /// they get whatever the chosen Animator can give, down to reading the component
        /// directly, and the state line says which of those it was.</param>
        void StartRecording(bool waiting)
        {
            if (!EditorApplication.isPlaying) return;
            // The field wins while it is really running something. It stops being a live
            // reference the moment Play mode rebuilds the scene, and then the scene is asked.
            var target = _target;
            if (target == null || PlayRecorder.PlayablesOn(target).Count == 0)
            {
                var found = PlayRecorder.Likeliest(_controller);
                if (found != null) target = found;
            }
            if (target == null) return;
            if (waiting && PlayRecorder.Matching(_controller,
                    PlayRecorder.PlayablesOn(target)) < 0) return;

            // Who else is in it is decided here and never revisited — see PlayRecorder.On. The
            // list is asked for unconditionally: without Av3Emulator it is empty, so the toggle
            // is a toggle over nothing rather than a control that appears and disappears.
            var recorder = PlayRecorder.On(target, _controller,
                _recordClones ? PlayTools.ClonesOf(target) : null);
            if (recorder == null) return;
            _target = target;
            _recorder = recorder;
            _recording = true;
            _playing = false;
            _findings = null;
            // A recording is not an experiment this window set up, so there are no settings
            // that produced it. A clip saved from one carries the rows and no claim about a
            // wire that was never there — see TraceClip.Save and DrawFindings.
            _ranWith = null;
            _view.trace = _recorder.Trace;
            _view.cursorFrame = 0;
            _view.Invalidate();
            _view.Fit(position.width);
            _lastTick = EditorApplication.timeSinceStartup;
        }

        /// <summary>Stops writing and keeps everything written. The recorder itself is kept
        /// too: it is what the state line counts, and holding it is what stops an armed window
        /// from starting again the instant it is stopped by hand.</summary>
        void StopRecording()
        {
            if (!_recording) return;
            _recording = false;
            _findings = null;
        }

        /// <summary>
        /// Play mode ending, which is the one event this mood turns on.
        ///
        /// Stopped on the way OUT rather than after: the scene is about to be taken down and a
        /// recorder still looking would be reading a destroyed Animator. What happens next is
        /// the asymmetry the whole mood rests on — leaving Play mode does not reload the domain,
        /// so the trace written inside it is still here afterwards, and everything the window
        /// offers over a trace now applies to it. ENTERING Play mode does reload, which is why
        /// an unsaved recording does not survive one and the state line says so.
        /// </summary>
        void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode) return;
            StopRecording();
            Repaint();
        }

        /// <summary>
        /// The Rec mood's panel: what to record off what, whether to start on its own, and how
        /// it is going.
        ///
        /// The clock and the wire are not here because neither means anything — the frames are
        /// the avatar's own and the other person is a real one or is nobody. The controller
        /// stays, because it is what the states and layers are named from.
        /// </summary>
        void DrawRec()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _controller = (AnimatorController)EditorGUILayout.ObjectField(
                L.Tr("Controller"), _controller, typeof(AnimatorController), false);

            EditorGUILayout.BeginHorizontal();
            _target = (Animator)EditorGUILayout.ObjectField(new GUIContent(L.Tr("Animator"),
                    L.Tr("The avatar to record. Drop the object wearing it here, or pick one out of the list beside this — the list is everything a graph is currently driving, which is what an avatar being worn looks like from outside.")),
                _target, typeof(Animator), true);
            if (GUILayout.Button(new GUIContent(L.Tr("Running"),
                    L.Tr("Every Animator some PlayableGraph is writing to right now, named after the tool holding it where a tool does. A tick marks the ones running this controller.")),
                    EditorStyles.miniButton, GUILayout.Width(76f)))
                ShowRunning();
            EditorGUILayout.EndHorizontal();

            _armed = EditorGUILayout.Toggle(new GUIContent(L.Tr("Record On Play"),
                L.Tr("Start recording as soon as an avatar running this controller appears in Play mode. Kept across entering Play mode, which is the only way to catch the first second of a session — by the time the window can be clicked, it has gone.")),
                _armed);

            _recordClones = EditorGUILayout.Toggle(new GUIContent(L.Tr("Record Clones"),
                L.Tr("Record the copies of this avatar that other people in the instance are seeing — Av3Emulator's non-local clones — into the same trace, under a scope each beside the wearer's. Whoever is there when the recording starts is who is in it; one made after that is caught by starting another recording.")),
                _recordClones);

            foreach (string line in RecState())
                EditorGUILayout.LabelField("• " + line, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
        }

        /// <summary>Where the recording stands, in the order somebody reads it: whether it is
        /// running, what it is reading, and what will happen to what it has.</summary>
        List<string> RecState()
        {
            var lines = new List<string>();
            if (!EditorApplication.isPlaying)
                lines.Add(L.Tr("Not in Play mode. Start the avatar running and press Record — or arm the toggle above and it will start on its own."));
            else if (_recording)
                lines.Add(L.Tr("Recording — {0} frame(s), {1} frame(s) missed.",
                    _recorder != null ? _recorder.Frames : 0,
                    _recorder != null ? _recorder.Missed : 0));
            else if (_armed && _recorder == null)
                lines.Add(L.Tr("Waiting for an avatar running this controller to appear."));

            if (_recorder != null)
            {
                if (_recorder.Matched)
                    lines.Add(L.Tr("Reading the graph that is running this controller, so the states, transitions and layer weights are named."));
                else if (_recorder.FromGraph)
                    lines.Add(L.Tr("Nothing in this avatar's graph is running this controller. Its parameters are recorded; nothing can be said about layers or states."));
                else
                    lines.Add(L.Tr("No graph is driving this Animator, so it is being read directly. Nothing VRChat-shaped is happening to this avatar."));
                if (_recorder.Sources > 1)
                    lines.Add(L.Tr("Reading {0} more copy(s) of this avatar as other people see it — their rows are under {1} and the scopes after it.",
                        _recorder.Sources - 1, Simulation.PlayRemoteScope));
                if (!_recording && _recorder.Frames > 0)
                    lines.Add(L.Tr("{0} frame(s) recorded. Entering Play mode again drops them — save the run as a clip to keep it.",
                        _recorder.Frames));
            }
            return lines;
        }

        /// <summary>
        /// The candidate list: every Animator a graph is writing to. Finding them asks no tool
        /// and looks for no component by name, so an avatar worn by something nobody has heard
        /// of is in the list too — NAMING them does ask, because "GestureManager: Somebody" is
        /// the difference between a list of objects and a list of avatars when a scene holds
        /// the wearer, two other people's copies of them and a prop that happens to animate.
        ///
        /// Av3Emulator's mirror and shadow copies are the one thing left out (see
        /// <see cref="PlayTools.Role.Aside"/>); its non-local clones are listed, because
        /// recording one of those on purpose is a reasonable thing to want.
        /// </summary>
        void ShowRunning()
        {
            var menu = new GenericMenu();
            var driven = PlayTools.Candidates(PlayRecorder.Driven());
            if (driven.Count == 0)
                menu.AddDisabledItem(new GUIContent(
                    L.Tr("Nothing in the scene is being driven by a graph.")));
            foreach (var animator in driven)
            {
                var pick = animator;
                bool runs = PlayRecorder.Matching(_controller,
                    PlayRecorder.PlayablesOn(pick)) >= 0;
                menu.AddItem(new GUIContent(PlayTools.Label(pick) + (runs ? " ✓" : string.Empty)),
                    pick == _target, () => { _target = pick; Repaint(); });
            }
            menu.ShowAsContext();
        }

        // ---- running --------------------------------------------------------

        /// <summary>The avatar's own answer to "what travels": the synced entries of whichever
        /// parameter store the controller is associated with.</summary>
        void FillFromStore()
        {
            _synced.Clear();
            var store = _controller != null ? ParameterStore.Of(_controller) : null;
            if (store == null) return;
            foreach (var entry in store.Read())
                if (entry != null && entry.synced && !string.IsNullOrEmpty(entry.name))
                    _synced.Add(entry.name);
        }

        /// <summary>
        /// A poke made by hand, taken down as a timed input at the second it happened.
        ///
        /// Live is where a controller is understood and batch is where an understanding is
        /// checked, and the two moods had no way from one to the other: an afternoon of pressing
        /// things taught somebody exactly which three presses matter, and then the run that would
        /// prove it had to be typed out again from memory. This is that walk, in the direction
        /// people actually go.
        ///
        /// Only the reader's own hand reaches here — this is called from the value cell's write
        /// callback, which is the one thing in the window a person edits directly. What a driver
        /// wrote, what the wire carried and what a layer did are all values the run WORKED OUT,
        /// and writing those down would produce a stimulus that replays the run's own output
        /// back into it.
        ///
        /// A LAYER'S WEIGHT is a live write and is deliberately not written down: a timed input
        /// is "at this second, set this parameter to this", and a weight is not a parameter —
        /// see <see cref="Stimulus"/>, which says why at length. A list that quietly carried one
        /// would replay into a different run than the one it was taken from.
        ///
        /// Two writes at one moment are ONE input, the later value winning. A float cell being
        /// dragged fires on every repaint, so a paused session would otherwise take a hundred
        /// inputs down for one drag — and they would all land on the same frame anyway, where
        /// only the last of them can be seen. Nothing else is ever dropped: a hand is the
        /// experiment, and an experiment that silently forgets what was done to it is the worst
        /// surprise this window could hold. A long list says so in its header and stays.
        ///
        /// Static, and handed the list: the rule is worth a test and an EditorWindow is not one.
        /// </summary>
        internal static void Record(List<Poke> pokes, SimSession session,
            SignalTrace.Signal signal, float value)
        {
            if (pokes == null || session == null || signal == null) return;
            if (!session.Has(signal.name)) return;
            // The wearer is the empty scope in this list, which is what the panel writes and
            // what a run reads (see Simulation.Targets). A row says "Local" for the same client,
            // and one list spelling it two ways would be one the reader cannot sort.
            string scope = signal.scope == Simulation.LocalScope ? string.Empty : signal.scope;
            float at = session.Time;

            var last = pokes.Count > 0 ? pokes[pokes.Count - 1] : null;
            if (last != null && last.parameter == signal.name && last.scope == scope
                && Mathf.Approximately(last.at, at))
            {
                last.value = value;
                return;
            }
            pokes.Add(new Poke
            {
                at = at,
                scope = scope,
                parameter = signal.name,
                value = value,
            });
        }

        /// <summary>The timed inputs as the engine takes them. Its own step because the panel
        /// asks what these inputs are about to do — see <see cref="RunWarnings"/> — and a
        /// warning read off a differently built list would be a warning about another run.</summary>
        Stimulus BuildStimulus()
        {
            var stimulus = new Stimulus();
            foreach (var poke in _pokes)
                if (!string.IsNullOrEmpty(poke.parameter))
                    stimulus.At(poke.at, poke.parameter, poke.value, poke.scope);
            return stimulus;
        }

        SimSettings BuildSettings()
        {
            var settings = new SimSettings
            {
                clock = new SimClock
                {
                    fps = _fps,
                    seconds = _seconds,
                    jitter = _jitter,
                    seed = _seed,
                },
                stimulus = BuildStimulus(),
                lagRows = _lagRows,
                wire = _twoClients
                    ? new SyncWire
                    {
                        intervalSeconds = _interval,
                        latencySeconds = _latency,
                        dropChance = _dropChance,
                        remoteJoinsAt = _joinsAt,
                        quantize = _quantize,
                        // The clock's seed unless the wire was given one, so a run set up
                        // before the wire could have its own is still that run.
                        seed = _ownWireSeed ? _wireSeed : _seed,
                    }
                    : null,
            };
            if (settings.wire != null)
                for (int i = 0; i < _remotes - 1 && i < _laterJoins.Count; i++)
                    settings.wire.Joining(_laterJoins[i]);
            if (settings.wire != null)
            {
                if (_synced.Count == 0) FillFromStore();
                settings.wire.Syncs(_synced.ToArray());
            }
            return settings;
        }

        void RunNow()
        {
            var settings = BuildSettings();
            if (!WorthTheWait(settings)) return;
            _playing = false;
            _notes = null;
            _findings = null;
            _ranWith = settings;
            _view.trace = Simulation.Run(_controller, _ranWith);
            _view.cursorFrame = 0;
            _view.Fit(position.width);
            Repaint();
        }

        /// <summary>
        /// Asks before a run big enough to be noticed, and only then.
        ///
        /// A batch run is computed whole with nothing drawn until it finishes, so an hour typed
        /// where a minute was meant is an editor that has stopped answering and no way to tell
        /// whether it ever will. The estimate is arithmetic over the settings —
        /// <see cref="RunCost"/> — so the question can be asked before any of the work is done.
        ///
        /// A question and not a refusal, and asked only past the threshold: a window that
        /// confirmed every run would be a window whose confirmations are dismissed unread, and
        /// the run that finds a bug is not this window's to veto.
        /// </summary>
        bool WorthTheWait(SimSettings settings)
        {
            int parameters = _controller != null ? _controller.parameters.Length : 0;
            int layers = _controller != null ? _controller.layers.Length : 0;
            long samples = RunCost.Samples(settings, parameters, layers);
            if (samples <= RunCost.Uncomfortable) return true;
            return EditorUtility.DisplayDialog(L.Tr("A long run"),
                L.Tr("This works out at about {0:N0} samples — {1:N0} rows over {2:N0} frames. Nothing is wrong with that; it will simply take a while, and a shorter run or fewer people costs proportionally less.",
                    samples, RunCost.Rows(settings, parameters, layers),
                    settings.clock != null ? settings.clock.Frames : 0),
                L.Tr("Run"), L.Tr("Cancel"));
        }

        void StartSession()
        {
            DropSession();
            _notes = null;
            _findings = null;
            if (_controller == null) return;
            // Kept for the same reason a batch run keeps it — a Save has to write the settings
            // that produced what it is saving — and unused by the findings, which never speak
            // about a live trace at all (see DrawFindings).
            _ranWith = BuildSettings();
            _session = new SimSession(_controller, _ranWith);
            _view.trace = _session.Trace;
            _view.cursorFrame = 0;
            _view.Fit(position.width);
            _lastTick = EditorApplication.timeSinceStartup;
        }

        void DropSession()
        {
            if (_session == null) return;
            _session.Dispose();
            _session = null;
        }
    }
}

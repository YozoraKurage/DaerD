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
    /// Both produce the same trace, and the same viewer reads it.
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

        [System.Serializable]
        sealed class Poke
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
        [SerializeField] bool _live;
        [SerializeField] bool _settingsOpen = true;
        [SerializeField] bool _inputsOpen = true;
        [SerializeField] bool _notesOpen = true;
        [SerializeField] bool _findingsOpen = true;

        readonly WaveformView _view = new WaveformView();
        SimSession _session;

        // SimNotes.For walks every layer and opens a SerializedObject per driver, and OnGUI
        // asks several times a frame — on a real avatar's FX that is a steady cost for an
        // answer that only changes when the controller does. Re-read when the controller field
        // changes, when the window regains focus (the edit happened elsewhere), and on every
        // Run/Restart (the moment the answer is about to be trusted).
        List<string> _notes;
        AnimatorController _notesFor;
        bool _notesWithRemote = true;

        // The other list, read off the finished trace instead of off the controller. It cannot
        // go stale on its own — a batch run's trace never changes once it exists — so it is
        // dropped where the trace is replaced rather than watched for. _ranWith is the
        // experiment that produced it, kept because the settings fields go on being edited
        // after a run and a finding about what was synced has to be about what WAS synced.
        // Null for a clip opened from disk, whose settings are not in it.
        List<string> _findings;
        SimSettings _ranWith;

        bool _playing;
        bool _follow = true;
        double _lastTick;
        float _speed = 1f;
        static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f };
        static readonly string[] SpeedLabels = { "0.25x", "0.5x", "1x", "2x", "4x" };

        void OnEnable()
        {
            EditorApplication.update += Tick;
            if (_controller == null) _controller = Selection.activeObject as AnimatorController;
        }

        void OnFocus() => _notes = null;

        void OnDisable()
        {
            EditorApplication.update -= Tick;
            // Before a domain reload rather than after: the clients own hidden GameObjects, and
            // one that outlives the C# holding it is a leak nothing can reach to clean up.
            DropSession();
        }

        void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            float elapsed = (float)(now - _lastTick);
            _lastTick = now;
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
            _view.editable = _live && _session != null ? (System.Func<SignalTrace.Signal, bool>)
                (signal => signal.kind != SignalKind.State
                    && Simulation.IsClient(signal.scope)
                    && _session.Has(signal.name))
                : null;
            _view.write = (signal, value) =>
            {
                if (_session != null) _session.Write(signal.scope, signal.name, value);
            };

            DrawToolbar();
            if (_settingsOpen) DrawSettings();
            DrawNotes();
            DrawFindings();
            if (_inputsOpen && !_live) DrawStimulus();
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
            if (live != _live)
            {
                _live = live;
                _playing = false;
                DropSession();
                _findings = null;
                _ranWith = null;
                if (!_live) _view.trace = null;
            }

            EditorGUI.BeginDisabledGroup(_controller == null);
            if (GUILayout.Button(_live ? L.Tr("Restart") : L.Tr("Run"),
                    EditorStyles.toolbarButton, GUILayout.Width(56f)))
            {
                if (_live) StartSession();
                else RunNow();
            }
            EditorGUI.EndDisabledGroup();

            bool has = _view.trace != null && _view.Frames > 0;
            EditorGUI.BeginDisabledGroup(!has && !_live);

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

            if (_live)
                _follow = GUILayout.Toggle(_follow, L.Tr("Follow"), EditorStyles.toolbarButton,
                    GUILayout.Width(52f));

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
            EditorGUI.EndDisabledGroup();

            GUILayout.FlexibleSpace();
            _view.filter = EditorGUILayout.TextField(_view.filter,
                EditorStyles.toolbarSearchField, GUILayout.Width(140f));
            if (GUILayout.Button(L.Tr("Fit"), EditorStyles.toolbarButton, GUILayout.Width(32f)))
                _view.Fit(position.width);
            _view.movedOnly = GUILayout.Toggle(_view.movedOnly, L.Tr("Moved"),
                EditorStyles.toolbarButton, GUILayout.Width(52f));
            DrawClipMenu(has);
            if (!_live)
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
            TraceClip.Save(_view.trace, path);
        }

        void OpenClip()
        {
            var clip = PickClip(L.Tr("Open Run"));
            if (clip == null) return;
            _playing = false;
            _live = false;
            DropSession();
            // Whatever settings the window is holding are not this clip's, and a finding about
            // a wire that did not carry this run would be a confident lie. The findings a trace
            // answers on its own still appear; the rest wait for a clip that carries what it
            // was run with.
            _findings = null;
            _ranWith = null;
            _view.trace = TraceClip.Load(clip);
            _view.cursorFrame = 0;
            _view.Invalidate();
            _view.Fit(position.width);
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
            if (_live || _view.trace == null || _view.Frames == 0) return;
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

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(L.Tr("Synced: {0} parameter(s)", _synced.Count));
                if (GUILayout.Button(L.Tr("From The Store"), EditorStyles.miniButton,
                        GUILayout.Width(110f)))
                    FillFromStore();
                EditorGUILayout.EndHorizontal();
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

        // ---- inputs ---------------------------------------------------------

        /// <summary>
        /// The same pokes, written down in advance. What a run has instead of hands — and what
        /// makes an experiment repeatable, which hands are not.
        /// </summary>
        void DrawStimulus()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L.Tr("Timed inputs"), EditorStyles.boldLabel);
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
                stimulus = new Stimulus(),
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
            foreach (var poke in _pokes)
                if (!string.IsNullOrEmpty(poke.parameter))
                    settings.stimulus.At(poke.at, poke.parameter, poke.value, poke.scope);
            if (settings.wire != null)
            {
                if (_synced.Count == 0) FillFromStore();
                settings.wire.Syncs(_synced.ToArray());
            }
            return settings;
        }

        void RunNow()
        {
            _playing = false;
            _notes = null;
            _findings = null;
            _ranWith = BuildSettings();
            _view.trace = Simulation.Run(_controller, _ranWith);
            _view.cursorFrame = 0;
            _view.Fit(position.width);
            Repaint();
        }

        void StartSession()
        {
            DropSession();
            _notes = null;
            _findings = null;
            _ranWith = null;
            if (_controller == null) return;
            _session = new SimSession(_controller, BuildSettings());
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

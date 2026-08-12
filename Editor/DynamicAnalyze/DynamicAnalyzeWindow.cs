using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// DD DynamicAnalyze's front door: settings on the left of a run, the run's waveform under
    /// it, and a transport that walks a cursor along the result.
    ///
    /// The window computes nothing. A run is <see cref="Simulation.Run"/>, whole, from settings
    /// that are all data; play and pause move a cursor along what came back. That split is why
    /// the engine can be tested with nothing drawn — and why pausing cannot desynchronize
    /// anything, because there is nothing left running to pause.
    /// </summary>
    sealed class DynamicAnalyzeWindow : EditorWindow
    {
        [MenuItem("YozoLab/DD DynamicAnalyze")]
        public static void Open()
        {
            var window = GetWindow<DynamicAnalyzeWindow>();
            window.titleContent = new GUIContent(L.Tr("DD DynamicAnalyze"));
            window.minSize = new Vector2(560f, 320f);
            window.Show();
        }

        [SerializeField] AnimatorController _controller;
        [SerializeField] float _fps = 60f;
        [SerializeField] float _seconds = 10f;
        [SerializeField] float _jitter;
        [SerializeField] int _seed = 1;
        [SerializeField] bool _twoClients = true;
        [SerializeField] float _interval = 0.2f;
        [SerializeField] float _dropChance;
        [SerializeField] bool _quantize = true;
        [SerializeField] List<string> _synced = new List<string>();
        [SerializeField] bool _settingsOpen = true;

        readonly WaveformView _view = new WaveformView();
        readonly Stimulus _stimulus = new Stimulus();

        bool _playing;
        double _lastTick;
        float _speed = 1f;
        static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f };
        static readonly string[] SpeedLabels = { "0.25×", "0.5×", "1×", "2×", "4×" };

        void OnEnable()
        {
            EditorApplication.update += Tick;
            if (_controller == null) _controller = Selection.activeObject as AnimatorController;
        }

        void OnDisable() => EditorApplication.update -= Tick;

        /// <summary>Play, in the only sense this window has one: the cursor walks the finished
        /// run at wall-clock speed. Nothing is being computed while it moves.</summary>
        void Tick()
        {
            if (!_playing || _view.trace == null || _view.Frames == 0) return;
            double now = EditorApplication.timeSinceStartup;
            float elapsed = (float)(now - _lastTick);
            _lastTick = now;

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
            DrawToolbar();
            if (_settingsOpen) DrawSettings();
            var rect = GUILayoutUtility.GetRect(0f, 100000f, 0f, 100000f);
            _view.Draw(rect);
            if (GUI.changed) Repaint();
        }

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUI.BeginDisabledGroup(_controller == null);
            if (GUILayout.Button(L.Tr("Run"), EditorStyles.toolbarButton, GUILayout.Width(46f)))
                RunNow();
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(_view.trace == null || _view.Frames == 0);
            if (GUILayout.Button("|◀", EditorStyles.toolbarButton, GUILayout.Width(28f)))
            { _view.cursorFrame = 0; _playing = false; }
            if (GUILayout.Button("◀", EditorStyles.toolbarButton, GUILayout.Width(24f)))
            { _view.cursorFrame = Mathf.Max(0, _view.cursorFrame - 1); _playing = false; }
            if (GUILayout.Toggle(_playing, _playing ? "❚❚" : "▶",
                    EditorStyles.toolbarButton, GUILayout.Width(28f)) != _playing)
            {
                _playing = !_playing;
                _lastTick = EditorApplication.timeSinceStartup;
                // Playing from the end would look like nothing happening at all.
                if (_playing && _view.cursorFrame >= _view.Frames - 1) _view.cursorFrame = 0;
            }
            if (GUILayout.Button("▶", EditorStyles.toolbarButton, GUILayout.Width(24f)))
            { _view.cursorFrame = Mathf.Min(_view.Frames - 1, _view.cursorFrame + 1); _playing = false; }
            if (GUILayout.Button("▶|", EditorStyles.toolbarButton, GUILayout.Width(28f)))
            { _view.cursorFrame = Mathf.Max(0, _view.Frames - 1); _playing = false; }

            int speed = Mathf.Max(0, System.Array.IndexOf(Speeds, _speed));
            speed = EditorGUILayout.Popup(speed, SpeedLabels, EditorStyles.toolbarPopup,
                GUILayout.Width(52f));
            _speed = Speeds[speed];

            GUILayout.Space(8f);
            if (_view.trace != null && _view.Frames > 0)
                GUILayout.Label(L.Tr("frame {0} / {1}   {2:0.###} s",
                        _view.cursorFrame, _view.Frames - 1,
                        _view.trace.TimeAt(_view.cursorFrame)),
                    EditorStyles.miniLabel);
            EditorGUI.EndDisabledGroup();

            GUILayout.FlexibleSpace();
            _view.filter = EditorGUILayout.TextField(_view.filter,
                EditorStyles.toolbarSearchField, GUILayout.Width(150f));
            if (GUILayout.Button(L.Tr("Fit"), EditorStyles.toolbarButton, GUILayout.Width(34f)))
                _view.Fit(position.width);
            _settingsOpen = GUILayout.Toggle(_settingsOpen, L.Tr("Settings"),
                EditorStyles.toolbarButton, GUILayout.Width(64f));
            EditorGUILayout.EndHorizontal();
        }

        void DrawSettings()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _controller = (AnimatorController)EditorGUILayout.ObjectField(
                L.Tr("Controller"), _controller, typeof(AnimatorController), false);

            EditorGUILayout.BeginHorizontal();
            _fps = EditorGUILayout.FloatField(new GUIContent(L.Tr("FPS"),
                L.Tr("Frames per simulated second. The frame count is this times the length; jitter varies how long each one is, not how many there are.")), _fps);
            _seconds = EditorGUILayout.FloatField(L.Tr("Seconds"), _seconds);
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
            if (!_twoClients)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.BeginHorizontal();
            _interval = EditorGUILayout.FloatField(new GUIContent(L.Tr("Sync Every (s)"),
                L.Tr("Seconds between samples. The wearer's synced values are read whole and handed over together, so a change that comes and goes inside one interval never leaves them.")), _interval);
            _dropChance = EditorGUILayout.Slider(L.Tr("Loss"), _dropChance, 0f, 1f);
            EditorGUILayout.EndHorizontal();
            _quantize = EditorGUILayout.Toggle(new GUIContent(L.Tr("Round Like The Wire"),
                L.Tr("Floats to 8 bits across -1..1, Ints to a byte, Bools to a bit. On, because that is what a remote actually holds.")), _quantize);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L.Tr("Synced: {0} parameter(s)", _synced.Count));
            if (GUILayout.Button(L.Tr("From The Store"), EditorStyles.miniButton,
                    GUILayout.Width(110f)))
                FillFromStore();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

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

        void RunNow()
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
                stimulus = _stimulus,
                wire = _twoClients
                    ? new SyncWire
                    {
                        intervalSeconds = _interval,
                        dropChance = _dropChance,
                        quantize = _quantize,
                        seed = _seed,
                    }
                    : null,
            };
            if (settings.wire != null)
            {
                if (_synced.Count == 0) FillFromStore();
                settings.wire.Syncs(_synced.ToArray());
            }

            _playing = false;
            _view.trace = Simulation.Run(_controller, settings);
            _view.cursorFrame = 0;
            _view.Fit(position.width);
            Repaint();
        }
    }
}

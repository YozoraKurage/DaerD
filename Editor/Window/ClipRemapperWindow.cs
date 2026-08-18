using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Repath tool for a controller's animation clips: scan the avatar for bindings whose
    /// hierarchy paths broke (renames / moves), rewrite From → To prefixes (with GameObject
    /// drop slots), and optionally follow future hierarchy changes automatically while the
    /// window stays open (Auto-Repath tracks the bindings that were valid when it was turned
    /// on).
    /// </summary>
    class ClipRemapperWindow : EditorWindow
    {
        class TrackedPath
        {
            public string path;
            public Transform transform;
        }

        [SerializeField] AnimatorController _controller;
        [SerializeField] GameObject _avatar;
        string _from = string.Empty;
        string _to = string.Empty;
        bool _selectedClipsOnly;
        bool _autoRepath;
        List<ClipRepather.BrokenBinding> _broken;
        List<string> _brokenPaths;
        readonly List<TrackedPath> _tracked = new List<TrackedPath>();
        Vector2 _scroll;
        string _report;

        public static ClipRemapperWindow Open(AnimatorController controller)
        {
            var window = GetWindow<ClipRemapperWindow>();
            window.minSize = new Vector2(420, 320);
            if (controller != null)
            {
                window._controller = controller;
                if (window._avatar == null)
                    window._avatar = ClipRepather.FindAnimatorRoot(controller);
            }
            window.Show();
            window.Focus();
            return window;
        }

        void OnEnable()
        {
            ApplyTitle();
            L.LanguageChanged += ApplyTitle;
            // Rebuilt rather than persisted: transform references and scan results don't
            // survive domain reloads anyway.
            _autoRepath = false;
        }

        void OnDisable()
        {
            L.LanguageChanged -= ApplyTitle;
            SetAutoRepath(false);
        }

        void ApplyTitle() => titleContent = new GUIContent(L.Tr("DaerD Remap"));

        void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            _controller = (AnimatorController)EditorGUILayout.ObjectField(
                _controller, typeof(AnimatorController), false);
            var avatar = (GameObject)EditorGUILayout.ObjectField(
                _avatar, typeof(GameObject), true);
            if (avatar != _avatar)
            {
                _avatar = avatar;
                _broken = null;
                SetAutoRepath(false);
            }
            EditorGUILayout.EndHorizontal();

            if (_controller == null || _avatar == null)
            {
                EditorGUILayout.HelpBox(
                    L.Tr("Assign the controller and the avatar (the GameObject with the Animator) to scan for broken animation paths."),
                    MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(L.Tr("Scan For Broken Paths")))
            {
                _broken = ClipRepather.ScanBroken(TargetClips(), _avatar);
                _brokenPaths = ClipRepather.DistinctBrokenPaths(_broken);
                _report = null;
            }
            bool auto = GUILayout.Toggle(_autoRepath,
                new GUIContent(L.Tr("Auto-Repath"),
                    L.Tr("While this window is open, follow renames and moves under the avatar and rewrite the bindings that were valid when this was enabled.")),
                EditorStyles.miniButton, GUILayout.Width(DaerDLayout.DialogButton));
            if (auto != _autoRepath)
                SetAutoRepath(auto);
            EditorGUILayout.EndHorizontal();

            DrawPathRow(L.Tr("From"), ref _from);
            DrawPathRow(L.Tr("To"), ref _to);
            _selectedClipsOnly = EditorGUILayout.ToggleLeft(
                new GUIContent(L.Tr("Only clips selected in the Project window"),
                    L.Tr("Off: every clip this controller references.")),
                _selectedClipsOnly);

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_from) || _from == _to))
                if (GUILayout.Button(L.Tr("Rewrite Paths")))
                {
                    int count = ClipRepather.Repath(TargetClips(), _from.Trim(), _to.Trim());
                    _report = L.Tr("{0} binding(s) rewritten.", count);
                    _broken = ClipRepather.ScanBroken(TargetClips(), _avatar);
                    _brokenPaths = ClipRepather.DistinctBrokenPaths(_broken);
                }
            if (_report != null)
                EditorGUILayout.LabelField(_report, EditorStyles.miniLabel);

            DrawScanResults();
        }

        void DrawPathRow(string label, ref string value)
        {
            EditorGUILayout.BeginHorizontal();
            value = EditorGUILayout.TextField(label, value);
            // Action slot: dropping an object under the avatar fills the field with its path.
            var dropped = (GameObject)EditorGUILayout.ObjectField(
                null, typeof(GameObject), true, GUILayout.Width(60));
            if (dropped != null && _avatar != null && dropped.transform.IsChildOf(_avatar.transform))
                value = AnimationUtility.CalculateTransformPath(dropped.transform, _avatar.transform);
            EditorGUILayout.EndHorizontal();
        }

        void DrawScanResults()
        {
            if (_broken == null) return;
            if (_broken.Count == 0)
            {
                EditorGUILayout.HelpBox(L.Tr("No broken bindings found."), MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(
                L.Tr("{0} broken binding(s) — click a path to fill From:", _broken.Count),
                EditorStyles.boldLabel);
            int shown = 0;
            foreach (var path in _brokenPaths)
            {
                if (shown++ >= 5) break;
                if (GUILayout.Button(path, EditorStyles.miniButton))
                    _from = path;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var entry in _broken)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    new GUIContent(entry.clip.name + " : " + entry.binding.path,
                        entry.binding.propertyName),
                    EditorStyles.miniLabel);
                if (GUILayout.Button(L.Tr("Ping"), EditorStyles.miniButton, GUILayout.Width(DaerDLayout.RowAction)))
                    EditorGUIUtility.PingObject(entry.clip);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        IEnumerable<AnimationClip> TargetClips()
        {
            if (!_selectedClipsOnly)
                return ClipRepather.ClipsOf(_controller);
            var selected = new List<AnimationClip>();
            foreach (var obj in Selection.objects)
                if (obj is AnimationClip clip)
                    selected.Add(clip);
            return selected;
        }

        // ---- auto-repath ------------------------------------------------------

        void SetAutoRepath(bool enabled)
        {
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            _tracked.Clear();
            _autoRepath = enabled && _avatar != null && _controller != null;
            if (!_autoRepath) return;

            // Track every currently-valid binding path with a live Transform reference —
            // renames and moves are then detected by recomputing each path.
            var seen = new HashSet<string>();
            foreach (var clip in ClipRepather.ClipsOf(_controller))
            {
                if (clip == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    TrackPath(binding.path, seen);
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    TrackPath(binding.path, seen);
            }
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        void TrackPath(string path, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(path) || !seen.Add(path)) return;
            var transform = _avatar.transform.Find(path);
            if (transform != null)
                _tracked.Add(new TrackedPath { path = path, transform = transform });
        }

        void OnHierarchyChanged()
        {
            if (!_autoRepath || _avatar == null || _controller == null) return;
            int total = 0;
            foreach (var tracked in _tracked)
            {
                if (tracked.transform == null || !tracked.transform.IsChildOf(_avatar.transform))
                    continue;
                string current = AnimationUtility.CalculateTransformPath(
                    tracked.transform, _avatar.transform);
                if (current == tracked.path) continue;
                // Repath only rewrites bindings still carrying the old path, so overlapping
                // parent/child updates converge instead of double-applying.
                total += ClipRepather.Repath(ClipRepather.ClipsOf(_controller), tracked.path, current);
                tracked.path = current;
            }
            if (total > 0)
            {
                _report = L.Tr("Auto-Repath rewrote {0} binding(s).", total);
                Repaint();
            }
        }
    }
}

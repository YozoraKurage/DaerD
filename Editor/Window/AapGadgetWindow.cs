using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Wizard for <see cref="AapGadgets"/>: pick an operation and its Float inputs, and
    /// generate the Direct-blend-tree gadget that computes it every frame. The output name
    /// follows the picked inputs until the user edits it by hand.
    /// </summary>
    class AapGadgetWindow : EditorWindow
    {
        AnimatorController _controller;
        Action _onApplied;

        string[] _floatParams = Array.Empty<string>();
        AapGadgets.Kind _kind = AapGadgets.Kind.Smooth;
        int _inputAIndex;
        int _inputBIndex;
        string _output = string.Empty;
        string _smoothing = string.Empty;
        float _smoothingDefault = 0.9f;
        // Kept apart from _smoothingDefault: the two mean different things, and switching
        // operations back and forth shouldn't rewrite the value the other one is holding.
        float _stepSize = 0.05f;
        float _rangeMin = -1f;
        float _rangeMax = 1f;
        float _inMin = 0f;
        float _inMax = 1f;
        float _threshold = 0.5f;
        // 0 = create a new layer; 1.. = _layerCandidates[index - 1].
        int _layerChoice;
        string _newLayerName = "DBT";
        readonly List<int> _layerCandidates = new List<int>();

        public static void Open(AnimatorController controller, Action onApplied)
        {
            var window = CreateInstance<AapGadgetWindow>();
            window.titleContent = new GUIContent(L.Tr("DBT Gadget"));
            window.minSize = new Vector2(420, 340);
            window._controller = controller;
            window._onApplied = onApplied;
            window.RefreshChoices();
            window.ShowUtility();
        }

        void RefreshChoices()
        {
            var floats = new List<string>();
            if (_controller != null)
                foreach (var p in _controller.parameters)
                    if (p.type == AnimatorControllerParameterType.Float)
                        floats.Add(p.name);
            _floatParams = floats.ToArray();
            _inputAIndex = 0;
            _inputBIndex = Mathf.Min(1, Mathf.Max(0, _floatParams.Length - 1));
            ApplyOutputDefault();

            _layerCandidates.Clear();
            if (_controller != null)
            {
                var layers = _controller.layers;
                for (int i = 0; i < layers.Length; i++)
                    if (DbtBuilder.CanHostGadget(layers[i]))
                        _layerCandidates.Add(i);
            }
            _layerChoice = _layerCandidates.Count > 0 ? 1 : 0;
        }

        string ParamAt(int index) =>
            _floatParams.Length > 0 ? _floatParams[Mathf.Clamp(index, 0, _floatParams.Length - 1)] : null;

        void ApplyOutputDefault()
        {
            string a = ParamAt(_inputAIndex), b = ParamAt(_inputBIndex);
            if (a == null) { _output = string.Empty; _smoothing = string.Empty; return; }
            _smoothing = _kind == AapGadgets.Kind.SmoothLinear ? a + "/StepSize" : a + "/Smoothing";
            switch (_kind)
            {
                case AapGadgets.Kind.Smooth: _output = a + "/Smoothed"; break;
                case AapGadgets.Kind.Add:
                case AapGadgets.Kind.AddRanged: _output = a + "+" + b; break;
                case AapGadgets.Kind.Sub:
                case AapGadgets.Kind.SubRanged: _output = a + "-" + b; break;
                case AapGadgets.Kind.Multiply: _output = a + "*" + b; break;
                case AapGadgets.Kind.And: _output = a + "&" + b; break;
                case AapGadgets.Kind.Or: _output = a + "|" + b; break;
                case AapGadgets.Kind.Not: _output = a + "/Not"; break;
                case AapGadgets.Kind.FloatAsBool: _output = a + "/Bool"; break;
                case AapGadgets.Kind.Remap: _output = a + "/Remapped"; break;
                case AapGadgets.Kind.Reciprocal: _output = a + "/Inv"; break;
                case AapGadgets.Kind.Divide: _output = a + "÷" + b; break;
                // No input to name it after — and one per controller is the idea anyway.
                case AapGadgets.Kind.FrameTime: _output = "FrameTime"; break;
                case AapGadgets.Kind.SmoothLinear: _output = a + "/Smoothed"; break;
                case AapGadgets.Kind.SeparateDigits: _output = a + "/Digits"; break;
                case AapGadgets.Kind.Sine: _output = a + "/Sin"; break;
                case AapGadgets.Kind.Cosine: _output = a + "/Cos"; break;
                case AapGadgets.Kind.Tangent: _output = a + "/Tan"; break;
            }
        }

        string KindDescription()
        {
            switch (_kind)
            {
                case AapGadgets.Kind.Smooth:
                    return L.Tr("output = lerp(input, output, smoothing) — exponential smoothing recalculated every frame.");
                case AapGadgets.Kind.Add:
                    return L.Tr("output = A + B. Positive values only (Direct weights clamp at 0); use Add (Ranged) for signed inputs.");
                case AapGadgets.Kind.AddRanged:
                    return L.Tr("output = A + B over the given range; works with negative values.");
                case AapGadgets.Kind.Sub:
                    return L.Tr("output = A - B. Positive values only; use Sub (Ranged) for signed inputs.");
                case AapGadgets.Kind.SubRanged:
                    return L.Tr("output = A - B over the given range; use a symmetric range (Min = -Max).");
                case AapGadgets.Kind.Multiply:
                    return L.Tr("output = A × B via nested Direct trees. Positive values only.");
                case AapGadgets.Kind.And:
                    return L.Tr("output = A AND B, for 0/1 inputs.");
                case AapGadgets.Kind.Or:
                    return L.Tr("output = A OR B, for 0/1 inputs.");
                case AapGadgets.Kind.Not:
                    return L.Tr("output = 1 - input, for 0/1 inputs.");
                case AapGadgets.Kind.FloatAsBool:
                    return L.Tr("output = 1 when the input is at or above the threshold, else 0.");
                case AapGadgets.Kind.Remap:
                    return L.Tr("Linearly remaps the input range to the output range (reversed output ranges invert the slope).");
                case AapGadgets.Kind.Reciprocal:
                    return L.Tr("output = 1 / input, for positive inputs. The result trails the input by two frames.");
                case AapGadgets.Kind.Divide:
                    return L.Tr("output = A / B, for positive inputs. Builds B's reciprocal first, so the result trails by three frames.");
                case AapGadgets.Kind.FrameTime:
                    return L.Tr("output = the seconds since the previous frame. Add only one per controller — the clock it runs is shared machinery.");
                case AapGadgets.Kind.SmoothLinear:
                    return L.Tr("Moves the output toward the input by Step Size every frame — a constant speed, where Smooth eases in. Drive Step Size from a Frame Time gadget for a frame-rate independent speed.");
                case AapGadgets.Kind.SeparateDigits:
                    return L.Tr("Splits a 0..1 input into its first three decimals: '/Tenths' holds 0…0.9, '/Hundredths' 0…0.09 and '/Thousandths' 0…0.009. The output name is the base name for the three.");
                case AapGadgets.Kind.Sine:
                    return L.Tr("output = sin(2π × input): 0..1 walks one whole turn.");
                case AapGadgets.Kind.Cosine:
                    return L.Tr("output = cos(2π × input): 0..1 walks one whole turn.");
                case AapGadgets.Kind.Tangent:
                    return L.Tr("output = tan(2π × input): 0..1 walks one whole turn, held to ±100 around the poles.");
            }
            return string.Empty;
        }

        // Must stay in AapGadgets.Kind order.
        static readonly string[] KindLabels =
        {
            "Smooth", "Add", "Add (Ranged)", "Sub", "Sub (Ranged)", "Multiply",
            "And", "Or", "Not", "Float As Bool", "Remap",
            "Reciprocal", "Divide", "Frame Time", "Smooth (Linear)", "Separate Digits",
            "Sine", "Cosine", "Tangent",
        };

        void OnGUI()
        {
            // Utility windows outlive domain reloads but their fields don't; bail out.
            if (_controller == null)
            {
                Close();
                return;
            }

            if (_floatParams.Length == 0)
            {
                EditorGUILayout.HelpBox(L.Tr("This controller has no Float parameters to smooth."), MessageType.Info);
                if (GUILayout.Button(L.Tr("Cancel"))) Close();
                return;
            }

            EditorGUILayout.LabelField(L.Tr("DBT Gadget"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                L.Tr("Adds a Direct blend tree gadget that computes the picked operation every frame. The generated clips and trees are stored as sub-assets of this controller."),
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            _kind = (AapGadgets.Kind)EditorGUILayout.Popup(L.Tr("Operation"), (int)_kind, KindLabels);
            if (AapGadgets.NeedsInput(_kind))
                _inputAIndex = EditorGUILayout.Popup(L.Tr("Input A"), _inputAIndex, _floatParams);
            if (AapGadgets.IsBinary(_kind))
                _inputBIndex = EditorGUILayout.Popup(L.Tr("Input B"), _inputBIndex, _floatParams);
            if (EditorGUI.EndChangeCheck())
                ApplyOutputDefault();   // follow the inputs until the name is edited below

            EditorGUILayout.HelpBox(KindDescription(), MessageType.None);
            _output = EditorGUILayout.TextField(L.Tr("Output Parameter"), _output);

            if (_kind == AapGadgets.Kind.Smooth)
            {
                _smoothing = EditorGUILayout.TextField(L.Tr("Smoothing Parameter"), _smoothing);
                _smoothingDefault = EditorGUILayout.Slider(
                    new GUIContent(L.Tr("Default Smoothing"),
                        L.Tr("0 = follow instantly; closer to 1 = smoother and slower. Stored as the smoothing parameter's default value.")),
                    _smoothingDefault, 0f, 1f);
            }
            else if (_kind == AapGadgets.Kind.SmoothLinear)
            {
                _smoothing = EditorGUILayout.TextField(L.Tr("Step Size Parameter"), _smoothing);
                _stepSize = EditorGUILayout.FloatField(
                    new GUIContent(L.Tr("Default Step Size"),
                        L.Tr("How far the output may travel per frame, in parameter units. Stored as the step size parameter's default value; other gadgets can share the parameter.")),
                    _stepSize);
            }

            if (AapGadgets.UsesRange(_kind))
            {
                EditorGUILayout.BeginHorizontal();
                _rangeMin = EditorGUILayout.FloatField(
                    _kind == AapGadgets.Kind.Remap ? L.Tr("Output Min") : L.Tr("Range Min"), _rangeMin);
                _rangeMax = EditorGUILayout.FloatField(
                    _kind == AapGadgets.Kind.Remap ? L.Tr("Output Max") : L.Tr("Range Max"), _rangeMax);
                EditorGUILayout.EndHorizontal();
            }
            if (_kind == AapGadgets.Kind.Remap)
            {
                EditorGUILayout.BeginHorizontal();
                _inMin = EditorGUILayout.FloatField(L.Tr("Input Min"), _inMin);
                _inMax = EditorGUILayout.FloatField(L.Tr("Input Max"), _inMax);
                EditorGUILayout.EndHorizontal();
            }
            if (_kind == AapGadgets.Kind.FloatAsBool)
                _threshold = EditorGUILayout.FloatField(L.Tr("Threshold"), _threshold);

            if (AapGadgets.UsesDbtLayer(_kind))
                DrawLayerChoice();
            if (AapGadgets.CreatesSupportingLayer(_kind))
                EditorGUILayout.HelpBox(AapGadgets.UsesDbtLayer(_kind)
                        ? L.Tr("This operation also adds a layer of its own, at the end of the controller. It has to stay after the blend tree layer to work.")
                        : L.Tr("This operation is a layer of its own: one state whose motion time follows the input. Nothing is added to a blend tree."),
                    MessageType.Info);

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(100)))
                Close();
            if (GUILayout.Button(L.Tr("Create"), GUILayout.Width(100)))
                TryApply();
            EditorGUILayout.EndHorizontal();
        }

        void DrawLayerChoice()
        {
            var labels = new string[_layerCandidates.Count + 1];
            labels[0] = L.Tr("Create new layer");
            var layers = _controller.layers;
            for (int i = 0; i < _layerCandidates.Count; i++)
            {
                int index = _layerCandidates[i];
                labels[i + 1] = index < layers.Length ? layers[index].name : "?";
            }
            _layerChoice = EditorGUILayout.Popup(L.Tr("Target Layer"), Mathf.Clamp(_layerChoice, 0, labels.Length - 1), labels);
            if (_layerChoice == 0)
                _newLayerName = EditorGUILayout.TextField(L.Tr("New Layer Name"), _newLayerName);
        }

        void TryApply()
        {
            var request = new AapGadgets.Request
            {
                controller = _controller,
                kind = _kind,
                inputA = AapGadgets.NeedsInput(_kind) ? ParamAt(_inputAIndex) : null,
                inputB = AapGadgets.IsBinary(_kind) ? ParamAt(_inputBIndex) : null,
                output = _output != null ? _output.Trim() : string.Empty,
                rangeMin = _rangeMin,
                rangeMax = _rangeMax,
                inMin = _inMin,
                inMax = _inMax,
                threshold = _threshold,
                smoothing = _smoothing != null ? _smoothing.Trim() : string.Empty,
                smoothingDefault = _kind == AapGadgets.Kind.SmoothLinear ? _stepSize : _smoothingDefault,
                layerIndex = _layerChoice > 0 && _layerChoice - 1 < _layerCandidates.Count
                    ? _layerCandidates[_layerChoice - 1] : -1,
                newLayerName = _newLayerName != null ? _newLayerName.Trim() : string.Empty,
            };

            var error = AapGadgets.Validate(request);
            if (error != null)
            {
                EditorUtility.DisplayDialog(L.Tr("DBT Gadget"), error, "OK");
                return;
            }
            AapGadgets.Apply(request);
            _onApplied?.Invoke();
            Close();
        }
    }
}

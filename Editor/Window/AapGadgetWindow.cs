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
        AnimationCurve _curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        int _lutSamples = 33;
        int _bufferFrames = 1;
        int _atan2Directions = 16;
        // 0 = create a new layer; 1.. = _layerCandidates[index - 1].
        int _layerChoice;
        string _newLayerName = "DBT";
        readonly List<int> _layerCandidates = new List<int>();
        // The gadgets already saved with this controller. 0 = create a new one; 1.. edits and
        // regenerates _gadgets[index - 1] in place.
        readonly List<GraphFrameData.AapGadgetConfig> _gadgets =
            new List<GraphFrameData.AapGadgetConfig>();
        int _gadgetChoice;

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

            _gadgets.Clear();
            _gadgets.AddRange(GraphFrameData.GetGadgets(_controller));
            _gadgetChoice = 0;
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
                case AapGadgets.Kind.Lut1D: _output = a + "/Lut"; break;
                case AapGadgets.Kind.Atan2: _output = a + "/Angle"; break;
                case AapGadgets.Kind.Buffer: _output = a + "/Buffered"; break;
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
                    return L.Tr("output = 1 / input, for positive inputs: exact from 1 up, a lookup table below it (capped at 240). The result trails the input by two frames.");
                case AapGadgets.Kind.Divide:
                    return L.Tr("output = A / B, for positive inputs. Builds B's reciprocal first, so the result trails by three frames.");
                case AapGadgets.Kind.FrameTime:
                    return L.Tr("output = the seconds since the previous frame. Add only one per controller — the clock it runs is shared machinery.");
                case AapGadgets.Kind.SmoothLinear:
                    return L.Tr("Moves the output toward the input by Step Size every frame — a constant speed, where Smooth eases in. Drive Step Size from a Frame Time gadget for a frame-rate independent speed.");
                case AapGadgets.Kind.SeparateDigits:
                    return L.Tr("Splits a 0..1 input into its first three decimals: '/Tenths' holds 0…0.9, '/Hundredths' 0…0.09 and '/Thousandths' 0…0.009. The output name is the base name for the three.");
                case AapGadgets.Kind.Sine:
                    return L.Tr("output = sin(2π × input): 0..1 walks one whole turn. A lookup table inside the blend tree layer.");
                case AapGadgets.Kind.Cosine:
                    return L.Tr("output = cos(2π × input): 0..1 walks one whole turn. A lookup table inside the blend tree layer.");
                case AapGadgets.Kind.Tangent:
                    return L.Tr("output = tan(2π × input): 0..1 walks one whole turn, held to ±100 around the poles. A lookup table inside the blend tree layer.");
                case AapGadgets.Kind.Lut1D:
                    return L.Tr("Bakes the curve into a 1D blend tree lookup table: the curve's time axis is the input, its value the output, linearly interpolated between evenly spaced sample points. Lives entirely inside the blend tree layer.");
                case AapGadgets.Kind.Atan2:
                    return L.Tr("output = atan2(Y, X) in turns: 0..1 counter-clockwise from +X, ready to feed the Sine / Cosine gadgets. Values near the origin collapse toward 0 — gate by magnitude. The 0/1 seam sits in a narrow band at +X.");
                case AapGadgets.Kind.Buffer:
                    return L.Tr("output = the input, delayed by exactly N frames. Every blend tree stage costs one frame, so branches of different depth see different frames of the same parameter — insert a buffer on the shallower branch to line the two up again.");
            }
            return string.Empty;
        }

        // Must stay in AapGadgets.Kind order.
        static readonly string[] KindLabels =
        {
            "Smooth", "Add", "Add (Ranged)", "Sub", "Sub (Ranged)", "Multiply",
            "And", "Or", "Not", "Float As Bool", "Remap",
            "Reciprocal", "Divide", "Frame Time", "Smooth (Linear)", "Separate Digits",
            "Sine", "Cosine", "Tangent", "LUT (Curve)", "Atan2", "Buffer (Delay)",
        };

        /// <summary>Shapes the Curve field can be filled with. Index 0 writes nothing — see
        /// <see cref="CurvePresetValue"/> for the rest.</summary>
        static readonly string[] CurvePresetLabels =
        {
            "Custom", "Linear", "Sqrt", "Square", "Smoothstep", "Ease Out",
        };

        /// <summary>Keys a preset is drawn with. Enough to carry the shape into the curve
        /// editor; the LUT samples the curve again at its own resolution anyway.</summary>
        const int CurvePresetKeys = 9;

        static AnimationCurve BuildCurvePreset(int preset)
        {
            // A straight line is exactly two keys — sampling it nine times would only give the
            // user more handles to drag than the shape needs.
            if (preset == 1) return AnimationCurve.Linear(0f, 0f, 1f, 1f);
            var curve = new AnimationCurve();
            for (int i = 0; i < CurvePresetKeys; i++)
            {
                float t = (float)i / (CurvePresetKeys - 1);
                curve.AddKey(new Keyframe(t, CurvePresetValue(preset, t)));
            }
            AapGadgets.SmoothTangents(curve);
            return curve;
        }

        static float CurvePresetValue(int preset, float t)
        {
            switch (preset)
            {
                case 2: return Mathf.Sqrt(t);
                case 3: return t * t;
                case 4: return t * t * (3f - 2f * t);      // smoothstep
                case 5: return 1f - (1f - t) * (1f - t);   // ease out
                default: return t;
            }
        }

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

            DrawGadgetChoice();

            EditorGUI.BeginChangeCheck();
            _kind = (AapGadgets.Kind)EditorGUILayout.Popup(L.Tr("Operation"), (int)_kind, KindLabels);
            // Atan2's inputs are a vector, not an A/B pair, and naming them after the axes is
            // the only hint that A is the one atan2 takes first.
            bool vectorInputs = _kind == AapGadgets.Kind.Atan2;
            if (AapGadgets.NeedsInput(_kind))
                _inputAIndex = EditorGUILayout.Popup(
                    vectorInputs ? L.Tr("Input Y") : L.Tr("Input A"), _inputAIndex, _floatParams);
            if (AapGadgets.IsBinary(_kind))
                _inputBIndex = EditorGUILayout.Popup(
                    vectorInputs ? L.Tr("Input X") : L.Tr("Input B"), _inputBIndex, _floatParams);
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
            else if (_kind == AapGadgets.Kind.Lut1D)
            {
                DrawCurveField();
            }
            else if (_kind == AapGadgets.Kind.Atan2)
            {
                _atan2Directions = EditorGUILayout.IntSlider(
                    new GUIContent(L.Tr("Directions"),
                        L.Tr("Ring samples around the circle. Angle accuracy is about 1/N turn between neighbouring directions; each direction is one clip.")),
                    _atan2Directions, AapGadgets.MinAtan2Directions, AapGadgets.MaxAtan2Directions);
            }
            else if (_kind == AapGadgets.Kind.Buffer)
            {
                _bufferFrames = EditorGUILayout.IntSlider(
                    new GUIContent(L.Tr("Frames"),
                        L.Tr("How many frames late the copy runs — one identity stage per frame. Match it to the pipeline depth of the branch you are aligning with.")),
                    _bufferFrames, AapGadgets.MinBufferFrames, AapGadgets.MaxBufferFrames);
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
                EditorGUILayout.HelpBox(
                    L.Tr("This operation also adds a layer of its own, at the end of the controller. It has to stay after the blend tree layer to work."),
                    MessageType.Info);

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(100)))
                Close();
            if (_gadgetChoice > 0 && GUILayout.Button(L.Tr("Delete"), GUILayout.Width(100)))
                TryDelete();
            if (GUILayout.Button(_gadgetChoice > 0 ? L.Tr("Regenerate") : L.Tr("Create"),
                GUILayout.Width(100)))
                TryApply();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Saved gadgets double as the form's subject: "create a new one", or load one
        /// of them back and rebuild it in place with the (editable) inputs it was made from.
        /// Same shape as the async-sync wizard's layer choice, for the same reason — a
        /// generated thing nobody can read back needs its inputs kept somewhere.</summary>
        void DrawGadgetChoice()
        {
            if (_gadgets.Count == 0) return;

            var labels = new string[_gadgets.Count + 1];
            labels[0] = L.Tr("Create new gadget");
            for (int i = 0; i < _gadgets.Count; i++)
                labels[i + 1] = GadgetLabel(_gadgets[i]);
            int picked = EditorGUILayout.Popup(L.Tr("Gadget"),
                Mathf.Clamp(_gadgetChoice, 0, labels.Length - 1), labels);
            if (picked != _gadgetChoice)
            {
                _gadgetChoice = picked;
                if (picked > 0) LoadConfig(_gadgets[picked - 1]);
            }
            if (_gadgetChoice == 0) return;

            EditorGUILayout.HelpBox(
                L.Tr("Applying regenerates this gadget in place (its trees are rebuilt)."),
                MessageType.None);
            string missing = MissingInput(_gadgets[_gadgetChoice - 1]);
            if (missing != null)
                EditorGUILayout.HelpBox(
                    L.Tr("Parameter '{0}' referenced by this gadget no longer exists.", missing),
                    MessageType.Warning);
        }

        /// <summary>"Hue*Gain (Multiply)": the output names the gadget, the operation says what
        /// it does. <see cref="KindLabels"/> is indexed by the enum the config stores as an int.
        /// </summary>
        static string GadgetLabel(GraphFrameData.AapGadgetConfig config)
        {
            string kind = config.kind >= 0 && config.kind < KindLabels.Length
                ? KindLabels[config.kind] : "?";
            return config.output + " (" + kind + ")";
        }

        /// <summary>An input this saved gadget names that the controller no longer declares, or
        /// null. Renaming or deleting a parameter leaves the record pointing at nothing, and the
        /// form then shows the first parameter instead — which is rarely what was meant, so say
        /// so. Applying is refused by Validate either way.</summary>
        string MissingInput(GraphFrameData.AapGadgetConfig config)
        {
            var kind = (AapGadgets.Kind)config.kind;
            if (AapGadgets.NeedsInput(kind) && Missing(config.inputA)) return config.inputA;
            if (AapGadgets.IsBinary(kind) && Missing(config.inputB)) return config.inputB;
            return null;
        }

        bool Missing(string name) =>
            !string.IsNullOrEmpty(name) && Array.IndexOf(_floatParams, name) < 0;

        /// <summary>Fills the whole form from a saved gadget, so Regenerate starts from what
        /// was built rather than from the defaults.</summary>
        void LoadConfig(GraphFrameData.AapGadgetConfig config)
        {
            var request = AapGadgets.ToRequest(config, _controller);
            _kind = request.kind;
            _inputAIndex = IndexOfParam(request.inputA);
            _inputBIndex = IndexOfParam(request.inputB);
            _output = request.output;
            _smoothing = request.smoothing;
            // The two live in separate fields, so only the one this kind means is written.
            if (_kind == AapGadgets.Kind.SmoothLinear) _stepSize = request.smoothingDefault;
            else _smoothingDefault = request.smoothingDefault;
            _rangeMin = request.rangeMin;
            _rangeMax = request.rangeMax;
            _inMin = request.inMin;
            _inMax = request.inMax;
            _threshold = request.threshold;
            // Already a copy — editing the field can't rewrite the record behind it.
            if (request.curve != null) _curve = request.curve;
            _lutSamples = request.lutSamples;
            _bufferFrames = request.bufferFrames;
            _atan2Directions = request.atan2Directions;

            int candidate = _layerCandidates.IndexOf(request.layerIndex);
            _layerChoice = candidate >= 0 ? candidate + 1 : 0;
        }

        /// <summary>The parameter's place in the popup, or 0 when the controller no longer has
        /// it — <see cref="MissingInput"/> is what tells the user about that.</summary>
        int IndexOfParam(string name)
        {
            int index = string.IsNullOrEmpty(name) ? -1 : Array.IndexOf(_floatParams, name);
            return index >= 0 ? index : 0;
        }

        void DrawCurveField()
        {
            // A "fill it in with this shape" button wearing a popup's clothes: it always shows
            // Custom, because the curve it writes stays editable by hand afterwards and a
            // sticky selection would soon be describing something the user has since redrawn.
            var presets = new string[CurvePresetLabels.Length];
            for (int i = 0; i < presets.Length; i++)
                presets[i] = L.Tr(CurvePresetLabels[i]);
            int preset = EditorGUILayout.Popup(L.Tr("Preset"), 0, presets);
            if (preset > 0) _curve = BuildCurvePreset(preset);

            _curve = EditorGUILayout.CurveField(
                new GUIContent(L.Tr("Curve"),
                    L.Tr("The function to bake: the time axis is the input, the value the output. Only the span between the first and last key is used; inputs outside it clamp.")),
                _curve);
            _lutSamples = EditorGUILayout.IntSlider(
                new GUIContent(L.Tr("Samples"),
                    L.Tr("How many points the curve is sampled at. The tree interpolates linearly between them, so corners want a sample of their own.")),
                _lutSamples, AapGadgets.MinLutSamples, AapGadgets.MaxLutSamples);
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
                curve = _curve,
                lutSamples = _lutSamples,
                atan2Directions = _atan2Directions,
                bufferFrames = _bufferFrames,
                smoothing = _smoothing != null ? _smoothing.Trim() : string.Empty,
                smoothingDefault = _kind == AapGadgets.Kind.SmoothLinear ? _stepSize : _smoothingDefault,
                layerIndex = _layerChoice > 0 && _layerChoice - 1 < _layerCandidates.Count
                    ? _layerCandidates[_layerChoice - 1] : -1,
                newLayerName = _newLayerName != null ? _newLayerName.Trim() : string.Empty,
                // Regenerating: the gadget on screen is swept before the new one is built, and
                // its own output names don't count as taken while it is being replaced.
                replaces = _gadgetChoice > 0 && _gadgetChoice <= _gadgets.Count
                    ? _gadgets[_gadgetChoice - 1] : null,
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

        /// <summary>Deletes the selected gadget outright and stays open on "create new" — the
        /// window is where the controller's gadgets are listed, so it is also where one is
        /// taken off the list.</summary>
        void TryDelete()
        {
            if (_gadgetChoice <= 0 || _gadgetChoice > _gadgets.Count) return;
            var config = _gadgets[_gadgetChoice - 1];
            if (!EditorUtility.DisplayDialog(L.Tr("DBT Gadget"),
                L.Tr("Delete this gadget? Its trees, clips and parameters are removed."),
                L.Tr("Delete"), L.Tr("Cancel")))
                return;

            AapGadgets.RemoveGadget(_controller, config);
            // No build follows this one, so the sub-assets it freed are flushed here.
            DbtBuilder.CommitSubAssets(_controller);
            _onApplied?.Invoke();
            RefreshChoices();
        }
    }
}

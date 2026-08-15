using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// What the hands mean over a waveform. Held apart from the viewer because drawing a trace
    /// and deciding what a drag does are different questions, and only the first one is about
    /// the trace.
    ///
    /// Modelled on Unity's own Animation window rather than invented. That is the window
    /// everybody using this editor already knows for "values over time", and a second one in
    /// the same editor answering the same question with different hands is a thing to be
    /// relearned every time it is opened. So: the ruler is where a run is scrubbed, the body of
    /// the plot is for getting around it, and the wheel zooms about the pointer.
    ///
    /// What that costs is the gesture this window used to have — click anywhere to put the
    /// cursor there, the way a logic analyser does it. It went because it was the same button
    /// in the same place as travelling, and a viewer with no way to pan was the more expensive
    /// half of that trade: a run long enough to need the cursor is a run long enough to need
    /// getting around, and the bar at the bottom was the only way to do it.
    ///
    /// A gesture is held by control id rather than by watching the rect, so a drag that leaves
    /// the window keeps going and ends where the button is let go — reaching the end of a long
    /// run is exactly the drag that leaves.
    ///
    /// Keys are spelled here rather than taken from DaerD's own shortcut table: this module is
    /// meant to lift out into its own assembly, and the table lives in the core.
    /// </summary>
    sealed class WaveformInput
    {
        enum Gesture { None, Scrub, Mark, Pan, Zoom }

        static readonly int Hash = "Yozolab.DaerD.Waveform".GetHashCode();

        /// <summary>One notch of the wheel. Multiplied rather than added, so the same notch
        /// covers the same proportion of the run wherever the zoom already is.</summary>
        const float WheelZoom = 1.18f;

        /// <summary>How much of that a pixel of an Alt+right drag is worth. Small: the drag is
        /// the fine adjustment, the wheel is the coarse one.</summary>
        const float DragZoom = 0.06f;

        Gesture _gesture;
        /// <summary>Where a zoom drag started. The anchor stays put for the whole gesture —
        /// zooming about the moving pointer would drift the run out from under the hand.</summary>
        float _zoomAt;

        public void Handle(WaveformView view, Rect ruler, Rect plot, Rect rows)
        {
            var e = Event.current;
            if (e == null || view == null || view.Frames == 0) return;
            int id = GUIUtility.GetControlID(Hash, FocusType.Passive);

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (GUIUtility.hotControl != 0) return;
                    _gesture = Begin(e, ruler, plot);
                    if (_gesture == Gesture.None) return;
                    _zoomAt = e.mousePosition.x;
                    GUIUtility.hotControl = id;
                    Track(view, plot, e);
                    e.Use();
                    return;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) return;
                    Track(view, plot, e);
                    e.Use();
                    return;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id) return;
                    GUIUtility.hotControl = 0;
                    _gesture = Gesture.None;
                    e.Use();
                    return;

                case EventType.ScrollWheel:
                    if (!rows.Contains(e.mousePosition) && !ruler.Contains(e.mousePosition))
                        return;
                    // Over the names and values it is a list like any other, and with Shift it
                    // is one wherever the pointer is. Over the run itself the wheel zooms,
                    // which is what it does in the Animation window and how a run of any
                    // length is actually got through.
                    if (e.shift || !(plot.Contains(e.mousePosition) || ruler.Contains(e.mousePosition)))
                        view.ScrollRows(e.delta.y * WaveformView.RowHeight);
                    else
                        view.ZoomAt(plot, e.mousePosition.x,
                            e.delta.y > 0f ? 1f / WheelZoom : WheelZoom);
                    e.Use();
                    return;

                case EventType.KeyDown:
                    // A window whose search box is being typed in is not being driven by keys.
                    if (EditorGUIUtility.editingTextField) return;
                    if (!Key(view, plot, e.keyCode)) return;
                    e.Use();
                    return;
            }
        }

        /// <summary>
        /// Which gesture a press starts. The ruler scrubs — with Shift it moves the other
        /// cursor, since a measurement is two of the same thing — and the plot travels.
        /// </summary>
        static Gesture Begin(Event e, Rect ruler, Rect plot)
        {
            if (ruler.Contains(e.mousePosition) && e.button == 0)
                return e.shift ? Gesture.Mark : Gesture.Scrub;
            if (!plot.Contains(e.mousePosition)) return Gesture.None;
            // Alt+right is Unity's own zoom drag, and it has to be checked before the buttons
            // that pan or it never happens.
            if (e.button == 1 && e.alt) return Gesture.Zoom;
            if (e.button == 0 || e.button == 2) return Gesture.Pan;
            return Gesture.None;
        }

        void Track(WaveformView view, Rect plot, Event e)
        {
            switch (_gesture)
            {
                case Gesture.Scrub:
                    view.cursorFrame = Frame(view, plot, e);
                    break;
                case Gesture.Mark:
                    // A click toggles the mark — put it down, pick it up — and a drag moves it.
                    // Toggling on every frame a drag crossed would flicker it out of existence.
                    if (e.type == EventType.MouseDown) view.Mark(Frame(view, plot, e));
                    else view.markFrame = Frame(view, plot, e);
                    break;
                case Gesture.Pan:
                    // The run follows the hand, so the window travels the other way.
                    if (e.type == EventType.MouseDrag) view.PanBy(-e.delta.x);
                    break;
                case Gesture.Zoom:
                    if (e.type == EventType.MouseDrag)
                        view.ZoomAt(plot, _zoomAt, Mathf.Pow(WheelZoom, e.delta.x * DragZoom));
                    break;
                default:
                    return;
            }
            GUI.changed = true;
        }

        static int Frame(WaveformView view, Rect plot, Event e) =>
            Mathf.Clamp(view.FrameAtX(plot, e.mousePosition.x), 0, view.Frames - 1);

        /// <summary>The keys the Animation window answers to, as far as they mean anything
        /// here: frame the whole run, and step the cursor.</summary>
        static bool Key(WaveformView view, Rect plot, KeyCode key)
        {
            switch (key)
            {
                // Frame Selected and Frame All. Nothing here is selected, so both frame all.
                case KeyCode.F:
                case KeyCode.A:
                    view.FitPlot(plot.width);
                    return true;
                case KeyCode.LeftArrow:
                    view.cursorFrame = Mathf.Max(0, view.cursorFrame - 1);
                    return true;
                case KeyCode.RightArrow:
                    view.cursorFrame = Mathf.Min(view.Frames - 1, view.cursorFrame + 1);
                    return true;
                case KeyCode.Home:
                    view.cursorFrame = 0;
                    return true;
                case KeyCode.End:
                    view.cursorFrame = view.Frames - 1;
                    return true;
                default:
                    return false;
            }
        }
    }
}

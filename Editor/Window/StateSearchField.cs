using System.Collections.Generic;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Toolbar search box that finds states / sub-state machines by name (or motion name)
    /// across every layer and jumps to the hit. Results drop down in a floating list that is
    /// parented to the window root so it can overlap the graph.
    /// </summary>
    class StateSearchField : VisualElement
    {
        const int MaxResults = 40;

        readonly DaerDContext _context;
        readonly VisualElement _popupHost;
        readonly ToolbarSearchField _field;
        VisualElement _popup;
        List<StateSearch.Result> _results = new List<StateSearch.Result>();
        readonly List<Label> _rows = new List<Label>();
        // Which result Enter would take. Kept as an index rather than a reference so it
        // survives the list being rebuilt on every keystroke.
        int _highlight;

        public StateSearchField(DaerDContext context, VisualElement popupHost)
        {
            _context = context;
            _popupHost = popupHost;

            _field = new ToolbarSearchField();
            _field.style.width = 170;
            _field.AddToClassList("dd-toolbar-item");
            Add(_field);
            RefreshTooltip();

            _field.RegisterValueChangedCallback(evt => RefreshResults(evt.newValue));
            _field.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            // Results and query text refer to the outgoing controller / layer — drop both,
            // or a later Enter would navigate with a stale path into the new controller.
            _context.ControllerChanged += ResetSearch;
            _context.LayerChanged += ResetSearch;
            _context.SearchRequested += TakeFocus;

            // The popup must not outlive the toolbar (window closed) — it lives on the
            // window root, not under this element.
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                ClosePopup();
                _context.ControllerChanged -= ResetSearch;
                _context.LayerChanged -= ResetSearch;
                _context.SearchRequested -= TakeFocus;
            });
        }

        /// <summary>Re-reads the localized tooltip; called by the window on language change.</summary>
        // The shortcut is appended rather than written into the message, so translating the
        // sentence never has to carry a key name that is the same in every language.
        public void RefreshTooltip() =>
            _field.tooltip = L.Tr("Search states (name or motion)") + "  (Ctrl+F)";

        /// <summary>
        /// Puts the caret in the box (Ctrl+F from the graph). The inner text field is what
        /// actually takes keystrokes; focusing the wrapper alone leaves the caret nowhere.
        /// </summary>
        void TakeFocus()
        {
            var input = _field.Q<TextField>();
            if (input != null) input.Focus();
            else _field.Focus();
        }

        /// <summary>Clears the query, the cached results and the popup.</summary>
        void ResetSearch()
        {
            ClosePopup();
            _results.Clear();
            _field.SetValueWithoutNotify(string.Empty);
        }

        void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                ClosePopup();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.DownArrow && _results.Count > 0)
            {
                MoveHighlight(1);
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.UpArrow && _results.Count > 0)
            {
                MoveHighlight(-1);
                evt.StopPropagation();
            }
            else if ((evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) && _results.Count > 0)
            {
                NavigateTo(_results[Mathf.Clamp(_highlight, 0, _results.Count - 1)]);
                evt.StopPropagation();
            }
        }

        /// <summary>Moves the highlight and scrolls it into view; stops at both ends rather
        /// than wrapping, so holding an arrow key settles somewhere instead of cycling.</summary>
        void MoveHighlight(int delta)
        {
            _highlight = Mathf.Clamp(_highlight + delta, 0, _results.Count - 1);
            ApplyHighlight();
            if (_highlight >= 0 && _highlight < _rows.Count)
                (_popup as ScrollView)?.ScrollTo(_rows[_highlight]);
        }

        void ApplyHighlight()
        {
            for (int i = 0; i < _rows.Count; i++)
                _rows[i].EnableInClassList("dd-search-result--active", i == _highlight);
        }

        void RefreshResults(string query)
        {
            _results = StateSearch.Find(_context.Controller, query, MaxResults);
            // A new query means a new list; Enter has to mean its best match, not whatever
            // row number the last one had been walked down to.
            _highlight = 0;
            if (string.IsNullOrWhiteSpace(query))
            {
                ClosePopup();
                return;
            }
            OpenPopup();
            BuildPopupContent();
        }

        void OpenPopup()
        {
            if (_popup != null) return;
            _popup = new ScrollView();
            _popup.AddToClassList("dd-search-popup");
            _popup.style.position = Position.Absolute;
            _popupHost.Add(_popup);
            _popupHost.RegisterCallback<PointerDownEvent>(OnGlobalPointerDown, TrickleDown.TrickleDown);
            PositionPopup();
        }

        void PositionPopup()
        {
            // Anchor under the search field, clamped so the popup never leaves the window.
            var fieldBound = _field.worldBound;
            var hostBound = _popupHost.worldBound;
            const float width = 320;
            float left = Mathf.Max(0, Mathf.Min(fieldBound.xMin - hostBound.xMin, hostBound.width - width - 4));
            _popup.style.left = left;
            _popup.style.top = fieldBound.yMax - hostBound.yMin + 2;
            _popup.style.width = width;
            _popup.style.maxHeight = 300;
        }

        void BuildPopupContent()
        {
            _popup.Clear();
            _rows.Clear();
            if (_results.Count == 0)
            {
                var empty = new Label(L.Tr("No matches."));
                empty.AddToClassList("dd-search-empty");
                _popup.Add(empty);
                return;
            }
            foreach (var result in _results)
            {
                var captured = result;
                var row = new Label(result.label) { tooltip = result.label };
                row.AddToClassList("dd-search-result");
                row.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    NavigateTo(captured);
                    evt.StopPropagation();
                });
                _popup.Add(row);
                _rows.Add(row);
            }
            ApplyHighlight();
        }

        void NavigateTo(StateSearch.Result result)
        {
            ClosePopup();
            _field.SetValueWithoutNotify(string.Empty);
            // NavigateTo only changes layer / drill path; leave blend tree mode explicitly so
            // the state machine graph (where the hit lives) is actually visible.
            if (_context.IsViewingBlendTree)
                _context.ExitBlendTree();
            _context.NavigateTo(result.layerIndex, result.stateMachinePath, result.target);
        }

        void OnGlobalPointerDown(PointerDownEvent evt)
        {
            // A click inside the popup or the search field keeps the popup open;
            // anywhere else dismisses it.
            if (evt.target is VisualElement ve && (IsInside(ve, _popup) || IsInside(ve, this)))
                return;
            ClosePopup();
        }

        static bool IsInside(VisualElement element, VisualElement container)
        {
            for (var v = element; v != null; v = v.parent)
                if (v == container) return true;
            return false;
        }

        void ClosePopup()
        {
            if (_popup == null) return;
            _popupHost.UnregisterCallback<PointerDownEvent>(OnGlobalPointerDown, TrickleDown.TrickleDown);
            _popup.RemoveFromHierarchy();
            _popup = null;
        }
    }
}

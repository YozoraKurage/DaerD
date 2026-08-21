using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>A side panel: a header plus a scrollable IMGUI body.</summary>
    abstract class PanelBase : VisualElement
    {
        protected readonly DaerDContext Context;
        readonly IMGUIContainer _imgui;
        Vector2 _scroll;

        protected PanelBase(DaerDContext context, string header)
        {
            Context = context;
            style.flexGrow = 1;
            style.flexBasis = 0;
            style.minHeight = 60;

            var headerLabel = new Label(header);
            headerLabel.AddToClassList("ce-panel__header");
            Add(headerLabel);

            _imgui = new IMGUIContainer(Render);
            _imgui.style.flexGrow = 1;
            Add(_imgui);
        }

        public void Refresh() => _imgui.MarkDirtyRepaint();

        void Render()
        {
            if (Context == null || !Context.HasController)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField(L.Tr("No controller loaded."), EditorStyles.centeredGreyMiniLabel);
                return;
            }
            DrawPinnedHeader();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawContent();
            EditorGUILayout.EndScrollView();
            DropFocusOnClickAway();
        }

        /// <summary>
        /// A mouse-down no control claimed drops keyboard focus, the way clicking empty space in
        /// the Inspector does. IMGUI keeps a text field focused until another control takes over,
        /// so without this a field that commits on focus loss — the parameter name box — could
        /// only be committed with Enter, and clicking away silently kept the edit open.
        ///
        /// Read after everything drew and deliberately not consumed: any control that wanted this
        /// click has already used the event, so what is left here landed on nothing.
        /// </summary>
        void DropFocusOnClickAway()
        {
            if (Event.current.type != EventType.MouseDown || GUIUtility.keyboardControl == 0) return;
            GUIUtility.keyboardControl = 0;
            // Leaves keyboard input claimed for a field nothing is editing any more, which eats
            // shortcuts until the next field takes focus.
            EditorGUIUtility.editingTextField = false;
            Refresh();
        }

        /// <summary>
        /// Drawn above the scroll view, so it stays put however far the body is scrolled.
        /// Panels with a toolbar (search field, Add button) put it here.
        /// </summary>
        protected virtual void DrawPinnedHeader() { }

        protected abstract void DrawContent();
    }
}

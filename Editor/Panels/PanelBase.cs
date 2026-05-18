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
                EditorGUILayout.LabelField("No controller loaded.", EditorStyles.centeredGreyMiniLabel);
                return;
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawContent();
            EditorGUILayout.EndScrollView();
        }

        protected abstract void DrawContent();
    }
}

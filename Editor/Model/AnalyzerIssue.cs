using UnityEngine;

namespace Yozolab.DaerD
{
    enum IssueSeverity { Info, Warning, Error }

    /// <summary>Stable machine-readable issue type; <see cref="ControllerAnalyzer.CategoryLabel"/> gives its localized label.</summary>
    enum IssueKind
    {
        UnusedParameter,
        InvalidCondition,
        DeadTransition,
        SoloTransition,
        UnreachableState,
        DuplicateName,
        TerminalStates,
        WriteDefaults,
        MissingMotion,
        EmptyLayer,
        LayerWeight,
        MissingBehaviour,
        DuplicateCondition,
        DirectBlendTree,
        VrcParameters,
        ClipBindings,
        AapDriver,
        AapLayers,
    }

    class AnalyzerIssue
    {
        public IssueKind kind;
        public IssueSeverity severity;
        public string message;
        public Object context;
        /// <summary>For layer-scoped issues whose context is the controller itself, the
        /// layer the message talks about — lets Ping open that layer. -1 when unset.</summary>
        public int layerIndex = -1;
        /// <summary>Optional one-click repair. Runs its own Undo registration; the caller
        /// re-analyzes afterwards, so the delegate doesn't need to update any UI.</summary>
        public System.Action fix;
        public string fixLabel;
        public string fixTooltip;
    }
}

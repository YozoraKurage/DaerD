using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Persistent storage for graph frames (comment/group boxes drawn behind nodes) and memo
    /// notes. One hidden sub-asset per controller, created lazily on first use — controllers
    /// that never use frames or notes are left untouched.
    /// </summary>
    class GraphFrameData : ScriptableObject
    {
        [Serializable]
        public class Frame
        {
            public string title = "Frame";
            public Color color = DaerDColors.DefaultFrame;
            public Rect bounds;
            public bool moveNodesWithFrame = true;
            /// A locked frame cannot be moved, resized, renamed or deleted from the graph.
            public bool locked;
            public AnimatorStateMachine stateMachine;
        }

        /// <summary>A free-floating memo (sticky note) drawn among the nodes.</summary>
        [Serializable]
        public class Note
        {
            public string text = "Memo";
            public Color color = DaerDColors.DefaultNote;
            public Rect bounds;
            public int fontSize = 12;
            public AnimatorStateMachine stateMachine;
        }

        public List<Frame> frames = new List<Frame>();
        public List<Note> notes = new List<Note>();

        /// <summary>
        /// This controller's designated placeholder clip. Assigned to new states on creation
        /// and offered by the analyzer as the fill-in fix for states (and blend tree slots)
        /// with no motion.
        /// </summary>
        public AnimationClip emptyClip;

        /// <summary>
        /// The parameter store this controller is explicitly associated with — a
        /// VRCExpressionParameters asset or an MA Parameters component. DaerD never guesses
        /// this from the scene on its own (gimmick controllers aren't wired to any avatar);
        /// the user assigns it, optionally via an explicit Detect action.
        /// </summary>
        public UnityEngine.Object parameterStore;

        /// <summary>
        /// The VRC Expressions Menu this controller was explicitly associated with.
        ///
        /// The UI that assigned it — the slot on the home screen and the menu editor window —
        /// was removed by the user decision of 2026-08-18: editing an avatar's menu is MA Menu's
        /// territory, not DaerD's. The field stays because saved data is not rewritten to suit a
        /// UI change (ADR 0016), and it is still READ, for the two things that were never menu
        /// editing: the async-sync wizard's warning about puppet-driven targets, and carrying a
        /// parameter rename over to the controls that name it. Both are answers about a
        /// controller somebody associated a menu with while the slot existed; a controller
        /// saved since then has nothing here and gets neither, which is the honest outcome of
        /// not asking the question any more.
        /// </summary>
        public UnityEngine.Object expressionsMenu;

        /// <summary>
        /// The pin that says which gimmick prefab is this controller's home: a prefab asset and
        /// the MA Merge Animator inside it that names this controller.
        ///
        /// <para>WHY A PIN AND NOT A SEARCH.</para>
        /// The link itself is not stored anywhere — the merge's own <c>animator</c> reference IS
        /// the link, and a project sweep can find every prefab that has one. What a sweep cannot
        /// say is which of them a person means when two prefabs merge the same controller, and
        /// running that sweep is a walk over every prefab in the project, which is not a thing to
        /// do behind somebody's back (ADR 0028). So the answer is asked for once, by hand, and
        /// written down; everything else about the prefab is derived from these two references
        /// when it is needed.
        ///
        /// <para>WHY THE MERGE IS AN <see cref="UnityEngine.Object"/>.</para>
        /// The same reason <see cref="parameterStore"/> is one. A field typed as a Modular Avatar
        /// component only exists in a project that has Modular Avatar, and saved data whose SHAPE
        /// depends on an installed package is data that goes missing when somebody opens the
        /// controller without it — not "unreadable until you reinstall", but gone from the file
        /// on the next save. Held as an Object the reference is inert and intact there, and the
        /// code that has to understand what it points at lives behind the DAERD_MA guard.
        ///
        /// <para>NEVER NORMALIZED.</para>
        /// Nothing writes these fields but <see cref="SetPrefabLink"/> and
        /// <see cref="ClearPrefabLink"/>, both of which are user actions. A reference that
        /// resolves to null is something to say out loud — the prefab may be on a branch that is
        /// not checked out, Modular Avatar may be uninstalled — and a "tidy-up" that wrote null
        /// over it would turn "I cannot see this right now" into "there was never one".
        /// </summary>
        [Serializable]
        public class PrefabLink
        {
            /// <summary>Root of the prefab ASSET (never a scene instance).</summary>
            public GameObject prefab;
            /// <summary>The MA Merge Animator component inside that prefab.</summary>
            public UnityEngine.Object mergeAnimator;
        }

        public PrefabLink prefabLink = new PrefabLink();

        /// <summary>
        /// One generated async (round-robin) sync setup: the layer that hosts it plus the
        /// wizard inputs, so the wizard can re-open the setup and regenerate the layer in
        /// place instead of piling up new ones.
        /// </summary>
        [Serializable]
        public class AsyncSyncConfig
        {
            /// <summary>Root state machine of the generated layer (identifies the layer
            /// across renames and reorders).</summary>
            public AnimatorStateMachine layer;
            public string baseName = "Async";
            public int encoding;
            public float stepSeconds = 0.3f;
            /// <summary>Synced Float channels. 0 in data saved before the field existed —
            /// read it through <see cref="FloatChannelsOrDefault"/>.</summary>
            public int floatChannels = 1;
            /// <summary>Synced Bool channels. 0 in data saved before the field existed —
            /// read it through <see cref="BoolChannelsOrDefault"/>.</summary>
            public int boolChannels = 1;
            public List<string> targets = new List<string>();
            /// <summary>Legacy boolean priority marks (data saved before rates existed);
            /// superseded by <see cref="rates"/>, kept so old setups still load.</summary>
            public List<string> priorities = new List<string>();
            /// <summary>Per-target sync rates (×1 entries are simply not stored).</summary>
            public List<SyncRate> rates = new List<SyncRate>();
            /// <summary>Targets that accept an on-demand sync request (a "base/Req/target"
            /// Bool plus redirect transitions in the generated layer).</summary>
            public List<string> requests = new List<string>();
            /// <summary>Generate the remote-initialized flag. False in data saved before the
            /// field existed, which is the behaviour those setups already had.</summary>
            public bool ready;
            /// <summary>Root state machine of the Ready watcher's layer, or null when
            /// <see cref="ready"/> is off — the same "identifies the layer across renames"
            /// job <see cref="layer"/> does, for the second layer a setup can own.</summary>
            public AnimatorStateMachine readyLayer;
            /// <summary>Targets that must reach a remote's real parameters together, however
            /// far apart the pass sends them. Empty in data saved before the field existed,
            /// which is the behaviour those setups already had.</summary>
            public List<SyncGroup> groups = new List<SyncGroup>();
            /// <summary>Generate the drift-suspicion flag. False in data saved before the
            /// field existed, which is the behaviour those setups already had.</summary>
            public bool stale;
            /// <summary>Root state machine of the Stale watcher's layer, or null when
            /// <see cref="stale"/> is off.</summary>
            public AnimatorStateMachine staleLayer;
            /// <summary>Explicit cycle, as target names, one entry per step — empty when the
            /// pass is derived from the rates. Absent in data saved before the field existed,
            /// which reads as empty and so as "rates", the behaviour those setups already had.</summary>
            public List<string> schedule = new List<string>();
            /// <summary>Targets that start a slot of their own rather than sharing channels
            /// with the target before them. Empty in data saved before the field existed,
            /// which is the greedy batching those setups already had.</summary>
            public List<string> slotBreaks = new List<string>();

            /// <summary>The pass written out as sets, one entry per step — empty when the
            /// slots are batched automatically, which is what data saved before the field
            /// existed deserializes to and so the behaviour those setups already had. Takes
            /// precedence over <see cref="schedule"/>, <see cref="rates"/> and
            /// <see cref="slotBreaks"/>, all of which it answers on its own.</summary>
            public List<StepSpec> steps = new List<StepSpec>();

            /// <summary>Whether the pass may put one slot in adjacent steps, paid for with a
            /// clock phase in the index. False in data saved before the field existed, which
            /// is the pass those setups already had.</summary>
            public bool allowRepeatSteps;

            [Serializable]
            public class SyncRate
            {
                public string name;
                public int rate = 1;
            }

            /// <summary>
            /// A set of targets assigned together: the decoder holds each of them aside as it
            /// arrives, and one driver copies the whole set into the real parameters once the
            /// last one is in. A class rather than a bare list for the reason
            /// <see cref="StepSpec"/> is one — Unity does not serialize a list of lists.
            /// </summary>
            [Serializable]
            public class SyncGroup
            {
                public string name;
                public List<string> members = new List<string>();
                /// <summary>Root state machine of this group's commit layer, or null when the
                /// group was never built. Identifies the layer across renames, exactly as
                /// <see cref="AsyncSyncConfig.layer"/> does for the cycle's own.</summary>
                public AnimatorStateMachine layer;
            }

            /// <summary>
            /// One step of a cycle written out as sets: the targets that step sends. A class
            /// rather than a bare list because Unity does not serialize a list of lists — and
            /// it lives here, beside the other saved shapes, for the reason
            /// <see cref="AsyncSyncConfig.encoding"/> is an int: saved data has no business
            /// depending on the model, so the model reads this rather than the other way round.
            /// </summary>
            [Serializable]
            public class StepSpec
            {
                /// <summary>Names in any order — the slots normalize them against the target
                /// list, so a step is a set and two steps naming the same targets are one slot.</summary>
                public List<string> targets = new List<string>();
            }

            public int FloatChannelsOrDefault => floatChannels < 1 ? 1 : floatChannels;

            public int BoolChannelsOrDefault => boolChannels < 1 ? 1 : boolChannels;

            /// <summary>Rates as a lookup. Old configs carry boolean priority marks
            /// instead; those map to ×2 — the closest match to what they used to do.</summary>
            public Dictionary<string, int> RateMap()
            {
                var map = new Dictionary<string, int>();
                if (rates != null)
                    foreach (var entry in rates)
                        if (entry != null && !string.IsNullOrEmpty(entry.name) && entry.rate > 1)
                            map[entry.name] = entry.rate;
                if (map.Count == 0 && priorities != null)
                    foreach (var name in priorities)
                        if (!string.IsNullOrEmpty(name))
                            map[name] = 2;
                return map;
            }

            public static List<SyncRate> ToRateEntries(Dictionary<string, int> map)
            {
                var entries = new List<SyncRate>();
                if (map != null)
                    foreach (var pair in map)
                        if (pair.Value > 1)
                            entries.Add(new SyncRate { name = pair.Key, rate = pair.Value });
                return entries;
            }
        }

        public List<AsyncSyncConfig> asyncSyncs = new List<AsyncSyncConfig>();

        /// <summary>
        /// One generated DBT (AAP) gadget: the layer hosting it, the blend tree child it hung
        /// there, and the wizard inputs. Same reason the async-sync setups are stored — a
        /// gadget expands into a wall of trees and clips nobody can read back, so the inputs
        /// are the only description of it that survives. The wizard re-opens a saved gadget to
        /// edit and regenerate it, and the C# exporter writes it back as the call that made it.
        /// </summary>
        [Serializable]
        public class AapGadgetConfig
        {
            /// <summary>Root state machine of the hosting DBT layer (identifies the layer
            /// across renames and reorders).</summary>
            public AnimatorStateMachine layer;
            /// <summary>The child this gadget added to that layer's root Direct tree — a blend
            /// tree, held as the Motion the root's children compare against, and the handle on
            /// everything it built: the rest of the gadget hangs off it.</summary>
            public Motion tree;
            /// <summary>(int)AapGadgets.Kind, an int for the same reason
            /// <see cref="AsyncSyncConfig.encoding"/> is one: this holder is saved data and has
            /// no business depending on the gadget model's enum.</summary>
            public int kind;
            public string inputA;
            /// <summary>Second input; only meaningful for the binary kinds.</summary>
            public string inputB;
            /// <summary>Result parameter, and the key this record is saved under: one gadget
            /// per output name, so regenerating replaces its own entry instead of adding a
            /// second one describing the same thing.</summary>
            public string output;
            public float rangeMin = -1f;
            public float rangeMax = 1f;
            public float inMin = 0f;
            public float inMax = 1f;
            public float threshold = 0.5f;
            public string smoothing;
            public float smoothingDefault = 0.9f;
            /// <summary>Lut1D only: the baked function. A copy of what the caller passed, so
            /// editing the curve afterwards can't rewrite the record of what was baked.</summary>
            public AnimationCurve curve;
            public int lutSamples = 33;
            public int bufferFrames = 1;
            public int atan2Directions = 16;

            /// <summary>Whether <paramref name="name"/> is a parameter this gadget owns — the
            /// output itself or anything under it ("Out", "Out/Shift", "Out/2"), which is the
            /// namespace contract the builders keep to. Removing a gadget sweeps exactly that,
            /// and regenerating one has to see those names as free rather than as collisions.
            /// </summary>
            public bool Owns(string name) =>
                !string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(name)
                && (name == output || name.StartsWith(output + "/", StringComparison.Ordinal));
        }

        public List<AapGadgetConfig> aapGadgets = new List<AapGadgetConfig>();

        /// <summary>
        /// One generated object gadget: something in the pinned gimmick prefab that this
        /// controller animates, kept as the inputs it was made from so it can be edited,
        /// regenerated and swept — the same bargain the DBT gadgets and the sync setups make
        /// (ADR 0013), for a family whose subject is a hierarchy rather than a parameter.
        ///
        /// <para>WHY THE TARGETS ARE REFERENCES AND NOT PATHS.</para>
        /// A path string dies the first time somebody renames an object, silently and without
        /// anything noticing (ADR 0044). A reference into the prefab survives renames,
        /// reparenting and re-saves — measured in a headless probe — so the reference is what is
        /// saved, and the path an animation curve needs is derived from it every time the gadget
        /// is applied. That is also why the merge itself is not recorded here: the pin
        /// (<see cref="PrefabLink"/>) already says which merge the paths are relative to, and a
        /// second copy of that answer could disagree with it.
        ///
        /// <para>WHY IT IS FLAT AND FULL OF ints.</para>
        /// <see cref="kind"/> and <see cref="mode"/> are the model's enums as numbers for the
        /// reason <see cref="AapGadgetConfig.kind"/> is one: saved data has no business depending
        /// on the model. Nothing here is typed as a Modular Avatar type either — a field whose
        /// SHAPE needs an installed package is a field that goes missing when the controller is
        /// opened without it, which is the same rule <see cref="parameterStore"/> follows.
        /// Evolution is by appending fields with defaults; nothing is ever renamed or reordered.
        ///
        /// <para>WHAT IS NOT HERE.</para>
        /// The target layer of a DBT-wired gadget is <see cref="layer"/> itself, so there is no
        /// layer index to save (an index is not stable across a reorder anyway). The next kinds
        /// of the family — a menu item, a position sync — add fields, not a second list: they
        /// share the way a target is pointed at, the way ownership is swept and the way clips
        /// are produced, and differ only in what they build.
        /// </summary>
        [Serializable]
        public class ObjectGadgetConfig
        {
            /// <summary>(int)ObjectGadgets.Kind.</summary>
            public int kind;
            /// <summary>Base name for the layer, the clips and the tree.</summary>
            public string name;
            /// <summary>The parameter that drives it, and the key this record is saved under:
            /// one object gadget per parameter, so regenerating replaces its own entry instead
            /// of adding a second one describing the same thing.</summary>
            public string parameter;
            /// <summary>Whether generating created that parameter. The whole basis on which
            /// removing the gadget may delete it again: a parameter that was already there when
            /// the gadget was built belongs to somebody else.</summary>
            public bool createdParameter;
            /// <summary>(int)ToggleBuilder.Mode — a layer of its own, or a child of a Direct
            /// blend tree layer's root.</summary>
            public int mode;
            public bool defaultOn;
            /// <summary>Whether the parameter is also declared to the avatar through the
            /// controller's parameter store. Off is a real answer: a gadget driven by another
            /// layer rather than by a menu has nothing to declare.</summary>
            public bool declare = true;
            public List<ObjectTargetRecord> targets = new List<ObjectTargetRecord>();
            /// <summary>Root state machine of the layer this gadget lives in — the layer it
            /// added (Layer mode) or the Direct blend tree layer hosting its tree. Identifies
            /// the layer across renames and reorders, as every other record here does.</summary>
            public AnimatorStateMachine layer;
            /// <summary>DBT mode: the child hung off that layer's root Direct tree. Null for a
            /// gadget wired as a layer, which has no tree.</summary>
            public Motion tree;
            public ClipOutput onClip = new ClipOutput();
            public ClipOutput offClip = new ClipOutput();
            /// <summary>Objects and components this gadget put INSIDE the prefab, held by
            /// reference because that is the whole of DaerD's claim on them (ADR 0045).
            /// Reserved: no kind places anything yet, so this is empty in every record P2
            /// writes, and the mechanism that applies and sweeps it arrives with the first kind
            /// that needs one.</summary>
            public List<PlacedArtifact> placed = new List<PlacedArtifact>();
        }

        /// <summary>One object a gadget animates, and how.</summary>
        [Serializable]
        public class ObjectTargetRecord
        {
            /// <summary>The object itself, somewhere inside the pinned prefab. The path is
            /// derived from it when a curve needs one and is never saved (ADR 0044).</summary>
            public GameObject target;
            /// <summary>Unchecked inverts this target: every row swaps its ON and OFF value.</summary>
            public bool activeWhenOn = true;
            /// <summary>Key GameObject.m_IsActive itself. Off when only components toggle.</summary>
            public bool toggleActive = true;
            public List<BindingRecord> bindings = new List<BindingRecord>();
        }

        /// <summary>One extra property on a target: a component's enabled flag, or a blendshape
        /// weight with its own OFF/ON values. The component is named rather than typed —
        /// a serialized System.Type is not a thing, and the short name is what
        /// <c>ToggleBuilder.FindComponentType</c> resolves against the types this project
        /// actually has (a PhysBone binding in a project without the SDK is a named refusal,
        /// not a corrupted record).</summary>
        [Serializable]
        public class BindingRecord
        {
            public string typeName;
            /// <summary>"m_Enabled" or "blendShape.&lt;name&gt;".</summary>
            public string property;
            public float offValue;
            public float onValue = 1f;
        }

        /// <summary>
        /// One side of a gadget's animation: the clip, and the rows the gadget put in it.
        ///
        /// <see cref="written"/> is a snapshot of what the last generate wrote, kept even for a
        /// clip DaerD owns outright. Its reason is ADR 0046 — a user-supplied clip is edited by
        /// its owner too, so regenerating has to remove the rows IT wrote last time rather than
        /// the rows the targets imply now, which is a different set the moment a target is
        /// renamed. Keeping the ledger for owned clips as well is what stops that from being a
        /// second code path: one bookkeeping, whoever the clip belongs to.
        /// </summary>
        [Serializable]
        public class ClipOutput
        {
            public AnimationClip clip;
            /// <summary>The clip is the user's asset, not a sub-asset DaerD made. Sweeping the
            /// gadget then removes its rows and never the file.</summary>
            public bool userProvided;
            public List<WrittenRow> written = new List<WrittenRow>();
        }

        /// <summary>One curve row a generate wrote, as the triple that identifies a curve.
        /// The type is a short name, "GameObject" for the m_IsActive rows.</summary>
        [Serializable]
        public class WrittenRow
        {
            public string path;
            public string typeName;
            public string property;
        }

        /// <summary>Something a gadget added inside the prefab. A holder rather than a bare
        /// reference so the record can grow fields (what it is, where it came from) without
        /// rewriting every saved list.</summary>
        [Serializable]
        public class PlacedArtifact
        {
            public UnityEngine.Object reference;
        }

        public List<ObjectGadgetConfig> objectGadgets = new List<ObjectGadgetConfig>();

        /// <summary>
        /// One per-state sync request: while the avatar is in <see cref="state"/>, the async
        /// sync setup named <see cref="baseName"/> is asked to send <see cref="targets"/> out
        /// of turn. The record is the authoring side; the runtime side is a Parameter Driver
        /// on the state (see SyncRequestBuilder), and this is what lets DaerD re-materialize
        /// and edit that driver as a component instead of raw driver rows.
        /// </summary>
        [Serializable]
        public class SyncRequest
        {
            public AnimatorState state;
            /// <summary>Base name of the async-sync setup the request talks to.</summary>
            public string baseName;
            /// <summary>Targets to request, a subset of the setup's multiplexed targets.</summary>
            public List<string> targets = new List<string>();
        }

        public List<SyncRequest> syncRequests = new List<SyncRequest>();

        /// <summary>Layers generated (and regenerated) by a C# recipe — the layer list shows
        /// them with a "C#" badge so hand-edits there read as "will be overwritten".</summary>
        [Serializable]
        public class CodeOwnedLayer
        {
            public AnimatorStateMachine layer;
            public UnityEngine.Object recipe;
        }

        public List<CodeOwnedLayer> codeOwned = new List<CodeOwnedLayer>();

        /// <summary>Replaces <paramref name="recipe"/>'s claims with the given machines.</summary>
        public static void SetCodeOwned(AnimatorController controller,
            List<AnimatorStateMachine> machines, UnityEngine.Object recipe)
        {
            if (controller == null || recipe == null) return;
            var data = GetOrCreate(controller);
            if (data == null) return;
            Undo.RegisterCompleteObjectUndo(data, "Record Recipe Layers");
            data.codeOwned.RemoveAll(entry => entry == null || entry.layer == null
                || entry.recipe == null || entry.recipe == recipe);
            foreach (var machine in machines)
                if (machine != null)
                    data.codeOwned.Add(new CodeOwnedLayer { layer = machine, recipe = recipe });
            EditorUtility.SetDirty(data);
        }

        /// <summary>
        /// Re-points every record keyed by a layer's state machine — async-sync setups,
        /// code-owned marks, frames, notes — from a replaced machine to its successor.
        /// A recipe regenerates a layer by destroy-and-recreate; without this, the SYNC
        /// badge, the wizard's saved setup and the layer's annotations all die with the
        /// old machine. Matched by instance ID captured before the destroy: destroyed
        /// wrappers still answer GetInstanceID, while Unity's == would lump every dead
        /// object together.
        /// </summary>
        public static void RemapStateMachine(AnimatorController controller, int oldMachineId,
            AnimatorStateMachine newMachine)
        {
            var data = Find(controller);
            if (data == null || newMachine == null) return;
            if (data.RemapMachineReferences(oldMachineId, newMachine))
            {
                Undo.RegisterCompleteObjectUndo(data, "Remap Layer Records");
                EditorUtility.SetDirty(data);
            }
        }

        /// <summary>Instance-level core of <see cref="RemapStateMachine"/> (testable without
        /// a persisted controller). Returns whether anything was re-pointed.</summary>
        public bool RemapMachineReferences(int oldMachineId, AnimatorStateMachine newMachine)
        {
            bool changed = false;
            foreach (var config in asyncSyncs)
                if (config != null && !ReferenceEquals(config.layer, null)
                    && config.layer.GetInstanceID() == oldMachineId)
                {
                    config.layer = newMachine;
                    changed = true;
                }
            // Only the layer follows: a gadget's tree died with the machine that held it, and
            // the entry is pruned on the next read rather than re-pointed at nothing.
            //
            // Object gadgets are deliberately NOT here, and it is not an omission. A Layer-wired
            // object gadget IS its layer, and re-pointing its record at the machine a recipe just
            // built would make the next regenerate delete that recipe's layer as if it were the
            // gadget's own. A tree-wired one would be worse off still: the layer it hangs in is
            // rebuilt around it, so following the layer would leave a record pointing at a tree
            // that is gone while a copy of that tree stands in the layer — and editing the gadget
            // would then add a second child beside the copy. Losing the record instead costs one
            // gadget that has to be rebuilt, which is the cheaper mistake — and it is what
            // happens: the record is pruned on the next read (see ObjectGadgetRecords), with
            // ControllerRecipe.Generate naming what it lost. What keeps a recipe's own toggles
            // out of that path is the export claiming them child by child, so the layer they live
            // in is declared without them and their call puts them back.
            foreach (var config in aapGadgets)
                if (config != null && !ReferenceEquals(config.layer, null)
                    && config.layer.GetInstanceID() == oldMachineId)
                {
                    config.layer = newMachine;
                    changed = true;
                }
            foreach (var entry in codeOwned)
                if (entry != null && !ReferenceEquals(entry.layer, null)
                    && entry.layer.GetInstanceID() == oldMachineId)
                {
                    entry.layer = newMachine;
                    changed = true;
                }
            foreach (var frame in frames)
                if (frame != null && !ReferenceEquals(frame.stateMachine, null)
                    && frame.stateMachine.GetInstanceID() == oldMachineId)
                {
                    frame.stateMachine = newMachine;
                    changed = true;
                }
            foreach (var note in notes)
                if (note != null && !ReferenceEquals(note.stateMachine, null)
                    && note.stateMachine.GetInstanceID() == oldMachineId)
                {
                    note.stateMachine = newMachine;
                    changed = true;
                }
            return changed;
        }

        /// <summary>Live machine → recipe map (stale entries pruned on read).</summary>
        public static Dictionary<AnimatorStateMachine, UnityEngine.Object> GetCodeOwned(
            AnimatorController controller)
        {
            var map = new Dictionary<AnimatorStateMachine, UnityEngine.Object>();
            var data = Find(controller);
            if (data == null) return map;
            data.codeOwned.RemoveAll(entry => entry == null || entry.layer == null || entry.recipe == null);
            foreach (var entry in data.codeOwned)
                map[entry.layer] = entry.recipe;
            return map;
        }

        /// <summary>Live configs (entries whose layer was deleted are pruned).</summary>
        public List<AsyncSyncConfig> AsyncSyncs()
        {
            asyncSyncs.RemoveAll(config => config == null || config.layer == null);
            return new List<AsyncSyncConfig>(asyncSyncs);
        }

        /// <summary>Adds or replaces the config for its layer.</summary>
        public void SaveAsyncSync(AsyncSyncConfig config)
        {
            if (config == null || config.layer == null) return;
            Undo.RegisterCompleteObjectUndo(this, "Save Async Sync Config");
            asyncSyncs.RemoveAll(existing => existing == null || existing.layer == null
                || existing.layer == config.layer);
            asyncSyncs.Add(config);
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Drops one saved setup. The pruning in <see cref="AsyncSyncs"/> answers a different
        /// question — it forgets a record whose layer somebody else deleted — and answering it
        /// is not the same as saying "this setup is gone": a caller that takes the layer away
        /// itself has to say so, or the record survives until the next read and the setup goes
        /// on owning a base name and a namespace in the meantime.
        ///
        /// Matched by identity first and by layer second, which is the key
        /// <see cref="SaveAsyncSync"/> replaces on. Identity is what lets this be called before
        /// the layers are removed — and it has to be, because a record whose layer is already
        /// gone can no longer be matched by it.
        /// </summary>
        public void RemoveAsyncSync(AsyncSyncConfig config)
        {
            if (config == null) return;
            Undo.RegisterCompleteObjectUndo(this, "Remove Async Sync Config");
            asyncSyncs.RemoveAll(existing => existing == null || existing == config
                || (config.layer != null && existing.layer == config.layer));
            EditorUtility.SetDirty(this);
        }

        public static List<AsyncSyncConfig> GetAsyncSyncs(AnimatorController controller)
        {
            var data = Find(controller);
            return data != null ? data.AsyncSyncs() : new List<AsyncSyncConfig>();
        }

        public static void SaveAsyncSync(AnimatorController controller, AsyncSyncConfig config)
        {
            var data = GetOrCreate(controller);
            if (data != null) data.SaveAsyncSync(config);
        }

        /// <summary>Removing must not create the holder on a controller that never had one —
        /// same reason <see cref="RemoveGadget(AnimatorController, string)"/> uses
        /// <see cref="Find"/>.</summary>
        public static void RemoveAsyncSync(AnimatorController controller, AsyncSyncConfig config)
        {
            var data = Find(controller);
            if (data != null) data.RemoveAsyncSync(config);
        }

        /// <summary>The saved setup with this base name, or null. Base names are how sync
        /// requests refer to a setup — they survive the layer being regenerated.</summary>
        public static AsyncSyncConfig FindAsyncSync(AnimatorController controller, string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) return null;
            foreach (var config in GetAsyncSyncs(controller))
                if (config.baseName == baseName)
                    return config;
            return null;
        }

        // ---- DBT gadgets ------------------------------------------------------

        /// <summary>Live gadget configs. An entry whose layer or whose tree was deleted
        /// describes nothing any more — the gadget is gone with it — so it is pruned rather
        /// than offered for regeneration.</summary>
        public List<AapGadgetConfig> Gadgets()
        {
            aapGadgets.RemoveAll(config => config == null || config.layer == null || config.tree == null);
            return new List<AapGadgetConfig>(aapGadgets);
        }

        /// <summary>Adds or replaces the config for its output name.</summary>
        public void SaveGadget(AapGadgetConfig config)
        {
            if (config == null || string.IsNullOrEmpty(config.output)) return;
            Undo.RegisterCompleteObjectUndo(this, "Save DBT Gadget Config");
            aapGadgets.RemoveAll(existing => existing == null || existing.output == config.output);
            aapGadgets.Add(config);
            EditorUtility.SetDirty(this);
        }

        public void RemoveGadget(string output)
        {
            if (string.IsNullOrEmpty(output)) return;
            Undo.RegisterCompleteObjectUndo(this, "Remove DBT Gadget Config");
            aapGadgets.RemoveAll(existing => existing == null || existing.output == output);
            EditorUtility.SetDirty(this);
        }

        public static List<AapGadgetConfig> GetGadgets(AnimatorController controller)
        {
            var data = Find(controller);
            return data != null ? data.Gadgets() : new List<AapGadgetConfig>();
        }

        public static void SaveGadget(AnimatorController controller, AapGadgetConfig config)
        {
            var data = GetOrCreate(controller);
            if (data != null) data.SaveGadget(config);
        }

        public static void RemoveGadget(AnimatorController controller, string output)
        {
            var data = Find(controller);
            if (data != null) data.RemoveGadget(output);
        }

        // ---- object gadgets ---------------------------------------------------

        /// <summary>
        /// Live object gadget records. An entry whose layer is gone describes nothing that can
        /// still be edited or swept — a Layer-wired gadget IS that layer, and a tree-wired one
        /// hangs off the root Direct tree inside it — so it is pruned rather than offered.
        ///
        /// A record whose TARGETS went missing is not pruned. That is the difference between a
        /// gadget that no longer exists and one whose subject moved out from under it, and the
        /// second is something to report by name rather than to forget (ADR 0044).
        /// </summary>
        public List<ObjectGadgetConfig> ObjectGadgetRecords()
        {
            objectGadgets.RemoveAll(config => config == null || config.layer == null);
            return new List<ObjectGadgetConfig>(objectGadgets);
        }

        /// <summary>Adds or replaces the record for its parameter.</summary>
        public void SaveObjectGadget(ObjectGadgetConfig config)
        {
            if (config == null || string.IsNullOrEmpty(config.parameter)) return;
            Undo.RegisterCompleteObjectUndo(this, "Save Object Gadget Config");
            objectGadgets.RemoveAll(existing => existing == null
                || existing.parameter == config.parameter);
            objectGadgets.Add(config);
            EditorUtility.SetDirty(this);
        }

        public void RemoveObjectGadget(string parameter)
        {
            if (string.IsNullOrEmpty(parameter)) return;
            Undo.RegisterCompleteObjectUndo(this, "Remove Object Gadget Config");
            objectGadgets.RemoveAll(existing => existing == null || existing.parameter == parameter);
            EditorUtility.SetDirty(this);
        }

        public static List<ObjectGadgetConfig> GetObjectGadgets(AnimatorController controller)
        {
            var data = Find(controller);
            return data != null ? data.ObjectGadgetRecords() : new List<ObjectGadgetConfig>();
        }

        public static void SaveObjectGadget(AnimatorController controller, ObjectGadgetConfig config)
        {
            var data = GetOrCreate(controller);
            if (data != null) data.SaveObjectGadget(config);
        }

        public static void RemoveObjectGadget(AnimatorController controller, string parameter)
        {
            var data = Find(controller);
            if (data != null) data.RemoveObjectGadget(parameter);
        }

        // ---- per-state sync requests -----------------------------------------

        /// <summary>Live sync requests (entries whose state was deleted are pruned).</summary>
        public List<SyncRequest> SyncRequests()
        {
            syncRequests.RemoveAll(request => request == null || request.state == null);
            return new List<SyncRequest>(syncRequests);
        }

        /// <summary>Adds or replaces the request for its (state, base name) pair.</summary>
        public void SaveSyncRequest(SyncRequest request)
        {
            if (request == null || request.state == null) return;
            Undo.RegisterCompleteObjectUndo(this, "Save Sync Request");
            syncRequests.RemoveAll(existing => existing == null || existing.state == null
                || (existing.state == request.state && existing.baseName == request.baseName));
            syncRequests.Add(request);
            EditorUtility.SetDirty(this);
        }

        public void RemoveSyncRequest(AnimatorState state, string baseName)
        {
            Undo.RegisterCompleteObjectUndo(this, "Remove Sync Request");
            syncRequests.RemoveAll(existing => existing == null || existing.state == null
                || (existing.state == state && existing.baseName == baseName));
            EditorUtility.SetDirty(this);
        }

        public static List<SyncRequest> GetSyncRequests(AnimatorController controller)
        {
            var data = Find(controller);
            return data != null ? data.SyncRequests() : new List<SyncRequest>();
        }

        public static List<SyncRequest> GetSyncRequests(AnimatorController controller,
            AnimatorState state)
        {
            var requests = GetSyncRequests(controller);
            requests.RemoveAll(request => request.state != state);
            return requests;
        }

        public static void SaveSyncRequest(AnimatorController controller, SyncRequest request)
        {
            var data = GetOrCreate(controller);
            if (data != null) data.SaveSyncRequest(request);
        }

        public static void RemoveSyncRequest(AnimatorController controller, AnimatorState state,
            string baseName)
        {
            var data = Find(controller);
            if (data != null) data.RemoveSyncRequest(state, baseName);
        }

        public static AnimationClip GetEmptyClip(AnimatorController controller)
        {
            var data = Find(controller);
            return data != null ? data.emptyClip : null;
        }

        public static void SetEmptyClip(AnimatorController controller, AnimationClip clip)
        {
            // Clearing must not create the holder on controllers that never had one.
            var data = clip == null ? Find(controller) : GetOrCreate(controller);
            if (data == null || data.emptyClip == clip) return;
            Undo.RegisterCompleteObjectUndo(data, "Set Empty Clip");
            data.emptyClip = clip;
            EditorUtility.SetDirty(data);
        }

        /// <summary>The designated Empty clip, created on first use: a 1-second clip animating a
        /// binding that exists on no avatar (a no-op at runtime), stored inside the .controller
        /// and registered as this controller's Empty clip. In-memory controllers have no asset
        /// to store it in — null, and callers leave their states motion-less.</summary>
        public static AnimationClip EnsureEmptyClip(AnimatorController controller)
        {
            // An already designated clip is the user's choice — kept even at zero length,
            // rather than silently replaced by a generated one.
            var designated = GetEmptyClip(controller);
            if (designated != null) return designated;

            var path = controller != null ? AssetDatabase.GetAssetPath(controller) : null;
            if (string.IsNullOrEmpty(path)) return null;

            var clip = new AnimationClip { name = "Empty" };
            // Curve on a path no avatar has, so playing the clip changes nothing; it exists only
            // to give the clip a length, which normalized exit times need to divide by.
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("DaerD Empty", typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0f, 1f, 1f));
            Undo.RegisterCreatedObjectUndo(clip, "Create Empty Clip");
            AssetDatabase.AddObjectToAsset(clip, controller);
            EditorUtility.SetDirty(controller);
            // The Project window lists sub-assets from the imported artifact, not from the
            // objects in memory, so the clip stays invisible there until the file is written
            // and reimported.
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
            SetEmptyClip(controller, clip);
            return clip;
        }

        public static UnityEngine.Object GetParameterStore(AnimatorController controller)
        {
            var data = Find(controller);
            return data != null ? data.parameterStore : null;
        }

        public static void SetParameterStore(AnimatorController controller, UnityEngine.Object store)
        {
            var data = store == null ? Find(controller) : GetOrCreate(controller);
            if (data == null || data.parameterStore == store) return;
            Undo.RegisterCompleteObjectUndo(data, "Set Parameter Store");
            data.parameterStore = store;
            EditorUtility.SetDirty(data);
        }

        /// <summary>Read-only by design — see <see cref="expressionsMenu"/> for why there is no
        /// setter beside this one any more.</summary>
        public static UnityEngine.Object GetExpressionsMenu(AnimatorController controller)
        {
            var data = Find(controller);
            return data != null ? data.expressionsMenu : null;
        }

        /// <summary>
        /// The saved pin, or null when this controller has no holder at all. The record handed
        /// back is the live one: read it, and go through <see cref="SetPrefabLink"/> /
        /// <see cref="ClearPrefabLink"/> to change it (see <see cref="PrefabLink"/> for why
        /// nothing else may write those two fields).
        /// </summary>
        public static PrefabLink GetPrefabLink(AnimatorController controller)
        {
            var data = Find(controller);
            return data != null ? data.prefabLink : null;
        }

        /// <summary>Pins the prefab and the merge inside it as this controller's home. Only ever
        /// called from an explicit user action.</summary>
        public static void SetPrefabLink(AnimatorController controller, GameObject prefab,
            UnityEngine.Object mergeAnimator)
        {
            var data = GetOrCreate(controller);
            if (data == null) return;
            // Data saved before the field existed deserializes the list-less holder with a null
            // record rather than an empty one.
            if (data.prefabLink == null) data.prefabLink = new PrefabLink();
            if (data.prefabLink.prefab == prefab && data.prefabLink.mergeAnimator == mergeAnimator)
                return;
            Undo.RegisterCompleteObjectUndo(data, "Set Prefab Link");
            data.prefabLink.prefab = prefab;
            data.prefabLink.mergeAnimator = mergeAnimator;
            EditorUtility.SetDirty(data);
        }

        /// <summary>
        /// Drops the pin. Clearing must not create a holder on a controller that never had one,
        /// which is why this asks <see cref="Find"/> rather than <see cref="GetOrCreate"/>.
        ///
        /// The "already empty" test is REFERENCE emptiness: a slot still holding a reference that
        /// no longer resolves is not empty, and Unity's == reports it as null exactly like an
        /// unset field. Skipping the write there would leave a dead pin that the UI can see and
        /// this method cannot clear.
        /// </summary>
        public static void ClearPrefabLink(AnimatorController controller)
        {
            var data = Find(controller);
            if (data == null || data.prefabLink == null) return;
            if (ReferenceEquals(data.prefabLink.prefab, null)
                && ReferenceEquals(data.prefabLink.mergeAnimator, null)) return;
            Undo.RegisterCompleteObjectUndo(data, "Clear Prefab Link");
            data.prefabLink.prefab = null;
            data.prefabLink.mergeAnimator = null;
            EditorUtility.SetDirty(data);
        }

        // ---- finding the holder ----------------------------------------------
        //
        // Everything DaerD stores against a controller — async sync setups, gadget configs,
        // the recipe-owned layers, the Empty clip, the parameter store — is reached through
        // Find, and several panels ask during their repaint. The lookup underneath loads EVERY
        // sub-asset of the .controller, and on a DaerD-built one that is every clip, tree,
        // state, transition and behaviour in it. Moving the mouse across the layer list used
        // to do that twice a frame.
        //
        // So the answer is remembered. What can change it is narrow: DaerD creating the holder
        // (written through below), or the asset itself changing on disk (the postprocessor at
        // the bottom of this file drops everything). A domain reload clears the table outright.

        static readonly Dictionary<AnimatorController, GraphFrameData> s_holders =
            new Dictionary<AnimatorController, GraphFrameData>(ControllerIdentity.Instance);

        /// <summary>The frame holder already stored on the controller, or null when none exists.</summary>
        public static GraphFrameData Find(AnimatorController controller)
        {
            if (controller == null) return null;
            if (s_holders.TryGetValue(controller, out var cached))
            {
                if (cached != null) return cached;
                // A real null is an answer ("this controller has none"). A reference that is
                // non-null but fails Unity's check is a holder that has since been destroyed,
                // and that one has to be looked up again.
                if (ReferenceEquals(cached, null)) return null;
            }
            var found = Scan(controller);
            s_holders[controller] = found;
            return found;
        }

        static GraphFrameData Scan(AnimatorController controller)
        {
            var path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is GraphFrameData data)
                    return data;
            return null;
        }

        /// <summary>Drops what Find remembered. Called for you when assets change; exposed for
        /// tests, which build and delete controllers faster than the importer reports.</summary>
        public static void ForgetHolders() => s_holders.Clear();

        /// <summary>
        /// Reference identity rather than Unity's equality, which reports a destroyed Object as
        /// equal to null — and so to any other destroyed one. Nothing about a lookup table
        /// should rest on that conflation: the question here is which object, not whether it is
        /// still alive, and aliveness is asked separately where it matters.
        /// </summary>
        sealed class ControllerIdentity : IEqualityComparer<AnimatorController>
        {
            public static readonly ControllerIdentity Instance = new ControllerIdentity();
            public bool Equals(AnimatorController a, AnimatorController b) => ReferenceEquals(a, b);
            public int GetHashCode(AnimatorController controller) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(controller);
        }

        /// <summary>Finds or creates the frame holder. In-memory controllers get a non-persisted instance.</summary>
        public static GraphFrameData GetOrCreate(AnimatorController controller)
        {
            var existing = Find(controller);
            if (existing != null) return existing;

            var data = CreateInstance<GraphFrameData>();
            data.name = "DaerD Frames";
            data.hideFlags = HideFlags.HideInHierarchy;
            var path = controller != null ? AssetDatabase.GetAssetPath(controller) : null;
            if (!string.IsNullOrEmpty(path))
            {
                AssetDatabase.AddObjectToAsset(data, controller);
                EditorUtility.SetDirty(controller);
                // Written through, so the cached "this controller has none" does not outlive
                // the holder that has just been added to it.
                s_holders[controller] = data;
            }
            // An in-memory controller keeps none of this: it has no asset to hang the holder
            // on, so every call builds a fresh one and nothing written to it is ever read back.
            // That is why anything testing saved configs needs a controller on disk.
            return data;
        }

        /// <summary>The frames belonging to one state machine view (frames are per-graph, not per-layer).</summary>
        public List<Frame> FramesIn(AnimatorStateMachine sm)
        {
            var result = new List<Frame>();
            if (sm == null) return result;
            foreach (var frame in frames)
                if (frame != null && frame.stateMachine == sm)
                    result.Add(frame);
            return result;
        }

        public Frame AddFrame(AnimatorStateMachine sm, Rect bounds, string title = "Frame")
        {
            Undo.RegisterCompleteObjectUndo(this, "Create Frame");
            var frame = new Frame { title = title, bounds = bounds, stateMachine = sm };
            frames.Add(frame);
            EditorUtility.SetDirty(this);
            return frame;
        }

        public void RemoveFrame(Frame frame)
        {
            if (frame == null) return;
            Undo.RegisterCompleteObjectUndo(this, "Delete Frame");
            frames.Remove(frame);
            EditorUtility.SetDirty(this);
        }

        /// <summary>The notes belonging to one state machine view.</summary>
        public List<Note> NotesIn(AnimatorStateMachine sm)
        {
            var result = new List<Note>();
            if (sm == null) return result;
            foreach (var note in notes)
                if (note != null && note.stateMachine == sm)
                    result.Add(note);
            return result;
        }

        public Note AddNote(AnimatorStateMachine sm, Rect bounds)
        {
            Undo.RegisterCompleteObjectUndo(this, "Create Note");
            var note = new Note { bounds = bounds, stateMachine = sm };
            notes.Add(note);
            EditorUtility.SetDirty(this);
            return note;
        }

        public void RemoveNote(Note note)
        {
            if (note == null) return;
            Undo.RegisterCompleteObjectUndo(this, "Delete Note");
            notes.Remove(note);
            EditorUtility.SetDirty(this);
        }
    }

    /// <summary>
    /// The one thing <see cref="GraphFrameData.Find"/>'s table cannot see coming: a controller
    /// changing on disk. Pulling a branch can add a holder to a controller DaerD has already
    /// decided has none, and no code of ours runs in between. Clearing the whole table costs
    /// nothing — it is refilled by the next lookup — so there is no need to work out which
    /// entry the import touched.
    /// </summary>
    class GraphFrameDataImportWatcher : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted,
            string[] moved, string[] movedFrom) => GraphFrameData.ForgetHolders();
    }
}

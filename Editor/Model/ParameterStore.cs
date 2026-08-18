using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
#if DAERD_MA
using MaConfig = nadena.dev.modular_avatar.core.ParameterConfig;
using MaParameters = nadena.dev.modular_avatar.core.ModularAvatarParameters;
using MaSyncType = nadena.dev.modular_avatar.core.ParameterSyncType;
#endif
#if DAERD_MA && DAERD_VRC
using MaMergeAnimator = nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator;
#endif

namespace Yozolab.DaerD
{
    /// <summary>
    /// Uniform access to "the thing that declares this controller's expression parameters":
    /// a VRCExpressionParameters asset (avatar workflow) or a Modular Avatar "MA Parameters"
    /// component (NDMF gimmick workflow). The association is explicit — stored per controller
    /// in <see cref="GraphFrameData"/> and assigned by the user; <see cref="DetectFor"/> only
    /// runs on an explicit user action and only returns EXACT matches (never "the only
    /// avatar in the scene").
    /// </summary>
    abstract class ParameterStore
    {
        public abstract Object Target { get; }
        /// <summary>Short label shown next to the slot ("VRC Params" / "MA Params").</summary>
        public abstract string Kind { get; }
        /// <summary>Synced-bit capacity, or -1 when the store has no own budget (an MA
        /// component contributes to the avatar's total, which DaerD can't see).</summary>
        public abstract int Capacity();
        public abstract List<VrcExpressionParameters.Entry> Read();

        /// <summary>
        /// What the BUILT avatar will call these parameters, for the names where that is not
        /// what the store calls them. Empty for a store whose names are already final, which is
        /// every store but one.
        ///
        /// <para>WHY THIS IS BESIDE <see cref="Read"/> AND NOT A FIELD ON THE ENTRY.</para>
        /// An MA Parameters component can declare a parameter INTERNAL, and an internal
        /// parameter is renamed on the way into the avatar so that two copies of the same
        /// gimmick do not fight over one name. The store keeps saying "Hat"; what travels is
        /// "Hat$-8842". Everything DaerD says about a synced parameter — what to put on the
        /// wire, what a recording will be matched against — is wrong by that difference, and
        /// the entry shape is the wrong place to carry it: the same
        /// <see cref="VrcExpressionParameters.Entry"/> is what the VRC asset backend reads and
        /// writes, what the sync window diffs and what <see cref="WriteAll"/> copies field by
        /// field, so a field only one backend can ever fill would have to be decided about at
        /// every one of those places. A separate question, asked by the two callers that need
        /// the answer, leaves all of them alone.
        ///
        /// Asked as a map rather than name by name because working the answer out means walking
        /// the object's ancestors and asking every renaming component on the way; a caller with
        /// a list of names should pay for that once.
        /// </summary>
        public virtual Dictionary<string, string> EffectiveNames() =>
            new Dictionary<string, string>();

        /// <summary>Aligns the store to the given entries (used by the sync command). Order
        /// is honoured where the store is ordered; MA applies it as a diff.</summary>
        public abstract void WriteAll(IList<VrcExpressionParameters.Entry> entries);
        public abstract void Add(VrcExpressionParameters.Entry entry);
        public abstract bool Remove(string name);
        public abstract bool Edit(string name, System.Action<VrcExpressionParameters.Entry> edit);

        public bool Rename(string oldName, string newName) =>
            Edit(oldName, entry => entry.name = newName);

        /// <summary>
        /// Sets the synced flag on every listed entry that exists in the store; returns how
        /// many actually changed. Names the store doesn't hold, and entries already on the
        /// wanted side, are skipped. Turning sync ON is skipped for entries with no concrete
        /// type (MA "NotSynced" rows): there is no type to sync them as, and the MA backend
        /// deliberately never invents one — give such a row a type in MA first.
        /// </summary>
        public int SetSynced(IEnumerable<string> names, bool synced)
        {
            if (names == null) return 0;
            int changed = 0;
            foreach (var name in names)
            {
                var entry = Find(name);
                if (entry == null || entry.synced == synced) continue;
                if (synced && !entry.typed) continue;
                if (Edit(name, e => e.synced = synced)) changed++;
            }
            return changed;
        }

        /// <summary>
        /// Changes what type one row is declared as; false when nothing changed.
        ///
        /// The type a store declares is not the animator's — it is what the parameter is sent as,
        /// which decides the synced bits it costs (Bool = 1, Int / Float = 8) and what a menu
        /// control does with it. Both backends land it in their own field: the VRC asset's
        /// valueType, MA's syncType.
        ///
        /// A row with no concrete type is refused rather than given one. That is an MA
        /// "NotSynced" row, and inventing a type for it would declare a parameter nobody asked
        /// for and charge the avatar bits for it — the same reason
        /// <see cref="SetSynced"/> will not switch one on.
        /// </summary>
        public bool SetValueType(string name, VrcExpressionParameters.ValueType type)
        {
            var entry = Find(name);
            if (entry == null || !entry.typed || entry.valueType == type) return false;
            return Edit(name, e => e.valueType = type);
        }

        public VrcExpressionParameters.Entry Find(string name)
        {
            foreach (var entry in Read())
                if (entry.name == name)
                    return entry;
            return null;
        }

        public int UsedBits()
        {
            int bits = 0;
            foreach (var entry in Read())
                if (entry.synced)
                    bits += VrcExpressionParameters.BitCost(entry.valueType);
            return bits;
        }

        /// <summary>Wraps a user-assigned object; null when the type isn't a known store.</summary>
        public static ParameterStore TryWrap(Object target)
        {
            if (target == null) return null;
            if (VrcExpressionParameters.Is(target))
                return new VrcStore(target);
#if DAERD_MA
            if (target is GameObject gameObject)
                target = gameObject.GetComponent<MaParameters>();
            if (target is MaParameters parameters)
                return new MaStore(parameters);
#endif
            return null;
        }

        /// <summary>The store explicitly associated with the controller, wrapped; null when
        /// none is assigned or the assigned object went missing.</summary>
        public static ParameterStore Of(AnimatorController controller) =>
            TryWrap(GraphFrameData.GetParameterStore(controller));

        /// <summary>
        /// Explicit detection (user-triggered only). Exact matches:
        /// an avatar descriptor whose playable layers run this controller, or an MA Merge
        /// Animator referencing it with an MA Parameters component on itself or a parent.
        /// </summary>
        public static Object DetectFor(AnimatorController controller)
        {
            var vrc = VrcExpressionParameters.FindAssetFor(controller);
            if (vrc != null) return vrc;
#if DAERD_MA
            return MaStore.FindFor(controller);
#else
            return null;
#endif
        }

        /// <summary>
        /// The store that governs one MA Merge Animator: the MA Parameters on its own object or
        /// on the nearest parent that has one, which is where Modular Avatar itself looks. Asked
        /// of a merge the user has already chosen, so unlike <see cref="DetectFor"/> this
        /// searches nothing and opens nothing — and it is how the prefab link fills this slot,
        /// which is the project-wide answer with the prefab it came from named.
        ///
        /// Null in a project without MA, and null when nothing above the merge declares any
        /// parameters — which is a gimmick prefab that has not been given a store yet, not an
        /// error.
        /// </summary>
        public static Object StoreFor(Object mergeAnimator)
        {
#if DAERD_MA
            var component = mergeAnimator as Component;
            if (component == null) return null;
            return MaStore.Above(component.transform);
#else
            return null;
#endif
        }

        /// <summary>
        /// The controller's parameters that have no row in the store yet, as entries ready to
        /// <see cref="Add"/>: async by default (neither synced nor saved), carrying the
        /// controller's own default value. Triggers are left out — they have no expression
        /// parameter equivalent. A null store yields every mappable parameter.
        /// </summary>
        public static List<VrcExpressionParameters.Entry> MissingEntries(
            AnimatorController controller, ParameterStore store)
        {
            var missing = new List<VrcExpressionParameters.Entry>();
            if (controller == null) return missing;

            var known = new HashSet<string>();
            if (store != null)
                foreach (var entry in store.Read())
                    known.Add(entry.name);

            foreach (var parameter in controller.parameters)
            {
                if (!known.Add(parameter.name)) continue;
                var mapped = VrcExpressionParameters.MapType(parameter.type);
                if (mapped == null) continue;   // Trigger
                missing.Add(new VrcExpressionParameters.Entry
                {
                    name = parameter.name,
                    valueType = mapped.Value,
                    defaultValue = parameter.type == AnimatorControllerParameterType.Float
                        ? parameter.defaultFloat
                        : parameter.type == AnimatorControllerParameterType.Int
                            ? parameter.defaultInt
                            : parameter.defaultBool ? 1f : 0f,
                    synced = false,
                    saved = false,
                });
            }
            return missing;
        }

        /// <summary>Store-vs-controller checks, appended to the analyzer's issue list.</summary>
        public void Analyze(AnimatorController controller, List<AnalyzerIssue> issues)
        {
            if (controller == null || Target == null) return;
            var entries = Read();

            int capacity = Capacity();
            if (capacity >= 0)
            {
                int used = 0;
                foreach (var entry in entries)
                    if (entry.synced)
                        used += VrcExpressionParameters.BitCost(entry.valueType);
                if (used > capacity)
                    issues.Add(new AnalyzerIssue
                    {
                        kind = IssueKind.VrcParameters,
                        severity = IssueSeverity.Error,
                        message = L.Tr("Expression parameters use {0} of {1} synced bits.", used, capacity),
                        context = Target,
                    });
            }

            foreach (var entry in entries)
            {
                var controllerParameter = DbtBuilder.FindParameter(controller, entry.name);
                if (controllerParameter == null)
                {
                    if (entry.synced)
                        issues.Add(new AnalyzerIssue
                        {
                            kind = IssueKind.VrcParameters,
                            severity = IssueSeverity.Info,
                            message = L.Tr("Expression parameter '{0}' has no matching controller parameter.", entry.name),
                            context = Target,
                        });
                    continue;
                }
                // Differing types are NOT an error: VRChat converts between every
                // combination ("parameter mismatching", e.g. a 1-bit synced Bool driving an
                // animator Float — https://vrc.school/docs/Other/Parameter-Mismatching).
                // Surface it as info so accidental mismatches stay visible.
                if (!entry.typed) continue;
                var mapped = VrcExpressionParameters.MapType(controllerParameter.type);
                if (mapped != null && mapped.Value != entry.valueType)
                    issues.Add(new AnalyzerIssue
                    {
                        kind = IssueKind.VrcParameters,
                        severity = IssueSeverity.Info,
                        message = L.Tr("Expression parameter '{0}' is {1} while the controller parameter is {2} — VRChat converts between them (parameter mismatching); make sure it's intentional.",
                            entry.name, entry.valueType, controllerParameter.type),
                        context = Target,
                    });
            }
        }

        // ---- VRCExpressionParameters backend ---------------------------------

        class VrcStore : ParameterStore
        {
            readonly Object _asset;
            public VrcStore(Object asset) => _asset = asset;

            public override Object Target => _asset;
            public override string Kind => "VRC Params";
            public override int Capacity() => VrcExpressionParameters.Capacity(_asset);
            public override List<VrcExpressionParameters.Entry> Read() =>
                VrcExpressionParameters.Read(_asset);
            public override void WriteAll(IList<VrcExpressionParameters.Entry> entries)
            {
                Undo.RegisterCompleteObjectUndo(_asset, "Sync Parameters");
                VrcExpressionParameters.WriteAll(_asset, entries);
            }
            public override void Add(VrcExpressionParameters.Entry entry) =>
                VrcExpressionParameters.Add(_asset, entry);
            public override bool Remove(string name) =>
                VrcExpressionParameters.Remove(_asset, name);
            public override bool Edit(string name, System.Action<VrcExpressionParameters.Entry> edit) =>
                VrcExpressionParameters.Edit(_asset, name, edit);
        }

        // ---- Modular Avatar "MA Parameters" backend ---------------------------

#if DAERD_MA
        /// <summary>
        /// Modular Avatar's MA Parameters component, reached through Modular Avatar's own
        /// types — <c>ModularAvatarParameters.parameters</c>, a list of
        /// <c>ParameterConfig</c> — behind the <c>DAERD_MA</c> versionDefine.
        ///
        /// <para>WHY BY TYPE RATHER THAN BY NAME.</para>
        /// This used to be a type-name match plus SerializedObject, which is the bargain DaerD
        /// keeps with the VRChat SDK (ADR 0009) and is the wrong bargain here. The SDK's is
        /// paid for a reason: DaerD has to compile, run and be tested in a project that has no
        /// SDK, so the SDK cannot be a reference. MA integration has no such half — it means
        /// nothing at all in a project without MA — so a versionDefine deletes the code instead
        /// of leaving it to fail quietly, and every field below becomes a compile error on the
        /// day MA renames one rather than a row that silently stops being read on somebody
        /// else's machine after an update. What was given up is real: the SerializedObject path
        /// also worked against ANY component that happened to have the same field names, which
        /// is what let the tests drive it with a stand-in of their own. They now drive the real
        /// component and skip themselves where it is absent.
        ///
        /// <para>WHAT A PROJECT WITHOUT MA SEES.</para>
        /// The same as before: nothing. <see cref="TryWrap"/> does not recognise MA components
        /// and <see cref="DetectFor"/> finds none — which is the honest answer, because a project
        /// without MA has no MA components in it.
        ///
        /// Prefix rows (PhysBone families) are read past and never touched: they name a family
        /// rather than a parameter, and the shared entry shape has nowhere to put one.
        /// </summary>
        class MaStore : ParameterStore
        {
            readonly MaParameters _component;
            public MaStore(MaParameters component) => _component = component;

            public override Object Target => _component;
            public override string Kind => "MA Params";
            public override int Capacity() => -1;   // contributes to the avatar's budget

            /// <summary>The MA Parameters component belonging to a scene MA Merge Animator
            /// that references this controller (on the same object or a parent).</summary>
            public static Object FindFor(AnimatorController controller)
            {
                if (controller == null) return null;
#if DAERD_VRC
                foreach (var merge in Object.FindObjectsOfType<MaMergeAnimator>(true))
                {
                    if (merge == null || merge.animator != controller) continue;
                    var parameters = Above(merge.transform);
                    if (parameters != null) return parameters;
                }
#endif
                return null;
            }

            /// <summary>The MA Parameters component on this object or the nearest parent that
            /// has one — MA reads a merge's parameters from the same place.</summary>
            public static MaParameters Above(Transform transform)
            {
                for (; transform != null; transform = transform.parent)
                {
                    var parameters = transform.GetComponent<MaParameters>();
                    if (parameters != null) return parameters;
                }
                return null;
            }

            // ---- reading ------------------------------------------------------

            public override List<VrcExpressionParameters.Entry> Read()
            {
                var entries = new List<VrcExpressionParameters.Entry>();
                if (_component == null) return entries;
                foreach (var config in _component.parameters)
                {
                    if (config.isPrefix) continue;
                    entries.Add(EntryOf(config));
                }
                return entries;
            }

            /// <summary>One MA row in the shared entry shape. "Synced" is two of MA's fields at
            /// once: a row with no type is not synced because there is nothing to sync it as,
            /// and a typed row that is localOnly is declared but stays at home.</summary>
            static VrcExpressionParameters.Entry EntryOf(MaConfig config) =>
                new VrcExpressionParameters.Entry
                {
                    name = config.nameOrPrefix ?? string.Empty,
                    valueType = MapSyncType(config.syncType),
                    typed = config.syncType != MaSyncType.NotSynced,
                    synced = config.syncType != MaSyncType.NotSynced && !config.localOnly,
                    saved = config.saved,
                    defaultValue = config.defaultValue,
                };

            /// <summary>
            /// The renames this component's rows are subject to, asked of NDMF rather than
            /// worked out here.
            ///
            /// NDMF is where the answer is: it is the framework that runs the renaming, MA is
            /// one plugin registered with it among however many a project has installed, and
            /// <c>ParameterInfo.ForUI</c> is the same query MA's own inspector uses to show a
            /// person what their gimmick will really be called. Re-deriving it from
            /// <c>internalParameter</c> and <c>remapTo</c> would be a second implementation of
            /// somebody else's rule that agrees with it right up until the version where it
            /// does not — and it could not see a rename applied by a component of a plugin
            /// DaerD has never heard of, which is most of the point of asking the framework.
            ///
            /// Asked at the GAME OBJECT rather than at the component: NDMF resolves renames by
            /// walking up from the object, and the component overload of that call reaches for
            /// <c>transform.parent</c> without checking it exists, which is a null reference on
            /// exactly the shape this is most often asked about — a gimmick prefab whose root
            /// is not inside an avatar. The object overload checks, and includes this
            /// component's own renames because it applies every provider on the object.
            ///
            /// The hole, stated: without NDMF this answers "no renames", which is what DaerD
            /// answered before any of this existed. A project with MA but no NDMF cannot exist
            /// (MA depends on it), so the case is theoretical — but the same is true of any
            /// build step that renames parameters without telling NDMF, and DaerD will show
            /// that gimmick's editor-side name and be wrong about it.
            /// </summary>
            public override Dictionary<string, string> EffectiveNames()
            {
                var renames = new Dictionary<string, string>();
#if DAERD_NDMF && DAERD_VRC
                if (_component == null) return renames;
                var mappings = nadena.dev.ndmf.ParameterInfo.ForUI
                    .GetParameterRemappingsAt(_component.gameObject);
                foreach (var config in _component.parameters)
                {
                    if (config.isPrefix || string.IsNullOrEmpty(config.nameOrPrefix)) continue;
                    if (!mappings.TryGetValue(
                            (nadena.dev.ndmf.ParameterNamespace.Animator, config.nameOrPrefix),
                            out var mapping))
                        continue;
                    if (string.IsNullOrEmpty(mapping.ParameterName)
                        || mapping.ParameterName == config.nameOrPrefix)
                        continue;
                    renames[config.nameOrPrefix] = mapping.ParameterName;
                }
#endif
                return renames;
            }

            static VrcExpressionParameters.ValueType MapSyncType(MaSyncType syncType)
            {
                switch (syncType)
                {
                    case MaSyncType.Int: return VrcExpressionParameters.ValueType.Int;
                    case MaSyncType.Bool: return VrcExpressionParameters.ValueType.Bool;
                    default: return VrcExpressionParameters.ValueType.Float;
                }
            }

            static MaSyncType MapValueType(VrcExpressionParameters.ValueType type)
            {
                switch (type)
                {
                    case VrcExpressionParameters.ValueType.Int: return MaSyncType.Int;
                    case VrcExpressionParameters.ValueType.Bool: return MaSyncType.Bool;
                    default: return MaSyncType.Float;
                }
            }

            // ---- writing ------------------------------------------------------

            /// <summary>MA entries are matched by name (order carries no meaning there), so
            /// "write all" applies as a diff and leaves prefix rows untouched.</summary>
            public override void WriteAll(IList<VrcExpressionParameters.Entry> entries)
            {
                var wanted = new Dictionary<string, VrcExpressionParameters.Entry>();
                foreach (var entry in entries) wanted[entry.name] = entry;
                foreach (var existing in Read())
                    if (!wanted.ContainsKey(existing.name))
                        Remove(existing.name);
                foreach (var entry in entries)
                {
                    if (Find(entry.name) != null)
                        Edit(entry.name, e => CopyInto(entry, e));
                    else
                        Add(entry);
                }
            }

            static void CopyInto(VrcExpressionParameters.Entry from, VrcExpressionParameters.Entry to)
            {
                to.name = from.name;
                to.valueType = from.valueType;
                to.typed = from.typed;
                to.synced = from.synced;
                to.saved = from.saved;
                to.defaultValue = from.defaultValue;
            }

            public override void Add(VrcExpressionParameters.Entry entry)
            {
                if (_component == null || entry == null || Find(entry.name) != null) return;
                Undo.RegisterCompleteObjectUndo(_component, "Add MA Parameter");
                var config = new MaConfig { nameOrPrefix = entry.name, remapTo = string.Empty };
                WriteEntry(ref config, entry);
                _component.parameters.Add(config);
                Dirty();
            }

            public override bool Remove(string name)
            {
                if (_component == null) return false;
                var list = _component.parameters;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].isPrefix || list[i].nameOrPrefix != name) continue;
                    Undo.RegisterCompleteObjectUndo(_component, "Remove MA Parameter");
                    list.RemoveAt(i);
                    Dirty();
                    return true;
                }
                return false;
            }

            public override bool Edit(string name, System.Action<VrcExpressionParameters.Entry> edit)
            {
                if (_component == null) return false;
                var list = _component.parameters;
                for (int i = 0; i < list.Count; i++)
                {
                    var config = list[i];
                    if (config.isPrefix || config.nameOrPrefix != name) continue;
                    var entry = EntryOf(config);
                    Undo.RegisterCompleteObjectUndo(_component, "Edit MA Parameter");
                    edit(entry);
                    config.nameOrPrefix = entry.name;
                    WriteEntry(ref config, entry);
                    // ParameterConfig is a struct: the copy above is what was edited, so the
                    // row is only really changed by putting it back.
                    list[i] = config;
                    Dirty();
                    return true;
                }
                return false;
            }

            /// <summary>Maps the shared entry shape back onto MA's fields. Synced entries get
            /// a concrete syncType; unsynced typed entries keep their type with localOnly.
            /// An entry with no type at all leaves syncType alone — inventing one would declare
            /// a parameter nobody asked for and charge the avatar bits for it.</summary>
            static void WriteEntry(ref MaConfig config, VrcExpressionParameters.Entry entry)
            {
                if (entry.typed) config.syncType = MapValueType(entry.valueType);
                config.localOnly = !entry.synced;
                config.saved = entry.saved;
                config.defaultValue = entry.defaultValue;
                config.hasExplicitDefaultValue = true;
            }

            /// <summary>
            /// Writes the change out, wherever the component lives.
            ///
            /// A scene component only needs to be marked dirty — the scene is saved by the
            /// person, when they save it. A component reached through the prefab link is INSIDE
            /// A PREFAB ASSET, and a dirty asset that nobody saves is a change that survives
            /// until the next domain reload and then is not there any more. Measured: editing
            /// the component of a loaded prefab asset in place and saving it is enough — the
            /// change is in the file and comes back after a reimport — so there is no round
            /// trip through <c>PrefabUtility.LoadPrefabContents</c>, which would hand back a
            /// different object graph than the one the store was handed and make every write
            /// go looking for its own component again.
            /// </summary>
            void Dirty()
            {
                EditorUtility.SetDirty(_component);
                if (EditorUtility.IsPersistent(_component))
                    AssetDatabase.SaveAssetIfDirty(_component);
            }
        }
#endif
    }

}

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

#if DAERD_NDMF && DAERD_VRC
// Registered with NDMF from inside the module rather than from the package's AssemblyInfo, so
// that lifting Editor/DynamicAnalyze out takes the plugin with it and leaves nothing behind
// naming a build framework the rest of DaerD has never heard of.
[assembly: nadena.dev.ndmf.ExportsPlugin(typeof(Yozolab.DaerD.DynamicAnalyze.BuildWatcher))]
#endif

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// What an avatar's build made of it, written down while it was being made — and the one
    /// place in this module that knows NDMF exists.
    ///
    /// <para>WHY STAND IN THE BUILD RATHER THAN READ THE RESULT AFTERWARDS.</para>
    /// A project that assembles its avatar out of parts does not hand DaerD the thing VRChat
    /// runs. The FX controller in the field is one input among several; what the avatar wears
    /// is a controller that exists for a few seconds inside a build, with other people's layers
    /// merged into it and parameters renamed out from under every name a person typed. Two
    /// things follow. A recording matched against the controller in the field finds nothing to
    /// match, so it loses its state, transition and layer-weight rows on exactly the avatars
    /// that are hardest to reason about. And the bridge between the names in the editor and the
    /// names on the wire — the thing every question about a synced parameter needs — is
    /// unrecoverable after the fact: a name like <c>Wag$1f4c2b</c> can be recognised as a
    /// rename but not traced back to what it renamed.
    ///
    /// Both are free while the build is running, because the build knows. NDMF lets a plugin
    /// put a pass in the sequence, so DD puts one in and copies out what it sees. The
    /// alternative was reconstructing it afterwards — walk the scene for the components that
    /// declare renames and reimplement their rules — which is a second implementation of
    /// somebody else's algorithm, wrong the moment they change it, and unable to see anything
    /// a plugin other than the one we reimplemented did.
    ///
    /// <para>WHY NOTHING IS ASKED OF THE USER.</para>
    /// Every avatar built in this session is captured, with no component to add and no box to
    /// tick. A per-avatar component was the obvious alternative and cannot be had at this
    /// price: DaerD is an Editor-only assembly, so a MonoBehaviour of ours left on an avatar
    /// would upload as a missing script. Doing it properly means a runtime assembly and a pass
    /// that strips the component again, which is a real design and not one this needs — the
    /// capture is a few dictionaries per avatar and reads nothing that costs anything.
    ///
    /// <para>WHAT THE CALLER SEES.</para>
    /// Nothing conditional. Every entry point below exists whether or not NDMF is installed and
    /// answers "no build has been seen for this avatar" when it is not, so no <c>#if</c> reaches
    /// the recorder or the window. The same is true without the VRChat SDK: the whole capture is
    /// about a VRChat avatar descriptor's playable layers and expression parameters, so it is
    /// compiled only when both are present, and the module's test run without either is what
    /// keeps that honest.
    ///
    /// <para>HOW LONG WHAT IS CAPTURED LIVES.</para>
    /// The controllers a build produces are NDMF's temporary assets, and NDMF deletes the whole
    /// temporary folder the moment Play mode ends (measured — see
    /// <c>DynamicAnalyzeBuildTests</c>). So the references kept here read as null from then on,
    /// and everything that has to outlive a session is copied out as text at capture time: the
    /// rename table, the parameter sets, the names the build says are synced. Cloning the
    /// controllers instead was considered and dropped — a merged FX is thousands of objects and
    /// a deep copy of it on every Play entry would be paid by everybody, to keep something the
    /// only reader of it (a recording, which bakes its state labels when it starts) does not
    /// need afterwards.
    /// </summary>
    static class BuildCapture
    {
        /// <summary>One of the avatar's playable layers as the build left it: the slot's own
        /// name out of the descriptor's enum — "FX", "Gesture" — and the controller sitting in
        /// it. The name is read off the serialized enum rather than from a table of our own, so
        /// a slot the SDK adds later is labelled correctly by a build that has never heard of
        /// it.</summary>
        public struct Slot
        {
            public string kind;
            public AnimatorController controller;
        }

        /// <summary>
        /// One avatar, as one build left it.
        ///
        /// Refreshed in place at every capture point rather than kept per phase: the interesting
        /// controller is the last one, and a reader asking "what is this avatar running" wants
        /// an answer rather than a history. <see cref="parametersAt"/> is the exception — that
        /// one IS the history, and it is what <see cref="Changed"/> reads to say what each phase
        /// of the build added or took away.
        /// </summary>
        public sealed class Built
        {
            /// <summary>The avatar's name as a person would say it. Taken with any
            /// <c>(Clone)</c> suffix removed, because VRChat's own build hook renames the object
            /// while it runs and puts the name back afterwards — so the name seen from inside a
            /// build is not the name seen from outside one.</summary>
            public string avatar = string.Empty;
            /// <summary>The object the build ran on. Held for identity rather than for reading:
            /// it is how a recorder decides that the avatar in front of it is this one.</summary>
            public GameObject root;
            /// <summary>The last build phase this was refreshed in.</summary>
            public string phase = string.Empty;
            public readonly List<Slot> slots = new List<Slot>();
            /// <summary>The expression parameters asset the build ended with — the truth about
            /// what travels. A temporary asset like the controllers, so
            /// <see cref="synced"/> beside it is what survives the session.</summary>
            public Object parameters;
            /// <summary>The names the built expression parameters call synced, copied out as
            /// text while the asset was alive.</summary>
            public readonly List<string> synced = new List<string>();
            /// <summary>Editing-time name → built name, for animator and menu parameters.</summary>
            public readonly Dictionary<string, string> renames = new Dictionary<string, string>();
            /// <summary>The same for PhysBone parameter prefixes, which are a namespace of their
            /// own: a prefix and a parameter can share a name and mean different things, and one
            /// table for both would answer the wrong one.</summary>
            public readonly Dictionary<string, string> prefixRenames =
                new Dictionary<string, string>();
            /// <summary>Phase name → every animator parameter the avatar's playable layers
            /// declared at the end of it. One set per phase, because a difference between two of
            /// them is what a build did to the parameters — see <see cref="Changed"/>.</summary>
            public readonly Dictionary<string, List<string>> parametersAt =
                new Dictionary<string, List<string>>();

            /// <summary>Which slot this controller is in, or the empty string when it is not one
            /// of this avatar's.</summary>
            public string KindOf(AnimatorController controller)
            {
                foreach (var slot in slots)
                    if (slot.controller == controller) return slot.kind;
                return string.Empty;
            }
        }

        /// <summary>
        /// How many avatars are remembered at once.
        ///
        /// A cap rather than a clear-out, because there is no moment to clear on: entering Play
        /// mode reloads the domain and empties this by itself, and LEAVING it deliberately does
        /// not — a recording is read after Play mode ends and the answer to "what was this
        /// avatar built into" has to still be there. What is left is a slow drip of one entry
        /// per avatar per build within one session, which this bounds. The oldest goes first.
        /// </summary>
        const int Kept = 8;

        static readonly List<Built> _built = new List<Built>();

        /// <summary>How many avatars have been seen built this session. On the panel's state
        /// line, and the thing a test asks about.</summary>
        public static int Count => _built.Count;

        /// <summary>
        /// The entry for this avatar root, or null.
        ///
        /// Identity first, because the object handed to a build is the object in the scene and a
        /// name is a thing two avatars can share. Name only when the object the build ran on is
        /// GONE, which is the one case identity cannot answer and somebody still needs one: the
        /// avatar built on entering Play mode is destroyed on leaving it, and the object that
        /// comes back is the editor's own copy under the same name. Asking about an avatar
        /// whose build is over is exactly what a person does after a recording, so the fallback
        /// is what makes the answer survive the session. What it costs is that two avatars
        /// called the same thing share an entry once the first is gone.
        /// </summary>
        public static Built Of(GameObject root)
        {
            if (root == null) return null;
            foreach (var built in _built)
                if (built.root == root) return built;
            string name = Named(root);
            foreach (var built in _built)
                if (built.root == null && built.avatar == name) return built;
            return null;
        }

        /// <summary>
        /// The entry for the avatar this Animator belongs to, or null.
        ///
        /// Walks up rather than assuming the Animator is on the avatar root. It usually is —
        /// VRChat wants the descriptor and the Animator on the same object — but a recording can
        /// be aimed at any Animator a graph is driving, and one inside an avatar is still that
        /// avatar's.
        /// </summary>
        public static Built For(Animator animator)
        {
            if (animator == null) return null;
            for (var transform = animator.transform; transform != null; transform = transform.parent)
            {
                var built = Of(transform.gameObject);
                if (built != null) return built;
            }
            return null;
        }

        /// <summary>Whether a build of this avatar was watched this session.</summary>
        public static bool Has(Animator animator) => For(animator) != null;

        /// <summary>
        /// The controllers this avatar's build put in its playable layers, in slot order and
        /// without repeats.
        ///
        /// Handed out as a list rather than as "the FX one" on purpose: which slot a recording
        /// is watching is decided by matching layer names against the graph that is running
        /// (<see cref="PlayRecorder.Matching"/>), and that machinery already exists and does not
        /// need to be told which slot to expect. An avatar whose Gesture layer is the one being
        /// read is then served by the same lines.
        /// </summary>
        public static List<AnimatorController> ControllersFor(Animator animator)
        {
            var found = new List<AnimatorController>();
            var built = For(animator);
            if (built == null) return found;
            foreach (var slot in built.slots)
                if (slot.controller != null && !found.Contains(slot.controller))
                    found.Add(slot.controller);
            return found;
        }

        /// <summary>Which playable layer this built controller is, for a line that has to say
        /// which one it read. Empty when it is not one of this avatar's.</summary>
        public static string KindOf(Animator animator, AnimatorController controller)
        {
            var built = For(animator);
            return built == null ? string.Empty : built.KindOf(controller);
        }

        /// <summary>What the build says travels, or null when this avatar's build was never
        /// seen. Null and empty are different answers — no build watched is not the same as a
        /// build that syncs nothing.</summary>
        public static List<string> SyncedFor(Animator animator)
        {
            var built = For(animator);
            return built == null ? null : new List<string>(built.synced);
        }

        /// <summary>
        /// Editing-time name → built name for this avatar's animator parameters, or an empty
        /// table when no build of it was watched.
        ///
        /// Read out loud by <see cref="Changed"/> and otherwise held rather than used: relabelling
        /// a recording's rows through it, and following a name the other way when a run is
        /// extracted, are both worth doing and are both a later wave's. What the capture settles
        /// is that the table can be had at all, which is the part that is only possible while the
        /// build is running.
        /// </summary>
        public static Dictionary<string, string> RemapOf(GameObject root)
        {
            var built = Of(root);
            return built == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(built.renames);
        }

        /// <summary>The same for PhysBone prefixes.</summary>
        public static Dictionary<string, string> PrefixRemapOf(GameObject root)
        {
            var built = Of(root);
            return built == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(built.prefixRenames);
        }

        // ---- what the build changed -----------------------------------------

        /// <summary>The phases a capture point sits at the end of. Named here rather than
        /// spelled at the three capture points below, because a difference between two of the
        /// sets they leave behind is only a before-and-after if something says which is which —
        /// and the dictionary they are kept in has an order of its own that is not the build's.
        /// </summary>
        internal const string Resolving = "Resolving";

        internal const string Transforming = "Transforming";

        internal const string Optimizing = "Optimizing";

        /// <summary>Those three in the order the build runs them, which is the order a reader
        /// is told what happened in.</summary>
        static readonly string[] Phases = { Resolving, Transforming, Optimizing };

        /// <summary>
        /// What this build did to the avatar, as the things the material already captured can
        /// honestly say: what was renamed — parameters and PhysBone prefixes being different
        /// namespaces, a line each — what appeared, what went away, and what travels in the end.
        ///
        /// <para>WHY PHASES AND NOT PLUGINS.</para>
        /// The grain is a build phase, so a line here reads "Transforming added these" and never
        /// "this plugin added these". That is not a simplification, it is the resolution the
        /// capture has: the passes stand at the END of a phase (the plugin at the foot of this
        /// file declares where), so what is compared is the avatar before a phase against the
        /// avatar after it, with every plugin that ran inside it summed together. Attributing a
        /// name to the plugin that made it would mean a capture point between every pair of
        /// passes — NDMF orders passes but offers no hook between other people's — and the
        /// alternative, guessing from the shape of a name which plugin's convention it follows,
        /// is a table of other people's private spellings that would be wrong on their next
        /// release.
        ///
        /// <para>WHY THE RENAME LINES AND THE ADDED LINES OVERLAP.</para>
        /// A renamed parameter shows up twice: once in the rename table, and once as a name that
        /// appeared in the phase the rename was applied in (with the old spelling among the ones
        /// that went away, when the avatar declared it before). Deliberate, because the two
        /// answer different questions — "what is this called now" and "when did it turn up" —
        /// and subtracting one from the other would make a merged-in parameter that is also
        /// renamed, which is the ordinary case, invisible in the half that says a merge happened
        /// at all.
        ///
        /// Pure and over one entry: no window, no scene, nothing asked of NDMF. Null is a
        /// caller with no capture in hand and answers with an empty list, which is how the panel
        /// knows to draw no frame at all rather than an empty one.
        /// </summary>
        public static List<string> Changed(Built built)
        {
            var lines = new List<string>();
            if (built == null) return lines;
            Renamed(built.renames, false, lines);
            Renamed(built.prefixRenames, true, lines);
            Between(built, lines);
            lines.Add(L.Tr(
                "{0} parameter(s) travel in the end. That is what the expression parameters this build produced call synced — the list VRChat really carries, rather than the one the store was written with.",
                built.synced.Count));
            return lines;
        }

        /// <summary>The same for the avatar this Animator belongs to, which is what the panel
        /// has in its hand: an Animator somebody is recording. Empty when no build of it was
        /// watched.</summary>
        public static List<string> ChangedFor(Animator animator) => Changed(For(animator));

        /// <summary>
        /// One rename table as a line, or nothing when it is empty.
        ///
        /// Sorted by the name a person typed, because that is the half of each pair they are
        /// looking for. The two tables get a line each rather than being poured together: a
        /// prefix and a parameter can share a name and mean different things (see
        /// <see cref="Built.prefixRenames"/>), so one list of both would be a list a reader
        /// cannot act on.
        /// </summary>
        static void Renamed(Dictionary<string, string> table, bool prefixes, List<string> lines)
        {
            if (table.Count == 0) return;
            var sources = new List<string>(table.Keys);
            sources.Sort(System.StringComparer.Ordinal);
            var pairs = new List<string>();
            foreach (string source in sources) pairs.Add(source + " → " + table[source]);
            lines.Add(prefixes
                ? L.Tr(
                    "The build renamed {0} PhysBone prefix(es) ({1}). A prefix names a group rather than a parameter, so nothing is called this on its own — what changed is the spelling every contact and physbone parameter under it now uses.",
                    table.Count, Join(pairs))
                : L.Tr(
                    "The build renamed {0} parameter(s) ({1}). The name on the left is the one written in the editor; the one on the right is what the built avatar wears, and what a recording of it says.",
                    table.Count, Join(pairs)));
        }

        /// <summary>
        /// What each phase added and what it took away, walked in build order.
        ///
        /// The two are collected separately and appended in turn so that the panel reads as
        /// everything that arrived and then everything that left, rather than alternating
        /// between the two per phase — a reader is asking one of those questions at a time.
        ///
        /// A phase with no set is skipped rather than treated as an empty avatar: a capture that
        /// never happened is not a build that declared nothing, and the difference is the
        /// whole list appearing as "taken away".
        /// </summary>
        static void Between(Built built, List<string> lines)
        {
            var added = new List<string>();
            var gone = new List<string>();
            List<string> before = null;
            foreach (string phase in Phases)
            {
                if (!built.parametersAt.TryGetValue(phase, out var now) || now == null) continue;
                if (before != null)
                {
                    var arrived = Only(now, before);
                    if (arrived.Count > 0)
                        added.Add(L.Tr(
                            "{0} added {1} parameter(s) ({2}). Read as the difference across a whole phase, so it is what everything running in it did between them — not what any one plugin did.",
                            phase, arrived.Count, Join(arrived)));
                    var left = Only(before, now);
                    if (left.Count > 0)
                        gone.Add(L.Tr(
                            "{0} took {1} parameter(s) away ({2}). The same reading across a whole phase: a name here is one the avatar's playable layers no longer declared by the end of it, whatever removed it.",
                            phase, left.Count, Join(left)));
                }
                before = now;
            }
            lines.AddRange(added);
            lines.AddRange(gone);
        }

        /// <summary>The names in the first set and not the second, in the order they are held —
        /// which is sorted, because that is how a phase's set was written down. Through a set
        /// rather than a scan of the list: this runs on every repaint of the panel, and a real
        /// avatar declares parameters in the hundreds.</summary>
        static List<string> Only(List<string> names, List<string> others)
        {
            var without = new HashSet<string>(others);
            var found = new List<string>();
            foreach (string name in names)
                if (!without.Contains(name)) found.Add(name);
            return found;
        }

        /// <summary>Names, with a tail rather than a wall of them — the shape SimNotes and
        /// RunFindings both use, spelled again here rather than made public in either for the
        /// sake of five lines.</summary>
        static string Join(List<string> names)
        {
            const int shown = 3;
            if (names.Count <= shown) return string.Join(", ", names.ToArray());
            var head = names.GetRange(0, shown);
            return string.Join(", ", head.ToArray())
                + L.Tr(" and {0} more", names.Count - shown);
        }

        /// <summary>Forgets everything. For tests, which have to be able to tell an entry this
        /// one made from an entry the previous one left behind.</summary>
        internal static void Forget() => _built.Clear();

        // ---- what the passes write ------------------------------------------

        /// <summary>
        /// The entry to write this avatar's capture into: the one for this very object if it is
        /// already known, otherwise the one under the same name, otherwise a new one.
        ///
        /// Name is what makes a rebuild replace rather than accumulate. Entering Play mode
        /// builds the same avatar again as a different object, and so does pressing bake twice;
        /// keeping both would mean a reader picking between two answers to one question, and the
        /// later one is always the one meant. What it costs is stated rather than hidden: two
        /// different avatars called the same thing share an entry, and the last built wins.
        /// </summary>
        static Built Claim(GameObject root, string name)
        {
            var built = Of(root);
            if (built == null)
                foreach (var known in _built)
                    if (known.avatar == name) { built = known; break; }
            if (built == null)
            {
                built = new Built();
                _built.Add(built);
                while (_built.Count > Kept) _built.RemoveAt(0);
            }
            else if (built.root != root)
            {
                // A different object under the same name is a different build, so nothing of the
                // old one is worth keeping — a rename table from a previous session's avatar
                // read against this one's would be worse than no table.
                built.slots.Clear();
                built.synced.Clear();
                built.renames.Clear();
                built.prefixRenames.Clear();
                built.parametersAt.Clear();
                built.parameters = null;
            }
            built.root = root;
            built.avatar = name;
            return built;
        }

        /// <summary>
        /// The avatar's name without the suffix VRChat's build hook borrows.
        ///
        /// Measured: entering Play mode with NDMF installed goes through the SDK's own
        /// preprocess hook, which renames the avatar object to "<c>Something(Clone)</c>" for the
        /// duration and puts the name back on the way out — so an object captured from inside a
        /// build carries a name nobody outside one would recognise it by.
        /// </summary>
        static string Named(GameObject root)
        {
            string name = root == null ? string.Empty : root.name;
            const string clone = "(Clone)";
            return name.EndsWith(clone, System.StringComparison.Ordinal)
                ? name.Substring(0, name.Length - clone.Length)
                : name;
        }

        /// <summary>Whatever component on this object calls itself an avatar descriptor.
        /// Matched by type name and read through SerializedObject, which is how the whole of
        /// DaerD reaches the VRChat SDK — the package is not referenced, so a project without it
        /// still compiles and a field renamed upstream degrades to "not found" rather than to a
        /// crash.</summary>
        static Component DescriptorOn(GameObject root)
        {
            if (root == null) return null;
            foreach (var component in root.GetComponents<Component>())
                if (component != null && component.GetType().Name == "VRCAvatarDescriptor")
                    return component;
            return null;
        }

#if DAERD_NDMF && DAERD_VRC
        /// <summary>
        /// One capture point: the avatar's playable layers, what it says travels, and the
        /// parameters standing at the end of this phase.
        ///
        /// Everything here is a read. A pass that changed anything would be a pass that made the
        /// avatar depend on whether DaerD happened to be installed, which is the one thing an
        /// observer must never do.
        /// </summary>
        internal static void Capture(nadena.dev.ndmf.BuildContext context, string phase)
        {
            var root = context == null ? null : context.AvatarRootObject;
            if (root == null) return;
            var built = Claim(root, Named(root));
            built.phase = phase;

            var descriptor = DescriptorOn(root);
            built.slots.Clear();
            built.parameters = null;
            if (descriptor != null)
            {
                var so = new SerializedObject(descriptor);
                built.parameters = so.FindProperty("expressionParameters")?.objectReferenceValue;
                foreach (string arrayName in new[] { "baseAnimationLayers", "specialAnimationLayers" })
                {
                    var layers = so.FindProperty(arrayName);
                    if (layers == null || !layers.isArray) continue;
                    for (int i = 0; i < layers.arraySize; i++)
                    {
                        var element = layers.GetArrayElementAtIndex(i);
                        var controller = element.FindPropertyRelative("animatorController")
                            ?.objectReferenceValue as AnimatorController;
                        if (controller == null) continue;
                        built.slots.Add(new Slot { kind = SlotName(element), controller = controller });
                    }
                }
            }

            built.synced.Clear();
            // Through the store rather than through a reader of our own: which names an avatar
            // syncs is a question DaerD already answers for both of the shapes a project stores
            // them in, and a second reader here could disagree with the panel beside it.
            var store = ParameterStore.TryWrap(built.parameters);
            if (store != null)
                foreach (var entry in store.Read())
                    if (entry != null && entry.synced && !string.IsNullOrEmpty(entry.name))
                        built.synced.Add(entry.name);

            built.parametersAt[phase] = Declared(built);
        }

        /// <summary>The name of the playable layer slot this element is, read off the serialized
        /// enum itself so no table here has to be kept in step with the SDK's.</summary>
        static string SlotName(SerializedProperty element)
        {
            var type = element.FindPropertyRelative("type");
            if (type == null) return string.Empty;
            var names = type.enumNames;
            int at = type.enumValueIndex;
            return names != null && at >= 0 && at < names.Length ? names[at] : string.Empty;
        }

        /// <summary>Every animator parameter this avatar's playable layers declare right now,
        /// sorted and without repeats. One set per phase is what a difference between phases is
        /// made of.</summary>
        static List<string> Declared(Built built)
        {
            var names = new List<string>();
            foreach (var slot in built.slots)
            {
                if (slot.controller == null) continue;
                foreach (var parameter in slot.controller.parameters)
                    if (!names.Contains(parameter.name)) names.Add(parameter.name);
            }
            names.Sort(System.StringComparer.Ordinal);
            return names;
        }

        /// <summary>
        /// The rename table, taken through NDMF's own published API.
        ///
        /// <para>WHY THAT API AND NOT THE PLUGIN'S OWN TABLE.</para>
        /// Modular Avatar keeps the mapping it computes in a type marked internal to its own
        /// assembly. Reaching it would mean either referencing MA — which this deliberately does
        /// not, so that a project with NDMF and no MA still gets everything below — or
        /// reflection into somebody's private field, which is the class of dependency that
        /// breaks silently on their next release. NDMF publishes the same answer for every
        /// plugin that declares one, so the table below is not MA's table; it is whatever the
        /// build's renamers between them decided, MA included.
        ///
        /// <para>WHY IT IS TAKEN THIS EARLY.</para>
        /// Measured: MA destroys the components that DECLARE the renames as the last act of the
        /// pass that applies them, in Transforming. The API answers by asking those components,
        /// so asked at the end of Transforming — where the controllers are captured, because
        /// that is where they are finished — it answers with an empty table. This therefore runs
        /// at the end of Resolving, which is after the renamers have resolved their references
        /// and before anything has been rewritten. The names it returns are the final ones: the
        /// mapping is a pure function of the component and its path in the hierarchy, so asking
        /// before the rename is applied gives the same answer as asking during it.
        ///
        /// <para>WHY TWO CALLS.</para>
        /// The remappings API answers "what renames are in force at THIS object", and asked at
        /// the avatar root that means the root's own components and nothing else — an avatar
        /// whose renaming is done by a prefab three levels down, which is the ordinary case and
        /// the one this feature exists for, would come back empty. The parameter walk answers
        /// for the whole tree: every parameter any component anywhere in the avatar declares,
        /// with the name it will end up under. The two are asked in that order because the first
        /// can carry a rename for a name nothing declares, and the second is the one that
        /// reaches. Only names that actually CHANGE are kept — a table saying that Wave is
        /// called Wave is a table nobody can read.
        ///
        /// <para>WHAT ASKING COSTS THE BUILD.</para>
        /// Nothing that shows. Asking a renamer what it would do makes it compute the name and
        /// remember it, so this warms a cache the build was going to fill anyway; the names are
        /// derived from each component's own path, one component of the kind is allowed per
        /// object, and the answers are cached per component — so warming it early cannot change
        /// what any of them decides. The built controller's own parameter names are asserted
        /// against the rule rather than against a recording, which is what would catch it if
        /// that ever stopped being true.
        /// </summary>
        internal static void CaptureRenames(nadena.dev.ndmf.BuildContext context)
        {
            var root = context == null ? null : context.AvatarRootObject;
            if (root == null) return;
            var built = Claim(root, Named(root));
            built.renames.Clear();
            built.prefixRenames.Clear();
            var info = nadena.dev.ndmf.ParameterInfo.ForContext(context);
            foreach (var pair in info.GetParameterRemappingsAt(root))
                Note(built, pair.Key.Item1, pair.Key.Item2, pair.Value.ParameterName);
            foreach (var parameter in info.GetParametersForObject(root))
                Note(built, parameter.Namespace, parameter.OriginalName, parameter.EffectiveName);
        }

        /// <summary>One line of the rename table, if it is one: a name that came out of the
        /// build under a different name than it went in under.</summary>
        static void Note(Built built, nadena.dev.ndmf.ParameterNamespace space,
            string source, string target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return;
            if (source == target) return;
            var table = space == nadena.dev.ndmf.ParameterNamespace.PhysBonesPrefix
                ? built.prefixRenames
                : built.renames;
            table[source] = target;
        }
#endif
    }

#if DAERD_NDMF && DAERD_VRC
    /// <summary>
    /// DD's place in the build sequence: three passes that read and change nothing.
    ///
    /// <para>WHERE THEY SIT AND WHY.</para>
    /// The end of Resolving is the only moment the rename table exists to be read — see
    /// <see cref="BuildCapture.CaptureRenames"/>. The end of Transforming is where the
    /// controllers are finished, since that is the phase avatar assembly happens in. The end of
    /// Optimizing is after everything that shrinks or rewrites what Transforming produced, and
    /// is therefore the last honest answer to "what is this avatar running". The capture is
    /// idempotent and overwrites, so the three points cost one dictionary each and the last one
    /// wins.
    ///
    /// <para>WHY THE ORDERING IS A STRING.</para>
    /// The passes are ordered after Modular Avatar by its qualified name rather than by its
    /// type, which is what lets this file avoid referencing MA at all: a name for a plugin that
    /// is not installed resolves to an empty pair of markers in NDMF's solver and constrains
    /// nothing (measured — the capture runs, and passes, with MA absent). Nothing here is
    /// specific to MA in the first place; it is ordered after it because it is the plugin most
    /// likely to be the one doing the renaming and merging that this is here to see.
    ///
    /// <para>WHAT IS NOT PROMISED.</para>
    /// Not being last. Another plugin may put a pass after these in the same phase, and what it
    /// then does is not in the capture. NDMF offers no "after everything" hook and inventing one
    /// out of ordering constraints against plugins nobody has installed would be a promise this
    /// cannot keep. The phase a capture came from is recorded with it, so a reader is told how
    /// far through the build the answer is from.
    /// </summary>
    sealed class BuildWatcher : nadena.dev.ndmf.Plugin<BuildWatcher>
    {
        /// <summary>DaerD's own package name, which is what NDMF asks a plugin to be identified
        /// by and what another plugin would order itself against.</summary>
        public override string QualifiedName => "net.yozolab.daerd";

        public override string DisplayName => "DaerD";

        /// <summary>Modular Avatar's two plugins, by the names they publish. The second is the
        /// tail end MA splits off so that it runs after other people's transformations; ordering
        /// after both is what makes "after MA" mean after all of MA.</summary>
        const string ModularAvatar = "nadena.dev.modular-avatar";
        const string ModularAvatarLate = "nadena.dev.modular-avatar.late-transform-stages";

        protected override void Configure()
        {
            InPhase(nadena.dev.ndmf.BuildPhase.Resolving)
                .AfterPlugin(ModularAvatar)
                .Run("DD DynamicAnalyze: renames", context =>
                {
                    BuildCapture.CaptureRenames(context);
                    BuildCapture.Capture(context, BuildCapture.Resolving);
                });

            InPhase(nadena.dev.ndmf.BuildPhase.Transforming)
                .AfterPlugin(ModularAvatar)
                .AfterPlugin(ModularAvatarLate)
                .Run("DD DynamicAnalyze: what was assembled",
                    context => BuildCapture.Capture(context, BuildCapture.Transforming));

            InPhase(nadena.dev.ndmf.BuildPhase.Optimizing)
                .AfterPlugin(ModularAvatar)
                .Run("DD DynamicAnalyze: what is left",
                    context => BuildCapture.Capture(context, BuildCapture.Optimizing));
        }
    }
#endif
}

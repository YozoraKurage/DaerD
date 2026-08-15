using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// The two tools an avatar is really worn with in the editor, asked by name — and the one
    /// place in this module that knows either of them exists.
    ///
    /// <para>WHY THERE IS A TOOL-AWARE LAYER AT ALL.</para>
    /// Everything a recording READS is taken through Unity's own Playable API and names no tool
    /// (see <see cref="PlayRecorder"/>): whoever built the graph is served by the same lines,
    /// including the tool nobody has written yet. That is worth keeping, and it is also not
    /// enough. A graph says an Animator is being driven; it cannot say which of the five
    /// Animators now in the scene is the one a person is wearing, that two of them are the
    /// mirror and shadow copies Av3Emulator makes for its own purposes and nobody means, or
    /// that a third is the same avatar as somebody else sees it. Those are facts the tool
    /// holds, so the tool is what gets asked. What is asked of it is small on purpose: who is
    /// worn, who is a copy, and which copies belong to whom.
    ///
    /// <para>WHY BY TYPE RATHER THAN BY NAME.</para>
    /// Both tools are reached through their own types, behind a <c>versionDefine</c> each
    /// (<c>DAERD_GM</c>, <c>DAERD_AV3E</c>, declared in the .asmdef against the package names),
    /// which is the same bargain DaerD already made with the VRChat SDK: a wrong field name is
    /// a compile error here rather than a feature that silently stops working on somebody
    /// else's machine after they update. Reflection by name was the alternative and was
    /// dropped — it costs the compiler's help exactly where the API is somebody else's and
    /// changes without telling us, and the deeper uses these defines open up later (writing a
    /// parameter back through GestureManager's own objects) would be unwritable safely.
    ///
    /// <para>WHAT THE CALLER SEES.</para>
    /// Nothing. Every entry point below exists whether or not either package is installed and
    /// answers "nobody has this avatar" when it is not, so no <c>#if</c> reaches the window or
    /// the recorder. Absent tools are the ordinary case — most projects have neither — and the
    /// module has to compile and behave in that project, which is what DaerD's test runs
    /// without the SDK check on every commit.
    /// </summary>
    static class PlayTools
    {
        /// <summary>Which tool has an avatar. Nothing is ever both: GestureManager wins when
        /// both are looking at one avatar, because it is the one being driven by hand and so
        /// the one somebody is watching.</summary>
        public enum Tool
        {
            None,
            GestureManager,
            Av3Emulator,
        }

        /// <summary>What this copy of the avatar IS to the tool holding it, which is the whole
        /// reason to ask a tool anything.</summary>
        public enum Role
        {
            /// <summary>Nobody has it.</summary>
            None,
            /// <summary>The copy the person is wearing — GestureManager's controlled avatar, or
            /// Av3Emulator's local one. What a recording means unless somebody says otherwise.</summary>
            Worn,
            /// <summary>Av3Emulator's non-local clone: the same avatar as somebody else in the
            /// instance sees it, driven by what crossed the wire rather than by the wearer.
            /// Worth recording — beside the wearer, it is the comparison this whole module is
            /// about — and never worth recording INSTEAD of the wearer.</summary>
            Copy,
            /// <summary>Av3Emulator's mirror or shadow clone. A copy made to answer a question
            /// about rendering, not about the avatar's logic; offering one as a thing to record
            /// would be offering the wrong avatar under the right name.</summary>
            Aside,
        }

        /// <summary>Who has this avatar and what it is to them.</summary>
        public struct Hold
        {
            public Tool tool;
            public Role role;

            /// <summary>Whether a tool has it at all.</summary>
            public bool Known => tool != Tool.None;

            /// <summary>Whether this is a copy the candidate list is better off not
            /// offering — see <see cref="Role.Aside"/>.</summary>
            public bool Hidden => role == Role.Aside;
        }

        /// <summary>
        /// Who has this avatar right now.
        ///
        /// Asked fresh every time rather than cached: both answers move while the editor runs —
        /// GestureManager's dictionary changes the moment somebody picks another avatar, and
        /// Av3Emulator's clones appear on a tick box — and this is asked when a menu opens or a
        /// recording starts, which is rarely enough that a stale answer would cost more than
        /// the look does.
        /// </summary>
        public static Hold On(Animator animator)
        {
            var hold = new Hold { tool = Tool.None, role = Role.None };
            if (animator == null) return hold;
#if DAERD_GM
            // The keys are the avatar GameObjects, which is where the Animator is: the module
            // takes its own Animator off the same object (ModuleBase.AvatarAnimator). Read as a
            // key rather than through the module, so an entry whose module is halfway through
            // being swapped still answers "GestureManager has this one".
            if (global::BlackStartX.GestureManager.GestureManager.ControlledAvatars
                    .ContainsKey(animator.gameObject))
                return new Hold { tool = Tool.GestureManager, role = Role.Worn };
#endif
#if DAERD_AV3E
            var runtime = animator.GetComponent<global::Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime>();
            if (runtime != null)
            {
                hold.tool = Tool.Av3Emulator;
                // Asked in this order because the flags overlap: a mirror clone is also not the
                // sync source, and reading it as a remote view of the avatar would put a
                // rendering copy's rows beside the wearer's under a name promising otherwise.
                //
                // Being worn is asked BOTH ways — the flag and the pointer — although the tool
                // sets one from the other (IsLocal = AvatarSyncSource == this, in its Awake).
                // Either alone would be a guess about a component whose Awake has not run,
                // which is every one of them outside Play mode; requiring both means the
                // uncertain case falls to Copy, and a copy is only ever recorded beside the
                // wearer rather than instead of one.
                bool copy = runtime.AvatarSyncSource != null && runtime.AvatarSyncSource != runtime;
                if (runtime.IsMirrorClone || runtime.IsShadowClone) hold.role = Role.Aside;
                else if (copy || !runtime.IsLocal) hold.role = Role.Copy;
                else hold.role = Role.Worn;
            }
#endif
            return hold;
        }

        /// <summary>The tool's own name, untranslated on purpose: it is what the tool calls
        /// itself in its own window and its own menu item, and a reader looking for the avatar
        /// GestureManager has hold of is looking for that word.</summary>
        public static string Name(Tool tool)
        {
            switch (tool)
            {
                case Tool.GestureManager: return "GestureManager";
                case Tool.Av3Emulator: return "Av3Emulator";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// How to write this avatar down in a list of them: "GestureManager: Somebody" for one a
        /// tool has, the object's own name for one nobody does.
        ///
        /// Av3Emulator's own naming does the rest of the work — its non-local clones are called
        /// after the avatar with "(Non-Local 1)" on the end — so the copies read as copies
        /// without this having to say it twice.
        /// </summary>
        public static string Label(Animator animator)
        {
            string name = animator == null ? string.Empty : animator.name;
            var hold = On(animator);
            return hold.Known ? Name(hold.tool) + ": " + name : name;
        }

        /// <summary>
        /// The ones worth offering, out of everything a graph is driving: all of them, less the
        /// mirror and shadow copies. Deliberately a filter over the tool-blind list rather than
        /// a list of its own — an avatar no tool has hold of stays a candidate, because a graph
        /// driving it is the only thing this feature ever needed.
        /// </summary>
        public static List<Animator> Candidates(List<Animator> driven)
        {
            var found = new List<Animator>();
            if (driven == null) return found;
            foreach (var animator in driven)
                if (!On(animator).Hidden) found.Add(animator);
            return found;
        }

        /// <summary>
        /// Which of these a tool says is the one being worn, or null if no tool says anything.
        ///
        /// GestureManager first: it has exactly one avatar at a time and somebody chose it by
        /// hand, which is a stronger statement of intent than anything else on offer. Then
        /// Av3Emulator's local copy, which is a statement about the scene rather than about a
        /// person, and is right whenever there is only one avatar in it.
        /// </summary>
        public static Animator Preferred(List<Animator> candidates)
        {
            if (candidates == null) return null;
            foreach (var animator in candidates)
                if (On(animator).tool == Tool.GestureManager) return animator;
            foreach (var animator in candidates)
            {
                var hold = On(animator);
                if (hold.tool == Tool.Av3Emulator && hold.role == Role.Worn) return animator;
            }
            return null;
        }

        /// <summary>
        /// The other people's copies of this avatar — Av3Emulator's non-local clones, in the
        /// order it made them.
        ///
        /// Empty for anything that is not the sync source, which is the point: a clone's own
        /// list is not somebody else's clones, and recording a copy's copies would be reading
        /// the same wire twice. Empty as well without Av3Emulator, so the recorder asks
        /// unconditionally and gets an honest nothing.
        /// </summary>
        public static List<Animator> ClonesOf(Animator animator)
        {
            var found = new List<Animator>();
#if DAERD_AV3E
            if (animator == null) return found;
            var runtime = animator.GetComponent<global::Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime>();
            if (runtime == null || runtime.IsMirrorClone || runtime.IsShadowClone) return found;
            if (!runtime.IsLocal) return found;
            if (runtime.AvatarSyncSource != null && runtime.AvatarSyncSource != runtime) return found;
            foreach (var clone in runtime.NonLocalClones)
            {
                if (clone == null || clone.IsMirrorClone || clone.IsShadowClone) continue;
                var other = clone.GetComponent<Animator>();
                if (other == null || other == animator || found.Contains(other)) continue;
                found.Add(other);
            }
#endif
            return found;
        }
    }
}

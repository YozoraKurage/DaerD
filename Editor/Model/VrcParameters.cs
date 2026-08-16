namespace Yozolab.DaerD
{
    /// <summary>
    /// The built-in ("primitive") parameters the VRChat SDK drives automatically on every avatar.
    /// Names are case-sensitive and must match VRChat exactly, or the platform won't feed them.
    /// Source: https://creators.vrchat.com/avatars/animator-parameters/
    /// </summary>
    static class VrcParameters
    {
        // Local enum so this file has no hard dependency on UnityEditor.Animations; the panel maps
        // it to AnimatorControllerParameterType when adding the parameter.
        public enum ParamType { Float, Int, Bool }

        /// <summary>
        /// How the platform gets this parameter to somebody else's copy of the avatar — which
        /// is not the avatar's business at all. Every one of these travels (or does not) by
        /// VRChat's own arrangement, on its own channels, without costing a synced bit or
        /// appearing in an expression parameter store.
        ///
        /// Which is exactly why anything reasoning about what a remote sees has to ask here:
        /// a parameter absent from the store is normally one that never crosses, and for a
        /// built-in that inference is backwards.
        ///
        /// Named after the channel rather than after "does it cross", because the channels are
        /// what VRChat documents and they differ in every way a reader would want to know
        /// about: when a value goes, whether it can be lost on the way, and what the other
        /// person's copy does with it in between. One "it broadcasts" answer covers all three
        /// with the fastest of them, which is the flattering half of the truth.
        /// </summary>
        public enum Sync
        {
            /// <summary>Carried with the avatar's pose, on a fixed cadence: "always updated
            /// every 0.1 seconds (10 times per second)", and a Float is interpolated locally on
            /// the receiving end so it moves smoothly between updates rather than in ten steps
            /// a second. Not in the expression sample, so it is neither paced by that cadence
            /// nor rounded to its eight bits.</summary>
            Ik,
            /// <summary>Carried on the playable channel — "every 0.1 to 1 seconds, depending on
            /// parameter changes", which is the SAME channel an expression parameter rides. So
            /// it arrives on the avatar's own sync cadence and misses when that sample misses,
            /// and it still costs the avatar nothing: the eight-bit encoding is what buys a
            /// place in the avatar's bit budget, and these are outside it.</summary>
            Playable,
            /// <summary>Driven by the voice and not synced directly: the other person's client
            /// computes it from the audio it is already receiving, so the wearer's value and
            /// theirs track each other without either being sent.</summary>
            Speech,
            /// <summary>Set per viewer, from something that is not the wearer's copy: IsLocal
            /// is true only on your own avatar, and IsOnFriendsList answers whether the person
            /// wearing it is on YOUR friends list. Carrying the wearer's value over would be a
            /// bug and not a simplification.</summary>
            PerViewer,
            /// <summary>Never leaves the client it is on.</summary>
            Local,
        }

        public struct Definition
        {
            public string name;
            public ParamType type;
            public string category;
            public Sync sync;

            public Definition(string name, ParamType type, string category, Sync sync)
            {
                this.name = name;
                this.type = type;
                this.category = category;
                this.sync = sync;
            }
        }

        // Grouped by purpose so the Add menu can build readable submenus.
        //
        // Not here: Expression1..Expression16, which are legacy and documented as travelling on
        // "IK or Playable" without saying which — sixteen entries in the Add menu for a set
        // nothing new should use, and a sync answer that would be a guess.
        //
        // The channel each one travels on is the documented one and not a guess, with the one
        // exception marked below. Note that the split does not follow the groups: the two
        // gesture indices ride with the pose while the two weights beside them ride with the
        // expression sample, and the scale family is split from the locomotion family it reads
        // like a cousin of.
        public static readonly Definition[] All =
        {
            // Locomotion / posture
            new Definition("VelocityX", ParamType.Float, "Locomotion", Sync.Ik),
            new Definition("VelocityY", ParamType.Float, "Locomotion", Sync.Ik),
            new Definition("VelocityZ", ParamType.Float, "Locomotion", Sync.Ik),
            new Definition("VelocityMagnitude", ParamType.Float, "Locomotion", Sync.Ik),
            new Definition("AngularY", ParamType.Float, "Locomotion", Sync.Ik),
            new Definition("Upright", ParamType.Float, "Locomotion", Sync.Ik),
            new Definition("Grounded", ParamType.Bool, "Locomotion", Sync.Ik),
            new Definition("Seated", ParamType.Bool, "Locomotion", Sync.Ik),
            new Definition("AFK", ParamType.Bool, "Locomotion", Sync.Ik),

            // Hands & gestures
            new Definition("GestureLeft", ParamType.Int, "Gestures", Sync.Ik),
            new Definition("GestureRight", ParamType.Int, "Gestures", Sync.Ik),
            new Definition("GestureLeftWeight", ParamType.Float, "Gestures", Sync.Playable),
            new Definition("GestureRightWeight", ParamType.Float, "Gestures", Sync.Playable),

            // Voice & lip sync
            new Definition("Viseme", ParamType.Int, "Voice", Sync.Speech),
            new Definition("Voice", ParamType.Float, "Voice", Sync.Speech),

            // Player state & tracking
            new Definition("IsLocal", ParamType.Bool, "State & Tracking", Sync.PerViewer),
            new Definition("VRMode", ParamType.Int, "State & Tracking", Sync.Ik),
            new Definition("TrackingType", ParamType.Int, "State & Tracking", Sync.Playable),
            new Definition("MuteSelf", ParamType.Bool, "State & Tracking", Sync.Playable),
            new Definition("InStation", ParamType.Bool, "State & Tracking", Sync.Ik),
            new Definition("Earmuffs", ParamType.Bool, "State & Tracking", Sync.Playable),
            new Definition("IsOnFriendsList", ParamType.Bool, "State & Tracking", Sync.PerViewer),
            // On the IK channel, which is easy to disbelieve for a number that changes once a
            // session: the version is part of what a joiner has to be told about the pose it is
            // about to be handed. Third-party tables that call it "None" are older than the
            // official one.
            new Definition("AvatarVersion", ParamType.Int, "State & Tracking", Sync.Ik),
            new Definition("PreviewMode", ParamType.Int, "State & Tracking", Sync.Local),
            new Definition("IsAnimatorEnabled", ParamType.Bool, "State & Tracking", Sync.Local),

            // Avatar scaling
            new Definition("ScaleModified", ParamType.Bool, "Avatar Scaling", Sync.Playable),
            new Definition("ScaleFactor", ParamType.Float, "Avatar Scaling", Sync.Playable),
            new Definition("ScaleFactorInverse", ParamType.Float, "Avatar Scaling", Sync.Playable),
            // The one guess in the table: the official channel list does not name it. Filed
            // with the rest of the scale family, which is documented as playable — a scale
            // number that crossed on a different channel from the ones it is derived beside
            // would be the surprising answer, not this one.
            new Definition("AdjustedScaleFactor", ParamType.Float, "Avatar Scaling", Sync.Playable),
            new Definition("EyeHeightAsMeters", ParamType.Float, "Avatar Scaling", Sync.Playable),
            new Definition("EyeHeightAsPercent", ParamType.Float, "Avatar Scaling", Sync.Playable),
        };

        // ---- what the platform does with them ---------------------------------

        /// <summary>The definition of a built-in by that name, or false for anything the
        /// avatar declared itself.</summary>
        public static bool TryFind(string name, out Definition definition)
        {
            foreach (var candidate in All)
                if (candidate.name == name) { definition = candidate; return true; }
            definition = default;
            return false;
        }

        /// <summary>Whether VRChat owns this name. A built-in is fed by the platform, so it is
        /// neither the avatar's to sync nor the avatar's to declare a meaning for.</summary>
        public static bool IsBuiltIn(string name) => TryFind(name, out _);

        /// <summary>
        /// The ones whose remote reading is not the wearer's own. Playspace movement counts on
        /// somebody else's copy of you and not on yours, so these cross faithfully on the IK
        /// channel and still differ between the two copies — the one shape a wire cannot
        /// reproduce by carrying a value correctly.
        /// </summary>
        public static bool PlayspaceDiffers(string name) =>
            name == "VelocityX" || name == "VelocityY" || name == "VelocityZ"
            || name == "VelocityMagnitude" || name == "AngularY";

        // ---- gestures ---------------------------------------------------------

        /// <summary>VRChat's hand-gesture indices, in value order (0..7). Used to show
        /// GestureLeft / GestureRight thresholds as names instead of raw numbers.</summary>
        public static readonly string[] GestureNames =
        {
            "Neutral", "Fist", "HandOpen", "FingerPoint",
            "Victory", "RockNRoll", "HandGun", "ThumbsUp",
        };

        public static bool IsGestureParameter(string name) =>
            name == "GestureLeft" || name == "GestureRight";

        /// <summary>The gesture name for a condition threshold, or null when the value is
        /// not one of the eight gesture indices (fractional or out of range).</summary>
        public static string GestureLabel(float threshold)
        {
            int index = (int)System.Math.Round(threshold);
            if (index < 0 || index >= GestureNames.Length) return null;
            if (System.Math.Abs(threshold - index) > 0.0001f) return null;
            return GestureNames[index];
        }
    }
}

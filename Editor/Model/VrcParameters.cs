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
        /// </summary>
        public enum Sync
        {
            /// <summary>The wearer's value reaches everybody. Continuously, on the IK, speech
            /// or playable channels, rather than in the expression parameter sample — so it is
            /// neither paced by that cadence nor rounded by it.</summary>
            Broadcast,
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
        public static readonly Definition[] All =
        {
            // Locomotion / posture
            new Definition("VelocityX", ParamType.Float, "Locomotion", Sync.Broadcast),
            new Definition("VelocityY", ParamType.Float, "Locomotion", Sync.Broadcast),
            new Definition("VelocityZ", ParamType.Float, "Locomotion", Sync.Broadcast),
            new Definition("VelocityMagnitude", ParamType.Float, "Locomotion", Sync.Broadcast),
            new Definition("AngularY", ParamType.Float, "Locomotion", Sync.Broadcast),
            new Definition("Upright", ParamType.Float, "Locomotion", Sync.Broadcast),
            new Definition("Grounded", ParamType.Bool, "Locomotion", Sync.Broadcast),
            new Definition("Seated", ParamType.Bool, "Locomotion", Sync.Broadcast),
            new Definition("AFK", ParamType.Bool, "Locomotion", Sync.Broadcast),

            // Hands & gestures
            new Definition("GestureLeft", ParamType.Int, "Gestures", Sync.Broadcast),
            new Definition("GestureRight", ParamType.Int, "Gestures", Sync.Broadcast),
            new Definition("GestureLeftWeight", ParamType.Float, "Gestures", Sync.Broadcast),
            new Definition("GestureRightWeight", ParamType.Float, "Gestures", Sync.Broadcast),

            // Voice & lip sync
            new Definition("Viseme", ParamType.Int, "Voice", Sync.Broadcast),
            new Definition("Voice", ParamType.Float, "Voice", Sync.Broadcast),

            // Player state & tracking
            new Definition("IsLocal", ParamType.Bool, "State & Tracking", Sync.PerViewer),
            new Definition("VRMode", ParamType.Int, "State & Tracking", Sync.Broadcast),
            new Definition("TrackingType", ParamType.Int, "State & Tracking", Sync.Broadcast),
            new Definition("MuteSelf", ParamType.Bool, "State & Tracking", Sync.Broadcast),
            new Definition("InStation", ParamType.Bool, "State & Tracking", Sync.Broadcast),
            new Definition("Earmuffs", ParamType.Bool, "State & Tracking", Sync.Broadcast),
            new Definition("IsOnFriendsList", ParamType.Bool, "State & Tracking", Sync.PerViewer),
            new Definition("AvatarVersion", ParamType.Int, "State & Tracking", Sync.Local),
            new Definition("PreviewMode", ParamType.Int, "State & Tracking", Sync.Local),
            new Definition("IsAnimatorEnabled", ParamType.Bool, "State & Tracking", Sync.Local),

            // Avatar scaling
            new Definition("ScaleModified", ParamType.Bool, "Avatar Scaling", Sync.Broadcast),
            new Definition("ScaleFactor", ParamType.Float, "Avatar Scaling", Sync.Broadcast),
            new Definition("ScaleFactorInverse", ParamType.Float, "Avatar Scaling", Sync.Broadcast),
            new Definition("AdjustedScaleFactor", ParamType.Float, "Avatar Scaling", Sync.Broadcast),
            new Definition("EyeHeightAsMeters", ParamType.Float, "Avatar Scaling", Sync.Broadcast),
            new Definition("EyeHeightAsPercent", ParamType.Float, "Avatar Scaling", Sync.Broadcast),
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

        /// <summary>Whether the wearer's value of this reaches everybody else by itself. True
        /// for most built-ins and false for every parameter an avatar declares — which is the
        /// distinction anything modelling a remote has to make, because the expression
        /// parameter store cannot answer it.</summary>
        public static bool Broadcasts(string name) =>
            TryFind(name, out var definition) && definition.sync == Sync.Broadcast;

        /// <summary>
        /// The ones whose remote reading is not the wearer's own. Playspace movement counts on
        /// somebody else's copy of you and not on yours, so these are broadcast and still
        /// differ between the two copies — the one shape a wire cannot reproduce by carrying a
        /// value faithfully.
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

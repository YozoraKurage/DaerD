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

        public struct Definition
        {
            public string name;
            public ParamType type;
            public string category;

            public Definition(string name, ParamType type, string category)
            {
                this.name = name;
                this.type = type;
                this.category = category;
            }
        }

        // Grouped by purpose so the Add menu can build readable submenus.
        public static readonly Definition[] All =
        {
            // Locomotion / posture
            new Definition("VelocityX", ParamType.Float, "Locomotion"),
            new Definition("VelocityY", ParamType.Float, "Locomotion"),
            new Definition("VelocityZ", ParamType.Float, "Locomotion"),
            new Definition("VelocityMagnitude", ParamType.Float, "Locomotion"),
            new Definition("AngularY", ParamType.Float, "Locomotion"),
            new Definition("Upright", ParamType.Float, "Locomotion"),
            new Definition("Grounded", ParamType.Bool, "Locomotion"),
            new Definition("Seated", ParamType.Bool, "Locomotion"),
            new Definition("AFK", ParamType.Bool, "Locomotion"),

            // Hands & gestures
            new Definition("GestureLeft", ParamType.Int, "Gestures"),
            new Definition("GestureRight", ParamType.Int, "Gestures"),
            new Definition("GestureLeftWeight", ParamType.Float, "Gestures"),
            new Definition("GestureRightWeight", ParamType.Float, "Gestures"),

            // Voice & lip sync
            new Definition("Viseme", ParamType.Int, "Voice"),
            new Definition("Voice", ParamType.Float, "Voice"),

            // Player state & tracking
            new Definition("IsLocal", ParamType.Bool, "State & Tracking"),
            new Definition("VRMode", ParamType.Int, "State & Tracking"),
            new Definition("TrackingType", ParamType.Int, "State & Tracking"),
            new Definition("MuteSelf", ParamType.Bool, "State & Tracking"),
            new Definition("InStation", ParamType.Bool, "State & Tracking"),
            new Definition("Earmuffs", ParamType.Bool, "State & Tracking"),
            new Definition("IsOnFriendsList", ParamType.Bool, "State & Tracking"),
            new Definition("AvatarVersion", ParamType.Int, "State & Tracking"),

            // Avatar scaling
            new Definition("ScaleModified", ParamType.Bool, "Avatar Scaling"),
            new Definition("ScaleFactor", ParamType.Float, "Avatar Scaling"),
            new Definition("ScaleFactorInverse", ParamType.Float, "Avatar Scaling"),
            new Definition("AdjustedScaleFactor", ParamType.Float, "Avatar Scaling"),
        };

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

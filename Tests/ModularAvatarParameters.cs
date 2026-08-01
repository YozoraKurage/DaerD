using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Stand-in for Modular Avatar's MA Parameters component: same type name and serialized
    /// field layout, so the SerializedObject-based store works against it without MA
    /// installed. Lives in its own file (Unity requires that to attach a MonoBehaviour).
    /// </summary>
    class ModularAvatarParameters : MonoBehaviour
    {
        [System.Serializable]
        public struct ParameterConfig
        {
            public string nameOrPrefix;
            public string remapTo;
            public bool internalParameter;
            public bool isPrefix;
            /// <summary>NotSynced = 0, Int = 1, Float = 2, Bool = 3.</summary>
            public int syncType;
            public bool localOnly;
            public float defaultValue;
            public bool saved;
            public bool hasExplicitDefaultValue;
        }

        public List<ParameterConfig> parameters = new List<ParameterConfig>();
    }
}

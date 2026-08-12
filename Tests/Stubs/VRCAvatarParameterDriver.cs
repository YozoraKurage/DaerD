using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Stand-in for the VRCSDK driver: DaerD matches the behaviour by type NAME and talks to
    /// it through SerializedObject, so a same-named class with the same serialized layout
    /// (`parameters` list plus `localOnly`) exercises the exact code path. Only the fields
    /// the tests read are declared; SerializedObject writes to the SDK's other entry fields
    /// simply find no property here, which the writers tolerate.
    /// </summary>
    public class VRCAvatarParameterDriver : StateMachineBehaviour
    {
        [System.Serializable]
        public class Parameter
        {
            public string name;
            public string source;
            public int type;
            public float value;
            public bool convertRange;
            // The range conversion's own four, so a test that copies through it means the
            // same thing with the SDK absent as with it present. Without them the writer
            // finds no property, the reader sees a conversion with no range, and the copy
            // quietly lands on zero — the shape of bug the SDK-less run exists to catch.
            public float sourceMin;
            public float sourceMax;
            public float destMin;
            public float destMax;
            public float chance;
            public float valueMin;
            public float valueMax;
            public bool preventRepeats;
        }

        public bool localOnly;
        public List<Parameter> parameters = new List<Parameter>();
    }
}

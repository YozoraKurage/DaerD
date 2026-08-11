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
        }

        public bool localOnly;
        public List<Parameter> parameters = new List<Parameter>();
    }
}

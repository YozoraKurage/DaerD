using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>Behaviour with data, standing in for an SDK type (found via TypeCache).</summary>
    public class IRTestBehaviour : StateMachineBehaviour
    {
        public string payload;
        public int number;
    }
}

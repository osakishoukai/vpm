

using UdonSharp;
using UnityEngine;

namespace com.osakishokai.vpm.dimmer.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DimmerLogger : UdonSharpBehaviour
    {
        public void Log(string msg)
        {
#if DIMM_DEBUG
            Debug.Log("[DIMM] " + msg);
#endif
        }

        public void LogWarning(string msg)
        {
#if DIMM_DEBUG
            Debug.LogWarning("[DIMM] " + msg);
#endif
        }
    }
}

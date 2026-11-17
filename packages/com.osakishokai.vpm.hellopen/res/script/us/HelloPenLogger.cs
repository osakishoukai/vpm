

using UdonSharp;
using UnityEngine;

namespace com.osakishokai.vpm.hellopen.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HelloPenLogger : UdonSharpBehaviour
    {
        public void Log(string msg)
        {
#if HLPN_DEBUG
            Debug.Log("[HLPN] " + msg);
#endif
        }

        public void LogWarning(string msg)
        {
#if HLPN_DEBUG
            Debug.LogWarning("[HLPN] " + msg);
#endif
        }
    }
}

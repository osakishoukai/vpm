

using UdonSharp;
using UnityEngine;

namespace com.osakishokai.vpm.lightplayer.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LightPlayerLogger : UdonSharpBehaviour
    {
        public void Log(string msg)
        {
#if LPLR_DEBUG
            Debug.Log("[LPLR] " + msg);
#endif
        }

        public void LogWarning(string msg)
        {
#if LPLR_DEBUG
            Debug.LogWarning("[LPLR] " + msg);
#endif
        }
    }
}

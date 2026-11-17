

using UdonSharp;
using UnityEngine;

namespace com.osakishokai.vpm.lightplayer.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class OsakiLightPlayerLogger : UdonSharpBehaviour
    {
        public void Log(string msg)
        {
#if LPLY_DEBUG
            Debug.Log("[LPLY] " + msg);
#endif
        }

        public void LogWarning(string msg)
        {
#if LPLY_DEBUG
            Debug.LogWarning("[LPLY] " + msg);
#endif
        }
    }
}



using UdonSharp;
using UnityEngine;

namespace com.osakishokai.vpm.lightmirror.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class LightMirrorLogger : UdonSharpBehaviour
    {
        public void Log(string msg)
        {
#if LMIR_DEBUG
            Debug.Log("[LMIR] " + msg);
#endif
        }

        public void LogWarning(string msg)
        {
#if LMIR_DEBUG
            Debug.LogWarning("[LMIR] " + msg);
#endif
        }
    }
}

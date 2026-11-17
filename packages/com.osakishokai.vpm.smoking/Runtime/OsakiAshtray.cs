// 吸える煙草の一式の部品。全体の説明は OsakiCigarette.cs の先頭
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Components;

namespace com.osakishokai.vpm.smoking
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
    public class OsakiAshtray : UdonSharpBehaviour
    {
        [Tooltip("置いてからこの秒数だれも持たなければ元の場所へ戻る")]
        public float idleReturnSeconds = 1800f;

        private VRCPickup pickup;
        private VRCObjectSync objectSync;
        private float lastDropTime;
        private bool idleLoop;
        private Vector3 homePos;

        void Start()
        {
            pickup = (VRCPickup)GetComponent(typeof(VRCPickup));
            objectSync = (VRCObjectSync)GetComponent(typeof(VRCObjectSync));
            homePos = transform.position;
        }

        public override void OnPickup()
        {
            idleLoop = false;
        }

        public override void OnDrop()
        {
            lastDropTime = Time.time;
            if (!idleLoop)
            {
                idleLoop = true;
                SendCustomEventDelayedSeconds(nameof(_IdleCheck), 30f);
            }
        }

        public void _ResetByButton()
        {
            idleLoop = false;
        }

        public void _IdleCheck()
        {
            if (!idleLoop) return;
            if (pickup != null && pickup.IsHeld) { idleLoop = false; return; }
            if (Time.time - lastDropTime < idleReturnSeconds)
            {
                SendCustomEventDelayedSeconds(nameof(_IdleCheck), 30f);
                return;
            }
            idleLoop = false;
            if (!Networking.IsOwner(gameObject)) return;
            if (Vector3.Distance(transform.position, homePos) < 0.02f) return;
            if (objectSync != null) objectSync.Respawn();
        }
    }
}

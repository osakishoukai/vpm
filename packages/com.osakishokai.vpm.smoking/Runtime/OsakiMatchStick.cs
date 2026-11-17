// 吸える煙草の一式の部品。全体の説明は OsakiCigarette.cs の先頭
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Components;
using VRC.SDK3.UdonNetworkCalling;
using VRC.Udon.Common.Interfaces;
using com.osakishokai.vpm.audiopool;
using com.osakishokai.vpm.particlepool;

namespace com.osakishokai.vpm.smoking
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
    public class OsakiMatchStick : UdonSharpBehaviour
    {
        [Tooltip("マッチの頭(+Z の端)。炎の位置・煙草との距離")]
        public Transform head;
        public ParticlePool flamePool;

        [Header("音(AudioPool 経由)")]
        public AudioPool sePool;
        public AudioClip strikeClip;

        [Header("調整")]
        [Tooltip("炎が点いている秒")]
        public float flameSeconds = 8f;
        [Tooltip("擦り面に当てた時、頭がこの速さ(m/秒)以上なら火が点く")]
        public float minStrikeSpeed = 0.25f;
        [Tooltip("置いてからこの秒数だれも持たなければ元の場所へ戻る")]
        public float idleReturnSeconds = 1800f;

        [System.NonSerialized] public bool burning;
        [System.NonSerialized] public float burnUntil;

        [System.NonSerialized] public float strikeSpeed;
        [System.NonSerialized] public int strikeCount;

        [System.NonSerialized] public int flameCount;

        private VRCPickup pickup;
        private VRCObjectSync objectSync;
        private bool held;
        private Vector3 prevHead;
        private bool burnLoop;
        private float lastDropTime;
        private bool idleLoop;
        private Vector3 homePos;

        void Start()
        {
            pickup = (VRCPickup)GetComponent(typeof(VRCPickup));
            objectSync = (VRCObjectSync)GetComponent(typeof(VRCObjectSync));
            homePos = transform.position;
        }

        public bool RuleStrike(float speed, bool isBurning)
        {
            return !isBurning && speed >= minStrikeSpeed;
        }

        public bool RuleBurnedOut(bool isBurning, float now, float until)
        {
            return isBurning && now >= until;
        }

        void Update()
        {
            if (!held || head == null) return;
            Vector3 p = head.position;
            float dt = Time.deltaTime;
            if (dt > 0f) strikeSpeed = (p - prevHead).magnitude / dt;
            prevHead = p;
        }

        void OnTriggerEnter(Collider other)
        {
            if (other == null || !held) return;
            if (!other.gameObject.name.StartsWith("Strike")) return;
            if (other.GetComponentInParent<OsakiMatchBox>() == null) return;
            _TryStrike();
        }

        public void _TryStrike()
        {
            if (!Networking.IsOwner(gameObject)) return;
            if (!RuleStrike(strikeSpeed, burning)) return;
            strikeCount++;
            LocalFlame(flameSeconds);
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(ReceiveStrike), flameSeconds);
        }

        [NetworkCallable]
        public void ReceiveStrike(float seconds)
        {
            if (seconds <= 0f || seconds > 60f) return;
            LocalFlame(seconds);
        }

        private void LocalFlame(float seconds)
        {
            burning = true;
            flameCount++;
            burnUntil = Time.time + seconds;
            if (flamePool != null && head != null) flamePool.PlayAsIs(head, false);
            if (sePool != null && strikeClip != null) sePool.Play(strikeClip, transform, false);
            if (!burnLoop)
            {
                burnLoop = true;
                SendCustomEventDelayedSeconds(nameof(_BurnCheck), 0.5f);
            }
        }

        public void _ResetByButton()
        {
            idleLoop = false;
        }

        public void _BurnCheck()
        {
            if (!burning) { burnLoop = false; return; }
            if (RuleBurnedOut(burning, Time.time, burnUntil))
            {
                burnLoop = false;
                burning = false;
                if (flamePool != null && head != null) flamePool.StopTargetEmitting(head);
                return;
            }
            SendCustomEventDelayedSeconds(nameof(_BurnCheck), 0.5f);
        }

        public override void OnPickup()
        {
            held = true;
            idleLoop = false;
            strikeSpeed = 0f;
            if (head != null) prevHead = head.position;
        }

        public override void OnDrop()
        {
            held = false;
            strikeSpeed = 0f;
            lastDropTime = Time.time;
            if (!idleLoop)
            {
                idleLoop = true;
                SendCustomEventDelayedSeconds(nameof(_IdleCheck), 30f);
            }
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

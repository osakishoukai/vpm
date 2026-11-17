// 吸える煙草の一式(SnackYuki 生まれ。ぱん「このタバコはなかなかいい感じ…共通アセット化して他にもこれを適用する」2026-11-17)。
// 置き方: Common/Smoking の prefab(Cigarette・MatchStick・MatchBox・Ashtray・CigPack。まとめて置くなら SmokingSet)を置くだけ。
//   見た目を店に合わせる時は、材質を商品の res に複製して差し替える(マッチ箱の札に店名を入れる等。Common の材質は書き換えない)。
// 遊び方: マッチ棒を持ってマッチ箱の側面(Strike_A/B)を擦る → 頭に炎(8秒) → 煙草の先を炎に近づける → 先が赤く光る(120秒で消える)。
//   火の点いた煙草を持って、口元(頭から 0.3m 以内)で Use = 吸う(先が明るく、火の先から細い煙)→ もう一度 Use = 吐く(1段目の口元の位置から前へ煙)。
//   デスクトップは距離によらず吸える。灰皿に置く(持っていない時に灰皿の中へ入る)と消える。
//   持てる物は置いて 30 分だれも持たなければ元の場所へ戻る。全部戻すボタンを作る商品は _ResetByButton を呼ぶ。
// 同期: VRCObjectSync で持った人が Owner。火・吸う・吐く・消すは Owner が決めて全員へ送り、途中参加者には Owner が送り直す。
// 炎・煙は Common の ParticlePool(あいことば OSAKI_MATCH_FLAME・OSAKI_SMOKE・OSAKI_PUFF で共有して数を絞る)、擦る音は Common の AudioPool。
// 擦る音 SE/OsakiMatchStrike.mp3 は効果音ラボ「マッチで火をつける」(商用可・クレジット不要・作品に組み込むのは可)。
// 作り直し:Tools/Osaki/Common/Build Smoking (UT の Assets/_Dev/Editor/OsakiSmokingBuilder.cs)。
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Components;
using VRC.SDK3.UdonNetworkCalling;
using VRC.Udon.Common.Interfaces;
using com.osakishokai.vpm.particlepool;

namespace com.osakishokai.vpm.smoking
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
    public class OsakiCigarette : UdonSharpBehaviour
    {
        [Header("見た目")]
        [Tooltip("火の先の光(点いている間だけ出す)")]
        public Renderer ember;
        public Material emberDim;
        public Material emberBright;
        [Tooltip("火の側の先(+Z の端)。マッチの頭との距離を測る")]
        public Transform tip;
        [Tooltip("吸い口(フィルターの端)。1段目で口元として覚える")]
        public Transform mouthEnd;
        [Tooltip("煙を出す位置と向きを置く目印(煙は +Z へ出る)")]
        public Transform exhaleAnchor;
        public ParticlePool smokePool;
        [Tooltip("吸う時に火の先から立つ細い煙(火の先に付いて動く)")]
        public ParticlePool puffPool;

        [Header("調整")]
        [Tooltip("火が点いてから自然に消えるまでの秒")]
        public float litSeconds = 120f;
        [Tooltip("1段目で先が明るい秒")]
        public float puffSeconds = 3f;
        [Tooltip("吸い口と頭がこの距離(m)以下で Use すると 1段目(吸う)。吸った後の Use は距離によらず 2段目(吐く)")]
        public float nearDistance = 0.3f;
        [Tooltip("燃えているマッチの頭と煙草の先がこの距離(m)以下で火が点く")]
        public float igniteDistance = 0.05f;
        [Tooltip("置いてからこの秒数だれも持たなければ元の場所へ戻る(火も消える)")]
        public float idleReturnSeconds = 1800f;

        public const int StUnlit = 0;
        public const int StLit = 1;
        public const int StPuffed = 2;
        public const int ActNone = 0;
        public const int ActPuff = 1;
        public const int ActExhale = 2;
        public const int ActIgnite = 3;
        public const int ActOut = 4;

        [System.NonSerialized] public int state;
        [System.NonSerialized] public bool bright;
        [System.NonSerialized] public float litUntil;
        [System.NonSerialized] public int exhaleCount;
        [System.NonSerialized] public int puffCount;
        [System.NonSerialized] public bool inAshtray;
        [System.NonSerialized] public Vector3 savedMouthLocal = new Vector3(0f, -0.07f, 0.08f);

        [System.NonSerialized] public float testDistance;
        [System.NonSerialized] public bool testVR = true;

        private VRCPickup pickup;
        private VRCObjectSync objectSync;
        private float brightUntil;
        private bool expireLoop;
        private OsakiMatchStick nearMatch;
        private bool igniteLoop;
        private float lastDropTime;
        private bool idleLoop;
        private Vector3 homePos;

        void Start()
        {
            pickup = (VRCPickup)GetComponent(typeof(VRCPickup));
            objectSync = (VRCObjectSync)GetComponent(typeof(VRCObjectSync));
            homePos = transform.position;
            ApplyState(StUnlit, false);
            if (!Networking.IsOwner(gameObject))
                SendCustomEventDelayedSeconds(nameof(_RequestSync), Random.Range(2f, 4f));
        }

        public int RuleUseAction(int s, float dist, bool vr)
        {
            if (s != StLit && s != StPuffed) return ActNone;
            if (s == StPuffed) return ActExhale;
            if (!vr) return ActPuff;
            return dist <= nearDistance ? ActPuff : ActNone;
        }

        public Vector3 RuleExhaleDir(Quaternion headRot)
        {
            return headRot * new Vector3(0f, -0.1f, 1f).normalized;
        }

        public int RuleNextState(int s, int act)
        {
            if (s < StUnlit || s > StPuffed) return -1;
            if (act == ActIgnite) return s == StUnlit ? StLit : -1;
            if (act == ActPuff) return s == StUnlit ? -1 : StPuffed;
            if (act == ActExhale) return s == StPuffed ? StLit : -1;
            if (act == ActOut) return s == StUnlit ? -1 : StUnlit;
            return -1;
        }

        public bool RuleCanIgnite(int s, bool matchBurning, float dist)
        {
            return s == StUnlit && matchBurning && dist <= igniteDistance;
        }

        public bool RuleExpired(int s, float now, float until)
        {
            return s != StUnlit && now >= until;
        }

        public bool RuleIsValidState(int s)
        {
            return s >= StUnlit && s <= StPuffed;
        }

        public bool ApplyState(int s, bool brightNow)
        {
            if (!RuleIsValidState(s)) return false;
            state = s;
            bright = s != StUnlit && brightNow;
            if (ember != null)
            {
                ember.gameObject.SetActive(s != StUnlit);
                Material m = bright ? emberBright : emberDim;
                if (m != null) ember.sharedMaterial = m;
            }
            return true;
        }

        public bool TestOnly_SetState(int s, bool brightNow)
        {
            return ApplyState(s, brightNow);
        }

        public override void OnPickupUseDown()
        {
            _Use();
        }

        public void _Use()
        {
            VRCPlayerApi lp = Networking.LocalPlayer;
            if (lp == null) return;
            VRCPlayerApi.TrackingData head = lp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            Vector3 mouthPos = mouthEnd != null ? mouthEnd.position : transform.position;
            float dist = Vector3.Distance(head.position, mouthPos);
            UseWith(dist, lp.IsUserInVR(), head.position, head.rotation);
        }

        public void TestOnly_Use()
        {
            VRCPlayerApi lp = Networking.LocalPlayer;
            Vector3 hp = transform.position;
            Quaternion hr = Quaternion.identity;
            if (lp != null)
            {
                VRCPlayerApi.TrackingData head = lp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
                hp = head.position; hr = head.rotation;
            }
            UseWith(testDistance, testVR, hp, hr);
        }

        private void UseWith(float dist, bool vr, Vector3 headPos, Quaternion headRot)
        {
            if (!Networking.IsOwner(gameObject)) return;
            int act = RuleUseAction(state, dist, vr);

            if (act == ActPuff) OwnerPuff(vr, headPos, headRot);
            else if (act == ActExhale) OwnerExhale(headPos, headRot);
        }

        private void OwnerPuff(bool vr, Vector3 headPos, Quaternion headRot)
        {
            if (RuleNextState(state, ActPuff) < 0) return;
            Quaternion inv = Quaternion.Inverse(headRot);
            if (vr && mouthEnd != null)
            {
                savedMouthLocal = inv * (mouthEnd.position - headPos);
            }
            else
            {
                savedMouthLocal = new Vector3(0f, -0.07f, 0.08f);
            }
            LocalPuff();
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(ReceivePuff));
        }

        private void OwnerExhale(Vector3 headPos, Quaternion headRot)
        {
            if (RuleNextState(state, ActExhale) < 0) return;
            Vector3 pos = headPos + headRot * savedMouthLocal;
            Vector3 dir = RuleExhaleDir(headRot);
            LocalExhale(pos, dir);
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(ReceiveExhale), pos, dir);
        }

        private void LocalIgnite(float seconds)
        {
            litUntil = Time.time + seconds;
            ApplyState(StLit, false);
            if (!expireLoop)
            {
                expireLoop = true;
                SendCustomEventDelayedSeconds(nameof(_ExpireCheck), 1f);
            }
        }

        private void LocalPuff()
        {
            if (state == StUnlit) return;
            puffCount++;
            brightUntil = Time.time + puffSeconds;
            ApplyState(StPuffed, true);
            if (puffPool != null && tip != null) puffPool.PlayAsIs(tip, false);
            SendCustomEventDelayedSeconds(nameof(_BrightOff), puffSeconds + 0.05f);
        }

        private void LocalExhale(Vector3 pos, Vector3 dir)
        {
            if (state == StUnlit) return;
            ApplyState(StLit, false);
            exhaleCount++;
            if (exhaleAnchor == null || smokePool == null) return;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            exhaleAnchor.SetPositionAndRotation(pos, Quaternion.LookRotation(dir.normalized));
            smokePool.PlayAsIs(exhaleAnchor, true);
        }

        private void LocalOut()
        {
            ApplyState(StUnlit, false);
        }

        public void _BrightOff()
        {
            if (!bright || Time.time < brightUntil) return;
            ApplyState(state, false);
        }

        public void _ExpireCheck()
        {
            if (state == StUnlit) { expireLoop = false; return; }
            if (RuleExpired(state, Time.time, litUntil))
            {
                expireLoop = false;
                LocalOut();
                return;
            }
            SendCustomEventDelayedSeconds(nameof(_ExpireCheck), 1f);
        }

        public void _OwnerIgnite()
        {
            if (!Networking.IsOwner(gameObject)) return;
            if (RuleNextState(state, ActIgnite) < 0) return;
            LocalIgnite(litSeconds);
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(ReceiveIgnite), litSeconds);
        }

        public void _ResetByButton()
        {
            idleLoop = false;
            if (state != StUnlit) _OwnerOut();
        }

        public void _OwnerOut()
        {
            if (!Networking.IsOwner(gameObject)) return;
            if (RuleNextState(state, ActOut) < 0) return;
            LocalOut();
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(ReceiveOut));
        }

        [NetworkCallable]
        public void ReceiveIgnite(float seconds)
        {
            if (seconds <= 0f || seconds > 3600f) return;
            LocalIgnite(seconds);
        }

        [NetworkCallable]
        public void ReceivePuff()
        {
            LocalPuff();
        }

        [NetworkCallable]
        public void ReceiveExhale(Vector3 pos, Vector3 dir)
        {
            LocalExhale(pos, dir);
        }

        [NetworkCallable]
        public void ReceiveOut()
        {
            LocalOut();
        }

        [NetworkCallable]
        public void ReceiveState(int s, float remaining)
        {
            if (!RuleIsValidState(s)) return;
            if (s == StUnlit || remaining <= 0f) { LocalOut(); return; }
            if (state == StUnlit) LocalIgnite(remaining);
            else litUntil = Time.time + remaining;
        }

        public void _RequestSync()
        {
            if (Networking.IsOwner(gameObject)) return;
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(RequestState));
        }

        [NetworkCallable]
        public void RequestState()
        {
            if (!Networking.IsOwner(gameObject)) return;
            float remaining = state == StUnlit ? 0f : Mathf.Max(0f, litUntil - Time.time);
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(ReceiveState), remaining > 0f ? state : StUnlit, remaining);
        }

        void OnTriggerEnter(Collider other)
        {
            if (other == null) return;
            OsakiMatchStick m = other.GetComponentInParent<OsakiMatchStick>();
            if (m != null)
            {
                nearMatch = m;
                if (!igniteLoop) { igniteLoop = true; _IgniteCheck(); }
                return;
            }
            OsakiAshtray a = other.GetComponentInParent<OsakiAshtray>();
            if (a != null)
            {
                inAshtray = true;
                if (!_IsHeld()) _OwnerOut();
            }
        }

        void OnTriggerExit(Collider other)
        {
            if (other == null) return;
            OsakiMatchStick m = other.GetComponentInParent<OsakiMatchStick>();
            if (m != null && m == nearMatch) { nearMatch = null; return; }
            OsakiAshtray a = other.GetComponentInParent<OsakiAshtray>();
            if (a != null) inAshtray = false;
        }

        public void _IgniteCheck()
        {
            if (nearMatch == null) { igniteLoop = false; return; }
            if (Networking.IsOwner(gameObject) && tip != null && nearMatch.head != null)
            {
                float d = Vector3.Distance(nearMatch.head.position, tip.position);
                if (RuleCanIgnite(state, nearMatch.burning, d)) _OwnerIgnite();
            }
            SendCustomEventDelayedSeconds(nameof(_IgniteCheck), 0.2f);
        }

        public bool _IsHeld()
        {
            return pickup != null && pickup.IsHeld;
        }

        public override void OnPickup()
        {
            idleLoop = false;
        }

        public override void OnDrop()
        {
            if (inAshtray) _OwnerOut();
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
            if (_IsHeld()) { idleLoop = false; return; }
            if (Time.time - lastDropTime < idleReturnSeconds)
            {
                SendCustomEventDelayedSeconds(nameof(_IdleCheck), 30f);
                return;
            }
            idleLoop = false;
            if (!Networking.IsOwner(gameObject)) return;
            _OwnerOut();
            if (Vector3.Distance(transform.position, homePos) < 0.02f) return;
            if (objectSync != null) objectSync.Respawn();
        }
    }
}

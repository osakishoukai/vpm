// 持って運べるBGMの箱(ラジオ・オルゴール等)。見た目のメッシュと曲だけを差し替えて使う。
// 置き方: BgmBox.prefab を置き、MeshFilter のメッシュ・MeshRenderer のマテリアル・AudioSource の曲を差し替えて、
// インスペクターの「メッシュに合わせる」を押す(当たり判定・音符・表示の位置をメッシュに合わせる)。
// 持てる・投げられる(重力あり)。持って Use で 近域 → 全域 → OFF を順に切り替える(全員に同期)。
//   VRCPickup は AutoHold=No のまま(VR はグリップを離したら離れる・投げられる。Yes にすると VR で投げにくい。ぱん 2026-11-07)。
//   No の Pickup には Use が届かないので、切り替えは機種ごとに拾う:
//     デスクトップ: 入室時にその人の手元だけ AutoHold=Yes にする → クリックで持つ → クリックで切り替え(OnPickupUseDown)→ 手放すキーで置く・投げる
//     VR: グリップで持っている間のトリガー(InputUse。持っている手のもの)で切り替える
//   試験: Tools > Osaki > Common > BgmBox Desktop Test(ClientSim でデスクトップの入力を送る。UT の Assets/_Dev/Editor/BgmBoxTest)
//   近域: 1〜16mでなだらかに減衰(立体音響) / 全域: 距離に関係なくワールド全体で同じ大きさ(2D) / OFF: 一時停止
// 同期: 持った人が Owner(VRCObjectSync)。Owner が次の状態を決めて全員へ送り、途中参加者には Owner が数秒後に送り直す
// 切り替えたら箱の上に2秒だけ文字で出す(TextMeshPro。日本語は VRChat 内蔵の Noto へフォールバックするので容量は増えない)。
// 入室した時の状態は 近域。startOff を立てると OFF から始まる(同じワールドで LightPlayer など別の曲が鳴る時。1.1.0)。
// 鳴っている間だけ音符が昇る。曲の大きさがそろっていない時は AudioSource の Volume で合わせる(既定 0.2 は -11.6 LUFS の曲で決めた値)。
using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRC.SDK3.UdonNetworkCalling;
using VRC.Udon.Common.Interfaces;

namespace com.osakishokai.vpm.bgmbox
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
    public class OsakiBgmBox : UdonSharpBehaviour
    {
        const int Near = 0, World = 1, Off = 2;

        [SerializeField] private AudioSource source;
        [SerializeField] private GameObject notes;
        [SerializeField] private TextMeshPro label;
        [SerializeField] private GameObject labelObject; // label の GameObject(TextMeshPro の transform・gameObject は Udon に公開されていない)
        [SerializeField] private string nearText = "♪ 近域";
        [SerializeField] private string worldText = "♪ 全域";
        [SerializeField] private string offText = "OFF";
        [SerializeField] private float labelSeconds = 2f;
        [Tooltip("入室した時は止めておく(OFF から始める)。同じワールドで LightPlayer など別の曲が鳴る時に")]
        [SerializeField] private bool startOff;

        private int mode = Near;
        private bool gotMode;   // Start より前に Owner の状態が届いた(startOff で上書きしない)
        private int labelSerial;

        private VRC_Pickup pickup;
        private bool held;      // 自分が持っている
        private bool isVr;

        void Start()
        {
            if (labelObject != null) labelObject.SetActive(false);
            if (startOff && !gotMode) mode = Off;
            Apply(false);
            SetupLocalHold();
        }

        // デスクトップの人の手元だけ AutoHold=Yes(No だとクリックを離した時に落ち、Use も届かない)。VR は prefab の No のまま
        private void SetupLocalHold()
        {
            var me = Networking.LocalPlayer;
            if (!Utilities.IsValid(me)) return;
            isVr = me.IsUserInVR();
            if (pickup == null) pickup = (VRC_Pickup)GetComponent(typeof(VRC_Pickup));
            if (pickup != null && !isVr) pickup.AutoHold = VRC_Pickup.AutoHoldMode.Yes;
        }

        public override void OnPickup() { held = true; }
        public override void OnDrop() { held = false; }

        // デスクトップ(AutoHold=Yes)の Use
        public override void OnPickupUseDown()
        {
            if (!isVr) NextMode();
        }

        // VR(AutoHold=No なので Use が来ない)は、持っている手のトリガーを拾う
        public override void InputUse(bool value, UdonInputEventArgs args)
        {
            if (!value || !held || !isVr || pickup == null) return;
            bool left = pickup.currentHand == VRC_Pickup.PickupHand.Left;
            if ((args.handType == HandType.LEFT) == left) NextMode();
        }

        // 持っている人は Owner(VRCObjectSync が持った人へ移す)
        public void NextMode()
        {
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(SetMode), (mode + 1) % 3, true);
        }

        [NetworkCallable(10)]
        public void SetMode(int m, bool show)
        {
            if (m < Near || m > Off) return;
            gotMode = true;
            if (m == mode && !show) return;
            mode = m;
            Apply(show);
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (player == null) return;
            if (player.isLocal) { SetupLocalHold(); return; } // Start の時に LocalPlayer がまだ無かった場合
            if (Networking.IsOwner(gameObject)) SendCustomEventDelayedSeconds(nameof(_Broadcast), Random.Range(3f, 6f));
        }

        public void _Broadcast()
        {
            if (!Networking.IsOwner(gameObject)) return;
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(SetMode), mode, false);
        }

        private void Apply(bool show)
        {
            if (source == null) return;
            if (mode == Off)
            {
                source.Pause();
            }
            else
            {
                bool world = mode == World;
                source.spatialBlend = world ? 0f : 1f;
                source.spatialize = !world;
                source.UnPause();
                if (!source.isPlaying) source.Play();
            }
            if (notes != null) notes.SetActive(mode != Off);
            if (show) ShowLabel(mode == Near ? nearText : mode == World ? worldText : offText);
        }

        private void ShowLabel(string text)
        {
            if (label == null || labelObject == null) return;
            label.text = text;
            labelObject.SetActive(true);
            labelSerial++;
            SendCustomEventDelayedSeconds(nameof(_HideLabel), labelSeconds);
        }

        // 続けて切り替えた時は、最後の表示から labelSeconds 後に消す
        public void _HideLabel()
        {
            labelSerial--;
            if (labelSerial <= 0 && labelObject != null)
            {
                labelSerial = 0;
                labelObject.SetActive(false);
            }
        }

        // 表示中だけ見ている人へ向ける
        void Update()
        {
            if (labelObject == null || !labelObject.activeSelf) return;
            var me = Networking.LocalPlayer;
            if (me == null) return;
            var head = me.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
            var t = labelObject.transform;
            var d = t.position - head;
            d.y = 0f;
            if (d.sqrMagnitude > 0.0001f) t.rotation = Quaternion.LookRotation(d);
        }
    }
}

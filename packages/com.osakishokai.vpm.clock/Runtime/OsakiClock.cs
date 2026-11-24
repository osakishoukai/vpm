// 時計の針を、見ている人の PC の今の時刻に合わせる(ぱん官能 SnackYuki #56「時計型の物は必ず正しい時刻を示す」)。
// 付け方: 時計の物にこのコンポーネントを足し、針の Transform を欄に入れる(使わない針は空でよい)。
//   針は 12 時を指した形で置く。その向きを 0 として、針の局所の axis の周りに回す。
//   回る向きが逆なら axis を反対(既定 (0,0,1) → (0,0,-1))にする。
// 針を別メッシュにできない文字盤(針を絵で描く)なら、シェーダーで _VRChatTimeEncoded1/2 を読む(StationBoard の StationClock.shader)。
// 1 秒に 1 回だけ回す(毎フレームの処理なし)。同期しない(時刻は各自の手元で正しい)。
using System;
using UdonSharp;
using UnityEngine;

namespace com.osakishokai.vpm.clock
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class OsakiClock : UdonSharpBehaviour
    {
        public Transform hourHand;
        public Transform minuteHand;
        public Transform secondHand;
        [Tooltip("針が回る軸(針の局所)。前から見て右回りになる向き")]
        public Vector3 axis = new Vector3(0f, 0f, 1f);

        private Quaternion hourRest, minuteRest, secondRest;

        void Start()
        {
            if (hourHand != null) hourRest = hourHand.localRotation;
            if (minuteHand != null) minuteRest = minuteHand.localRotation;
            if (secondHand != null) secondRest = secondHand.localRotation;
            _Tick();
        }

        public void _Tick()
        {
            DateTime now = DateTime.Now;
            int h = now.Hour, m = now.Minute, s = now.Second;
            Turn(hourHand, hourRest, ((h % 12) + m / 60f + s / 3600f) * 30f);
            Turn(minuteHand, minuteRest, (m + s / 60f) * 6f);
            Turn(secondHand, secondRest, s * 6f);
            // 次の秒の頭の少し後に打つ(秒針が秒の途中で動かないように)
            SendCustomEventDelayedSeconds(nameof(_Tick), 1.02f - now.Millisecond * 0.001f);
        }

        private void Turn(Transform t, Quaternion rest, float deg)
        {
            if (t != null) t.localRotation = rest * Quaternion.AngleAxis(deg, axis);
        }
    }
}

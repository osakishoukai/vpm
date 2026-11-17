// 放すと真下の面へピタッと下りて、立てて置かれる(コップ・瓶・パン・小物)。KomugiNoShippo から(ぱん官能 KomugiNoShippo #72)。
// 付け方: OsakiPickupSetup.SetupPickup で作った持てる物(VRC_Pickup・VRCObjectSync・kinematic・層 13)に、このコンポーネントを足すだけ。
// 動き: 放した人(Owner)の手元で、物の底から真下へ reach m 以内の面を探し、見つかればそこへ置いて向きを水平の回転だけにする。
//       位置は VRCObjectSync が全員へ配る(この部品は通信しない)。面が無ければ放した所に浮いたまま(kinematic のまま)。
// 面: Default・Environment・Walkthrough(17) のトリガーでないコライダー。人を止めたくない机・棚は、天板に合わせた
//     見えない BoxCollider を層 17(Walkthrough)で置く(人はすり抜け、物だけ載る)。
// 寝かせて置く物(煙草・ペン等)には付けない(必ず立てる)。
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Components;

namespace com.osakishokai.vpm.settledrop
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
    public class OsakiSettleDrop : UdonSharpBehaviour
    {
        [Tooltip("放した時の底から、この深さ(m)までの面に下りる")]
        public float reach = 0.30f;
        [Tooltip("下りる先にする層。既定 = Default・Environment・Walkthrough(17)")]
        public LayerMask surfaceMask = (1 << 0) | (1 << 11) | (1 << 17);

        private float bottom;

        void Start()
        {
            // 置かれた姿(立てた姿)の、Transform の位置から見た見た目の底の深さ。pivot が底なら 0
            Renderer[] rs = GetComponentsInChildren<Renderer>();
            float minY = transform.position.y;
            for (int i = 0; i < rs.Length; i++) if (rs[i] != null && rs[i].enabled) minY = Mathf.Min(minY, rs[i].bounds.min.y);
            bottom = transform.position.y - minY;
        }

        public override void OnDrop()
        {
            // 放した直後の 1 フレームは VRChat が手の位置を書くので、次のフレームで置く
            SendCustomEventDelayedFrames(nameof(_Settle), 1);
        }

        public void _Settle()
        {
            if (!Networking.IsOwner(gameObject)) return;
            VRCPickup pu = (VRCPickup)GetComponent(typeof(VRCPickup));
            if (pu != null && pu.IsHeld) return;
            Vector3 foot = transform.position + Vector3.down * bottom;
            RaycastHit hit;
            if (!Physics.Raycast(foot + Vector3.up * 0.05f, Vector3.down, out hit, reach + 0.05f, surfaceMask, QueryTriggerInteraction.Ignore)) return;
            transform.SetPositionAndRotation(hit.point + Vector3.up * bottom, Quaternion.Euler(0f, transform.rotation.eulerAngles.y, 0f));
        }
    }
}

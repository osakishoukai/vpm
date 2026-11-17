

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace com.osakishokai.vpm.hellopen.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HelloPenEraser : UdonSharpBehaviour
    {
        public HelloPenBoard board;
        [Tooltip("先の端。消しゴムの真ん中(この物の原点)から先までを、両側へ同じ長さで消す所にする")]
        public Transform tip;
        [Tooltip("消している間だけ出す印(消す範囲)")]
        public GameObject activeMark;
        public float radius = 0.025f;
        [Tooltip("消しゴムの軸に沿って当たりを調べる点の数(両端を含む)")]
        public int samples = 5;
        public float rayRadius = 0.02f;
        public float interval = 0.05f;

        Vector3 homePos;
        Quaternion homeRot;
        VRC_Pickup pickup;
        bool held;
        bool erasing;
        float next;

        void Start()
        {
            homePos = transform.position;
            homeRot = transform.rotation;
            pickup = (VRC_Pickup)GetComponent(typeof(VRC_Pickup));
            SetErasing(false);
        }

        public override void OnPickup() { held = true; }
        public override void OnDrop() { held = false; SetErasing(false); }
        public override void OnPickupUseDown() { SetErasing(true); next = 0f; }
        public override void OnPickupUseUp() { SetErasing(false); }

        void SetErasing(bool on)
        {
            erasing = on;
            if (activeMark != null) activeMark.SetActive(on);
        }

        void Update()
        {
            if (!held || !erasing || board == null || tip == null || Time.time < next) return;
            next = Time.time + interval;
            VRCPlayerApi lp = Networking.LocalPlayer;
            if (lp != null && !lp.IsUserInVR())
            {
                VRCPlayerApi.TrackingData h = lp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
                board.EraseRay(h.position, h.rotation * Vector3.forward, rayRadius);
            }

            Vector3 c = transform.position;
            Vector3 half = tip.position - c;
            int k = samples < 2 ? 2 : samples;
            for (int i = 0; i < k; i++) board.EraseAt(c + half * (2f * i / (k - 1) - 1f), radius);
        }

        public void ReturnHome()
        {
            if (pickup != null && pickup.IsHeld) pickup.Drop();
            SetErasing(false);
            transform.SetPositionAndRotation(homePos, homeRot);
        }
    }
}

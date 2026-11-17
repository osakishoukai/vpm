

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace com.osakishokai.vpm.hellopen.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HelloPenPen : UdonSharpBehaviour
    {
        public HelloPenBoard board;
        public int colorIndex;
        [Tooltip("ペン先(書く点)。動かさない。差し替えモデルの先をここへ合わせる")]
        public Transform tip;
        [Tooltip("差し替えモデルの口。お好きなペンのモデルをこの下に置く(元の Body は消すか非表示に)。位置・向き・大きさは台(Board)の「差し替えモデル」で全部のペンに同じだけ掛かる")]
        public Transform model;
        [Tooltip("書く面の欠片(書く人にだけ見える)")]
        public Transform fragment;
        public Renderer fragmentRenderer;
        [Tooltip("自分が書いている途中の線")]
        public LineRenderer preview;

        [Header("書き心地")]
        [Tooltip("手ぶれのならし(秒)。大きいほどなめらかで遅れる")]
        public float smoothTime = 0.03f;
        [Tooltip("点を足す最小の間隔(m)")]
        public float minStep = 0.0015f;
        [Tooltip("書かないまま、この秒数で面の固定が外れる")]
        public float idleUnfix = 10f;
        [Tooltip("書いている途中の線を他の人へ送る間隔(秒)")]
        public float liveInterval = 0.25f;
        public float fragmentAlphaFree = 0.25f;
        public float fragmentAlphaFixed = 0.6f;

        const int MAXP = 300;

        Vector3 homePos;
        Quaternion homeRot;
        VRC_Pickup pickup;
        bool held;
        bool vr;

        bool planeFixed;
        Vector3 pOrigin;
        Quaternion pRot;
        int blockId;
        int blockTime;
        float lastWrite;

        bool drawing;
        Vector2[] pts = new Vector2[MAXP];
        int n;
        Vector2 sm;
        int strokeId;
        float nextLive;
        Vector3[] wbuf = new Vector3[MAXP + 1];
        MaterialPropertyBlock mpb;

        void Start()
        {
            homePos = transform.position;
            homeRot = transform.rotation;
            pickup = (VRC_Pickup)GetComponent(typeof(VRC_Pickup));
            if (fragment != null) fragment.gameObject.SetActive(false);
            if (preview != null)
            {
                preview.positionCount = 0;
                Color c = board != null && colorIndex < board.palette.Length ? board.palette[colorIndex] : Color.white;
                preview.startColor = c; preview.endColor = c;
                if (board != null) preview.widthMultiplier = board.LineRendererWidth();

                preview.transform.SetParent(null, false);
            }
            mpb = new MaterialPropertyBlock();
        }

        public override void OnPickup()
        {
            held = true;
            vr = Networking.LocalPlayer != null && Networking.LocalPlayer.IsUserInVR();
            planeFixed = false;
            if (fragment != null) fragment.gameObject.SetActive(true);
        }

        public override void OnDrop()
        {
            if (drawing) EndStroke();
            held = false;
            planeFixed = false;
            if (fragment != null) fragment.gameObject.SetActive(false);
        }

        public override void OnPickupUseDown() { BeginStroke(); }

        public override void OnPickupUseUp() { if (drawing) EndStroke(); }

        public void ReturnHome()
        {
            if (pickup != null && pickup.IsHeld) pickup.Drop();
            transform.SetPositionAndRotation(homePos, homeRot);
        }

        void HeadRay(out Vector3 o, out Vector3 d, out Quaternion r)
        {
            VRCPlayerApi lp = Networking.LocalPlayer;
            VRCPlayerApi.TrackingData h = lp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            o = h.position; r = h.rotation; d = h.rotation * Vector3.forward;
        }

        Quaternion FacingRot(Vector3 at)
        {
            Vector3 o, d; Quaternion hr;
            HeadRay(out o, out d, out hr);
            Vector3 f = at - o;
            if (f.sqrMagnitude < 0.0004f) f = d;
            f.Normalize();
            Vector3 up = Mathf.Abs(Vector3.Dot(f, Vector3.up)) > 0.95f ? hr * Vector3.up : Vector3.up;
            return Quaternion.LookRotation(f, up);
        }

        void FixPlane()
        {
            pOrigin = tip.position;
            pRot = FacingRot(pOrigin);
            planeFixed = true;
            blockId = board.NewId();
            blockTime = HelloPenBoard.NowUnix();
        }

        bool PenPoint(out Vector2 p)
        {
            Vector3 w = tip.position;
            if (!vr)
            {
                Vector3 o, d; Quaternion hr;
                HeadRay(out o, out d, out hr);
                Vector3 nrm = pRot * Vector3.forward;
                float den = Vector3.Dot(d, nrm);
                if (Mathf.Abs(den) > 1e-4f)
                {
                    float t = Vector3.Dot(pOrigin - o, nrm) / den;
                    if (t > 0f && t < 20f) w = o + d * t;
                }
            }
            Vector3 l = Quaternion.Inverse(pRot) * (w - pOrigin);
            p = new Vector2(l.x, l.y);
            return true;
        }

        Vector3 ToWorld(Vector2 p) { return pOrigin + pRot * new Vector3(p.x, p.y, 0f); }

        void BeginStroke()
        {
            if (board == null || tip == null) return;
            if (!planeFixed) FixPlane();
            drawing = true;
            n = 0;
            strokeId = board.NewId();
            PenPoint(out sm);
            AddPoint(sm);
            nextLive = Time.time + liveInterval;
            lastWrite = Time.time;
        }

        void EndStroke()
        {
            drawing = false;
            lastWrite = Time.time;
            if (n > 0) board.PenStroke(blockId, blockTime, pOrigin, pRot, strokeId, colorIndex, pts, n, false);
            n = 0;
            if (preview != null) preview.positionCount = 0;
        }

        void AddPoint(Vector2 p)
        {
            if (n >= MAXP)
            {

                Vector2 last = pts[n - 1];
                board.PenStroke(blockId, blockTime, pOrigin, pRot, strokeId, colorIndex, pts, n, false);
                strokeId = board.NewId();
                n = 0;
                pts[n++] = last;
            }
            pts[n++] = p;
        }

        void Update()
        {
            if (!held) return;
            if (planeFixed && !drawing && Time.time - lastWrite > idleUnfix) planeFixed = false;

            if (drawing)
            {
                Vector2 raw;
                PenPoint(out raw);
                float a = smoothTime > 0f ? 1f - Mathf.Exp(-Time.deltaTime / smoothTime) : 1f;
                sm += (raw - sm) * a;
                Vector2 last = pts[n - 1];
                float dist = Vector2.Distance(sm, last);
                if (dist >= minStep)
                {

                    int steps = Mathf.CeilToInt(dist / 0.03f);
                    for (int i = 1; i <= steps; i++) AddPoint(Vector2.Lerp(last, sm, (float)i / steps));
                }
                DrawPreview();
                if (Time.time >= nextLive)
                {
                    nextLive = Time.time + liveInterval;
                    board.PenStroke(blockId, blockTime, pOrigin, pRot, strokeId, colorIndex, pts, n, true);
                }
                lastWrite = Time.time;
            }
            UpdateFragment();
        }

        void DrawPreview()
        {
            if (preview == null) return;

            float k = HelloPenBoard.LineScale;
            for (int i = 0; i < n; i++) wbuf[i] = new Vector3(pts[i].x * k, pts[i].y * k, 0f);
            wbuf[n] = new Vector3(sm.x * k, sm.y * k, 0f);
            preview.transform.SetPositionAndRotation(pOrigin, pRot);
            preview.transform.localScale = new Vector3(1f / k, 1f / k, 1f / k);
            preview.positionCount = n + 1;
            preview.SetPositions(wbuf);
        }

        void UpdateFragment()
        {
            if (fragment == null) return;
            if (planeFixed)
            {
                Vector2 p;
                PenPoint(out p);
                fragment.SetPositionAndRotation(ToWorld(p), pRot);
            }
            else
            {
                fragment.SetPositionAndRotation(tip.position, FacingRot(tip.position));
            }
            if (fragmentRenderer != null)
            {
                mpb.SetFloat("_Alpha", planeFixed ? fragmentAlphaFixed : fragmentAlphaFree);
                fragmentRenderer.SetPropertyBlock(mpb);
            }
        }

        public bool TestOnly_IsPlaneFixed() { return planeFixed; }

        public void TestOnly_LiveStroke(Vector3 origin, Quaternion rot, Vector2[] p, bool newStroke, bool commit)
        {
            if (newStroke)
            {
                pOrigin = origin; pRot = rot; planeFixed = true;
                blockId = board.NewId();
                blockTime = HelloPenBoard.NowUnix();
                strokeId = board.NewId();
            }
            n = 0;
            for (int i = 0; i < p.Length && i < MAXP; i++) pts[n++] = p[i];
            board.PenStroke(blockId, blockTime, pOrigin, pRot, strokeId, colorIndex, pts, n, !commit);
            if (commit) n = 0;
            lastWrite = Time.time;
        }

        public void TestOnly_WriteStroke(Vector3 origin, Quaternion rot, Vector2[] p, bool newBlock)
        {
            if (newBlock || !planeFixed)
            {
                pOrigin = origin; pRot = rot; planeFixed = true;
                blockId = board.NewId();
                blockTime = HelloPenBoard.NowUnix();
            }
            strokeId = board.NewId();
            n = 0;
            for (int i = 0; i < p.Length && i < MAXP; i++) pts[n++] = p[i];
            board.PenStroke(blockId, blockTime, pOrigin, pRot, strokeId, colorIndex, pts, n, true);
            board.PenStroke(blockId, blockTime, pOrigin, pRot, strokeId, colorIndex, pts, n, false);
            n = 0;
            lastWrite = Time.time;
        }
    }
}

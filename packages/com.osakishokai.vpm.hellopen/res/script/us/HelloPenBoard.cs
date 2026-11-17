

using UdonSharp;
using UnityEngine;
using TMPro;
using VRC.SDKBase;
using VRC.SDK3.Persistence;
using VRC.SDK3.UdonNetworkCalling;
using VRC.Udon.Common.Interfaces;

namespace com.osakishokai.vpm.hellopen.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class HelloPenBoard : UdonSharpBehaviour
    {
        [Header("部品")]
        public HelloPenLogger log;
        public LineRenderer lineTemplate;
        public TextMeshPro labelTemplate;
        [Tooltip("他の人が書いている途中の線の先に出す、小さなペン先の印。色はパレットの升を _MainTex_ST でずらして出す")]
        public Renderer tipMarkTemplate;
        public Transform linesRoot;
        public HelloPenPen[] pens;
        public HelloPenEraser eraser;

        [Header("見た目")]
        public Color[] palette;
        public float lineWidth = 0.004f;
        public float labelGap = 0.012f;

        [Header("差し替えモデル")]
        [Tooltip("各ペンの Model(差し替えモデルの口)の位置(m)。ペン先(Tip)と持つ当たりは動かないので、モデルの先が Tip に合うよう動かす")]
        public Vector3 modelPosition = Vector3.zero;
        [Tooltip("各ペンの Model の向き(度)。ペンの先は +Z")]
        public Vector3 modelRotation = Vector3.zero;
        [Tooltip("各ペンの Model の大きさ(倍)")]
        public float modelScale = 1f;

        [Header("保存")]
        [Tooltip("PlayerData のキー。1つのワールドに台を2つ置く時は別にする")]
        public string saveKey = "HelloPen.lines";
        [Tooltip("線の上限(保存の大きさ、バイト)。PlayerData の上限 100KB(圧縮後)に収まるよう、圧縮が効かなくても入る値")]
        public int maxBytes = 95000;
        [Tooltip("線が変わってから保存するまでの待ち(秒)の下限。上限は 下限+人数×saveDelayPerPlayer(saveDelayCap まで)")]
        public float saveDelayMin = 10f;
        public float saveDelayPerPlayer = 2f;
        public float saveDelayCap = 60f;
        [Tooltip("自分の保存どうしの最短の間隔(秒)")]
        public float saveMinInterval = 15f;
        float lastSaveTime;

        const int MAXB = 512;
        const int MAXS = 4096;
        const byte VER = 2;

        const float Q2 = 4000f, Q1 = 1000f;

        public const float LineScale = 10f;
        const int CHUNK = 8000;

        int bN;
        int[] bId = new int[MAXB];
        string[] bAuthor = new string[MAXB];
        int[] bTime = new int[MAXB];
        Vector3[] bOrigin = new Vector3[MAXB];
        Quaternion[] bRot = new Quaternion[MAXB];
        TextMeshPro[] bLabel = new TextMeshPro[MAXB];
        GameObject[] bLabelGo = new GameObject[MAXB];

        int sN;
        int[] sId = new int[MAXS];
        int[] sBlock = new int[MAXS];
        byte[] sColor = new byte[MAXS];
        Vector2[][] sPts = new Vector2[MAXS][];
        LineRenderer[] sLine = new LineRenderer[MAXS];
        bool[] sLive = new bool[MAXS];
        float[] sLiveT = new float[MAXS];
        GameObject[] sMark = new GameObject[MAXS];
        MaterialPropertyBlock markMpb;

        int[] tomb = new int[256];
        int tombPos;

        int idCounter;
        bool haveState;
        bool creator;
        bool restored;
        bool saveScheduled;
        bool snapScheduled;
        bool visible = true;

        int rxSnapId;
        byte[][] rxParts;
        int rxGot;

        void Start()
        {
            ApplyModelAdjust();

            if (lineTemplate != null && lineTemplate.sharedMaterial != null)
                lineTemplate.sharedMaterial.SetFloat("_HalfWidth", lineWidth * 0.5f);
        }

        public float LineRendererWidth()
        {
#if UNITY_ANDROID
            return lineWidth * LineScale;
#else
            return 0f;
#endif
        }

        public void ApplyModelAdjust()
        {
            if (pens == null) return;
            for (int i = 0; i < pens.Length; i++)
            {
                if (pens[i] == null || pens[i].model == null) continue;
                Transform m = pens[i].model;
                m.localPosition = modelPosition;
                m.localRotation = Quaternion.Euler(modelRotation);
                m.localScale = Vector3.one * modelScale;
            }
        }

#if !COMPILER_UDONSHARP && UNITY_EDITOR

        void OnValidate()
        {
            if (Application.isPlaying) return;
            ApplyModelAdjust();
        }
#endif

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            Log("join master=" + Networking.IsMaster + " n=" + VRCPlayerApi.GetPlayerCount());
            SendCustomEventDelayedSeconds(nameof(_RequestState), 3f);
            SendCustomEventDelayedSeconds(nameof(_LiveSweep), 5f);
            SendCustomEventDelayedSeconds(nameof(_RestoreFallback), 30f);
        }

        public void _RestoreFallback()
        {
            if (restored) return;
            Log("restored event missed");
            OnRestoredLocal();
        }

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal || restored) return;
            OnRestoredLocal();
        }

        void OnRestoredLocal()
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            restored = true;
            creator = Networking.IsMaster && !haveState;
            Log("restored master=" + Networking.IsMaster + " have=" + haveState + " creator=" + creator);
            if (!creator) return;
            haveState = true;
            byte[] data;
            if (!PlayerData.TryGetBytes(player, saveKey, out data) || data == null || data.Length < 2)
            {
                Log("restore none");
                return;
            }
            int before = sN;
            Merge(data, 2);
            Log("restore strokes=" + (sN - before) + " bytes=" + data.Length);
            if (sN > before) SendSnapshot(1);
        }

        public void _RequestState()
        {
            if (haveState) return;
            if (Networking.IsMaster) return;
            Log("req state");
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(_Req));
            SendCustomEventDelayedSeconds(nameof(_RequestState), 15f);
        }

        [NetworkCallable(5)]
        public void _Req()
        {
            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (caller == null || caller.isLocal) return;
            if (!Networking.IsMaster || snapScheduled) return;
            snapScheduled = true;
            SendCustomEventDelayedSeconds(nameof(_SendSnapForJoiners), 1.5f);
        }

        public void _SendSnapForJoiners()
        {
            snapScheduled = false;
            SendSnapshot(0);
        }

        void SendSnapshot(int kind)
        {
            byte[] all = Encode(false);
            int total = (all.Length + CHUNK - 1) / CHUNK;
            if (total < 1) total = 1;
            int snapId = Random.Range(1, int.MaxValue);
            for (int p = 0; p < total; p++)
            {
                int off = p * CHUNK;
                int len = Mathf.Min(CHUNK, all.Length - off);
                byte[] part = new byte[len];
                System.Array.Copy(all, off, part, 0, len);
                SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(_Snap), part, snapId, p, total, kind);
            }
            Log("snap send kind=" + kind + " bytes=" + all.Length + " parts=" + total);
        }

        [NetworkCallable(20)]
        public void _Snap(byte[] part, int snapId, int p, int total, int kind)
        {
            if (kind == 0 && haveState) return;
            if (part == null || total <= 0 || total > 64 || p < 0 || p >= total) return;
            if (snapId != rxSnapId || rxParts == null || rxParts.Length != total)
            {
                rxSnapId = snapId;
                rxParts = new byte[total][];
                rxGot = 0;
            }
            if (rxParts[p] != null) return;
            rxParts[p] = part;
            rxGot++;
            if (rxGot < total) return;
            int size = 0;
            for (int i = 0; i < total; i++) size += rxParts[i].Length;
            byte[] all = new byte[size];
            int off = 0;
            for (int i = 0; i < total; i++) { System.Array.Copy(rxParts[i], 0, all, off, rxParts[i].Length); off += rxParts[i].Length; }
            rxParts = null;
            int before = sN;
            Merge(all, 1);
            haveState = true;
            Log("snap recv kind=" + kind + " bytes=" + size + " new=" + (sN - before));
            MarkDirty();
        }

        public int NewId()
        {
            idCounter++;
            return (Networking.LocalPlayer.playerId << 20) | (idCounter & 0xFFFFF);
        }

        public static int NowUnix()
        {
            return (int)(System.DateTime.UtcNow - new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)).TotalSeconds;
        }

        public void PenStroke(int blockId, int time, Vector3 origin, Quaternion rot, int strokeId, int color, Vector2[] pts, int n, bool live)
        {
            if (n <= 0) return;
            byte[] rec = EncodeOne(blockId, Networking.LocalPlayer.displayName, time, origin, rot, strokeId, color, pts, n);
            if (live)
            {
                SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(_Live), rec);
            }
            else
            {
                Merge(rec, 1);
                SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(_Commit), rec);
                MarkDirty();
            }
        }

        [NetworkCallable(10)]
        public void _Live(byte[] rec)
        {
            if (rec == null) return;
            Merge(rec, 0);
        }

        [NetworkCallable(20)]
        public void _Commit(byte[] rec)
        {
            if (rec == null) return;
            Merge(rec, 1);
            MarkDirty();
        }

        public void EraseAt(Vector3 p, float r)
        {
            int[] hits = new int[16];
            int h = 0;
            for (int b = 0; b < bN && h < hits.Length; b++)
            {
                Vector3 local = Quaternion.Inverse(bRot[b]) * (p - bOrigin[b]);
                if (Mathf.Abs(local.z) > r) continue;
                float rr = r * r - local.z * local.z;
                h = CollectHits(b, new Vector2(local.x, local.y), rr, hits, h);
            }
            if (h > 0) EraseIds(hits, h);
        }

        public void EraseRay(Vector3 o, Vector3 d, float r)
        {
            int[] hits = new int[16];
            int h = 0;
            for (int b = 0; b < bN && h < hits.Length; b++)
            {
                Vector3 nrm = bRot[b] * Vector3.forward;
                float den = Vector3.Dot(d, nrm);
                if (Mathf.Abs(den) < 1e-4f) continue;
                float t = Vector3.Dot(bOrigin[b] - o, nrm) / den;
                if (t < 0f || t > 20f) continue;
                Vector3 local = Quaternion.Inverse(bRot[b]) * (o + d * t - bOrigin[b]);
                h = CollectHits(b, new Vector2(local.x, local.y), r * r, hits, h);
            }
            if (h > 0) EraseIds(hits, h);
        }

        int CollectHits(int b, Vector2 q, float rr, int[] hits, int h)
        {
            int id = bId[b];
            for (int s = 0; s < sN && h < hits.Length; s++)
            {
                if (sBlock[s] != id || sLive[s]) continue;
                Vector2[] pts = sPts[s];
                for (int i = 0; i < pts.Length; i++)
                {
                    Vector2 a = pts[i];
                    Vector2 c = i + 1 < pts.Length ? pts[i + 1] : a;
                    if (SegDist2(q, a, c) <= rr) { hits[h++] = sId[s]; break; }
                }
            }
            return h;
        }

        static float SegDist2(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float l = ab.sqrMagnitude;
            float t = l > 1e-10f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / l) : 0f;
            return (a + ab * t - p).sqrMagnitude;
        }

        void EraseIds(int[] ids, int n)
        {
            int[] send = new int[n];
            System.Array.Copy(ids, send, n);
            ApplyErase(send);
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(_Erase), send);
        }

        [NetworkCallable(10)]
        public void _Erase(int[] ids)
        {
            if (ids == null || ids.Length > 64) return;
            ApplyErase(ids);
        }

        void ApplyErase(int[] ids)
        {
            int removed = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                tomb[tombPos] = ids[i];
                tombPos = (tombPos + 1) % tomb.Length;
                int s = FindStroke(ids[i]);
                if (s >= 0) { RemoveStroke(s); removed++; }
            }
            if (removed > 0)
            {
                Log("erase n=" + removed);
                MarkDirty();
            }
        }

        public void ButtonReturnAll()
        {
            if (pens != null) for (int i = 0; i < pens.Length; i++) if (pens[i] != null) pens[i].ReturnHome();
            if (eraser != null) eraser.ReturnHome();
        }

        public void ButtonToggleVisible()
        {
            visible = !visible;
            if (linesRoot != null) linesRoot.gameObject.SetActive(visible);
        }

        public bool IsVisible() { return visible; }

        public void ButtonClearAll()
        {
            if (!Networking.IsMaster) { Log("clear refused (not master)"); return; }
            ClearLocal();
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(_ClearAll));
        }

        [NetworkCallable(2)]
        public void _ClearAll()
        {
            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (caller == null || !caller.isMaster) return;
            ClearLocal();
        }

        void ClearLocal()
        {
            while (sN > 0) RemoveStroke(sN - 1);
            Log("clear all");
            MarkDirty();
        }

        void MarkDirty()
        {
            if (!restored || saveScheduled) return;
            saveScheduled = true;
            float max = Mathf.Min(saveDelayMin + saveDelayPerPlayer * VRCPlayerApi.GetPlayerCount(), saveDelayCap);
            float d = Random.Range(saveDelayMin, Mathf.Max(saveDelayMin, max));
            float sinceLast = Time.time - lastSaveTime;
            if (lastSaveTime > 0f && sinceLast + d < saveMinInterval) d = saveMinInterval - sinceLast;
            SendCustomEventDelayedSeconds(nameof(_Save), d);
        }

        public void _Save()
        {
            saveScheduled = false;
            lastSaveTime = Time.time;
            byte[] data = Encode(false);
            PlayerData.SetBytes(saveKey, data);
            Log("save bytes=" + data.Length + " strokes=" + sN);
        }

        public void _LiveSweep()
        {
            float now = Time.time;
            for (int s = sN - 1; s >= 0; s--)
                if (sLive[s] && now - sLiveT[s] > 15f) RemoveStroke(s);
            SendCustomEventDelayedSeconds(nameof(_LiveSweep), 5f);
        }

        int FindStroke(int id)
        {
            for (int s = 0; s < sN; s++) if (sId[s] == id) return s;
            return -1;
        }

        int FindBlock(int id)
        {
            for (int b = 0; b < bN; b++) if (bId[b] == id) return b;
            return -1;
        }

        bool IsTomb(int id)
        {
            for (int i = 0; i < tomb.Length; i++) if (tomb[i] == id && id != 0) return true;
            return false;
        }

        int AddBlock(int id, string author, int time, Vector3 origin, Quaternion rot)
        {
            if (bN >= MAXB) EvictOldestBlock();
            int b = bN++;
            bId[b] = id; bAuthor[b] = author; bTime[b] = time; bOrigin[b] = origin; bRot[b] = rot;
            GameObject g = Instantiate(labelTemplate.gameObject, linesRoot);
            g.SetActive(true);
            TextMeshPro t = g.GetComponent<TextMeshPro>();
            t.text = author + " " + FormatTime(time);
            bLabel[b] = t;
            bLabelGo[b] = g;
            return b;
        }

        void RemoveBlockAt(int b)
        {
            if (bLabelGo[b] != null) Destroy(bLabelGo[b]);
            for (int i = b; i < bN - 1; i++)
            {
                bId[i] = bId[i + 1]; bAuthor[i] = bAuthor[i + 1]; bTime[i] = bTime[i + 1];
                bOrigin[i] = bOrigin[i + 1]; bRot[i] = bRot[i + 1]; bLabel[i] = bLabel[i + 1]; bLabelGo[i] = bLabelGo[i + 1];
            }
            bN--;
            bLabel[bN] = null;
            bLabelGo[bN] = null;
        }

        void EvictOldestBlock()
        {
            if (bN == 0) return;
            int id = bId[0];
            for (int s = sN - 1; s >= 0; s--) if (sBlock[s] == id) RemoveStrokeOnly(s);
            RemoveBlockAt(0);
            Log("evict block=" + id);
        }

        int AddStroke(int id, int blockId, int color, Vector2[] pts, bool live)
        {
            if (sN >= MAXS) EvictOldestBlock();
            int s = sN++;
            sId[s] = id; sBlock[s] = blockId; sColor[s] = (byte)color; sPts[s] = pts; sLive[s] = live; sLiveT[s] = Time.time;
            LineRenderer lr = Instantiate(lineTemplate.gameObject, linesRoot).GetComponent<LineRenderer>();
            lr.gameObject.SetActive(true);
            sLine[s] = lr;
            Color c = color < palette.Length ? palette[color] : Color.white;
            lr.startColor = c; lr.endColor = c;
            lr.widthMultiplier = LineRendererWidth();
            DrawStroke(s);
            UpdateMark(s);
            return s;
        }

        void DrawStroke(int s)
        {
            int b = FindBlock(sBlock[s]);
            if (b < 0) return;
            Vector2[] pts = sPts[s];
            int n = pts.Length;

            Transform t = sLine[s].transform;
            t.SetPositionAndRotation(bOrigin[b], bRot[b]);
            float sc = 1f / (LineScale * Mathf.Max(linesRoot.lossyScale.x, 1e-4f));
            t.localScale = new Vector3(sc, sc, sc);

            float z = -0.00005f * ((sId[s] * 7) & 15) * LineScale;
            Vector3[] w;
            if (n == 1)
            {
                w = new Vector3[2];
                w[0] = new Vector3(pts[0].x * LineScale, pts[0].y * LineScale, z);
                w[1] = w[0] + new Vector3(0.0005f * LineScale, 0f, 0f);
            }
            else if (n == 2)
            {
                w = new Vector3[2];
                for (int i = 0; i < 2; i++) w[i] = new Vector3(pts[i].x * LineScale, pts[i].y * LineScale, z);
            }
            else
            {

                w = new Vector3[2 * n - 2];
                int k = 0;
                w[k++] = new Vector3(pts[0].x * LineScale, pts[0].y * LineScale, z);
                for (int i = 0; i < n - 1; i++)
                {
                    Vector2 a = pts[i], c = pts[i + 1];
                    if (i > 0) { Vector2 q = (a * 0.75f + c * 0.25f) * LineScale; w[k++] = new Vector3(q.x, q.y, z); }
                    if (i < n - 2) { Vector2 r = (a * 0.25f + c * 0.75f) * LineScale; w[k++] = new Vector3(r.x, r.y, z); }
                }
                w[k++] = new Vector3(pts[n - 1].x * LineScale, pts[n - 1].y * LineScale, z);
            }
            sLine[s].positionCount = w.Length;
            sLine[s].SetPositions(w);
        }

        void UpdateMark(int s)
        {
            if (!sLive[s])
            {
                if (sMark[s] != null) { Destroy(sMark[s]); sMark[s] = null; }
                return;
            }
            if (tipMarkTemplate == null) return;
            int b = FindBlock(sBlock[s]);
            if (b < 0) return;
            if (sMark[s] == null)
            {
                GameObject g = Instantiate(tipMarkTemplate.gameObject, linesRoot);
                g.SetActive(true);
                if (markMpb == null) markMpb = new MaterialPropertyBlock();

                markMpb.SetVector("_MainTex_ST", new Vector4(1f, 1f, sColor[s] / 16f, 0f));
                g.GetComponent<Renderer>().SetPropertyBlock(markMpb);
                sMark[s] = g;
            }
            Vector2 p = sPts[s][sPts[s].Length - 1];
            Transform t = sMark[s].transform;
            t.SetPositionAndRotation(bOrigin[b] + bRot[b] * new Vector3(p.x, p.y, 0f), bRot[b]);
            float sc = 1f / Mathf.Max(linesRoot.lossyScale.x, 1e-4f);
            t.localScale = new Vector3(sc, sc, sc);
        }

        void RemoveStrokeOnly(int s)
        {
            if (sLine[s] != null) Destroy(sLine[s].gameObject);
            if (sMark[s] != null) Destroy(sMark[s]);
            for (int i = s; i < sN - 1; i++)
            {
                sId[i] = sId[i + 1]; sBlock[i] = sBlock[i + 1]; sColor[i] = sColor[i + 1]; sPts[i] = sPts[i + 1];
                sLine[i] = sLine[i + 1]; sLive[i] = sLive[i + 1]; sLiveT[i] = sLiveT[i + 1]; sMark[i] = sMark[i + 1];
            }
            sN--;
            sLine[sN] = null; sPts[sN] = null; sMark[sN] = null;
        }

        void RemoveStroke(int s)
        {
            int blockId = sBlock[s];
            RemoveStrokeOnly(s);
            for (int i = 0; i < sN; i++) if (sBlock[i] == blockId) { UpdateLabel(FindBlock(blockId)); return; }
            int b = FindBlock(blockId);
            if (b >= 0) RemoveBlockAt(b);
        }

        void UpdateLabel(int b)
        {
            if (b < 0 || bLabelGo[b] == null) return;
            int id = bId[b];
            float maxX = -1e9f, minY = 1e9f;
            bool any = false;
            for (int s = 0; s < sN; s++)
            {
                if (sBlock[s] != id || sLive[s]) continue;
                Vector2[] pts = sPts[s];
                for (int i = 0; i < pts.Length; i++)
                {
                    if (pts[i].x > maxX) maxX = pts[i].x;
                    if (pts[i].y < minY) minY = pts[i].y;
                }
                any = true;
            }
            bLabelGo[b].SetActive(any);
            if (!any) return;
            bLabelGo[b].transform.SetPositionAndRotation(
                bOrigin[b] + bRot[b] * new Vector3(maxX, minY - labelGap, 0f), bRot[b]);
        }

        string FormatTime(int unix)
        {
            System.DateTime t = new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc).AddSeconds(unix).ToLocalTime();
            System.DateTime now = System.DateTime.Now;
            if (t.Year == now.Year && t.Month == now.Month && t.Day == now.Day) return t.ToString("H:mm");
            return t.ToString("M/d H:mm");
        }

        int TotalBytes()
        {
            int n = 2;

            for (int b = 0; b < bN; b++) n += 31 + 2 * (bAuthor[b] == null ? 0 : Mathf.Min(bAuthor[b].Length, 64));
            for (int s = 0; s < sN; s++) if (!sLive[s]) n += 9 + 2 * sPts[s].Length;
            return n;
        }

        byte[] wb;
        int wp;

        void W8(int v) { wb[wp++] = (byte)(v & 0xFF); }
        void W16(int v) { wb[wp++] = (byte)(v & 0xFF); wb[wp++] = (byte)((v >> 8) & 0xFF); }
        void W32(int v) { W16(v & 0xFFFF); W16((v >> 16) & 0xFFFF); }

        void WBlockHead(int id, string author, int time, Vector3 o, Quaternion r, int nStrokes)
        {
            W32(id); W32(time);
            W32(Mathf.RoundToInt(o.x * 10000f)); W32(Mathf.RoundToInt(o.y * 10000f)); W32(Mathf.RoundToInt(o.z * 10000f));
            W16(Mathf.RoundToInt(r.x * 32767f)); W16(Mathf.RoundToInt(r.y * 32767f));
            W16(Mathf.RoundToInt(r.z * 32767f)); W16(Mathf.RoundToInt(r.w * 32767f));
            if (author == null) author = "";
            int len = Mathf.Min(author.Length, 64);
            W8(len);
            for (int i = 0; i < len; i++) W16(author[i]);
            W16(nStrokes);
        }

        void WStroke(int id, int color, Vector2[] pts, int n)
        {
            W32(id); W8(color); W16(n);
            int px = 0, py = 0;
            for (int i = 0; i < n; i++)
            {
                int x = Mathf.Clamp(Mathf.RoundToInt(pts[i].x * Q2), -32000, 32000);
                int y = Mathf.Clamp(Mathf.RoundToInt(pts[i].y * Q2), -32000, 32000);
                if (i == 0) { W16(x); W16(y); }
                else
                {
                    int dx = Mathf.Clamp(x - px, -127, 127);
                    int dy = Mathf.Clamp(y - py, -127, 127);
                    W8(dx); W8(dy);
                    x = px + dx; y = py + dy;
                }
                px = x; py = y;
            }
        }

        byte[] EncodeOne(int blockId, string author, int time, Vector3 o, Quaternion r, int strokeId, int color, Vector2[] pts, int n)
        {
            wb = new byte[3 + 31 + 128 + 9 + 2 * n + 4];
            wp = 0;
            W8(VER); W16(1);
            WBlockHead(blockId, author, time, o, r, 1);
            WStroke(strokeId, color, pts, n);
            return Trim();
        }

        byte[] Encode(bool includeLive)
        {
            while (TotalBytes() > maxBytes && bN > 1) EvictOldestBlock();
            wb = new byte[TotalBytes() + 16];
            wp = 0;
            W8(VER);
            W16(bN);
            for (int b = 0; b < bN; b++)
            {
                int id = bId[b];
                int cnt = 0;
                for (int s = 0; s < sN; s++) if (sBlock[s] == id && (includeLive || !sLive[s])) cnt++;
                WBlockHead(id, bAuthor[b], bTime[b], bOrigin[b], bRot[b], cnt);
                for (int s = 0; s < sN; s++)
                    if (sBlock[s] == id && (includeLive || !sLive[s])) WStroke(sId[s], sColor[s], sPts[s], sPts[s].Length);
            }
            return Trim();
        }

        byte[] Trim()
        {
            byte[] o = new byte[wp];
            System.Array.Copy(wb, o, wp);
            wb = null;
            return o;
        }

        byte[] rb;
        int rp;

        int R8() { return rb[rp++]; }
        int R16u() { int v = rb[rp] | (rb[rp + 1] << 8); rp += 2; return v; }
        int R16() { int v = R16u(); return v >= 32768 ? v - 65536 : v; }
        int R32() { int lo = R16u(); int hi = R16u(); return lo | (hi << 16); }
        int RS8() { int v = R8(); return v >= 128 ? v - 256 : v; }

        void Merge(byte[] data, int mode)
        {
            rb = data;
            rp = 0;
            if (data.Length < 3) { rb = null; return; }
            int ver = R8();
            if (ver != VER && ver != 1) { rb = null; return; }
            float qs = ver == 1 ? Q1 : Q2;
            int nb = R16u();
            for (int k = 0; k < nb; k++)
            {
                if (rp + 29 > data.Length) break;
                int bid = R32();
                int time = R32();
                Vector3 o = new Vector3(R32() / 10000f, R32() / 10000f, R32() / 10000f);
                Quaternion r = new Quaternion(R16() / 32767f, R16() / 32767f, R16() / 32767f, R16() / 32767f);
                r = Normalize(r);
                int len = R8();
                if (rp + len * 2 + 2 > data.Length) break;
                char[] cs = new char[len];
                for (int i = 0; i < len; i++) cs[i] = (char)R16u();
                string author = new string(cs);
                int ns = R16u();
                if (mode == 2) bid = NewId();
                int b = FindBlock(bid);
                bool touched = false;
                for (int j = 0; j < ns; j++)
                {
                    if (rp + 7 > data.Length) { k = nb; break; }
                    int sid = R32();
                    int color = R8();
                    int np = R16u();
                    if (np <= 0 || rp + 4 + 2 * (np - 1) > data.Length) { k = nb; break; }
                    Vector2[] pts = new Vector2[np];
                    int x = R16(), y = R16();
                    pts[0] = new Vector2(x / qs, y / qs);
                    for (int i = 1; i < np; i++)
                    {
                        x += RS8(); y += RS8();
                        pts[i] = new Vector2(x / qs, y / qs);
                    }
                    if (mode == 2) sid = NewId();
                    if (IsTomb(sid)) continue;
                    if (b < 0) b = AddBlock(bid, author, time, o, r);
                    int s = FindStroke(sid);
                    bool live = mode == 0;
                    if (s < 0) AddStroke(sid, bid, color, pts, live);
                    else if (sLive[s])
                    {
                        sPts[s] = pts; sLive[s] = live; sLiveT[s] = Time.time;
                        DrawStroke(s);
                        UpdateMark(s);
                    }
                    touched = true;
                }
                if (touched) UpdateLabel(FindBlock(bid));
            }
            rb = null;
        }

        static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m < 1e-6f) return Quaternion.identity;
            return new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }

        void Log(string s)
        {
            if (log != null) log.Log("board " + s);
        }

        public int TestOnly_StrokeCount() { return sN; }
        public int TestOnly_BlockCount() { return bN; }
        public byte[] TestOnly_Encode() { return Encode(false); }
        public void TestOnly_Merge(byte[] d, int mode) { Merge(d, mode); }
        public void TestOnly_Clear() { while (sN > 0) RemoveStroke(sN - 1); }
        public string TestOnly_Label(int b) { return b < bN && bLabel[b] != null ? bLabel[b].text : null; }
        public bool TestOnly_HaveState() { return haveState; }
        public int TestOnly_MarkCount() { int c = 0; for (int s = 0; s < sN; s++) if (sMark[s] != null) c++; return c; }
        public Vector3 TestOnly_StrokePoint(int s)
        {
            if (s < 0 || s >= sN) return Vector3.zero;
            int b = FindBlock(sBlock[s]);
            if (b < 0) return Vector3.zero;
            return bOrigin[b] + bRot[b] * new Vector3(sPts[s][0].x, sPts[s][0].y, 0f);
        }
    }
}

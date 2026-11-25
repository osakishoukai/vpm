// 曲面を平らな板で近似したガラス(丸いガラスの壁・筒の窓)は、面ごとに法線が違い、映り込みが板の境で折れてカクカクに見える。
// ビルド・Play のシーンの写しで、Osaki/VPM/Glass を使う面の法線を、直角より緩い折れ(89 度未満)の隣の面となめらかにした
// メッシュの写しに差し替える(家の決まりのオートスムーズと同じ角度。箱のショーケースの角・平らな窓は変わらない)。
// 元のメッシュ・シーン・プレハブは触らない。ガラス以外の submesh の頂点は変えない。
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using com.osakishokai.vpm.build;

namespace com.osakishokai.vpm.glass.editor
{
    public sealed class OsakiGlassSmoothStep : ICommonSceneStep
    {
        const string ShaderName = "Osaki/VPM/Glass";
        const float SmoothAngle = 89f;

        public int Phase => 50;

        public void PrepareBuild() { }
        public void PreparePlay() { }
        public void BeginPlay() { }
        public void EndPlay() { }

        public void ProcessScene(Scene scene, CommonSceneMode mode)
        {
            if (mode == CommonSceneMode.Manual) return;
            var cache = new Dictionary<string, Mesh>();
            int n = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    var mesh = mf ? mf.sharedMesh : null;
                    if (!mesh || mr.isPartOfStaticBatch) continue;   // Play では静的にまとめた後に走る
                    var mats = mr.sharedMaterials;
                    var glassSubs = new List<int>();
                    for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
                        if (mats[s] && mats[s].shader && mats[s].shader.name == ShaderName && mesh.GetTopology(s) == MeshTopology.Triangles) glassSubs.Add(s);
                    if (glassSubs.Count == 0) continue;
                    string key = mesh.GetInstanceID() + ":" + string.Join(",", glassSubs);
                    if (!cache.TryGetValue(key, out var smoothed)) cache[key] = smoothed = Smooth(mesh, glassSubs);
                    if (smoothed == null) continue;
                    mf.sharedMesh = smoothed;
                    n++;
                }
            if (n > 0) Debug.Log($"[OsakiGlass] 板で近似した曲面のガラスの法線をなめらかにした: {n} 個(メッシュの写し。元は触らない)");
        }

        // 変える所が無ければ null
        static Mesh Smooth(Mesh src, List<int> subs)
        {
            var vs = src.vertices;
            var ns = src.normals;
            if (ns == null || ns.Length != vs.Length) return null;
            var ts = src.tangents;
            bool hasT = ts != null && ts.Length == vs.Length;
            float cosMax = Mathf.Cos(SmoothAngle * Mathf.Deg2Rad);

            // ガラスの三角形と、その面の法線(面積つき)
            var tris = new List<int[]>();
            var fn = new List<Vector3>();
            foreach (var s in subs)
            {
                var ix = src.GetTriangles(s);
                tris.Add(ix);
                for (int i = 0; i + 2 < ix.Length; i += 3)
                    fn.Add(Vector3.Cross(vs[ix[i + 1]] - vs[ix[i]], vs[ix[i + 2]] - vs[ix[i]]));
            }
            // 同じ位置の角を集める(0.1mm で丸める)
            var corners = new Dictionary<Vector3Int, List<(int f, int v)>>();   // 位置 → (面の番号, 頂点)
            Vector3Int K(Vector3 p) => new Vector3Int(Mathf.RoundToInt(p.x * 10000f), Mathf.RoundToInt(p.y * 10000f), Mathf.RoundToInt(p.z * 10000f));
            {
                int f = 0;
                foreach (var ix in tris)
                    for (int i = 0; i + 2 < ix.Length; i += 3, f++)
                        for (int c = 0; c < 3; c++)
                        {
                            var k = K(vs[ix[i + c]]);
                            if (!corners.TryGetValue(k, out var l)) corners[k] = l = new List<(int f, int v)>();
                            l.Add((f, ix[i + c]));
                        }
            }

            var outV = new List<Vector3>(vs);
            var outN = new List<Vector3>(ns);
            var outT = hasT ? new List<Vector4>(ts) : null;
            var copyFrom = new List<int>();   // 足した頂点の元
            var assigned = new Dictionary<int, Vector3>();   // 元の頂点 → 付けた法線
            var dup = new Dictionary<(int, Vector3Int), int>();
            var newTris = new List<int[]>();
            bool changed = false;
            {
                int f = 0;
                foreach (var ix in tris)
                {
                    var nix = (int[])ix.Clone();
                    for (int i = 0; i + 2 < ix.Length; i += 3, f++)
                    {
                        var self = fn[f].normalized;
                        if (self == Vector3.zero) continue;
                        for (int c = 0; c < 3; c++)
                        {
                            int vi = ix[i + c];
                            var sum = Vector3.zero;
                            bool split = false;   // なめらかにつなぐ面どうしで、元の法線が割れているか(既になめらかな物は変えない)
                            foreach (var (g, gv) in corners[K(vs[vi])])
                            {
                                var gn = fn[g];
                                if (gn == Vector3.zero) continue;
                                if (Vector3.Dot(gn.normalized, self) > cosMax) { sum += gn; if (Vector3.Angle(ns[gv], ns[vi]) > 1f) split = true; }
                            }
                            if (!split) continue;
                            var nn = sum.normalized;
                            if (nn == Vector3.zero) continue;
                            // 元の法線の表裏はそのまま(面の向きと逆に付けてあった物は逆のまま)
                            if (Vector3.Dot(ns[vi], self) < 0f) nn = -nn;
                            if (Vector3.Angle(nn, ns[vi]) > 0.5f) changed = true;
                            if (!assigned.TryGetValue(vi, out var have)) { assigned[vi] = nn; outN[vi] = nn; if (hasT) outT[vi] = Ortho(ts[vi], nn); }
                            else if (Vector3.Angle(have, nn) > 0.5f)
                            {
                                var dk = (vi, K(nn));
                                if (!dup.TryGetValue(dk, out int ni))
                                {
                                    ni = outV.Count; dup[dk] = ni;
                                    outV.Add(vs[vi]); outN.Add(nn); if (hasT) outT.Add(Ortho(ts[vi], nn)); copyFrom.Add(vi);
                                }
                                nix[i + c] = ni;
                            }
                        }
                    }
                    newTris.Add(nix);
                }
            }
            if (!changed) return null;
            if (copyFrom.Count > 0 && src.blendShapeCount > 0) return null;   // 頂点を足すとブレンドシェイプが合わなくなる

            var m = Object.Instantiate(src);
            m.name = src.name + "_GlassSmooth";
            if (copyFrom.Count > 0)
            {
                if (outV.Count > 65535) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                CopyChannels(src, m, copyFrom, outV);
            }
            m.SetNormals(outN);
            if (hasT) m.SetTangents(outT);
            for (int j = 0; j < subs.Count; j++) m.SetTriangles(newTris[j], subs[j], false);
            return m;
        }

        static Vector4 Ortho(Vector4 t, Vector3 n)
        {
            var v = new Vector3(t.x, t.y, t.z);
            v = (v - n * Vector3.Dot(n, v)).normalized;
            if (v == Vector3.zero) v = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            return new Vector4(v.x, v.y, v.z, t.w == 0f ? 1f : t.w);
        }

        // 頂点を足した時、他の欄(UV・色・骨)を元の頂点から写して長さをそろえる
        static void CopyChannels(Mesh src, Mesh m, List<int> from, List<Vector3> outV)
        {
            m.SetVertices(outV);
            for (int ch = 0; ch < 8; ch++)
            {
                var uv = new List<Vector4>();
                src.GetUVs(ch, uv);
                if (uv.Count != src.vertexCount) continue;
                foreach (var f in from) uv.Add(uv[f]);
                m.SetUVs(ch, uv);
            }
            var col = src.colors;
            if (col != null && col.Length == src.vertexCount)
            {
                var l = new List<Color>(col);
                foreach (var f in from) l.Add(col[f]);
                m.SetColors(l);
            }
            var bw = src.boneWeights;
            if (bw != null && bw.Length == src.vertexCount)
            {
                var l = new List<BoneWeight>(bw);
                foreach (var f in from) l.Add(bw[f]);
                m.boneWeights = l.ToArray();
            }
        }
    }
}
#endif

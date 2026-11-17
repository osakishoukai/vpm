// ビルドの場面の写しで、どのレンダラーも使っていないライトマップを落とす(task 505)。
// 焼いた場面に OsakiLightmapData の prefab を置くと、ビルド直前の書き戻し(OsakiLightmapPrefabStep)で prefab のライトマップが後ろに足され、
// レンダラーはそちらを指す。場面自身の古いライトマップ(LightingDataAsset)は誰も使わないのに積まれ、容量が二重になっていた
// (NostalgicSchool v58: Android 20→27.5MB・PC 34.4→55.8MB)。使っていない分だけを詰めて落とし、番号を付け直す。
// 光・反射のプローブには触らない。OsakiLightmapData が無い場面・編集中の場面・Play は触らない。
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using com.osakishokai.vpm.build;

namespace com.osakishokai.vpm.lightmapprefab.editor
{
    public sealed class OsakiUnusedLightmapDropStep : ICommonSceneStep
    {
        // 書き戻し・焼き専用ライト切り(50)の後
        public int Phase => 60;

        public void PrepareBuild() { }
        public void PreparePlay() { }
        public void BeginPlay() { }
        public void EndPlay() { }

        public void ProcessScene(Scene scene, CommonSceneMode mode)
        {
            if (mode != CommonSceneMode.Build) return;
            var roots = scene.GetRootGameObjects();
            bool has = false;
            foreach (var r in roots) if (r.GetComponentInChildren<OsakiLightmapData>(true) != null) { has = true; break; }
            if (!has) return;

            var cur = LightmapSettings.lightmaps;
            if (cur == null || cur.Length == 0) return;
            var renderers = new List<Renderer>();
            var terrains = new List<Terrain>();
            foreach (var r in roots) { renderers.AddRange(r.GetComponentsInChildren<Renderer>(true)); terrains.AddRange(r.GetComponentsInChildren<Terrain>(true)); }
            var used = new bool[cur.Length];
            foreach (var r in renderers) if (r.lightmapIndex >= 0 && r.lightmapIndex < cur.Length) used[r.lightmapIndex] = true;
            foreach (var t in terrains) if (t.lightmapIndex >= 0 && t.lightmapIndex < cur.Length) used[t.lightmapIndex] = true;

            var map = new int[cur.Length];
            var kept = new List<LightmapData>();
            for (int i = 0; i < cur.Length; i++) { map[i] = used[i] ? kept.Count : -1; if (used[i]) kept.Add(cur[i]); }
            int dropped = cur.Length - kept.Count;
            if (dropped == 0) return;

            LightmapSettings.lightmaps = kept.ToArray();
            foreach (var r in renderers) if (r.lightmapIndex >= 0 && r.lightmapIndex < cur.Length) r.lightmapIndex = map[r.lightmapIndex];
            foreach (var t in terrains) if (t.lightmapIndex >= 0 && t.lightmapIndex < cur.Length) t.lightmapIndex = map[t.lightmapIndex];
            Debug.Log($"[OsakiLightmapPrefab] 使っていないライトマップを {dropped} 枚落とした({cur.Length}→{kept.Count}。場面の古い焼きと prefab の焼きの二重)");
        }
    }
}
#endif

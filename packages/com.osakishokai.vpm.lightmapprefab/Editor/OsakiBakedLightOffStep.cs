// ライトマップ入り prefab(OsakiLightmapData)の配下にある「焼き専用(Baked)なのに焼き済みの印が無い」ライトを、
// ビルド・Play のシーン処理で切る(UdonTemplate #155)。
// prefab にすると Light.bakingOutput(焼き済みの印)が残らないので、置いた場面・VRChat では Baked のライトが
// リアルタイムで点き、焼いた光に重ねて照らし影も落とす(AquaSphere: 光の玉・ランタンの影が壁に出た)。
// 焼いた本人の場面(isBaked = true)のライトは触らない。編集中のシーンは触らない(ビルド・Play の写しだけ)。
// OsakiLightmapPrefab は凍結中なので、直しは新しいファイル・新しい型に置く(#159 の型)。呼び手の変更は要らない(Step は自動で見つかる)
#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.SceneManagement;
using com.osakishokai.vpm.build;

namespace com.osakishokai.vpm.lightmapprefab.editor
{
    public sealed class OsakiBakedLightOffStep : ICommonSceneStep
    {
        public int Phase => 50;

        public void PrepareBuild() { }
        public void PreparePlay() { }
        public void BeginPlay() { }
        public void EndPlay() { }

        public void ProcessScene(Scene scene, CommonSceneMode mode)
        {
            if (mode == CommonSceneMode.Manual) return;
            int n = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var d in root.GetComponentsInChildren<OsakiLightmapData>(true))
                    foreach (var l in d.GetComponentsInChildren<Light>(true))
                        if (l.enabled && l.lightmapBakeType == LightmapBakeType.Baked && !l.bakingOutput.isBaked) { l.enabled = false; n++; }
            if (n > 0) Debug.Log($"[OsakiLightmapPrefab] 焼き専用のライトを {n} 個切った(prefab では焼き済みの印が消え、リアルタイムで二重に照らすため)");
        }
    }
}
#endif

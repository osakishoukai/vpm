// ベイク済みのライトプローブを prefab に持たせる部品(OsakiLightmapData と同じ考え方。OsakiLightmapData は凍結済みなので別の部品にした)。
// ライトプローブはシーンが持つ情報(LightingDataAsset の中)なので、prefab にすると消える。木・ポスター・看板など
// 「光をプローブで受ける」物は、買い手の空のシーンでほぼ光を受けず真っ黒に写る。
// この部品が焼いたプローブ(LightingDataAsset の中の LightProbes)を指しておき、シーンに置かれた時(OnEnable)と
// ビルド直前(OsakiLightProbePrefabStep)にシーンへ書き戻す。
// シーンに別のプローブがある時は書き戻さない(買い手が自分で焼いたプローブを消さない)。警告だけ出す。
// 付け方: OsakiLightProbePrefab.Attach(prefabPath, lightingDataAssetPath)
using UnityEngine;

namespace com.osakishokai.vpm.lightmapprefab
{
    [ExecuteAlways]
    public class OsakiLightProbeData : MonoBehaviour
    {
        public LightProbes lightProbes;

        void OnEnable() { if (!OsakiLightmapData.SuppressApply) Apply(); }

        public void Apply()
        {
            if (lightProbes == null) return;
            var cur = LightmapSettings.lightProbes;
            if (cur == lightProbes) return;
            if (cur != null && cur.count > 0)
            {
                Debug.LogWarning("[OsakiLightProbeData] シーンに別のライトプローブがあるので、" + name + " のプローブは書き戻さない(プローブで光を受ける物の明るさが変わる)");
                return;
            }
            LightmapSettings.lightProbes = lightProbes;
            LightProbes.Tetrahedralize();
        }
    }
}

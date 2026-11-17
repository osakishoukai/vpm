// ベイク済みライトマップを prefab に持たせる部品。
// ライトマップの割り当て(レンダラーごとの番号と UV の位置)はシーンが持つ情報なので、prefab にすると消える。
// この部品が割り当てを覚えておき、シーンに置かれた時(OnEnable)とビルド直前(OsakiLightmapPrefabStep)にシーンへ書き戻す。
// VRChat のクライアントではこの部品は動かないが、ビルド時に書き戻された状態がそのままワールドに入る。
// 作り方: OsakiLightmapPrefab.Build(Glim 等でベイクした後に、商品のメニューから呼ぶ)
using UnityEngine;

namespace com.osakishokai.vpm.lightmapprefab
{
    [ExecuteAlways]
    public class OsakiLightmapData : MonoBehaviour
    {
        public Texture2D[] lightmaps;
        public Renderer[] renderers;
        public int[] mapIndex;
        public Vector4[] scaleOffset;
        public LightmapsMode lightmapsMode = LightmapsMode.NonDirectional;
        public Material skybox;
        public bool applySkybox = true;

        // 試験用: ビルド済みワールドを読み直して確かめる時、この部品が書き戻してしまわないよう止める
        public static bool SuppressApply;

        void OnEnable() { if (!SuppressApply) Apply(); }

        public void Apply()
        {
            if (applySkybox && skybox != null && RenderSettings.skybox != skybox) RenderSettings.skybox = skybox;
            if (lightmaps == null || lightmaps.Length == 0 || renderers == null) return;
            if (LightmapSettings.lightmapsMode != lightmapsMode) LightmapSettings.lightmapsMode = lightmapsMode;
            // 既に同じ並びが入っていればそこを使い、無ければ後ろに足す(他のベイク済みライトマップと共存する)
            var current = LightmapSettings.lightmaps;
            int first = -1;
            for (int i = 0; i + lightmaps.Length <= current.Length && first < 0; i++)
            {
                bool same = true;
                for (int k = 0; k < lightmaps.Length && same; k++) same = current[i + k].lightmapColor == lightmaps[k];
                if (same) first = i;
            }
            if (first < 0)
            {
                first = current.Length;
                var merged = new LightmapData[current.Length + lightmaps.Length];
                System.Array.Copy(current, merged, current.Length);
                for (int k = 0; k < lightmaps.Length; k++) merged[first + k] = new LightmapData { lightmapColor = lightmaps[k] };
                LightmapSettings.lightmaps = merged;
            }
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                renderers[i].lightmapIndex = first + mapIndex[i];
                renderers[i].lightmapScaleOffset = scaleOffset[i];
            }
        }
    }
}

// ビルド直前に、開いているシーンの OsakiLightmapData をすべて書き戻す(他のベイク等で割り当てが消えていても戻す)。
// 書き戻しはビルド要求の時点(PrepareBuild)で行う。この時点で編集中のシーンに入れた状態が、SDK の保存とビルドを経てワールドに入る
// (ビルドした .vrcw を読み直すと、prefab のレンダラーにライトマップが当たっている)。
#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.SceneManagement;
using com.osakishokai.vpm.build;

namespace com.osakishokai.vpm.lightmapprefab.editor
{
    public sealed class OsakiLightmapPrefabStep : ICommonSceneStep
    {
        // ShadowPickup(100) より前。ProcessScene では何もしないので順序には影響しない
        public int Phase => 50;

        public void PrepareBuild()
        {
            foreach (var d in Object.FindObjectsOfType<OsakiLightmapData>()) d.Apply();
        }

        public void PreparePlay() { }
        public void BeginPlay() { }
        public void ProcessScene(Scene scene, CommonSceneMode mode) { }
        public void EndPlay() { }
    }
}
#endif

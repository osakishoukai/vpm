// ShadowPickupのシーン処理ステップ(Common基盤 ShadowPickup 世代1)。
// このフォルダは初回公開時に丸ごと凍結する。修正・機能追加が必要になったら、
// このフォルダは変更せず、新しい世代フォルダ(ShadowPickup2 等)を作ってそこにStepを置く。
// ランチャー(OsakiSceneProcessor)が ICommonSceneStep を自動検出して呼ぶ。
//
// Play時は2相: PreparePlay(ExitingEditMode)では編集シーンを絶対に変更せず、Prefab同一性を
// SessionStateへ保存するだけ。BeginPlay/ProcessScene(EnteredPlayMode後)で、破棄される
// Play用シーンコピーに対してベイクする。
#if UNITY_EDITOR
using UnityEngine.SceneManagement;
using com.osakishokai.vpm.build;

namespace com.osakishokai.vpm.shadowpickup.editor
{
    public sealed class ShadowPickupSceneStep : ICommonSceneStep
    {
        public int Phase => CommonPhase.ShadowPickup;

        // 実際のVRChatビルドでは、Prefab編集時メタデータが生きているこの時点で
        // マーカーのPrefab同一性を確定して覚えておく。
        public void PrepareBuild()
        {
            // Build開始ごとに完全初期化する(前回Buildの情報を参照しない)。
            ShadowPickupBaker.ClearBuildPrefabIdentities();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                ShadowPickupBaker.PrecachePrefabIdentities(SceneManager.GetSceneAt(i));
            }
        }

        public void PreparePlay()
        {
            ShadowPickupBaker.PrecachePlayPrefabIdentities();
        }

        public void BeginPlay()
        {
            ShadowPickupBaker.RestorePlayPrefabIdentitiesFromSession();
        }

        public void ProcessScene(Scene scene, CommonSceneMode mode)
        {
            ShadowPickupBaker.BakeScene(scene, ToBakeMode(mode));
        }

        public void EndPlay()
        {
            ShadowPickupBaker.ClearPlayPrefabIdentities();
        }

        private static ShadowPickupBakeMode ToBakeMode(CommonSceneMode mode)
        {
            switch (mode)
            {
                case CommonSceneMode.Play: return ShadowPickupBakeMode.Play;
                case CommonSceneMode.Manual: return ShadowPickupBakeMode.Manual;
                default: return ShadowPickupBakeMode.Build;
            }
        }
    }
}
#endif

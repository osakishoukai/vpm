// Common の OsakiGlass_Standard の MatCap(OsakiGlassRim.png)には左上に四角い窓の照りが描かれていて、
// 見上げる角度で窓ガラスに白い四角が映る。Common のその材質・画像は出荷済みで書き換えられないので、
// ビルド・Play のシーンの写しで、四角を消した OsakiGlass_Standard2 に差し替える(編集中のシーンは触らない)。
// 材質は GUID で探し、どちらかがプロジェクトに無ければ何もしない。
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using com.osakishokai.vpm.build;

namespace com.osakishokai.vpm.glass.editor
{
    public sealed class OsakiGlassRimFixStep : ICommonSceneStep
    {
        const string OldGuid = "e46022d427fb3f6bebbe83bd0aa868ec"; // Common/Glass/Material/OsakiGlass_Standard.mat
        const string NewGuid = "c82686914b3d49bf89d1235651cddffb"; // Common/Glass/Material/OsakiGlass_Standard2.mat

        public int Phase => 50;

        public void PrepareBuild() { }
        public void PreparePlay() { }
        public void BeginPlay() { }
        public void EndPlay() { }

        public void ProcessScene(Scene scene, CommonSceneMode mode)
        {
            if (mode == CommonSceneMode.Manual) return;
            var oldMat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(OldGuid));
            var newMat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(NewGuid));
            if (oldMat == null || newMat == null) return;
            int n = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    bool hit = false;
                    for (int i = 0; i < mats.Length; i++)
                        if (mats[i] == oldMat) { mats[i] = newMat; hit = true; }
                    if (hit) { r.sharedMaterials = mats; n++; }
                }
            if (n > 0) Debug.Log($"[OsakiGlass] OsakiGlass_Standard を Standard2 に差し替えた: {n} 個(MatCap の四角い照りを消した版)");
        }
    }
}
#endif

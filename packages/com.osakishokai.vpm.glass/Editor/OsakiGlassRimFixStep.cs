// Common の OsakiGlass_Standard の MatCap(OsakiGlassRim.png)には左上に四角い窓の照りが描かれていて、
// 見上げる角度で窓ガラスに白い四角が映る。Common のその材質・画像は出荷済みで書き換えられないので、
// ビルド・Play のシーンの写しで、四角を消した OsakiGlass_Standard2 に差し替える(編集中のシーンは触らない)。
// 商品側で写した材質(MatCap に旧 OsakiGlassRim を持つ物)も、旧画像を OsakiGlassRim2 にした材質の写しに替える
// (1.2.0 は Standard だけを替えていて、写した材質の窓に四角が残った。kaizen #1743 NekozukiNoIe #104)。
// 材質・画像は GUID で探し、プロジェクトに無ければ何もしない。元の材質は触らない。
#if UNITY_EDITOR
using System.Collections.Generic;
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
        const string OldRimGuid = "d1cf5fe8a410181428b86d76411788fb"; // Common/Glass/OsakiGlassRim.png(四角あり)
        const string NewRimGuid = "38976c3074c0485ab3927c203baa31a6"; // Common/Glass/OsakiGlassRim2.png
        const string MatCap = "_MatCap";

        public int Phase => 50;

        public void PrepareBuild() { }
        public void PreparePlay() { }
        public void BeginPlay() { }
        public void EndPlay() { }

        static T Load<T>(string guid) where T : Object => AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));

        public void ProcessScene(Scene scene, CommonSceneMode mode)
        {
            if (mode == CommonSceneMode.Manual) return;
            var oldMat = Load<Material>(OldGuid);
            var newMat = Load<Material>(NewGuid);
            var oldRim = Load<Texture>(OldRimGuid);
            var newRim = Load<Texture>(NewRimGuid);
            bool swapMat = oldMat != null && newMat != null;
            bool swapRim = oldRim != null && newRim != null;
            if (!swapMat && !swapRim) return;
            var copies = new Dictionary<Material, Material>();
            int n = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    bool hit = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (!m) continue;
                        if (swapMat && m == oldMat) { mats[i] = newMat; hit = true; continue; }
                        if (!swapRim || !m.HasProperty(MatCap) || m.GetTexture(MatCap) != oldRim) continue;
                        if (!copies.TryGetValue(m, out var c))
                        {
                            c = new Material(m) { name = m.name };
                            c.SetTexture(MatCap, newRim);
                            copies[m] = c;
                        }
                        mats[i] = c; hit = true;
                    }
                    if (hit) { r.sharedMaterials = mats; n++; }
                }
            if (n > 0) Debug.Log($"[OsakiGlass] MatCap の四角い照りの無い版に差し替えた: {n} 個(Standard→Standard2、写した材質 {copies.Count} 種は OsakiGlassRim2 の写し)");
        }
    }
}
#endif

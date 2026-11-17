// OsakiWorldSettings のエディタ側。置かれた時・ビルド直前に、ブルームを実機とシーンビューの両方で効かせる準備をする。
//   ①プロファイルが空なら共通の OsakiBloom を入れる
//   ②参照カメラを決める(VRC_SceneDescriptor.ReferenceCamera → 無ければ MainCamera → 無ければ ReferenceCamera を作る)
//   ③そのカメラに PostProcessLayer を付け、音量のレイヤーを見させる(実機は参照カメラの PostProcessLayer を写す)
//   ④そのカメラを有効な MainCamera にする(シーンビューは有効な MainCamera の画像効果を写す。無効だとシーンビューだけ素のまま)
//   ⑤VRC_SceneDescriptor.ReferenceCamera が空ならそのカメラを入れる
// 既にある設定は壊さない(レイヤーは足すだけ、他の項目は触らない)。
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;
using com.osakishokai.vpm.build;

namespace com.osakishokai.vpm.worldsettings.editor
{
    [InitializeOnLoad]
    public static class OsakiWorldSettingsEditor
    {
        const string DefaultProfileGuid = "6b0e3f5a1c2d4e8f9a7b3c1d5e2f4a60"; // Common/WorldSettings/OsakiBloom.asset

        static OsakiWorldSettingsEditor() { OsakiWorldSettings.EditorApply = Prepare; }

        static void Prepare(OsakiWorldSettings s)
        {
            // prefab アセットそのものや、prefab の単体編集(プレビューのシーン)ではカメラを探さない
            if (EditorUtility.IsPersistent(s) || !s.gameObject.scene.IsValid() || UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(s.gameObject.scene)) return;
            var v = s.GetComponent<PostProcessVolume>();
            if (s.profile == null)
            {
                s.profile = AssetDatabase.LoadAssetAtPath<PostProcessProfile>(AssetDatabase.GUIDToAssetPath(DefaultProfileGuid));
                if (s.profile != null) { v.sharedProfile = s.profile; EditorUtility.SetDirty(s); EditorUtility.SetDirty(v); }
            }

            var desc = Object.FindObjectOfType<VRC.SDKBase.VRC_SceneDescriptor>();
            Camera cam = null;
            if (desc != null && desc.ReferenceCamera != null) cam = desc.ReferenceCamera.GetComponent<Camera>();
            if (cam == null) cam = FindMainCamera(s.gameObject.scene);
            if (cam == null)
            {
                var go = new GameObject("ReferenceCamera");
                SceneManager.MoveGameObjectToScene(go, s.gameObject.scene);
                cam = go.AddComponent<Camera>();
                cam.nearClipPlane = 0.01f;
                cam.allowHDR = true;
                Undo.RegisterCreatedObjectUndo(go, "Osaki World Settings");
            }

            var layer = cam.GetComponent<PostProcessLayer>();
            if (layer == null)
            {
                var guids = AssetDatabase.FindAssets("t:PostProcessResources");
                if (guids.Length == 0) { Debug.LogWarning("[OsakiWorldSettings] PostProcessResources が見つからない(Post Processing パッケージ未導入?)"); return; }
                layer = Undo.AddComponent<PostProcessLayer>(cam.gameObject);
                layer.Init(AssetDatabase.LoadAssetAtPath<PostProcessResources>(AssetDatabase.GUIDToAssetPath(guids[0])));
                layer.antialiasingMode = PostProcessLayer.Antialiasing.None;
            }
            int mask = 1 << s.volumeLayer;
            if ((layer.volumeLayer.value & mask) == 0) { layer.volumeLayer = layer.volumeLayer.value | mask; EditorUtility.SetDirty(layer); }
            if (layer.volumeTrigger == null) { layer.volumeTrigger = cam.transform; EditorUtility.SetDirty(layer); }

            if (!cam.CompareTag("MainCamera")) { cam.tag = "MainCamera"; EditorUtility.SetDirty(cam.gameObject); }
            if (!cam.enabled) { cam.enabled = true; EditorUtility.SetDirty(cam); }

            if (desc != null && desc.ReferenceCamera == null)
            {
                desc.ReferenceCamera = cam.gameObject;
                EditorUtility.SetDirty(desc);
            }
        }

        static Camera FindMainCamera(Scene scene)
        {
            foreach (var c in Object.FindObjectsOfType<Camera>(true))
                if (c.gameObject.scene == scene && c.CompareTag("MainCamera")) return c;
            return null;
        }
    }

    // ビルド直前に書き戻す(他の処理で空・環境光が変わっていても、この部品の値でワールドに入る)
    public sealed class OsakiWorldSettingsStep : ICommonSceneStep
    {
        public int Phase => 50;

        public void PrepareBuild()
        {
            foreach (var s in Object.FindObjectsOfType<OsakiWorldSettings>()) s.Apply();
        }

        public void PreparePlay() { }
        public void BeginPlay() { }
        public void ProcessScene(Scene scene, CommonSceneMode mode) { }
        public void EndPlay() { }
    }
}
#endif

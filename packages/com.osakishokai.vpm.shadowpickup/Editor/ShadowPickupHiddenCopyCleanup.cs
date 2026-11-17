// ShadowPickup のベイクが残す「隠れた複製」を消すシーン処理ステップ。
// ShadowPickupBaker は prefab とのつながりが無い物の見た目を読むため、marker 付きの複製を
// HideAndDontSave で作り、消さずに残す。残るとアクティブなシーンに見えない marker が溜まり、
// Play 中は原点に描かれる。Baker は出荷済みなので変えず、ベイクの直後(Phase ShadowPickup+1)にここで消す。
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using com.osakishokai.vpm.build;

namespace com.osakishokai.vpm.shadowpickup.editor
{
    public sealed class ShadowPickupHiddenCopyCleanup : ICommonSceneStep
    {
        public int Phase => CommonPhase.ShadowPickup + 1;

        public void PrepareBuild() { }
        public void PreparePlay() { }
        public void BeginPlay() { }
        public void EndPlay() { DestroyAll(); }

        public void ProcessScene(Scene scene, CommonSceneMode mode) { DestroyAll(); }

        /// <summary>アセットでない、HideAndDontSave の ShadowPickup 付き GameObject を全部消す。消した数を返す。</summary>
        public static int DestroyAll()
        {
            var targets = new List<GameObject>();
            foreach (ShadowPickup marker in Resources.FindObjectsOfTypeAll<ShadowPickup>())
            {
                if (marker == null || EditorUtility.IsPersistent(marker)) continue;
                GameObject root = marker.transform.root.gameObject;
                if ((root.hideFlags & HideFlags.HideAndDontSave) != HideFlags.HideAndDontSave) continue;
                if (!targets.Contains(root)) targets.Add(root);
            }
            foreach (GameObject go in targets) Object.DestroyImmediate(go);
            return targets.Count;
        }
    }
}
#endif

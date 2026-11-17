

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using com.osakishokai.vpm.lightmirror.us;

namespace com.osakishokai.vpm.lightmirror.editor
{
    [InitializeOnLoad]
    public static class OsakiLightMirrorMaskPreview
    {
        static readonly int MaskId = Shader.PropertyToID("_Mask");

        static OsakiLightMirrorMaskPreview()
        {
            EditorApplication.hierarchyChanged += RefreshAll;
            EditorSceneManager.sceneOpened += (s, m) => RefreshAll();
            Undo.undoRedoPerformed += RefreshAll;
            ObjectChangeEvents.changesPublished += OnChanges;
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorApplication.delayCall += RefreshAll;
        }

        static void OnPlayMode(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.ExitingEditMode) ForEach(m => Apply(m, false));
            else if (s == PlayModeStateChange.EnteredEditMode) RefreshAll();
        }

        static void OnChanges(ref ObjectChangeEventStream stream)
        {
            for (int i = 0; i < stream.length; i++)
            {
                if (stream.GetEventType(i) != ObjectChangeKind.ChangeGameObjectOrComponentProperties) continue;
                stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var e);
                var o = EditorUtility.InstanceIDToObject(e.instanceId);
                var lm = o as OsakiLightMirror;
                if (lm == null && o is GameObject go) lm = go.GetComponent<OsakiLightMirror>();
                if (lm != null) Refresh(lm);
            }
        }

        public static void RefreshAll()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            ForEach(Refresh);
        }

        public static void Refresh(OsakiLightMirror lm)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            Apply(lm, true);
        }

        static void ForEach(System.Action<OsakiLightMirror> f)
        {
            foreach (var lm in Object.FindObjectsOfType<OsakiLightMirror>(true)) f(lm);
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
                foreach (var lm in stage.prefabContentsRoot.GetComponentsInChildren<OsakiLightMirror>(true)) f(lm);
        }

        static void Apply(OsakiLightMirror lm, bool show)
        {
            if (lm == null) return;
            var so = new SerializedObject(lm);
            var mask = show ? so.FindProperty("mask").objectReferenceValue as Texture : null;
            Set(so.FindProperty("probeSurface").objectReferenceValue as Renderer, mask);
            Set(so.FindProperty("cameraScreen").objectReferenceValue as Renderer, mask);
            foreach (var n in new[] { "fullMirror", "playerMirror" })
            {
                var go = so.FindProperty(n).objectReferenceValue as GameObject;
                if (go != null) Set(go.GetComponent<Renderer>(), mask);
            }
        }

        static void Set(Renderer r, Texture mask)
        {
            if (r == null) return;
            if (mask == null) { if (r.HasPropertyBlock()) r.SetPropertyBlock(null); return; }
            var b = new MaterialPropertyBlock();
            r.GetPropertyBlock(b);
            b.SetTexture(MaskId, mask);
            r.SetPropertyBlock(b);
        }
    }
}

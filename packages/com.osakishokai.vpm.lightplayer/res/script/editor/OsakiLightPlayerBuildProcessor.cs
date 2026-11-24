using com.osakishokai.vpm.lightplayer.us;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Components;
using VRC.SDK3.Video.Components.AVPro;

namespace com.osakishokai.vpm.lightplayer.us.editor
{
    public sealed class OsakiLightPlayerBuildProcessor : IProcessSceneWithReport
    {
        public int callbackOrder => -110;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (!scene.IsValid()) return;
            int patched = 0;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                OsakiLightPlayer[] players = roots[r].GetComponentsInChildren<OsakiLightPlayer>(true);
                for (int i = 0; i < players.Length; i++)
                {
                    if (PatchPlayer(players[i])) patched++;
                }
            }
            if (patched > 0)
                Debug.Log("[LPLY-BUILD] SpeakerAvproMono を自動追加・接続: " + patched + " 台 (scene=" + scene.name + ")");
        }

        public static bool PatchPlayer(OsakiLightPlayer lp)
        {
            if (lp == null) return false;
            var soLp = new SerializedObject(lp);
            var pAvp = soLp.FindProperty("avproPlayer");
            var pSpk = soLp.FindProperty("avproSpeaker");
            var pMono = soLp.FindProperty("avproSpeakerMono");
            if (pAvp == null || pSpk == null || pMono == null) return false;
            if (pMono.objectReferenceValue != null) return false;

            var avp = pAvp.objectReferenceValue as VRCAVProVideoPlayer;
            var spk = pSpk.objectReferenceValue as AudioSource;
            if (avp == null || spk == null) return false;

            Transform existing = lp.transform.Find("SpeakerAvproMono");
            GameObject go;
            if (existing != null)
            {
                go = existing.gameObject;
            }
            else
            {
                go = new GameObject("SpeakerAvproMono");
                go.transform.SetParent(lp.transform, false);
                go.transform.localPosition = spk.transform.localPosition;
                go.transform.localRotation = spk.transform.localRotation;
                go.transform.localScale = spk.transform.localScale;
            }

            AudioSource mono = go.GetComponent<AudioSource>();
            if (mono == null) mono = go.AddComponent<AudioSource>();
            mono.playOnAwake = false;
            mono.loop = false;
            mono.mute = false;
            mono.volume = 0f;
            mono.spatialBlend = 0f;
            mono.spatialize = false;
            mono.spatializePostEffects = false;
            mono.panStereo = 0f;
            mono.priority = spk.priority;
            mono.outputAudioMixerGroup = spk.outputAudioMixerGroup;
            mono.bypassEffects = spk.bypassEffects;
            mono.bypassListenerEffects = spk.bypassListenerEffects;
            mono.bypassReverbZones = spk.bypassReverbZones;

            VRCSpatialAudioSource vsp = go.GetComponent<VRCSpatialAudioSource>();
            if (vsp == null) vsp = go.AddComponent<VRCSpatialAudioSource>();
            vsp.EnableSpatialization = false;
            vsp.Gain = 0f;
            vsp.UseAudioSourceVolumeCurve = false;

            VRCAVProVideoSpeaker avs = go.GetComponent<VRCAVProVideoSpeaker>();
            if (avs == null) avs = go.AddComponent<VRCAVProVideoSpeaker>();
            var soAvs = new SerializedObject(avs);
            var fVp = soAvs.FindProperty("videoPlayer");
            var fMode = soAvs.FindProperty("mode");
            if (fVp != null) fVp.objectReferenceValue = avp;
            if (fMode != null) fMode.enumValueIndex = (int)VRCAVProVideoSpeaker.ChannelMode.MonoLeft;
            soAvs.ApplyModifiedPropertiesWithoutUndo();

            pMono.objectReferenceValue = mono;
            soLp.ApplyModifiedPropertiesWithoutUndo();
            UdonSharpEditorUtility.CopyProxyToUdon(lp);
            return true;
        }
    }
}



using com.osakishokai.vpm.lightplayer.us;
using UdonSharpEditor;
using UnityEditor;

namespace com.osakishokai.vpm.lightplayer.us.editor
{
    [CustomEditor(typeof(OsakiLightPlayer))]
    public class OsakiLightPlayerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
            EditorGUILayout.HelpBox("曲は子の LightPlayerBgm の AudioSource に、静止画は idleTexture に入れる。大きさは根の Scale で変える。何台置いても曲は 1 台分だけ鳴る。", MessageType.Info);
            DrawDefaultInspector();
        }
    }
}

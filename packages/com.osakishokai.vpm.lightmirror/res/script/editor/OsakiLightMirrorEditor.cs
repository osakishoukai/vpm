
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using com.osakishokai.vpm.lightmirror.us;

namespace com.osakishokai.vpm.lightmirror.editor
{
    [CustomEditor(typeof(OsakiLightMirror))]
    public class OsakiLightMirrorEditor : Editor
    {
        static readonly string[] ModeNames = { "フルミラー(全部を映す)", "プレイヤーオンリー(人だけ+背景)", "カメラミラー(撮った映像を鏡の向きで。軽い)" };
        bool showInner;

        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
            serializedObject.Update();

            var mode = serializedObject.FindProperty("mode");
            mode.intValue = EditorGUILayout.Popup("モード", mode.intValue, ModeNames);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("offDistance"), new GUIContent("離れたら戻す距離(m)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("showProbe"), new GUIContent("普段の映り込みを出す", "鏡が点いていない時に、周りの景色(リフレクションプローブ)を映す。外すと透明になる(インタラクトはそのまま効く)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("mask"), new GUIContent("マスク(白=鏡)"));

            if (mode.intValue == OsakiLightMirror.ModePlayerOnly)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("background"), new GUIContent("背景の画像"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("backgroundColor"), new GUIContent("背景の色(画像が無い時)"));
            }
            if (mode.intValue == OsakiLightMirror.ModeCamera)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("cameraResolution"), new GUIContent("縦の画素数"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("cameraLayers"), new GUIContent("写すレイヤー"));
                EditorGUILayout.HelpBox("カメラの位置・向き・画角は、子の Camera を動かして変えられます。", MessageType.None);
            }
            EditorGUILayout.HelpBox("大きさは、この GameObject の Scale(X=幅・Y=高さ)で変えます。鏡の面は -Z 側です。", MessageType.None);

            showInner = EditorGUILayout.Foldout(showInner, "中身(prefab のまま触らない)");
            if (showInner)
            {
                EditorGUI.indentLevel++;
                foreach (var n in new[] { "probeSurface", "fullMirror", "playerMirror", "cameraScreen", "mirrorCamera" })
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(n));
                EditorGUI.indentLevel--;
            }
            if (serializedObject.ApplyModifiedProperties()) OsakiLightMirrorMaskPreview.Refresh((OsakiLightMirror)target);
        }
    }
}

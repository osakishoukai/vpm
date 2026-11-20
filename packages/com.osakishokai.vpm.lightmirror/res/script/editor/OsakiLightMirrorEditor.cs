
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
            EditorGUILayout.PropertyField(serializedObject.FindProperty("showProbe"), new GUIContent("普段の見た目を出す", "鏡が点いていない時に、普段の材質(空なら周りの景色=リフレクションプローブの映り込み)を出す。外すと透明になる(インタラクトはそのまま効く)"));
            var idle = serializedObject.FindProperty("idleMaterial");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(idle, new GUIContent("普段の材質", "鏡が点いていない時の見た目(OsakiGlass_Standard などの硝子)。空ならプローブの映り込み。インタラクトで鏡に替わる"));
            bool idleChanged = EditorGUI.EndChangeCheck();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("mask"), new GUIContent("マスク(白=鏡)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("seSource"), new GUIContent("点ける音の AudioSource", "押して点けた時に鳴らす。空なら鳴らない"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("seOn"), new GUIContent("点ける音"));

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
            if (serializedObject.ApplyModifiedProperties())
            {
                if (idleChanged) SyncIdleMaterial();
                OsakiLightMirrorMaskPreview.Refresh((OsakiLightMirror)target);
            }
        }

        // 普段の材質を、エディタでも見えるように中身の Probe へ写す(実行時は U# が同じことをする)。空に戻したら prefab の材質へ戻す
        void SyncIdleMaterial()
        {
            var so = new SerializedObject(target);
            var r = so.FindProperty("probeSurface").objectReferenceValue as Renderer;
            if (r == null) return;
            var m = so.FindProperty("idleMaterial").objectReferenceValue as Material;
            if (m != null)
            {
                Undo.RecordObject(r, "LightMirror 普段の材質");
                r.sharedMaterial = m;
                PrefabUtility.RecordPrefabInstancePropertyModifications(r);
                return;
            }
            if (!PrefabUtility.IsPartOfPrefabInstance(r)) return;
            var rso = new SerializedObject(r);
            var mats = rso.FindProperty("m_Materials");
            if (mats != null && mats.prefabOverride) PrefabUtility.RevertPropertyOverride(mats, InteractionMode.UserAction);
        }
    }
}

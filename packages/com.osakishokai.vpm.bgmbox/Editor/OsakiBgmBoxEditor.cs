// BgmBox のインスペクターに「メッシュに合わせる」を足す。メッシュを差し替えたら押す。
//   当たり判定(BoxCollider)をメッシュの外形に、音符の出る位置と切り替え表示をメッシュの上面に合わせる
// 商品のビルダーからは OsakiBgmBoxEditor.FitToMesh(go) を呼ぶ
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace com.osakishokai.vpm.bgmbox.editor
{
    [CustomEditor(typeof(OsakiBgmBox))]
    public class OsakiBgmBoxEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
            DrawDefaultInspector();
            EditorGUILayout.Space();
            if (GUILayout.Button("メッシュに合わせる(当たり判定・音符の位置)"))
                FitToMesh(((OsakiBgmBox)target).gameObject);
        }

        public static void FitToMesh(GameObject go)
        {
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                Debug.LogWarning("[BgmBox] メッシュが無い: " + go.name);
                return;
            }
            var b = mf.sharedMesh.bounds;
            var col = go.GetComponent<BoxCollider>();
            Undo.RecordObject(col, "Fit BgmBox");
            col.center = b.center;
            col.size = b.size;
            var notes = go.transform.Find("Notes");
            if (notes != null)
            {
                Undo.RecordObject(notes, "Fit BgmBox");
                notes.localPosition = new Vector3(b.center.x, b.max.y + 0.005f, b.center.z);
            }
            var label = go.transform.Find("Label");
            if (label != null)
            {
                Undo.RecordObject(label, "Fit BgmBox");
                label.localPosition = new Vector3(b.center.x, b.max.y + 0.12f, b.center.z);
            }
            EditorUtility.SetDirty(col);
        }
    }
}

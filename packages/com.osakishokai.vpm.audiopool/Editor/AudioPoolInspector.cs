// AudioPool設定Inspector(Common基盤 AudioPool 世代1)。このフォルダは初回公開時に丸ごと凍結する。
// mergeGroupKeyは既存キーのドロップダウン＋新規登録欄で編集する。
// キー一覧は最初の表示時と明示的な「更新」時だけプロジェクト全体を走査する。
using System;
using System.Collections.Generic;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;
using com.osakishokai.vpm.audiopool;
using Object = UnityEngine.Object;

namespace com.osakishokai.vpm.audiopool.editor
{
    internal static class AudioPoolMergeKeyCache
    {
        private static readonly SortedSet<string> Keys =
            new SortedSet<string>(StringComparer.Ordinal);

        private static bool initialized;
        private static bool scanning;

        public static bool Initialized => initialized;

        public static void EnsureInitialized()
        {
            if (!initialized && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Refresh();
            }
        }

        public static List<string> GetKeys()
        {
            return new List<string>(Keys);
        }

        public static void Remember(string key)
        {
            if (!string.IsNullOrEmpty(key))
            {
                Keys.Add(key);
            }
        }

        public static void Refresh()
        {
            if (scanning || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            scanning = true;
            var collected = new SortedSet<string>(StringComparer.Ordinal);
            int failureCount = 0;

            try
            {
                // 未保存・編集中の現在のシーンも拾う。
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    CollectScene(SceneManager.GetSceneAt(i), collected);
                }

                // 開いているPrefab Stageの未保存の変更も拾う。
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null && stage.prefabContentsRoot != null)
                {
                    CollectRoot(stage.prefabContentsRoot, collected);
                }

                // シーンアセットのファイル読み取り専用スキャンは行わない。
                // このUnityバージョンのEditorSceneManagerには、シーンを開かずに
                // 読み取り専用でスキャンできるAPI(OpenPreviewScene等)が無いため、
                // 開いていないシーンファイル内のみに存在するあいことばは一覧に出ない
                // (Pool設定はPrefabに持たせる運用が前提なので実用上の影響は小さい)。
                string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
                int total = prefabGuids.Length;
                int completed = 0;

                foreach (string guid in prefabGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    ShowProgress(path, completed++, total);

                    try
                    {
                        // Prefabアセットを読むだけで、インスタンス化・保存しない。
                        GameObject prefab =
                            AssetDatabase.LoadAssetAtPath<GameObject>(path);

                        if (prefab != null)
                        {
                            CollectRoot(prefab, collected);
                        }
                    }
                    catch (Exception exception)
                    {
                        failureCount++;
                        Debug.LogWarning(
                            "[common PoolInspector] "
                            + "Prefabのキー収集に失敗しました: "
                            + path + "\n" + exception.Message);
                    }
                }

                Keys.Clear();
                foreach (string key in collected)
                {
                    Keys.Add(key);
                }

                initialized = true;

                if (failureCount > 0)
                {
                    Debug.LogWarning(
                        "[common PoolInspector] "
                        + "あいことば一覧は一部のアセットを収集できていません。"
                        + "Consoleを確認してから「更新」を押してください。"
                        + " 失敗数: " + failureCount);
                }
            }
            catch (Exception exception)
            {
                // Inspector再描画ごとの自動再試行を避ける。
                // 以前のキャッシュがあれば保持し、明示的な更新で再試行する。
                initialized = true;
                Debug.LogException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                scanning = false;
            }
        }

        private static void ShowProgress(string path, int completed, int total)
        {
            EditorUtility.DisplayProgressBar(
                "Poolのあいことば一覧を更新",
                path,
                total == 0 ? 1f : (float)completed / total);
        }

        private static void CollectScene(Scene scene, ISet<string> destination)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                CollectRoot(root, destination);
            }
        }

        private static void CollectRoot(
            GameObject root, ISet<string> destination)
        {
            foreach (AudioPool pool in root.GetComponentsInChildren<AudioPool>(true))
            {
                AddKey(destination, pool.mergeGroupKey);
            }
        }

        private static void AddKey(ISet<string> destination, string key)
        {
            if (!string.IsNullOrEmpty(key))
            {
                destination.Add(key);
            }
        }
    }

    [CustomEditor(typeof(AudioPool))]
    [CanEditMultipleObjects]
    public sealed class AudioPoolInspector : Editor
    {
        private const string SourcesPropertyName = "audioSources";

        private string newKey = "";

        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target))
            {
                return;
            }

            AudioPoolMergeKeyCache.EnsureInitialized();
            serializedObject.Update();

            bool readOnly = EditorApplication.isPlayingOrWillChangePlaymode;

            using (new EditorGUI.DisabledScope(readOnly))
            {
                DrawMergeGroupKey();

                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("localCapacity"),
                    new GUIContent("ローカル容量"));

                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("sharedCapacity"),
                    new GUIContent("共有容量"));

                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("minDistance"),
                    new GUIContent("最小距離"));

                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("maxDistance"),
                    new GUIContent("最大距離"));
            }

            if (serializedObject.ApplyModifiedProperties())
            {
                // 複数選択時も、それぞれのUdon backingへ反映する。
                foreach (Object inspected in targets)
                {
                    var proxy = (UdonSharpBehaviour)inspected;
                    UdonBehaviour backing =
                        UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);

                    if (backing != null)
                    {
                        Undo.RecordObject(backing, "Change pool settings");
                    }

                    AudioPoolSceneStep.Sync(proxy);
                }
            }

            EditorGUILayout.Space();

            EditorGUILayout.HelpBox(
                "同じ空でないあいことばのPoolを、シーン内・種類別に共有します。\n"
                + "複数なら共有容量、1個だけ／未設定ならローカル容量で"
                + "Sourceを自動生成します。\n"
                + "設定が違う場合はHierarchy順の先頭を採用し、警告を出します。",
                MessageType.Info);

            EditorGUILayout.HelpBox(
                readOnly
                    ? "Play中の設定変更・再生成は行いません。"
                        + "変更する場合はPlayを終了してください。"
                    : "Sourceの手動生成は不要です。ビルド時とPlay開始後に生成します。\n"
                        + "Play時はReload Sceneを有効にしてください。",
                MessageType.None);

            // 生成前の旧配列を確認でき、Play中には共有先・生成結果も確認できる。
            // 自動生成用の配列・配線なので編集は許可しない。
            serializedObject.Update();

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("sharedPool"),
                    new GUIContent("共有先（自動設定）"));

                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty(SourcesPropertyName),
                    new GUIContent("Source配列（自動生成）"),
                    true);
            }
        }

        private void DrawMergeGroupKey()
        {
            SerializedProperty keyProperty =
                serializedObject.FindProperty("mergeGroupKey");

            List<string> keys = AudioPoolMergeKeyCache.GetKeys();

            // キャッシュ後に外部ツール等で追加された現在値も必ず表示する。
            if (!keyProperty.hasMultipleDifferentValues
                && !string.IsNullOrEmpty(keyProperty.stringValue)
                && !keys.Contains(keyProperty.stringValue))
            {
                keys.Add(keyProperty.stringValue);
                keys.Sort(StringComparer.Ordinal);
            }

            string[] labels = new string[keys.Count + 1];
            labels[0] = "（未設定・共有しない）";

            int selectedIndex = 0;

            for (int i = 0; i < keys.Count; i++)
            {
                labels[i + 1] = "'" + keys[i] + "'";

                if (string.Equals(
                    keys[i], keyProperty.stringValue, StringComparison.Ordinal))
                {
                    selectedIndex = i + 1;
                }
            }

            EditorGUILayout.BeginHorizontal();

            EditorGUI.showMixedValue = keyProperty.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();

            int nextIndex = EditorGUILayout.Popup(
                "あいことば", selectedIndex, labels);

            if (EditorGUI.EndChangeCheck())
            {
                keyProperty.stringValue = nextIndex == 0 ? "" : keys[nextIndex - 1];
            }

            EditorGUI.showMixedValue = false;

            if (GUILayout.Button("更新", GUILayout.Width(48f)))
            {
                AudioPoolMergeKeyCache.Refresh();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            newKey = EditorGUILayout.TextField("新しいあいことば", newKey);

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(newKey)))
            {
                if (GUILayout.Button("作成して選択", GUILayout.Width(104f)))
                {
                    // 新規入力だけ前後の意図しない空白を除く。
                    // 保存済みのキーは勝手に正規化せず、完全一致で扱う。
                    string key = newKey.Trim();

                    AudioPoolMergeKeyCache.Remember(key);
                    keyProperty.stringValue = key;
                    newKey = "";
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "一覧はAudioPoolを含む全Prefab・シーンから収集します。\n"
                + "初回表示と「更新」時だけ全走査します。"
                + "他のアセットで追加・変更した場合は更新してください。\n"
                + "大文字・小文字・空白を含めた完全一致です。"
                + "別種のPoolとは、同じあいことばでも混ぜません。",
                MessageType.None);

            if (!AudioPoolMergeKeyCache.Initialized
                && EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorGUILayout.HelpBox(
                    "Play中はプロジェクト全体の走査を行いません。"
                    + "一覧の取得はPlay終了後に行ってください。",
                    MessageType.Info);
            }
        }
    }
}

// AudioPoolの生成・集約ステップ(Common基盤 AudioPool 世代1)。
// このフォルダは初回公開時に丸ごと凍結する。修正・機能追加が必要になったら、
// このフォルダは変更せず、新しい世代フォルダ(AudioPool2 等)を作ってそこにStepを置く。
// OsakiSceneProcessor(ランチャー)は ICommonSceneStep を自動検出して呼ぶだけなので、
// 新世代を足してもランチャーは変更不要。
using System;
using System.Collections.Generic;
using System.Text;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Components;
using VRC.Udon;
using com.osakishokai.vpm.build;
using Object = UnityEngine.Object;

namespace com.osakishokai.vpm.audiopool.editor
{
    public sealed class AudioPoolSceneStep : ICommonSceneStep
    {
        private const string LogPrefix = "[common AudioPool] ";

        // 同じシーンインスタンスに複数回呼ばれても二重生成しない。
        // シーンハンドルだけでは、破棄後のハンドル再利用を区別できないため
        // 実際のコンポーネントを記録する。アセットや実行時フィールドには保存しない。
        private static readonly HashSet<UdonSharpBehaviour> ProcessedPools =
            new HashSet<UdonSharpBehaviour>();

        public int Phase => CommonPhase.Pool;

        public void PrepareBuild() { }

        public void PreparePlay() { }

        public void BeginPlay()
        {
            ProcessedPools.Clear();
        }

        public void EndPlay()
        {
            // Domain Reloadが無効な場合も、次のPlayへ記録を持ち越さない。
            ProcessedPools.Clear();
        }

        public void ProcessScene(Scene scene, CommonSceneMode mode)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            ProcessedPools.RemoveWhere(pool => pool == null);

            var pools = new List<AudioPool>();

            GameObject[] roots = scene.GetRootGameObjects();
            Array.Sort(
                roots,
                (a, b) => a.transform.GetSiblingIndex()
                    .CompareTo(b.transform.GetSiblingIndex()));

            foreach (GameObject root in roots)
            {
                CollectPools(root.transform, pools);
            }

            List<List<AudioPool>> groups = MakeGroups(pools, pool => pool.mergeGroupKey);

            // 破壊的な変更より前に、シーン内の全対象を検証する。
            foreach (List<AudioPool> group in groups)
            {
                WarnAudioMismatch(group);

                AudioPool selected = group[0];
                ValidateCapacity(
                    selected, selected.localCapacity, selected.sharedCapacity);
                ValidateAudioDistance(selected);

                foreach (AudioPool client in group)
                {
                    ValidateLocalSources(client, client.audioSources);
                }
            }

            foreach (List<AudioPool> group in groups)
            {
                ProcessAudioGroup(scene, group);
            }
        }

        private static void CollectPools(Transform transform, List<AudioPool> pools)
        {
            // GetComponentsは同一GameObject上のコンポーネント順。
            foreach (AudioPool pool in transform.GetComponents<AudioPool>())
            {
                if (pool.sharedPool == null && !ProcessedPools.Contains(pool))
                {
                    pools.Add(pool);
                }
            }

            // 非アクティブオブジェクトも含めて、Sibling順で走査する。
            for (int i = 0; i < transform.childCount; i++)
            {
                CollectPools(transform.GetChild(i), pools);
            }
        }

        private static void ProcessAudioGroup(Scene scene, List<AudioPool> group)
        {
            AudioPool selected = group[0];

            if (group.Count == 1)
            {
                DestroyLocalSources(selected.audioSources);
                selected.sharedPool = null;
                selected.audioSources =
                    CreateAudioSources(
                        selected.transform, selected.localCapacity,
                        selected.minDistance, selected.maxDistance);

                Sync(selected);
                ProcessedPools.Add(selected);
                return;
            }

            GameObject root = CreateInactiveRoot(
                scene, "__common_AudioPool_" + selected.mergeGroupKey);

            AudioPool backend = root.AddUdonSharpComponent<AudioPool>();
            backend.mergeGroupKey = "";
            backend.localCapacity = selected.localCapacity;
            backend.sharedCapacity = selected.sharedCapacity;
            backend.minDistance = selected.minDistance;
            backend.maxDistance = selected.maxDistance;
            backend.sharedPool = null;
            backend.audioSources =
                CreateAudioSources(
                    root.transform, selected.sharedCapacity,
                    selected.minDistance, selected.maxDistance);

            Sync(backend);
            ProcessedPools.Add(backend);

            foreach (AudioPool client in group)
            {
                DestroyLocalSources(client.audioSources);
                client.audioSources = new AudioSource[0];
                client.sharedPool = backend;

                Sync(client);
                ProcessedPools.Add(client);
            }

            // Source／Udon変数／窓口の配線を済ませてから有効にする。
            root.SetActive(true);

            Debug.Log(
                LogPrefix + "Audio: " + group.Count + " pools -> "
                + selected.sharedCapacity + " sources / '"
                + selected.mergeGroupKey + "'",
                backend);
        }

        private static void WarnAudioMismatch(List<AudioPool> group)
        {
            AudioPool selected = group[0];
            bool mismatch = false;

            foreach (AudioPool pool in group)
            {
                if (pool.localCapacity != selected.localCapacity
                    || pool.sharedCapacity != selected.sharedCapacity
                    || !Mathf.Approximately(pool.minDistance, selected.minDistance)
                    || !Mathf.Approximately(pool.maxDistance, selected.maxDistance))
                {
                    mismatch = true;
                    break;
                }
            }

            if (!mismatch)
            {
                return;
            }

            var message = new StringBuilder();
            message.Append(LogPrefix)
                .Append("あいことば '").Append(selected.mergeGroupKey)
                .AppendLine("' を持つAudioPoolの設定が食い違っています:");

            foreach (AudioPool pool in group)
            {
                message.Append("  ").Append(DescribePool(pool))
                    .Append(": localCapacity=").Append(pool.localCapacity)
                    .Append(", sharedCapacity=").Append(pool.sharedCapacity)
                    .Append(", minDistance=").Append(pool.minDistance)
                    .Append(", maxDistance=").Append(pool.maxDistance)
                    .AppendLine();
            }

            message.Append("採用したのは ")
                .Append(DescribePool(selected))
                .Append(" です（Hierarchy順の先頭）。");

            Debug.LogWarning(message.ToString(), selected);
        }

        private static void ValidateAudioDistance(AudioPool owner)
        {
            if (owner.minDistance <= 0f || owner.maxDistance < owner.minDistance)
            {
                throw new BuildFailedException(
                    LogPrefix + "採用するAudioPoolの距離設定が不正です"
                    + "(minDistance>0 かつ maxDistance>=minDistance): "
                    + DescribePool(owner)
                    + " / minDistance=" + owner.minDistance
                    + ", maxDistance=" + owner.maxDistance);
            }
        }

        private static AudioSource[] CreateAudioSources(
            Transform parent, int capacity, float minDistance, float maxDistance)
        {
            var sources = new AudioSource[capacity];

            for (int i = 0; i < capacity; i++)
            {
                var instance = new GameObject("AudioSource_" + i);
                instance.SetActive(false);
                instance.transform.SetParent(parent, false);

                AudioSource source = instance.AddComponent<AudioSource>();
                source.enabled = true;
                source.playOnAwake = false;
                source.loop = false;
                source.volume = 1f;
                source.pitch = 1f;
                source.spatialBlend = 1f;

                // 短いSE向け。追従・瞬間移動による意図しないピッチ変動を避ける。
                source.dopplerLevel = 0f;

                // Unity側の一般的な3D音源設定。距離はAudioPoolの設定値。
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.minDistance = minDistance;
                source.maxDistance = maxDistance;

                // VRChatの距離減衰はVRCSpatialAudioSourceが決める。Farだけを
                // maxDistanceに揃え、Gain / Near / UseAudioSourceVolumeCurve等は
                // SDKのコンポーネント追加時の標準値をそのまま採用する。
                // VRChat内の距離減衰とEditor単体の聞こえ方が同一とは限らない。
                // Source PrefabやAudioClipのアセット参照は一切使用しない。
                VRCSpatialAudioSource spatial = instance.AddComponent<VRCSpatialAudioSource>();
                spatial.Far = maxDistance;

                source.Stop();
                sources[i] = source;
                instance.SetActive(true);
            }

            return sources;
        }

        private static List<List<T>> MakeGroups<T>(
            List<T> pools, Func<T, string> getKey)
        {
            var result = new List<List<T>>();
            var keyedGroups =
                new Dictionary<string, List<T>>(StringComparer.Ordinal);

            foreach (T pool in pools)
            {
                string key = getKey(pool);

                // null／空文字列は必ず独立した1件のグループにする。
                // 非空のキーは大文字小文字・空白も含めた完全一致。
                if (string.IsNullOrEmpty(key))
                {
                    result.Add(new List<T> { pool });
                    continue;
                }

                if (!keyedGroups.TryGetValue(key, out List<T> group))
                {
                    group = new List<T>();
                    keyedGroups.Add(key, group);
                    result.Add(group);
                }

                group.Add(pool);
            }

            // Dictionaryの列挙順には依存しない。
            return result;
        }

        private static GameObject CreateInactiveRoot(Scene scene, string name)
        {
            var root = new GameObject(name);
            root.SetActive(false);
            SceneManager.MoveGameObjectToScene(root, scene);
            return root;
        }

        private static string DescribePool(Component pool)
        {
            string path = "";
            Transform current = pool.transform;

            while (current != null)
            {
                string part = current.name + "[" + current.GetSiblingIndex() + "]";
                path = string.IsNullOrEmpty(path) ? part : part + "/" + path;
                current = current.parent;
            }

            Component[] components = pool.GetComponents(pool.GetType());
            int componentIndex = Array.IndexOf(components, pool);

            return pool.gameObject.scene.name + "/" + path
                + " (" + pool.GetType().Name + "#" + componentIndex + ")";
        }

        private static void ValidateCapacity(
            Component owner, int localCapacity, int sharedCapacity)
        {
            if (localCapacity < 1 || sharedCapacity < 1)
            {
                throw new BuildFailedException(
                    LogPrefix + "採用するPoolの容量は両方とも1以上にしてください: "
                    + DescribePool(owner)
                    + " / localCapacity=" + localCapacity
                    + ", sharedCapacity=" + sharedCapacity);
            }
        }

        private static void ValidateNoUdon(GameObject root)
        {
            if (root.GetComponentsInChildren<UdonBehaviour>(true).Length != 0
                || root.GetComponentsInChildren<UdonSharpBehaviour>(true).Length != 0)
            {
                throw new BuildFailedException(
                    LogPrefix
                    + "Source専用オブジェクトにはUdonを含められません: "
                    + root.name);
            }
        }

        private static void ValidateLocalSources<T>(
            Component owner, T[] sources) where T : Component
        {
            if (sources == null)
            {
                return;
            }

            foreach (T source in sources)
            {
                if (source == null)
                {
                    continue;
                }

                // 旧実装で生成済みのSourceも安全に置き換える。
                // GameObjectごと削除するため、Pool直下の専用子だけを許可する。
                if (EditorUtility.IsPersistent(source)
                    || source.gameObject.scene != owner.gameObject.scene
                    || source.transform.parent != owner.transform)
                {
                    throw new BuildFailedException(
                        LogPrefix
                        + "既存のローカルSourceはPool直下の専用子にしてください: "
                        + DescribePool(owner) + " / " + source.name);
                }

                ValidateNoUdon(source.gameObject);
            }
        }

        private static void DestroyLocalSources<T>(T[] sources) where T : Component
        {
            if (sources == null)
            {
                return;
            }

            var deleted = new HashSet<GameObject>();

            foreach (T source in sources)
            {
                if (source == null)
                {
                    continue;
                }

                GameObject gameObject = source.gameObject;
                if (deleted.Add(gameObject))
                {
                    // Editor専用処理。再生中も、配列の置換と同時に旧Sourceを除去する。
                    // アセットを削除しないことはValidateLocalSourcesで検証済み。
                    Object.DestroyImmediate(gameObject);
                }
            }
        }

        public static void Sync(UdonSharpBehaviour proxy)
        {
            UdonSharpEditorUtility.CopyProxyToUdon(proxy);

            UdonBehaviour backing =
                UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);

            if (EditorApplication.isPlaying)
            {
                // 初期化済みのUdon VMにも、実行時に読む公開変数を明示的に反映。
                // UdonSharpBehaviour参照はproxyではなくbackingを渡す。
                // VM未初期化の場合の初期値は上のCopyProxyToUdonに任せる。
                if (backing != null)
                {
                    AudioPool audio = proxy as AudioPool;
                    if (audio != null)
                    {
                        backing.SetProgramVariable(
                            nameof(AudioPool.audioSources), audio.audioSources);

                        backing.SetProgramVariable(
                            nameof(AudioPool.sharedPool),
                            audio.sharedPool == null
                                ? null
                                : UdonSharpEditorUtility.GetBackingUdonBehaviour(
                                    audio.sharedPool));
                    }
                }

                // Play中の一時変更を、Prefabの保存操作やUndoに登録しない。
                return;
            }

            EditorUtility.SetDirty(proxy);

            if (backing != null)
            {
                EditorUtility.SetDirty(backing);
            }

            if (PrefabUtility.IsPartOfPrefabInstance(proxy))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(proxy);

                if (backing != null)
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(backing);
                }
            }
        }
    }
}

// ParticlePoolの生成・集約ステップ(Common基盤 ParticlePool 世代1)。
// このフォルダは初回公開時に丸ごと凍結する。修正・機能追加が必要になったら、
// このフォルダは変更せず、新しい世代フォルダ(ParticlePool2 等)を作ってそこにStepを置く。
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
using VRC.Udon;
using com.osakishokai.vpm.build;
using Object = UnityEngine.Object;

namespace com.osakishokai.vpm.particlepool.editor
{
    public sealed class ParticlePoolSceneStep : ICommonSceneStep
    {
        private const string LogPrefix = "[common ParticlePool] ";

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

            var pools = new List<ParticlePool>();

            GameObject[] roots = scene.GetRootGameObjects();
            Array.Sort(
                roots,
                (a, b) => a.transform.GetSiblingIndex()
                    .CompareTo(b.transform.GetSiblingIndex()));

            foreach (GameObject root in roots)
            {
                CollectPools(root.transform, pools);
            }

            List<List<ParticlePool>> groups = MakeGroups(pools, pool => pool.mergeGroupKey);

            // 破壊的な変更より前に、シーン内の全対象を検証する。
            foreach (List<ParticlePool> group in groups)
            {
                WarnParticleMismatch(group);

                ParticlePool selected = group[0];
                ValidateCapacity(
                    selected, selected.localCapacity, selected.sharedCapacity);
                ValidateParticleSource(selected.sourcePrefab, selected);

                // 非採用メンバーのPrefab・容量は警告だけ。
                // 採用する設定が有効なら、不一致を理由にビルドを失敗させない。
                foreach (ParticlePool client in group)
                {
                    ValidateLocalSources(client, client.particleSystems);
                }
            }

            foreach (List<ParticlePool> group in groups)
            {
                ProcessParticleGroup(scene, group);
            }
        }

        private static void CollectPools(Transform transform, List<ParticlePool> pools)
        {
            // GetComponentsは同一GameObject上のコンポーネント順。
            foreach (ParticlePool pool in transform.GetComponents<ParticlePool>())
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

        private static void ProcessParticleGroup(
            Scene scene, List<ParticlePool> group)
        {
            ParticlePool selected = group[0];

            if (group.Count == 1)
            {
                DestroyLocalSources(selected.particleSystems);
                selected.sharedPool = null;
                selected.particleSystems = CreateParticleSources(
                    selected.sourcePrefab,
                    selected.transform,
                    selected.localCapacity);

                Sync(selected);
                ProcessedPools.Add(selected);
                return;
            }

            GameObject root = CreateInactiveRoot(
                scene, "__common_ParticlePool_" + selected.mergeGroupKey);

            ParticlePool backend = root.AddUdonSharpComponent<ParticlePool>();
            backend.mergeGroupKey = "";
            backend.localCapacity = selected.localCapacity;
            backend.sharedCapacity = selected.sharedCapacity;
            backend.sourcePrefab = selected.sourcePrefab;
            backend.sharedPool = null;
            backend.particleSystems = CreateParticleSources(
                selected.sourcePrefab, root.transform, selected.sharedCapacity);

            Sync(backend);
            ProcessedPools.Add(backend);

            foreach (ParticlePool client in group)
            {
                DestroyLocalSources(client.particleSystems);
                client.particleSystems = new ParticleSystem[0];
                client.sharedPool = backend;

                Sync(client);
                ProcessedPools.Add(client);
            }

            root.SetActive(true);

            Debug.Log(
                LogPrefix + "Particle: " + group.Count + " pools -> "
                + selected.sharedCapacity + " systems / '"
                + selected.mergeGroupKey + "'",
                backend);
        }

        private static void WarnParticleMismatch(List<ParticlePool> group)
        {
            ParticlePool selected = group[0];
            bool mismatch = false;

            foreach (ParticlePool pool in group)
            {
                if (pool.localCapacity != selected.localCapacity
                    || pool.sharedCapacity != selected.sharedCapacity
                    || pool.sourcePrefab != selected.sourcePrefab)
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
                .AppendLine("' を持つParticlePoolの設定が食い違っています:");

            foreach (ParticlePool pool in group)
            {
                message.Append("  ").Append(DescribePool(pool))
                    .Append(": localCapacity=").Append(pool.localCapacity)
                    .Append(", sharedCapacity=").Append(pool.sharedCapacity)
                    .Append(", sourcePrefab=").Append(DescribePrefab(pool.sourcePrefab))
                    .AppendLine();
            }

            message.Append("採用したのは ")
                .Append(DescribePool(selected))
                .Append(" です（Hierarchy順の先頭）。")
                .Append("Prefabが異なる場合も、採用したPrefabに統一します。");

            Debug.LogWarning(message.ToString(), selected);
        }

        private static string DescribePrefab(ParticleSystem prefab)
        {
            if (prefab == null)
            {
                return "(未設定)";
            }

            string path = AssetDatabase.GetAssetPath(prefab);
            return string.IsNullOrEmpty(path)
                ? prefab.name + " (Prefabアセットではありません)"
                : path;
        }

        private static void ValidateParticleSource(
            ParticleSystem template, ParticlePool owner)
        {
            if (template == null)
            {
                throw new BuildFailedException(
                    LogPrefix + "採用するParticlePoolのsourcePrefabが未設定です: "
                    + DescribePool(owner));
            }

            if (!PrefabUtility.IsPartOfPrefabAsset(template)
                || template.transform.parent != null)
            {
                throw new BuildFailedException(
                    LogPrefix
                    + "sourcePrefabにはPrefabアセットのルート上の"
                    + "ParticleSystemを指定してください: "
                    + DescribePool(owner));
            }

            ValidateNoUdon(template.gameObject);

            if (template.GetComponents<ParticleSystem>().Length != 1)
            {
                throw new BuildFailedException(
                    LogPrefix
                    + "Particle用PrefabのルートにはParticleSystemを"
                    + "1つだけ配置してください: "
                    + DescribePrefab(template));
            }

            if (template.GetComponentsInChildren<AudioSource>(true).Length != 0)
            {
                throw new BuildFailedException(
                    LogPrefix
                    + "Particle用PrefabにはAudioSourceを含めないでください: "
                    + DescribePrefab(template));
            }
        }

        private static ParticleSystem[] CreateParticleSources(
            ParticleSystem template, Transform parent, int capacity)
        {
            var systems = new ParticleSystem[capacity];

            // アクティブなPrefabをPlay中に複製しても、設定前に
            // playOnAwakeが発火しないよう非アクティブな親の下で生成する。
            var staging = new GameObject("__ParticlePool_SourceStaging");
            staging.SetActive(false);
            staging.transform.SetParent(parent, false);

            try
            {
                for (int i = 0; i < capacity; i++)
                {
                    GameObject instance = Object.Instantiate(
                        template.gameObject, staging.transform, false);

                    instance.name = "ParticleSystem_" + i;
                    instance.SetActive(false);

                    ParticleSystem system = instance.GetComponent<ParticleSystem>();

                    foreach (ParticleSystem child
                        in instance.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        var main = child.main;
                        main.playOnAwake = false;

                        // 素材自身によるPool枠の破棄・無効化を防ぐ。
                        main.stopAction = ParticleSystemStopAction.None;

                        child.Stop(
                            true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    }

                    // Prefabのローカル位置・回転・スケールを維持する。
                    instance.transform.SetParent(parent, false);
                    systems[i] = system;
                    instance.SetActive(true);
                }
            }
            finally
            {
                Object.DestroyImmediate(staging);
            }

            return systems;
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
                    ParticlePool particle = proxy as ParticlePool;
                    if (particle != null)
                    {
                        backing.SetProgramVariable(
                            nameof(ParticlePool.particleSystems),
                            particle.particleSystems);

                        backing.SetProgramVariable(
                            nameof(ParticlePool.sharedPool),
                            particle.sharedPool == null
                                ? null
                                : UdonSharpEditorUtility.GetBackingUdonBehaviour(
                                    particle.sharedPool));
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

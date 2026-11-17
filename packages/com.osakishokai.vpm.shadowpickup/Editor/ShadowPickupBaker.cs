#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;
using com.osakishokai.vpm.shadowpickup;
using Object = UnityEngine.Object;

namespace com.osakishokai.vpm.shadowpickup.editor
{
    public enum ShadowPickupBakeMode
    {
        Build,
        Play,
        Manual
    }

    /// <summary>
    /// Core editor baker shared by Build, Play and explicit Manual Baked Scene
    /// generation. The source scene is an authoring format; generated runtime
    /// objects are disposable and are rebuilt from source data.
    /// </summary>
    public static class ShadowPickupBaker
    {
        public const string GeneratedRootName = "__ShadowPickup_Generated";
        // Do NOT put ObjectSync pickups below VRC_SceneDescriptor.RespawnHeightY:
        // VRChat treats that as falling out of the world. Keep the pool just above
        // the respawn threshold and isolate it horizontally instead.
        private const float PoolRespawnHeightMargin = 10f;
        private const float PoolHorizontalOffset = 10000f;
        private const float SceneHorizontalStride = 1000f;
        private const float GroupSpacing = 64f;
        private const float SlotSpacing = 10.0f;
        private const int GroupColumns = 16;

        // PrefabUtility.IsPartOfPrefabInstance has been confirmed to return
        // false for genuinely-instanced GameObjects by the time
        // IProcessSceneWithReport.OnProcessScene runs during the real VRChat
        // AssetBundle build's scene export (GetPrefabInstanceStatus reports
        // NotAPrefab there for an object that was Connected moments earlier in
        // the same Editor session). Manual bake does not go through
        // BuildAssetBundles. Play mode has a separate two-phase cache: prefab
        // identity is captured during ExitingEditMode without mutating the Scene,
        // persisted through SessionState, then consumed after EnteredPlayMode.
        //
        // ShadowPickupSceneStep.PrepareBuild (called from the launcher's IVRCSDKBuildRequestedCallback) resolves
        // and caches each marker's prefab identity here while metadata is
        // still live, before the build's export/flatten step; CollectGroups
        // falls back to this cache only when the live query has already
        // failed. Keyed by hierarchy path: nothing else touches the scene
        // between OnBuildRequested and OnProcessScene during a build (unlike
        // switching into Play mode, where ClientSim/VRC SDK setup can insert a
        // scene root object ahead of the marker and shift its path -- ruled
        // out an identity-based key here after finding that GlobalObjectId
        // computed on a GameObject Unity no longer recognizes as prefab-linked
        // does NOT match the GlobalObjectId computed on the same object while
        // it was still recognized, so it can't bridge exactly this gap either).
        // No cross-domain-reload persistence needed: a Build doesn't trigger a
        // domain reload between OnBuildRequested and OnProcessScene the way
        // entering Play mode does.
        private static readonly Dictionary<string, string> _precachedPrefabPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        // Scene名だけの別名キーが、同名の別Sceneで別Prefabを指してしまう場合の目印(その別名キーは使わない)。
        private static readonly HashSet<string> _ambiguousBuildAliases = new HashSet<string>(StringComparer.Ordinal);

        // Play mode must not mutate the authoring Scene while Unity is still in
        // ExitingEditMode. Instead, cache prefab identity while edit-time prefab
        // metadata is trustworthy, persist it through the domain/scene reload using
        // SessionState, then consume it after EnteredPlayMode on the disposable Play
        // Scene copy. This fixes the irreversible-authoring-scene bake regression.
        private const string PlayPrefabCacheSessionKey = "ShadowPickup.PlayPrefabIdentityCache.v1";
        private static readonly Dictionary<string, string> _playCachedPrefabPaths = new Dictionary<string, string>(StringComparer.Ordinal);

        [Serializable]
        private sealed class PlayPrefabCacheData
        {
            public List<PlayPrefabCacheEntry> entries = new List<PlayPrefabCacheEntry>();
        }

        [Serializable]
        private sealed class PlayPrefabCacheEntry
        {
            public string sceneIdentity;
            public string hierarchyPath;
            public string prefabPath;
        }

        public static void PrecachePlayPrefabIdentities()
        {
            ClearOrdinalCache();
            PlayPrefabCacheData data = new PlayPrefabCacheData();

            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded) continue;

                foreach (ShadowPickup marker in FindSceneMarkers(scene))
                {
                    if (marker == null) continue;
                    GameObject go = marker.gameObject;
                    if (!PrefabUtility.IsPartOfPrefabInstance(go)) continue; // CollectGroups raises the real validation error later.
                    GameObject nearestRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
                    GameObject outermostRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
                    if (nearestRoot != go) continue; // 入れ子でも可(店舗 prefab の中の商品など)

                    string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                    if (string.IsNullOrEmpty(prefabPath)) continue;

                    data.entries.Add(new PlayPrefabCacheEntry
                    {
                        sceneIdentity = SceneIdentity(scene),
                        hierarchyPath = StableHierarchyPath(go.transform),
                        prefabPath = prefabPath
                    });
                }
            }

            ClearOrdinalCache();
            SessionState.SetString(PlayPrefabCacheSessionKey, JsonUtility.ToJson(data));
        }

        public static void RestorePlayPrefabIdentitiesFromSession()
        {
            _playCachedPrefabPaths.Clear();
            string json = SessionState.GetString(PlayPrefabCacheSessionKey, string.Empty);
            if (string.IsNullOrEmpty(json)) return;

            PlayPrefabCacheData data = JsonUtility.FromJson<PlayPrefabCacheData>(json);
            if (data == null || data.entries == null) return;

            HashSet<string> ambiguousAliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (PlayPrefabCacheEntry entry in data.entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.sceneIdentity) ||
                    string.IsNullOrEmpty(entry.hierarchyPath) || string.IsNullOrEmpty(entry.prefabPath)) continue;
                _playCachedPrefabPaths[entry.sceneIdentity + "|" + entry.hierarchyPath] = entry.prefabPath;
                // Scene.path normally survives Play mode, but some tooling can expose
                // an alternate path while keeping the Scene name. Cache the name alias
                // too so the Play transition is not coupled to that representation.
                string sceneNameAlias = Path.GetFileNameWithoutExtension(entry.sceneIdentity.Replace('\\', '/'));
                if (!string.IsNullOrEmpty(sceneNameAlias))
                {
                    // 同名の別Sceneが同じ階層パスに別Prefabを持つ場合、別名キーは曖昧なので使わない。
                    string aliasKey = sceneNameAlias + "|" + entry.hierarchyPath;
                    if (_playCachedPrefabPaths.TryGetValue(aliasKey, out string existing) && existing != entry.prefabPath)
                        ambiguousAliases.Add(aliasKey);
                    else
                        _playCachedPrefabPaths[aliasKey] = entry.prefabPath;
                }
            }

            foreach (string aliasKey in ambiguousAliases) _playCachedPrefabPaths.Remove(aliasKey);
        }

        public static void ClearPlayPrefabIdentities()
        {
            _playCachedPrefabPaths.Clear();
            SessionState.EraseString(PlayPrefabCacheSessionKey);
        }

        /// <summary>Build開始ごとに呼ぶ。前回Buildの情報を持ち越さない。</summary>
        public static void ClearBuildPrefabIdentities()
        {
            _precachedPrefabPaths.Clear();
            _ambiguousBuildAliases.Clear();
        }

        // 使い終わった(=そのSceneをベイクした)Sceneの記憶を捨てる。
        private static void RemoveBuildPrefabIdentities(Scene scene)
        {
            string identityPrefix = SceneIdentity(scene) + "/";
            string aliasPrefix = scene.name + "/";
            List<string> stale = _precachedPrefabPaths.Keys
                .Where(k => k.StartsWith(identityPrefix, StringComparison.Ordinal) || k.StartsWith(aliasPrefix, StringComparison.Ordinal)).ToList();
            foreach (string key in stale) _precachedPrefabPaths.Remove(key);
        }

        public static void PrecachePrefabIdentities(Scene scene)
        {
            foreach (ShadowPickup marker in FindSceneMarkers(scene))
            {
                if (marker == null) continue;
                GameObject go = marker.gameObject;
                if (!PrefabUtility.IsPartOfPrefabInstance(go)) continue; // let CollectGroups raise the real error later
                GameObject nearestRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
                GameObject outermostRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
                if (nearestRoot != go) continue; // ditto(入れ子は可)
                string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (string.IsNullOrEmpty(path)) continue;
                string hierarchyPath = HierarchyPath(go.transform);
                _precachedPrefabPaths[SceneIdentity(scene) + "/" + hierarchyPath] = path;

                // Scene名だけの別名キー: ビルド中にScene pathの表現が変わっても引けるようにする。
                string aliasKey = scene.name + "/" + hierarchyPath;
                if (_precachedPrefabPaths.TryGetValue(aliasKey, out string existing) && existing != path)
                    _ambiguousBuildAliases.Add(aliasKey);
                else
                    _precachedPrefabPaths[aliasKey] = path;
            }
        }

        private sealed class SourceInstance
        {
            public ShadowPickup marker;
            public string hierarchyPath;
        }

        private sealed class RenderPart
        {
            public Mesh mesh;
            public Material material;
            public int submesh;
            public Matrix4x4 relativeToPrefabRoot;
            public string debugName;
        }

        private sealed class Group
        {
            public string prefabPath;
            public string guid;
            public GameObject prefab;
            public GameObject realPrefab;
            public List<SourceInstance> sources = new List<SourceInstance>();
            public List<RenderPart> parts = new List<RenderPart>();
            public Vector3 desktopLocalBoundsCenter;
            public Vector3 desktopLocalBoundsExtents;
        }

        [MenuItem("Tools/ShadowPickup/Create Baked Scene", priority = 1200)]
        public static void CreateBakedSceneFromActiveScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("ShadowPickup", "Manual Bake is only available outside Play Mode.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Scene source = SceneManager.GetActiveScene();
            if (!source.IsValid() || string.IsNullOrEmpty(source.path))
            {
                EditorUtility.DisplayDialog("ShadowPickup", "Save the source Scene as a .unity asset before creating a Baked Scene.", "OK");
                return;
            }

            string directory = Path.GetDirectoryName(source.path).Replace('\\', '/');
            string stem = Path.GetFileNameWithoutExtension(source.path);
            if (stem.EndsWith("_ShadowBaked", StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("ShadowPickup", "Open the authoring/source Scene, not an existing _ShadowBaked Scene, before running Manual Bake.", "OK");
                return;
            }

            string bakedPath = directory + "/" + stem + "_ShadowBaked.unity";
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(bakedPath) != null)
            {
                if (!EditorUtility.DisplayDialog("ShadowPickup", "A previous Baked Scene already exists and will be replaced:\n\n" + bakedPath, "Replace", "Cancel"))
                    return;
                if (!AssetDatabase.DeleteAsset(bakedPath))
                    throw new Exception("Could not delete previous Baked Scene: " + bakedPath);
            }

            if (!AssetDatabase.CopyAsset(source.path, bakedPath))
                throw new Exception("Could not copy Scene to: " + bakedPath);

            AssetDatabase.Refresh();
            Scene baked = EditorSceneManager.OpenScene(bakedPath, OpenSceneMode.Single);
            BakeScene(baked, ShadowPickupBakeMode.Manual);
            EditorSceneManager.MarkSceneDirty(baked);
            EditorSceneManager.SaveScene(baked);
            AssetDatabase.SaveAssets();

            Debug.Log("[ShadowPickup] Created Baked Scene: " + bakedPath);
            EditorUtility.DisplayDialog("ShadowPickup", "Baked Scene created successfully:\n\n" + bakedPath + "\n\nThis is a generated inspection/debug artifact. Keep editing the original source Scene.", "OK");
        }

        [MenuItem("Tools/ShadowPickup/Validate Active Scene", priority = 1201)]
        public static void ValidateActiveSceneMenu()
        {
            Scene scene = SceneManager.GetActiveScene();
            List<Group> groups = CollectGroups(scene, true);
            List<Group> virtualGroupsList = groups.Where(g => g.sources.Count > ShadowPickupPool.Capacity).ToList();

            if (virtualGroupsList.Count > 0 && FindSceneDescriptor(scene) == null)
                throw new BuildFailedException("ShadowPickup requires a VRC Scene Descriptor when at least one group will be virtualized.");

            // Match the real Bake preconditions without mutating the Scene. Groups
            // of 8 or fewer are intentionally left as ordinary Pickups and do not
            // need DMI-compatible render parts.
            foreach (Group group in virtualGroupsList) AnalyzePrefab(group);

            int virtualGroups = virtualGroupsList.Count;
            int virtualInstances = virtualGroupsList.Sum(g => g.sources.Count);
            Debug.Log("[ShadowPickup] Validation OK. Groups=" + groups.Count + ", virtualized groups=" + virtualGroups + ", virtualized instances=" + virtualInstances);
            EditorUtility.DisplayDialog("ShadowPickup", "Validation OK.\n\nGroups: " + groups.Count + "\nVirtualized groups: " + virtualGroups + "\nVirtualized instances: " + virtualInstances, "OK");
        }

        public static void BakeScene(Scene scene, ShadowPickupBakeMode mode)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;

            List<ShadowPickup> markers = FindSceneMarkers(scene);
            GameObject existingGenerated = FindGeneratedRoot(scene);

            // A Manual Baked Scene intentionally contains generated runtime data and
            // no authoring markers. Allow it to be played/built without erasing it.
            if (markers.Count == 0)
            {
                if (existingGenerated != null)
                    Debug.Log("[ShadowPickup] Scene already contains generated data and no authoring markers; keeping it as-is: " + scene.name);
                return;
            }

            if (existingGenerated != null) Object.DestroyImmediate(existingGenerated);

            List<Group> groups = CollectGroups(scene, true);
            if (mode == ShadowPickupBakeMode.Build) RemoveBuildPrefabIdentities(scene);
            if (groups.Count == 0) return;

            VRC_SceneDescriptor sceneDescriptor = FindSceneDescriptor(scene);
            if (sceneDescriptor == null)
                throw new BuildFailedException("ShadowPickup requires a VRC Scene Descriptor.");

            // Pool Y must stay ABOVE the VRChat respawn-height boundary. Horizontal
            // separation provides the actual quarantine from the playable world.
            float poolY = sceneDescriptor.RespawnHeightY + PoolRespawnHeightMargin;

            GameObject generatedRoot = new GameObject(GeneratedRootName);
            SceneManager.MoveGameObjectToScene(generatedRoot, scene);

            List<GameObject> virtualizedSourcesToDestroy = new List<GameObject>();
            List<ShadowPickup> bypassMarkersToRemove = new List<ShadowPickup>();

            try
            {
                int groupOrdinal = 0;
                int virtualizedGroupCount = 0;
                int virtualizedInstanceCount = 0;

                foreach (Group group in groups.OrderBy(g => g.prefabPath, StringComparer.Ordinal))
                {
                    // <= Capacity: no virtualization benefit.
                    // If realPrefab is set (standard for lightweight authoring), swap
                    // in real instances so pickups retain Colliders/Rigidbody/VRCPickup.
                    // If no realPrefab, keep authored instances and remove only the marker.
                    if (group.sources.Count <= ShadowPickupPool.Capacity)
                    {
                        if (group.realPrefab != null)
                        {
                            foreach (SourceInstance s in group.sources)
                            {
                                GameObject realInst = (GameObject)PrefabUtility.InstantiatePrefab(group.realPrefab, scene);
                                realInst.transform.SetParent(s.marker.transform.parent, false);
                                realInst.transform.localPosition = s.marker.transform.localPosition;
                                realInst.transform.localRotation = s.marker.transform.localRotation;
                                realInst.transform.localScale = s.marker.transform.localScale;
                                realInst.name = s.marker.gameObject.name;
                                virtualizedSourcesToDestroy.Add(s.marker.gameObject);
                            }
                        }
                        else
                        {
                            bypassMarkersToRemove.AddRange(group.sources.Select(s => s.marker));
                        }
                        continue;
                    }

                    AnalyzePrefab(group);

                    // If a single Prefab group has more instances than one ShadowPickupPool can hold (MaxLogicalInstances = 1023),
                    // automatically partition it into clusters of up to MaxClusterSize (1000) instances each.
                    // This allows placing thousands or tens of thousands of identical items (e.g. 10k onigiri) seamlessly.
                    const int MaxClusterSize = 1000;
                    int totalSources = group.sources.Count;
                    int clusterCount = (totalSources + MaxClusterSize - 1) / MaxClusterSize;

                    for (int c = 0; c < clusterCount; c++)
                    {
                        int clusterSize = Mathf.Min(MaxClusterSize, totalSources - c * MaxClusterSize);
                        List<SourceInstance> clusterSources = group.sources.GetRange(c * MaxClusterSize, clusterSize);
                        Group subGroup = new Group
                        {
                            prefabPath = group.prefabPath,
                            guid = group.guid,
                            prefab = group.prefab,
                            realPrefab = group.realPrefab,
                            sources = clusterSources,
                            parts = group.parts,
                            desktopLocalBoundsCenter = group.desktopLocalBoundsCenter,
                            desktopLocalBoundsExtents = group.desktopLocalBoundsExtents
                        };

                        Vector3 poolBase = PoolBaseForGroup(poolY, groupOrdinal++, scene.buildIndex);
                        BuildVirtualizedGroup(scene, generatedRoot.transform, subGroup, poolBase, mode, clusterCount > 1 ? c : -1);
                        virtualizedGroupCount++;
                        virtualizedInstanceCount += clusterSize;
                    }

                    virtualizedSourcesToDestroy.AddRange(group.sources.Select(s => s.marker.gameObject));
                }

                // Commit source-scene replacement only after every generated group
                // was successfully built.
                foreach (ShadowPickup marker in bypassMarkersToRemove)
                    if (marker != null) Object.DestroyImmediate(marker);

                foreach (GameObject go in virtualizedSourcesToDestroy.Distinct())
                    if (go != null) Object.DestroyImmediate(go);

                if (generatedRoot.transform.childCount == 0)
                    Object.DestroyImmediate(generatedRoot);

                if (mode != ShadowPickupBakeMode.Play)
                    ConfigureNetworkIds(scene);

                Debug.Log("[ShadowPickup] Bake complete (" + mode + ") scene=" + scene.name +
                          " groups=" + groups.Count + " virtualizedGroups=" + virtualizedGroupCount +
                          " virtualizedInstances=" + virtualizedInstanceCount);
            }
            catch
            {
                if (generatedRoot != null) Object.DestroyImmediate(generatedRoot);
                throw;
            }
        }

        private static List<Group> CollectGroups(Scene scene, bool validate)
        {
            ClearOrdinalCache();
            try { return CollectGroupsCore(scene, validate); }
            finally { ClearOrdinalCache(); }
        }

        private static List<Group> CollectGroupsCore(Scene scene, bool validate)
        {
            List<ShadowPickup> markers = FindSceneMarkers(scene);
            Dictionary<string, Group> byGroupKey = new Dictionary<string, Group>(StringComparer.Ordinal);
            HashSet<string> validatedAuthoringPaths = new HashSet<string>(StringComparer.Ordinal);

            foreach (ShadowPickup marker in markers)
            {
                if (marker == null) continue;
                GameObject go = marker.gameObject;
                string path;
                GameObject looseTemplate = null;

                if (PrefabUtility.IsPartOfPrefabInstance(go) && PrefabUtility.GetNearestPrefabInstanceRoot(go) == go)
                {
                    // 入れ子(店舗 prefab の中に置いた商品 prefab)も可。最寄りの prefab ルートで判定する
                    path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                }
                else
                {
                    // During Play, Unity/VRC tooling can flatten prefab metadata before
                    // EnteredPlayMode. Resolve against the edit-time SessionState cache
                    // first. Its stable path ignores unrelated sibling insertions such as
                    // ClientSim helper roots. The Build path retains its existing exact
                    // hierarchy cache as a separate fallback.
                    string stablePath = StableHierarchyPath(go.transform);
                    string playKey = SceneIdentity(go.scene) + "|" + stablePath;
                    string playNameKey = go.scene.name + "|" + stablePath;
                    string buildKey = SceneIdentity(go.scene) + "/" + HierarchyPath(go.transform);
                    string buildAliasKey = go.scene.name + "/" + HierarchyPath(go.transform);
                    if (!_playCachedPrefabPaths.TryGetValue(playKey, out path) &&
                        !_playCachedPrefabPaths.TryGetValue(playNameKey, out path) &&
                        !_precachedPrefabPaths.TryGetValue(buildKey, out path) &&
                        !(!_ambiguousBuildAliases.Contains(buildAliasKey) && _precachedPrefabPaths.TryGetValue(buildAliasKey, out path)))
                    {
                        // prefab とのつながりが無い物(LightmapPrefab で複製した店舗の中の商品など)。
                        // marker 自身の realPrefab で束ね、見た目はその物の複製(シーン外)から読む
                        if (marker.realPrefab == null)
                            throw new BuildFailedException("ShadowPickup requires realPrefab: " + HierarchyPath(go.transform));
                        path = null;
                        string looseKey = "REAL:" + AssetDatabase.GetAssetPath(marker.realPrefab);
                        if (byGroupKey.TryGetValue(looseKey, out Group known)) looseTemplate = known.prefab; // 同じ品は最初の1個の複製を使い回す
                        else
                        {
                            looseTemplate = UnityEngine.Object.Instantiate(go);
                            looseTemplate.hideFlags = HideFlags.HideAndDontSave;
                            looseTemplate.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                            looseTemplate.transform.localScale = Vector3.one;
                        }
                    }
                }

                GameObject prefab = looseTemplate != null ? looseTemplate : AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    throw new BuildFailedException("Could not load ShadowPickup Prefab Asset: " + path);
                if (looseTemplate != null) path = "LOOSE:" + AssetDatabase.GetAssetPath(marker.realPrefab);

                ShadowPickup rootMarker = prefab.GetComponent<ShadowPickup>();
                GameObject realPrefab = rootMarker != null ? rootMarker.realPrefab : null;
                string realPrefabPath = realPrefab != null ? AssetDatabase.GetAssetPath(realPrefab) : null;
                string groupKey = !string.IsNullOrEmpty(realPrefabPath) ? ("REAL:" + realPrefabPath) : ("PATH:" + path);

                if (!byGroupKey.TryGetValue(groupKey, out Group group))
                {
                    group = new Group
                    {
                        prefabPath = !string.IsNullOrEmpty(realPrefabPath) ? realPrefabPath : path,
                        guid = !string.IsNullOrEmpty(realPrefabPath) ? AssetDatabase.AssetPathToGUID(realPrefabPath) : AssetDatabase.AssetPathToGUID(path),
                        prefab = prefab,
                        realPrefab = realPrefab
                    };
                    byGroupKey.Add(groupKey, group);
                }
                group.sources.Add(new SourceInstance { marker = marker, hierarchyPath = HierarchyPath(go.transform) });

                if (validate && validatedAuthoringPaths.Add(path))
                {
                    ValidateAuthoringPrefab(prefab, path);
                }
            }

            List<Group> result = byGroupKey.Values.ToList();
            foreach (Group group in result)
            {
                group.sources = group.sources
                    .OrderBy(s => Mathf.RoundToInt(s.marker.transform.position.z * 100f))
                    .ThenBy(s => Mathf.RoundToInt(s.marker.transform.position.x * 100f))
                    .ThenBy(s => Mathf.RoundToInt(s.marker.transform.position.y * 100f))
                    .ThenBy(s => s.hierarchyPath, StringComparer.Ordinal)
                    .ToList();
                if (validate) ValidateGroupBasics(group);
            }
            return result;
        }

        private static void ValidateAuthoringPrefab(GameObject prefab, string path)
        {
            ShadowPickup[] markers = prefab.GetComponentsInChildren<ShadowPickup>(true);
            if (markers.Length != 1 || markers[0].gameObject != prefab)
                throw new BuildFailedException("ShadowPickup Prefab must contain exactly one ShadowPickup marker, on the Prefab root: " + path);

            if (markers[0].realPrefab == null)
                throw new BuildFailedException("ShadowPickup requires realPrefab on the Authoring Prefab root: " + path);

            // Authoring prefabs are shadow-render-only: the AnalyzePrefab/RaycastAvailableIndex bounds
            // this bakes from come entirely from MeshRenderer/MeshFilter, and the NearestAvailableIndex/
            // RaycastAvailableIndex hover-target selection at runtime never reads a Collider (verified:
            // VR mode is pure point distance against baked positions, Desktop mode raycasts baked mesh
            // bounds, neither touches Collider). So Collider/Rigidbody/VRC_Pickup/VRC_ObjectSync/behaviour
            // scripts belong solely on ShadowPickup.realPrefab -- the authoring prefab may only carry the
            // mesh (Transform/MeshFilter/MeshRenderer) plus the ShadowPickup marker itself.
            foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true))
            {
                foreach (Component c in t.GetComponents<Component>())
                {
                    if (c is Transform || c is MeshFilter || c is MeshRenderer || c is ShadowPickup) continue;
                    throw new BuildFailedException("ShadowPickup Authoring Prefab may only have MeshFilter/MeshRenderer + the ShadowPickup marker "
                        + "(Collider/Rigidbody/Pickup/ObjectSync/behaviours belong on realPrefab instead); found "
                        + c.GetType().Name + " on " + RelativePath(t, prefab.transform) + ": " + path);
                }
            }
        }

        private static void ValidateGroupBasics(Group group)
        {
            GameObject targetPrefab = group.realPrefab != null ? group.realPrefab : group.prefab;

            VRC_Pickup pickup = targetPrefab.GetComponent<VRC_Pickup>();
            if (pickup == null)
                throw new BuildFailedException("ShadowPickup requires VRC Pickup on the Real Prefab root: " + AssetDatabase.GetAssetPath(targetPrefab));
            if (targetPrefab.GetComponent<Rigidbody>() == null)
                throw new BuildFailedException("ShadowPickup requires the ordinary VRC Pickup Rigidbody on the Real Prefab root: " + AssetDatabase.GetAssetPath(targetPrefab));
            if (targetPrefab.GetComponentsInChildren<Collider>(true).Length == 0)
                throw new BuildFailedException("ShadowPickup requires at least one Collider in the Real Pickup Prefab hierarchy: " + AssetDatabase.GetAssetPath(targetPrefab));
        }

        private static void AnalyzePrefab(Group group)
        {
            group.parts.Clear();
            Transform prefabRoot = group.prefab.transform;
            MeshRenderer[] renderers = group.prefab.GetComponentsInChildren<MeshRenderer>(true);
            bool hasDesktopBounds = false;
            Vector3 desktopMin = Vector3.zero;
            Vector3 desktopMax = Vector3.zero;

            foreach (MeshRenderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || !IsActiveInPrefab(renderer.transform, prefabRoot)) continue;

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    throw new BuildFailedException("Enabled MeshRenderer has no MeshFilter/sharedMesh in ShadowPickup Prefab: " + group.prefabPath + " / " + RelativePath(renderer.transform, prefabRoot));

                Mesh mesh = filter.sharedMesh;
                if (mesh.subMeshCount <= 0)
                    throw new BuildFailedException("Mesh has no submeshes: " + mesh.name + " in " + group.prefabPath);

                Material[] mats = renderer.sharedMaterials;
                if (mats == null || mats.Length == 0)
                    throw new BuildFailedException("Enabled MeshRenderer has no Material: " + group.prefabPath + " / " + RelativePath(renderer.transform, prefabRoot));

                Matrix4x4 relative = prefabRoot.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                EncapsulateTransformedBounds(mesh.bounds, relative, ref hasDesktopBounds, ref desktopMin, ref desktopMax);
                for (int m = 0; m < mats.Length; m++)
                {
                    Material mat = mats[m];
                    if (mat == null)
                        throw new BuildFailedException("Null Material in ShadowPickup Prefab: " + group.prefabPath + " / " + RelativePath(renderer.transform, prefabRoot));
                    if (!mat.enableInstancing)
                        throw new BuildFailedException("Material must have Enable GPU Instancing ON for ShadowPickup: " + AssetDatabase.GetAssetPath(mat) + " (Prefab: " + group.prefabPath + ")");

                    // Unity renders extra materials using the last submesh when a
                    // Renderer has more materials than submeshes. Mirror that here.
                    int submesh = Mathf.Min(m, mesh.subMeshCount - 1);
                    group.parts.Add(new RenderPart
                    {
                        mesh = mesh,
                        material = mat,
                        submesh = submesh,
                        relativeToPrefabRoot = relative,
                        debugName = RelativePath(renderer.transform, prefabRoot) + "#" + m
                    });
                }
            }

            if (group.parts.Count == 0 || !hasDesktopBounds)
                throw new BuildFailedException("ShadowPickup Prefab has no enabled MeshFilter + MeshRenderer render parts in its initial Prefab state: " + group.prefabPath);

            group.desktopLocalBoundsCenter = (desktopMin + desktopMax) * 0.5f;
            group.desktopLocalBoundsExtents = (desktopMax - desktopMin) * 0.5f;
        }

        private static void BuildVirtualizedGroup(Scene scene, Transform generatedRoot, Group group, Vector3 poolBase, ShadowPickupBakeMode mode, int clusterIndex = -1)
        {
            string shortGuid = string.IsNullOrEmpty(group.guid) ? "noguid" : group.guid.Substring(0, Mathf.Min(8, group.guid.Length));
            string clusterSuffix = clusterIndex >= 0 ? ("_c" + clusterIndex.ToString("D2")) : "";
            string groupName = group.realPrefab != null ? group.realPrefab.name : group.prefab.name;
            GameObject groupRoot = new GameObject("Group_" + Sanitize(groupName) + "_" + shortGuid + clusterSuffix);
            groupRoot.transform.SetParent(generatedRoot, false);
            // Critical for Play-mode baking: do not let dynamically added UdonSharp
            // behaviours run Start() before their baked arrays/references are assigned.
            groupRoot.SetActive(false);

            int count = group.sources.Count;
            Vector3[] positions = new Vector3[count];
            Quaternion[] rotations = new Quaternion[count];
            Vector3[] scales = new Vector3[count];
            Matrix4x4[] rootMatrices = new Matrix4x4[count];
            Vector3[] desktopBoundsCenters = new Vector3[count];
            Vector3[] desktopBoundsExtents = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                Transform t = group.sources[i].marker.transform;
                positions[i] = t.position;
                rotations[i] = t.rotation;
                scales[i] = t.lossyScale;
                rootMatrices[i] = t.localToWorldMatrix;
                BakeWorldAabb(rootMatrices[i], group.desktopLocalBoundsCenter, group.desktopLocalBoundsExtents,
                    out desktopBoundsCenters[i], out desktopBoundsExtents[i]);
            }

            ShadowPickupInstancing instancing = groupRoot.AddUdonSharpComponent<ShadowPickupInstancing>();
            instancing.meshes = group.parts.Select(p => p.mesh).ToArray();
            instancing.materials = group.parts.Select(p => p.material).ToArray();
            instancing.submeshIndices = group.parts.Select(p => p.submesh).ToArray();
            instancing.instanceCount = count;
            instancing.instancePositions = positions;
            instancing.instanceRotations = rotations;
            instancing.instanceScales = scales;
            instancing.instanceRootMatrices = rootMatrices;
            instancing.desktopBoundsCenters = desktopBoundsCenters;
            instancing.desktopBoundsExtents = desktopBoundsExtents;
            instancing.partRelativeMatrices = group.parts.Select(p => p.relativeToPrefabRoot).ToArray();
            instancing.variantIds = group.sources.Select(s => s.marker != null ? s.marker.variantID : (byte)0).ToArray();

            ShadowPickupPool pool = groupRoot.AddUdonSharpComponent<ShadowPickupPool>();
            pool.instances = instancing;

            ShadowPickupSlot[] slots = new ShadowPickupSlot[ShadowPickupPool.Capacity];
            for (int i = 0; i < slots.Length; i++)
            {
                Vector3 poolPosition = poolBase + new Vector3((i % 4) * SlotSpacing, 0f, (i / 4) * SlotSpacing);
                slots[i] = CreateRealSlot(scene, groupRoot.transform, group.realPrefab != null ? group.realPrefab : group.prefab, pool, i, poolPosition);
            }
            pool.slots = slots;

            UdonSharpEditorUtility.CopyProxyToUdon(instancing);
            UdonSharpEditorUtility.CopyProxyToUdon(pool);

            var backingInstancing = UdonSharpEditorUtility.GetBackingUdonBehaviour(instancing);
            if (backingInstancing != null && instancing.variantIds != null)
            {
                backingInstancing.SetProgramVariable(nameof(ShadowPickupInstancing.variantIds), instancing.variantIds);
            }

            if (mode == ShadowPickupBakeMode.Play)
            {
                // Arrays of generated UdonSharpBehaviour references need to be
                // pushed as backing UdonBehaviours after dynamic Play-mode creation.
                UdonSharpEditorUtility.GetBackingUdonBehaviour(pool).SetProgramVariable(
                    nameof(ShadowPickupPool.slots),
                    slots.Select(s => UdonSharpEditorUtility.GetBackingUdonBehaviour(s)).ToArray());
            }

            if (mode == ShadowPickupBakeMode.Play)
            {
                // AddUdonSharpComponent initialises only the behaviours it adds. Every other
                // UdonBehaviour that arrives with the Prefab (the product's own script, pools, ...)
                // is created through PrefabUtility.InstantiatePrefab, which the ClientSim/Editor
                // Play runtime never initialises: those scripts would silently never run.
                // Initialisation is idempotent and happens before the group is activated.
                foreach (ShadowPickupSlot slot in slots)
                    foreach (UdonBehaviour behaviour in slot.gameObject.GetComponentsInChildren<UdonBehaviour>(true))
                        behaviour.InitializeUdonContent();
            }

            // All generated data is now wired. Start/OnEnable may run from here.
            groupRoot.SetActive(true);
        }

        private static ShadowPickupSlot CreateRealSlot(Scene scene, Transform parent, GameObject prefab, ShadowPickupPool pool, int slotIndex, Vector3 poolPosition)
        {
            GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            if (go == null) throw new BuildFailedException("Failed to instantiate ShadowPickup real prefab: " + AssetDatabase.GetAssetPath(prefab));

            go.name = "Real_" + slotIndex.ToString("D2");
            go.SetActive(false);
            go.transform.SetPositionAndRotation(poolPosition, Quaternion.identity);

            ShadowPickup marker = go.GetComponent<ShadowPickup>();
            if (marker != null) Object.DestroyImmediate(marker);

            VRC_Pickup pickup = go.GetComponent<VRC_Pickup>();
            if (pickup == null) throw new BuildFailedException("Generated ShadowPickup real slot lost its root VRC Pickup: " + prefab.name);

            VRCObjectSync objectSync = go.GetComponent<VRCObjectSync>();
            if (objectSync == null) objectSync = go.AddComponent<VRCObjectSync>();
            objectSync.AllowCollisionOwnershipTransfer = false;
            objectSync.ForceKinematicOnRemote = true;

            Rigidbody body = go.GetComponent<Rigidbody>();
            bool bodyDefaultKinematic = body != null && body.isKinematic;
            bool bodyDefaultGravity = body != null && body.useGravity;
            if (body != null)
            {
                if (!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }

                body.isKinematic = true;
                body.useGravity = false;
            }

            // ShadowPickup controls when the real object is interactable. The
            // prefab's original proximity is intentionally not authoritative.
            pickup.pickupable = false;
            pickup.proximity = ShadowPickupPool.DesktopProximity;
            pickup.DisallowTheft = true;

            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            Collider[] colliders = go.GetComponentsInChildren<Collider>(true);
            bool[] rendererDefaults = renderers.Select(r => r != null && r.enabled).ToArray();
            bool[] colliderDefaults = colliders.Select(c => c != null && c.enabled).ToArray();

            ShadowPickupSlot slot = go.AddUdonSharpComponent<ShadowPickupSlot>();
            slot.pool = pool;
            slot.slot = slotIndex;
            slot.pickup = pickup;
            slot.objectSync = objectSync;
            slot.body = body;
            slot.renderers = renderers;
            slot.rendererDefaultEnabled = rendererDefaults;
            slot.colliders = colliders;
            slot.colliderDefaultEnabled = colliderDefaults;
            slot.bodyDefaultKinematic = bodyDefaultKinematic;
            slot.bodyDefaultGravity = bodyDefaultGravity;
            slot.poolPosition = poolPosition;
            slot.poolRotation = Quaternion.identity;
            slot.poolScale = go.transform.localScale;

            UdonSharpEditorUtility.CopyProxyToUdon(slot);
            go.SetActive(true);
            return slot;
        }

        private static void EncapsulateTransformedBounds(Bounds localBounds, Matrix4x4 matrix,
            ref bool hasBounds, ref Vector3 min, ref Vector3 max)
        {
            Vector3 c = localBounds.center;
            Vector3 e = localBounds.extents;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 p = matrix.MultiplyPoint3x4(c + new Vector3(e.x * sx, e.y * sy, e.z * sz));
                        if (!hasBounds)
                        {
                            min = p;
                            max = p;
                            hasBounds = true;
                        }
                        else
                        {
                            min = Vector3.Min(min, p);
                            max = Vector3.Max(max, p);
                        }
                    }
        }

        private static void BakeWorldAabb(Matrix4x4 rootMatrix, Vector3 localCenter, Vector3 localExtents,
            out Vector3 worldCenter, out Vector3 worldExtents)
        {
            bool hasBounds = false;
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;
            Bounds local = new Bounds(localCenter, localExtents * 2f);
            EncapsulateTransformedBounds(local, rootMatrix, ref hasBounds, ref min, ref max);
            worldCenter = (min + max) * 0.5f;
            worldExtents = (max - min) * 0.5f;
        }

        private static void ConfigureNetworkIds(Scene scene)
        {
            VRC_SceneDescriptor descriptor = FindSceneDescriptor(scene);
            if (descriptor == null)
                throw new BuildFailedException("ShadowPickup requires a VRC Scene Descriptor to assign Network IDs.");

            // Every bake destroys and recreates the generated Pool GameObjects (see
            // BakeScene), so the Scene Descriptor's own persisted NetworkIDCollection
            // always accumulates entries pointing at objects from the previous bake
            // that no longer exist. The SDK reports each of those as SetError.InvalidObject
            // rather than silently pruning them; ignoring that one error kind here is the
            // same pattern the VRC SDK's own ClientSim scene setup uses for the identical
            // situation (ClientSimNetworkingUtilities.DoSceneSetup).
            VRC.SDKBase.Network.NetworkIDAssignment.ConfigureNetworkIDs(descriptor, out var errors,
                VRC.SDKBase.Network.NetworkIDAssignment.SetError.InvalidObject);
            if (errors != null && errors.Count > 0)
            {
                foreach (var e in errors)
                    Debug.LogError("[ShadowPickup] NetworkID error=" + e.error
                        + " location=" + DescribeNetworkIDPair(e.location)
                        + " related=" + DescribeNetworkIDPair(e.relatedLocation));
                throw new BuildFailedException("ShadowPickup Network ID assignment failed: " + errors.Count + " error(s). Check the Console for SDK details.");
            }
        }

        // NetworkIDPair (VRC SDK) is a reference type. For an InvalidObject-class error,
        // the SDK constructs SetErrorLocation with only `location` set (see
        // ClientSimNetworkingUtilities.cs) -- `relatedLocation` is left at its default,
        // which is a genuinely null reference, not merely one whose gameObject field is
        // missing. Guard both to avoid crashing the error report itself mid-build.
        private static string DescribeNetworkIDPair(VRC.SDKBase.Network.NetworkIDPair pair)
        {
            if (pair == null || pair.gameObject == null) return "null";
            return pair.gameObject.name + "(id=" + pair.ID + ")";
        }

        private static Vector3 PoolBaseForGroup(float poolY, int ordinal, int sceneBuildIndex)
        {
            int x = ordinal % GroupColumns;
            int z = ordinal / GroupColumns;
            float sceneOffset = Mathf.Max(0, sceneBuildIndex) * SceneHorizontalStride;
            return new Vector3(PoolHorizontalOffset + sceneOffset + x * GroupSpacing, poolY, z * GroupSpacing);
        }

        private static VRC_SceneDescriptor FindSceneDescriptor(Scene scene)
        {
            return scene.GetRootGameObjects()
                .SelectMany(r => r.GetComponentsInChildren<VRC_SceneDescriptor>(true))
                .FirstOrDefault();
        }

        private static List<ShadowPickup> FindSceneMarkers(Scene scene)
        {
            List<ShadowPickup> result = new List<ShadowPickup>();
            foreach (GameObject root in scene.GetRootGameObjects())
                result.AddRange(root.GetComponentsInChildren<ShadowPickup>(true));
            return result;
        }

        private static GameObject FindGeneratedRoot(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.name == GeneratedRootName) return root;
            return null;
        }

        private static bool IsActiveInPrefab(Transform t, Transform root)
        {
            Transform current = t;
            while (current != null)
            {
                if (!current.gameObject.activeSelf) return false;
                if (current == root) break;
                current = current.parent;
            }
            return true;
        }

        private static string RelativePath(Transform t, Transform root)
        {
            if (t == root) return root.name;
            List<string> names = new List<string>();
            Transform current = t;
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        private static string SceneIdentity(Scene scene)
        {
            return !string.IsNullOrEmpty(scene.path) ? scene.path : scene.name;
        }

        // Unlike Transform.GetSiblingIndex(), this ordinal only counts siblings with
        // the same name. Unrelated helper roots/children inserted by ClientSim or the
        // VRC SDK therefore do not invalidate the Play prefab-identity cache.
        private static string StableHierarchyPath(Transform t)
        {
            List<string> names = new List<string>();
            Transform current = t;
            while (current != null)
            {
                names.Add(current.name + "[" + SameNameSiblingOrdinal(current) + "]");
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        // Ordinals are computed once per parent and cached for the duration of one pass (ClearOrdinalCache at its start and end).
        // Scanning the earlier siblings for every call is O(children) each, i.e. O(N^2) for the N products that share one parent:
        // 15,000 products under one root made entering Play take over 20 minutes (~10^8 GetChild/name calls per pass, two passes).
        private static readonly Dictionary<Transform, Dictionary<int, int>> OrdinalCache = new Dictionary<Transform, Dictionary<int, int>>();

        private static void ClearOrdinalCache() { OrdinalCache.Clear(); }

        private static int SameNameSiblingOrdinal(Transform t)
        {
            int ordinal = 0;
            if (t.parent != null)
            {
                Transform parent = t.parent;
                if (!OrdinalCache.TryGetValue(parent, out Dictionary<int, int> ordinals))
                {
                    ordinals = new Dictionary<int, int>();
                    Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int i = 0; i < parent.childCount; i++)
                    {
                        Transform child = parent.GetChild(i);
                        string childName = child.name;
                        seen.TryGetValue(childName, out int earlierSameName);
                        ordinals[child.GetInstanceID()] = earlierSameName;   // number of earlier siblings with the same name
                        seen[childName] = earlierSameName + 1;
                    }
                    OrdinalCache[parent] = ordinals;
                }
                return ordinals.TryGetValue(t.GetInstanceID(), out int cached) ? cached : 0;
            }

            Scene scene = t.gameObject.scene;
            if (!scene.IsValid()) return 0;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                Transform sibling = roots[i].transform;
                if (sibling == t) break;
                if (sibling.name == t.name) ordinal++;
            }
            return ordinal;
        }

        private static string HierarchyPath(Transform t)
        {
            List<string> names = new List<string>();
            Transform current = t;
            while (current != null)
            {
                names.Add(current.name + "[" + current.GetSiblingIndex() + "]");
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Prefab";
            char[] chars = value.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray();
            return new string(chars);
        }
    }

    [CustomEditor(typeof(ShadowPickup))]
    public sealed class ShadowPickupInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("realPrefab"), new GUIContent("Real Prefab", "Dedicated real pickup prefab instantiated for pool slots. If empty, this prefab itself is used as the real slot."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("variantID"), new GUIContent("Variant ID", "Variant / flavor ID (0..15) used for 4x4 atlas rendering."));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Attach ShadowPickup to the ROOT of a VRC Pickup prefab.\n\n" +
                "Real Prefab: Dedicated real pickup prefab instantiated for pool slots (optional).\n" +
                "Constants: Pool=8, VR activation=0.15m, VR release=0.25m, Desktop view-ray range=2m, Drop return=5s.\n\n" +
                "The same Prefab Asset must be used for every instance in a group. Scene-instance visual overrides are not represented by the Shadow state.",
                MessageType.Info);

            if (GUILayout.Button("Validate Active Scene"))
                ShadowPickupBaker.ValidateActiveSceneMenu();
            if (GUILayout.Button("Create Baked Scene Copy"))
                ShadowPickupBaker.CreateBakedSceneFromActiveScene();
        }
    }
}
#endif

// Common基盤のシーン処理ランチャー。Unityへ登録するシーン/ビルド処理はこのクラスだけ。
//
// 役割は「ICommonSceneStepの実装を自動検出し、Phase昇順に呼ぶ」ことだけ。
// 基盤固有の処理(ShadowPickupのベイク、AudioPool/ParticlePoolの集約など)は
// 各基盤フォルダのStepが持つ。新しい基盤・新世代を足してもこのファイルは変更しない。
// このファイルは公開後に変更しない。
//
// 別クラスに分けてcallbackOrderやイベント購読順に頼ると、Playで順序が不定になる
// (PlayModeStateChangedの購読者の呼び出し順は保証されない)。ランチャー1つが
// 全Stepを直列に呼ぶので、ビルドでもPlayでも順序は常に同じ。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDKBase.Editor.BuildPipeline;

namespace com.osakishokai.vpm.build
{
    [InitializeOnLoad]
    public sealed class OsakiSceneProcessor : IProcessSceneWithReport, IVRCSDKBuildRequestedCallback
    {
        private const string LogPrefix = "[common SceneProcessor] ";

        // UdonSharpの[PostProcessScene](order 0)やVRCSDKの処理より前。
        public int callbackOrder => -120;

        /// <summary>試験用: 各StepのProcessScene呼び出し直前に通知する。通常はnull。</summary>
        public static Action<ICommonSceneStep, Scene> StepObserver;

        private static List<ICommonSceneStep> steps;

        static OsakiSceneProcessor()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// 検出済みStepをPhase昇順(同Phaseは型名の辞書順)で返す。
        /// 実装は引数なしのpublicコンストラクタを持つ具象クラスに限る。
        /// </summary>
        public static IReadOnlyList<ICommonSceneStep> Steps()
        {
            if (steps == null)
            {
                var found = new List<ICommonSceneStep>();
                foreach (Type type in TypeCache.GetTypesDerivedFrom<ICommonSceneStep>())
                {
                    if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) == null)
                    {
                        continue;
                    }

                    found.Add((ICommonSceneStep)Activator.CreateInstance(type));
                }

                steps = found
                    .OrderBy(step => step.Phase)
                    .ThenBy(step => step.GetType().FullName, StringComparer.Ordinal)
                    .ToList();
            }

            return steps;
        }

        // ---------------------------------------------------------------- ビルド

        // 実際のVRChatビルドでは、Prefab編集時メタデータがまだ生きているこの時点で
        // 各基盤が必要な情報を確定して覚えておく。
        public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
        {
            foreach (ICommonSceneStep step in Steps())
            {
                step.PrepareBuild();
            }

            return true;
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // Playへ移行する途中に呼ばれるシーン処理では絶対に変更しない。
            // Play用の変更はEnteredPlayModeに限定する。
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            RunScene(scene, CommonSceneMode.Build);
        }

        /// <summary>1シーンを、全StepでPhase順に処理する(ビルド・手動・試験用)。</summary>
        public static void RunScene(Scene scene, CommonSceneMode mode)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            foreach (ICommonSceneStep step in Steps())
            {
                StepObserver?.Invoke(step, scene);
                step.ProcessScene(scene, mode);
            }
        }

        // ------------------------------------------------------------------ Play

        // Playは2相: ExitingEditModeでは編集シーンを絶対に変更せず、必要な読み取りだけを行う。
        // EnteredPlayModeで、破棄されるPlay用シーンコピーに対して全Stepを順に実行する。
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                try
                {
                    // Reload Sceneを無効にすると、Play中の変更が全て自動復元される
                    // という前提を置けない。シーンを書き換える前に中止する。
                    // Domain Reloadだけの無効化は問題ない。
                    if (EditorSettings.enterPlayModeOptionsEnabled
                        && (EditorSettings.enterPlayModeOptions
                            & EnterPlayModeOptions.DisableSceneReload) != 0)
                    {
                        throw new BuildFailedException(
                            LogPrefix
                            + "Play時の自動処理にはReload Sceneが必要です。"
                            + "Project Settings > Editor > Enter Play Mode SettingsでReload Sceneを有効にしてください。");
                    }

                    foreach (ICommonSceneStep step in Steps())
                    {
                        step.PreparePlay();
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    EndPlayAll();
                    EditorApplication.isPlaying = false;
                }

                return;
            }

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                if (!EditorApplication.isPlaying)
                {
                    return;
                }

                try
                {
                    foreach (ICommonSceneStep step in Steps())
                    {
                        step.BeginPlay();
                    }

                    int count = SceneManager.sceneCount;

                    // Phaseごとに全シーンを処理する(Phase順を全シーンで保つ)。
                    foreach (ICommonSceneStep step in Steps())
                    {
                        for (int i = 0; i < count; i++)
                        {
                            Scene scene = SceneManager.GetSceneAt(i);
                            if (!scene.IsValid() || !scene.isLoaded)
                            {
                                continue;
                            }

                            StepObserver?.Invoke(step, scene);
                            step.ProcessScene(scene, CommonSceneMode.Play);
                        }
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    // ビルドと同じ検証エラーを表示し、不完全な状態でPlayを続けない。
                    // 変更があってもReload Scene有効のPlay終了で元に戻る。
                    EditorApplication.isPlaying = false;
                }

                return;
            }

            if (state == PlayModeStateChange.EnteredEditMode)
            {
                EndPlayAll();
            }
        }

        private static void EndPlayAll()
        {
            foreach (ICommonSceneStep step in Steps())
            {
                try
                {
                    step.EndPlay();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }
    }
}
#endif

// パーティクル再生プール。全て再生中なら最古のSystemを止めて奪う。
// Sourceはビルド時／Editor Play開始後に、ParticlePoolSceneStep(OsakiSceneProcessorが呼ぶ)が自動生成する。
using UdonSharp;
using UnityEngine;

namespace com.osakishokai.vpm.particlepool
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ParticlePool : UdonSharpBehaviour
    {
        [Tooltip("同じあいことばのParticlePoolを共有します。空文字列なら独立動作します。")]
        public string mergeGroupKey = "";

        [Min(1)]
        [Tooltip("単独動作時のSystem数")]
        public int localCapacity = 2;

        [Min(1)]
        [Tooltip("複数Poolを共有したときのSystem数。配置数には比例しません。")]
        public int sharedCapacity = 4;

        [Tooltip("ParticleSystemをルートに持つ専用Prefab。子ParticleSystemを含められます。")]
        public ParticleSystem sourcePrefab;

        [HideInInspector]
        public ParticleSystem[] particleSystems;

        [HideInInspector]
        public ParticlePool sharedPool;

        private Transform[] particleTargets;
        private ParticleSystem.MinMaxGradient[] originalStartColors;
        private bool[] followTargets;
        private long[] playOrders;
        private long nextPlayOrder;

        void Start()
        {
            if (sharedPool == null)
            {
                EnsureInitialized();
            }
        }

        private void EnsureInitialized()
        {
            int count = particleSystems == null ? 0 : particleSystems.Length;

            // EditorでStart後にSource配列が設定されても、次の呼び出しで初期化する。
            if (particleTargets != null && particleTargets.Length == count)
            {
                return;
            }

            particleTargets = new Transform[count];
            originalStartColors = new ParticleSystem.MinMaxGradient[count];
            followTargets = new bool[count];
            playOrders = new long[count];

            for (int i = 0; i < count; i++)
            {
                if (particleSystems[i] == null)
                {
                    continue;
                }

                // Poolが書き換える startColor の元の値。PlayAsIs で復元する。
                originalStartColors[i] = particleSystems[i].main.startColor;

                // posAbsolute=true を World固定にするため、Pool自身の階層から切り離す
                // (Poolを動かす・回す・拡縮しても影響しない。単独Poolでも共有Poolでも同じ挙動)。
                particleSystems[i].transform.SetParent(null);
            }
        }

        // startColorを(white〜color)にして再生する。
        public void Play(Color color, Transform target, bool posAbsolute)
        {
            PlayInternal(color, true, target, posAbsolute);
        }

        // Prefab自身のstartColorをそのまま使って再生する(色を上書きしない)。
        public void PlayAsIs(Transform target, bool posAbsolute)
        {
            PlayInternal(Color.white, false, target, posAbsolute);
        }

        private void PlayInternal(Color color, bool overrideColor, Transform target, bool posAbsolute)
        {
            if (target == null)
            {
                return;
            }

            if (sharedPool != null && sharedPool != this)
            {
                sharedPool.PlayInternal(color, overrideColor, target, posAbsolute);
                return;
            }

            EnsureInitialized();

            int index = GetOneSource();
            if (index < 0)
            {
                int count = particleSystems == null ? 0 : particleSystems.Length;
                Debug.LogWarning("[ParticlePool] Play failed: no available system (systems count=" + count + ")");
                return;
            }

            ParticleSystem system = particleSystems[index];

            // 以前の粒子を確実に消してから移動する。
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            particleTargets[index] = target;
            followTargets[index] = !posAbsolute;
            system.transform.position = target.position;
            system.transform.rotation = target.rotation;

            // startColor は毎回決め直す: 色指定なら(white〜color)、PlayAsIsなら元の値へ戻す。
            // (以前のPlay(color)の色を、同じslotの次の再生へ持ち越さない)
            var main = system.main;
            if (overrideColor)
            {
                main.startColor = new ParticleSystem.MinMaxGradient(Color.white, color);
            }
            else
            {
                main.startColor = originalStartColors[index];
            }

            system.Play(true);

            nextPlayOrder++;
            playOrders[index] = nextPlayOrder;
        }

        public void StopTarget(Transform target)
        {
            if (target == null)
            {
                return;
            }

            if (sharedPool != null && sharedPool != this)
            {
                sharedPool.StopTarget(target);
                return;
            }

            EnsureInitialized();

            for (int i = 0; i < particleTargets.Length; i++)
            {
                if (particleTargets[i] != target)
                {
                    continue;
                }

                ParticleSystem system = particleSystems[i];
                if (system != null)
                {
                    system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }

                particleTargets[i] = null;
                followTargets[i] = false;
            }
        }

        /// <summary>
        /// 放出のみ停止し、既に飛んでいる粒子は寿命まで残す（自然な停止）。
        /// </summary>
        public void StopTargetEmitting(Transform target)
        {
            if (target == null)
            {
                return;
            }

            if (sharedPool != null && sharedPool != this)
            {
                sharedPool.StopTargetEmitting(target);
                return;
            }

            EnsureInitialized();

            for (int i = 0; i < particleTargets.Length; i++)
            {
                if (particleTargets[i] != target)
                {
                    continue;
                }

                ParticleSystem system = particleSystems[i];
                if (system != null)
                {
                    system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }

                particleTargets[i] = null;
                followTargets[i] = false;
            }
        }

        public void StopAll()
        {
            if (sharedPool != null && sharedPool != this)
            {
                sharedPool.StopAll();
                return;
            }

            EnsureInitialized();

            for (int i = 0; i < particleTargets.Length; i++)
            {
                ParticleSystem system = particleSystems[i];
                if (system != null)
                {
                    system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }

                particleTargets[i] = null;
                followTargets[i] = false;
            }
        }

        public bool IsPlayingTarget(Transform target)
        {
            if (target == null)
            {
                return false;
            }

            if (sharedPool != null && sharedPool != this)
            {
                return sharedPool.IsPlayingTarget(target);
            }

            EnsureInitialized();

            for (int i = 0; i < particleTargets.Length; i++)
            {
                if (particleTargets[i] == target)
                {
                    ParticleSystem system = particleSystems[i];
                    if (system != null && system.IsAlive(true))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        void LateUpdate()
        {
            if (sharedPool != null)
            {
                return;
            }

            EnsureInitialized();

            for (int i = 0; i < particleTargets.Length; i++)
            {
                ParticleSystem system = particleSystems[i];

                if (system == null || !system.IsAlive(true))
                {
                    particleTargets[i] = null;
                    followTargets[i] = false;
                    continue;
                }

                if (!followTargets[i])
                {
                    continue;
                }

                Transform target = particleTargets[i];

                if (target == null || !target.gameObject.activeInHierarchy)
                {
                    system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    particleTargets[i] = null;
                    followTargets[i] = false;
                    continue;
                }

                system.transform.position = target.position;
                system.transform.rotation = target.rotation;
            }
        }

        private int GetOneSource()
        {
            int oldestIndex = -1;
            long oldestOrder = long.MaxValue;

            for (int i = 0; i < particleTargets.Length; i++)
            {
                ParticleSystem system = particleSystems[i];
                if (system == null)
                {
                    continue;
                }

                if (!system.IsAlive(true))
                {
                    return i;
                }

                if (oldestIndex < 0 || playOrders[i] < oldestOrder)
                {
                    oldestIndex = i;
                    oldestOrder = playOrders[i];
                }
            }

            if (oldestIndex >= 0)
            {
                particleSystems[oldestIndex].Stop(
                    true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            return oldestIndex;
        }
    }
}

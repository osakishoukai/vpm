// SE再生プール。全て再生中なら最古のSourceを止めて奪う。
// Sourceはビルド時／Editor Play開始後に、AudioPoolSceneStep(OsakiSceneProcessorが呼ぶ)が自動生成する。
// 同じ空でないmergeGroupKeyのPoolが複数ある場合は共有Poolへ転送する。
using UdonSharp;
using UnityEngine;

namespace com.osakishokai.vpm.audiopool
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class AudioPool : UdonSharpBehaviour
    {
        [Tooltip("同じあいことばのAudioPoolを共有します。空文字列なら独立動作します。")]
        public string mergeGroupKey = "";

        [Min(1)]
        [Tooltip("単独動作時のSource数")]
        public int localCapacity = 2;

        [Min(1)]
        [Tooltip("複数Poolを共有したときのSource数。配置数には比例しません。")]
        public int sharedCapacity = 8;

        [Min(0.01f)]
        [Tooltip("Sourceの最小距離(この距離までは減衰しない)。共有時はあいことば内で揃えてください。")]
        public float minDistance = 1f;

        [Min(0.01f)]
        [Tooltip("Sourceの最大距離(これより遠くは聞こえない)。共有時はあいことば内で揃えてください。")]
        public float maxDistance = 40f;

        [HideInInspector]
        public AudioSource[] audioSources;

        [HideInInspector]
        public AudioPool sharedPool;

        private Transform[] audioTargets;
        private bool[] followTargets;
        private long[] playOrders;
        private long nextPlayOrder;

        // コンボ状態は共有先ではなく、呼び出し元の窓口ごとに独立して持つ。
        private float comboLastPlayTime = -100f;
        private int comboCount;

        void Start()
        {
            if (sharedPool == null)
            {
                EnsureInitialized();
            }
        }

        private void EnsureInitialized()
        {
            int count = audioSources == null ? 0 : audioSources.Length;

            // EditorでStart後にSource配列が設定されても、次の呼び出しで初期化する。
            if (audioTargets != null && audioTargets.Length == count)
            {
                return;
            }

            audioTargets = new Transform[count];
            followTargets = new bool[count];
            playOrders = new long[count];

            // posAbsolute=true を World固定にするため、Sourceを Pool自身の階層から切り離す
            // (Poolを動かす・回す・拡縮しても影響しない。単独Poolでも共有Poolでも同じ挙動)。
            for (int i = 0; i < count; i++)
            {
                if (audioSources[i] != null)
                {
                    audioSources[i].transform.SetParent(null);
                }
            }
        }

        public void Play(AudioClip clip, Transform target, bool posAbsolute)
        {
            PlayPitchedVolume(clip, target, posAbsolute, 1f, 1f);
        }

        public void PlayPitched(AudioClip clip, Transform target, bool posAbsolute, float pitch)
        {
            PlayPitchedVolume(clip, target, posAbsolute, pitch, 1f);
        }

        public void PlayVolume(AudioClip clip, Transform target, bool posAbsolute, float volume)
        {
            PlayPitchedVolume(clip, target, posAbsolute, 1f, volume);
        }

        public void PlayPitchedVolume(AudioClip clip, Transform target, bool posAbsolute, float pitch, float volume)
        {
            if (clip == null || target == null)
            {
                return;
            }

            if (sharedPool != null && sharedPool != this)
            {
                sharedPool.PlayPitchedVolume(clip, target, posAbsolute, pitch, volume);
                return;
            }

            EnsureInitialized();

            int index = GetOneSource();
            if (index < 0)
            {
                int count = audioSources == null ? 0 : audioSources.Length;
                Debug.LogWarning("[AudioPool] Play failed: no available source (sources count=" + count + ")");
                return;
            }

            AudioSource source = audioSources[index];

            audioTargets[index] = target;
            // 親子付けしない。targetが破棄されてもSource自体は失われない。
            followTargets[index] = !posAbsolute;
            source.transform.position = target.position;

            source.loop = false;
            source.volume = Mathf.Clamp01(volume);
            source.pitch = pitch;
            source.clip = clip;
            source.Play();


            nextPlayOrder++;
            playOrders[index] = nextPlayOrder;
        }

        public void PlayLoop(AudioClip clip, Transform target, bool posAbsolute)
        {
            PlayLoopPitchedVolume(clip, target, posAbsolute, 1f, 1f);
        }

        public void PlayLoopPitched(AudioClip clip, Transform target, bool posAbsolute, float pitch)
        {
            PlayLoopPitchedVolume(clip, target, posAbsolute, pitch, 1f);
        }

        public void PlayLoopVolume(AudioClip clip, Transform target, bool posAbsolute, float volume)
        {
            PlayLoopPitchedVolume(clip, target, posAbsolute, 1f, volume);
        }

        public void PlayLoopPitchedVolume(AudioClip clip, Transform target, bool posAbsolute, float pitch, float volume)
        {
            if (clip == null || target == null)
            {
                return;
            }

            if (sharedPool != null && sharedPool != this)
            {
                sharedPool.PlayLoopPitchedVolume(clip, target, posAbsolute, pitch, volume);
                return;
            }

            EnsureInitialized();

            int index = GetOneSource();
            if (index < 0)
            {
                int count = audioSources == null ? 0 : audioSources.Length;
                Debug.LogWarning("[AudioPool] PlayLoop failed: no available source (sources count=" + count + ")");
                return;
            }

            AudioSource source = audioSources[index];

            audioTargets[index] = target;
            followTargets[index] = !posAbsolute;
            source.transform.position = target.position;

            source.loop = true;
            source.volume = Mathf.Clamp01(volume);
            source.pitch = pitch;
            source.clip = clip;
            source.Play();


            nextPlayOrder++;
            playOrders[index] = nextPlayOrder;
        }

        public void PlayVaried(AudioClip clip, Transform target, bool posAbsolute, float variation)
        {
            variation = Mathf.Abs(variation);
            float pitch = 1f + UnityEngine.Random.Range(-variation, variation);
            PlayPitched(clip, target, posAbsolute, pitch);
        }

        // 前回のPlayComboからresetTime秒以上空くとコンボをリセットする。
        public void PlayCombo(
            AudioClip clip,
            Transform target,
            bool posAbsolute,
            float resetTime,
            float pitchStep,
            int maxCombo)
        {
            if (clip == null || target == null)
            {
                return;
            }

            maxCombo = Mathf.Max(0, maxCombo);

            if (Time.time - comboLastPlayTime < resetTime)
            {
                if (comboCount < maxCombo)
                {
                    comboCount++;
                }
            }
            else
            {
                comboCount = 0;
            }

            comboLastPlayTime = Time.time;

            float pitch = 1f + pitchStep * Mathf.Min(comboCount, maxCombo);
            PlayPitched(clip, target, posAbsolute, pitch);
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

            for (int i = 0; i < audioTargets.Length; i++)
            {
                if (audioTargets[i] != target)
                {
                    continue;
                }

                AudioSource source = audioSources[i];
                if (source != null)
                {
                    source.loop = false;
                    source.volume = 1f;
                    source.Stop();
                }

                audioTargets[i] = null;
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

            for (int i = 0; i < audioTargets.Length; i++)
            {
                AudioSource source = audioSources[i];
                if (source != null)
                {
                    source.loop = false;
                    source.volume = 1f;
                    source.Stop();
                }

                audioTargets[i] = null;
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

            for (int i = 0; i < audioTargets.Length; i++)
            {
                if (audioTargets[i] == target)
                {
                    AudioSource source = audioSources[i];
                    if (source != null && source.isPlaying)
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

            for (int i = 0; i < audioTargets.Length; i++)
            {
                AudioSource source = audioSources[i];

                if (source == null || !source.isPlaying)
                {
                    if (source != null)
                    {
                        source.loop = false;
                        source.volume = 1f;
                    }
                    audioTargets[i] = null;
                    followTargets[i] = false;
                    continue;
                }

                if (!followTargets[i])
                {
                    continue;
                }

                Transform target = audioTargets[i];

                if (target == null || !target.gameObject.activeInHierarchy)
                {
                    source.loop = false;
                    source.volume = 1f;
                    source.Stop();
                    audioTargets[i] = null;
                    followTargets[i] = false;
                    continue;
                }

                source.transform.position = target.position;
            }
        }

        private int GetOneSource()
        {
            int oldestIndex = -1;
            long oldestOrder = long.MaxValue;

            for (int i = 0; i < audioTargets.Length; i++)
            {
                AudioSource source = audioSources[i];
                if (source == null)
                {
                    continue;
                }

                if (!source.isPlaying)
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
                AudioSource oldest = audioSources[oldestIndex];
                if (oldest != null)
                {
                    oldest.loop = false;
                    oldest.volume = 1f;
                    oldest.Stop();
                }
            }

            return oldestIndex;
        }
    }
}

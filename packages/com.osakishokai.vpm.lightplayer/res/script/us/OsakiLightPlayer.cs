

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Components.Video;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDK3.Video.Components.Base;
using VRC.Udon.Common.Interfaces;

namespace com.osakishokai.vpm.lightplayer.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class OsakiLightPlayer : UdonSharpBehaviour
    {

        public const int StageAvpro = 0, StageUnity = 1, StageIdle = 2;

        public const int ActRetry = 0, ActNextStage = 1, ActGiveUp = 2;

        public const int StatusBgm = 0, StatusLoading = 1, StatusFailed = 2, StatusVideo = 3;

        private const float LoadGap = 5.5f;

        private const float RateLimitedWait = 6f;
        private const int MaxRateLimitedRetry = 2;

        private const int MaxErrorRetry = 1;

        private const float LoadTimeout = 15f;

        private const float FailShowSeconds = 4f;

        private const float DriftInterval = 2f;

        public const string BgmName = "LightPlayerBgm";

        [Header("再生機(同じ GameObject に置く)")]
        [SerializeField] private BaseVRCVideoPlayer avproPlayer;
        [SerializeField] private BaseVRCVideoPlayer unityPlayer;
        [Tooltip("AVPro の音(VRCAVProVideoSpeaker を付けた AudioSource)")]
        [SerializeField] private AudioSource avproSpeaker;
        [Tooltip("Unity の再生機の音(targetAudioSources に入れた AudioSource)")]
        [SerializeField] private AudioSource unitySpeaker;

        [Header("画面")]
        [SerializeField] private Renderer screen;
        [Tooltip("AVPro の絵を受ける別の Renderer(VRCAVProVideoScreen を付けた物)。空なら画面の _AvproTex に直接入る作り")]
        [SerializeField] private Renderer avproSource;
        [Tooltip("Unity の再生機の絵を受ける別の Renderer(再生機の Render Mode = Material Override の先)。空なら画面の _MainTex に直接入る作り")]
        [SerializeField] private Renderer unitySource;

        [Header("定常(曲と静止画)")]
        [Tooltip("ワールドの曲(子の LightPlayerBgm)。loop・2D・Play On Awake なしにしておく。台が何台あっても鳴るのは 1 台の曲だけ")]
        [SerializeField] private AudioSource bgm;
        [Tooltip("音量つまみに対する曲の大きさ")]
        [Range(0f, 1f)] [SerializeField] private float bgmLevel = 0.25f;
        [Tooltip("定常で画面に出す静止画")]
        [SerializeField] private Texture idleTexture;

        [Header("静止画の代わりのワールドカメラ(任意・PC だけ。Quest は静止画)")]
        [Tooltip("targetTexture に描くカメラ。定常の間・近くにいる間だけ描く")]
        [SerializeField] private Camera liveCam;
        [Tooltip("この点から liveRadius より離れたら描くのを止める。空なら画面から測る")]
        [SerializeField] private Transform liveCenter;
        [SerializeField] private float liveRadius = 8f;
        [Tooltip("何フレームに1回描くか(1 = 毎フレーム)")]
        [SerializeField] private int liveEveryFrames = 2;

        [Header("再生")]
        [Range(0f, 1f)] [SerializeField] private float defaultVolume = 0.5f;
        [Tooltip("繰り返しの最初の状態(操作の列のボタンで切り替わる)。入っていれば動画を最後まで再生したら頭から繰り返す。切ってあれば終わったら定常へ戻る")]
        [SerializeField] private bool loop = true;
        [Tooltip("この秒数よりずれたら合わせ直す")]
        [SerializeField] private float driftTolerance = 1.5f;

        [Header("部品")]
        [SerializeField] private OsakiLightPlayerUi ui;
        [SerializeField] private OsakiLightPlayerLogger logger;

        private VRCUrl url;
        private bool playing;
        private float basePos;
        private int baseMs;

        private int stage = StageIdle;
        private bool loading, loaded;
        private float duration;
        private int retry;
        private string failedUrl = "";
        private float failShowUntil;
        private float nextLoadAt;
        private bool loadQueued;
        private float timeoutAt;
        private float userVolume;
        private float nextDrift;
        private bool resendPending;

        private OsakiLightPlayer leader;
        private OsakiLightPlayer[] members;
        private int memberCount;

        private float unmuteVolume;

        private bool loopDefault;

        private MaterialPropertyBlock mpb;
        private int modeId, avproTexId, idleTexId, mainTexId, shadeId;
        private Texture avproTexSeen, unityTexSeen;

        private bool uiShown;

        private bool liveWant;
        private int liveFrame;

        private bool liveBound;

        private float duckFactor = 1f;
        private float duckUntil;

        private bool ready;

        void Start() { Init(); }

        private void Init()
        {
            if (ready) return;
            ready = true;
            url = VRCUrl.Empty;
            mpb = new MaterialPropertyBlock();
            modeId = VRCShader.PropertyToID("_Mode");
            avproTexId = VRCShader.PropertyToID("_AvproTex");
            idleTexId = VRCShader.PropertyToID("_IdleTex");
            mainTexId = VRCShader.PropertyToID("_MainTex");
            shadeId = VRCShader.PropertyToID("_UiShade");
            userVolume = defaultVolume;
            unmuteVolume = defaultVolume > 0.01f ? defaultVolume : 0.5f;
            loopDefault = loop;
            if (avproPlayer != null) avproPlayer.Loop = loop;
            if (unityPlayer != null) unityPlayer.Loop = loop;
            InitIdleImage();
            JoinGroup();
            ApplyAll();
        }

        private void JoinGroup()
        {
            GameObject g = GameObject.Find(BgmName);
            OsakiLightPlayer l = g != null ? g.GetComponentInParent<OsakiLightPlayer>() : null;
            if (l == null) l = this;
            leader = l;
            if (l == this)
            {
                members = new OsakiLightPlayer[4];
                members[0] = this;
                memberCount = 1;
                if (bgm != null && !bgm.isPlaying) bgm.Play();
                return;
            }
            if (bgm != null) { bgm.Stop(); bgm.volume = 0f; }
            l._Join(this);
            if (logger != null) logger.Log("曲は別の台が鳴らす(この台の曲は止めた)");
        }

        public void _Join(OsakiLightPlayer p)
        {
            Init();
            if (p == null || leader != this) return;
            for (int i = 0; i < memberCount; i++) if (members[i] == p) return;
            if (memberCount == members.Length)
            {
                OsakiLightPlayer[] grown = new OsakiLightPlayer[members.Length * 2];
                for (int i = 0; i < memberCount; i++) grown[i] = members[i];
                members = grown;
            }
            members[memberCount++] = p;
        }

        public static float ExpectedPos(bool isPlaying, float basePosSec, int baseServerMs, int nowServerMs, float dur)
        {
            float p = basePosSec;

            if (isPlaying) p += (nowServerMs - baseServerMs) / 1000f;
            if (dur > 0f && !float.IsInfinity(dur)) p = Mathf.Clamp(p, 0f, dur);
            return Mathf.Max(0f, p);
        }

        public static float ExpectedPosLoop(bool isPlaying, float basePosSec, int baseServerMs, int nowServerMs, float dur)
        {
            if (dur <= 1f || float.IsInfinity(dur)) return ExpectedPos(isPlaying, basePosSec, baseServerMs, nowServerMs, dur);
            float p = basePosSec;
            if (isPlaying) p += (nowServerMs - baseServerMs) / 1000f;
            p = p % dur;
            if (p < 0f) p += dur;
            return p;
        }

        public static string Clock(float sec)
        {
            if (sec < 0f || float.IsInfinity(sec) || float.IsNaN(sec)) return "--:--";
            int s = Mathf.FloorToInt(sec);
            int h = s / 3600;
            s -= h * 3600;
            int m = s / 60;
            s -= m * 60;
            string ss = (s < 10 ? "0" : "") + s.ToString();
            if (h > 0) return h.ToString() + ":" + (m < 10 ? "0" : "") + m.ToString() + ":" + ss;
            return (m < 10 ? "0" : "") + m.ToString() + ":" + ss;
        }

        public static bool HasFiniteDuration(float dur) { return dur > 1f && !float.IsInfinity(dur) && !float.IsNaN(dur); }

        public static int ErrorAction(VideoError e, int retryCount, bool wasLoaded)
        {

            if (e == VideoError.InvalidURL || e == VideoError.AccessDenied) return ActGiveUp;

            if (wasLoaded) return ActNextStage;
            if (e == VideoError.RateLimited) return retryCount < MaxRateLimitedRetry ? ActRetry : ActNextStage;
            return retryCount < MaxErrorRetry ? ActRetry : ActNextStage;
        }

        public static int NextStage(int s) { return s == StageAvpro ? StageUnity : StageIdle; }

        public static int ScreenMode(int s, bool isLoaded)
        {
            if (!isLoaded) return 0;
            if (s == StageUnity) return 1;
            if (s == StageAvpro) return 2;
            return 0;
        }

        public static float BgmVolume(bool showsVideo, float volume, float level) { return showsVideo ? 0f : volume * level; }

        public static bool LiveOn(bool idle, bool near) { return idle && near; }

        public bool _HasUrl() { return url != null && !string.IsNullOrEmpty(url.Get()); }
        public bool _IsPlaying() { return playing; }
        public float _Duration() { return duration; }
        public float _Volume() { Init(); return userVolume; }
        public bool _ShowsVideo() { return loaded && stage != StageIdle; }

        public bool _IsFailedHere() { return _HasUrl() && stage == StageIdle; }

        public float _Now()
        {
            int now = Networking.GetServerTimeInMilliseconds();
            return loop ? ExpectedPosLoop(playing, basePos, baseMs, now, duration) : ExpectedPos(playing, basePos, baseMs, now, duration);
        }

        public int _StatusCode()
        {
            if (_ShowsVideo()) return StatusVideo;
            if (loading && _HasUrl()) return StatusLoading;
            if (Time.time < failShowUntil) return StatusFailed;
            return StatusBgm;
        }

        public void _RequestPlay(VRCUrl u)
        {
            if (u == null || string.IsNullOrEmpty(u.Get())) return;
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReqPlay), u);
        }

        public void _Toggle()
        {
            Init();
            if (!_HasUrl()) return;
            if (_IsFailedHere())
            {
                if (logger != null) logger.Log("stage=idle 手元でやり直し");
                failedUrl = "";
                failShowUntil = 0f;
                StartStage(FirstStage());
                return;
            }
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReqToggle));
        }

        public void _Stop()
        {
            if (!_HasUrl()) return;
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReqStop));
        }

        public void _RequestSeek(float pos)
        {
            if (!_HasUrl() || float.IsNaN(pos) || float.IsInfinity(pos)) return;
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReqSeek), Mathf.Max(0f, pos));
        }

        public void _SetUiShown(bool on)
        {
            Init();
            if (uiShown == on) return;
            uiShown = on;
            if (logger != null) logger.Log("ui shown=" + on);
            ApplyScreen();
        }

        public void _SetVolume(float v)
        {
            Init();
            v = Mathf.Clamp01(v);
            if (v > 0.001f) unmuteVolume = v;
            SetVolumeHere(v);
        }

        public void _ToggleMute()
        {
            Init();
            if (_IsMuted()) { SetVolumeHere(unmuteVolume); return; }
            unmuteVolume = userVolume;
            SetVolumeHere(0f);
        }

        public bool _IsMuted() { return userVolume <= 0.001f; }

        public void _ToggleLoop()
        {
            Init();
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ReqLoop), !loop);
        }

        public bool _IsLoop() { return loop; }

        private void SetVolumeHere(float v)
        {
            userVolume = v;
            if (ui != null) ui._ShowVolume(v);
            ApplyVolume();
        }

        public void _Duck(float level, float seconds)
        {
            Init();
            if (leader != this) { if (leader != null) leader._Duck(level, seconds); return; }
            if (float.IsNaN(level) || float.IsInfinity(level) || float.IsNaN(seconds) || float.IsInfinity(seconds)) return;
            if (seconds <= 0f) return;
            level = Mathf.Clamp01(level);
            float now = Time.time;
            if (now >= duckUntil) duckFactor = level;
            else duckFactor = Mathf.Min(duckFactor, level);
            float until = now + seconds;
            if (until > duckUntil) duckUntil = until;
            ApplyBgm();
            SendCustomEventDelayedSeconds(nameof(_DuckCheck), seconds + 0.05f);
        }

        public void _DuckCheck()
        {
            if (leader != this) return;
            if (Time.time < duckUntil) return;
            if (duckFactor >= 1f) return;
            duckFactor = 1f;
            ApplyBgm();
        }

        public void _ApplyBgm()
        {
            Init();
            if (leader != this) { leader._ApplyBgm(); return; }
            ApplyBgm();
        }

        private void ApplyBgm()
        {
            if (bgm == null) return;
            bool anyVideo = false;
            float maxVol = 0f;
            for (int i = 0; i < memberCount; i++)
            {
                OsakiLightPlayer m = members[i];
                if (m == null) continue;
                if (m._ShowsVideo()) anyVideo = true;
                maxVol = Mathf.Max(maxVol, m._Volume());
            }
            float duck = 1f;
            if (Time.time < duckUntil) duck = duckFactor;
            else duckFactor = 1f;
            bgm.volume = BgmVolume(anyVideo, maxVol, bgmLevel) * duck;
        }

        [NetworkCallable(4)]
        public void ReqPlay(VRCUrl u)
        {
            if (!Networking.IsOwner(gameObject)) return;
            if (u == null || string.IsNullOrEmpty(u.Get())) return;
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(NetPlay), u, Networking.GetServerTimeInMilliseconds());
        }

        [NetworkCallable(4)]
        public void ReqToggle()
        {
            if (!Networking.IsOwner(gameObject)) return;
            Init();
            if (!_HasUrl()) return;
            int now = Networking.GetServerTimeInMilliseconds();
            if (playing)
            {
                SendCustomNetworkEvent(NetworkEventTarget.All, nameof(NetPause), _Now(), now);
                return;
            }
            float from = basePos;
            if (HasFiniteDuration(duration) && from >= duration - 0.5f) from = 0f;
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(NetResume), from, now);
        }

        [NetworkCallable(4)]
        public void ReqStop()
        {
            if (!Networking.IsOwner(gameObject)) return;
            Init();
            if (!_HasUrl()) return;
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(NetStop), Networking.GetServerTimeInMilliseconds());
        }

        [NetworkCallable(5)]
        public void ReqSeek(float pos)
        {
            if (!Networking.IsOwner(gameObject)) return;
            Init();
            if (!_HasUrl() || float.IsNaN(pos) || float.IsInfinity(pos)) return;
            pos = Mathf.Max(0f, pos);
            if (HasFiniteDuration(duration)) pos = Mathf.Min(pos, duration);
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(NetSeek), pos, Networking.GetServerTimeInMilliseconds());
        }

        [NetworkCallable(4)]
        public void ReqLoop(bool on)
        {
            if (!Networking.IsOwner(gameObject)) return;
            Init();

            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(NetLoop), on, _HasUrl() ? _Now() : 0f, Networking.GetServerTimeInMilliseconds());
        }

        [NetworkCallable(4)]
        public void NetLoop(bool on, float pos, int atMs)
        {
            Init();
            if (float.IsNaN(pos)) return;
            SetLoop(on);
            if (_HasUrl())
            {
                basePos = Mathf.Max(0f, pos);
                baseMs = atMs;
            }
            if (logger != null) logger.Log("net loop=" + on);
            ApplyAll();
        }

        private void SetLoop(bool on)
        {
            loop = on;
            if (avproPlayer != null) avproPlayer.Loop = on;
            if (unityPlayer != null) unityPlayer.Loop = on;
        }

        [NetworkCallable(4)]
        public void NetPlay(VRCUrl u, int atMs)
        {
            Init();
            if (u == null || string.IsNullOrEmpty(u.Get())) return;
            url = u;
            playing = true;
            basePos = 0f;
            baseMs = atMs;
            failedUrl = "";
            failShowUntil = 0f;
            if (logger != null) logger.Log("net play url_len=" + u.Get().Length);
            StartStage(FirstStage());
        }

        [NetworkCallable(4)]
        public void NetPause(float pos, int atMs)
        {
            Init();
            if (!_HasUrl() || float.IsNaN(pos)) return;
            playing = false;
            basePos = Mathf.Max(0f, pos);
            baseMs = atMs;
            BaseVRCVideoPlayer p = Active();

            if (p != null && loaded) { p.Pause(); p.SetTime(basePos); }
            if (logger != null) logger.Log("net pause pos=" + basePos.ToString("F1"));
            ApplyAll();
        }

        [NetworkCallable(4)]
        public void NetResume(float pos, int atMs)
        {
            Init();
            if (!_HasUrl() || float.IsNaN(pos)) return;
            playing = true;
            basePos = Mathf.Max(0f, pos);
            baseMs = atMs;
            if (logger != null) logger.Log("net resume pos=" + basePos.ToString("F1"));
            BaseVRCVideoPlayer p = Active();
            if (p != null && loaded)
            {
                if (HasFiniteDuration(duration)) p.SetTime(_Now());
                p.Play();
            }
            ApplyAll();
        }

        [NetworkCallable(4)]
        public void NetStop(int atMs)
        {
            Init();
            if (logger != null) logger.Log("net stop");
            StopHere();
            baseMs = atMs;
        }

        [NetworkCallable(5)]
        public void NetSeek(float pos, int atMs)
        {
            Init();
            if (!_HasUrl() || float.IsNaN(pos)) return;
            basePos = Mathf.Max(0f, pos);
            baseMs = atMs;
            BaseVRCVideoPlayer p = Active();
            if (p != null && loaded && HasFiniteDuration(duration)) p.SetTime(_Now());
            if (logger != null) logger.Log("net seek pos=" + basePos.ToString("F1"));
            ApplyAll();
        }

        [NetworkCallable(2)]
        public void NetState(VRCUrl u, bool isPlaying, float pos, int atMs, bool isLoop)
        {
            Init();
            if (Networking.IsOwner(gameObject)) return;
            SetLoop(isLoop);
            if (u == null || string.IsNullOrEmpty(u.Get()) || float.IsNaN(pos)) { ApplyAll(); return; }
            bool sameUrl = _HasUrl() && url.Get() == u.Get();
            url = u;
            playing = isPlaying;
            basePos = Mathf.Max(0f, pos);
            baseMs = atMs;
            if (sameUrl)
            {

                if (stage == StageIdle && failedUrl == u.Get()) { ApplyAll(); return; }
                if (loaded || loading)
                {
                    BaseVRCVideoPlayer p = Active();
                    if (p != null && loaded)
                    {
                        if (HasFiniteDuration(duration)) p.SetTime(_Now());
                        if (playing) p.Play(); else p.Pause();
                    }
                    ApplyAll();
                    return;
                }
            }
            if (logger != null) logger.Log("net state url_len=" + u.Get().Length + " playing=" + isPlaying);
            failedUrl = "";
            StartStage(FirstStage());
        }

        public override void OnPlayerJoined(VRCPlayerApi p)
        {
            if (!Utilities.IsValid(p) || p.isLocal) return;
            if (!Networking.IsOwner(gameObject) || resendPending) return;

            resendPending = true;
            SendCustomEventDelayedSeconds(nameof(_Resend), Random.Range(3f, 6f));
        }

        public void _Resend()
        {
            resendPending = false;
            if (!Networking.IsOwner(gameObject)) return;

            if (!_HasUrl() && loop == loopDefault) return;
            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(NetState), _HasUrl() ? url : VRCUrl.Empty, playing, _Now(), Networking.GetServerTimeInMilliseconds(), loop);
        }

        private int FirstStage()
        {
#if UNITY_EDITOR

            if (unityPlayer != null) return StageUnity;
#endif
            if (avproPlayer != null) return StageAvpro;
            if (unityPlayer != null) return StageUnity;
            return StageIdle;
        }

        private BaseVRCVideoPlayer Active()
        {
            if (stage == StageAvpro) return avproPlayer;
            if (stage == StageUnity) return unityPlayer;
            return null;
        }

        private void StartStage(int s)
        {
            stage = s;
            retry = 0;
            if (stage == StageIdle) { GiveUp(); return; }
            if (logger != null) logger.Log("stage=" + StageName(stage) + " 開始");
            LoadCurrent();
        }

        private void LoadCurrent()
        {
            loading = true;
            loaded = false;
            duration = 0f;
            avproTexSeen = null;
            unityTexSeen = null;
            StopPlayers();
            ApplyAll();
            if (loadQueued) return;
            float wait = nextLoadAt - Time.time;
            if (wait > 0f)
            {
                loadQueued = true;
                SendCustomEventDelayedSeconds(nameof(_LoadNow), wait);
                return;
            }
            DoLoad();
        }

        public void _LoadNow()
        {
            loadQueued = false;
            if (!loading || !_HasUrl() || stage == StageIdle) return;
            float wait = nextLoadAt - Time.time;
            if (wait > 0.01f)
            {
                loadQueued = true;
                SendCustomEventDelayedSeconds(nameof(_LoadNow), wait);
                return;
            }
            DoLoad();
        }

        private void DoLoad()
        {
            BaseVRCVideoPlayer p = Active();
            if (p == null) { StartStage(NextStage(stage)); return; }
            nextLoadAt = Time.time + LoadGap;
            timeoutAt = Time.time + LoadTimeout;
            p.Loop = loop;
            p.LoadURL(url);
            SendCustomEventDelayedSeconds(nameof(_LoadTimeout), LoadTimeout + 0.1f);
            if (logger != null) logger.Log("stage=" + StageName(stage) + " 読込開始 retry=" + retry);
        }

        public void _LoadTimeout()
        {
            if (!loading || loadQueued || Time.time < timeoutAt) return;
            if (logger != null) logger.LogWarning("stage=" + StageName(stage) + " 読込の時間切れ");
            HandleError(VideoError.PlayerError);
        }

        private void HandleError(VideoError e)
        {
            int act = ErrorAction(e, retry, loaded);
            if (act == ActRetry)
            {
                retry++;
                if (e == VideoError.RateLimited) nextLoadAt = Mathf.Max(nextLoadAt, Time.time + RateLimitedWait);
                LoadCurrent();
                return;
            }
            if (act == ActNextStage) { StartStage(NextStage(stage)); return; }
            GiveUp();
        }

        private void GiveUp()
        {
            if (logger != null) logger.LogWarning("stage=idle 再生をあきらめて定常へ");
            stage = StageIdle;
            loading = false;
            loaded = false;
            duration = 0f;
            failedUrl = _HasUrl() ? url.Get() : "";
            failShowUntil = Time.time + FailShowSeconds;
            StopPlayers();
            ApplyAll();

            SendCustomEventDelayedSeconds(nameof(_RefreshLater), FailShowSeconds + 0.1f);
        }

        private void StopHere()
        {
            url = VRCUrl.Empty;
            playing = false;
            basePos = 0f;
            stage = StageIdle;
            loading = false;
            loaded = false;
            duration = 0f;
            retry = 0;
            failedUrl = "";
            failShowUntil = 0f;
            StopPlayers();
            ApplyAll();
        }

        private void StopPlayers()
        {
            if (avproPlayer != null) avproPlayer.Stop();
            if (unityPlayer != null) unityPlayer.Stop();
        }

        public void _RefreshLater() { ApplyAll(); }

        private static string StageName(int s)
        {
            if (s == StageAvpro) return "avpro";
            if (s == StageUnity) return "unity";
            return "idle";
        }

        public override void OnVideoReady()
        {
            if (!loading || loadQueued) return;
            BaseVRCVideoPlayer p = Active();

            if (p == null || !p.IsReady) return;
            loading = false;
            loaded = true;
            retry = 0;
            duration = p.GetDuration();
            if (HasFiniteDuration(duration)) p.SetTime(_Now());
            if (playing) p.Play(); else p.Pause();
            nextDrift = Time.time + DriftInterval;
            if (logger != null) logger.Log("stage=" + StageName(stage) + " 読込完了 dur=" + duration.ToString("F1"));
            ApplyAll();
        }

        public override void OnVideoError(VideoError videoError)
        {

            if (stage == StageIdle || loadQueued) return;
            if (!loading && !loaded) return;
            if (logger != null) logger.LogWarning("stage=" + StageName(stage) + " error=" + videoError);
            HandleError(videoError);
        }

        public override void OnVideoEnd()
        {
            if (!loaded || loop || !playing) return;
            if (logger != null) logger.Log("stage=" + StageName(stage) + " 最後まで再生");

            if (Networking.IsOwner(gameObject)) SendCustomNetworkEvent(NetworkEventTarget.All, nameof(NetStop), Networking.GetServerTimeInMilliseconds());
            else StopHere();
        }

        private void ApplyAll()
        {
            ApplyScreen();
            ApplyVolume();
            if (ui != null) ui._Refresh();
        }

        private void ApplyVolume()
        {
            if (avproSpeaker != null) avproSpeaker.volume = userVolume;
            if (unitySpeaker != null) unitySpeaker.volume = userVolume;
            if (leader == this) ApplyBgm();
            else if (leader != null) leader._ApplyBgm();
        }

        private void ApplyScreen()
        {
            if (screen == null) return;
            screen.GetPropertyBlock(mpb);
            mpb.SetFloat(modeId, ScreenMode(stage, loaded));
            mpb.SetFloat(shadeId, uiShown ? 1f : 0f);
            screen.SetPropertyBlock(mpb);
            if (liveCam != null) liveWant = LiveOn(!_ShowsVideo(), LiveNear());
        }

        private void InitIdleImage()
        {
            if (screen == null) return;
            Texture t = idleTexture;
#if UNITY_ANDROID || UNITY_IOS
            if (liveCam != null)
            {
                liveCam.enabled = false;
                liveCam.gameObject.SetActive(false);
                liveCam = null;
            }
#else
            if (liveCam != null)
            {
                liveCam.enabled = false;
                if (liveCam.targetTexture != null) t = liveCam.targetTexture;
                SendCustomEventDelayedSeconds(nameof(_NearTick), 0.5f);
            }
#endif
            screen.GetPropertyBlock(mpb);
            if (t != null) mpb.SetTexture(idleTexId, t);
            screen.SetPropertyBlock(mpb);
        }

        private bool LiveNear()
        {
            VRCPlayerApi lp = Networking.LocalPlayer;
            if (!Utilities.IsValid(lp) || screen == null) return false;
            Vector3 d = lp.GetPosition() - (liveCenter != null ? liveCenter.position : screen.transform.position);
            return d.sqrMagnitude <= liveRadius * liveRadius;
        }

        public void _NearTick()
        {
            if (liveCam == null) return;
            SendCustomEventDelayedSeconds(nameof(_NearTick), 0.5f);
            liveWant = LiveOn(!_ShowsVideo(), LiveNear());
        }

        void Update()
        {
            if (liveCam != null)
            {
                if (liveWant)
                {

                    if (!liveBound && liveCam.enabled) BindLiveTexture();
                    liveFrame++;
                    bool draw = liveFrame >= liveEveryFrames;
                    if (draw) liveFrame = 0;
                    if (liveCam.enabled != draw) liveCam.enabled = draw;
                }
                else if (liveCam.enabled) liveCam.enabled = false;
            }
            if (!loaded) return;
            if (stage == StageAvpro && avproSource != null) CopyAvproTexture();
            if (stage == StageUnity && unitySource != null) CopyUnityTexture();
            if (!playing || Time.time < nextDrift) return;
            nextDrift = Time.time + DriftInterval;
            if (!HasFiniteDuration(duration)) return;
            BaseVRCVideoPlayer p = Active();
            if (p == null) return;
            float want = _Now();
            if (Mathf.Abs(p.GetTime() - want) > driftTolerance) p.SetTime(want);
        }

        private void BindLiveTexture()
        {
            liveBound = true;
            if (screen == null || liveCam.targetTexture == null) return;
            screen.GetPropertyBlock(mpb);
            mpb.SetTexture(idleTexId, liveCam.targetTexture);
            screen.SetPropertyBlock(mpb);
        }

        private Texture SourceTexture(Renderer src)
        {
            src.GetPropertyBlock(mpb);
            Texture t = mpb.GetTexture(mainTexId);
            if (t == null && src.sharedMaterial != null) t = src.sharedMaterial.GetTexture(mainTexId);
            return t;
        }

        private void CopyAvproTexture()
        {
            Texture t = SourceTexture(avproSource);
            if (t == null || t == avproTexSeen) return;
            avproTexSeen = t;
            screen.GetPropertyBlock(mpb);
            mpb.SetTexture(avproTexId, t);
            screen.SetPropertyBlock(mpb);
        }

        private void CopyUnityTexture()
        {
            Texture t = SourceTexture(unitySource);
            if (t == null || t == unityTexSeen) return;
            unityTexSeen = t;
            screen.GetPropertyBlock(mpb);
            mpb.SetTexture(mainTexId, t);
            screen.SetPropertyBlock(mpb);
        }

        public int TestOnly_Stage() { return stage; }
        public bool TestOnly_Loading() { return loading; }
        public bool TestOnly_Loaded() { return loaded; }
        public bool TestOnly_LoadQueued() { return loadQueued; }
        public string TestOnly_Url() { return _HasUrl() ? url.Get() : ""; }
        public string TestOnly_FailedUrl() { return failedUrl; }
        public float TestOnly_BasePos() { return basePos; }

        public float TestOnly_BgmVolume() { Init(); if (leader != this) return leader.TestOnly_BgmVolume(); return bgm != null ? bgm.volume : -1f; }
        public float TestOnly_DuckFactor() { Init(); if (leader != this) return leader.TestOnly_DuckFactor(); return Time.time < duckUntil ? duckFactor : 1f; }
        public bool TestOnly_IsBgmLeader() { Init(); return leader == this; }
        public bool TestOnly_LiveOn() { return liveCam != null && liveWant; }

        public float TestOnly_PlayerTime() { BaseVRCVideoPlayer p = Active(); return p != null && loaded ? p.GetTime() : -1f; }
        public void TestOnly_SetDuration(float d) { duration = d; }
        public void TestOnly_SetLoaded(bool v) { loaded = v; loading = false; loadQueued = false; ApplyAll(); }
    }
}

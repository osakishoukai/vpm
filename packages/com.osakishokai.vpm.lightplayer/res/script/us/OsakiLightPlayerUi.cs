

using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;

namespace com.osakishokai.vpm.lightplayer.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class OsakiLightPlayerUi : UdonSharpBehaviour
    {
        private const string TextBgm = "BGM再生中";
        private const string TextLoading = "読み込み中";
        private const string TextFailed = "再生できませんでした";

        private const float TickSeconds = 0.25f;

        private const float SeekDebounce = 0.3f;
        private const float SeekHold = 0.8f;

        [SerializeField] private OsakiLightPlayer player;

        [Header("出し入れ")]
        [Tooltip("操作の列とグラデーション。消す時は alpha 0・押せないにする")]
        [SerializeField] private CanvasGroup controls;
        [Tooltip("レイが外れてから消すまでの秒数")]
        [SerializeField] private float hideSeconds = 3f;

        [Header("操作の列(左から)")]
        [SerializeField] private VRCUrlInputField urlField;
        [SerializeField] private Button playButton;
        [SerializeField] private Image playIcon;
        [SerializeField] private Sprite playSprite;
        [SerializeField] private Sprite pauseSprite;
        [SerializeField] private Button stopButton;
        [Tooltip("繰り返しの切り替え。切ってある間はアイコンを暗くする")]
        [SerializeField] private Image loopIcon;
        [SerializeField] private Slider seek;
        [SerializeField] private TextMeshProUGUI timeText;
        [Tooltip("音量のアイコン(押すとミュート ⇔ 元の音量)。ミュートの間は薄くする")]
        [SerializeField] private Image volumeIcon;
        [SerializeField] private Slider volume;
        [SerializeField] private TextMeshProUGUI statusText;

        private const float OffAlpha = 0.4f;

        private const float LoopOffGray = 0.2f;
        private const float LoopOffAlpha = 0.75f;

        private bool shown;
        private bool rayInside;
        private float hideAt;
        private bool hideScheduled;
        private bool ticking;
        private float pendingSeek;
        private bool seekScheduled;
        private float seekHoldUntil;
        private bool ready;

        void Start() { Init(); }

        private void Init()
        {
            if (ready) return;
            ready = true;
            SetShown(false);
            if (player != null) _ShowVolume(player._Volume());
            _Refresh();
        }

        public void _OnRayEnter()
        {
            Init();
            rayInside = true;
            if (!shown) SetShown(true);
        }

        public void _OnRayExit()
        {
            Init();
            rayInside = false;
            _Touch();
        }

        public void _Touch()
        {
            hideAt = Mathf.Max(hideAt, Time.time + hideSeconds);
            if (hideScheduled || !shown) return;
            hideScheduled = true;
            SendCustomEventDelayedSeconds(nameof(_HideCheck), hideAt - Time.time + 0.05f);
        }

        public void _HideCheck()
        {
            hideScheduled = false;
            if (!shown || rayInside) return;
            float left = hideAt - Time.time;
            if (left > 0f)
            {
                hideScheduled = true;
                SendCustomEventDelayedSeconds(nameof(_HideCheck), left + 0.05f);
                return;
            }
            SetShown(false);
        }

        private void SetShown(bool on)
        {
            shown = on;
            if (controls != null)
            {
                controls.alpha = on ? 1f : 0f;
                controls.interactable = on;
                controls.blocksRaycasts = on;
            }
            if (player != null) player._SetUiShown(on);
            if (!on) return;
            _Refresh();
            if (!ticking) { ticking = true; SendCustomEventDelayedSeconds(nameof(_Tick), TickSeconds); }
        }

        public void _Tick()
        {
            if (!shown) { ticking = false; return; }
            SendCustomEventDelayedSeconds(nameof(_Tick), TickSeconds);
            RefreshTime();
        }

        public void _OnUrlEntered()
        {
            _Touch();
            if (urlField == null || player == null) return;
            VRCUrl u = urlField.GetUrl();
            if (u == null || string.IsNullOrEmpty(u.Get())) return;
            urlField.SetUrl(VRCUrl.Empty);
            player._RequestPlay(u);
        }

        public void _OnPlay()
        {
            _Touch();
            if (player != null) player._Toggle();
        }

        public void _OnStop()
        {
            _Touch();
            if (player != null) player._Stop();
        }

        public void _OnSeek()
        {
            _Touch();
            if (seek == null || player == null) return;
            float dur = player._Duration();
            if (!OsakiLightPlayer.HasFiniteDuration(dur)) return;
            pendingSeek = seek.value * dur;
            seekHoldUntil = Time.time + SeekHold;
            if (seekScheduled) return;
            seekScheduled = true;
            SendCustomEventDelayedSeconds(nameof(_CommitSeek), SeekDebounce);
        }

        public void _CommitSeek()
        {
            seekScheduled = false;
            if (player != null) player._RequestSeek(pendingSeek);
        }

        public void _OnVolume()
        {
            _Touch();
            if (volume != null && player != null) player._SetVolume(volume.value);
        }

        public void _OnMute()
        {
            _Touch();
            if (player != null) player._ToggleMute();
        }

        public void _OnLoop()
        {
            _Touch();
            if (player != null) player._ToggleLoop();
        }

        public void _ShowVolume(float v)
        {
            if (volume != null && !Mathf.Approximately(volume.value, v)) volume.SetValueWithoutNotify(v);
            if (volumeIcon != null) SetAlpha(volumeIcon, v <= 0.001f ? OffAlpha : 1f);
        }

        private void SetAlpha(Image img, float a)
        {
            Color c = img.color;
            if (Mathf.Approximately(c.a, a)) return;
            c.a = a;
            img.color = c;
        }

        public void _Refresh()
        {
            if (player == null) return;
            bool hasUrl = player._HasUrl();
            bool showPause = player._IsPlaying() && !player._IsFailedHere();
            if (playButton != null) playButton.interactable = hasUrl;
            if (stopButton != null) stopButton.interactable = hasUrl;
            if (playIcon != null)
            {
                Sprite s = showPause ? pauseSprite : playSprite;
                if (s != null && playIcon.sprite != s) playIcon.sprite = s;
            }
            if (loopIcon != null)
            {
                Color lc = player._IsLoop() ? Color.white : new Color(LoopOffGray, LoopOffGray, LoopOffGray, LoopOffAlpha);
                if (loopIcon.color != lc) loopIcon.color = lc;
            }
            if (statusText != null)
            {
                int st = player._StatusCode();
                string t = "";
                if (st == OsakiLightPlayer.StatusBgm) t = TextBgm;
                else if (st == OsakiLightPlayer.StatusLoading) t = TextLoading;
                else if (st == OsakiLightPlayer.StatusFailed) t = TextFailed;
                if (statusText.text != t) statusText.text = t;
            }
            RefreshTime();
        }

        private void RefreshTime()
        {
            if (player == null) return;
            bool video = player._ShowsVideo();
            float dur = player._Duration();
            bool finite = OsakiLightPlayer.HasFiniteDuration(dur);
            float now = player._Now();
            if (timeText != null)
            {
                string t = "";
                if (video) t = OsakiLightPlayer.Clock(now) + " / " + (finite ? OsakiLightPlayer.Clock(dur) : "LIVE");
                if (timeText.text != t) timeText.text = t;
            }
            if (seek != null)
            {
                seek.interactable = video && finite;
                if (Time.time >= seekHoldUntil) seek.SetValueWithoutNotify(video && finite ? Mathf.Clamp01(now / dur) : 0f);
            }
        }

        public bool TestOnly_Shown() { return shown; }
    }
}

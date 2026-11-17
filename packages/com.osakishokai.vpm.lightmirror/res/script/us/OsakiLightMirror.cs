

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace com.osakishokai.vpm.lightmirror.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class OsakiLightMirror : UdonSharpBehaviour
    {
        public const int ModeFull = 0, ModePlayerOnly = 1, ModeCamera = 2;

        [SerializeField] private int mode = ModeFull;
        [Tooltip("この距離(m)より離れると、鏡をやめてプローブの表示に戻る。測るのは頭から鏡の面まで")]
        [SerializeField] private float offDistance = 5f;
        [Tooltip("鏡が点いていない時に、プローブの映り込みを出す。外すと透明になる(インタラクトはそのまま効く)")]
        [SerializeField] private bool showProbe = true;
        [Tooltip("鏡の形。白=鏡・黒か透明=抜け。空なら四角")]
        [SerializeField] private Texture mask;
        [Tooltip("プレイヤーオンリーの背景の画像。鏡いっぱいに、縦横比を保って敷く")]
        [SerializeField] private Texture background;
        [Tooltip("プレイヤーオンリーで背景の画像が無い時の色")]
        [SerializeField] private Color backgroundColor = new Color(0.16f, 0.17f, 0.2f, 1f);
        [Tooltip("カメラミラーの縦の画素数。横は鏡の縦横比から決まる")]
        [SerializeField] private int cameraResolution = 1024;
        [Tooltip("カメラミラーで写す物のレイヤー。人だけにすると軽い")]
        [SerializeField] private LayerMask cameraLayers = ~((1 << 5) | (1 << 10) | (1 << 12) | (1 << 19) | (1 << 20));

        [Header("中身(prefab のまま触らない)")]
        [SerializeField] private Renderer probeSurface;
        [SerializeField] private GameObject fullMirror;
        [SerializeField] private GameObject playerMirror;
        [SerializeField] private Renderer cameraScreen;
        [SerializeField] private Camera mirrorCamera;

        private bool isOn;
        private bool prepared;
        private int checkSerial;
        private RenderTexture cameraTexture;

        void Start()
        {
            Prepare();
            Apply();
        }

        public override void Interact() { TurnOn(); }

        public void TurnOn()
        {
            if (isOn) return;
            Prepare();
            isOn = true;
            Apply();
            checkSerial++;
            SendCustomEventDelayedSeconds(nameof(_CheckDistance), 0.5f);
        }

        public void TurnOff()
        {
            if (!isOn) return;
            isOn = false;
            Apply();
        }

        public bool IsOn() { return isOn; }
        public int GetMode() { return mode; }

        public void _CheckDistance()
        {

            checkSerial--;
            if (checkSerial > 0 || !isOn) { if (checkSerial < 0) checkSerial = 0; return; }
            if (DistanceToLocalHead() > offDistance) { TurnOff(); return; }
            checkSerial++;
            SendCustomEventDelayedSeconds(nameof(_CheckDistance), 0.5f);
        }

        public float DistanceToLocalHead()
        {
            var me = Networking.LocalPlayer;
            if (!Utilities.IsValid(me)) return 0f;
            var head = me.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
            var local = transform.InverseTransformPoint(head);
            local.x = Mathf.Clamp(local.x, -0.5f, 0.5f);
            local.y = Mathf.Clamp(local.y, -0.5f, 0.5f);
            local.z = 0f;
            return Vector3.Distance(head, transform.TransformPoint(local));
        }

        private void Apply()
        {
            if (probeSurface != null) probeSurface.enabled = !isOn && showProbe;
            if (fullMirror != null) fullMirror.SetActive(isOn && mode == ModeFull);
            if (playerMirror != null) playerMirror.SetActive(isOn && mode == ModePlayerOnly);
            bool cam = isOn && mode == ModeCamera;
            if (cameraScreen != null) cameraScreen.enabled = cam;
            if (mirrorCamera != null) mirrorCamera.enabled = cam;
            DisableInteractive = isOn;
        }

        private void Prepare()
        {
            if (prepared) return;
            prepared = true;
            SetMask(probeSurface);
            if (fullMirror != null) SetMask(fullMirror.GetComponent<Renderer>());
            if (playerMirror != null)
            {
                var r = playerMirror.GetComponent<Renderer>();
                if (r != null)
                {
                    var m = r.material;
                    if (mask != null) m.SetTexture("_Mask", mask);
                    m.SetColor("_BgColor", backgroundColor);
                    if (background != null)
                    {
                        m.SetTexture("_BgTex", background);
                        m.SetFloat("_UseBgTex", 1f);
                        FitCover(m, "_BgTex", background.width, background.height);
                    }
                }
            }
            if (mode == ModeCamera) PrepareCamera();
        }

        private void SetMask(Renderer r)
        {
            if (r == null || mask == null) return;
            r.material.SetTexture("_Mask", mask);
        }

        private void PrepareCamera()
        {
            if (mirrorCamera == null || cameraScreen == null) return;
            var s = transform.lossyScale;
            float aspect = Mathf.Abs(s.y) > 0.0001f ? Mathf.Abs(s.x / s.y) : 1f;
            int h = Mathf.Clamp(cameraResolution, 64, 4096);
            int w = Mathf.Clamp(Mathf.RoundToInt(h * aspect), 64, 4096);
            cameraTexture = new RenderTexture(w, h, 24);
            cameraTexture.antiAliasing = 2;
            mirrorCamera.targetTexture = cameraTexture;
            mirrorCamera.aspect = (float)w / h;

            mirrorCamera.cullingMask = cameraLayers.value & ~(1 << 10);
            cameraScreen.material.SetTexture("_MainTex", cameraTexture);
            var m = cameraScreen.material;
            if (mask != null) m.SetTexture("_Mask", mask);
        }

        private void FitCover(Material m, string prop, int texW, int texH)
        {
            var s = transform.lossyScale;
            float mirrorAspect = Mathf.Abs(s.y) > 0.0001f ? Mathf.Abs(s.x / s.y) : 1f;
            float texAspect = texH > 0 ? (float)texW / texH : 1f;
            var scale = Vector2.one;
            if (texAspect > mirrorAspect) scale.x = mirrorAspect / texAspect;
            else scale.y = texAspect / mirrorAspect;
            m.SetTextureScale(prop, scale);
            m.SetTextureOffset(prop, (Vector2.one - scale) * 0.5f);
        }
    }
}

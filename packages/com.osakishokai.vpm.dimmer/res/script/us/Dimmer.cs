

using UdonSharp;
using UnityEngine;

namespace com.osakishokai.vpm.dimmer.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class Dimmer : UdonSharpBehaviour
    {
        public const float Min = 0f;
        public const float Max = 100f;

        [Tooltip("入室した時の明るさ(0〜100)。100 = 暗くしない")]
        [Range(0f, 100f)] public float initialBrightness = 100f;

        [Tooltip("全画面の板(子の Overlay)。触らない")]
        [SerializeField] private Renderer overlay;

        [SerializeField] private DimmerLogger logger;

        private float _brightness = 100f;
        private Material _mat;
        private bool _inited;

        public float Brightness
        {
            get { return _brightness; }
        }

        private void Start()
        {
            if (!_inited) SetBrightness(initialBrightness);
        }

        public void SetBrightness(float value)
        {
            _inited = true;
            float v = ClampBrightness(value);
            _brightness = v;
            if (overlay == null) return;
            if (v >= Max)
            {
                overlay.enabled = false;
            }
            else
            {
                if (_mat == null) _mat = overlay.material;
                _mat.SetFloat("_Brightness", v / Max);
                overlay.enabled = true;
            }
            if (logger != null) logger.Log("brightness=" + v);
        }

        public static float ClampBrightness(float value)
        {
            if (float.IsNaN(value)) return Max;
            return Mathf.Clamp(value, Min, Max);
        }
    }
}

using UdonSharp;
using UnityEngine;

namespace com.osakishokai.vpm.dimmer.us
{

    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DimmerSlider : UdonSharpBehaviour
    {
        [Tooltip("明るさを渡す先。空なら場面の \"Dimmer\" を探す")]
        public Dimmer dimmer;

        [Tooltip("値に合わせて縦に動かすつまみ(モデル)")]
        public Transform knob;

        [Tooltip("掴む所(VRC Pickup の付いた見えない箱。この物の子)")]
        public Transform grip;

        [Tooltip("つまみ(knob)の縦の位置(親の中の localPosition.y)。最小値のとき / 最大値のとき")]
        public float knobMinY = -0.108f;
        public float knobMaxY = 0.108f;

        [Tooltip("掴む所の縦の位置(この物の中)。最小値のとき / 最大値のとき。手の高さをこの範囲で値に直す")]
        public float gripMinY = -0.108f;
        public float gripMaxY = 0.108f;

        public float minValue = 10f;
        public float maxValue = 100f;

        [Tooltip("入室した時の値")]
        [Range(10f, 100f)] public float value = 100f;

        private bool _held;
        private float _gripX;
        private float _gripZ;

        private void Start()
        {
            if (dimmer == null)
            {
                GameObject go = GameObject.Find("Dimmer");
                if (go != null) dimmer = go.GetComponent<Dimmer>();
            }
            if (grip != null)
            {
                _gripX = grip.localPosition.x;
                _gripZ = grip.localPosition.z;
            }
            value = Mathf.Clamp(value, minValue, maxValue);
            MoveKnob();
            PlaceGrip();
        }

        public float Value
        {
            get { return value; }
        }

        public void SetValue(float v)
        {
            value = Mathf.Clamp(v, minValue, maxValue);
            MoveKnob();
            if (dimmer != null) dimmer.SetBrightness(value);
        }

        public void _GripStart()
        {
            _held = true;
        }

        public void _GripEnd()
        {
            _held = false;
            Drag();
            PlaceGrip();
        }

        public bool Held
        {
            get { return _held; }
        }

        private void Update()
        {
            if (!_held) return;
            Drag();
        }

        public void Drag()
        {
            if (grip == null) return;
            float y = transform.InverseTransformPoint(grip.position).y;
            float v = ValueFromY(y, gripMinY, gripMaxY, minValue, maxValue);
            if (Mathf.Abs(v - value) < 0.01f) return;
            SetValue(v);
        }

        private void MoveKnob()
        {
            if (knob == null) return;
            Vector3 p = knob.localPosition;
            p.y = KnobY(value, minValue, maxValue, knobMinY, knobMaxY);
            knob.localPosition = p;
        }

        private void PlaceGrip()
        {
            if (grip == null) return;
            grip.localPosition = new Vector3(_gripX, KnobY(value, minValue, maxValue, gripMinY, gripMaxY), _gripZ);
            grip.localRotation = Quaternion.identity;
        }

        public static float KnobY(float value, float min, float max, float yMin, float yMax)
        {
            if (max <= min) return yMin;
            float t = Mathf.Clamp01((value - min) / (max - min));
            return Mathf.Lerp(yMin, yMax, t);
        }

        public static float ValueFromY(float y, float yMin, float yMax, float vMin, float vMax)
        {
            if (yMax <= yMin) return vMin;
            float t = Mathf.Clamp01((y - yMin) / (yMax - yMin));
            return Mathf.Lerp(vMin, vMax, t);
        }
    }
}

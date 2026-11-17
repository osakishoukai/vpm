

using UdonSharp;
using UnityEngine;

namespace com.osakishokai.vpm.dimmer.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DimmerLamp : UdonSharpBehaviour
    {
        [Tooltip("明るさを渡す先。空なら場面の \"Dimmer\" を探す")]
        public Dimmer dimmer;

        [Tooltip("押すたびに順に渡す明るさ(0〜100)。最初の値が入室時")]
        public float[] steps = new float[] { 100f, 50f, 10f };

        [Tooltip("光らせるシェードの Renderer。空なら光り方は変えない")]
        public Renderer shade;

        [Tooltip("段ごとのシェードの材質(steps と同じ並び)。足りない段は替えない")]
        public Material[] shadeMaterials;

        private int _index;

        public int StepIndex
        {
            get { return _index; }
        }

        private void Start()
        {
            if (dimmer == null)
            {
                GameObject go = GameObject.Find("Dimmer");
                if (go != null) dimmer = go.GetComponent<Dimmer>();
            }
            ApplyShade();
        }

        public override void Interact()
        {
            Next();
        }

        public void Next()
        {
            if (steps == null || steps.Length == 0) return;
            _index = NextIndex(_index, steps.Length);
            if (dimmer != null) dimmer.SetBrightness(steps[_index]);
            ApplyShade();
        }

        private void ApplyShade()
        {
            if (shade == null || shadeMaterials == null) return;
            if (_index < 0 || _index >= shadeMaterials.Length) return;
            Material m = shadeMaterials[_index];
            if (m != null) shade.sharedMaterial = m;
        }

        public static int NextIndex(int index, int count)
        {
            if (count <= 0) return 0;
            int n = index + 1;
            if (n >= count || n < 0) n = 0;
            return n;
        }
    }
}

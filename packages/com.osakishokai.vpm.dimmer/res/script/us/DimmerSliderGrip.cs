using UdonSharp;

namespace com.osakishokai.vpm.dimmer.us
{

    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DimmerSliderGrip : UdonSharpBehaviour
    {
        public DimmerSlider slider;

        public override void OnPickup()
        {
            if (slider != null) slider._GripStart();
        }

        public override void OnDrop()
        {
            if (slider != null) slider._GripEnd();
        }
    }
}

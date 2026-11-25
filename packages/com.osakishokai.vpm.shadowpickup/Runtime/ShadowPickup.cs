using UnityEngine;
using VRC.SDKBase;

namespace com.osakishokai.vpm.shadowpickup
{
    /// <summary>
    /// Authoring marker. Attach this to the ROOT of a VRC Pickup prefab.
    /// The editor baker replaces large groups of identical prefab instances with
    /// GPU-instanced shadows plus a small pool of real pickup instances.
    ///
    /// Other runtime constants live in ShadowPickupPool.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ShadowPickup/Shadow Pickup")]
    public sealed class ShadowPickup : MonoBehaviour, IEditorOnly
    {
        [Tooltip("Optional: dedicated real pickup prefab instantiated for pool slots. If null, this authoring prefab itself is used as the real slot prefab.")]
        public GameObject realPrefab;

        [Tooltip("Variant / flavor ID (0..15) used for 4x4 atlas rendering and payload visual selection.")]
        [Range(0, 15)]
        public byte variantID = 0;

        [Tooltip("Seconds after a drop until the real pickup returns to its shadow. One value per group (realPrefab): the authoring prefab root's value is used.")]
        [Min(0f)]
        public float returnDelay = ShadowPickupPool.ReturnDelay;

#if UNITY_EDITOR
        private void OnValidate()
        {
            MaterialPropertyBlock mpb = new MaterialPropertyBlock();
            mpb.SetFloat("_VariantID", (float)variantID);
            var renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r != null) r.SetPropertyBlock(mpb);
            }
        }
#endif
    }
}

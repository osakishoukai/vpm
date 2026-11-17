// ワールドの空・環境光・反射とブルームを、シーンに置くだけで整える部品。
// 空・環境光はシーンが持つ情報なので prefab にすると消える。この部品が覚えておき、置かれた時(OnEnable)とビルド直前に書き戻す。
// ブルームは同じ GameObject の PostProcessVolume(全域)で掛ける。参照カメラ(VRC_SceneDescriptor.ReferenceCamera)に
// PostProcessLayer を付けるので実機でも効き、そのカメラを MainCamera にするのでシーンビューにも同じブルームが映る
// (シーンビューは MainCamera の画像効果を写す)。カメラ側の準備はエディタ専用なので OsakiWorldSettingsEditor が受け持つ。
// VRChat のクライアントではこの部品は動かない(ビルド時に外れる)。書き戻された状態がそのままワールドに入る。
// Quest はポストエフェクトが効かないのでブルームは PC だけ。
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace com.osakishokai.vpm.worldsettings
{
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(PostProcessVolume))]
    public class OsakiWorldSettings : MonoBehaviour
    {
        [Header("空・環境光・反射(applyEnvironment がオフなら触らない)")]
        public bool applyEnvironment = true;
        public Material skybox;
        public AmbientMode ambientMode = AmbientMode.Skybox;
        [ColorUsage(false, true)] public Color ambientSky = Color.black;
        [ColorUsage(false, true)] public Color ambientEquator = Color.black;
        [ColorUsage(false, true)] public Color ambientGround = Color.black;
        public float ambientIntensity = 1f;
        public Cubemap customReflection;
        public float reflectionIntensity = 1f;

        [Header("ブルーム(値はプロファイルの中。空なら共通の OsakiBloom)")]
        public PostProcessProfile profile;
        [Tooltip("音量(Volume)を置くレイヤー。参照カメラはこのレイヤーだけを見る。既定は Water(4)=どのプロジェクトにも名前がある番号")]
        public int volumeLayer = 4;

        // エディタ側(カメラの準備・既定プロファイルの割り当て)。ランタイムのアセンブリからエディタを参照しないための受け口
        public static System.Action<OsakiWorldSettings> EditorApply;

        void OnEnable() { Apply(); }

        public void Apply()
        {
            if (applyEnvironment)
            {
                if (skybox != null) RenderSettings.skybox = skybox;
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientSkyColor = ambientSky;
                RenderSettings.ambientEquatorColor = ambientEquator;
                RenderSettings.ambientGroundColor = ambientGround;
                RenderSettings.ambientIntensity = ambientIntensity;
                if (customReflection != null)
                {
                    RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                    RenderSettings.customReflection = customReflection;
                }
                RenderSettings.reflectionIntensity = reflectionIntensity;
            }

            var v = GetComponent<PostProcessVolume>();
            v.isGlobal = true;
            if (profile != null) v.sharedProfile = profile;
            if (gameObject.layer != volumeLayer) gameObject.layer = volumeLayer;

            if (!Application.isPlaying && EditorApply != null) EditorApply(this);
        }

        // 今のシーンの空・環境光・反射を覚えさせる(既存のシーンから移す時用)
        [ContextMenu("Capture Current Scene Environment")]
        public void CaptureEnvironment()
        {
            skybox = RenderSettings.skybox;
            ambientMode = RenderSettings.ambientMode;
            ambientSky = RenderSettings.ambientSkyColor;
            ambientEquator = RenderSettings.ambientEquatorColor;
            ambientGround = RenderSettings.ambientGroundColor;
            ambientIntensity = RenderSettings.ambientIntensity;
            // 反射が Skybox の時に customReflection を読むと例外になる(Unity 2022)
            customReflection = RenderSettings.defaultReflectionMode == DefaultReflectionMode.Custom ? RenderSettings.customReflection as Cubemap : null;
            reflectionIntensity = RenderSettings.reflectionIntensity;
        }
    }
}

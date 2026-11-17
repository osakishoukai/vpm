// Common基盤の「シーン処理Step」契約。
// 各基盤フォルダ(ShadowPickup / AudioPool / ParticlePool / 将来の新世代)が、自分自身を
// 処理するStepをこのインターフェースで実装する。OsakiSceneProcessor(ランチャー)は
// 実装を自動検出し、Phase昇順で呼ぶだけ。新しい基盤・新世代を足してもランチャーは変更しない。
//
// この契約(インターフェース・列挙・Phase定数)は公開後に変更しない。
#if UNITY_EDITOR
using UnityEngine.SceneManagement;

namespace com.osakishokai.vpm.build
{
    /// <summary>処理の起点。</summary>
    public enum CommonSceneMode
    {
        /// <summary>VRChatビルド中のシーン処理。</summary>
        Build = 0,
        /// <summary>手動・試験からの直接実行。</summary>
        Manual = 1,
        /// <summary>EnteredPlayMode後の、破棄されるPlay用シーンコピー。</summary>
        Play = 2,
    }

    /// <summary>
    /// 処理順(Phase)の取り決め。小さいものから先に実行する。
    /// 同じPhase同士の順序は規定しない(型名の辞書順で決定的に並ぶだけ)。
    /// </summary>
    public static class CommonPhase
    {
        /// <summary>Prefab複製・元インスタンス除去など、シーン構造を確定させる基盤(ShadowPickup系)。</summary>
        public const int ShadowPickup = 100;

        /// <summary>確定した構造を見て集約する基盤(AudioPool / ParticlePool系)。</summary>
        public const int Pool = 200;
    }

    /// <summary>
    /// 基盤ごとのシーン処理。実装は引数なしのpublicコンストラクタを持つ具象クラス。
    /// どのメソッドも「自分の対象が無いシーン」で呼ばれても何もしないこと。
    /// </summary>
    public interface ICommonSceneStep
    {
        /// <summary>実行順。CommonPhaseの値を使う。</summary>
        int Phase { get; }

        /// <summary>VRChatビルド要求時。Prefab編集時メタデータがまだ生きている時点で呼ばれる。</summary>
        void PrepareBuild();

        /// <summary>Play移行の直前(ExitingEditMode)。編集シーンを絶対に変更しないこと。</summary>
        void PreparePlay();

        /// <summary>EnteredPlayMode直後、いずれのProcessSceneよりも前に1回。</summary>
        void BeginPlay();

        /// <summary>1シーンの処理。Phase順に、全Stepが同じシーンへ順に呼ばれる。</summary>
        void ProcessScene(Scene scene, CommonSceneMode mode);

        /// <summary>Play終了後(EnteredEditMode)の後始末。</summary>
        void EndPlay();
    }
}
#endif

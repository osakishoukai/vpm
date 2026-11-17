

using UdonSharp;
using UnityEngine;
using TMPro;
using VRC.SDKBase;

namespace com.osakishokai.vpm.hellopen.us
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HelloPenButton : UdonSharpBehaviour
    {
        public HelloPenBoard board;
        public int action;
        [Tooltip("表示切り替えで入れ替える絵(0=出ている時 1=消えている時)")]
        public GameObject[] toggleIcons;

        [Header("全消しの確かめる板(action 2〜4)")]
        public GameObject confirmPanel;
        public TextMeshPro confirmText;
        [Tooltip("「消す」ボタン。マスターでない人には出さない")]
        public GameObject confirmYes;
        [Tooltip("この秒数だけ何も押さなければ板をしまう")]
        public float confirmTimeout = 8f;
        int confirmToken;

        public override void Interact() { Press(); }

        public void Press()
        {
            if (board == null) return;
            if (action == 0) board.ButtonReturnAll();
            else if (action == 1)
            {
                board.ButtonToggleVisible();
                if (toggleIcons != null && toggleIcons.Length >= 2)
                {
                    bool v = board.IsVisible();
                    if (toggleIcons[0] != null) toggleIcons[0].SetActive(v);
                    if (toggleIcons[1] != null) toggleIcons[1].SetActive(!v);
                }
            }
            else if (action == 2) OpenConfirm();
            else if (action == 3) { board.ButtonClearAll(); CloseConfirm(); }
            else if (action == 4) CloseConfirm();
        }

        void OpenConfirm()
        {
            if (confirmPanel == null) { board.ButtonClearAll(); return; }
            bool master = Networking.IsMaster;
            if (confirmText != null)
                confirmText.text = master ? "線を全部消しますか？\n<size=60%>全員の画面から消えます</size>" : "全消しは\nインスタンスマスターだけができます";
            if (confirmYes != null) confirmYes.SetActive(master);
            confirmPanel.SetActive(true);
            confirmToken++;
            SendCustomEventDelayedSeconds(nameof(_ConfirmTimeout), confirmTimeout);
        }

        void CloseConfirm()
        {
            if (confirmPanel != null) confirmPanel.SetActive(false);
        }

        int seenToken;
        public void _ConfirmTimeout()
        {

            seenToken++;
            if (seenToken == confirmToken) CloseConfirm();
        }
    }
}

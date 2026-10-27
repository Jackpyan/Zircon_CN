using Client.Controls;
using Client.UserModels;
using System;
using System.Drawing;

namespace Client.Scenes.Views
{
    /// <summary>
    /// 辅助功能主窗口（按 A 键打开）。
    /// 移植自 A端 mir3_cb\zircon-legend-client\Client\Scenes\Views\BigPatchDialog.cs。
    ///
    /// 阶段 1：仅窗口骨架 + 空 DXTabControl，不含任何标签页。
    /// 后续阶段按 辅助功能移植.md 逐个加入：常用 / 辅助 / 保护 / 聊天 / 便签 / 记录 / 拾取 / 魔法 / 帮助。
    /// </summary>
    public sealed class BigPatchDialog : DXWindow
    {
        #region Properties

        public DXTabControl TabControl;

        public override WindowType Type => WindowType.BigPatchBox;
        public override bool CustomSize => false;
        public override bool AutomaticVisibility => true;

        #endregion

        public BigPatchDialog()
        {
            TitleLabel.Text = "辅助功能";
            HasFooter = false;

            // 与 A端保持一致：客户端区域 550 x 428
            SetClientSize(new Size(550, 428));

            TabControl = new DXTabControl
            {
                Parent = this,
                Location = ClientArea.Location,
                Size = ClientArea.Size,
            };
        }

        #region IDisposable

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                if (TabControl != null)
                {
                    if (!TabControl.IsDisposed)
                        TabControl.Dispose();

                    TabControl = null;
                }
            }
        }

        #endregion
    }
}

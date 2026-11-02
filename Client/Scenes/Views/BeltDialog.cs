using Client.Controls;
using Client.Envir;
using Client.UserModels;
using Library;
using System;
using System.Drawing;
using System.Linq;

//Cleaned
namespace Client.Scenes.Views
{
    public sealed class BeltDialog : DXWindow
    {
        #region Properties

        public ClientBeltLink[] Links;

        public DXItemGrid Grid;
        public override void OnClientAreaChanged(Rectangle oValue, Rectangle nValue)
        {
            base.OnClientAreaChanged(oValue, nValue);

            if (Links == null || Grid == null) return;

            Grid?.Dispose();

            int cols = Math.Max(1, (ClientArea.Width) / (DXItemCell.CellWidth - 1));
            int rows = Math.Max(1, (ClientArea.Height) / (DXItemCell.CellHeight - 1));

            Grid = new DXItemGrid
            {
                Parent = this,
                Location = ClientArea.Location,
                GridSize = new Size(cols, rows),
                GridType = GridType.Belt,
                AllowLink = false,
            };

            for (int i = 0; i < Grid.Grid.Length; i++)
            {
                new DXLabel
                {
                    Parent = Grid.Grid[i],
                    Text = ((i + 1) % 10).ToString(),
                    Font = new Font(Config.FontName, CEnvir.FontSize(8F), FontStyle.Italic),
                    IsControl = false,
                    Location = new Point(-2, -1)
                };
            }

            Grid.BringToFront();
            Grid.Visible = true;

            // 兜底：物品快捷栏只允许横向一行；若算出多行（竖向），立刻改回横向满格
            if (rows > 1 && !_forcingHorizontal)
            {
                _forcingHorizontal = true;
                Size = GetAcceptableResize(HorizontalRequest);
                UpdateSettings();   // 顺便把新尺寸写回窗口设置，避免下次又被旧竖排尺寸覆盖
                _forcingHorizontal = false;
            }

            // 重建网格后要把当前透明度重新同步下去，否则新格子是 1.0（不透明）
            Grid.Opacity = Opacity;

            foreach (DXItemCell cell in Grid.Grid)
                cell.Opacity = Opacity;

            UpdateLinks();
        }

        private bool _forcingHorizontal;

        public override WindowType Type => WindowType.BeltBox;
        public override bool CustomSize => true;
        public override bool AutomaticVisibility => true;

        #endregion

        public BeltDialog()
        {
            HasTitle = false;
            HasFooter = false;
            HasTopBorder = false;
            TitleLabel.Visible = false;
            CloseButton.Visible = false;
            AllowResize = true;

            Links = new ClientBeltLink[Globals.MaxBeltCount];
            for (int i = 0; i < Globals.MaxBeltCount; i++)
                Links[i] = new ClientBeltLink { Slot = i };

            // 与 A 端一致：初始就撑到最大（MaxBeltCount 格一行），而不是 1 格。
            // 之前用 Size.Empty → 只算出 1×1，玩家看到的就是「只有一个格子」。
            Size = GetAcceptableResize(HorizontalRequest);

            Grid = new DXItemGrid
            {
                Parent = this,
                Location = new Point(0, 0),
                GridSize = new Size(1, 1),   // 占位，真实尺寸由下面的 OnClientAreaChanged 重建
                GridType = GridType.Belt,
                AllowLink = false,
            };

            Grid.BringToFront();
            Grid.Visible = true;

            OnClientAreaChanged(ClientArea, ClientArea);

            // 半透明：放在最后设置，确保 Grid / 格子等子控件都已创建并一起变透明
            Opacity = 0.5F;
        }

        /// <summary>把窗口透明度同步给所有子控件（否则只有背景半透明，格子仍是不透明）。</summary>
        public override void OnOpacityChanged(float oValue, float nValue)
        {
            base.OnOpacityChanged(oValue, nValue);

            foreach (DXControl control in Controls)
                control.Opacity = Opacity;
        }

        #region Methods
        public void UpdateLinks()
        {
            foreach (ClientBeltLink link in Links)
            {
                if (link.Slot < 0 || link.Slot >= Grid.Grid.Length) continue;

                if (link.LinkInfoIndex > 0)
                    Grid.Grid[link.Slot].QuickInfo = Globals.ItemInfoList.Binding.FirstOrDefault(x => x.Index == link.LinkInfoIndex);
                else if (link.LinkItemIndex > 0)
                    Grid.Grid[link.Slot].QuickItem = GameScene.Game.Inventory.FirstOrDefault(x => x?.Index == link.LinkItemIndex);
            }
        }
        public override void ApplySettings()
        {
            base.ApplySettings();

            // 旧设置可能是 1 格，或者被拉成竖向一列；
            // 物品快捷栏统一按「横向一行满格」显示（与 A 端一致）。
            int cols = Math.Max(1, ClientArea.Width / (DXItemCell.CellWidth - 1));
            int rows = Math.Max(1, ClientArea.Height / (DXItemCell.CellHeight - 1));

            if (cols * rows < 2 || rows > 1)
                Size = GetAcceptableResize(HorizontalRequest);
        }

        /// <summary>
        /// 请求一个「很宽很矮」的矩形，让 GetAcceptableResize 算出 x=MaxBeltCount、y=1（横向一行）。
        /// 不能用 10000×10000 这种正方形：客户区高度 = 10000 - 2*(6+NoHeaderSize) 比宽度略大，
        /// 会走进 area.Height > area.Width 分支被算成竖排（这就是之前一直竖向的原因）。
        /// </summary>
        private static Size HorizontalRequest => new Size(100000, DXItemCell.CellHeight * 2);

        public override Size GetAcceptableResize(Size size)
        {
            Rectangle area = GetClientArea(size);

            int x = (int)Math.Ceiling((area.Width - 10) / (float)DXItemCell.CellWidth);
            int y = (int)Math.Ceiling((area.Height - 10) / (float)DXItemCell.CellHeight);

            if (area.Height > area.Width)
                x = 0;
            else
                y = 0;

            x = Math.Max(1, Math.Min(Globals.MaxBeltCount, x)) * (DXItemCell.CellWidth - 1) + 1;
            y = Math.Max(1, Math.Min(Globals.MaxBeltCount, y)) * (DXItemCell.CellHeight - 1) + 1;

            if (x >= y)
                x += 10;
            else
                y += 10;

            return GetSize(new Size(x, y));
        }

        #endregion

        #region IDisposable

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                if (Links != null)
                {
                    for (int i = 0; i < Links.Length; i++)
                        Links[i] = null;

                    Links = null;
                }

                if (Grid != null)
                {
                    if (!Grid.IsDisposed)
                        Grid.Dispose();

                    Grid = null;
                }
            }

        }

        #endregion
    }
}
using Client.Controls;
using Client.Envir;
using Client.Models;
using Client.UserModels;
using Library;
using Library.SystemModels;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using C = Library.Network.ClientPackets;

namespace Client.Scenes.Views
{
    public sealed class InventoryDialog : DXImageControl
    {
        #region Properties

        public DXItemGrid Grid;

        public DXLabel TitleLabel, PrimaryCurrencyLabel, SecondaryCurrencyLabel, WeightLabel, WalletLabel, PrimaryCurrencyTitle, SecondaryCurrencyTitle;
        public DXButton CloseButton, SortButton, TrashButton, SellButton, RecycleButton, RecycleSetButton;

        public List<DXItemCell> SelectedItems = new();

        public List<ItemType> SellableItemTypes = new();

        #region PrimaryCurrency

        public CurrencyInfo PrimaryCurrency
        {
            get => _PrimaryCurrency;
            set
            {
                if (_PrimaryCurrency == value) return;

                CurrencyInfo oldValue = _PrimaryCurrency;
                _PrimaryCurrency = value;

                OnPrimaryCurrencyChanged(oldValue, value);
            }
        }
        private CurrencyInfo _PrimaryCurrency;

        public event EventHandler<EventArgs> PrimaryCurrencyChanged;
        public void OnPrimaryCurrencyChanged(CurrencyInfo oValue, CurrencyInfo nValue)
        {
            if (GameScene.Game.User == null)
                return;

            foreach (DXItemCell cell in Grid.Grid)
                cell.Selected = false;

            RefreshPrimaryCurrency();

            PrimaryCurrencyChanged?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #region SecondaryCurrency

        public CurrencyInfo SecondaryCurrency
        {
            get => _SecondaryCurrency;
            set
            {
                if (_SecondaryCurrency == value) return;

                CurrencyInfo oldValue = _SecondaryCurrency;
                _SecondaryCurrency = value;

                OnPrimaryCurrencyChanged(oldValue, value);
            }
        }
        private CurrencyInfo _SecondaryCurrency;

        public event EventHandler<EventArgs> SecondaryCurrencyChanged;
        public void OnSecondaryCurrencyChanged(CurrencyInfo oValue, CurrencyInfo nValue)
        {
            if (GameScene.Game.User == null)
                return;

            foreach (DXItemCell cell in Grid.Grid)
                cell.Selected = false;

            RefreshSecondaryCurrency();

            SecondaryCurrencyChanged?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        public override void OnIsVisibleChanged(bool oValue, bool nValue)
        {
            if (!IsVisible)
                Grid.ClearLinks();

            if (IsVisible)
                BringToFront();

            if (Settings != null)
                Settings.Visible = nValue;

            base.OnIsVisibleChanged(oValue, nValue);
        }

        public override void OnLocationChanged(Point oValue, Point nValue)
        {
            base.OnLocationChanged(oValue, nValue);

            if (Settings != null && IsMoving)
                Settings.Location = nValue;
        }

        public override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            switch (e.KeyCode)
            {
                case Keys.Escape:
                    if (CloseButton.Visible)
                    {
                        CloseButton.InvokeMouseClick();
                        if (!Config.EscapeCloseAll)
                            e.Handled = true;
                    }
                    break;
            }
        }

        #endregion

        #region Settings

        public WindowSetting Settings;
        public WindowType Type => WindowType.InventoryBox;

        public void LoadSettings()
        {
            if (Type == WindowType.None || !CEnvir.Loaded) return;

            Settings = CEnvir.WindowSettings.Binding.FirstOrDefault(x => x.Resolution == Config.GameSize && x.Window == Type);

            if (Settings != null)
            {
                ApplySettings();
                return;
            }

            Settings = CEnvir.WindowSettings.CreateNewObject();
            Settings.Resolution = Config.GameSize;
            Settings.Window = Type;
            Settings.Size = Size;
            Settings.Visible = Visible;
            Settings.Location = Location;
        }

        public void ApplySettings()
        {
            if (Settings == null) return;

            Location = Settings.Location;

            Visible = Settings.Visible;
        }


        #endregion

        public InventoryDialog()
        {
            LibraryFile = LibraryFile.Interface;
            Index = 130;
            Movable = true;
            Sort = true;
            DropShadow = true;

            CloseButton = new DXButton
            {
                Parent = this,
                Index = 15,
                LibraryFile = LibraryFile.Interface,
                Hint = CEnvir.Language.CommonControlClose,
                HintPosition = HintPosition.TopLeft
            };
            CloseButton.Location = new Point(DisplayArea.Width - CloseButton.Size.Width - 3, 3);
            CloseButton.MouseClick += (o, e) => Visible = false;

            TitleLabel = new DXWindowTitleLabel
            {
                Text = CEnvir.Language.InventoryDialogTitle,
                Parent = this,
            };

            // 2026-11-23：标题**靠左**，把中间到右侧的空间腾出来给按钮，
            // 原来居中时和「回收设置 / 丢弃」挤在一起（用户反馈太丑）。
            TitleLabel.AutoSize = true;
            TitleLabel.Location = new Point(DisplayArea.Location.X + 8, TitleLabel.Location.Y);

            Grid = new DXItemGrid
            {
                GridSize = new Size(6, 8),
                Parent = this,
                ItemGrid = GameScene.Game.Inventory,
                GridType = GridType.Inventory,
                Location = new Point(20, 39),
                GridPadding = 1,
                BackColour = Color.Empty,
                Border = false
            };

            foreach (DXItemCell cell in Grid.Grid)
            {
                cell.SelectedChanged += Cell_SelectedChanged;
            }

            CEnvir.LibraryList.TryGetValue(LibraryFile.GameInter, out MirLibrary library);

            DXControl WeightBar = new DXControl
            {
                Parent = this,
                Location = new Point(53, 355),
                Size = library.GetSize(360),
            };
            WeightBar.BeforeDraw += (o, e) =>
            {
                if (library == null) return;

                if (MapObject.User.Stats[Stat.BagWeight] == 0) return;

                float percent = Math.Min(1, Math.Max(0, MapObject.User.BagWeight / (float)MapObject.User.Stats[Stat.BagWeight]));

                if (percent == 0) return;

                if (!library.TryGetTexture(360, ImageType.Image, out MirImage image, out var texture, out var sourceRectangle)) return;

                PresentTexture(texture, sourceRectangle, this, new Rectangle(WeightBar.DisplayArea.X, WeightBar.DisplayArea.Y, (int)(image.Width * percent), image.Height), Color.White, WeightBar);
            };

            WeightLabel = new DXLabel
            {
                Parent = this,
                ForeColour = Color.White,
                Outline = true,
                OutlineColour = Color.Black,
                DrawFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter
            };
            WeightLabel.SizeChanged += (o, e) =>
            {
                WeightLabel.Location = new Point(WeightBar.Location.X + (WeightBar.Size.Width - WeightLabel.Size.Width) / 2, WeightBar.Location.Y - 1 + (WeightBar.Size.Height - WeightLabel.Size.Height) / 2);
            };

            PrimaryCurrencyTitle = new DXLabel
            {
                AutoSize = false,
                ForeColour = Color.Goldenrod,
                DrawFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.Left,
                Parent = this,
                Location = new Point(55, 381),
                Font = new Font(Config.FontName, CEnvir.FontSize(8F), FontStyle.Bold),
                Text = CEnvir.Language.InventoryDialogPrimaryCurrencyTitle,
                Size = new Size(97, 20)
            };

            PrimaryCurrencyLabel = new DXLabel
            {
                AutoSize = false,
                ForeColour = Color.White,
                DrawFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.Right,
                Parent = this,
                Location = new Point(80, 381),
                Text = "0",
                Size = new Size(97, 20)
            };
            PrimaryCurrencyLabel.MouseClick += PrimaryCurrencyLabel_MouseClick;

            SecondaryCurrencyTitle = new DXLabel
            {
                AutoSize = false,
                ForeColour = Color.DarkOrange,
                DrawFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.Left,
                Parent = this,
                Location = new Point(55, 400),
                Font = new Font(Config.FontName, CEnvir.FontSize(8F), FontStyle.Bold),
                Text = CEnvir.Language.InventoryDialogSecondaryCurrencyTitle,
                Size = new Size(97, 20)
            };

            SecondaryCurrencyLabel = new DXLabel
            {
                AutoSize = false,
                ForeColour = Color.White,
                DrawFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.Right,
                Parent = this,
                Location = new Point(80, 400),
                Text = "0",
                Size = new Size(97, 20)
            };
            SecondaryCurrencyLabel.MouseClick += SecondaryCurrencyLabel_MouseClick;

            SortButton = new DXButton
            {
                LibraryFile = LibraryFile.GameInter,
                Index = 364,
                Parent = this,
                Location = new Point(180, 384),
                Hint = CEnvir.Language.InventoryDialogSortButtonHint
            };
            SortButton.MouseClick += SortButton_MouseClick;

            TrashButton = new DXButton
            {
                LibraryFile = LibraryFile.GameInter,
                Index = 358,
                Parent = this,
                // 2026-11-23：**丢弃挪到标题栏**（关闭按钮左边），底部那一行让给「一键回收」。
                // 有回收之后丢弃用得少，标题栏足够放。
                Location = new Point(CloseButton.Location.X - 30, 4),
                Hint = CEnvir.Language.InventoryDialogTrashButtonHint
            };
            TrashButton.MouseClick += TrashButton_MouseClick;

            SellButton = new DXButton
            {
                LibraryFile = LibraryFile.GameInter,
                Index = 354,
                Parent = this,
                Location = new Point(218, 384),
                Hint = "全部出售",
                Enabled = true,
                Visible = false
            };
            SellButton.MouseClick += SellButton_MouseClick;

            // 一键回收（2026-11-22 回收功能）：安全区内启用，点击后服务端执行
            // 未勾选保留的可售物品一键出售（联动仓库存仓），结果通过 S.RecycleResult 回显。
            // 2026-11-23：**默认启用**。
            // 原来 Enabled = false 且只有 SafeZoneChanged 会置 true —— 玩家在城里登录时
            // 那个事件不一定触发，按钮就一直灰着，看起来像「没有这个按钮」。
            // 安全区校验在点击时已经做了（未在安全区直接提示、不发包），不必靠 Enabled 卡。
            RecycleButton = new DXButton
            {
                LibraryFile = LibraryFile.GameInter,
                // 2026-11-23：**356 是竖分隔条**（用户实测：按钮位置显示一条竖黑条），
                // 换成 354（出售图标，方形）。354 原本给 SellButton 用，但两个按钮
                // 分属不同模式（Normal 显示回收 / Sell 显示出售），不会同屏出现。
                Index = 354,
                Parent = this,
                // 2026-11-23：**占原「丢弃」的位置**（218,384）。
                // 窗口是固定尺寸的图片控件（Interface 图 130），x=256 贴右边缘会被裁掉；
                // 218 是底部一行能完整显示的最右位置，丢弃挪去标题栏后这里正好空出来。
                Location = new Point(218, 384),
                Hint = "一键回收（安全区内可用）",
                Enabled = true
            };
            RecycleButton.MouseClick += RecycleButton_MouseClick;

            // 回收设置入口（2026-11-23）：标题栏、丢弃按钮左边。
            // 用**文字按钮**而不是图标 —— 图库里没有合适的「齿轮」索引，
            // 之前给回收按钮挑图标就因为索引是分隔条踩过坑。
            RecycleSetButton = new DXButton
            {
                ButtonType = ButtonType.SmallButton,
                Parent = this,
                Size = new Size(46, SmallButtonHeight),
                Label = { Text = "回收设置" },
                Hint = "启用开关 / 保留清单 / 是否存仓库"
            };
            RecycleSetButton.Location = new Point(CloseButton.Location.X - 30 - 52, 5);
            RecycleSetButton.MouseClick += (o, e) =>
            {
                if (GameScene.Game.RecycleBox == null) return;

                GameScene.Game.RecycleBox.Refresh();
                GameScene.Game.RecycleBox.Visible = true;
            };



            WalletLabel = new DXLabel
            {
                Parent = this,
                AutoSize = false,
                Location = new Point(8, 380),
                Hint = string.Format(CEnvir.Language.InventoryDialogWalletLabelHint, CEnvir.GetKeyBindLabel(KeyBindAction.CurrencyWindow)),
                Size = new Size(45, 40),
                Sound = SoundIndex.GoldPickUp
            };
            WalletLabel.MouseClick += WalletLabel_MouseClick;
        }

        private void SortButton_MouseClick(object sender, MouseEventArgs e)
        {
            if (GameScene.Game.Observer) return;

            C.ItemSort packet = new C.ItemSort { Grid = GridType.Inventory };
            CEnvir.Enqueue(packet);
        }

        private void TrashButton_MouseClick(object sender, MouseEventArgs e)
        {
            if (GameScene.Game.Observer) return;

            var cell = DXItemCell.SelectedCell;

            if (cell == null || cell.Item == null) return;
            if ((cell.Item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) return;
            if ((cell.Item.Flags & UserItemFlags.Marriage) == UserItemFlags.Marriage) return;

            if (cell.GridType != GridType.Inventory) return;

            cell.Locked = true;

            C.ItemDelete packet = new C.ItemDelete { Grid = cell.GridType, Slot = cell.Slot };

            CEnvir.Enqueue(packet);
        }

        private void Cell_SelectedChanged(object sender, EventArgs e)
        {
            var cell = sender as DXItemCell;

            if (InvMode == InventoryMode.Sell)
            {
                if (cell.Selected)
                {
                    if (cell.Item != null && (cell.Item.Flags & UserItemFlags.Locked) != UserItemFlags.Locked)
                    {
                        if (!SellableItemTypes.Contains(cell.Item.Info.ItemType))
                        {
                            GameScene.Game.ReceiveChat(string.Format(CEnvir.Language.UnableToSellHere, cell.Item.Info.ItemName), MessageType.System);
                            cell.Selected = false;
                            return;
                        }

                        SelectedItems.Add(cell);
                    }
                }
                else
                    SelectedItems.Remove(cell);

                long sum = 0;
                int count = 0;
                decimal exchangeRate = PrimaryCurrency.ExchangeRate <= 0M ? 1M : PrimaryCurrency.ExchangeRate;

                foreach (DXItemCell itemCell in SelectedItems)
                {
                    count++;
                    sum += (long)(itemCell.Item.Price(itemCell.Item.Count) / exchangeRate);
                }

                SecondaryCurrencyLabel.Text = sum.ToString("#,##0");

                SellButton.Enabled = true;
                SellButton.Hint = count == 1 ? "出售" : "全部出售";
            }
        }

        private void SellButton_MouseClick(object sender, MouseEventArgs e)
        {
            if (GameScene.Game.Observer) return;

            var cell = DXItemCell.SelectedCell;

            if (cell != null && cell.Item != null && (cell.Item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) return;

            List<CellLinkInfo> links = new();

            if (SelectedItems.Count > 0)
            {
                foreach (DXItemCell itemCell in SelectedItems)
                {
                    if ((itemCell.Item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) continue;

                    links.Add(new CellLinkInfo { Count = itemCell.Item.Count, GridType = GridType.Inventory, Slot = itemCell.Slot });
                }
            }
            else
            {
                //Sell all
                foreach (DXItemCell itemCell in Grid.Grid)
                {
                    if (itemCell.Item == null) continue;
                    if ((itemCell.Item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) continue;

                    if (SellableItemTypes.Count > 0 && !SellableItemTypes.Contains(itemCell.Item.Info.ItemType)) continue;

                    links.Add(new CellLinkInfo { Count = itemCell.Item.Count, GridType = GridType.Inventory, Slot = itemCell.Slot });
                }
            }

            if (links.Count > 0)
            {
                CEnvir.Enqueue(new C.NPCSell { Links = links });
            }
        }

        private void RecycleButton_MouseClick(object sender, MouseEventArgs e)
        {
            if (GameScene.Game.Observer) return;

            // 安全区限定（与服务端校验双保险）：未在安全区不发包，直接提示。
            if (MapObject.User == null || !MapObject.User.InSafeZone)
            {
                GameScene.Game.ReceiveChat("未在安全区，无法使用一键回收。", MessageType.System);
                return;
            }

            // PotionLevel=0：完整客户端真人手动回收，不启用药品特殊规则，按保留清单判定。
            CEnvir.Enqueue(new C.RecycleNow { Confirm = true, PotionLevel = 0 });
        }

        private void PrimaryCurrencyLabel_MouseClick(object sender, MouseEventArgs e)
        {
            if (GameScene.Game.SelectedCell == null)
            {
                var userCurrency = GameScene.Game.User.GetCurrency(PrimaryCurrency);

                if (!userCurrency.CanPickup) return;
                DXSoundManager.Play(SoundIndex.GoldPickUp);

                if (GameScene.Game.CurrencyPickedUp == null && userCurrency.Amount > 0)
                    GameScene.Game.CurrencyPickedUp = userCurrency;
                else
                    GameScene.Game.CurrencyPickedUp = null;
            }
        }

        private void SecondaryCurrencyLabel_MouseClick(object sender, MouseEventArgs e)
        {
            if (GameScene.Game.SelectedCell == null)
            {
                var userCurrency = GameScene.Game.User.GetCurrency(SecondaryCurrency);

                if (!userCurrency.CanPickup) return;
                DXSoundManager.Play(SoundIndex.GoldPickUp);

                if (GameScene.Game.CurrencyPickedUp == null && userCurrency.Amount > 0)
                    GameScene.Game.CurrencyPickedUp = userCurrency;
                else
                    GameScene.Game.CurrencyPickedUp = null;
            }
        }

        private void WalletLabel_MouseClick(object sender, MouseEventArgs e)
        {
            GameScene.Game.CurrencyBox.Visible = !GameScene.Game.CurrencyBox.Visible;
        }

        #region Methods

        public void RefreshCurrency()
        {
            RefreshPrimaryCurrency();
            RefreshSecondaryCurrency();
        }

        private void SetPrimaryCurrency(CurrencyInfo currency)
        {
            PrimaryCurrency = currency ?? Globals.CurrencyInfoList.Binding.First(x => x.Type == CurrencyType.Gold);
        }

        private void RefreshPrimaryCurrency()
        {
            SetPrimaryCurrency(PrimaryCurrency);

            var userCurrency = GameScene.Game.User.GetCurrency(PrimaryCurrency);

            PrimaryCurrencyTitle.Text = userCurrency.Info.Abbreviation;
            PrimaryCurrencyLabel.Text = userCurrency.Amount.ToString("#,##0");
        }

        private void SetSecondaryCurrency(CurrencyInfo currency)
        {
            SecondaryCurrency = currency ?? Globals.CurrencyInfoList.Binding.First(x => x.Type == CurrencyType.GameGold);
        }

        private void RefreshSecondaryCurrency()
        {
            SetSecondaryCurrency(SecondaryCurrency);

            if (InvMode == InventoryMode.Sell) return;

            var userCurrency = GameScene.Game.User.GetCurrency(SecondaryCurrency);

            SecondaryCurrencyTitle.Text = userCurrency.Info.Abbreviation;
            SecondaryCurrencyTitle.ForeColour = Color.DarkOrange;
            SecondaryCurrencyLabel.Text = userCurrency.Amount.ToString("#,##0");
        }

        public void SellMode(CurrencyInfo currency, List<ItemType> sellableItemTypes)
        {
            SetPrimaryCurrency(currency);

            SellableItemTypes = sellableItemTypes;

            InvMode = InventoryMode.Sell;
        }

        public void NormalMode()
        {
            foreach (DXItemCell cell in SelectedItems.ToArray())
                cell.Selected = false;

            SelectedItems.Clear();

            SetPrimaryCurrency(null);

            SellableItemTypes.Clear();

            InvMode = InventoryMode.Normal;
        }

        #region InventoryMode

        public InventoryMode InvMode
        {
            get => _InvMode;
            set
            {
                if (_InvMode == value) return;

                InventoryMode oldValue = _InvMode;
                _InvMode = value;

                OnInventoryModeChanged(oldValue, value);
            }
        }
        private InventoryMode _InvMode;
        public event EventHandler<EventArgs> InventoryModeChanged;
        public void OnInventoryModeChanged(InventoryMode oValue, InventoryMode nValue)
        {
            TrashButton.Visible = false;
            SellButton.Visible = false;
            RecycleButton.Visible = false;

            DXItemCell.SelectedCell = null;

            switch (nValue)
            {
                case InventoryMode.Normal:
                    {
                        RefreshCurrency();

                        TrashButton.Visible = true;
                        RecycleButton.Visible = true;

                        TitleLabel.Text = CEnvir.Language.InventoryDialogTitle;
                    }
                    break;
                case InventoryMode.Sell:
                    {
                    SecondaryCurrencyTitle.Text = "总计";
                        SecondaryCurrencyTitle.ForeColour = Color.CornflowerBlue;
                        SecondaryCurrencyLabel.Text = 0.ToString("#,##0");

                        SellButton.Visible = true;

                        TitleLabel.Text = CEnvir.Language.InventoryDialogTitle + " [出售]";
                    }
                    break;
            }

            InventoryModeChanged?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #endregion

        #region IDisposable

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                if (Grid != null)
                {
                    if (!Grid.IsDisposed)
                        Grid.Dispose();

                    Grid = null;
                }

                if (TitleLabel != null)
                {
                    if (!TitleLabel.IsDisposed)
                        TitleLabel.Dispose();

                    TitleLabel = null;
                }

                if (PrimaryCurrencyLabel != null)
                {
                    if (!PrimaryCurrencyLabel.IsDisposed)
                        PrimaryCurrencyLabel.Dispose();

                    PrimaryCurrencyLabel = null;
                }

                if (SecondaryCurrencyLabel != null)
                {
                    if (!SecondaryCurrencyLabel.IsDisposed)
                        SecondaryCurrencyLabel.Dispose();

                    SecondaryCurrencyLabel = null;
                }

                if (PrimaryCurrencyTitle != null)
                {
                    if (!PrimaryCurrencyTitle.IsDisposed)
                        PrimaryCurrencyTitle.Dispose();

                    PrimaryCurrencyTitle = null;
                }

                if (SecondaryCurrencyTitle != null)
                {
                    if (!SecondaryCurrencyTitle.IsDisposed)
                        SecondaryCurrencyTitle.Dispose();

                    SecondaryCurrencyTitle = null;
                }

                if (WeightLabel != null)
                {
                    if (!WeightLabel.IsDisposed)
                        WeightLabel.Dispose();

                    WeightLabel = null;
                }

                if (WalletLabel != null)
                {
                    WalletLabel.MouseClick -= WalletLabel_MouseClick;

                    if (!WalletLabel.IsDisposed)
                        WalletLabel.Dispose();

                    WalletLabel = null;
                }

                if (CloseButton != null)
                {
                    if (!CloseButton.IsDisposed)
                        CloseButton.Dispose();

                    CloseButton = null;
                }

                if (SortButton != null)
                {
                    if (!SortButton.IsDisposed)
                        SortButton.Dispose();

                    SortButton = null;
                }

                if (TrashButton != null)
                {
                    if (!TrashButton.IsDisposed)
                        TrashButton.Dispose();

                    TrashButton = null;
                }

                if (SellButton != null)
                {
                    if (!SellButton.IsDisposed)
                        SellButton.Dispose();

                    SellButton = null;
                }

                if (RecycleSetButton != null)
                {
                    if (!RecycleSetButton.IsDisposed)
                        RecycleSetButton.Dispose();

                    RecycleSetButton = null;
                }

                if (RecycleButton != null)
                {
                    if (!RecycleButton.IsDisposed)
                        RecycleButton.Dispose();

                    RecycleButton = null;
                }

                if (PrimaryCurrencyTitle != null)
                {
                    if (!PrimaryCurrencyTitle.IsDisposed)
                        PrimaryCurrencyTitle.Dispose();

                    SecondaryCurrencyTitle = null;
                }

                if (SecondaryCurrencyTitle != null)
                {
                    if (!SecondaryCurrencyTitle.IsDisposed)
                        SecondaryCurrencyTitle.Dispose();

                    SecondaryCurrencyTitle = null;
                }
            }
        }

        #endregion
    }
}

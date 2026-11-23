using Client.Controls;
using Client.Envir;
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
    /// <summary>
    /// 一键回收设置（2026-11-23）。
    /// <para>
    /// 布局参考完整客户端 <b>A 键辅助页里的「拾取」页面</b>：
    /// 顶部开关 + 「勾选非白」快捷按钮，下方是**带复选框的物品列表**（不是手输名字）。
    /// </para>
    /// <para>
    /// <b>卡顿</b>：列表用 <see cref="DXListView"/>，它内部只绘制当前可见行
    /// （见 <c>DXListView</c> 里「只画当前可见区间」那段），拾取页 1600+ 行拖动不卡也是靠这个，
    /// 所以这里直接复用，不自己堆控件。
    /// </para>
    /// <para>
    /// <b>协议不变</b>：仍然只发 <c>C.RecycleSet { Enabled, Settings }</c>，
    /// 存仓开关编码进 Settings 第一段（`store=0/1;物品名,物品名`）。
    /// </para>
    /// </summary>
    public sealed class RecycleDialog : DXWindow
    {
        private const int NameColumnWidth = 250;
        private const int CheckColumnWidth = 46;

        /// <summary>列表一行：物品名 + 品质（用于着色和「非白」判定）。</summary>
        private sealed class Row
        {
            public string Name;
            public Rarity Rarity;
            public bool HasPart;
            public bool CanHaveStats;
        }

        public DXCheckBox EnabledBox, StoreBox;
        public DXTextBox SearchBox;
        public DXButton SearchButton, SelectNonWhiteButton, SaveButton;
        public DXListView ItemView;
        public DXLabel HelpLabel;

        private readonly List<Row> _rows = new List<Row>();
        private readonly HashSet<string> _keep = new HashSet<string>(StringComparer.Ordinal);
        private bool _built;

        public override WindowType Type => WindowType.None;
        public override bool CustomSize => false;
        public override bool AutomaticVisibility => false;

        public RecycleDialog()
        {
            TitleLabel.Text = "一键回收设置";

            HasFooter = true;
            Movable = true;
            Border = true;

            SetClientSize(new Size(420, 470));

            EnabledBox = new DXCheckBox
            {
                Label = { Text = "启用一键回收" },
                Parent = this,
            };
            EnabledBox.Location = new Point(ClientArea.Location.X + 10, ClientArea.Y + 10);

            StoreBox = new DXCheckBox
            {
                Label = { Text = "保留物品存仓库" },
                Parent = this,
            };
            StoreBox.Location = new Point(ClientArea.Location.X + 150, ClientArea.Y + 10);

            SelectNonWhiteButton = new DXButton
            {
                ButtonType = ButtonType.SmallButton,
                Parent = this,
                Size = new Size(90, 18),
                Label = { Text = "勾选非白" },
                Hint = "把所有非白色品质的物品一键加入保留清单"
            };
            SelectNonWhiteButton.Location = new Point(ClientArea.Location.X + 300, ClientArea.Y + 9);
            SelectNonWhiteButton.MouseClick += (o, e) => SelectNonWhite();

            SearchBox = new DXTextBox
            {
                Parent = this,
                Size = new Size(300, 20),
                MaxLength = 40,
            };
            SearchBox.Location = new Point(ClientArea.Location.X + 10, ClientArea.Y + 38);

            SearchButton = new DXButton
            {
                ButtonType = ButtonType.SmallButton,
                Parent = this,
                Size = new Size(80, 18),
                Label = { Text = "搜索" },
            };
            SearchButton.Location = new Point(ClientArea.Location.X + 318, ClientArea.Y + 38);
            SearchButton.MouseClick += (o, e) => RefreshItems(SearchBox.TextBox.Text);

            ItemView = new DXListView
            {
                Parent = this,
                Location = new Point(ClientArea.Location.X + 6, ClientArea.Y + 64),
                Size = new Size(ClientArea.Width - 12, ClientArea.Height - 120),
                ItemBorder = false,
            };

            ItemView.InsertColumn(0U, "物品名称", NameColumnWidth, 24, "勾选=保留（存仓或留背包），不勾=回收出售");
            ItemView.InsertColumn(1U, "保留", CheckColumnWidth, 24, "勾选后这件物品不会被出售");

            HelpLabel = new DXLabel
            {
                Parent = this,
                Text = "回城卷 / 道士符 / 毒药 / 红蓝药 固定留背包，不在此列表内。",
                ForeColour = Color.Gray,
            };
            HelpLabel.Location = new Point(ClientArea.Location.X + 10, ClientArea.Y + ClientArea.Height - 52);

            SaveButton = new DXButton
            {
                ButtonType = ButtonType.SmallButton,
                Parent = this,
                Size = new Size(70, SmallButtonHeight),
                Label = { Text = "保存" },
            };
            SaveButton.Location = new Point(ClientArea.Location.X + ClientArea.Width - 80, ClientArea.Y + ClientArea.Height - 30);
            SaveButton.MouseClick += (o, e) => Save();
        }

        /// <summary>打开时同步服务端下发的设置并重建列表。</summary>
        public void Refresh()
        {
            ParseSettings(CEnvir.RecycleSettings);

            EnabledBox.Checked = CEnvir.RecycleEnabled;
            StoreBox.Checked = ReadStoreFlag(CEnvir.RecycleSettings);

            if (!_built) BuildRows();

            RefreshItems(null);
        }

        /// <summary>把 `store=0|1;物品名,物品名` 拆开，物品名进 <see cref="_keep"/>。</summary>
        private void ParseSettings(string raw)
        {
            _keep.Clear();

            if (string.IsNullOrWhiteSpace(raw)) return;

            string list = raw;
            int index = raw.IndexOf(';');

            if (index >= 0)
            {
                string head = raw.Substring(0, index).Trim();

                if (head.StartsWith("store=", StringComparison.OrdinalIgnoreCase))
                    list = raw.Substring(index + 1);
            }

            foreach (string name in list.Split(',', '，'))
            {
                string text = name.Trim();

                if (text.Length > 0) _keep.Add(text);
            }
        }

        private static bool ReadStoreFlag(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return true;

            int index = raw.IndexOf(';');

            if (index < 0) return true;

            string head = raw.Substring(0, index).Trim();

            if (!head.StartsWith("store=", StringComparison.OrdinalIgnoreCase)) return true;

            return head.Substring(6).Trim() == "1";
        }

        /// <summary>一次性把所有物品整理成行数据（只在第一次打开时跑）。</summary>
        private void BuildRows()
        {
            _built = true;
            _rows.Clear();

            foreach (ItemInfo info in Globals.ItemInfoList.Binding)
            {
                if (info == null || string.IsNullOrEmpty(info.ItemName)) continue;

                _rows.Add(new Row
                {
                    Name = info.ItemName,
                    Rarity = info.Rarity,
                    HasPart = info.ItemEffect == ItemEffect.ItemPart,
                    CanHaveStats = info.ItemType == ItemType.Weapon
                                || info.ItemType == ItemType.Armour
                                || info.ItemType == ItemType.Helmet
                                || info.ItemType == ItemType.Necklace
                                || info.ItemType == ItemType.Bracelet
                                || info.ItemType == ItemType.Ring
                                || info.ItemType == ItemType.Shoes
                                || info.ItemType == ItemType.Shield,
                });
            }
        }

        /// <summary>按关键字重建列表（空关键字 = 全部）。</summary>
        private void RefreshItems(string keyword)
        {
            if (ItemView == null) return;

            ItemView.RemoveAll();

            string filter = keyword?.Trim();

            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];

                if (!string.IsNullOrEmpty(filter) && row.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                uint line = ItemView.InsertItem(uint.MaxValue, row.Name);

                if (ItemView.GetItem(line, 0U) is DXLabel label)
                    label.ForeColour = GetNameColour(row);

                ItemView.SetItem(line, 1U, CreateRowCheckBox(_keep.Contains(row.Name), row.Name));
            }

            ItemView.UpdateItems();
        }

        private DXCheckBox CreateRowCheckBox(bool value, string name)
        {
            DXCheckBox box = new DXCheckBox
            {
                Size = new Size(CheckColumnWidth, 20),
                Checked = value,
                Tag = name,
            };

            box.CheckedChanged += (o, e) =>
            {
                if (!(o is DXCheckBox source) || !(source.Tag is string itemName)) return;

                if (source.Checked) _keep.Add(itemName);
                else _keep.Remove(itemName);
            };

            return box;
        }

        /// <summary>「勾选非白」：所有非普通品质的物品一键加入保留（与拾取页同名按钮一致）。</summary>
        private void SelectNonWhite()
        {
            foreach (Row row in _rows)
            {
                if (row.Rarity == Rarity.Common) continue;

                _keep.Add(row.Name);
            }

            RefreshItems(SearchBox?.TextBox?.Text);
        }

        private void Save()
        {
            if (_keep.Count == 0)
            {
                GameScene.Game.ReceiveChat("保留清单不能为空（至少勾选一件要保留的物品）。", MessageType.System);
                return;
            }

            string settings = (StoreBox.Checked ? "store=1;" : "store=0;") + string.Join(",", _keep);

            CEnvir.Enqueue(new C.RecycleSet
            {
                Enabled = EnabledBox.Checked,
                Settings = settings,
            });

            CEnvir.RecycleEnabled = EnabledBox.Checked;
            CEnvir.RecycleSettings = settings;

            GameScene.Game.ReceiveChat(string.Format("一键回收设置已保存（保留 {0} 件）。", _keep.Count), MessageType.System);

            Visible = false;
        }

        /// <summary>物品名着色，规则与拾取页一致（碎片/可带属性/普通三套配色）。</summary>
        private static Color GetNameColour(Row row)
        {
            if (row.HasPart)
            {
                switch (row.Rarity)
                {
                    case Rarity.Common: return Color.DarkGray;
                    case Rarity.Superior: return Color.DarkSeaGreen;
                    case Rarity.Elite: return Color.MediumOrchid;
                }
            }
            else if (row.CanHaveStats)
            {
                switch (row.Rarity)
                {
                    case Rarity.Common: return Color.LightSkyBlue;
                    case Rarity.Superior: return Color.Lime;
                    case Rarity.Elite: return Color.Magenta;
                }
            }
            else
            {
                switch (row.Rarity)
                {
                    case Rarity.Common: return Color.Gainsboro;
                    case Rarity.Superior: return Color.LightGreen;
                    case Rarity.Elite: return Color.Violet;
                }
            }

            return Color.Gainsboro;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing) return;

            if (EnabledBox != null) { if (!EnabledBox.IsDisposed) EnabledBox.Dispose(); EnabledBox = null; }
            if (StoreBox != null) { if (!StoreBox.IsDisposed) StoreBox.Dispose(); StoreBox = null; }
            if (SearchBox != null) { if (!SearchBox.IsDisposed) SearchBox.Dispose(); SearchBox = null; }
            if (SearchButton != null) { if (!SearchButton.IsDisposed) SearchButton.Dispose(); SearchButton = null; }
            if (SelectNonWhiteButton != null) { if (!SelectNonWhiteButton.IsDisposed) SelectNonWhiteButton.Dispose(); SelectNonWhiteButton = null; }
            if (SaveButton != null) { if (!SaveButton.IsDisposed) SaveButton.Dispose(); SaveButton = null; }
            if (HelpLabel != null) { if (!HelpLabel.IsDisposed) HelpLabel.Dispose(); HelpLabel = null; }
            if (ItemView != null) { if (!ItemView.IsDisposed) ItemView.Dispose(); ItemView = null; }
        }
    }
}

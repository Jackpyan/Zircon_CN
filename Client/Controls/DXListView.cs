using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Client.Controls
{
    /// <summary>
    /// 带表头、可滚动、支持多列的多行列表控件。
    /// 移植自 A 端 Client\Controls\DXListView.cs（610 行）。
    ///
    /// 与 A 端的差异：
    /// 1. **删掉 `DrawGrid`**：A 端那份用 SlimDX 的 `DXManager.Line` / `Surface` / `PresentTexture`
    ///    画网格，B 端渲染层已换成 RenderingCore；而且该方法在 A 端**从未被调用**，直接去掉。
    /// 2. **修正 `Dispose` 顺序**：B 端 `DXControl.Dispose` 会先销毁全部子控件再把
    ///    `Controls` 置 null，派生类必须在 `base.Dispose` **之前**清掉自己的引用。
    /// 3. `HeightLight` setter 加空值保护（A 端把非空引用置 null 会 NRE）。
    /// 4. `RemoveAll` 不再 `Items.Controls.Clear()`：B 端明令禁止对 Controls 做 Clear
    ///    （控件内部件也挂在里面），改为逐个 Dispose，Dispose 内部会自行从父列表移除。
    /// 5. 除零保护：`VScrollBar.Change` 为 0 时不计算首行。
    /// </summary>
    public class DXListView : DXControl
    {
        public int ItemHeight;
        public int Vspac;
        public int Hspac;
        public Color ItemBorderColour;
        public Color ItemSelectedBorderColour;
        public Color ItemBackColour;
        public Color ItemSelectedBackColour;
        public Color HeaderBorderColour;
        public Color HeaderSelectedBorderColour;
        public Color HeaderBackColour;
        public Color HeaderSelectedBackColour;
        public bool ItemBorder;
        public bool HeaderBorder;
        public bool SelectedBorder;
        public bool HasHeader;
        public DXVScrollBar HScrollBar;
        public DXVScrollBar VScrollBar;
        public int VScrollValue;
        public DXControl Items;
        public DXControl Headers { get; set; }
        private int _First;
        private int _Last;
        private DXControl _HeightLight;

        public event EventHandler<EventArgs> ItemMouseEnter;

        public event EventHandler<EventArgs> ItemMouseLeave;

        public event EventHandler<MouseEventArgs> ItemMouseClick;

        public event EventHandler<MouseEventArgs> ItemMouseDbClick;

        public event EventHandler<MouseEventArgs> ItemMouseRButton;

        public DXControl HeightLight
        {
            get => _HeightLight;
            set
            {
                if (_HeightLight == value) return;

                DXControl heightLight = _HeightLight;
                _HeightLight = value;

                if (heightLight != null)
                {
                    foreach (DXControl control in heightLight.Controls)
                    {
                        control.BackColour = ItemBackColour;
                        control.BorderColour = ItemBorderColour;
                    }

                    heightLight.BorderColour = ItemBorderColour;
                    heightLight.Border = false;
                }

                if (_HeightLight == null) return;

                foreach (DXControl control in _HeightLight.Controls)
                {
                    control.BackColour = ItemSelectedBackColour;
                    control.BorderColour = ItemSelectedBorderColour;
                }

                _HeightLight.BorderColour = ItemSelectedBorderColour;
                _HeightLight.Border = true;
            }
        }

        public uint ItemCount => (uint)Items.Controls.Count;

        public uint ColumnCount => (uint)Headers.Controls.Count;

        public DXListView()
        {
            ItemHeight = 18;
            HasHeader = true;
            Hspac = 1;
            Vspac = 0;
            _First = 0;
            _Last = 0;
            ItemBorderColour = Color.FromArgb(69, 56, 32);
            ItemBackColour = Color.Empty;
            ItemSelectedBorderColour = Color.FromArgb(160, 125, 22);
            ItemSelectedBackColour = Color.FromArgb(31, 25, 12);
            ItemBorder = true;
            HeaderBorderColour = Color.FromArgb(160, 125, 22);
            HeaderBackColour = Color.FromArgb(31, 25, 12);
            HeaderSelectedBorderColour = Color.Yellow;
            HeaderSelectedBackColour = Color.FromArgb(89, 68, 12);
            HeaderBorder = true;
            SelectedBorder = false;

            Headers = new DXControl
            {
                Parent = this,
                Size = new Size(ItemHeight, ItemHeight)
            };

            Items = new DXControl
            {
                Parent = this,
                Size = new Size(ItemHeight, ItemHeight)
            };

            VScrollValue = 0;

            VScrollBar = new DXVScrollBar
            {
                Parent = this,
                Size = new Size(14, ItemHeight),
                Visible = true,
                Value = 0,
                MaxValue = 1,
                MinValue = 0,
                BorderColour = HeaderBorderColour
            };

            VScrollBar.ValueChanged += (o, e) => UpdateItems();

            MouseWheel += VScrollBar.DoMouseWheel;
            Headers.MouseWheel += VScrollBar.DoMouseWheel;
            Headers.MouseUp += OnItemMouseUp;
            Items.MouseWheel += VScrollBar.DoMouseWheel;
            Items.MouseUp += OnItemMouseUp;
        }

        public override void OnSizeChanged(Size oValue, Size nValue)
        {
            base.OnSizeChanged(oValue, nValue);

            UpdateViewRect();
        }

        public void UpdateViewRect()
        {
            if (Headers == null || Items == null || VScrollBar == null) return;

            Headers.Location = new Point(5, 5);
            Headers.Size = new Size(Size.Width - VScrollBar.Size.Width - 10, Headers.Size.Height);

            Items.Location = new Point(5, Headers.Location.Y + Headers.Size.Height + 3);
            Items.Size = new Size(Headers.Size.Width, Size.Height - Headers.Size.Height - 10 - 3);

            UpdateScrollBar();
            UpdateItems();
        }

        protected override void DrawChildControls()
        {
            foreach (DXControl control in Items.Controls)
                control.Draw();

            foreach (DXControl control in Headers.Controls)
                control.Draw();

            HeightLight?.Draw();

            VScrollBar.Draw();
        }

        public void UpdateScrollBar()
        {
            if (VScrollBar == null || Items == null || Headers == null) return;

            if (ItemCount == 0U || ColumnCount == 0U)
            {
                VScrollBar.Visible = false;
                return;
            }

            VScrollBar.Location = new Point(Headers.Location.X + Headers.Size.Width, Headers.Location.Y + 1);
            VScrollBar.VisibleSize = Items.Size.Height;
            VScrollBar.Size = new Size(VScrollBar.Size.Width, Headers.Size.Height + Items.Size.Height + 3);
            VScrollBar.Visible = true;

            VScrollBar.Change = Items.Controls[0].Size.Height + Vspac;

            if (VScrollBar.Change <= 0) return;

            int num = VScrollBar.VisibleSize % VScrollBar.Change;

            if (num <= 0) return;

            VScrollBar.VisibleSize -= num;
        }

        public void UpdateItems()
        {
            if (VScrollBar == null || Items == null || Headers == null) return;

            int x = 1;
            int y = 0;
            int num1 = VScrollBar.Value;

            if (ItemCount > 0U && VScrollBar.Change > 0)
                _First = num1 / VScrollBar.Change;

            for (int index = 0; index < _First; ++index)
                if (index < Items.Controls.Count)
                    Items.Controls[index].Visible = false;

            int first1 = _First;

            while (true)
            {
                if (y >= Size.Height || (long)first1 >= (long)ItemCount) break;

                DXControl control = Items.Controls[first1];
                control.Location = new Point(0, y);
                control.Size = new Size(Items.Size.Width, control.Size.Height);
                y += control.Size.Height + Vspac;
                control.Visible = true;
                _Last = first1;
                ++first1;
            }

            for (int index = _Last + 1; (long)index < (long)ItemCount; ++index)
                Items.Controls[index].Visible = false;

            VScrollBar.MaxValue = (int)((long)ItemCount * (long)VScrollBar.Change);

            for (uint index = 0; index < ColumnCount; ++index)
            {
                DXControl control1 = Headers.Controls[(int)index];
                control1.Location = new Point(x, 1);
                control1.Size = new Size(control1.Size.Width, Headers.Size.Height - 2);

                for (int first2 = _First; first2 <= _Last && (long)first2 < (long)ItemCount; ++first2)
                {
                    DXControl control2 = Items.Controls[first2];

                    if ((long)index >= (long)control2.Controls.Count) continue;

                    DXControl control3 = control2.Controls[(int)index];
                    control3.Location = new Point(control1.Location.X, 1);
                    control3.Size = new Size(control1.Size.Width, control2.Size.Height);
                }

                x += control1.Size.Width + Hspac;
            }
        }

        public void OnItemMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            ItemMouseRButton?.Invoke(sender, e);
        }

        public void OnItemMouseEnter(object sender, EventArgs e)
        {
            if (!(sender is DXControl dxControl)) return;

            if (HeightLight == dxControl.Parent)
            {
                if (SelectedBorder)
                    dxControl.BorderColour = ItemSelectedBorderColour;
                else
                    dxControl.BackColour = ItemSelectedBackColour;
            }
            else if (SelectedBorder)
                dxControl.BorderColour = ItemSelectedBorderColour;
            else
                dxControl.BackColour = ItemSelectedBackColour;

            ItemMouseEnter?.Invoke(sender, e);
        }

        public void OnItemMouseLeave(object sender, EventArgs e)
        {
            if (!(sender is DXControl dxControl)) return;

            if (HeightLight == dxControl.Parent)
            {
                if (SelectedBorder)
                    dxControl.BorderColour = ItemSelectedBorderColour;
                else
                    dxControl.BackColour = ItemSelectedBackColour;
            }
            else if (SelectedBorder)
                dxControl.BorderColour = ItemBorderColour;
            else
                dxControl.BackColour = ItemBackColour;

            ItemMouseLeave?.Invoke(sender, e);
        }

        public void OnItemMouseClick(object sender, MouseEventArgs e)
        {
            if (!(sender is DXControl dxControl)) return;

            HeightLight = dxControl.Parent;

            ItemMouseClick?.Invoke(sender, e);
        }

        public void OnItemMouseDbClick(object sender, MouseEventArgs e)
        {
            if (!(sender is DXControl)) return;

            ItemMouseDbClick?.Invoke(sender, e);
        }

        public uint InsertColumn(uint col, DXControl control)
        {
            control.Border = HeaderBorder;
            control.BorderColour = HeaderBorderColour;
            control.BackColour = HeaderBackColour;

            Headers.Size = new Size(Headers.Size.Width, control.Size.Height);

            UpdateViewRect();

            if (col >= ColumnCount)
            {
                col = ColumnCount;
                control.Parent = Headers;
            }
            else
            {
                control.Parent = Headers;
                Headers.Controls.Remove(control);
                Headers.Controls.Insert((int)col, control);
            }

            control.MouseClick += (o, e) => { };
            control.MouseEnter += (o, e) => control.BackColour = HeaderSelectedBackColour;
            control.MouseLeave += (o, e) => control.BackColour = HeaderBackColour;
            control.MouseUp += OnItemMouseUp;

            UpdateScrollBar();
            UpdateItems();

            return col;
        }

        public void DeleteColumn(uint col)
        {
            if (col >= ColumnCount) return;

            Headers.Controls.RemoveAt((int)col);

            foreach (DXControl control in Items.Controls)
                if ((int)col < control.Controls.Count)
                    control.Controls.RemoveAt((int)col);

            UpdateScrollBar();
            UpdateItems();
        }

        public uint InsertItem(uint nItem, DXControl control)
        {
            if (ColumnCount == 0U)
                InsertColumn(0U, new DXLabel { Text = "unnamed", AutoSize = false });

            DXControl wrapper = new DXControl { Size = control.Size };
            wrapper.MouseClick += (o, e) => HeightLight = o as DXControl;
            wrapper.MouseWheel += VScrollBar.DoMouseWheel;

            if (nItem >= ItemCount)
            {
                nItem = ItemCount;
                wrapper.Parent = Items;
            }
            else
            {
                wrapper.Parent = Items;
                Items.Controls.Remove(wrapper);
                Items.Controls.Insert((int)nItem, wrapper);
            }

            control.Parent = wrapper;
            control.Border = ItemBorder;
            control.BorderColour = ItemBorderColour;
            control.BackColour = ItemBackColour;
            control.MouseUp += OnItemMouseUp;
            control.MouseEnter += OnItemMouseEnter;
            control.MouseLeave += OnItemMouseLeave;
            control.MouseClick += OnItemMouseClick;
            control.MouseDoubleClick += OnItemMouseDbClick;

            for (uint index = 1; index < ColumnCount; ++index)
            {
                DXLabel label = new DXLabel
                {
                    Parent = wrapper,
                    Text = index.ToString(),
                    Border = control.Border,
                    BorderColour = control.BorderColour,
                    AutoSize = false
                };

                label.MouseWheel += VScrollBar.DoMouseWheel;
            }

            control.MouseWheel += VScrollBar.DoMouseWheel;

            UpdateScrollBar();

            return nItem;
        }

        public DXControl SetItem(uint nItem, uint subItem, DXControl control)
        {
            if (nItem < ItemCount && subItem < ColumnCount)
            {
                DXControl wrapper = Items.Controls[(int)nItem];
                DXControl old = wrapper.Controls[(int)subItem];

                old.Parent = null;
                control.Parent = wrapper;
                wrapper.Controls.Remove(control);
                wrapper.Controls.Insert((int)subItem, control);

                control.Border = ItemBorder;
                control.BorderColour = ItemBorderColour;
                control.BackColour = ItemBackColour;
                control.MouseUp += OnItemMouseUp;
                control.MouseWheel += VScrollBar.DoMouseWheel;
                control.MouseEnter += OnItemMouseEnter;
                control.MouseLeave += OnItemMouseLeave;
                control.MouseClick += OnItemMouseClick;
                control.MouseDoubleClick += OnItemMouseDbClick;

                old.Dispose();
            }

            return control;
        }

        public DXControl GetItem(uint nItem, uint nSubItem)
        {
            if (nItem < ItemCount && nSubItem < ColumnCount)
                return Items.Controls[(int)nItem].Controls[(int)nSubItem];

            return null;
        }

        public uint InsertItem(uint nItem, string text)
        {
            DXLabel label = new DXLabel
            {
                Text = text,
                AutoSize = false,
                Size = new Size(0, ItemHeight),
                DrawFormat = TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter
            };

            return InsertItem(nItem, label);
        }

        public DXControl SetItem(uint nItem, uint subItem, string text)
        {
            DXLabel label = new DXLabel
            {
                AutoSize = true,
                Text = text
            };

            return SetItem(nItem, subItem, label);
        }

        public uint InsertColumn(uint nColumn, string text, int width, int height, string hint = null)
        {
            DXLabel label = new DXLabel
            {
                Text = text,
                AutoSize = false,
                Size = new Size(width, height),
                DrawFormat = TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter,
                Hint = hint
            };

            return InsertColumn(nColumn, label);
        }

        public void SortByName(string name)
        {
            for (int index = 0; (long)index < (long)ItemCount; ++index)
            {
                DXControl control = Items.Controls[index];

                if (control.Controls.Count == 0) continue;
                if (control.Controls[0].Text.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;

                Items.Controls.RemoveAt(index);
                Items.Controls.Insert(0, control);
            }

            VScrollBar.Value = 0;
            UpdateItems();
            UpdateScrollBar();
        }

        public void DeleteItem(uint nItem)
        {
            if (nItem >= ItemCount) return;

            DXControl control = Items.Controls[(int)nItem];
            Items.Controls.RemoveAt((int)nItem);

            UpdateScrollBar();
            UpdateItems();

            control.Dispose();
        }

        public void RemoveAll()
        {
            // 不能对 Items.Controls 做 Clear：B 端 DXControl 的内部件也挂在 Controls 里。
            // Dispose 会自行从父列表移除，这里逐个释放即可。
            List<DXControl> controls = new List<DXControl>(Items.Controls);

            foreach (DXControl control in controls)
                control.Dispose();

            controls.Clear();

            UpdateScrollBar();
            UpdateItems();
        }

        protected override void Dispose(bool disposing)
        {
            // 必须先清掉自身引用，再调 base（base 会销毁全部子控件并把 Controls 置 null）
            if (disposing)
            {
                ItemMouseEnter = null;
                ItemMouseLeave = null;
                ItemMouseClick = null;
                ItemMouseDbClick = null;
                ItemMouseRButton = null;

                _HeightLight = null;
                HScrollBar = null;
                VScrollBar = null;
                Items = null;
                Headers = null;
            }

            base.Dispose(disposing);
        }
    }
}

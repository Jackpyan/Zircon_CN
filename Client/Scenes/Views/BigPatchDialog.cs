using Client.Controls;
using Client.Envir;
using Client.UserModels;
using Library;
using Library.SystemModels;
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using C = Library.Network.ClientPackets;

namespace Client.Scenes.Views
{
    /// <summary>
    /// 辅助功能主窗口（按 A 键打开）。
    /// 移植自 A端 mir3_cb\zircon-legend-client\Client\Scenes\Views\BigPatchDialog.cs。
    ///
    /// 已完成：阶段 1（窗口骨架）、阶段 2（常用标签页）。
    /// 后续阶段按 辅助功能移植.md 逐个加入：辅助 / 保护 / 聊天 / 便签 / 记录 / 拾取 / 魔法 / 帮助。
    /// </summary>
    public sealed class BigPatchDialog : DXWindow
    {
        #region Properties

        public DXTabControl TabControl;

        public DXCommonlyTab Commonly;
        public DXProtectionTab Protect;

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

            Commonly = new DXCommonlyTab
            {
                Parent = TabControl,
                TabButton = { Label = { Text = "常用", Hint = "一些常用的功能设置" } },
            };

            Protect = new DXProtectionTab
            {
                Parent = TabControl,
                Border = true,
                TabButton = { Label = { Text = "保护", Hint = "自动使用物品，以及遇到特殊情况的自动应对方案" } },
            };
        }

        #region 控件工厂

        /// <summary>
        /// 创建复选框。
        /// B端 DXCheckBox 与 A端的差异：
        /// 1. 没有 Text 属性，文字挂在 Label 上；
        /// 2. 没有 AutoSize，尺寸由 Label.DisplayArea + Box.DisplayArea 自动计算；
        /// 3. UpdateControl() 是 private，不需要也不允许手动调用；
        /// 4. 有 SetSilentState(bool) 可静默赋值，不触发 CheckedChanged（避免初始化时误写配置）。
        /// </summary>
        public static DXCheckBox CreateCheckBox(DXControl parent, string name, int x, int y, EventHandler<EventArgs> changed, bool @checked = false)
        {
            DXCheckBox box = new DXCheckBox
            {
                Parent = parent,
                Location = new Point(x, y),
            };

            box.Label.Text = name;
            box.SetSilentState(@checked);

            if (changed != null)
                box.CheckedChanged += changed;

            return box;
        }

        #endregion

        #region IDisposable

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                Commonly = null;
                Protect = null;

                if (TabControl != null)
                {
                    if (!TabControl.IsDisposed)
                        TabControl.Dispose();

                    TabControl = null;
                }
            }
        }

        #endregion

        #region 分组框控件（A端自定义，B端没有，随本文件移植）

        /// <summary>
        /// 带标题的分组框。
        /// 移植自 A端 BigPatchDialog.cs:1093-1150，但**边框绘制方式不同**：
        /// A端重写 UpdateBorderInformation 生成 5 点折线，让上边框在标题处断开（标题凸出在框顶）；
        /// B端 DXControl.DrawBorder() 只用 BorderInformation 是否为 null 作开关，固定绘制矩形四条边，
        /// 折线坐标被完全忽略（见 DXControl.cs:1787-1805）。
        /// 因此这里不再重写 UpdateBorderInformation，标题显示在框内左上角，无缺口。
        /// 这是渲染机制差异，不影响功能。
        /// </summary>
        public class DXGroupBox : DXControl
        {
            public DXLabel Name;

            public DXGroupBox()
            {
                Name = new DXLabel
                {
                    Parent = this,
                    Outline = true,
                    ForeColour = Color.FromArgb(70, 58, 35),
                    Location = new Point(5, 10),
                };

                BorderColour = Color.FromArgb(70, 58, 35);
                Border = true;
            }
        }

        #endregion

        #region 常用标签页

        /// <summary>
        /// 常用标签页。移植自 A端 BigPatchDialog.cs:1293-2105。
        /// 三列布局：常用 / 战斗 / 物品，下方为天气（隐藏）、鼠标中键、刷新包裹、特殊命令。
        /// </summary>
        public class DXCommonlyTab : DXTab
        {
            #region Properties

            public DXGroupBox GroupNormal;
            public DXGroupBox GroupWar;
            public DXGroupBox GroupItem;
            public DXGroupBox GroupWeather;
            public DXComboBox CombWeather;

            public DXLabel LabCommand;
            public DXComboBox CombCmdBox;
            public DXButton BtSubmit;

            public DXGroupBox GroupMouseMiddle;
            public DXCheckBox ChkCallMounts;
            public DXCheckBox ChkCastingMagic;
            public DXComboBox CombMiddleMouse;

            public DXButton RefreshBag;
            public DXButton ReloadConfig;

            public DXCheckBox ShowItemNames;
            public DXCheckBox ChkAutoPick;
            public DXCheckBox ChkTabPick;
            public DXCheckBox ChkAutoBook;

            #endregion

            private const int ColX = 15;      // 复选框左边距
            private const int RowHeight = 25; // 行间距

            public DXCommonlyTab()
            {
                Border = true;

                GroupNormal = CreateGroup("常用", new Size(120, 405));
                GroupWar = CreateGroup("战斗", new Size(120, 405));
                GroupItem = CreateGroup("物品", new Size(120, 405));

                // 天气分组在 A端就已被隐藏（选项全部注释掉），这里保留占位，待后续阶段接入
                GroupWeather = CreateGroup("天气", new Size(120, 32));
                GroupWeather.Visible = false;

                GroupMouseMiddle = CreateGroup("鼠标中键", new Size(120, 132));

                BuildNormalGroup();
                BuildWarGroup();
                BuildItemGroup();
                BuildWeatherGroup();
                BuildMouseMiddleGroup();
                BuildBottomButtons();
            }

            #region 分组框创建

            private DXGroupBox CreateGroup(string title, Size size)
            {
                DXGroupBox group = new DXGroupBox
                {
                    Parent = this,
                    Size = size,
                    Location = new Point(10, 0),
                };

                group.Name.Text = title;
                return group;
            }

            #endregion

            #region 常用

            private void BuildNormalGroup()
            {
                CHK_ITEM_SET[] items =
                {
                    new CHK_ITEM_SET("免蜡烛", Config.免蜡烛, (o, e) =>
                    {
                        Config.免蜡烛 = ((DXCheckBox)o).Checked;
                        // B端光照逻辑在 MapControl.LLayer（嵌套类 Light）上，A端是 MapControl.UpdateLights()
                        GameScene.Game?.MapControl?.LLayer?.UpdateLights();
                    }),
                    new CHK_ITEM_SET("名字显示", Config.ShowPlayerNames, (o, e) =>
                    {
                        Config.ShowPlayerNames = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("清理尸体", Config.清理尸体, (o, e) =>
                    {
                        Config.清理尸体 = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("在安全处有效", Config.在安全处有效, (o, e) =>
                    {
                        Config.在安全处有效 = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("Boss提示", Config.Boss提示, (o, e) =>
                    {
                        Config.Boss提示 = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("自动特修物品", Config.SpecialRepair, (o, e) =>
                    {
                        Config.SpecialRepair = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("跑不停", Config.跑不停, (o, e) =>
                    {
                        bool state = ((DXCheckBox)o).Checked;
                        Config.跑不停 = state;

                        if (GameScene.Game != null)
                            GameScene.Game.AutoRun = state;
                    }),
                    new CHK_ITEM_SET("怪物信息", Config.MonsterBoxVisible, (o, e) =>
                    {
                        Config.MonsterBoxVisible = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("自动关组", Config.自动关组, (o, e) =>
                    {
                        Config.自动关组 = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("怪物名字显示", Config.ShowMonsterNames, (o, e) =>
                    {
                        Config.ShowMonsterNames = ((DXCheckBox)o).Checked;
                    }),
                };

                int y = 30;
                foreach (CHK_ITEM_SET item in items)
                {
                    CreateCheckBox(GroupNormal, item.Name, ColX, y, item.Method, item.State);
                    y += 24;
                }

                GroupNormal.Size = new Size(GroupNormal.Size.Width, y);
            }

            #endregion

            #region 战斗

            private void BuildWarGroup()
            {
                CHK_ITEM_SET[] items =
                {
                    new CHK_ITEM_SET("免SHIFT", Config.免SHIFT, (o, e) =>
                    {
                        Config.免SHIFT = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("数字飘血", Config.ShowDamageNumbers, (o, e) =>
                    {
                        Config.ShowDamageNumbers = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("关闭经验提示", Config.关闭经验提示, (o, e) =>
                    {
                        Config.关闭经验提示 = ((DXCheckBox)o).Checked;
                    }),
                    new CHK_ITEM_SET("Ctrl + 1-5 控制随从", Config.启用Ctrl数字控制宠物, (o, e) =>
                    {
                        Config.启用Ctrl数字控制宠物 = ((DXCheckBox)o).Checked;
                    }),
                };

                int y = 30;
                foreach (CHK_ITEM_SET item in items)
                {
                    CreateCheckBox(GroupWar, item.Name, ColX, y, item.Method, item.State);
                    y += RowHeight;
                }

                GroupWar.Size = new Size(GroupWar.Size.Width, y);
            }

            #endregion

            #region 物品

            private void BuildItemGroup()
            {
                int y = 30;

                ShowItemNames = CreateCheckBox(GroupItem, "物品名称显示", ColX, y,
                    (o, e) => Config.ShowItemNames = ShowItemNames.Checked, Config.ShowItemNames);
                y += RowHeight;

                // 「快速自动捡取」与「Tab捡取」互斥
                ChkAutoPick = CreateCheckBox(GroupItem, "快速自动捡取", ColX, y,
                    (o, e) => Config.快速自动拾取 = ChkAutoPick.Checked, Config.快速自动拾取);
                ChkAutoPick.CheckedChanged += (o, e) =>
                {
                    if (ChkAutoPick.Checked)
                        ChkTabPick.Checked = false;
                };
                y += RowHeight;

                ChkTabPick = CreateCheckBox(GroupItem, "Tab捡取", ColX, y,
                    (o, e) => Config.Tab捡取 = ChkTabPick.Checked, Config.Tab捡取);
                ChkTabPick.CheckedChanged += (o, e) =>
                {
                    if (ChkTabPick.Checked)
                        ChkAutoPick.Checked = false;
                };
                y += RowHeight;

                // A端原逻辑：勾选技能书时复用 Tab捡取 的互斥判断（历史遗留，无害，保持行为一致）
                ChkAutoBook = CreateCheckBox(GroupItem, "自动学习技能书", ColX, y,
                    (o, e) => Config.自动学习技能书 = ChkAutoBook.Checked, Config.自动学习技能书);
                ChkAutoBook.CheckedChanged += (o, e) =>
                {
                    if (ChkTabPick.Checked)
                        ChkAutoPick.Checked = false;
                };
                y += RowHeight;

                GroupItem.Size = new Size(GroupItem.Size.Width, y);
            }

            #endregion

            #region 天气（A端已隐藏，仅占位）

            private void BuildWeatherGroup()
            {
                CombWeather = new DXComboBox
                {
                    Parent = GroupWeather,
                    Location = new Point(35, 45),
                };

                // A端此处原本有「未选择 / 晴 / 雾 / 燃烧的雾 / 雪 / 花瓣雨 / 雨」等选项，
                // 但全部被注释掉，且 GroupWeather.Visible = false。这里同样不填选项，
                // 待后续阶段接入 WeatherSetting 时再启用。
                GroupWeather.Size = new Size(GroupWar.Size.Width, CombWeather.Size.Height + RowHeight * 2);
            }

            #endregion

            #region 鼠标中键

            private void BuildMouseMiddleGroup()
            {
                int y = 30;

                ChkCallMounts = CreateCheckBox(GroupMouseMiddle, "骑马", ColX, y, (o, e) =>
                {
                    if (ChkCallMounts.Checked)
                        ChkCastingMagic.Checked = !ChkCallMounts.Checked;

                    Config.是否开启鼠标中间按钮自动使用坐骑 = ChkCallMounts.Checked;
                }, Config.是否开启鼠标中间按钮自动使用坐骑);
                y += RowHeight;

                ChkCastingMagic = CreateCheckBox(GroupMouseMiddle, "魔法", ColX, y, (o, e) =>
                {
                    if (ChkCastingMagic.Checked)
                        ChkCallMounts.Checked = !ChkCastingMagic.Checked;

                    Config.是否开启鼠标中间按钮自动使用技能 = ChkCastingMagic.Checked;
                }, Config.是否开启鼠标中间按钮自动使用技能);

                CombMiddleMouse = new DXComboBox
                {
                    Parent = GroupMouseMiddle,
                };

                AddFKeyItems(CombMiddleMouse);
                CombMiddleMouse.SelectedItemChanged += (o, e) =>
                {
                    DXListBoxItem selected = CombMiddleMouse.ListBox.SelectedItem;
                    if (selected?.Item != null)
                        Config.鼠标中间按钮使用F几的技能 = (int)selected.Item;
                };
                CombMiddleMouse.ListBox.SelectItem(Config.鼠标中间按钮使用F几的技能);
                CombMiddleMouse.Size = new Size(50, 18);
                CombMiddleMouse.Location = new Point(ColX + ChkCastingMagic.DisplayArea.Right, y);

                y += RowHeight;
                GroupMouseMiddle.Size = new Size(GroupMouseMiddle.Size.Width, y + 10);
            }

            /// <summary>
            /// 向 ComboBox 填充「空 + F1 ~ F12」，Item 值：空 = 0，F1 = 1 … F12 = 12。
            /// </summary>
            private static void AddFKeyItems(DXComboBox combo)
            {
                new DXListBoxItem
                {
                    Parent = combo.ListBox,
                    Label = { Text = "空" },
                    Item = 0,
                };

                for (int i = 1; i <= 12; i++)
                {
                    new DXListBoxItem
                    {
                        Parent = combo.ListBox,
                        Label = { Text = "F" + i },
                        Item = i,
                    };
                }
            }

            #endregion

            #region 底部按钮与特殊命令

            private void BuildBottomButtons()
            {
                RefreshBag = new DXButton
                {
                    Parent = this,
                    Size = new Size(70, 18),
                    ButtonType = ButtonType.SmallButton,
                    Label = { Text = "刷新包裹" },
                };
                RefreshBag.MouseClick += (o, e) =>
                {
                    if (GameScene.Game == null || GameScene.Game.Observer) return;

                    // A端发的是 SortBagItem，B端等价物是 ItemSort（带 Grid 参数）
                    CEnvir.Enqueue(new C.ItemSort { Grid = GridType.Inventory });
                };

                ReloadConfig = new DXButton
                {
                    Parent = this,
                    Size = new Size(70, 18),
                    ButtonType = ButtonType.SmallButton,
                    Label = { Text = "重置设置" },
                };
                ReloadConfig.MouseClick += (o, e) => { };

                LabCommand = new DXLabel
                {
                    Parent = this,
                    Text = "特殊命令",
                };

                CombCmdBox = new DXComboBox
                {
                    Parent = this,
                    Size = new Size(100, 18),
                };

                new DXListBoxItem
                {
                    Parent = CombCmdBox.ListBox,
                    Label = { Text = "空" },
                    Item = null,
                };

                string[] commands =
                {
                    "@允许召唤",
                    "@队伍召唤",
                    "@宠物技能3",
                    "@宠物技能5",
                    "@宠物技能7",
                    "@宠物技能10",
                    "@宠物技能11",
                    "@宠物技能13",
                    "@宠物技能15",
                    "@允许交易",
                    "@允许加入行会",
                    "@退出行会",
                    "@属性提取",
                    "@摇骰子",
                };

                for (int i = 0; i < commands.Length; i++)
                {
                    new DXListBoxItem
                    {
                        Parent = CombCmdBox.ListBox,
                        Label = { Text = commands[i] },
                        Item = i,
                    };
                }

                CombCmdBox.ListBox.SelectItem(null);

                BtSubmit = new DXButton
                {
                    Parent = this,
                    Size = new Size(50, 16),
                    ButtonType = ButtonType.SmallButton,
                    Label = { Text = "执行" },
                };
                BtSubmit.MouseClick += (o, e) =>
                {
                    if (GameScene.Game == null || GameScene.Game.Observer) return;

                    string text = CombCmdBox?.SelectedLabel?.Text;
                    if (!string.IsNullOrEmpty(text) && text != "空")
                        CEnvir.Enqueue(new C.Chat { Text = text });
                };
            }

            #endregion

            #region 布局

            public override void OnSizeChanged(Size oValue, Size nValue)
            {
                base.OnSizeChanged(oValue, nValue);

                int colWidth = (Size.Width - 40) / 3;

                if (GroupNormal != null)
                {
                    GroupNormal.Location = new Point(10, 0);
                    GroupNormal.Size = new Size(colWidth, GroupNormal.Size.Height);
                }

                if (GroupWar != null && GroupNormal != null)
                {
                    GroupWar.Location = new Point(GroupNormal.Location.X + GroupNormal.Size.Width + 10, 0);
                    GroupWar.Size = new Size(colWidth, GroupWar.Size.Height);
                }

                if (GroupItem != null && GroupWar != null)
                {
                    GroupItem.Location = new Point(GroupWar.Location.X + GroupWar.Size.Width + 10, 0);
                    GroupItem.Size = new Size(colWidth, GroupItem.Size.Height);
                }

                if (GroupWeather != null && GroupWar != null)
                {
                    GroupWeather.Location = new Point(GroupWar.Location.X, GroupWar.Location.Y + GroupWar.Size.Height);
                    GroupWeather.Size = new Size(GroupWar.Size.Width, GroupWeather.Size.Height);
                }

                if (GroupMouseMiddle != null && GroupItem != null)
                {
                    GroupMouseMiddle.Location = new Point(GroupItem.Location.X, GroupItem.Location.Y + GroupItem.Size.Height + 10);
                    GroupMouseMiddle.Size = new Size(GroupItem.Size.Width, GroupMouseMiddle.Size.Height);
                }

                if (RefreshBag != null && GroupMouseMiddle != null)
                {
                    RefreshBag.Location = new Point(GroupMouseMiddle.Location.X + 5,
                        GroupMouseMiddle.Location.Y + GroupMouseMiddle.Size.Height + 10);
                }

                if (RefreshBag == null || ReloadConfig == null)
                    return;

                ReloadConfig.Location = new Point(RefreshBag.Location.X + RefreshBag.Size.Width + 20, RefreshBag.Location.Y);

                if (LabCommand == null || CombCmdBox == null || BtSubmit == null)
                    return;

                int cmdLabelY = RefreshBag.Location.Y + RefreshBag.Size.Height + 20;
                LabCommand.Location = new Point(RefreshBag.Location.X, cmdLabelY);

                int cmdComboY = cmdLabelY + LabCommand.Size.Height + 5;
                CombCmdBox.Location = new Point(RefreshBag.Location.X, cmdComboY);

                BtSubmit.Location = new Point(CombCmdBox.Location.X + CombCmdBox.Size.Width + 5, cmdComboY);
            }

            #endregion

            /// <summary>
            /// A端用一个结构体数组批量描述复选框，便于循环创建。
            /// </summary>
            public struct CHK_ITEM_SET
            {
                public string Name;
                public bool State;
                public EventHandler<EventArgs> Method;

                public CHK_ITEM_SET(string name, bool state, EventHandler<EventArgs> method)
                {
                    Name = name;
                    State = state;
                    Method = method;
                }
            }
        }

        #endregion

        #region 保护标签页

        /// <summary>
        /// 自动喝药的一行：物品格 + 血量% + 魔法% + 启用 + 上下移。
        ///
        /// 与 B端原有的顶层类 <see cref="Client.Scenes.Views.AutoPotionRow"/> 的关键差异：
        /// A端 UI 用**百分比**（0-100），发给服务端时按当前最大 HP/MP 换算成绝对值；
        /// B端原版直接用绝对值（0-50000）。本移植保留 A端的百分比方案，
        /// 并在本地 ini 缓存百分比，避免换装备后 maxHP 变化导致阈值失准。
        ///
        /// 注意：本类是 BigPatchDialog 的嵌套类，与命名空间级的 AutoPotionRow 不冲突。
        /// </summary>
        public sealed class AutoPotionRow : DXControl
        {
            #region Properties

            private ItemInfo _UseItem;
            private int _Index;

            public DXLabel IndexLabel;
            public DXLabel HealthLabel;
            public DXLabel ManaLabel;
            public DXItemCell ItemCell;
            public DXNumberBox HealthTargetBox;
            public DXNumberBox ManaTargetBox;
            public DXCheckBox EnabledCheckBox;
            public DXButton UpButton;
            public DXButton DownButton;

            public ItemInfo UseItem
            {
                get => _UseItem;
                set
                {
                    if (_UseItem == value) return;

                    ItemInfo oldValue = _UseItem;
                    _UseItem = value;

                    OnUseItemChanged(oldValue, value);
                }
            }
            public event EventHandler<EventArgs> UseItemChanged;
            public void OnUseItemChanged(ItemInfo oValue, ItemInfo nValue)
            {
                UseItemChanged?.Invoke(this, EventArgs.Empty);
            }

            public int Index
            {
                get => _Index;
                set
                {
                    if (_Index == value) return;

                    int oldValue = _Index;
                    _Index = value;

                    OnIndexChanged(oldValue, value);
                }
            }
            public event EventHandler<EventArgs> IndexChanged;
            public void OnIndexChanged(int oValue, int nValue)
            {
                IndexChanged?.Invoke(this, EventArgs.Empty);

                IndexLabel.Text = (Index + 1).ToString();
                ItemCell.Slot = Index;

                UpButton.Enabled = Index > 0;
                DownButton.Enabled = Index < Globals.MaxAutoPotionCount - 1;
            }

            #endregion

            public AutoPotionRow()
            {
                Size = new Size(260, 46);
                Border = true;
                BorderColour = Color.FromArgb(198, 166, 99);

                UpButton = new DXButton
                {
                    Index = 44,
                    LibraryFile = LibraryFile.Interface,
                    Location = new Point(5, 5),
                    Parent = this,
                    Enabled = false,
                };
                UpButton.MouseClick += (o, e) => SwapRow(-1);

                DownButton = new DXButton
                {
                    Index = 46,
                    LibraryFile = LibraryFile.Interface,
                    Location = new Point(5, 29),
                    Parent = this,
                };
                DownButton.MouseClick += (o, e) => SwapRow(1);

                // B端 DXItemCell 只有无参构造，尺寸由 GridType.AutoPotion 决定（A端是 new DXItemCell(38, 38)）
                ItemCell = new DXItemCell
                {
                    Parent = this,
                    Location = new Point(20, 5),
                    AllowLink = true,
                    FixedBorder = true,
                    Border = true,
                    GridType = GridType.AutoPotion,
                };

                IndexLabel = new DXLabel
                {
                    Parent = ItemCell,
                    Text = (Index + 1).ToString(),
                    Font = new Font(Config.FontName, CEnvir.FontSize(8F), FontStyle.Italic),
                    IsControl = false,
                    Location = new Point(-2, -1),
                };

                HealthTargetBox = new DXNumberBox
                {
                    Parent = this,
                    Location = new Point(105, 5),
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 100,
                    MinValue = 0,
                    UpButton = { Location = new Point(63, 1) },
                };
                HealthTargetBox.ValueTextBox.ValueChanged += (o, e) => SendUpdate();

                ManaTargetBox = new DXNumberBox
                {
                    Parent = this,
                    Location = new Point(105, 25),
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 100,
                    MinValue = 0,
                    UpButton = { Location = new Point(63, 1) },
                };
                ManaTargetBox.ValueTextBox.ValueChanged += (o, e) => SendUpdate();

                HealthLabel = new DXLabel
                {
                    Parent = this,
                    IsControl = false,
                    Text = "血量%:",
                };
                HealthLabel.Location = new Point(
                    HealthTargetBox.Location.X - HealthLabel.Size.Width,
                    HealthTargetBox.Location.Y + (HealthTargetBox.Size.Height - HealthLabel.Size.Height) / 2);

                ManaLabel = new DXLabel
                {
                    Parent = this,
                    IsControl = false,
                    Text = "魔法%:",
                };
                ManaLabel.Location = new Point(
                    ManaTargetBox.Location.X - ManaLabel.Size.Width,
                    ManaTargetBox.Location.Y + (ManaTargetBox.Size.Height - ManaLabel.Size.Height) / 2);

                EnabledCheckBox = new DXCheckBox
                {
                    Parent = this,
                    Label = { Text = "启用" },
                };
                EnabledCheckBox.CheckedChanged += (o, e) => SendUpdate();
                EnabledCheckBox.Location = new Point(Size.Width - EnabledCheckBox.Size.Width - 5, 5);
            }

            #region Methods

            /// <summary>
            /// 与相邻行交换内容（direction = -1 上移，+1 下移）。
            /// </summary>
            private void SwapRow(int direction)
            {
                DXProtectionTab tab = GameScene.Game?.BigPatchBox?.Protect;
                if (tab == null) return;

                int target = Index + direction;
                if (target < 0 || target >= tab.Rows.Length) return;

                AutoPotionRow other = tab.Rows[target];
                if (other == null) return;

                tab.Updating = true;

                ItemInfo info = ItemCell.QuickInfo;
                long hp = HealthTargetBox.Value;
                long mp = ManaTargetBox.Value;
                bool enabled = EnabledCheckBox.Checked;

                ItemCell.QuickInfo = other.ItemCell.QuickInfo;
                HealthTargetBox.Value = other.HealthTargetBox.Value;
                ManaTargetBox.Value = other.ManaTargetBox.Value;
                EnabledCheckBox.Checked = other.EnabledCheckBox.Checked;

                other.ItemCell.QuickInfo = info;
                other.HealthTargetBox.Value = hp;
                other.ManaTargetBox.Value = mp;
                other.EnabledCheckBox.Checked = enabled;

                tab.Updating = false;

                SendUpdate();
                other.SendUpdate();
            }

            /// <summary>
            /// 把 UI 上的百分比换算成绝对值发给服务端，并把百分比缓存到本地 ini。
            /// </summary>
            public void SendUpdate()
            {
                DXProtectionTab tab = GameScene.Game?.BigPatchBox?.Protect;
                if (tab == null || tab.Updating) return;
                if (GameScene.Game.Observer) return;

                // 最大 HP/MP 无效时不发送，避免算出 0 或负数阈值
                Stats stats = GameScene.Game.User?.Stats;
                if (stats == null) return;

                int maxHP = stats[Stat.Health];
                int maxMP = stats[Stat.Mana];
                if (maxHP <= 0 || maxMP <= 0) return;

                int absoluteHealth = (int)(maxHP * HealthTargetBox.Value / 100f);
                int absoluteMana = (int)(maxMP * ManaTargetBox.Value / 100f);

                if (absoluteHealth < 0 || absoluteHealth > maxHP) return;
                if (absoluteMana < 0 || absoluteMana > maxMP) return;

                CEnvir.Enqueue(new C.AutoPotionLinkChanged
                {
                    Slot = Index,
                    LinkIndex = ItemCell.Item?.Info.Index ?? -1,
                    Enabled = EnabledCheckBox.Checked,
                    Health = absoluteHealth,
                    Mana = absoluteMana,
                });

                tab.SaveLocalSettings();
            }

            #endregion

            #region IDisposable

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);

                if (!disposing) return;

                _UseItem = null;
                UseItemChanged = null;
                _Index = 0;
                IndexChanged = null;

                if (IndexLabel != null)
                {
                    if (!IndexLabel.IsDisposed) IndexLabel.Dispose();
                    IndexLabel = null;
                }

                if (HealthLabel != null)
                {
                    if (!HealthLabel.IsDisposed) HealthLabel.Dispose();
                    HealthLabel = null;
                }

                if (ManaLabel != null)
                {
                    if (!ManaLabel.IsDisposed) ManaLabel.Dispose();
                    ManaLabel = null;
                }

                if (ItemCell != null)
                {
                    if (!ItemCell.IsDisposed) ItemCell.Dispose();
                    ItemCell = null;
                }

                if (HealthTargetBox != null)
                {
                    if (!HealthTargetBox.IsDisposed) HealthTargetBox.Dispose();
                    HealthTargetBox = null;
                }

                if (ManaTargetBox != null)
                {
                    if (!ManaTargetBox.IsDisposed) ManaTargetBox.Dispose();
                    ManaTargetBox = null;
                }

                if (EnabledCheckBox != null)
                {
                    if (!EnabledCheckBox.IsDisposed) EnabledCheckBox.Dispose();
                    EnabledCheckBox = null;
                }

                if (UpButton != null)
                {
                    if (!UpButton.IsDisposed) UpButton.Dispose();
                    UpButton = null;
                }

                if (DownButton != null)
                {
                    if (!DownButton.IsDisposed) DownButton.Dispose();
                    DownButton = null;
                }
            }

            #endregion
        }

        /// <summary>
        /// 保护标签页：8 行自动喝药配置。移植自 A端 BigPatchDialog.cs:3344-3491。
        /// 与服务端的 AutoPotionLinkChanged 包、AutoPotionLink 表对接（B端均已存在，无需改服务端）。
        /// </summary>
        public class DXProtectionTab : DXTab
        {
            public ClientAutoPotionLink[] Links;
            public AutoPotionRow[] Rows;

            /// <summary>
            /// 隐藏基类 DXTab.Updating，避免与服务端同步时的重入判断互相干扰。
            /// TabControl 内部通过 DXTab 类型访问的是基类字段，不受影响。
            /// </summary>
            public new bool Updating;

            public DXProtectionTab()
            {
                Links = new ClientAutoPotionLink[Globals.MaxAutoPotionCount];
                Rows = new AutoPotionRow[Globals.MaxAutoPotionCount];

                for (int i = 0; i < Rows.Length; i++)
                {
                    Rows[i] = new AutoPotionRow
                    {
                        Parent = this,
                        Location = new Point(5, 5 + 50 * i),
                        Index = i,
                    };
                }
            }

            /// <summary>
            /// 服务端下发喝药链后同步到 UI。本地 ini 里已有百分比时优先用本地值
            /// （服务端只存绝对值，换装备后会失准），本地没有才用服务端绝对值反算百分比。
            /// </summary>
            public void UpdateLinks()
            {
                Updating = true;

                LoadLocalSettings();

                foreach (ClientAutoPotionLink link in Links)
                {
                    if (link == null || link.Slot < 0 || link.Slot >= Rows.Length) continue;

                    AutoPotionRow row = Rows[link.Slot];
                    if (row == null) continue;

                    row.ItemCell.QuickInfo = Globals.ItemInfoList.Binding.FirstOrDefault(x => x.Index == link.LinkInfoIndex);

                    if (row.HealthTargetBox.Value == 0 && row.ManaTargetBox.Value == 0)
                    {
                        int maxHP = GameScene.Game?.User?.Stats[Stat.Health] ?? 1;
                        int maxMP = GameScene.Game?.User?.Stats[Stat.Mana] ?? 1;

                        if (maxHP <= 0) maxHP = 1;
                        if (maxMP <= 0) maxMP = 1;

                        long healthPercent = (long)(link.Health * 100f / maxHP);
                        long manaPercent = (long)(link.Mana * 100f / maxMP);

                        row.HealthTargetBox.Value = Math.Max(0, Math.Min(100, healthPercent));
                        row.ManaTargetBox.Value = Math.Max(0, Math.Min(100, manaPercent));
                    }

                    row.EnabledCheckBox.Checked = link.Enabled;
                }

                Updating = false;
            }

            /// <summary>
            /// 最大 HP/MP 变化时重发所有已启用的行，把百分比重新换算成新的绝对值阈值。
            /// </summary>
            public void ResendAllAutoPotionLinks()
            {
                if (Rows == null || GameScene.Game?.User == null) return;

                Stats stats = GameScene.Game.User.Stats;
                if (stats == null || stats[Stat.Health] <= 0 || stats[Stat.Mana] <= 0) return;

                foreach (AutoPotionRow row in Rows)
                {
                    if (row != null && row.EnabledCheckBox.Checked)
                        row.SendUpdate();
                }
            }

            #region 本地百分比缓存

            private const string LocalSettingsFileSuffix = "-AutoPotion.ini";

            private static string LocalSettingsPath
            {
                get
                {
                    string character = GameScene.Game?.User?.Name;
                    if (string.IsNullOrWhiteSpace(character)) return null;

                    // 角色名可能含文件系统非法字符，替换掉
                    foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                        character = character.Replace(c, '_');

                    return $"./{character}{LocalSettingsFileSuffix}";
                }
            }

            public void SaveLocalSettings()
            {
                string path = LocalSettingsPath;
                if (path == null) return;

                try
                {
                    using (System.IO.StreamWriter writer = new System.IO.StreamWriter(path))
                    {
                        for (int i = 0; i < Rows.Length; i++)
                        {
                            if (Rows[i] == null) continue;

                            writer.WriteLine($"[Slot{i}]");
                            writer.WriteLine($"HealthPercent={Rows[i].HealthTargetBox.Value}");
                            writer.WriteLine($"ManaPercent={Rows[i].ManaTargetBox.Value}");
                        }
                    }
                }
                catch
                {
                    // 写失败不影响游戏，忽略
                }
            }

            public void LoadLocalSettings()
            {
                string path = LocalSettingsPath;
                if (path == null || !System.IO.File.Exists(path)) return;

                try
                {
                    int currentSlot = -1;

                    foreach (string line in System.IO.File.ReadAllLines(path))
                    {
                        string trimmed = line.Trim();

                        if (trimmed.StartsWith("[Slot") && trimmed.EndsWith("]"))
                        {
                            if (int.TryParse(trimmed.Substring(5, trimmed.Length - 6), out int slot))
                                currentSlot = slot;

                            continue;
                        }

                        if (currentSlot < 0 || currentSlot >= Rows.Length || Rows[currentSlot] == null) continue;

                        if (trimmed.StartsWith("HealthPercent="))
                        {
                            if (long.TryParse(trimmed.Substring(14), out long value))
                                Rows[currentSlot].HealthTargetBox.Value = value;
                        }
                        else if (trimmed.StartsWith("ManaPercent="))
                        {
                            if (long.TryParse(trimmed.Substring(12), out long value))
                                Rows[currentSlot].ManaTargetBox.Value = value;
                        }
                    }
                }
                catch
                {
                    // 读失败按无本地配置处理
                }
            }

            #endregion
        }

        #endregion
    }
}

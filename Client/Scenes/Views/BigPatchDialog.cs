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
        public DXPlayerHelperTab Helper;
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

            Helper = new DXPlayerHelperTab
            {
                Parent = TabControl,
                Border = true,
                TabButton = { Label = { Text = "辅助", Hint = "游戏相关的一些自定义调整" } },
            };

            Protect = new DXProtectionTab
            {
                Parent = TabControl,
                Border = true,
                TabButton = { Label = { Text = "保护", Hint = "自动使用物品，以及遇到特殊情况的自动应对方案" } },
            };
        }

        #region 外部同步入口

        /// <summary>
        /// 角色登录信息到达时调用：按职业显示对应的自动施法分组框。
        /// 移植自 A端 BigPatchDialog.cs:159-187。
        /// 注意：喝药链的同步已由 <see cref="DXProtectionTab.UpdateLinks"/> 单独负责。
        /// </summary>
        public void UpdateLinks(StartInformation info)
        {
            if (info == null) return;

            if (Helper != null)
            {
                Helper.Warrior.Visible = info.Class == MirClass.Warrior;
                Helper.Wizard.Visible = info.Class == MirClass.Wizard;
                Helper.Taoist.Visible = info.Class == MirClass.Taoist;
                Helper.Assassin.Visible = info.Class == MirClass.Assassin;
            }

            Helper?.UpdateMagic();
        }

        /// <summary>
        /// 角色数据变化（学新技能、升级等）时刷新依赖用户数据的控件。
        /// 移植自 A端 BigPatchDialog.cs:881-885。由 GameScene.UserChanged() 调用。
        /// </summary>
        public void UserChanged()
        {
            Helper?.UpdateMagic();
        }

        /// <summary>
        /// 挂机剩余时间更新。移植自 A端 BigPatchDialog.cs:189-217。
        /// ⚠️ B端没有 S.AutoTimeChanged 包，本方法暂无人调用，
        /// 剩余时间标签会一直显示「0 小时 0 分钟 0 秒」，等阶段 6 补包后接入。
        /// </summary>
        public void OnTimerChanged(long autoTime)
        {
            if (Helper?.TimeLable == null) return;

            TimeSpan timeSpan = new TimeSpan(0, 0, (int)autoTime);
            Helper.TimeLable.Text = $"{timeSpan.Hours + timeSpan.Days * 24} 小时 {timeSpan.Minutes} 分钟 {timeSpan.Seconds} 秒";
        }

        #endregion

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
                Helper = null;
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

        #region 辅助标签页

        /// <summary>
        /// 辅助标签页：按职业分组的自动施法开关 + 自动技能 1/2 + 挂机设置。
        /// 移植自 A端 BigPatchDialog.cs:2106-3022。
        ///
        /// ⚠️ 阶段 4 只做「配置界面」，不含运行逻辑：
        ///   - A端每个开关还会发 C.AutoFightConfChanged 给服务端，本阶段**全部不发送**。
        ///     原因：B端服务端没有 Process(C.AutoFightConfChanged) 重载，收到未处理的包会
        ///     抛 NotImplementedException → BaseConnection 直接 Disconnecting=true 踢下线。
        ///     必须等阶段 6 服务端处理实现后，才能在同一版本里一起开启发送。
        ///   - A端的 AutoSkills()/AutoHealing()/PerformHardPause() 等 14 个运行方法、
        ///     以及 MapControl 里的挂机打怪 AI（约 300 行）属阶段 4b，本阶段不移植。
        ///     因此：勾选这些开关**不会真正自动施法或挂机**，只写 Config 并持久化。
        /// </summary>
        public class DXPlayerHelperTab : DXTab
        {
            #region Properties

            // 职业分组框：同一位置重叠，靠 Visible 只显示当前职业那个
            public DXGroupBox Warrior;
            public DXGroupBox Wizard;
            public DXGroupBox Taoist;
            public DXGroupBox Assassin;

            public DXGroupBox AutoSkill;
            public DXGroupBox Android;

            // 战士
            public DXCheckBox AutoFlamingSword;
            public DXCheckBox AutoDragobRise;
            public DXCheckBox AutoBladeStorm;
            public DXCheckBox AutoSwiftBlade;
            public DXCheckBox AutoThunderStrike;
            public DXCheckBox AutoDefiance;
            public DXCheckBox AutoMight;
            public DXCheckBox AutoReflectDamage;
            public DXCheckBox AutoEndurance;
            public DXCheckBox AutoHalfMoon;
            public DXCheckBox AutoDestructiveSurge;

            // 法师
            public DXCheckBox AutoMagicShield;
            public DXCheckBox AutoRenounce;
            public DXCheckBox AutoThunder;
            public DXCheckBox AutoWizardSkill;

            // 道士
            public DXCheckBox AutoPoisonDust;
            public DXCheckBox AutoAmulet;
            public DXCheckBox AutoCelestial;
            public DXCheckBox AutoTaoistSkill;
            public DXCheckBox AutoStrengthOfFaith;
            public DXCheckBox AutoLifeSteal;
            public DXCheckBox AutoMagicResistance;
            public DXCheckBox AutoResilience;
            public DXCheckBox AutoElementalSuperiority;
            public DXCheckBox AutoBloodLust;
            public DXCheckBox AutoKeepLife;

            // 刺客
            public DXCheckBox AutoFourFlowers;
            public DXCheckBox AutoEvasion;
            public DXCheckBox AutoRagingWind;

            // 自动技能 1 / 2
            public DXComboBox CombSkill1;
            public DXNumberBox NumbSkill1;
            public DXCheckBox AutoSkill_1;
            public DXComboBox CombSkill2;
            public DXNumberBox NumbSkill2;
            public DXCheckBox AutoSkill_2;

            public DXCheckBox ChkPauseAutoSkills;

            // 挂机
            public DXLabel TimeLable;
            public DXCheckBox AndroidPlayer;
            public DXCheckBox AndroidPoisonDust;
            public DXCheckBox AndroidEluded;
            public DXCheckBox AndroidBackCastle;
            public DXComboBox AndroidSkills;
            public DXCheckBox AndroidLongRange;
            public DXNumberBox AndroidCoordX;
            public DXNumberBox AndroidCoordY;
            public DXNumberBox AndroidCoordRange;
            public DXCheckBox AndroidLockRange;
            public DXNumberBox AndroidBackCastleMinPHValue;
            public DXCheckBox AndroidMinPHBackCastle;
            public DXNumberBox AndroidRandomMinPHValue;
            public DXCheckBox AndroidMinPHRandom;
            public DXNumberBox TimeBoxRandom;
            public DXCheckBox ChkAutoRandom;
            public DXNumberBox ExpTimeBoxRandom;
            public DXCheckBox ExpAutoRandom;

            // 挂机统计
            public DXLabel SummerLabelEx, SummerLabelKill, SummerLabelGold, SummerLabelHuntGold;
            public DXLabel SummerLabelExfen, SummerLabelKillfen, SummerLabelGoldfen, SummerLabelHuntGoldfen;
            public DXLabel Guajishijian;

            public int skilledCount;
            public decimal totalExperience, totalGold, totalHuntGold;
            private static DateTime LastTimes = DateTime.Now;

            #endregion

            private const int ColX = 15;       // 复选框左边距
            private const int Col2 = ColX + 120; // 第二列

            public DXPlayerHelperTab()
            {
                BuildClassGroups();
                BuildAutoSkillGroup();
                BuildAndroidGroup();

                // 职业分组框默认全部隐藏，由 BigPatchDialog.UpdateLinks 按职业打开其中一个
                Warrior.Visible = false;
                Wizard.Visible = false;
                Taoist.Visible = false;
                Assassin.Visible = false;

                ChkPauseAutoSkills = CreateCheckBox(this, "暂停以上全部辅助、定时施法", 15, 30,
                    (o, e) => Config.暂停辅助功能 = ChkPauseAutoSkills.Checked,
                    Config.暂停辅助功能);
                ChkPauseAutoSkills.Hint = "勾选后将暂停所有职业技能辅助功能和自动施法，但保留设置";
            }

            #region 分组框创建

            private DXGroupBox CreateGroup(string title)
            {
                DXGroupBox group = new DXGroupBox
                {
                    Parent = this,
                    Size = new Size(120, 32),
                    Location = new Point(10, 0),
                };

                group.Name.Text = title;
                return group;
            }

            #endregion

            #region 职业自动施法

            private void BuildClassGroups()
            {
                Warrior = CreateGroup("战士");
                Wizard = CreateGroup("法师");
                Taoist = CreateGroup("道士");
                Assassin = CreateGroup("刺客");

                int y = 30;

                // ---- 战士 ----
                AutoFlamingSword = CreateCheckBox(Warrior, "自动烈火", ColX, y,
                    (o, e) => Config.自动烈火 = AutoFlamingSword.Checked, Config.自动烈火);
                AutoDragobRise = CreateCheckBox(Warrior, "自动翔空", Col2, y,
                    (o, e) => Config.自动翔空 = AutoDragobRise.Checked, Config.自动翔空);

                y += 25;
                AutoBladeStorm = CreateCheckBox(Warrior, "自动莲月", ColX, y,
                    (o, e) => Config.自动莲月 = AutoBladeStorm.Checked, Config.自动莲月);

                y += 35;
                AutoMight = CreateCheckBox(Warrior, "自动破血", ColX, y,
                    (o, e) => Config.自动破血 = AutoMight.Checked, Config.自动破血);
                AutoDefiance = CreateCheckBox(Warrior, "自动铁布衫", Col2, y,
                    (o, e) => Config.自动铁布衫 = AutoDefiance.Checked, Config.自动铁布衫);

                y += 25;
                AutoEndurance = CreateCheckBox(Warrior, "自动金刚之躯", ColX, y,
                    (o, e) => Config.自动金刚之躯 = AutoEndurance.Checked, Config.自动金刚之躯);
                AutoReflectDamage = CreateCheckBox(Warrior, "自动移花接木", Col2, y,
                    (o, e) => Config.自动移花接木 = AutoReflectDamage.Checked, Config.自动移花接木);

                y += 35;
                AutoDestructiveSurge = CreateCheckBox(Warrior, "智能十方斩", ColX, y,
                    (o, e) => Config.自动十方斩 = AutoDestructiveSurge.Checked, Config.自动十方斩);
                AutoHalfMoon = CreateCheckBox(Warrior, "智能半月弯刀", Col2, y,
                    (o, e) => Config.自动半月弯刀 = AutoHalfMoon.Checked, Config.自动半月弯刀);

                y += 25;
                AutoSwiftBlade = CreateCheckBox(Warrior, "自动快刀斩马", ColX, y,
                    (o, e) => Config.自动快刀斩马 = AutoSwiftBlade.Checked, Config.自动快刀斩马);
                AutoThunderStrike = CreateCheckBox(Warrior, "自动天雷锤", Col2, y,
                    (o, e) => Config.自动天雷锤 = AutoThunderStrike.Checked, Config.自动天雷锤);

                // ---- 法师 ----
                y = 30;
                AutoMagicShield = CreateCheckBox(Wizard, "自动魔法盾", ColX, y,
                    (o, e) => Config.自动魔法盾 = AutoMagicShield.Checked, Config.自动魔法盾);

                y += 25;
                AutoRenounce = CreateCheckBox(Wizard, "自动凝血", ColX, y,
                    (o, e) => Config.自动凝血 = AutoRenounce.Checked, Config.自动凝血);

                y += 25;
                AutoThunder = CreateCheckBox(Wizard, "自动天打雷劈", ColX, y,
                    (o, e) => Config.自动天打雷劈 = AutoThunder.Checked, Config.自动天打雷劈);

                y += 25;
                AutoWizardSkill = CreateCheckBox(Wizard, "自动连续技能", ColX, y,
                    (o, e) => Config.自动法师连续技能 = AutoWizardSkill.Checked, Config.自动法师连续技能);

                // ---- 道士 ----
                y = 30;
                AutoPoisonDust = CreateCheckBox(Taoist, "自动换毒", ColX, y,
                    (o, e) => Config.自动换毒 = AutoPoisonDust.Checked, Config.自动换毒);
                AutoAmulet = CreateCheckBox(Taoist, "自动换符", Col2, y,
                    (o, e) => Config.自动换符 = AutoAmulet.Checked, Config.自动换符);

                y += 25;
                AutoCelestial = CreateCheckBox(Taoist, "自动阴阳盾", ColX, y,
                    (o, e) => Config.自动阴阳盾 = AutoCelestial.Checked, Config.自动阴阳盾);
                AutoTaoistSkill = CreateCheckBox(Taoist, "自动连续技能", Col2, y,
                    (o, e) => Config.自动道士连续技能 = AutoTaoistSkill.Checked, Config.自动道士连续技能);

                y += 25;
                AutoLifeSteal = CreateCheckBox(Taoist, "自动吸星大法", ColX, y,
                    (o, e) => Config.自动吸星大法 = AutoLifeSteal.Checked, Config.自动吸星大法);
                AutoMagicResistance = CreateCheckBox(Taoist, "自动施放幽灵盾", Col2, y,
                    (o, e) => Config.自动施放幽灵盾 = AutoMagicResistance.Checked, Config.自动施放幽灵盾);

                y += 25;
                AutoStrengthOfFaith = CreateCheckBox(Taoist, "有宠物时自动移花接玉", ColX, y,
                    (o, e) => Config.有宠物时自动移花接玉 = AutoStrengthOfFaith.Checked, Config.有宠物时自动移花接玉);

                y += 25;
                AutoResilience = CreateCheckBox(Taoist, "自动施放神圣战甲", ColX, y,
                    (o, e) => Config.自动施放神圣战甲术 = AutoResilience.Checked, Config.自动施放神圣战甲术);

                y += 25;
                AutoBloodLust = CreateCheckBox(Taoist, "自动给宠物施放猛虎强势", ColX, y,
                    (o, e) => Config.自动给宠物施放猛虎强势 = AutoBloodLust.Checked, Config.自动给宠物施放猛虎强势);

                y += 25;
                AutoElementalSuperiority = CreateCheckBox(Taoist, "自动施放强魔震法", ColX, y,
                    (o, e) => Config.自动强魔震法 = AutoElementalSuperiority.Checked, Config.自动强魔震法);

                y += 25;
                AutoKeepLife = CreateCheckBox(Taoist, "生命低于一半时自动施展秒影并恢复血量", ColX, y,
                    (o, e) => Config.自动施展秒影恢复血量 = AutoKeepLife.Checked, Config.自动施展秒影恢复血量);

                // ---- 刺客 ----
                y = 30;
                AutoFourFlowers = CreateCheckBox(Assassin, "自动四花", ColX, y,
                    (o, e) => Config.自动四花 = AutoFourFlowers.Checked, Config.自动四花);

                y += 25;
                AutoEvasion = CreateCheckBox(Assassin, "自动风之闪避", ColX, y,
                    (o, e) => Config.自动风之闪避 = AutoEvasion.Checked, Config.自动风之闪避);

                y += 25;
                AutoRagingWind = CreateCheckBox(Assassin, "自动风之守护", ColX, y,
                    (o, e) => Config.自动风之守护 = AutoRagingWind.Checked, Config.自动风之守护);
            }

            #endregion

            #region 自动技能 1 / 2

            private void BuildAutoSkillGroup()
            {
                AutoSkill = CreateGroup("自动技能");

                int y = 35;

                DXLabel label1 = new DXLabel
                {
                    Parent = AutoSkill,
                    Text = "技能Ⅰ",
                    Hint = "根据定义的时间间隔(秒单位)自动释放技能",
                };
                label1.Location = new Point(ColX, y);

                CombSkill1 = new DXComboBox
                {
                    Parent = AutoSkill,
                    Size = new Size(90, 18),
                    Location = new Point(label1.Location.X + label1.Size.Width + 5, y),
                };
                CombSkill1.SelectedItemChanged += (o, e) => OnAutoSkillSelected(1);

                NumbSkill1 = new DXNumberBox
                {
                    Parent = AutoSkill,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 50000,
                    MinValue = 0,
                    Value = Config.自动技能1多长时间使用一次,
                    UpButton = { Location = new Point(63, 1) },
                };
                NumbSkill1.ValueTextBox.ValueChanged += (o, e) => Config.自动技能1多长时间使用一次 = NumbSkill1.Value;
                NumbSkill1.Location = new Point(CombSkill1.Location.X + CombSkill1.Size.Width + 5, y);

                // A端此复选框手工创建：无文本、只有 Hint（B端 DXCheckBox 需用 Label.Text 设文字，
                // 但这里 A端本来就没有可见文字，故只设 Hint）
                AutoSkill_1 = new DXCheckBox
                {
                    Parent = AutoSkill,
                    Hint = "自动技能①",
                };
                AutoSkill_1.SetSilentState(Config.是否开启自动技能1);
                AutoSkill_1.Location = new Point(NumbSkill1.Location.X + NumbSkill1.Size.Width + 5, y + 2);
                AutoSkill_1.CheckedChanged += (o, e) => Config.是否开启自动技能1 = AutoSkill_1.Checked;

                int y2 = y + 25;

                DXLabel label2 = new DXLabel
                {
                    Parent = AutoSkill,
                    Text = "技能Ⅱ",
                    Hint = "根据定义的时间间隔(秒单位)自动释放技能",
                };
                label2.Location = new Point(ColX, y2);

                CombSkill2 = new DXComboBox
                {
                    Parent = AutoSkill,
                    Size = new Size(90, 18),
                    Location = new Point(label2.Location.X + label2.Size.Width + 5, y2),
                };
                CombSkill2.SelectedItemChanged += (o, e) => OnAutoSkillSelected(2);

                NumbSkill2 = new DXNumberBox
                {
                    Parent = AutoSkill,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 50000,
                    MinValue = 0,
                    Value = Config.自动技能2多长时间使用一次,
                    UpButton = { Location = new Point(63, 1) },
                };
                NumbSkill2.ValueTextBox.ValueChanged += (o, e) => Config.自动技能2多长时间使用一次 = NumbSkill2.Value;
                NumbSkill2.Location = new Point(CombSkill2.Location.X + CombSkill2.Size.Width + 5, y2);

                AutoSkill_2 = new DXCheckBox
                {
                    Parent = AutoSkill,
                    Hint = "自动技能②",
                };
                AutoSkill_2.SetSilentState(Config.是否开启自动技能2);
                AutoSkill_2.Location = new Point(NumbSkill2.Location.X + NumbSkill2.Size.Width + 5, y2 + 2);
                AutoSkill_2.CheckedChanged += (o, e) => Config.是否开启自动技能2 = AutoSkill_2.Checked;

                AutoSkill.Size = new Size(125, y2 + 25 + 5);
            }

            /// <summary>
            /// 选中技能后写入 Config，并把间隔抬到该技能的冷却时间（秒）。
            /// A端逻辑：仅当当前间隔小于技能 CD 时才抬高，不覆盖用户设的更大值。
            /// </summary>
            private void OnAutoSkillSelected(int index)
            {
                DXComboBox combo = index == 1 ? CombSkill1 : CombSkill2;
                DXNumberBox numb = index == 1 ? NumbSkill1 : NumbSkill2;

                if (!(combo.SelectedItem is MagicType magic) || magic == MagicType.None) return;

                foreach (MagicInfo info in Globals.MagicInfoList.Binding)
                {
                    if (info.Magic != magic) continue;

                    if (index == 1)
                        Config.自动技能1 = magic;
                    else
                        Config.自动技能2 = magic;

                    long seconds = info.Delay / 1000;
                    if (numb.Value < seconds)
                        numb.Value = seconds;

                    break;
                }
            }

            #endregion

            #region 挂机

            private void BuildAndroidGroup()
            {
                Android = CreateGroup("挂机");

                int x = ColX;
                int y = 30;

                // 剩余挂机时间
                DXLabel timeTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "剩时间:",
                    Outline = true,
                    Location = new Point(x, y),
                };

                TimeLable = new DXLabel
                {
                    Parent = Android,
                    Text = "0 小时 0 分钟 0 秒",
                    Outline = true,
                    Border = false,
                    ForeColour = Color.Cyan,
                    Location = new Point(timeTitle.Location.X + timeTitle.Size.Width - 8, y),
                };

                AndroidPlayer = CreateCheckBox(Android, "开始挂机", x + 170, y, OnHangupToggled, Config.开始挂机);

                // 注：A端此处会检查 User.AutoTime 与地图 AllowRT 后发包启动挂机 AI。
                // B端没有 User.AutoTime 字段，也没有挂机 AI（阶段 4b），故仅保留地图检查。

                y += 25;
                AndroidPoisonDust = CreateCheckBox(Android, "自动上毒", x, y,
                    (o, e) => Config.自动上毒 = AndroidPoisonDust.Checked, Config.自动上毒);
                AndroidEluded = CreateCheckBox(Android, "自动躲避", x + 85, y,
                    (o, e) => Config.自动躲避 = AndroidEluded.Checked, Config.自动躲避);
                AndroidBackCastle = CreateCheckBox(Android, "死亡回城", x + 170, y,
                    (o, e) => Config.死亡回城 = AndroidBackCastle.Checked, Config.死亡回城);

                y += 25;
                AndroidLongRange = CreateCheckBox(Android, "法系挂机", x, y,
                    (o, e) => Config.是否远战挂机 = AndroidLongRange.Checked, Config.是否远战挂机);

                DXLabel skillTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "技能",
                    Outline = true,
                    Location = new Point(x + 70, y),
                };

                AndroidSkills = new DXComboBox
                {
                    Parent = Android,
                    Size = new Size(120, 18),
                    Location = new Point(x + 70 + skillTitle.Size.Width + 5, y),
                };
                AndroidSkills.SelectedItemChanged += (o, e) =>
                {
                    DXListBoxItem selected = AndroidSkills.ListBox.SelectedItem;

                    if (selected?.Item == null)
                    {
                        // 选择了「不使用技能」
                        Config.远战挂机是否使用技能 = false;
                        Config.挂机自动技能 = MagicType.None;
                    }
                    else
                    {
                        Config.远战挂机是否使用技能 = true;
                        Config.挂机自动技能 = (MagicType)selected.Item;
                    }
                };

                y += 40;
                DXLabel xTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "X坐标:",
                    Outline = true,
                    Location = new Point(x, y),
                };
                AndroidCoordX = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 1024,
                    MinValue = 1,
                    Value = Config.范围挂机坐标.X,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + xTitle.Size.Width, y),
                };
                AndroidCoordX.ValueTextBox.ValueChanged += (o, e) =>
                    Config.范围挂机坐标 = new Point((int)AndroidCoordX.Value, Config.范围挂机坐标.Y);

                DXLabel yTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "Y坐标:",
                    Outline = true,
                    Location = new Point(x + 120, y),
                };
                AndroidCoordY = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 1024,
                    MinValue = 1,
                    Value = Config.范围挂机坐标.Y,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + 120 + yTitle.Size.Width, y),
                };
                AndroidCoordY.ValueTextBox.ValueChanged += (o, e) =>
                    Config.范围挂机坐标 = new Point(Config.范围挂机坐标.X, (int)AndroidCoordY.Value);

                y += 25;
                DXLabel rangeTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "范围 :",
                    Outline = true,
                    Location = new Point(x, y),
                };
                AndroidCoordRange = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 1024,
                    MinValue = 1,
                    Value = Config.范围距离,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + rangeTitle.Size.Width + 5, y),
                };
                AndroidCoordRange.ValueTextBox.ValueChanged += (o, e) => Config.范围距离 = AndroidCoordRange.Value;

                AndroidLockRange = CreateCheckBox(Android, "范围挂机", x + 170, y,
                    (o, e) => Config.范围挂机 = AndroidLockRange.Checked, Config.范围挂机);

                // 回城保护
                y += 50;
                DXLabel hpTitle1 = new DXLabel
                {
                    Parent = Android,
                    Text = "血量低于 % :",
                    Outline = true,
                    Hint = "　　百分比值",
                    Location = new Point(x, y),
                };
                AndroidBackCastleMinPHValue = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 100,
                    MinValue = 1,
                    Value = Config.血量剩下百分之多少时自动回城,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + hpTitle1.Size.Width, y),
                };
                AndroidBackCastleMinPHValue.ValueTextBox.ValueChanged += (o, e) =>
                    Config.血量剩下百分之多少时自动回城 = AndroidBackCastleMinPHValue.Value;

                AndroidMinPHBackCastle = CreateCheckBox(Android, "回城保护", x + 170, y,
                    (o, e) => Config.是否开启回城保护 = AndroidMinPHBackCastle.Checked, Config.是否开启回城保护);

                // 随机保护
                y += 20;
                DXLabel hpTitle2 = new DXLabel
                {
                    Parent = Android,
                    Text = "血量低于 % :",
                    Outline = true,
                    Hint = "　　百分比值",
                    Location = new Point(x, y),
                };
                AndroidRandomMinPHValue = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 100,
                    MinValue = 1,
                    Value = Config.血量剩下百分之多少时自动随机,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + hpTitle2.Size.Width, y),
                };
                AndroidRandomMinPHValue.ValueTextBox.ValueChanged += (o, e) =>
                    Config.血量剩下百分之多少时自动随机 = AndroidRandomMinPHValue.Value;

                AndroidMinPHRandom = CreateCheckBox(Android, "随机保护", x + 170, y,
                    (o, e) => Config.是否开启随机保护 = AndroidMinPHRandom.Checked, Config.是否开启随机保护);

                // 定时随机
                y += 20;
                DXLabel intervalTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "每间隔(秒) :",
                    Outline = true,
                    Location = new Point(x, y),
                };
                TimeBoxRandom = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 10000,
                    MinValue = 5,
                    Value = Config.隔多少秒自动随机一次,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + intervalTitle.Size.Width, y),
                };
                TimeBoxRandom.ValueTextBox.ValueChanged += (o, e) => Config.隔多少秒自动随机一次 = TimeBoxRandom.Value;

                ChkAutoRandom = CreateCheckBox(Android, "自动随机", x + 170, y,
                    (o, e) => Config.是否开启每间隔自动随机 = ChkAutoRandom.Checked, Config.是否开启每间隔自动随机);
                ChkAutoRandom.CheckedChanged += (o, e) =>
                {
                    if (ChkAutoRandom.Checked)
                        ExpAutoRandom.Checked = false;
                };

                // 无经验随机
                y += 20;
                DXLabel expTitle = new DXLabel
                {
                    Parent = Android,
                    Text = "无经验(秒) :",
                    Outline = true,
                    Location = new Point(x, y),
                };
                ExpTimeBoxRandom = new DXNumberBox
                {
                    Parent = Android,
                    Size = new Size(80, 20),
                    ValueTextBox = { Size = new Size(40, 18) },
                    MaxValue = 10000,
                    MinValue = 5,
                    Value = Config.多少秒无经验或者未杀死目标自动随机,
                    UpButton = { Location = new Point(63, 1) },
                    Location = new Point(x + expTitle.Size.Width, y),
                };
                ExpTimeBoxRandom.ValueTextBox.ValueChanged += (o, e) =>
                    Config.多少秒无经验或者未杀死目标自动随机 = ExpTimeBoxRandom.Value;

                ExpAutoRandom = CreateCheckBox(Android, "自动随机", x + 170, y,
                    (o, e) => Config.是否开启指定时间无经验或者未杀死目标自动随机 = ExpAutoRandom.Checked,
                    Config.是否开启指定时间无经验或者未杀死目标自动随机);
                ExpAutoRandom.CheckedChanged += (o, e) =>
                {
                    if (ExpAutoRandom.Checked)
                        ChkAutoRandom.Checked = false;
                };

                BuildStatistics();
            }

            /// <summary>
            /// 「开始挂机」开关。
            /// 阶段 4：只写 Config + 保留地图限制检查，不启动挂机 AI、不发包。
            /// 阶段 4b 需在此处接入 MapControl 的挂机逻辑。
            /// </summary>
            private void OnHangupToggled(object sender, EventArgs e)
            {
                Config.开始挂机 = AndroidPlayer.Checked;

                if (!AndroidPlayer.Checked) return;

                MapInfo mapInfo = GameScene.Game?.MapControl?.MapInfo;
                if (mapInfo != null && !mapInfo.AllowRT)
                {
                    // 不允许挂机的地图，直接撤销勾选（用 SetSilentState 避免重入触发一次多余的「结束挂机」）
                    AndroidPlayer.SetSilentState(false);
                    Config.开始挂机 = false;
                    GameScene.Game.ReceiveChat("目前您在不允许使用自动打怪功能的地图，因此不能挂机", MessageType.System);
                }
            }

            #endregion

            #region 挂机统计

            private void BuildStatistics()
            {
                int x = ColX;

                SummerLabelKill = new DXLabel { Parent = Android, Location = new Point(x, 285), Text = "杀怪数量:0" };
                SummerLabelEx = new DXLabel { Parent = Android, Location = new Point(110, 285), Text = "获得经验:0" };
                SummerLabelHuntGold = new DXLabel { Parent = Android, Location = new Point(x, 305), Text = "获得赏金:0" };
                SummerLabelGold = new DXLabel { Parent = Android, Location = new Point(110, 305), Text = "获得金币:0" };
                SummerLabelKillfen = new DXLabel { Parent = Android, Location = new Point(x, 325), Text = "分钟杀怪:0" };
                SummerLabelExfen = new DXLabel { Parent = Android, Location = new Point(110, 325), Text = "分钟经验:0" };
                SummerLabelHuntGoldfen = new DXLabel { Parent = Android, Location = new Point(x, 345), Text = "分钟赏金:0" };
                SummerLabelGoldfen = new DXLabel { Parent = Android, Location = new Point(110, 345), Text = "分钟金币:0" };
                Guajishijian = new DXLabel { Parent = Android, Location = new Point(x, 365), Text = "统计时间:0 小时 0 分钟 0 秒" };

                DXButton resetButton = new DXButton
                {
                    Parent = Android,
                    Size = new Size(40, 18),
                    Location = new Point(210, 365),
                    ButtonType = ButtonType.SmallButton,
                    Label = { Text = "重置" },
                };
                resetButton.MouseClick += (o, e) => ResetStatistics();
            }

            private void ResetStatistics()
            {
                skilledCount = 0;
                totalExperience = 0;
                totalGold = 0;
                totalHuntGold = 0;

                SummerLabelKill.Text = "杀怪数量:0";
                SummerLabelEx.Text = "获得经验:0";
                SummerLabelHuntGold.Text = "获得赏金:0";
                SummerLabelGold.Text = "获得金币:0";
                SummerLabelKillfen.Text = "分钟杀怪:0";
                SummerLabelExfen.Text = "分钟经验:0";
                SummerLabelHuntGoldfen.Text = "分钟赏金:0";
                SummerLabelGoldfen.Text = "分钟金币:0";
                Guajishijian.Text = "统计时间:0 小时 0 分钟 0 秒";

                LastTimes = DateTime.Now;
            }

            /// <summary>
            /// 由 GameScene 在获得经验时调用。
            /// ⚠️ 阶段 4 尚未接入调用点，统计面板会一直显示 0，等阶段 4b 一起接。
            /// </summary>
            public void GainedExperience(decimal amount)
            {
                TimeSpan elapsed = DateTime.Now - LastTimes;
                int minutes = elapsed.Minutes + elapsed.Hours * 60 + 1;

                Guajishijian.Text = $"统计时间:{elapsed.Hours + elapsed.Days * 24} 小时 {elapsed.Minutes} 分钟 {elapsed.Seconds} 秒";

                totalExperience += amount;
                if (amount <= 0) return;

                skilledCount++;
                SummerLabelKill.Text = $"杀怪数量:{skilledCount:#,##0}";
                SummerLabelKillfen.Text = $"分钟杀怪:{skilledCount / minutes:#,##0}";
                SummerLabelEx.Text = $"获得经验:{totalExperience:#,##0}";
                SummerLabelExfen.Text = $"分钟经验:{totalExperience / minutes:#,##0}";
            }

            /// <summary>由 GameScene 在获得金币时调用（阶段 4b 接入）。</summary>
            public void GainedGold(decimal amount)
            {
                TimeSpan elapsed = DateTime.Now - LastTimes;
                int minutes = elapsed.Minutes + elapsed.Hours * 60 + 1;

                totalGold += amount;
                SummerLabelGold.Text = $"获得金币:{totalGold:#,##0}";
                SummerLabelGoldfen.Text = $"分钟金币:{totalGold / minutes:#,##0}";
            }

            /// <summary>由 GameScene 在获得赏金时调用（阶段 4b 接入）。</summary>
            public void GainedHuntGold(decimal amount)
            {
                TimeSpan elapsed = DateTime.Now - LastTimes;
                int minutes = elapsed.Minutes + elapsed.Hours * 60 + 1;

                totalHuntGold += amount;
                SummerLabelHuntGold.Text = $"获得赏金:{totalHuntGold:#,##0}";
                SummerLabelHuntGoldfen.Text = $"分钟赏金:{totalHuntGold / minutes:#,##0}";
            }

            #endregion

            #region 技能下拉框填充

            /// <summary>
            /// 用当前角色已学技能填充三个下拉框（自动技能1、自动技能2、挂机技能）。
            /// 必须由外部在登录完成 / 技能变化时调用，否则下拉框为空。
            /// 移植自 A端 BigPatchDialog.cs:2938-2991。
            /// </summary>
            public void UpdateMagic()
            {
                if (GameScene.Game?.User == null) return;

                // 先清空，避免重复调用导致选项累积（A端原版没清，会重复追加）
                ClearListBoxItems(CombSkill1);
                ClearListBoxItems(CombSkill2);
                ClearListBoxItems(AndroidSkills);

                new DXListBoxItem
                {
                    Parent = AndroidSkills.ListBox,
                    Label = { Text = "不使用技能" },
                    Item = null,
                };

                foreach (ClientUserMagic magic in GameScene.Game.User.Magics.Values)
                {
                    MagicInfo info = magic.Info;
                    if (info == null) continue;
                    if (info.School == MagicSchool.Passive || info.School == MagicSchool.None) continue;

                    new DXListBoxItem
                    {
                        Parent = CombSkill1.ListBox,
                        Label = { Text = info.Name },
                        Item = info.Magic,
                    };

                    new DXListBoxItem
                    {
                        Parent = CombSkill2.ListBox,
                        Label = { Text = info.Name },
                        Item = info.Magic,
                    };

                    // 开关型/被动型技能不能作为挂机技能（只会切换状态不产生攻击）
                    // 注意：不影响自动技能 1/2 下拉框，定时开关型技能是有意义的
                    if (MapControl.NonAutoSkillMagics.Contains(info.Magic)) continue;

                    new DXListBoxItem
                    {
                        Parent = AndroidSkills.ListBox,
                        Label = { Text = info.Name },
                        Item = info.Magic,
                    };
                }

                CombSkill1.ListBox.SelectItem(Config.自动技能1);
                CombSkill2.ListBox.SelectItem(Config.自动技能2);

                // 旧配置里存的开关型技能无法作为挂机技能，加载时重置为「不使用技能」
                if (Config.挂机自动技能 != MagicType.None && MapControl.NonAutoSkillMagics.Contains(Config.挂机自动技能))
                {
                    Config.远战挂机是否使用技能 = false;
                    Config.挂机自动技能 = MagicType.None;
                }

                if (!Config.远战挂机是否使用技能 || Config.挂机自动技能 == MagicType.None)
                    AndroidSkills.ListBox.SelectItem(null);
                else
                    AndroidSkills.ListBox.SelectItem(Config.挂机自动技能);
            }

            /// <summary>
            /// 清空下拉框的列表项（只销毁 DXListBoxItem，保留 DXListBox 自带的滚动条）。
            ///
            /// ⚠ 绝对不能用 ListBox.Controls.Clear()：
            /// DXListBox 构造时把 ScrollBar 以 Parent = this 挂进了 Controls，
            /// Clear() 会把滚动条一起摘掉，滚动条变成「_Parent 仍指向 ListBox、但不在 Controls 里」的孤儿。
            /// 之后退出游戏销毁控件树时，DXListBox.Dispose 看到 ScrollBar 还没 IsDisposed，
            /// 又去调 ScrollBar.Dispose()，而此刻 parent.Controls 已被置 null，
            /// DXControl.Dispose 里的 parent?.Controls.Remove(this) 就会抛 NullReferenceException，
            /// 被 BaseConnection.Process 重抛后直接崩掉客户端。
            /// </summary>
            private static void ClearListBoxItems(DXComboBox combo)
            {
                if (combo == null) return;

                // 先断开选中引用，避免指向即将被销毁的项（此时项还活着，安全）
                combo.SelectedItem = null;

                DXListBox listBox = combo.ListBox;
                if (listBox == null) return;

                listBox.SelectedItem = null;

                // 快照后再销毁：item.Dispose() 会把自己从 Controls 里移除，不能直接遍历原集合
                foreach (DXControl control in listBox.Controls.ToArray())
                {
                    if (control is DXListBoxItem item && !item.IsDisposed)
                        item.Dispose();
                }

                listBox.UpdateScrollBar();
            }

            #endregion

            #region 布局

            public override void OnSizeChanged(Size oValue, Size nValue)
            {
                base.OnSizeChanged(oValue, nValue);

                int x = 5;
                int y = 5;
                int width = (Size.Width - 15) / 2;

                // 左列：职业分组框（四者重叠，靠 Visible 切换）在上，自动技能在下
                int classHeight = Size.Height - 50 - AutoSkill.Size.Height - 15;

                foreach (DXGroupBox group in new[] { Warrior, Wizard, Taoist, Assassin })
                {
                    if (group == null) continue;

                    group.Size = new Size(width, classHeight);
                    group.Location = new Point(x, y);
                }

                AutoSkill.Size = new Size(width, AutoSkill.Size.Height);
                AutoSkill.Location = new Point(x, y + classHeight);

                if (ChkPauseAutoSkills != null)
                    ChkPauseAutoSkills.Location = new Point(AutoSkill.Location.X + 5, AutoSkill.Location.Y + AutoSkill.Size.Height + 5);

                // 右列：挂机
                if (Android != null)
                {
                    Android.Size = new Size(width, Size.Height - 10);
                    Android.Location = new Point(x + width + 5, y);
                }
            }

            #endregion
        }

        #endregion
    }
}

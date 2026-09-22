using Client.Controls;
using Client.Envir;
using Client.Models;
using Client.Models.Particles;
using Library;
using Library.SystemModels;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using C = Library.Network.ClientPackets;

//Cleaned
namespace Client.Scenes.Views
{
    public sealed partial class MapControl : DXControl
    {
        #region Properties

        public static UserObject User => GameScene.Game.User;

        /// <summary>
        /// 不能作为「挂机自动技能」使用的技能：开关型 / 被动型。
        /// 这些技能释放后只切换状态、不产生攻击，误选会让挂机角色站桩不攻击。
        /// 移植自 A端 MapControl.cs:93-100。
        /// 注意：不影响「自动技能 1/2」下拉框（定时开关型技能是有意义的）。
        /// </summary>
        public static readonly HashSet<MagicType> NonAutoSkillMagics = new HashSet<MagicType>
        {
            MagicType.Swordsmanship, MagicType.SpiritSword, MagicType.WillowDance, MagicType.VineTreeDance,
            MagicType.Thrusting, MagicType.HalfMoon, MagicType.FlamingSword, MagicType.DragonRise,
            MagicType.BladeStorm, MagicType.DemonicRecovery, MagicType.DestructiveSurge, MagicType.Endurance,
            MagicType.FlameSplash,
            MagicType.FullBloom, MagicType.WhiteLotus, MagicType.RedLotus, MagicType.SweetBrier, MagicType.Karma
        };

        #region MapInformation

        public MapInfo MapInfo
        {
            get => _MapInfo;
            set
            {
                if (_MapInfo == value) return;

                MapInfo oldValue = _MapInfo;
                _MapInfo = value;

                OnMapInfoChanged(oldValue, value);
            }
        }
        private MapInfo _MapInfo;

        public InstanceInfo InstanceInfo
        {
            get => _InstanceInfo;
            set
            {
                if (_InstanceInfo == value) return;

                InstanceInfo oldValue = _InstanceInfo;
                _InstanceInfo = value;
                OnInstanceInfoChanged(oldValue, value);
            }
        }
        private InstanceInfo _InstanceInfo;
        public void OnInstanceInfoChanged(InstanceInfo oValue, InstanceInfo nValue)
        {
            MapInfoChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler<EventArgs> MapInfoChanged;
        public void OnMapInfoChanged(MapInfo oValue, MapInfo nValue)
        {
            TextureValid = false;
            LoadMap();

            if (oValue != null)
            {
                if (nValue == null || nValue.Music != oValue.Music)
                    DXSoundManager.Stop(oValue.Music);
            }

            if (nValue != null)
                DXSoundManager.Play(nValue.Music);

            UpdateWeather();
            LLayer.UpdateLights();
            MapInfoChanged?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #region Animation

        public int Animation
        {
            get => _Animation;
            set
            {
                if (_Animation == value) return;

                int oldValue = _Animation;
                _Animation = value;

                OnAnimationChanged(oldValue, value);
            }
        }
        private int _Animation;
        public event EventHandler<EventArgs> AnimationChanged;
        public void OnAnimationChanged(int oValue, int nValue)
        {
            AnimationChanged?.Invoke(this, EventArgs.Empty);
            TextureValid = false;
        }



        #endregion


        #region MouseLocation

        public Point MouseLocation
        {
            get => _MouseLocation;
            set
            {
                if (_MouseLocation == value) return;

                Point oldValue = _MouseLocation;
                _MouseLocation = value;

                OnMouseLocationChanged(oldValue, value);
            }
        }
        private Point _MouseLocation;
        public event EventHandler<EventArgs> MouseLocationChanged;
        public void OnMouseLocationChanged(Point oValue, Point nValue)
        {
            MouseLocationChanged?.Invoke(this, EventArgs.Empty);
            UpdateMapLocation();
        }

        #endregion

        public override void OnSizeChanged(Size oValue, Size nValue)
        {
            base.OnSizeChanged(oValue, nValue);

            if (FLayer != null)
                FLayer.Size = Size;

            if (LLayer != null)
                LLayer.Size = Size;


            OffSetX = Size.Width / 2 / CellWidth;
            OffSetY = Size.Height / 2 / CellHeight;
            PixelOffsetX = (Size.Width - CellWidth) / 2 - OffSetX * CellWidth;
            PixelOffsetY = (Size.Height - CellHeight) / 2 - OffSetY * CellHeight - ManualHeightOffset;
        }

        public MouseButtons MapButtons;
        public Point MapLocation;

        public bool Mining;
        public Point MiningPoint;
        public MirDirection MiningDirection;

        public FishingState FishingState;
        public Point FloatLocation;
        public MirDirection FishingDirection;
        public bool AutoCast;

        public TamingState TamingState;
        public MonsterObject TamingObject;
        public uint TamingObjectID;

        public Floor FLayer;
        public Light LLayer;

        public bool ShadowPixelShaderEnabled { get; set; } = true;

        public Cell[,] Cells;
        public int Width, Height;

        /// <summary>
        /// 本地 A* 寻路器，供挂机 AI 使用。随地图切换（LoadMap）重建。
        /// B 端原生自动寻路是服务端驱动的（AutoPathRoute），没有「给定目标点算路」的能力。
        /// </summary>
        public PathFinder PathFinder { get; private set; }

        public List<DXControl> MapInfoObjects = new List<DXControl>();
        public List<MapObject> Objects = new List<MapObject>();
        public List<MirEffect> Effects = new List<MirEffect>();
        public List<Models.Particles.ParticleEmitter> ParticleEffects = new List<Models.Particles.ParticleEmitter>();

        public const int CellWidth = 48, CellHeight = 32;
        public const int ManualHeightOffset = 34;

        public int ViewRangeX = 12, ViewRangeY = 24;

        public static int OffSetX;
        public static int OffSetY;
        public static int PixelOffsetX;
        public static int PixelOffsetY;


        #endregion

        public MapControl()
        {
            DrawTexture = true;

            BackColour = Color.Empty;

            FLayer = new Floor { Parent = this, Size = Size };
            LLayer = new Light { Parent = this, Location = new Point(-GameScene.Game.Location.X, -GameScene.Game.Location.Y), Size = Size };
        }

        #region Methods

        protected override void OnClearTexture()
        {
            worldNamesValid = false;
            base.OnClearTexture();

            if (!Visible) return;

            DrawBackground();

            if (FLayer.TextureValid)
            {
                RenderTexture floorTexture = FLayer.ControlTexture;
                Rectangle floorSource = new Rectangle(0, 0, FLayer.TextureSize.Width, FLayer.TextureSize.Height);
                RectangleF floorDestination = new RectangleF(0F, 0F, FLayer.TextureSize.Width, FLayer.TextureSize.Height);
                RenderingPipelineManager.DrawTexture(floorTexture, floorSource, floorDestination, Color.White);
            }

            if (Config.DrawEffects)
            {
                foreach (MirEffect ob in Effects)
                {
                    if (ob.DrawType != DrawType.Floor) continue;

                    ob.Draw();
                }
            }

            DrawObjects();

            if (MapObject.MouseObject != null) // && MapObject.MouseObject != MapObject.TargetObject)
                MapObject.MouseObject.DrawBlend();

            if (Config.DrawEffects)
            {
                foreach (MirEffect ob in Effects)
                {
                    if (ob.DrawType != DrawType.Final) continue;

                    ob.Draw();
                }
            }

            RenderingPipelineManager.FlushSprite();

            bool previousBlendEnabled = RenderingPipelineManager.IsBlending();
            float previousBlendRate = RenderingPipelineManager.GetBlendRate();
            BlendMode previousBlendMode = RenderingPipelineManager.GetBlendMode();

            RenderingPipelineManager.SetBlend(true, 1F, BlendMode.LIGHTMAP);

            if (LLayer.TextureValid)
            {
                RenderTexture lightLayerTexture = LLayer.ControlTexture;
                Rectangle lightLayerSource = new Rectangle(0, 0, LLayer.TextureSize.Width, LLayer.TextureSize.Height);
                RectangleF lightLayerDestination = new RectangleF(0F, 0F, LLayer.TextureSize.Width, LLayer.TextureSize.Height);
                RenderingPipelineManager.DrawTexture(lightLayerTexture, lightLayerSource, lightLayerDestination, Color.White);
            }

            RenderingPipelineManager.SetBlend(previousBlendEnabled, previousBlendRate, previousBlendMode);
        }

        private void DrawWorldOverlays()
        {
            RenderingPipelineManager.PushUIScale(GameScene.Game.UIScale);
            try
            {
                DrawWorldNames();

                if (MapObject.MouseObject != null && MapObject.MouseObject.Race != ObjectType.Item)
                {
                    SetWorldOverlayScaleOrigin(MapObject.MouseObject);
                    MapObject.MouseObject.DrawName();
                }

                foreach (MapObject ob in HasGroundItems ? statusOverlayObjects : Objects)
                {
                    SetWorldOverlayScaleOrigin(ob);
                    ob.DrawChat();
                    ob.DrawPoison();
                    ob.DrawHealth();
                }

                if (Config.ShowDamageNumbers)
                    foreach (MapObject ob in HasGroundItems ? statusOverlayObjects : Objects)
                    {
                        SetWorldOverlayScaleOrigin(ob);
                        ob.DrawDamage();
                    }
            }
            finally
            {
                RenderingPipelineManager.PopUIScale();
            }

            if (HasGroundItems && MapLocation.X >= 0 && MapLocation.X < Width && MapLocation.Y >= 0 && MapLocation.Y < Height)
            {
                Cell cell = Cells[MapLocation.X, MapLocation.Y];
                DrawLootFocus(cell);
            }
        }

        private static void SetWorldOverlayScaleOrigin(MapObject ob)
        {
            RenderingPipelineManager.SetUIScaleOrigin(new PointF(ob.DrawX + CellWidth / 2F, ob.DrawY + CellHeight / 2F));
        }

        protected override void DrawControl()
        {
            if (!TextureValid)
                CreateTexture();

            if (!TextureValid || !Config.ColourGrading || MapInfo == null)
            {
                DrawMapTexture();
                return;
            }

            ColourGradePreset grade = GetColourGradePreset();
            RenderingPipelineManager.EnableColourGradeEffect(grade.Exposure, grade.Contrast, grade.Saturation, grade.Tint, grade.TintStrength);

            try
            {
                DrawMapTexture();
            }
            finally
            {
                RenderingPipelineManager.DisableSpriteShaderEffect();
            }
        }

        private void DrawMapTexture()
        {
            if (!ControlTexture.IsValid || TextureSize.Width <= 0 || TextureSize.Height <= 0)
                return;

            RenderingPipelineManager.DrawTexture(
                ControlTexture,
                new Rectangle(Point.Empty, TextureSize),
                new RectangleF(DisplayArea.X, DisplayArea.Y, DisplayArea.Width, DisplayArea.Height),
                IsEnabled ? Color.White : Color.FromArgb(75, 75, 75));
        }

        private ColourGradePreset GetColourGradePreset()
        {
            switch (MapInfo.Light)
            {
                case LightSetting.Night:
                    return new ColourGradePreset(0.04F, 1.025F, 0.94F, Color.FromArgb(226, 238, 255), 0.11F);
                case LightSetting.Twilight:
                    return new ColourGradePreset(0.02F, 1.035F, 0.98F, Color.FromArgb(255, 242, 228), 0.06F);
                case LightSetting.Light:
                    return new ColourGradePreset(0.015F, 1.035F, 1.02F, Color.White, 0F);
            }

            switch (GameScene.Game.TimeOfDay)
            {
                case TimeOfDay.Dawn:
                    return new ColourGradePreset(0.025F, 1.03F, 1F, Color.FromArgb(255, 241, 224), 0.07F);
                case TimeOfDay.Dusk:
                    return new ColourGradePreset(0.015F, 1.04F, 0.98F, Color.FromArgb(255, 235, 218), 0.08F);
                case TimeOfDay.Night:
                    return new ColourGradePreset(0.04F, 1.025F, 0.94F, Color.FromArgb(226, 238, 255), 0.11F);
                default:
                    return new ColourGradePreset(0.015F, 1.035F, 1.02F, Color.White, 0F);
            }
        }

        private readonly struct ColourGradePreset
        {
            public float Exposure { get; }
            public float Contrast { get; }
            public float Saturation { get; }
            public Color Tint { get; }
            public float TintStrength { get; }

            public ColourGradePreset(float exposure, float contrast, float saturation, Color tint, float tintStrength)
            {
                Exposure = exposure;
                Contrast = contrast;
                Saturation = saturation;
                Tint = tint;
                TintStrength = tintStrength;
            }
        }

        public override void Draw()
        {
            if (!IsVisible || Size.Width == 0 || Size.Height == 0) return;

            FLayer.CheckTexture();
            LLayer.CheckTexture();

            OnBeforeDraw();

            DrawControl();

            DrawWorldOverlays();

            DrawBorder();
            OnAfterDraw();
        }

        private void DrawBackground()
        {
            if (MapInfo.Background <= 0) return;

            if (!CEnvir.LibraryList.TryGetValue(LibraryFile.Background, out MirLibrary library)) return;

            if (!library.TryGetTexture(MapInfo.Background, ImageType.Image, out _, out var texture, out var sourceRectangle)) return;

            PresentTexture(texture, sourceRectangle, Parent, DisplayArea, Color.White, this, 0, 0, 1F);
        }

        private void DrawMapImage(MirLibrary library, int index, float x, float y, float opacity = 1F)
        {
            if (!ShadowPixelShaderEnabled)
            {
                library.Draw(index, x, y, Color.White, false, opacity, ImageType.Image);
                return;
            }

            RenderingPipelineManager.EnableSolidShadowFillEffect(GameScene.ShadowOpacity);
            library.Draw(index, x, y, Color.White, false, opacity, ImageType.Image);
        }

        private void DrawMapImageBlend(MirLibrary library, int index, float x, float y, float rate)
        {
            if (!ShadowPixelShaderEnabled)
            {
                library.DrawBlend(index, x, y, Color.White, false, rate, ImageType.Image);
                return;
            }

            RenderingPipelineManager.EnableSolidShadowFillEffect(GameScene.ShadowOpacity);
            library.DrawBlend(index, x, y, Color.White, false, rate, ImageType.Image);
        }

        private void DrawObjects()
        {
            int minX = Math.Max(0, User.CurrentLocation.X - OffSetX - 4), maxX = Math.Min(Width - 1, User.CurrentLocation.X + OffSetX + 4);
            int minY = Math.Max(0, User.CurrentLocation.Y - OffSetY - 4), maxY = Math.Min(Height - 1, User.CurrentLocation.Y + OffSetY + 25);

            bool useLootRows = HasGroundItems;
            if (useLootRows)
            {
                objectRows.Reset(minY, maxY);
                effectRows.Reset(minY, maxY);
                foreach (MapObject ob in Objects)
                {
                    if (ob is ItemObject item && !item.IsPileRepresentative) continue;
                    objectRows.Add(ob.RenderY, ob);
                }
                if (Config.DrawEffects)
                    foreach (MirEffect effect in Effects)
                    {
                        if (effect.DrawType != DrawType.Object) continue;
                        if (effect is LootEffect loot && !loot.Representative) continue;
                        if (effect.MapTarget.IsEmpty && effect.Target != null)
                        {
                            if (effect.Target != User) effectRows.Add(effect.Target.RenderY, effect);
                        }
                        else effectRows.Add(effect.MapTarget.Y, effect);
                    }
            }

            for (int y = minY; y <= maxY; y++)
            {
                int drawY = (y - User.CurrentLocation.Y + OffSetY + 1) * CellHeight + PixelOffsetY - User.MovingOffSet.Y - User.ShakeScreenOffset.Y;

                for (int x = minX; x <= maxX; x++)
                {
                    int drawX = (x - User.CurrentLocation.X + OffSetX) * CellWidth + PixelOffsetX - User.MovingOffSet.X - User.ShakeScreenOffset.X;

                    Cell cell = Cells[x, y];

                    if (!cell.LibrariesLoaded)
                    {
                        if (Libraries.KROrder.TryGetValue(cell.MiddleFile, out LibraryFile file) && file != LibraryFile.Tilesc)
                            CEnvir.LibraryList.TryGetValue(file, out cell.MiddleLibrary);

                        if (Libraries.KROrder.TryGetValue(cell.FrontFile, out file) && file != LibraryFile.Tilesc)
                            CEnvir.LibraryList.TryGetValue(file, out cell.FrontLibrary);

                        cell.LibrariesLoaded = true;
                    }

                    if (cell.MiddleLibrary != null)
                    {
                        int index = cell.MiddleImage - 1;

                        bool blend = false;
                        bool animated = cell.MiddleAnimationFrame > 1 && cell.MiddleAnimationFrame < 255;
                        if (animated)
                        {
                            blend = cell.MiddleAnimationBlend;
                            index += Animation % cell.MiddleAnimationCount;
                        }

                        Size s = cell.MiddleLibrary.GetSize(index);
                        bool cellSized = IsCellSized(s);

                        if (!cellSized)
                        {
                            if (!blend)
                                DrawMapImage(cell.MiddleLibrary, index, drawX, drawY - s.Height);
                            else
                                DrawMapImageBlend(cell.MiddleLibrary, index, drawX, drawY - s.Height, 0.5F);
                        }
                        else if (animated)
                        {
                            if (!blend)
                                DrawMapImage(cell.MiddleLibrary, index, drawX, drawY - CellHeight);
                            else
                                DrawMapImageBlend(cell.MiddleLibrary, index, drawX, drawY - CellHeight, 0.5F);
                        }
                    }

                    if (cell.FrontLibrary != null)
                    {
                        int index = cell.FrontImage - 1;

                        bool blend = false;
                        bool animated = cell.FrontAnimationFrame > 1 && cell.FrontAnimationFrame < 255;
                        if (animated)
                        {
                            blend = cell.FrontAnimationBlend;
                            int frameCount = cell.FrontAnimationCount;
                            if (frameCount > 0)
                            {
                                index += Animation % frameCount;
                            }
                        }

                        Size s = cell.FrontLibrary.GetSize(index);
                        bool cellSized = IsCellSized(s);

                        if (!cellSized)
                        {
                            if (!blend)
                                DrawMapImage(cell.FrontLibrary, index, drawX, drawY - s.Height);
                            else
                                DrawMapImageBlend(cell.FrontLibrary, index, drawX, drawY - s.Height, 0.5F);
                        }
                        else if (animated)
                        {
                            if (!blend)
                                DrawMapImage(cell.FrontLibrary, index, drawX, drawY - CellHeight);
                            else
                                DrawMapImageBlend(cell.FrontLibrary, index, drawX, drawY - CellHeight, 0.5F);
                        }
                    }
                }

                RenderingPipelineManager.DisableSpriteShaderEffect();

                if (useLootRows)
                {
                    foreach (MapObject ob in objectRows[y])
                        ob.Draw();
                    if (Config.DrawEffects)
                        foreach (MirEffect ob in effectRows[y])
                            ob.Draw();
                }
                else
                {
                    // Keep the original small-scene path free of loot preparation.
                    foreach (MapObject ob in Objects)
                        if (ob.RenderY == y)
                            ob.Draw();
                    if (Config.DrawEffects)
                        foreach (MirEffect ob in Effects)
                        {
                            if (ob.DrawType != DrawType.Object) continue;
                            if (ob.MapTarget.IsEmpty && ob.Target != null)
                            {
                                if (ob.Target.RenderY == y && ob.Target != User)
                                    ob.Draw();
                            }
                            else if (ob.MapTarget.Y == y)
                                ob.Draw();
                        }
                }

            }

            if (User.Opacity == 1f)
            {
                float oldOpacity = MapObject.User.Opacity;
                MapObject.User.Opacity = 0.65F;

                MapObject.User.DrawPlayer(false, false);

                MapObject.User.Opacity = oldOpacity;
            }

            if (Config.DrawEffects)
            {
                if (Config.DrawParticles)
                {
                    foreach (var ob in ParticleEffects)
                    {
                        ob.Draw();
                    }
                }

                foreach (MirEffect ob in Effects)
                {
                    if (ob.DrawType != DrawType.Object || !ob.MapTarget.IsEmpty || ob.Target != User) continue;

                    ob.Draw();
                }
            }
        }

        private static bool IsCellSized(Size size)
        {
            return (size.Width == CellWidth && size.Height == CellHeight) ||
                   (size.Width == CellWidth * 2 && size.Height == CellHeight * 2);
        }

        private void LoadMap()
        {
            PathFinder = null;      // 地图切换时旧寻路器立即失效
            ResetAndroidState();    // 旧地图的挂机路径同样作废

            try
            {
                var path = Path.Combine(Config.MapPath, MapInfo.FileName + ".map");

                if (!File.Exists(path)) return;

                using (MemoryStream mStream = new MemoryStream(File.ReadAllBytes(path)))
                using (BinaryReader reader = new BinaryReader(mStream))
                {
                    mStream.Seek(22, SeekOrigin.Begin);
                    Width = reader.ReadInt16();
                    Height = reader.ReadInt16();

                    mStream.Seek(28, SeekOrigin.Begin);

                    Cells = new Cell[Width, Height];
                    for (int x = 0; x < Width; x++)
                        for (int y = 0; y < Height; y++)
                            Cells[x, y] = new Cell();

                    for (int x = 0; x < Width / 2; x++)
                        for (int y = 0; y < Height / 2; y++)
                        {
                            Cells[(x * 2), (y * 2)].BackFile = reader.ReadByte();
                            Cells[(x * 2), (y * 2)].BackImage = reader.ReadUInt16();
                        }

                    for (int x = 0; x < Width; x++)
                        for (int y = 0; y < Height; y++)
                        {
                            byte flag = reader.ReadByte();
                            Cells[x, y].MiddleAnimationFrame = reader.ReadByte();

                            byte value = reader.ReadByte();
                            Cells[x, y].FrontAnimationFrame = value == 255 ? 0 : value;
                            Cells[x, y].FrontAnimationFrame &= 0x8F; //Probably a Blend Flag

                            Cells[x, y].FrontFile = reader.ReadByte();
                            Cells[x, y].MiddleFile = reader.ReadByte();

                            Cells[x, y].MiddleImage = reader.ReadUInt16() + 1;
                            Cells[x, y].FrontImage = reader.ReadUInt16() + 1;

                            mStream.Seek(3, SeekOrigin.Current);

                            Cells[x, y].Light = (byte)(reader.ReadByte() & 0x0F) * 2;

                            mStream.Seek(1, SeekOrigin.Current);

                            Cells[x, y].Flag = ((flag & 0x01) != 1) || ((flag & 0x02) != 2);
                        }

                    // 地图数据就绪后才创建寻路器
                    if (Width > 0 && Height > 0)
                        PathFinder = new PathFinder(this);
                }
            }
            catch (Exception ex)
            {
                CEnvir.SaveException(ex);
            }

            foreach (MapObject ob in Objects)
                if (ob.CurrentLocation.X < Width && ob.CurrentLocation.Y < Height)
                    Cells[ob.CurrentLocation.X, ob.CurrentLocation.Y].AddObject(ob);
        }

        private void UpdateWeather()
        {
            GameScene.Game.MapControl.ParticleEffects.Clear();

            if (Config.DrawWeather)
            {
                var point = new Point((Size.Width) / 2, (Size.Height) / 2);

                if (GameScene.Game.MapControl.MapInfo.Weather.HasFlag(Weather.Rain))
                {
                    var emitter = (ParticleEmitter)Activator.CreateInstance(typeof(Rain), point);
                    GameScene.Game.MapControl.ParticleEffects.Add(emitter);
                }

                if (GameScene.Game.MapControl.MapInfo.Weather.HasFlag(Weather.Snow))
                {
                    var emitter = (ParticleEmitter)Activator.CreateInstance(typeof(Snow), point);
                    GameScene.Game.MapControl.ParticleEffects.Add(emitter);
                }

                if (GameScene.Game.MapControl.MapInfo.Weather.HasFlag(Weather.Fog))
                {
                    var emitter = (ParticleEmitter)Activator.CreateInstance(typeof(Fog), point);
                    GameScene.Game.MapControl.ParticleEffects.Add(emitter);
                }

                if (GameScene.Game.MapControl.MapInfo.Weather.HasFlag(Weather.Lightning))
                {
                    var emitter = (ParticleEmitter)Activator.CreateInstance(typeof(Lightning), point);
                    GameScene.Game.MapControl.ParticleEffects.Add(emitter);
                }
            }
        }

        public override void OnMouseMove(MouseEventArgs e)
        {
            e = GameScene.Game.ToWorldMouseEventArgs(e);
            base.OnMouseMove(e);
            MouseLocation = e.Location;
        }
        public override void OnMouseDown(MouseEventArgs e)
        {
            e = GameScene.Game.ToWorldMouseEventArgs(e);
            base.OnMouseDown(e);

            if (GameScene.Game.Observer) return;

            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                GameScene.Game.CancelAutoPath();

            MapButtons |= e.Button;

            if (e.Button == MouseButtons.Right)
            {
                if (Config.RightClickDeTarget && MapObject.TargetObject?.Race == ObjectType.Monster)
                    MapObject.TargetObject = null;
            }

            if (e.Button == MouseButtons.Middle)
            {
                // 鼠标中键：自动使用坐骑 / 自动使用技能（移植自 A 端 MapControl.OnMouseDown）
                if (Config.是否开启鼠标中间按钮自动使用坐骑)
                {
                    if (CEnvir.Now < User.NextActionTime || User.ActionQueue.Count > 0) return;
                    if (CEnvir.Now < User.ServerTime) return;

                    User.ServerTime = CEnvir.Now.AddSeconds(5);
                    CEnvir.Enqueue(new C.Mount());
                }
                else if (Config.是否开启鼠标中间按钮自动使用技能)
                {
                    switch (Config.鼠标中间按钮使用F几的技能)
                    {
                        case 1: GameScene.Game.UseMagic(SpellKey.Spell01); break;
                        case 2: GameScene.Game.UseMagic(SpellKey.Spell02); break;
                        case 3: GameScene.Game.UseMagic(SpellKey.Spell03); break;
                        case 4: GameScene.Game.UseMagic(SpellKey.Spell04); break;
                        case 5: GameScene.Game.UseMagic(SpellKey.Spell05); break;
                        case 6: GameScene.Game.UseMagic(SpellKey.Spell06); break;
                        case 7: GameScene.Game.UseMagic(SpellKey.Spell07); break;
                        case 8: GameScene.Game.UseMagic(SpellKey.Spell08); break;
                        case 9: GameScene.Game.UseMagic(SpellKey.Spell09); break;
                        case 10: GameScene.Game.UseMagic(SpellKey.Spell10); break;
                        case 11: GameScene.Game.UseMagic(SpellKey.Spell11); break;
                        case 12: GameScene.Game.UseMagic(SpellKey.Spell12); break;
                    }
                }
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            DXItemCell cell = DXItemCell.SelectedCell;
            if (cell != null)
            {
                MapButtons &= ~e.Button;

                if (cell.GridType == GridType.Belt)
                {
                    cell.QuickInfo = null;
                    cell.QuickItem = null;
                    DXItemCell.SelectedCell = null;

                    ClientBeltLink link = GameScene.Game.BeltBox.Links[cell.Slot];
                    CEnvir.Enqueue(new C.BeltLinkChanged { Slot = link.Slot, LinkIndex = link.LinkInfoIndex, LinkItemIndex = link.LinkItemIndex }); //Update serve
                    return;
                }

                if (cell.GridType == GridType.AutoPotion)
                {
                    cell.QuickInfo = null;
                    cell.QuickItem = null;
                    DXItemCell.SelectedCell = null;

                    GameScene.Game.BigPatchBox?.Protect?.Rows[cell.Slot]?.SendUpdate();
                    return;
                }

                if ((cell.Item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked || (cell.GridType != GridType.Inventory && cell.GridType != GridType.CompanionInventory))
                {
                    DXItemCell.SelectedCell = null;
                    return;
                }

                DXItemAmountWindow window = new DXItemAmountWindow("丢弃物品", cell.Item);

                window.ConfirmButton.MouseClick += (o, a) =>
                {
                    if (window.Amount <= 0) return;

                    CEnvir.Enqueue(new C.ItemDrop
                    {
                        Link = new CellLinkInfo { GridType = cell.GridType, Slot = cell.Slot, Count = window.Amount }
                    });

                    cell.Locked = true;
                };

                DXItemCell.SelectedCell = null;
                return;
            }

            if (GameScene.Game.CurrencyPickedUp != null)
            {
                MapButtons &= ~e.Button;

                int index = GameScene.Game.CurrencyPickedUp.Info.Index;

                DXItemAmountWindow window = new DXItemAmountWindow("丢弃物品", new ClientUserItem(GameScene.Game.CurrencyPickedUp.Info.DropItem, GameScene.Game.CurrencyPickedUp.Amount));

                window.ConfirmButton.MouseClick += (o, a) =>
                {
                    if (window.Amount <= 0) return;

                    CEnvir.Enqueue(new C.CurrencyDrop
                    {
                        CurrencyIndex = index,
                        Amount = window.Amount
                    });
                };

                GameScene.Game.CurrencyPickedUp = null;
                return;
            }

            if (TryPickUpSelectedLoot()) return;

            if (CanAttack(MapObject.MouseObject))
            {
                MapObject.TargetObject = MapObject.MouseObject;

                #region Shuriken
                if (!Functions.InRange(MapObject.TargetObject.CurrentLocation, User.CurrentLocation, Globals.MagicRange) && User.LibraryWeaponShape == Globals.ShurikenLibraryWeaponShape)
                {

                    GameScene.Game.OutputTime = CEnvir.Now.AddSeconds(1);
                    GameScene.Game.ReceiveChat(string.Format(CEnvir.Language.GameSceneThrowTooFar, "投掷武器"), MessageType.Hint);

                    Stop();

                    return;
                }

                if (User.Horse == HorseType.None && User.LibraryWeaponShape == Globals.ShurikenLibraryWeaponShape && MapObject.TargetObject != null &&
                    (MapObject.TargetObject.Race == ObjectType.Monster || MapObject.TargetObject.Race == ObjectType.Player))
                {

                    if (CEnvir.Now < User.AttackTime)
                    {
                        Stop();

                        return;
                    }


                    if (CEnvir.Now >= User.AttackTime)
                    {
                        MapObject.User.AttemptAction(
                            new ObjectAction
                            (
                                      MirAction.RangeAttack,
                                      Functions.DirectionFromPoint(MapObject.User.CurrentLocation, MapObject.TargetObject.CurrentLocation),
                                      MapObject.User.CurrentLocation,
                                      MapObject.TargetObject.ObjectID, //Ranged Attack Target ID;
                                      MagicType.Shuriken
                            ));

                        Stop();

                        return;
                    }


                }

                #endregion Shukiran

                if (MapObject.MouseObject.Race == ObjectType.Monster && ((MonsterObject)MapObject.MouseObject).MonsterInfo.AI >= 0) //Check if AI is guard
                {
                    MapObject.MagicObject = MapObject.TargetObject;
                    GameScene.Game.FocusObject = MapObject.MouseObject;
                }
                return;
            }

            MapObject.TargetObject = null;
            GameScene.Game.FocusObject = null;
            //GameScene.Game.OldTargetObjectID = 0;
        }

        private void Stop()
        {
            Functions.Move(Location, MapObject.TargetObject.Direction, 0);
            GameScene.Game.FocusObject = null;
            MapObject.TargetObject = null;
        }
        public override void OnMouseClick(MouseEventArgs e)
        {
            e = GameScene.Game.ToWorldMouseEventArgs(e);
            base.OnMouseClick(e);
            switch (e.Button)
            {
                case MouseButtons.Left:
                    if (GameScene.Game.Observer) return;

                    GameScene.Game.AutoRun = false;
                    if (MapObject.MouseObject == null) return;
                    NPCObject npc = MapObject.MouseObject as NPCObject;
                    if (npc != null)
                    {
                        if (CEnvir.Now <= GameScene.Game.NPCTime) return;

                        GameScene.Game.NPCTime = CEnvir.Now.AddSeconds(1);

                        CEnvir.Enqueue(new C.NPCCall { ObjectID = npc.ObjectID });
                    }
                    break;
                case MouseButtons.Right:
                    GameScene.Game.AutoRun = false;

                    if (User.CurrentAction == MirAction.Standing)
                        GameScene.Game.CanRun = false;

                    if (!CEnvir.Ctrl) return;

                    PlayerObject player = MapObject.MouseObject as PlayerObject;

                    if (player == null || player == MapObject.User) return;
                    if (CEnvir.Now <= GameScene.Game.InspectTime && player.ObjectID == GameScene.Game.InspectID) return;

                    GameScene.Game.InspectTime = CEnvir.Now.AddMilliseconds(2500);
                    GameScene.Game.InspectID = player.ObjectID;
                    CEnvir.Enqueue(new C.Inspect { Index = player.CharacterIndex });
                    break;
            }
        }

        public void CheckCursor()
        {
            MapObject deadObject = null, itemObject = null;

            for (int d = 0; d < 4; d++)
            {
                for (int y = MapLocation.Y - d; y <= MapLocation.Y + d; y++)
                {
                    if (y >= Height) continue;
                    if (y < 0) break;
                    for (int x = MapLocation.X - d; x <= MapLocation.X + d; x++)
                    {
                        if (x >= Width) continue;
                        if (x < 0) break;

                        List<MapObject> list = Cells[x, y].Objects;
                        if (list == null) continue;

                        MapObject cellSelect = null;
                        for (int i = list.Count - 1; i >= 0; i--)
                        {
                            MapObject ob = list[i];

                            if (ob == MapObject.User || ob.Race == ObjectType.Spell || ((x != MapLocation.X || y != MapLocation.Y) && !ob.MouseOver(MouseLocation))) continue;

                            if (ob.Dead || (ob.Race == ObjectType.Monster && ((MonsterObject)ob).CompanionObject != null))
                            {
                                if (deadObject == null)
                                    deadObject = ob;
                                continue;
                            }
                            if (ob.Race == ObjectType.Item)
                            {
                                if (itemObject == null)
                                    itemObject = ob;
                                continue;
                            }
                            if (x == MapLocation.X && y == MapLocation.Y && !ob.MouseOver(MouseLocation))
                            {
                                if (cellSelect == null)
                                    cellSelect = ob;
                            }
                            else
                            {
                                MapObject.MouseObject = ob;
                                return;
                            }
                        }

                        if (cellSelect != null)
                        {
                            MapObject.MouseObject = cellSelect;
                            return;
                        }
                    }
                }
            }

            if (itemObject is ItemObject item)
                itemObject = GetSelectedLoot(item);

            MapObject mouseOb = deadObject ?? itemObject;

            if (mouseOb == null)
            {
                if ((User.CurrentLocation.X == MapLocation.X && User.CurrentLocation.Y == MapLocation.Y) || User.MouseOver(MouseLocation))
                    mouseOb = User;
            }


            MapObject.MouseObject = mouseOb;
        }

        public void ProcessInput()
        {
            if (GameScene.Game.Observer) return;

            if (User.Dead || (User.Poison & PoisonType.Paralysis) == PoisonType.Paralysis || (User.Poison & PoisonType.Containment) == PoisonType.Containment || User.Buffs.Any(x => x.Type == BuffType.DragonRepulse)) return; //Para or Frozen??

            if (User.MagicAction != null)
            {
                if (CEnvir.Now < MapObject.User.NextActionTime || MapObject.User.ActionQueue.Count != 0) return;

                MapObject.User.AttemptAction(User.MagicAction);
                User.MagicAction = null;
                Mining = false;
            }

            // 挂机 AI：开启后本帧完全由 AI 接管，跳过后续鼠标输入处理
            // （否则鼠标位置会参与方向计算，导致角色乱走）。
            //
            // ⚠ 必须放在下面的「贴身物理攻击」之前：
            // 那个分支会直接 return，导致挂机 AI 一整帧不执行，
            // 法师被怪贴脸时只做普通攻击、永远放不出技能。
            if (Config.开始挂机)
            {
                _lastAutoAndroidState = true;
                ProcessAndroid();
                return;
            }

            bool haselementalhurricane = MapObject.User.VisibleBuffs.ContainsKey(BuffType.ElementalHurricane);

            if (!haselementalhurricane && MapObject.TargetObject != null && !MapObject.TargetObject.Dead && ((MapObject.TargetObject.Race == ObjectType.Monster && string.IsNullOrEmpty(MapObject.TargetObject.PetOwner)) || (CEnvir.Shift || Config.免SHIFT)))
            {
                if (Functions.Distance(MapObject.TargetObject.CurrentLocation, MapObject.User.CurrentLocation) == 1 && CEnvir.Now > User.AttackTime && User.Horse == HorseType.None)
                {
                    MapObject.User.AttemptAction(new ObjectAction(
                        MirAction.Attack,
                        Functions.DirectionFromPoint(MapObject.User.CurrentLocation, MapObject.TargetObject.CurrentLocation),
                        MapObject.User.CurrentLocation,
                        0, //Ranged Attack Target ID
                        MagicType.None,
                        Element.None));
                    return;
                }
            }

            if (_lastAutoAndroidState)
            {
                // 挂机 开→关：清掉本地寻路残留，避免角色继续自己走
                _lastAutoAndroidState = false;
                ResetAndroidState();
            }

            //If  auto run

            MirDirection direction = MouseDirection(), best;

            if (GameScene.Game.AutoRun && !haselementalhurricane)
            {
                if (!GameScene.Game.MoveFrame || (User.Poison & PoisonType.WraithGrip) == PoisonType.WraithGrip) return;
                Run(direction);
                return;
            }

            if (MouseControl == this)
            {
                switch (MapButtons)
                {
                    case MouseButtons.Left:
                        Mining = false;

                        if ((CEnvir.Shift || Config.免SHIFT) && MapObject.TargetObject == null)
                        {
                            if (FishingState != FishingState.None) return;
                            if (TamingState != TamingState.None) return;

                            if (CEnvir.Now > User.AttackTime && User.Horse == HorseType.None && !haselementalhurricane)
                                MapObject.User.AttemptAction(new ObjectAction(MirAction.Attack, direction, MapObject.User.CurrentLocation, 0, MagicType.None, Element.None));

                            return;
                        }

                        ClientUserItem weap = GameScene.Game.Equipment[(int)EquipmentSlot.Weapon];
                        ClientUserItem armour = GameScene.Game.Equipment[(int)EquipmentSlot.Armour];

                        if (CEnvir.Alt)
                        {
                            if (haselementalhurricane) return;
                            if (User.Horse != HorseType.None) return;
                            if (FishingState != FishingState.None) return;
                            if (TamingState != TamingState.None) return;

                            if (weap?.Info.ItemEffect == ItemEffect.FishingRod && armour?.Info.ItemEffect == ItemEffect.FishingRobe)
                            {
                                FloatLocation = MapLocation;
                                FishingDirection = Functions.DirectionFromPoint(MapObject.User.CurrentLocation, FloatLocation);

                                var distance = Functions.Distance(MapObject.User.CurrentLocation, FloatLocation);

                                if (Functions.FishingZone(Globals.FishingInfoList, MapInfo, Width, Height, FloatLocation) != null && Functions.ValidFishingDistance(distance, User.Stats[Stat.ThrowDistance]))
                                {
                                    FishingState = FishingState.Cast;
                                    break;
                                }
                            }
                            else if (weap?.Info.ItemEffect == ItemEffect.TamingLasso && MapObject.TargetObject is MonsterObject target && !target.Dead && target.MonsterInfo.AI == 135)
                            {
                                var distance = Functions.Distance(MapObject.User.CurrentLocation, target.CurrentLocation);

                                if (distance <= Globals.TamingDistance)
                                {
                                    TamingObject = target;
                                    TamingState = TamingState.Cast;
                                    break;
                                }
                            }
                            else
                            {
                                MapObject.User.AttemptAction(new ObjectAction(MirAction.Harvest, direction, MapObject.User.CurrentLocation));
                                return;
                            }
                        }

                        if (FishingState != FishingState.None)
                        {
                            FishingState = FishingState.Cancel;
                            break;
                        }

                        if (TamingState != TamingState.None)
                        {
                            TamingState = TamingState.Cancel;
                            break;
                        }

                        if (MapLocation == MapObject.User.CurrentLocation)
                        {
                            if (CEnvir.Now <= GameScene.Game.PickUpTime) return;

                            CEnvir.Enqueue(new C.PickUp());
                            GameScene.Game.PickUpTime = CEnvir.Now.AddMilliseconds(250);

                            return;
                        }

                        if (MapObject.MouseObject != null && MapObject.MouseObject.Race != ObjectType.Item && !MapObject.MouseObject.Dead) break;

                        if (!haselementalhurricane && MapInfo.CanMine && weap != null && weap.Info.ItemEffect == ItemEffect.PickAxe)
                        {
                            MiningPoint = Functions.Move(User.CurrentLocation, direction);

                            if (MiningPoint.X >= 0 && MiningPoint.Y >= 0 && MiningPoint.X < Width && MiningPoint.Y < Height && Cells[MiningPoint.X, MiningPoint.Y].Flag)
                            {
                                Mining = true;
                                break;
                            }
                        }

                        if (!CanMove(direction, 1) || haselementalhurricane)
                        {
                            best = MouseDirectionBest(direction, 1);

                            if (best == direction)
                            {
                                if (direction != User.Direction)
                                    MapObject.User.AttemptAction(new ObjectAction(MirAction.Standing, direction, MapObject.User.CurrentLocation));
                                return;
                            }

                            direction = best;
                        }

                        if (!haselementalhurricane && GameScene.Game.MoveFrame && (User.Poison & PoisonType.WraithGrip) != PoisonType.WraithGrip)
                            MapObject.User.AttemptAction(new ObjectAction(MirAction.Moving, direction, Functions.Move(MapObject.User.CurrentLocation, direction), 1, MagicType.None));
                        return;
                    case MouseButtons.Right:
                        Mining = false;

                        if (FishingState != FishingState.None)
                        {
                            FishingState = FishingState.Cancel;
                            break;
                        }

                        if (TamingState != TamingState.None)
                        {
                            TamingState = TamingState.Cancel;
                            break;
                        }

                        if (MapObject.MouseObject is PlayerObject && MapObject.MouseObject != MapObject.User && CEnvir.Ctrl) break;

                        if (!GameScene.Game.MoveFrame || (User.Poison & PoisonType.WraithGrip) == PoisonType.WraithGrip) break;

                        if (Functions.InRange(MapLocation, MapObject.User.CurrentLocation, 2) || haselementalhurricane)
                        {
                            if (direction != User.Direction)
                                MapObject.User.AttemptAction(new ObjectAction(MirAction.Standing, direction, MapObject.User.CurrentLocation));
                            return;
                        }

                        Run(direction);

                        return;
                }
            }

            if (Mining)
            {
                ClientUserItem weap = GameScene.Game.Equipment[(int)EquipmentSlot.Weapon];

                if (MapInfo.CanMine && weap != null && (weap.CurrentDurability > 0 || weap.Info.Durability == 0) && weap.Info.ItemEffect == ItemEffect.PickAxe &&
                    MiningPoint.X >= 0 && MiningPoint.Y >= 0 && MiningPoint.X < Width && MiningPoint.Y < Height && Cells[MiningPoint.X, MiningPoint.Y].Flag &&
                    Functions.Distance(MiningPoint, MapObject.User.CurrentLocation) == 1 && User.Horse == HorseType.None)
                {
                    if (CEnvir.Now > User.AttackTime)
                    {
                        MapObject.User.AttemptAction(new ObjectAction(
                            MirAction.Mining,
                            Functions.DirectionFromPoint(MapObject.User.CurrentLocation, MiningPoint),
                            MapObject.User.CurrentLocation,
                            false));
                    }
                }
                else
                {
                    Mining = false;
                }
            }

            if (FishingState != FishingState.None || AutoCast)
            {
                if (CEnvir.Now > User.AttackTime)
                {
                    if (FishingState == FishingState.Reel)
                    {
                        FishingState = FishingState.None;
                    }
                    else
                    {
                        if (AutoCast)
                        {
                            FishingState = FishingState.Cast;
                            AutoCast = false;
                        }

                        MapObject.User.AttemptAction(new ObjectAction(MirAction.Fishing, FishingDirection, MapObject.User.CurrentLocation, FishingState, FloatLocation, MapObject.User.FishFound));
                    }
                }

                return;
            }

            if (TamingState != TamingState.None)
            {
                if (MapObject.TargetObject != null)
                {
                    TamingObjectID = MapObject.TargetObject.ObjectID;
                    direction = Functions.DirectionFromPoint(MapObject.User.CurrentLocation, MapObject.TargetObject.CurrentLocation);
                }

                if (CEnvir.Now > User.AttackTime)
                {
                    MapObject.User.AttemptAction(new ObjectAction(MirAction.Taming, direction, MapObject.User.CurrentLocation, TamingState, TamingObjectID));
                }

                return;
            }

            if (MapObject.TargetObject == null || MapObject.TargetObject.Dead) return;
            if ((MapObject.TargetObject.Race == ObjectType.Player || !string.IsNullOrEmpty(MapObject.TargetObject.PetOwner)) && (!CEnvir.Shift && !Config.免SHIFT)) return;
            if (Functions.InRange(MapObject.TargetObject.CurrentLocation, MapObject.User.CurrentLocation, 1)) return;

            direction = Functions.DirectionFromPoint(MapObject.User.CurrentLocation, MapObject.TargetObject.CurrentLocation);

            if (!CanMove(direction, 1) || haselementalhurricane)
            {
                best = DirectionBest(direction, 1, MapObject.TargetObject.CurrentLocation);

                if (best == direction)
                {
                    if (direction != User.Direction)
                        MapObject.User.AttemptAction(new ObjectAction(MirAction.Standing, direction, MapObject.User.CurrentLocation));
                    return;
                }
                direction = best;
            }

            if (!haselementalhurricane && GameScene.Game.MoveFrame && (User.Poison & PoisonType.WraithGrip) != PoisonType.WraithGrip)
                MapObject.User.AttemptAction(new ObjectAction(MirAction.Moving, direction, Functions.Move(MapObject.User.CurrentLocation, direction), 1, MagicType.None));
        }

        #region 挂机 AI（阶段 4b）

        // ---- 参数 ----
        private const int AndroidSearchRange = 25;   // 选怪 / 追击的最大距离
        private const int AndroidDetectRange = 9;    // 视为「已进入战斗」的距离（对应 A 端 SHORT_DISTANCE_DETECTION_RANGE）
        private const int AndroidAttackRange = 1;    // 近战攻击距离
        private const int AndroidWanderRadius = 12;  // 非「范围挂机」时的游走半径
        private const int AndroidAdjacentRange = 2;  // 这个距离内有怪就先打，不去捡东西
        private const int AndroidPickupRange = 10;   // 挂机时能「发现」战利品的半径

        private bool _lastAutoAndroidState;
        private DateTime _androidRepathTime;
        private DateTime _androidWanderTime;
        private DateTime _nextAutoSkillTime;
        private List<Node> _androidPath;
        private ItemObject _androidLootTarget;
        private DateTime _androidLootStartTime;
        private DateTime _androidLootCooldown;

        /// <summary>挂机关闭时清理本地寻路状态。</summary>
        private void ResetAndroidState()
        {
            _androidPath = null;
            _androidLootTarget = null;
            _androidLootStartTime = default;
            _androidLootCooldown = default;
            _androidRepathTime = default;
            _androidWanderTime = default;
        }

        /// <summary>
        /// 挂机 AI 主入口（每帧一次）。调用方保证 User 存活且未麻痹 / 冰冻。
        ///
        /// 流程：选怪 → 贴身则交由上方的攻击分支处理 → 远程则放技能 → 否则追击。
        /// 与 A 端的差异：A 端把挂机逻辑散落在 ProcessInput 多个分支里，
        /// 这里收敛成单一入口，便于维护。
        /// </summary>
        private void ProcessAndroid()
        {
            if (User == null || MapInfo == null || Width <= 0 || Height <= 0) return;

            // 换到不允许挂机的地图时自动关闭（开关时已检查过一次，但地图会在挂机中途变化）
            if (!MapInfo.AllowRT)
            {
                Config.开始挂机 = false;
                GameScene.Game.BigPatchBox?.Helper?.AndroidPlayer?.SetSilentState(false);
                GameScene.Game.ReceiveChat("当前地图不允许挂机，已自动关闭", MessageType.System);
                return;
            }

            // 有怪贴身时不去捡东西（否则会站着挨打）
            bool combatNearby = FindNearestMonster(AndroidAdjacentRange) != null;

            // 地上有该捡的战利品、且没有怪贴身 → 先捡完再打下一只
            if (!combatNearby && Config.快速自动拾取 && TryAndroidLoot())
                return;

            // 道士挂机：宝宝没了就补召
            if (TrySummonPet()) return;

            MapObject target = MapObject.TargetObject;

            // 目标失效（死亡 / 不可攻击 / 跑太远 / 出了范围挂机区域）→ 丢弃重选
            if (target != null && (target.Dead
                || target.Race != ObjectType.Monster      // 挂机只打怪，不追玩家
                || !GameScene.Game.CanAttackTarget(target)
                || Functions.Distance(User.CurrentLocation, target.CurrentLocation) > AndroidSearchRange
                || !InAndroidRange(target.CurrentLocation)))
                target = null;

            if (target == null)
                target = SelectMonster();

            // 换了目标（或从游走切到战斗）→ 作废旧路径，避免继续朝上一个点走
            if (target != MapObject.TargetObject)
            {
                _androidPath = null;
                _androidRepathTime = default;
            }

            MapObject.TargetObject = target;

            if (target == null)
            {
                // 没怪 → 在挂机范围内游走
                _androidPath = null;
                WanderAndroid();
                return;
            }

            int distance = Functions.Distance(User.CurrentLocation, target.CurrentLocation);

            if (distance <= AndroidAttackRange)
            {
                // 远程职业被贴身 → 先退一步（放风筝），退不动再考虑输出。
                //
                // ⚠ 只认「自动躲避」这一个开关：
                // Config 里的配置是**全局**的（存 Zircon.ini，不分角色），
                // 若把「法系挂机」也算进条件，玩家用法师勾过之后切到战士仍然会一直后退。
                // 再加一层职业限制：只有远程职业才放风筝，近战（战士/刺客）勾了也不后退。
                if (Config.自动躲避 && IsRangedClass(User.Class) && TryStepAway(target)) return;

                // 贴身：技能优先（法师/道士被怪贴脸也要放技能），放不出来再普通物理攻击
                if (CanCastAutoSkill() && TryAutoSkill()) return;

                AndroidAttack(target);
                return;
            }

            // 远程挂机：进入射程后优先放技能（技能不可用则继续追击）
            // 同样加职业限制：Config 是全局的，战士勾过「法系挂机」不该停在中距离发呆
            if (Config.是否远战挂机 && IsRangedClass(User.Class)
                && Functions.InRange(target.CurrentLocation, User.CurrentLocation, AndroidDetectRange))
            {
                if (TryAutoSkill())
                    return;
            }

            MoveTowardTarget(target);
        }

        /// <summary>朝目标移动：优先用 A* 路径，路径不可用时退化为「朝目标走一步」。</summary>
        private void MoveTowardTarget(MapObject target)
        {
            if (!GameScene.Game.MoveFrame || (User.Poison & PoisonType.WraithGrip) == PoisonType.WraithGrip) return;

            int distance = Functions.Distance(User.CurrentLocation, target.CurrentLocation);

            // 近距离直接走一步，避免每 600ms 做一次 A*（近战追击时绝大多数帧都走这条）
            if (distance > 3)
            {
                if (PathFinder != null && CEnvir.Now >= _androidRepathTime)
                {
                    Point approach = AndroidApproachPoint(target.CurrentLocation);
                    _androidPath = PathFinder.FindPath(User.CurrentLocation, approach);
                    _androidRepathTime = CEnvir.Now.AddMilliseconds(600);
                }

                if (FollowAndroidPath())
                    return;
            }

            StepToward(target.CurrentLocation);
        }

        /// <summary>朝指定坐标移动（拾取用；物品格不阻挡，可直接作为寻路终点）。</summary>
        private void MoveTowardPoint(Point destination)
        {
            if (!GameScene.Game.MoveFrame || (User.Poison & PoisonType.WraithGrip) == PoisonType.WraithGrip) return;

            int distance = Functions.Distance(User.CurrentLocation, destination);

            if (distance > 3)
            {
                if (PathFinder != null && CEnvir.Now >= _androidRepathTime)
                {
                    _androidPath = PathFinder.FindPath(User.CurrentLocation, destination);
                    _androidRepathTime = CEnvir.Now.AddMilliseconds(600);
                }

                if (FollowAndroidPath())
                    return;
            }

            StepToward(destination);
        }

        /// <summary>返回范围内最近的、可攻击的怪物（用于判断是否该放弃拾取先打）。</summary>
        private MapObject FindNearestMonster(int range)
        {
            if (Objects == null) return null;

            MapObject best = null;
            int bestDistance = range + 1;

            foreach (MapObject ob in Objects)
            {
                if (ob == null || ob.Dead || ob == User) continue;
                if (ob.Race != ObjectType.Monster) continue;
                if (!string.IsNullOrEmpty(ob.PetOwner)) continue;
                if (!GameScene.Game.CanAttackTarget(ob)) continue;

                int distance = Functions.Distance(User.CurrentLocation, ob.CurrentLocation);

                if (distance <= 0 || distance >= bestDistance) continue;

                bestDistance = distance;
                best = ob;
            }

            return best;
        }

        /// <summary>
        /// 挂机时的「战利品优先」：
        /// 找到范围内该捡的物品就清掉当前目标、走过去（或原地等 PickupItems 发拾取包）。
        /// 返回 true 表示本帧已被拾取流程占用。
        ///
        /// 之所以需要它：挂机选怪半径是 25 格，杀完一只后若附近还有怪，
        /// AI 会立刻扑向下一只，地上的战利品就永远捡不到了。
        /// </summary>
        private bool TryAndroidLoot()
        {
            BigPatchDialog box = GameScene.Game?.BigPatchBox;

            if (box == null) return false;

            // 刚放弃过一轮拾取（捡不起来），冷却期内正常打怪
            if (CEnvir.Now < _androidLootCooldown) return false;

            ItemObject loot = box.FindPickableItem(User.CurrentLocation, AndroidPickupRange);

            if (loot == null || !InAndroidRange(loot.CurrentLocation))
            {
                _androidLootTarget = null;
                return false;
            }

            // 换了拾取目标 → 作废旧路径，避免朝上一只怪/上一个物品走
            if (!ReferenceEquals(loot, _androidLootTarget))
            {
                _androidLootTarget = loot;
                _androidLootStartTime = CEnvir.Now;
                _androidPath = null;
                _androidRepathTime = default;
            }
            else if (CEnvir.Now > _androidLootStartTime.AddSeconds(6))
            {
                // 同一件东西 6 秒都没捡起来（背包满 / 服务端拒绝）→ 放弃它，
                // 冷却 10 秒继续打怪，避免角色永久站桩。
                _androidLootTarget = null;
                _androidLootCooldown = CEnvir.Now.AddSeconds(10);
                return false;
            }

            MapObject.TargetObject = null;

            int pickupRadius = User.Stats[Stat.PickUpRadius];

            if (Functions.Distance(User.CurrentLocation, loot.CurrentLocation) <= pickupRadius)
            {
                // 已在拾取范围内：站住等 PickupItems() 把包发出去
                _androidPath = null;
                return true;
            }

            MoveTowardPoint(loot.CurrentLocation);

            return true;
        }

        /// <summary>沿 _androidPath 走一步。返回 true 表示本帧已被移动占用。</summary>
        private bool FollowAndroidPath()
        {
            if (_androidPath == null || _androidPath.Count == 0) return false;

            // 丢弃已经到达的节点（含起点）
            while (_androidPath.Count > 0 && User.CurrentLocation == _androidPath[0].Location)
                _androidPath.RemoveAt(0);

            if (_androidPath.Count == 0)
            {
                _androidPath = null;
                return false;
            }

            MirDirection dir = Functions.DirectionFromPoint(User.CurrentLocation, _androidPath[0].Location);

            if (!CanMove(dir, 1))
            {
                // 路径被新出现的阻挡打断 → 丢弃，下一帧重算
                _androidPath = null;
                _androidRepathTime = default;
                return false;
            }

            if (!GameScene.Game.MoveFrame || (User.Poison & PoisonType.WraithGrip) == PoisonType.WraithGrip)
                return true;

            MapObject.User.AttemptAction(new ObjectAction(
                MirAction.Moving, dir, Functions.Move(User.CurrentLocation, dir, 1), 1, MagicType.None));

            return true;
        }

        /// <summary>朝指定坐标走一步，被挡时尝试相邻方向。</summary>
        private void StepToward(Point location)
        {
            if (!GameScene.Game.MoveFrame || (User.Poison & PoisonType.WraithGrip) == PoisonType.WraithGrip) return;

            MirDirection dir = Functions.DirectionFromPoint(User.CurrentLocation, location);

            if (!CanMove(dir, 1))
            {
                MirDirection best = DirectionBest(dir, 1, location);

                if (best == dir)
                {
                    if (dir != User.Direction)
                        MapObject.User.AttemptAction(new ObjectAction(MirAction.Standing, dir, User.CurrentLocation));
                    return;
                }

                dir = best;
            }

            MapObject.User.AttemptAction(new ObjectAction(
                MirAction.Moving, dir, Functions.Move(User.CurrentLocation, dir, 1), 1, MagicType.None));
        }

        /// <summary>无怪时在挂机范围内随机游走（范围挂机以设定坐标为中心，否则以当前位置为中心）。</summary>
        private void WanderAndroid()
        {
            if (CEnvir.Now < _androidWanderTime) return;
            _androidWanderTime = CEnvir.Now.AddSeconds(1.0);

            Point center;
            int radius;

            if (Config.范围挂机)
            {
                center = Config.范围挂机坐标;
                radius = (int)Config.范围距离;
            }
            else
            {
                center = User.CurrentLocation;
                radius = AndroidWanderRadius;
            }

            if (radius < 1) radius = 1;

            int cx = Math.Min(Math.Max(center.X, 0), Width - 1);
            int cy = Math.Min(Math.Max(center.Y, 0), Height - 1);

            int minX = Math.Max(cx - radius, 0);
            int maxX = Math.Min(cx + radius + 1, Width);
            int minY = Math.Max(cy - radius, 0);
            int maxY = Math.Min(cy + radius + 1, Height);

            if (maxX <= minX || maxY <= minY) return;

            Point destination = new Point(CEnvir.Random.Next(minX, maxX), CEnvir.Random.Next(minY, maxY));

            if (PathFinder != null)
            {
                _androidPath = PathFinder.FindPath(User.CurrentLocation, destination);
                _androidRepathTime = CEnvir.Now.AddSeconds(2.0);

                if (FollowAndroidPath())
                    return;
            }

            // 寻路不可用（地图未加载 / 目标不可达）→ 直接朝目标方向走一步
            StepToward(destination);
        }

        /// <summary>选择最近的、可攻击的、位于挂机范围内的怪物。</summary>
        private MapObject SelectMonster()
        {
            if (Objects == null) return null;

            MapObject best = null;
            int bestDistance = int.MaxValue;

            foreach (MapObject ob in Objects)
            {
                if (ob == null || ob.Dead || ob == User) continue;
                if (ob.Race != ObjectType.Monster) continue;            // 只选怪（CanAttackTarget 对玩家也返回 true）
                if (!GameScene.Game.CanAttackTarget(ob)) continue;      // 已含 AI<0 / 不可见判断
                if (!string.IsNullOrEmpty(ob.PetOwner)) continue;
                if (!InAndroidRange(ob.CurrentLocation)) continue;

                int distance = Functions.Distance(User.CurrentLocation, ob.CurrentLocation);

                if (distance <= 0) continue;   // 与自己重叠的怪打不到
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = ob;
            }

            return bestDistance <= AndroidSearchRange ? best : null;
        }

        /// <summary>坐标是否在「范围挂机」的矩形范围内（未开启范围挂机时恒为 true）。</summary>
        private bool InAndroidRange(Point location)
        {
            if (!Config.范围挂机) return true;

            int range = (int)Config.范围距离;

            return Math.Abs(location.X - Config.范围挂机坐标.X) <= range
                && Math.Abs(location.Y - Config.范围挂机坐标.Y) <= range;
        }

        /// <summary>
        /// 求目标的「可站立邻格」。
        /// 怪物自身占据的格子 Cell.Blocking() 为 true，A* 无法以它为终点，
        /// 因此退而求其次：取目标周围离自己最近的一个空格作为寻路终点。
        /// </summary>
        private Point AndroidApproachPoint(Point target)
        {
            if (EmptyCell(target)) return target;

            MirDirection dir = Functions.DirectionFromPoint(target, User.CurrentLocation);
            Point candidate = Functions.Move(target, dir, 1);

            if (EmptyCell(candidate)) return candidate;

            for (int i = 1; i < 8; i++)
            {
                candidate = Functions.Move(target, Functions.ShiftDirection(dir, i), 1);

                if (EmptyCell(candidate)) return candidate;
            }

            return target;
        }

        /// <summary>
        /// 尝试释放「挂机自动技能」。
        /// true  = 本帧行动已被技能流程占用（调用方不要再追击 / 平砍）；
        /// false = 技能不可用（未配置 / 未学会 / 是开关型技能），调用方应回退到普通攻击。
        /// </summary>
        /// <summary>
        /// 放一个指定技能（不看「挂机自动技能」配置），用于召唤等辅助行为。
        /// 返回 true 表示已经发出施法动作。
        /// </summary>
        private bool TryCastMagic(MagicType type)
        {
            if (type == MagicType.None) return false;

            ClientUserMagic magic = GameScene.Game.GetMagic(type);

            if (magic == null) return false;                 // 未学会
            if (CEnvir.Now < magic.NextCast) return false;   // 冷却中
            if (magic.Cost > User.CurrentMP) return false;   // 蓝不够

            if (MapObject.User.MagicAction != null) return false;
            if (MapObject.User.ActionQueue != null && MapObject.User.ActionQueue.Count > 0) return false;

            GameScene.Game.UseMagic(type);
            return true;
        }

        /// <summary>
        /// 道士挂机：场上没有自己的宝宝时自动召唤（先骷髅、后神兽）。
        /// 其它职业直接返回 false。
        /// </summary>
        private bool TrySummonPet()
        {
            if (User.Class != MirClass.Taoist) return false;

            // 场上已有自己的宝宝 → 不用召
            if (Objects != null)
            {
                foreach (MapObject ob in Objects)
                {
                    if (ob == null || ob.Dead) continue;
                    if (ob.Race != ObjectType.Monster) continue;
                    if (ob.PetOwner != User.Name) continue;

                    return false;
                }
            }

            return TryCastMagic(MagicType.SummonSkeleton)
                || TryCastMagic(MagicType.SummonShinsu);
        }

        /// <summary>远程职业（法师/道士）才会放风筝，近战不后退。</summary>
        private static bool IsRangedClass(MirClass cls)
        {
            return cls == MirClass.Wizard || cls == MirClass.Taoist;
        }

        /// <summary>
        /// 朝「远离威胁」的方向走一步（远程职业放风筝）。
        /// 只走一格，且不会走出范围挂机的区域；找不到更好的格子就返回 false。
        /// </summary>
        private bool TryStepAway(MapObject threat)
        {
            if (threat == null) return false;
            if (!GameScene.Game.MoveFrame) return false;
            if ((User.Poison & PoisonType.WraithGrip) == PoisonType.WraithGrip) return false;

            int currentDistance = Functions.Distance(User.CurrentLocation, threat.CurrentLocation);

            MirDirection best = MirDirection.Up;
            int bestDistance = currentDistance;

            for (MirDirection dir = MirDirection.Up; dir <= MirDirection.UpLeft; dir++)
            {
                if (!CanMove(dir, 1)) continue;

                Point next = Functions.Move(User.CurrentLocation, dir, 1);

                if (!EmptyCell(next)) continue;
                if (!InAndroidRange(next)) continue;

                int distance = Functions.Distance(next, threat.CurrentLocation);

                if (distance <= bestDistance) continue;

                bestDistance = distance;
                best = dir;
            }

            if (bestDistance <= currentDistance) return false;   // 退无可退（被逼到角落）

            MapObject.User.AttemptAction(new ObjectAction(
                MirAction.Moving, best, Functions.Move(User.CurrentLocation, best, 1), 1, MagicType.None));

            return true;
        }

        /// <summary>
        /// 当前该用哪个挂机技能：
        /// 周围怪 ≥2 且配了群攻技能（已学会、未被黑名单排除）→ 用群攻，否则用单攻技能。
        /// </summary>
        private MagicType PickAutoSkillType()
        {
            MagicType group = Config.挂机自动技能2;

            if (group != MagicType.None
                && !NonAutoSkillMagics.Contains(group)
                && CountNearbyMonsters(AndroidDetectRange) >= 2)
            {
                ClientUserMagic groupMagic = GameScene.Game.GetMagic(group);

                // 群攻技能冷却中 / 蓝不够 → 退回单攻技能，避免干等
                if (groupMagic != null && CEnvir.Now >= groupMagic.NextCast && groupMagic.Cost <= User.CurrentMP)
                    return group;
            }

            return Config.挂机自动技能;
        }

        /// <summary>统计范围内的可攻击怪物数量（不含他人宝宝）。</summary>
        private int CountNearbyMonsters(int range)
        {
            if (Objects == null) return 0;

            int count = 0;

            foreach (MapObject ob in Objects)
            {
                if (ob == null || ob.Dead || ob == User) continue;
                if (ob.Race != ObjectType.Monster) continue;
                if (!string.IsNullOrEmpty(ob.PetOwner)) continue;
                if (!GameScene.Game.CanAttackTarget(ob)) continue;
                if (Functions.Distance(User.CurrentLocation, ob.CurrentLocation) > range) continue;

                count++;
            }

            return count;
        }

        /// <summary>
        /// 当前是否真的「能放出」挂机自动技能。
        /// 与 <see cref="TryAutoSkill"/> 的区别：冷却中 / 蓝不够 / 动作未结束一律返回 false，
        /// 这样贴身时可以先判断「放不出技能 → 改普通攻击」，而不是站着干等。
        /// </summary>
        private bool CanCastAutoSkill()
        {
            // 「法系挂机」未勾选 = 纯物理挂机，任何职业都不放技能。
            // 之前贴身分支只判断「远战挂机是否使用技能」，而该开关是全局配置且
            // 从下拉框选过技能后一直为 true → 取消勾选「法系挂机」后战士仍会继续放迅捷剑法。
            if (!Config.是否远战挂机) return false;

            MagicType type = PickAutoSkillType();

            if (type == MagicType.None || NonAutoSkillMagics.Contains(type)) return false;
            if (CEnvir.Now < _nextAutoSkillTime) return false;

            if (MapObject.User.ActionQueue != null && MapObject.User.ActionQueue.Count > 0) return false;
            if (MapObject.User.MagicAction != null) return false;

            ClientUserMagic magic = GameScene.Game.GetMagic(type);

            if (magic == null) return false;                 // 未学会
            if (CEnvir.Now < magic.NextCast) return false;   // 冷却中
            if (magic.Cost > User.CurrentMP) return false;   // 蓝不够

            MapObject target = MapObject.TargetObject;

            return target != null && !target.Dead;
        }

        /// <summary>
        /// 挂机时对贴身目标做普通物理攻击。
        /// 挂机分支已经提到 ProcessInput 的「手动攻击」之前，所以这里要自己实现一份，
        /// 否则近战角色挂机会完全不出手。
        /// </summary>
        private void AndroidAttack(MapObject target)
        {
            if (target == null || target.Dead) return;
            if (User.Horse != HorseType.None) return;
            if (CEnvir.Now <= User.AttackTime) return;
            if (Functions.Distance(User.CurrentLocation, target.CurrentLocation) > AndroidAttackRange) return;

            MapObject.User.AttemptAction(new ObjectAction(
                MirAction.Attack,
                Functions.DirectionFromPoint(User.CurrentLocation, target.CurrentLocation),
                MapObject.User.CurrentLocation,
                0, // Ranged Attack Target ID
                MagicType.None,
                Element.None));
        }

        private bool TryAutoSkill()
        {
            // 同上：未勾选「法系挂机」一律不放技能，避免历史配置（远战挂机是否使用技能=true）残留生效。
            if (!Config.是否远战挂机) return false;

            MagicType type = PickAutoSkillType();

            if (type == MagicType.None || NonAutoSkillMagics.Contains(type)) return false;

            if (CEnvir.Now < _nextAutoSkillTime) return true;

            if (MapObject.User.ActionQueue != null && MapObject.User.ActionQueue.Count > 0) return true;
            if (MapObject.User.MagicAction != null) return true;

            ClientUserMagic magic = GameScene.Game.GetMagic(type);

            if (magic == null) return false;                // 未学会
            if (CEnvir.Now < magic.NextCast) return true;   // 冷却中：等待，不刷屏
            if (magic.Cost > User.CurrentMP) return true;   // 蓝不够：等待，不刷屏

            MapObject target = MapObject.TargetObject;

            if (target == null || target.Dead) return false;

            // 显式传入目标：挂机时鼠标多半不在目标身上，
            // UseMagic(type) 默认会打 MouseObject，导致技能打空/打错。
            MapObject.MagicObject = target;

            if (IsGroundTargetMagic(type))
            {
                // 有一类技能不是锁定目标，而是「打鼠标所指的地面」（火墙 / 流星 / 施毒 …），
                // 施法点取 MapControl.MapLocation。挂机时鼠标位置不可控，技能会打空。
                //
                // 这里只临时把 MapLocation 指向目标格，调用完立刻还原：
                // GameScene.UseMagic 一个字都不用改，非挂机玩家完全不受影响。
                // 自身/队伍类技能（群体治愈、隐身、魔防…）指向自己脚下而不是怪物格。
                Point oldMapLocation = MapLocation;

                MapLocation = IsSelfCenteredMagic(type) ? User.CurrentLocation : target.CurrentLocation;

                try
                {
                    GameScene.Game.UseMagic(type, target);
                }
                finally
                {
                    MapLocation = oldMapLocation;
                }
            }
            else
            {
                GameScene.Game.UseMagic(type, target);
            }

            _nextAutoSkillTime = CEnvir.Now.AddMilliseconds(600);
            return true;
        }

        /// <summary>
        /// 该技能是否「打鼠标所指的地面」而不是锁定目标。
        /// 分类依据 GameScene.UseMagic 里现成的两处 switch：
        /// 1. 用 MapLocation 做距离校验的那组；2. targetLocation 直接取 MapLocation 的那组。
        /// 仅用于挂机施法，不影响手动释放。
        /// </summary>
        public static bool IsGroundTargetMagic(MagicType type)
        {
            switch (type)
            {
                // —— 以地图位置为施法点 ——
                case MagicType.SwiftBlade:
                case MagicType.FireWall:
                case MagicType.FireStorm:
                case MagicType.LightningWave:
                case MagicType.IceStorm:
                case MagicType.DragonTornado:
                case MagicType.GeoManipulation:
                case MagicType.Transparency:
                case MagicType.MeteorShower:
                case MagicType.Tempest:
                case MagicType.Asteroid:
                case MagicType.Tornado:
                case MagicType.MagicResistance:
                case MagicType.Resilience:
                case MagicType.LifeSteal:
                case MagicType.MassInvisibility:
                case MagicType.TrapOctagon:
                case MagicType.ElementalSuperiority:
                case MagicType.BloodLust:
                case MagicType.MassHeal:
                case MagicType.BurningFire:

                // —— targetLocation 强制取地图位置（道士符咒/施毒类）——
                case MagicType.Purification:
                case MagicType.EvilSlayer:
                case MagicType.GreaterEvilSlayer:
                case MagicType.ExplosiveTalisman:
                case MagicType.ImprovedExplosiveTalisman:
                case MagicType.PoisonDust:
                case MagicType.Neutralize:
                case MagicType.BindingTalisman:
                case MagicType.BrainStorm:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// 以「自己脚下」为施法点的技能（自身 / 队伍范围效果）。
        /// 挂机时不该指向怪物格。
        /// </summary>
        public static bool IsSelfCenteredMagic(MagicType type)
        {
            switch (type)
            {
                case MagicType.Transparency:
                case MagicType.MagicResistance:
                case MagicType.Resilience:
                case MagicType.MassInvisibility:
                case MagicType.ElementalSuperiority:
                case MagicType.BloodLust:
                case MagicType.MassHeal:
                case MagicType.LifeSteal:
                    return true;

                default:
                    return false;
            }
        }

        #endregion

        public void Run(MirDirection direction)
        {
            int steps = 1;

            if (GameScene.Game.CanRun && CEnvir.Now >= User.NextRunTime && User.BagWeight <= User.Stats[Stat.BagWeight] && User.WearWeight <= User.Stats[Stat.WearWeight])
            {
                steps++;
                if (User.Horse != HorseType.None)
                    steps++;
            }

            for (int i = 1; i <= steps; i++)
            {
                if (CanMove(direction, i)) continue;

                MirDirection best = MouseDirectionBest(direction, 1);

                if (best == direction)
                {
                    if (i == 1)
                    {
                        if (direction != User.Direction)
                            MapObject.User.AttemptAction(new ObjectAction(MirAction.Standing, direction, MapObject.User.CurrentLocation));
                        return;
                    }

                    steps = i - 1;
                }
                else
                {
                    steps = 1;
                }

                direction = best;
                break;
            }

            MapObject.User.AttemptAction(new ObjectAction(MirAction.Moving, direction, Functions.Move(MapObject.User.CurrentLocation, direction, steps), steps, MagicType.None));
        }

        public MirDirection MouseDirectionBest(MirDirection dir, int distance) //22.5 = 16
        {
            Point loc = Functions.Move(MapObject.User.CurrentLocation, dir, distance);

            if (loc.X >= 0 && loc.Y >= 0 && loc.X < Width && loc.Y < Height && !Cells[loc.X, loc.Y].Blocking()) return dir;

            Point localMouse = new Point(MouseLocation.X - GameScene.Game.Location.X, MouseLocation.Y - GameScene.Game.Location.Y);

            PointF c = new PointF(OffSetX * CellWidth + PixelOffsetX + CellWidth / 2F, OffSetY * CellHeight + PixelOffsetY + CellHeight / 2F);
            PointF a = new PointF(c.X, 0);
            PointF b = localMouse;
            float bc = (float)Functions.Distance(c, b);
            float ac = bc;
            b.Y -= c.Y;
            c.Y += bc;
            b.Y += bc;
            double ab = (float)Functions.Distance(b, a);
            double x = (ac * ac + bc * bc - ab * ab) / (2 * ac * bc);
            double angle = Math.Acos(x);

            angle *= 180 / Math.PI;

            if (localMouse.X < c.X) angle = 360 - angle;

            MirDirection best = (MirDirection)(angle / 45F);

            if (best == dir)
                best = Functions.ShiftDirection(dir, 1);

            MirDirection next = Functions.ShiftDirection(dir, -((int)best - (int)dir));

            if (CanMove(best, distance))
                return best;

            if (CanMove(next, distance))
                return next;

            return dir;
        }
        public MirDirection DirectionBest(MirDirection dir, int distance, Point targetLocation) //22.5 = 16
        {
            Point loc = Functions.Move(MapObject.User.CurrentLocation, dir, distance);

            if (loc.X >= 0 && loc.Y >= 0 && loc.X < Width && loc.Y < Height && !Cells[loc.X, loc.Y].Blocking()) return dir;


            PointF c = new PointF(MapObject.OffSetX * MapObject.CellWidth + PixelOffsetX + MapObject.CellWidth / 2F, MapObject.OffSetY * MapObject.CellHeight + PixelOffsetY + MapObject.CellHeight / 2F);
            PointF a = new PointF(c.X, 0);
            PointF b = new PointF((targetLocation.X - MapObject.User.CurrentLocation.X + MapObject.OffSetX) * MapObject.CellWidth + PixelOffsetX + MapObject.CellWidth / 2F,
                (targetLocation.Y - MapObject.User.CurrentLocation.Y + MapObject.OffSetY) * MapObject.CellHeight + PixelOffsetY + MapObject.CellHeight / 2F);
            float bc = (float)Functions.Distance(c, b);
            float ac = bc;
            b.Y -= c.Y;
            c.Y += bc;
            b.Y += bc;
            double ab = (float)Functions.Distance(b, a);
            double x = (ac * ac + bc * bc - ab * ab) / (2 * ac * bc);
            double angle = Math.Acos(x);

            angle *= 180 / Math.PI;

            if (b.X < c.X) angle = 360 - angle;

            MirDirection best = (MirDirection)(angle / 45F);

            if (best == dir)
                best = Functions.ShiftDirection(dir, 1);

            MirDirection next = Functions.ShiftDirection(dir, -((int)best - (int)dir));

            if (CanMove(best, distance))
                return best;

            return CanMove(next, distance) ? next : dir;
        }

        private bool CanMove(MirDirection dir, int distance)
        {
            for (int i = 1; i <= distance; i++)
            {
                Point loc = Functions.Move(User.CurrentLocation, dir, i);

                if (loc.X < 0 || loc.Y < 0 || loc.X >= Width || loc.Y > Height) return false;

                if (Cells[loc.X, loc.Y].Blocking())
                    return false;
            }
            return true;
        }

        /// <summary>坐标是否在地图内且该格可站立（无地形阻挡、无阻挡对象）。</summary>
        public bool EmptyCell(Point loc)
        {
            if (Cells == null) return false;
            if (loc.X < 0 || loc.Y < 0 || loc.X >= Width || loc.Y >= Height) return false;

            return !Cells[loc.X, loc.Y].Blocking();
        }

        public MirDirection MouseDirection() //22.5 = 16
        {
            Point localMouse = new Point(MouseLocation.X - GameScene.Game.Location.X, MouseLocation.Y - GameScene.Game.Location.Y);
            PointF p = new PointF((localMouse.X - PixelOffsetX) / (float)CellWidth, (localMouse.Y - PixelOffsetY) / (float)CellHeight);

            //If close proximity then co by co ords 
            if (Functions.InRange(new Point(OffSetX, OffSetY), Point.Truncate(p), 2))
                return Functions.DirectionFromPoint(new Point(OffSetX, OffSetY), Point.Truncate(p));

            PointF c = new PointF(OffSetX * CellWidth + PixelOffsetX + CellWidth / 2F, OffSetY * CellHeight + PixelOffsetY + CellHeight / 2F);
            PointF a = new PointF(c.X, 0);
            PointF b = new PointF(localMouse.X, localMouse.Y);
            float bc = (float)Functions.Distance(c, b);
            float ac = bc;
            b.Y -= c.Y;
            c.Y += bc;
            b.Y += bc;
            float ab = (float)Functions.Distance(b, a);
            double x = (ac * ac + bc * bc - ab * ab) / (2 * ac * bc);
            double angle = Math.Acos(x);

            angle *= 180 / Math.PI;

            if (localMouse.X < c.X) angle = 360 - angle;
            angle += 22.5F;
            if (angle > 360) angle -= 360;


            return (MirDirection)(angle / 45F);
        }

        public void AddObject(MapObject ob)
        {
            TextureValid = false;
            Objects.Add(ob);
            if (ob is ItemObject) groundItemCount++;

            if (ob.CurrentLocation.X < Width && ob.CurrentLocation.Y < Height)
                Cells[ob.CurrentLocation.X, ob.CurrentLocation.Y].AddObject(ob);
        }

        public void RemoveObject(MapObject ob)
        {
            if (!Objects.Remove(ob)) return;

            TextureValid = false;
            if (ob is ItemObject item)
            {
                groundItemCount--;
                focusedLoot.Remove(item);
                if (!HasGroundItems)
                    ClearLootCaches();
            }

            nameOverlayObjects.Remove(ob);
            statusOverlayObjects.Remove(ob);

            if (ob.CurrentLocation.X < Width && ob.CurrentLocation.Y < Height)
                Cells[ob.CurrentLocation.X, ob.CurrentLocation.Y].RemoveObject(ob);
        }

        public bool CanAttack(MapObject ob)
        {
            if (ob == null || ob == User) return false;

            switch (ob.Race)
            {
                case ObjectType.Player:
                    break;
                case ObjectType.Monster:
                    MonsterObject mob = (MonsterObject)ob;

                    if (mob.MonsterInfo.AI < 0) return false;

                    break;
                default:
                    return false;
            }

            return !ob.Dead;
        }

        public void UpdateMapLocation()
        {
            if (User == null) return;

            GameScene.Game.MapControl.MapLocation = new Point((GameScene.Game.MapControl.MouseLocation.X - GameScene.Game.Location.X - PixelOffsetX) / CellWidth - OffSetX + User.CurrentLocation.X,
                                                              (GameScene.Game.MapControl.MouseLocation.Y - GameScene.Game.Location.Y - PixelOffsetY) / CellHeight - OffSetY + User.CurrentLocation.Y);
        }

        public bool HasTarget(Point loc)
        {
            if (loc.X < 0 || loc.Y < 0 || loc.X >= Width || loc.Y > Height) return false;

            Cell cell = Cells[loc.X, loc.Y];

            if (cell.Objects == null) return false;

            foreach (MapObject ob in cell.Objects)
                if (ob.Blocking) return true;

            return false;
        }

        public bool CanEnergyBlast(MirDirection direction)
        {
            return HasTarget(Functions.Move(MapObject.User.CurrentLocation, direction, 2));
        }

        public bool CanHalfMoon(MirDirection direction)
        {
            if (HasTarget(Functions.Move(MapObject.User.CurrentLocation, Functions.ShiftDirection(direction, -1)))) return true;
            if (HasTarget(Functions.Move(MapObject.User.CurrentLocation, Functions.ShiftDirection(direction, 1)))) return true;
            if (HasTarget(Functions.Move(MapObject.User.CurrentLocation, Functions.ShiftDirection(direction, 2)))) return true;

            return false;
        }

        public bool CanDestructiveSurge(MirDirection direction)
        {
            for (int i = 1; i < 8; i++)
                if (HasTarget(Functions.Move(MapObject.User.CurrentLocation, Functions.ShiftDirection(direction, i)))) return true;

            return false;
        }

        public bool ValidCell(Point location)
        {
            if (location.X < 0 || location.Y < 0 || location.X >= Width || location.Y >= Height) return false;

            return !Cells[location.X, location.Y].Flag;
        }
        #endregion

        #region IDisposable

        protected override void Dispose(bool disposing)
        {
            ReleaseWorldNames();
            base.Dispose(disposing);

            if (disposing)
            {
                DisposeLoot();
                foreach (MapObject ob in Objects)
                    if (ob is ItemObject item) item.ReleaseLabels();
                _MapInfo = null;
                MapInfoChanged = null;

                _InstanceInfo = null;

                _Animation = 0;
                AnimationChanged = null;

                MapButtons = 0;
                MapLocation = Point.Empty;
                Mining = false;
                MiningPoint = Point.Empty;
                MiningDirection = 0;

                FishingState = FishingState.None;
                FloatLocation = Point.Empty;
                FishingDirection = 0;
                AutoCast = false;

                if (FLayer != null)
                {
                    if (!FLayer.IsDisposed)
                        FLayer.Dispose();

                    FLayer = null;
                }

                if (LLayer != null)
                {
                    if (!LLayer.IsDisposed)
                        LLayer.Dispose();

                    LLayer = null;
                }

                Cells = null;
                PathFinder = null;

                Width = 0;
                Height = 0;

                MapInfoObjects.Clear();
                MapInfoObjects = null;

                Objects.Clear();
                Objects = null;

                Effects.Clear();
                Effects = null;
                ViewRangeX = 0;
                ViewRangeY = 0;
                OffSetX = 0;
                OffSetY = 0;

                if (ParticleEffects != null)
                {
                    foreach (var effect in ParticleEffects)
                    {
                        effect.Dispose();
                    }

                    ParticleEffects.Clear();
                    ParticleEffects = null;
                }
            }
        }

        #endregion

        public sealed class Floor : DXControl
        {
            public Floor()
            {
                IsControl = false;
            }

            #region Methods
            public void CheckTexture()
            {
                if (!TextureValid)
                    CreateTexture();
            }

            protected override void OnClearTexture()
            {
                base.OnClearTexture();

                int minX = Math.Max(0, User.CurrentLocation.X - OffSetX - 4), maxX = Math.Min(GameScene.Game.MapControl.Width - 1, User.CurrentLocation.X + OffSetX + 4);
                int minY = Math.Max(0, User.CurrentLocation.Y - OffSetY - 4), maxY = Math.Min(GameScene.Game.MapControl.Height - 1, User.CurrentLocation.Y + OffSetY + 4);

                for (int y = minY; y <= maxY; y++)
                {
                    if (y < 0) continue;
                    if (y >= GameScene.Game.MapControl.Height) break;

                    int drawY = (y - User.CurrentLocation.Y + OffSetY) * CellHeight + PixelOffsetY - User.MovingOffSet.Y - User.ShakeScreenOffset.Y;

                    for (int x = minX; x <= maxX; x++)
                    {
                        if (x < 0) continue;
                        if (x >= GameScene.Game.MapControl.Width) break;

                        int drawX = (x - User.CurrentLocation.X + OffSetX) * CellWidth + PixelOffsetX - User.MovingOffSet.X - User.ShakeScreenOffset.X;

                        Cell tile = GameScene.Game.MapControl.Cells[x, y];

                        if (y % 2 == 0 && x % 2 == 0)
                        {
                            MirLibrary library;
                            LibraryFile file;

                            if (!Libraries.KROrder.TryGetValue(tile.BackFile, out file)) continue;

                            if (!CEnvir.LibraryList.TryGetValue(file, out library)) continue;

                            library.Draw(tile.BackImage, drawX, drawY, Color.White, false, 1F, ImageType.Image);
                        }
                    }
                }

                for (int y = minY; y <= maxY; y++)
                {
                    int drawY = (y - User.CurrentLocation.Y + OffSetY + 1) * CellHeight + PixelOffsetY - User.MovingOffSet.Y - User.ShakeScreenOffset.Y;

                    for (int x = minX; x <= maxX; x++)
                    {
                        int drawX = (x - User.CurrentLocation.X + OffSetX) * CellWidth + PixelOffsetX - User.MovingOffSet.X - User.ShakeScreenOffset.X;
                        Cell cell = GameScene.Game.MapControl.Cells[x, y];

                        if (!(cell.MiddleAnimationFrame > 1 && cell.MiddleAnimationFrame < 255) &&
                            Libraries.KROrder.TryGetValue(cell.MiddleFile, out LibraryFile file) &&
                            file != LibraryFile.Tilesc &&
                            CEnvir.LibraryList.TryGetValue(file, out MirLibrary library))
                        {
                            int index = cell.MiddleImage - 1;
                            Size size = library.GetSize(index);

                            if (IsCellSized(size))
                                library.Draw(index, drawX, drawY - CellHeight, Color.White, false, 1F, ImageType.Image);
                        }

                        if (!(cell.FrontAnimationFrame > 1 && cell.FrontAnimationFrame < 255) &&
                            Libraries.KROrder.TryGetValue(cell.FrontFile, out file) &&
                            file != LibraryFile.Tilesc &&
                            CEnvir.LibraryList.TryGetValue(file, out library))
                        {
                            int index = cell.FrontImage - 1;
                            Size size = library.GetSize(index);

                            if (IsCellSized(size))
                                library.Draw(index, drawX, drawY - CellHeight, Color.White, false, 1F, ImageType.Image);
                        }
                    }
                }
            }

            public override void Draw()
            {
            }
            protected override void DrawControl()
            {
            }

            #endregion
        }

        public sealed class Light : DXControl
        {
            private const float LightScale = 0.02F;
            private const float BaseLightSize = 0.1F;
            private const float TileLightScaleMultiplier = 30F;
            private const float EffectLightScaleDivisor = 5F;
            private const int TileLightSearchPadding = 15;

            private int _lastLightSignature;

            public Light()
            {
                IsControl = false;
                BackColour = Color.FromArgb(15, 15, 15);
            }

            #region Methods

            public void CheckTexture()
            {
                UpdateAmbientLight();

                if (!ShouldRenderLightLayer())
                {
                    _lastLightSignature = 0;

                    if (TextureValid)
                        DisposeTexture();

                    return;
                }

                // A pipeline reset can briefly leave the shared light texture unavailable.
                // Do not create and cache an incomplete light layer during that window.
                try
                {
                    if (!RenderingPipelineManager.GetLightTexture().IsValid ||
                        RenderingPipelineManager.GetLightTextureSize().IsEmpty)
                        return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                int signature = GetLightSignature();

                if (TextureValid && signature == _lastLightSignature)
                    return;

                CreateTexture();
                _lastLightSignature = signature;
            }

            protected override void OnClearTexture()
            {
                base.OnClearTexture();

                MapControl map = GameScene.Game.MapControl;
                UserObject user = MapObject.User;

                if (user.Dead)
                {
                    RenderingPipelineManager.Clear(RenderClearFlags.Target, Color.IndianRed, 0, 0);
                    return;
                }

                bool previousBlendEnabled = RenderingPipelineManager.IsBlending();
                float previousBlendRate = RenderingPipelineManager.GetBlendRate();
                BlendMode previousBlendMode = RenderingPipelineManager.GetBlendMode();

                RenderingPipelineManager.SetBlend(true, 1F, BlendMode.COLORFY);

                RenderTexture lightTexture;
                Size lightSize;

                try
                {
                    lightTexture = RenderingPipelineManager.GetLightTexture();
                    lightSize = RenderingPipelineManager.GetLightTextureSize();
                }
                catch (InvalidOperationException)
                {
                    RenderingPipelineManager.SetBlend(previousBlendEnabled, previousBlendRate, previousBlendMode);
                    return;
                }

                Rectangle lightSource = new Rectangle(0, 0, lightSize.Width, lightSize.Height);

                if ((user.Poison & PoisonType.Abyss) == PoisonType.Abyss)
                {
                    RenderingPipelineManager.Clear(RenderClearFlags.Target, Color.Black, 0, 0);

                    float scale = BaseLightSize + 4 * LightScale;
                    float abyssX = OffSetX * CellWidth + PixelOffsetX + CellWidth / 2F - (lightSize.Width * scale) / 2F;
                    float abyssY = OffSetY * CellHeight + PixelOffsetY - (lightSize.Height * scale) / 2F;

                    DrawLight(lightTexture, lightSource, lightSize, abyssX, abyssY, scale, Color.White);

                    RenderingPipelineManager.SetBlend(previousBlendEnabled, previousBlendRate, previousBlendMode);

                    var abyssEffects = user.CreateMagicEffect(MagicEffect.Abyss);

                    foreach (var effect in abyssEffects)
                    {
                        effect.Draw();
                    }
                    return;
                }

                foreach (MapObject ob in map.Objects)
                {
                    if (!ShouldDrawObjectLight(ob, user))
                        continue;

                    float scale = BaseLightSize + ob.Light * 2 * LightScale;
                    float objectX = (OffSetX + ob.CurrentLocation.X - user.CurrentLocation.X) * CellWidth + PixelOffsetX + ob.MovingOffSet.X - user.MovingOffSet.X + CellWidth / 2F - (lightSize.Width * scale) / 2F;
                    float objectY = (OffSetY + ob.CurrentLocation.Y - user.CurrentLocation.Y) * CellHeight + PixelOffsetY + ob.MovingOffSet.Y - user.MovingOffSet.Y - (lightSize.Height * scale) / 2F;

                    DrawLight(lightTexture, lightSource, lightSize, objectX, objectY, scale, ob.LightColour);
                }

                foreach (MirEffect ob in map.Effects)
                {
                    float frameLight = ob.FrameLight;
                    if (ob is LootEffect && !LootLightVisible(ob, frameLight, lightSize)) continue;

                    if (frameLight > 0)
                    {
                        float scale = BaseLightSize + frameLight * 2 * LightScale / EffectLightScaleDivisor;
                        float effectX = ob.DrawX + CellWidth / 2F - (lightSize.Width * scale) / 2F;
                        float effectY = ob.DrawY + CellHeight / 2F - (lightSize.Height * scale) / 2F;

                        DrawLight(lightTexture, lightSource, lightSize, effectX, effectY, scale, ob.FrameLightColour);
                    }
                }

                int minX = Math.Max(0, user.CurrentLocation.X - OffSetX - TileLightSearchPadding);
                int maxX = Math.Min(map.Width - 1, user.CurrentLocation.X + OffSetX + TileLightSearchPadding);
                int minY = Math.Max(0, user.CurrentLocation.Y - OffSetY - TileLightSearchPadding);
                int maxY = Math.Min(map.Height - 1, user.CurrentLocation.Y + OffSetY + TileLightSearchPadding);

                for (int y = minY; y <= maxY; y++)
                {
                    if (y < 0) continue;
                    if (y >= map.Height) break;

                    int drawY = (y - user.CurrentLocation.Y + OffSetY) * CellHeight + PixelOffsetY - user.MovingOffSet.Y - user.ShakeScreenOffset.Y;

                    for (int x = minX; x <= maxX; x++)
                    {
                        if (x < 0) continue;
                        if (x >= map.Width) break;

                        int drawX = (x - user.CurrentLocation.X + OffSetX) * CellWidth + PixelOffsetX - user.MovingOffSet.X - user.ShakeScreenOffset.X;

                        Cell tile = map.Cells[x, y];

                        if (tile.Light == 0) continue;

                        float scale = BaseLightSize + tile.Light * TileLightScaleMultiplier * LightScale;
                        float tileX = drawX + CellWidth / 2F - (lightSize.Width * scale) / 2F;
                        float tileY = drawY + CellHeight / 2F - (lightSize.Height * scale) / 2F;

                        DrawLight(lightTexture, lightSource, lightSize, tileX, tileY, scale, Color.White);
                    }
                }

                RenderingPipelineManager.SetBlend(previousBlendEnabled, previousBlendRate, previousBlendMode);
            }

            private void DrawLight(RenderTexture lightTexture, Rectangle sourceRectangle, Size lightSize, float topLeftX, float topLeftY, float scale, Color colour)
            {
                float width = lightSize.Width * scale;
                float height = lightSize.Height * scale;

                if (width <= 0 || height <= 0)
                {
                    return;
                }

                RectangleF destination = new RectangleF(topLeftX, topLeftY, width, height);
                RenderingPipelineManager.DrawTexture(lightTexture, sourceRectangle, destination, colour);
            }

            private static bool ShouldDrawObjectLight(MapObject ob, UserObject user)
            {
                return ob.Light > 0 && (!ob.Dead || ob == user || ob.Race == ObjectType.Spell);
            }

            private static bool LootLightVisible(MirEffect effect, float frameLight, Size lightSize)
            {
                if (frameLight <= 0) return false;
                float scale = BaseLightSize + frameLight * 2 * LightScale / EffectLightScaleDivisor;
                float width = lightSize.Width * scale, height = lightSize.Height * scale;
                return new RectangleF(effect.DrawX + CellWidth / 2F - width / 2,
                    effect.DrawY + CellHeight / 2F - height / 2, width, height)
                    .IntersectsWith(new RectangleF(PointF.Empty, GameScene.Game.MapControl.Size));
            }

            public void UpdateLights()
            {
                UpdateAmbientLight();
                TextureValid = false;
            }

            private void UpdateAmbientLight()
            {
                // 免蜡烛：强制环境光全白，跳过按地图暗度计算。
                // 注意：CheckTexture() 每帧直接调本方法（不经过 UpdateLights），守卫必须放在这里才生效。
                if (Config.免蜡烛)
                {
                    BackColour = Color.White;
                    Visible = true;
                    return;
                }

                switch (GameScene.Game.MapControl.MapInfo.Light)
                {
                    case LightSetting.Default:
                        byte shading = (byte)(255 * GameScene.Game.DayTime);
                        BackColour = Color.FromArgb(shading, shading, shading);
                        Visible = true;
                        break;
                    case LightSetting.Night:
                        BackColour = Color.FromArgb(15, 15, 15);
                        Visible = true;
                        break;
                    case LightSetting.Twilight:
                        BackColour = Color.FromArgb(100, 100, 100);
                        Visible = true;
                        break;
                    case LightSetting.Light:
                        BackColour = Color.FromArgb(255, 255, 255);
                        Visible = true;
                        break;
                }

                if (MapObject.User != null && (MapObject.User.Poison & PoisonType.Abyss) == PoisonType.Abyss)
                {
                    BackColour = Color.FromArgb(15, 15, 15);
                    Visible = false;
                }
            }

            private static bool ShouldRenderLightLayer()
            {
                MapControl map = GameScene.Game.MapControl;
                UserObject user = MapObject.User;

                if (user != null && (user.Dead || (user.Poison & PoisonType.Abyss) == PoisonType.Abyss))
                    return true;

                Light lightLayer = map.LLayer;

                if (map.MapInfo.Light == LightSetting.Light)
                    return false;

                return lightLayer.BackColour.R < 255 || lightLayer.BackColour.G < 255 || lightLayer.BackColour.B < 255;
            }

            private static int GetLightSignature()
            {
                unchecked
                {
                    int hash = 17;
                    MapControl map = GameScene.Game.MapControl;
                    UserObject user = MapObject.User;

                    hash = hash * 31 + map.Size.Width;
                    hash = hash * 31 + map.Size.Height;
                    hash = hash * 31 + (int)map.MapInfo.Light;
                    hash = hash * 31 + map.LLayer.BackColour.ToArgb();
                    hash = hash * 31 + user.CurrentLocation.X;
                    hash = hash * 31 + user.CurrentLocation.Y;
                    hash = hash * 31 + user.MovingOffSet.X;
                    hash = hash * 31 + user.MovingOffSet.Y;
                    hash = hash * 31 + user.ShakeScreenOffset.X;
                    hash = hash * 31 + user.ShakeScreenOffset.Y;
                    hash = hash * 31 + user.Light;
                    hash = hash * 31 + user.LightColour.ToArgb();
                    hash = hash * 31 + (int)user.Poison;

                    foreach (MapObject ob in map.Objects)
                    {
                        if (!ShouldDrawObjectLight(ob, user))
                            continue;

                        hash = hash * 31 + ob.ObjectID.GetHashCode();
                        hash = hash * 31 + ob.CurrentLocation.X;
                        hash = hash * 31 + ob.CurrentLocation.Y;
                        hash = hash * 31 + ob.MovingOffSet.X;
                        hash = hash * 31 + ob.MovingOffSet.Y;
                        hash = hash * 31 + ob.Light;
                        hash = hash * 31 + ob.LightColour.ToArgb();
                        hash = hash * 31 + (ob.Dead ? 1 : 0);
                    }

                    foreach (MirEffect effect in map.Effects)
                    {
                        float frameLight = effect.FrameLight;
                        if (frameLight <= 0)
                            continue;
                        if (effect is LootEffect && !LootLightVisible(effect, frameLight, RenderingPipelineManager.GetLightTextureSize())) continue;

                        hash = hash * 31 + effect.DrawX;
                        hash = hash * 31 + effect.DrawY;
                        hash = hash * 31 + (int)(frameLight * 100);
                        hash = hash * 31 + effect.FrameLightColour.ToArgb();
                    }

                    return hash;
                }
            }
            protected override void DrawControl()
            {
            }

            public override void Draw()
            {
            }
            #endregion
        }
    }

    public sealed class Cell
    {
        public int BackFile;
        public int BackImage;

        public int MiddleFile;
        public int MiddleImage;

        public int FrontFile;
        public int FrontImage;

        public int FrontAnimationFrame { get; set; }
        public int FrontAnimationTick { get; set; }
        public int FrontAnimationCount => FrontAnimationFrame & FrontFrameMask;
        public bool FrontAnimationBlend => (FrontAnimationFrame & FrontBlendBit) != 0;

        public int MiddleAnimationFrame { get; set; }
        public int MiddleAnimationTick { get; set; }
        public int MiddleAnimationCount => MiddleAnimationFrame & MiddleFrameMask;
        public bool MiddleAnimationBlend => (MiddleAnimationFrame & MiddleBlendBit) != 0;

        public int Light;

        public bool Flag;

        // --- FRONT ANIMATION ENCODING ---
        public const int FrontFrameMask = 0x0F; // lower 4 bits = frame count
        public const int FrontBlendBit = 0x80;  // bit 7 = blend flag

        // --- MIDDLE ANIMATION ENCODING ---
        public const int MiddleFrameMask = 0x0F; // lower 4 bits = frame count
        public const int MiddleBlendBit = 0x80;  // bit 7 = blend flag

        public MirLibrary MiddleLibrary;
        public MirLibrary FrontLibrary;
        public bool LibrariesLoaded;

        public List<MapObject> Objects;
        public int ObjectVersion { get; private set; }

        public bool Blocking()
        {
            if (Objects != null)
            {
                foreach (MapObject ob in Objects)
                    if (ob.Blocking) return true;
            }

            return Flag;
        }

        public void AddObject(MapObject ob)
        {
            if (Objects == null)
                Objects = new List<MapObject>();
            ObjectVersion++;

            if (ob.Race == ObjectType.Spell)
                Objects.Insert(0, ob);
            else
                Objects.Add(ob);

            ob.CurrentCell = this;
        }

        public void RemoveObject(MapObject ob)
        {
            Objects.Remove(ob);
            ObjectVersion++;

            if (Objects.Count == 0)
                Objects = null;

            ob.CurrentCell = null;
        }
    }

}

using Client.Controls;
using Client.Envir;
using Client.Scenes;
using Client.Scenes.Views;
using Library;
using Sentry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Client
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            InstallCrashHandlers();

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            ConfigReader.Load(Assembly.GetAssembly(typeof(Config)));

            if (Config.SentryEnabled && !string.IsNullOrEmpty(Config.SentryDSN))
            {
                using (SentrySdk.Init(Config.SentryDSN))
                    Init(args);
            }
            else
            {
                Init(args);
            }

            ConfigReader.Save(typeof(Config).Assembly);

            // 按角色的辅助功能配置（[CharacterScoped] 的字段不进 Zircon.ini，单独落盘）
            Client.Envir.CharacterConfigStore.Save();
        }

        /// <summary>
        /// 全局兜底：之前客户端没有任何未处理异常钩子，网络/渲染线程一抛异常就是
        /// 「进程直接消失、不留日志、不弹框」。这里把三条通道都挂上，统一写 Errors\。
        /// UI 线程异常只记录不退出（默认行为是弹 ThreadExceptionDialog 或直接终止）。
        /// </summary>
        static void InstallCrashHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += (o, e) =>
            {
                CEnvir.SaveError("[UI线程]\r\n" + e.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += (o, e) =>
            {
                CEnvir.SaveError("[非UI线程]\r\n" + (e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.ToString());
            };

            TaskScheduler.UnobservedTaskException += (o, e) =>
            {
                // 断线时挂起的异步接收会被「中止 I/O」收尾（SocketException 995）：
                // 套接字先关、接收任务后完成，异常没人 await → 由终结器线程兜底抛出来。
                // 这是**正常拆链的副产物**，不是崩溃。以前它带着完整堆栈写进 Errors\，
                // 2026-11-13 用户看到后以为客户端崩了 —— 现在单独一行说明。
                if (IsSocketTeardown(e.Exception))
                {
                    CEnvir.SaveError("[任务] 连接已断开：挂起的套接字接收被中止（SocketException 995），属正常拆链，不影响运行。");
                    e.SetObserved();
                    return;
                }

                CEnvir.SaveError("[任务]\r\n" + e.Exception);
                e.SetObserved();
            };
        }

        /// <summary>
        /// 是否是「套接字被关掉」这一类的收尾异常（OperationAborted / Interrupted / 已释放）。
        /// 逐层拆 <see cref="AggregateException"/>，因为 Socket 内部会把它们包一层。
        /// </summary>
        private static bool IsSocketTeardown(Exception exception)
        {
            if (exception == null) return false;

            if (exception is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.Flatten().InnerExceptions)
                    if (IsSocketTeardown(inner)) return true;

                return false;
            }

            if (exception is ObjectDisposedException) return true;

            if (exception is System.Net.Sockets.SocketException socket)
                return socket.SocketErrorCode is System.Net.Sockets.SocketError.OperationAborted
                    or System.Net.Sockets.SocketError.Interrupted
                    or System.Net.Sockets.SocketError.NotSocket;

            return false;
        }

        static void Init(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MirLibrary.GetNow = () => CEnvir.Now;
            MirLibrary.GetCacheDuration = () => Config.CacheDuration;
            MirLibrary.GetUseZlAtlasPages = () => Config.UseZlAtlasPages;
            MirLibrary.DrawCounted = () => CEnvir.DPSCounter++;

            foreach (KeyValuePair<LibraryFile, string> pair in Libraries.LibraryList)
            {
                if (!File.Exists(@".\" + pair.Value)) continue;

                CEnvir.LibraryList[pair.Key] = new MirLibrary(@".\" + pair.Value);
            }

            CEnvir.Init(args);

            CEnvir.Target = new TargetForm();
            string requestedPipelineId = RenderingPipelineManager.NormalizePipelineId(Config.RenderingPipeline);
            if (!string.Equals(Config.RenderingPipeline, requestedPipelineId, StringComparison.OrdinalIgnoreCase))
                Config.RenderingPipeline = requestedPipelineId;

            string activePipelineId = RenderingPipelineManager.InitializeWithFallback(requestedPipelineId, new RenderingPipelineContext(CEnvir.Target, CreateRenderingHostSettings()));
            if (!string.Equals(Config.RenderingPipeline, activePipelineId, StringComparison.OrdinalIgnoreCase))
                Config.RenderingPipeline = activePipelineId;
            DXSoundManager.Create();

            DXControl.ActiveScene = new LoginScene(Config.ExtendedLogin ? Config.GameSize : Config.IntroSceneSize);

            RenderingPipelineManager.RunMessageLoop(CEnvir.Target, CEnvir.GameLoop);

            CEnvir.Session?.Save(true);
            CEnvir.Unload();
            RenderingPipelineManager.Shutdown();
            DXSoundManager.Unload();
        }

        private static RenderingHostSettings CreateRenderingHostSettings()
        {
            return new RenderingHostSettings
            {
                Now = () => CEnvir.Now,
                SaveException = CEnvir.SaveException,
                InvalidateRenderCaches = InvalidateRenderCaches,
                RecreateRenderTarget = CEnvir.Target.RecreateRenderTarget,
                FullScreenChanged = fullScreen =>
                {
                    if (DXConfigWindow.ActiveConfig?.FullScreenCheckBox != null)
                        DXConfigWindow.ActiveConfig.FullScreenCheckBox.Checked = fullScreen;

                    DXConfigWindow.ActiveConfig?.UpdateScaleControlState();
                },
                GetActiveSceneSize = () => DXControl.ActiveScene?.Size ?? Config.GameSize,
                GetWindowScale = () => CEnvir.Target?.WindowScale ?? 1F,
                GetUIScale = () => CEnvir.Target?.UIScale ?? 1F,
                GetTextRasterScale = () => CEnvir.Target?.TextRasterScale ?? 1F,
                GetMonitorScale = TargetForm.GetMonitorScale,
                GetDefaultMonitor = () => Config.DefaultMonitor,
                SetDefaultMonitor = value => Config.DefaultMonitor = value,
                GetRenderingPipeline = () => Config.RenderingPipeline,
                SetRenderingPipeline = value => Config.RenderingPipeline = value,
                GetGameSize = () => Config.GameSize,
                SetGameSize = value => Config.GameSize = value,
                GetFullScreen = () => Config.FullScreen,
                SetFullScreen = value => Config.FullScreen = value,
                GetBorderless = () => Config.Borderless,
                SetBorderless = value => Config.Borderless = value,
                GetVSync = () => Config.VSync,
                SetVSync = value => Config.VSync = value,
                GetUseD3D11SpriteBatch = () => Config.UseD3D11SpriteBatch,
                SetUseD3D11SpriteBatch = value => Config.UseD3D11SpriteBatch = value,
            };
        }

        internal static void InvalidateRenderCaches()
        {
            InvalidateControlCaches(false);

            foreach (MirLibrary library in CEnvir.LibraryList.Values)
                library?.DisposeTextures();
        }

        internal static void InvalidateUiRenderCaches()
        {
            InvalidateControlCaches(true);
        }

        private static void InvalidateControlCaches(bool skipWorld)
        {
            HashSet<DXControl> visited = new();
            InvalidateControlTree(DXControl.ActiveScene, visited, skipWorld);

            foreach (DXControl messageBox in DXControl.MessageBoxList.ToArray())
                InvalidateControlTree(messageBox, visited, skipWorld);

            InvalidateControlTree(DXControl.MouseControl, visited, skipWorld);
            InvalidateControlTree(DXControl.FocusControl, visited, skipWorld);
            InvalidateControlTree(DXControl.DebugLabel, visited, skipWorld);
            InvalidateControlTree(DXControl.HintLabel, visited, skipWorld);
            InvalidateControlTree(DXControl.PingLabel, visited, skipWorld);
        }

        private static void InvalidateControlTree(DXControl control, HashSet<DXControl> visited, bool skipWorld)
        {
            // Detached overlays can still reference disposed controls whose child list has been cleared.
            if (control == null || control.IsDisposed || !visited.Add(control))
                return;

            if (skipWorld && control is MapControl)
                return;

            control.DisposeTexture();

            foreach (DXControl child in control.Controls.ToArray())
                InvalidateControlTree(child, visited, skipWorld);
        }
    }
}

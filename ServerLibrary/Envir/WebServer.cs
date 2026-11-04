using Library;
using MirDB;
using Server.DBModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Server.Envir
{
    public static class WebServer
    {
        public static ConcurrentQueue<WebCommand> WebCommandQueue;
        public static bool WebServerStarted { get; set; }

        private static HttpListener WebListener;
        public const string ActivationCommand = "Activation", ResetCommand = "Reset", DeleteCommand = "Delete",
            SystemDBSyncCommand = "SystemDBSync";
        public const string ActivationKey = "ActivationKey", ResetKey = "ResetKey", DeleteKey = "DeleteKey";

        /// <summary>账号结果页（激活/重置/注销 的成功与失败）在程序目录下的可选覆盖目录。</summary>
        private static readonly string AccountDiskPath = PlatformPath.Resolve(@".\Account\");

        public const string Completed = "Completed";
        public const string Currency = "GBP";

        public static Dictionary<decimal, long> GoldTable = new Dictionary<decimal, long>
        {
            [5M] = 500,
            [10M] = 1030,
            [15M] = 1590,
            [20M] = 2180,
            [30M] = 3360,
            [50M] = 5750,
            [100M] = 12000,
        };

        public static readonly string VerifiedPath = PlatformPath.Resolve(@".\Database\Store\Verified\"),
            InvalidPath = PlatformPath.Resolve(@".\Database\Store\Invalid\"),
            CompletePath = PlatformPath.Resolve(@".\Database\Store\Complete\");

        private static HttpListener BuyListener, IPNListener;
        public static ConcurrentQueue<IPNMessage> Messages = new ConcurrentQueue<IPNMessage>();
        public static List<IPNMessage> PaymentList = new List<IPNMessage>(), HandledPayments = new List<IPNMessage>();

        static WebServer()
        {
            Messages = new ConcurrentQueue<IPNMessage>();

            PaymentList.Clear();

            if (Directory.Exists(VerifiedPath))
            {
                string[] files = Directory.GetFiles(VerifiedPath);

                foreach (string file in files)
                    Messages.Enqueue(new IPNMessage { FileName = file, Message = File.ReadAllText(file), Verified = true });
            }
        }

        public static void StartWebServer(bool log = true)
        {
            if (!Config.EnableWebServer) return;

            try
            {
                WebCommandQueue = new ConcurrentQueue<WebCommand>();

                WebListener = new HttpListener();
                WebListener.Prefixes.Add(Config.WebPrefix);

                // 账号结果页走同一个端口（默认 80）的 /account/ 前缀：
                // 邮件链接跳到 http://<主机>/account/xxx.html，由这里直接输出 HTML。
                string accountPrefix = GetAccountPrefix();
                if (!string.IsNullOrEmpty(accountPrefix) && !WebListener.Prefixes.Contains(accountPrefix))
                    WebListener.Prefixes.Add(accountPrefix);

                WebListener.Start();
                WebListener.BeginGetContext(WebConnection, null);

                BuyListener = new HttpListener();
                BuyListener.Prefixes.Add(Config.BuyPrefix);

                IPNListener = new HttpListener();
                IPNListener.Prefixes.Add(Config.IPNPrefix);

                BuyListener.Start();
                BuyListener.BeginGetContext(BuyConnection, null);

                IPNListener.Start();
                IPNListener.BeginGetContext(IPNConnection, null);



                WebServerStarted = true;

                if (log) SEnvir.Log("Web 服务器已启动。");
            }
            catch (Exception ex)
            {
                WebServerStarted = false;
                SEnvir.Log(ex.ToString());

                if (WebListener != null && WebListener.IsListening)
                    WebListener?.Stop();
                WebListener = null;

                if (BuyListener != null && BuyListener.IsListening)
                    BuyListener?.Stop();
                BuyListener = null;

                if (IPNListener != null && IPNListener.IsListening)
                    IPNListener?.Stop();
                IPNListener = null;
            }
        }
        public static void StopWebServer(bool log = true)
        {
            if (!Config.EnableWebServer) return;

            HttpListener expiredWebListener = WebListener;
            WebListener = null;

            HttpListener expiredBuyListener = BuyListener;
            BuyListener = null;
            HttpListener expiredIPNListener = IPNListener;
            IPNListener = null;


            WebServerStarted = false;
            expiredWebListener?.Stop();
            expiredBuyListener?.Stop();
            expiredIPNListener?.Stop();

            WebCommandQueue = null;
            PaymentList.Clear();
            HandledPayments.Clear();
            while (Messages.TryDequeue(out _))
            {
            }

            if (log) SEnvir.Log("Web 服务器已停止。");
        }

        private static void WebConnection(IAsyncResult result)
        {
            try
            {
                HttpListenerContext context = WebListener.EndGetContext(result);

                // /account/xxx.html：账号结果页（激活 / 重置密码 / 注销）
                string path = context.Request.Url?.AbsolutePath ?? string.Empty;
                if (path.StartsWith("/account/", StringComparison.OrdinalIgnoreCase))
                {
                    ServeAccountPage(context, path);
                    return;
                }

                string command = context.Request.QueryString["Type"];

                switch (command)
                {
                    case ActivationCommand:
                        Activation(context);
                        break;
                    case ResetCommand:
                        ResetPassword(context);
                        break;
                    case DeleteCommand:
                        DeleteAccount(context);
                        break;
                    case SystemDBSyncCommand:
                        SystemDBSync(context);
                        break;
                }
            }
            catch { }
            finally
            {
                if (WebListener != null && WebListener.IsListening)
                    WebListener.BeginGetContext(WebConnection, null);
            }
        }

        /// <summary>由 WebPrefix 推导出同端口的 /account/ 前缀；解析失败返回 null。</summary>
        private static string GetAccountPrefix()
        {
            // 不能用 new Uri()：通配前缀 http://*:80/Command/ 里的 * 不是合法主机名，会抛异常。
            string prefix = Config.WebPrefix;

            if (string.IsNullOrWhiteSpace(prefix)) return null;

            int schemeEnd = prefix.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) return null;

            int pathStart = prefix.IndexOf('/', schemeEnd + 3);
            string authority = pathStart < 0 ? prefix : prefix.Substring(0, pathStart);

            return authority.TrimEnd('/') + "/account/";
        }

        /// <summary>
        /// 输出账号结果页（激活 / 重置密码 / 注销 的成功与失败）。
        /// 优先读程序目录下的 Account\&lt;文件名&gt;（运维可直接改文案），
        /// 没有则用编译进 DLL 的嵌入资源（开箱即用，零部署）。
        /// </summary>
        private static void ServeAccountPage(HttpListenerContext context, string path)
        {
            try
            {
                string name = Path.GetFileName(path);

                if (string.IsNullOrEmpty(name) ||
                    name.IndexOf("..", StringComparison.Ordinal) >= 0 ||
                    !name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                byte[] content = LoadAccountPage(name);

                if (content == null)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentEncoding = Encoding.UTF8;
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.ContentLength64 = content.Length;
                context.Response.OutputStream.Write(content, 0, content.Length);
            }
            catch
            {
                // 结果页出错不能影响主流程
            }
            finally
            {
                context.Response.Close();
            }
        }

        private static byte[] LoadAccountPage(string fileName)
        {
            try
            {
                string diskPath = Path.Combine(AccountDiskPath, fileName);
                if (File.Exists(diskPath)) return File.ReadAllBytes(diskPath);
            }
            catch { }

            try
            {
                Assembly assembly = typeof(WebServer).Assembly;
                string suffix = ".Account." + fileName;

                foreach (string resource in assembly.GetManifestResourceNames())
                {
                    if (!resource.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

                    using (Stream stream = assembly.GetManifestResourceStream(resource))
                    {
                        if (stream == null) return null;

                        using (MemoryStream memory = new MemoryStream())
                        {
                            stream.CopyTo(memory);
                            return memory.ToArray();
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private static void SystemDBSync(HttpListenerContext context)
        {
            try
            {
                if (!Config.AllowSystemDBSync)
                {
                    SEnvir.Log($"尝试同步，但同步功能未启用");
                    context.Response.StatusCode = 401;
                    return;
                }

                if (context.Request.HttpMethod != "POST" || !context.Request.HasEntityBody)
                {
                    SEnvir.Log($"尝试同步，但请求方法不是 POST 或请求正文为空");
                    context.Response.StatusCode = 401;
                    return;
                }

                if (context.Request.ContentLength64 > 1024 * 1024 * 10)
                {
                    SEnvir.Log($"尝试同步，但数据超过 SystemDB 大小限制");
                    context.Response.StatusCode = 400;
                    return;
                }

                var masterPassword = context.Request.QueryString["Key"];
                if (string.IsNullOrEmpty(masterPassword) || !masterPassword.Equals(Config.SyncKey))
                {
                    SEnvir.Log($"尝试同步，但收到的密钥无效");
                    context.Response.StatusCode = 400;
                    return;
                }

                SEnvir.Log($"正在开始远程同步...");

                var buffer = new byte[context.Request.ContentLength64];
                var offset = 0;
                var length = 0;
                var bufferSize = 1024 * 16;

                while ((length = context.Request.InputStream.Read(buffer, offset, offset + bufferSize > buffer.Length ? buffer.Length - offset : bufferSize)) > 0)
                    offset += length;

                if (SEnvir.Session.BackUp && !Directory.Exists(SEnvir.Session.SystemBackupPath))
                    Directory.CreateDirectory(SEnvir.Session.SystemBackupPath);

                if (File.Exists(SEnvir.Session.SystemPath))
                {
                    if (SEnvir.Session.BackUp)
                    {
                        using (FileStream sourceStream = File.OpenRead(SEnvir.Session.SystemPath))
                        using (FileStream destStream = File.Create(SEnvir.Session.SystemBackupPath + "System " + SEnvir.Session.ToBackUpFileName(DateTime.UtcNow) + Session.Extension + Session.CompressExtension))
                        using (GZipStream compress = new GZipStream(destStream, CompressionMode.Compress))
                            sourceStream.CopyTo(compress);
                    }

                    File.Delete(SEnvir.Session.SystemPath);
                }

                File.WriteAllBytes(SEnvir.Session.SystemPath, buffer);

                context.Response.StatusCode = 200;

                SEnvir.Log($"同步完成...");
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "text/plain";
                var message = Encoding.UTF8.GetBytes(ex.ToString());
                context.Response.OutputStream.Write(message, 0, message.Length);
                SEnvir.Log("同步异常：" + ex.ToString());
            }
            finally
            {
                context.Response.Close();
            }
        }

        private static void Activation(HttpListenerContext context)
        {
            string key = context.Request.QueryString[ActivationKey];

            if (string.IsNullOrEmpty(key))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                return;
            }

            AccountInfo account = null;
            for (int i = 0; i < SEnvir.AccountInfoList.Count; i++)
            {
                AccountInfo temp = SEnvir.AccountInfoList[i]; //Different Threads, Caution must be taken to prevent errors
                if (string.Compare(temp.ActivationKey, key, StringComparison.Ordinal) != 0) continue;

                account = temp;
                break;
            }

            if (Config.AllowWebActivation && account != null)
            {
                WebCommandQueue.Enqueue(new WebCommand(CommandType.Activation, account));
                context.Response.Redirect(Config.ActivationSuccessLink);
            }
            else
                context.Response.Redirect(Config.ActivationFailLink);

            context.Response.Close();
        }
        private static void ResetPassword(HttpListenerContext context)
        {
            string key = context.Request.QueryString[ResetKey];

            if (string.IsNullOrEmpty(key))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                return;
            }

            AccountInfo account = null;
            for (int i = 0; i < SEnvir.AccountInfoList.Count; i++)
            {
                AccountInfo temp = SEnvir.AccountInfoList[i]; //Different Threads, Caution must be taken to prevent errors
                if (string.Compare(temp.ResetKey, key, StringComparison.Ordinal) != 0) continue;

                account = temp;
                break;
            }

            if (Config.AllowWebResetPassword && account != null && account.ResetTime.AddMinutes(25) > SEnvir.Now)
            {
                WebCommandQueue.Enqueue(new WebCommand(CommandType.PasswordReset, account));
                context.Response.Redirect(Config.ResetSuccessLink);
            }
            else
                context.Response.Redirect(Config.ResetFailLink);

            context.Response.Close();
        }
        private static void DeleteAccount(HttpListenerContext context)
        {
            string key = context.Request.QueryString[DeleteKey];

            if (string.IsNullOrEmpty(key))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                return;
            }

            AccountInfo account = null;
            for (int i = 0; i < SEnvir.AccountInfoList.Count; i++)
            {
                AccountInfo temp = SEnvir.AccountInfoList[i]; //Different Threads, Caution must be taken to prevent errors
                if (string.Compare(temp.ActivationKey, key, StringComparison.Ordinal) != 0) continue;

                account = temp;
                break;
            }

            if (Config.AllowDeleteAccount && account != null)
            {
                WebCommandQueue.Enqueue(new WebCommand(CommandType.AccountDelete, account));
                context.Response.Redirect(Config.DeleteSuccessLink);
            }
            else
                context.Response.Redirect(Config.DeleteFailLink);

            context.Response.Close();
        }

        private static void BuyConnection(IAsyncResult result)
        {
            try
            {
                HttpListenerContext context = BuyListener.EndGetContext(result);

                string characterName = context.Request.QueryString["Character"];

                CharacterInfo character = null;
                for (int i = 0; i < SEnvir.CharacterInfoList.Count; i++)
                {
                    if (string.Compare(SEnvir.CharacterInfoList[i].CharacterName, characterName, StringComparison.OrdinalIgnoreCase) != 0) continue;

                    character = SEnvir.CharacterInfoList[i];
                    break;
                }

                if (character?.Account.Key != context.Request.QueryString["Key"])
                    character = null;

                string response = character == null ? Properties.Resources.CharacterNotFound : Properties.Resources.BuyGameGold.Replace("$CHARACTERNAME$", character.CharacterName);

                using (StreamWriter writer = new StreamWriter(context.Response.OutputStream, context.Request.ContentEncoding))
                    writer.Write(response);
            }
            catch { }
            finally
            {
                if (BuyListener != null && BuyListener.IsListening) //IsBound ?
                    BuyListener.BeginGetContext(BuyConnection, null);
            }

        }
        private static void IPNConnection(IAsyncResult result)
        {
            const string LiveURL = @"https://ipnpb.paypal.com/cgi-bin/webscr";

            const string verified = "VERIFIED";

            try
            {
                if (IPNListener == null || !IPNListener.IsListening) return;

                HttpListenerContext context = IPNListener.EndGetContext(result);

                string rawMessage;
                using (StreamReader readStream = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    rawMessage = readStream.ReadToEnd();


                Task.Run(async () =>
                {
                    string data = "cmd=_notify-validate&" + rawMessage;

                    // Create a handler with explicit TLS settings
                    var handler = new HttpClientHandler
                    {
                        SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                     | System.Security.Authentication.SslProtocols.Tls13
                    };

                    using (HttpClient httpClient = new HttpClient(handler))
                    {
                        var content = new StringContent(data, Encoding.ASCII, "application/x-www-form-urlencoded");
                        var response = await httpClient.PostAsync(LiveURL, content);

                        IPNMessage message = new IPNMessage { Message = rawMessage, Verified = await response.Content.ReadAsStringAsync() == verified };

                        if (!Directory.Exists(VerifiedPath))
                            Directory.CreateDirectory(VerifiedPath);

                        if (!Directory.Exists(InvalidPath))
                            Directory.CreateDirectory(InvalidPath);

                        string path = (message.Verified ? VerifiedPath : InvalidPath) + Path.GetRandomFileName();

                        File.WriteAllText(path, message.Message);

                        message.FileName = path;

                        Messages.Enqueue(message);
                    }
                });

                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.Close();
            }
            catch (Exception ex)
            {
                SEnvir.Log(ex.ToString());
            }
            finally
            {
                if (IPNListener != null && IPNListener.IsListening) //IsBound ?
                    IPNListener.BeginGetContext(IPNConnection, null);
            }
        }

        public static void Process()
        {
            while (!WebCommandQueue.IsEmpty)
            {
                if (!WebCommandQueue.TryDequeue(out WebCommand webCommand)) continue;

                switch (webCommand.Command)
                {
                    case CommandType.None:
                        break;
                    case CommandType.Activation:
                        webCommand.Account.Activated = true;
                        webCommand.Account.ActivationKey = string.Empty;
                        break;
                    case CommandType.PasswordReset:
                        string password = Functions.RandomString(SEnvir.Random, 10);

                        webCommand.Account.Password = SEnvir.CreateHash(password);
                        webCommand.Account.ResetKey = string.Empty;
                        webCommand.Account.WrongPasswordCount = 0;
                        EmailService.SendResetPasswordEmail(webCommand.Account, password);
                        break;
                    case CommandType.AccountDelete:
                        if (webCommand.Account.Activated) continue;

                        webCommand.Account.Delete();
                        break;
                }
            }
        }

        public static void Save()
        {
            HandledPayments.AddRange(PaymentList);
        }

        public static void CommitChanges(object data)
        {
            foreach (IPNMessage message in HandledPayments)
            {
                if (message.Duplicate)
                {
                    File.Delete(message.FileName);
                    continue;
                }

                if (!Directory.Exists(CompletePath))
                    Directory.CreateDirectory(CompletePath);

                File.Move(message.FileName, CompletePath + Path.GetFileName(message.FileName) + ".txt");
                PaymentList.Remove(message);
            }
            HandledPayments.Clear();
        }
    }
}

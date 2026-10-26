using Library;
using Server.DBModels;
using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace Server.Envir
{
    public static class EmailService
    {
        public static int EMailsSent;

        public static void SendActivationEmail(AccountInfo account)
        {
            account.ActivationKey = Functions.RandomString(SEnvir.Random, 20);
            account.ActivationTime = SEnvir.Now.AddMinutes(5);
            EMailsSent++;

            Task.Run(() =>
            {
                try
                {

                    SmtpClient client = new SmtpClient(Config.MailServer, Config.MailPort)
                    {
                        EnableSsl = Config.MailUseSSL,
                        UseDefaultCredentials = false,

                        Credentials = new NetworkCredential(Config.MailAccount, Config.MailPassword),
                    };

                    MailMessage message = new MailMessage(new MailAddress(Config.MailFrom, Config.MailDisplayName), new MailAddress(account.EMailAddress))
                    {
                        Subject = "请激活你的账户",
                        IsBodyHtml = true,

                        Body = $"Dear {account.RealName}, <br><br>" +
                               $"感谢您注册了账户，登录游戏前，你需要先激活您的账户<br><br>" +
                               $"要完成注册并激活账户，请点击以下链接:<br>" +
                               $"<a href=\"{Config.WebCommandLink}?Type={WebServer.ActivationCommand}&{WebServer.ActivationKey}={account.ActivationKey}\">点击这里激活账户</a><br><br>" +
                               $"如果上述链接无效，请在下次登录账户时使用以下激活密钥<br>" +
                               $"激活密钥: {account.ActivationKey}<br><br>" +
                               (account.Referral != null ? $"You were referred by: {account.Referral.EMailAddress}<br><br>" : "") +
                               $"如果您未创建此账户，且想取消注册以删除此账户，请访问以下链接:<br>" +
                               $"<a href=\"{Config.WebCommandLink}?Type={WebServer.DeleteCommand}&{WebServer.DeleteKey}={account.ActivationKey}\">点击这里删除账户</a><br><br>" +
                               $"游戏见<br>"
                    };

                    client.Send(message);

                    message.Dispose();
                    client.Dispose();
                }
                catch (Exception ex)
                {
                    SEnvir.Log(ex.Message);
                    SEnvir.Log(ex.StackTrace);
                }
            });
        }
        public static void ResendActivationEmail(AccountInfo account)
        {
            if (string.IsNullOrEmpty(account.ActivationKey))
                account.ActivationKey = Functions.RandomString(SEnvir.Random, 20);

            account.ActivationTime = SEnvir.Now.AddMinutes(15);
            EMailsSent++;

            Task.Run(() =>
            {
                try
                {
                    SmtpClient client = new SmtpClient(Config.MailServer, Config.MailPort)
                    {
                        EnableSsl = Config.MailUseSSL,
                        UseDefaultCredentials = false,
                        Credentials = new NetworkCredential(Config.MailAccount, Config.MailPassword),
                    };

                    MailMessage message = new MailMessage(new MailAddress(Config.MailFrom, Config.MailDisplayName), new MailAddress(account.EMailAddress))
                    {
                        Subject = "Zircon Account Activation",
                        IsBodyHtml = false,

                        Body = $"亲爱的 {account.RealName}\n" +
                               $"\n" +
                               $"感谢您注册了账户，登录游戏前必须先激活账户.\n" +
                               $"\n" +
                               $"下次尝试登录账户时，请使用以下激活密钥.\n" +
                               $"激活密钥: {account.ActivationKey}\n\n" +
                               $"游戏中见\n" +
                               $"\n" +
                               $"为确保发送成功，本邮件使用纯文本发送。",
                    };

                    client.Send(message);

                    message.Dispose();
                    client.Dispose();
                }
                catch (Exception ex)
                {
                    SEnvir.Log(ex.Message);
                    SEnvir.Log(ex.StackTrace);
                }
            });
        }
        public static void SendChangePasswordEmail(AccountInfo account, string ipAddress)
        {
            if (SEnvir.Now < account.PasswordTime)
                return;

            account.PasswordTime = Time.Now.AddMinutes(60);

            EMailsSent++;
            Task.Run(() =>
            {
                try
                {
                    SmtpClient client = new SmtpClient(Config.MailServer, Config.MailPort)
                    {
                        EnableSsl = Config.MailUseSSL,
                        UseDefaultCredentials = false,
                        Credentials = new NetworkCredential(Config.MailAccount, Config.MailPassword),
                    };

                    MailMessage message = new MailMessage(new MailAddress(Config.MailFrom, Config.MailDisplayName), new MailAddress(account.EMailAddress))
                    {
                        Subject = "Zircon Password Changed",
                        IsBodyHtml = true,

                        Body = $"Dear {account.RealName}, <br><br>" +
                               $"This is an E-Mail to inform you that your password for Zircon has been changed.<br>" +
                               $"IP Address: {ipAddress}<br><br>" +
                               $"If you did not make this change please contact an administrator immediately.<br><br>" +
                               $"We'll see you in game<br>" +
                               $"<a href=\"http://www.zirconserver.com\">Zircon Server</a>"
                    };

                    client.Send(message);

                    message.Dispose();
                    client.Dispose();
                }
                catch (Exception ex)
                {
                    SEnvir.Log(ex.Message);
                    SEnvir.Log(ex.StackTrace);
                }
            });
        }
        public static void SendResetPasswordRequestEmail(AccountInfo account, string ipAddress)
        {
            account.ResetKey = Functions.RandomString(SEnvir.Random, 20);
            account.ResetTime = SEnvir.Now.AddMinutes(5);
            EMailsSent++;

            Task.Run(() =>
            {
                try
                {
                    SmtpClient client = new SmtpClient(Config.MailServer, Config.MailPort)
                    {
                        EnableSsl = Config.MailUseSSL,
                        UseDefaultCredentials = false,
                        Credentials = new NetworkCredential(Config.MailAccount, Config.MailPassword),
                    };

                    MailMessage message = new MailMessage(new MailAddress(Config.MailFrom, Config.MailDisplayName), new MailAddress(account.EMailAddress))
                    {
                        Subject = "Zircon Password Reset",
                        IsBodyHtml = true,

                        Body = $"Dear {account.RealName}, <br><br>" +
                               $"A request to reset your password has been made.<br>" +
                               $"IP Address: {ipAddress}<br><br>" +
                               $"To reset your password please click on the following link:<br>" +
                               $"<a href=\"{Config.WebCommandLink}?Type={WebServer.ResetCommand}&{WebServer.ResetKey}={account.ResetKey}\">Reset Password</a><br><br>" +
                               $"If the above link does not work please use the following Reset Key to reset your password<br>" +
                               $"Reset Key: {account.ResetKey}<br><br>" +
                               $"If you did not request this reset, please ignore this email as your password will not be changed.<br><br>" +
                               $"We'll see you in game<br>" +
                               $"<a href=\"http://www.zirconserver.com\">Zircon Server</a>"
                    };

                    client.Send(message);

                    message.Dispose();
                    client.Dispose();
                }
                catch (Exception ex)
                {
                    SEnvir.Log(ex.Message);
                    SEnvir.Log(ex.StackTrace);
                }
            });
        }
        public static void SendResetPasswordEmail(AccountInfo account, string password)
        {
            account.ResetKey = Functions.RandomString(SEnvir.Random, 20);
            account.ResetTime = SEnvir.Now.AddMinutes(5);
            EMailsSent++;

            Task.Run(() =>
            {
                try
                {
                    SmtpClient client = new SmtpClient(Config.MailServer, Config.MailPort)
                    {
                        EnableSsl = Config.MailUseSSL,
                        UseDefaultCredentials = false,
                        Credentials = new NetworkCredential(Config.MailAccount, Config.MailPassword),
                    };

                    MailMessage message = new MailMessage(new MailAddress(Config.MailFrom, Config.MailDisplayName), new MailAddress(account.EMailAddress))
                    {
                        Subject = "Zircon Password has been Reset.",
                        IsBodyHtml = true,

                        Body = $"Dear {account.RealName}, <br><br>" +
                               $"This is an E-Mail to inform you that your password for Zircon has been reset.<br>" +
                               $"Your new password: {password}<br><br>" +
                               $"If you did not make this reset please contact an administrator immediately.<br><br>" +
                               $"We'll see you in game<br>" +
                               $"<a href=\"http://www.zirconserver.com\">Zircon Server</a>"
                    };

                    client.Send(message);

                    message.Dispose();
                    client.Dispose();
                }
                catch (Exception ex)
                {
                    SEnvir.Log(ex.Message);
                    SEnvir.Log(ex.StackTrace);
                }
            });
        }

    }
}

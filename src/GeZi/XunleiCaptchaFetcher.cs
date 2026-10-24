using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using GeZi.Core.Api;
using GeZi.Core.Support;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GeZi
{
    /// <summary>
    /// 迅雷「网页路线」的验证码取用器。
    /// <para>
    /// <b>为什么需要它：</b>迅雷云盘的 <c>captcha/init</c> 盾按 <c>client_id</c> 分体系 ——
    /// App 的 client_id 和网页的 client_id 签发的 <c>captcha_token</c> 互不通用。
    /// 我们的 <c>pan_token</c> 来自网页登录（<see cref="XunleiConstants.ClientId"/>），
    /// 所以必须配网页体系签发的 captcha；用 App 体系去申请会被判
    /// <c>captcha_invalid / detail="client_id not match"</c>。
    /// </para>
    /// <para>
    /// <b>为什么不自己算签名：</b>网页的 <c>captcha_sign</c> 由一层层混淆的
    /// <c>pub*</c> 函数链算出（service-pubkey 动态加载），纯算法复现成本高且随版本失效。
    /// 网页自己会在 localStorage 里写好 <c>captcha_&lt;clientId&gt;</c>
    /// （含 <c>token</c> 与 <c>expires_at</c>）和 <c>deviceid</c>，
    /// 我们只要把 WebView2 打开几秒读出来即可 —— 这也是 refs/YunX 的做法。
    /// </para>
    /// <para>
    /// <b>device_id 必须原样用网页那份：</b>形如 <c>wdi10.&lt;64 位 hex&gt;</c>。
    /// 实测把它截断成 32 位或用随机值，都会得到
    /// <c>captcha_invalid / detail="device not match"</c>。
    /// </para>
    /// </summary>
    internal static class XunleiCaptchaFetcher
    {
        private const string LoginUrl = "https://pan.xunlei.com/";
        private const int WaitMs = 12000;

        /// <summary>
        /// 打开一次隐藏的 WebView2，读出网页当前的 captcha 与 deviceid。
        /// 失败返回 <c>(null, null)</c>，调用方按「没有可用 captcha」处理。
        /// </summary>
        public static async Task<(string captcha, string deviceId)> FetchAsync()
        {
            var app = Application.Current;
            if (app == null)
                return (null, null);

            try
            {
                var op = app.Dispatcher.InvokeAsync(() => FetchOnUiThreadAsync());
                return await op.Task.Unwrap().ConfigureAwait(false);
            }
            catch
            {
                return (null, null);
            }
        }

        private static async Task<(string captcha, string deviceId)> FetchOnUiThreadAsync()
        {
            Window win = null;
            WebView2 web = null;
            try
            {
                win = new Window
                {
                    Width = 420,
                    Height = 320,
                    // 放到屏幕外 + 不抢焦点：用户点「刷新列表」时不该突然弹一个窗口
                    Left = -4000,
                    Top = -4000,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowActivated = false,
                    Title = "迅雷验证码",
                };

                web = new WebView2();
                win.Content = web;
                win.Show();

                string folder = WebView2Runtime.CreateIsolatedDataFolder(PanKind.Xunlei.ToString());
                var env = await CoreWebView2Environment.CreateAsync(null, folder).ConfigureAwait(true);
                await web.EnsureCoreWebView2Async(env).ConfigureAwait(true);

                try
                {
                    web.CoreWebView2.Settings.UserAgent = XunleiConstants.DesktopUa;
                }
                catch { }

                web.CoreWebView2.Navigate(LoginUrl);

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(WaitMs);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(700).ConfigureAwait(true);

                    string cap = await ReadStorageAsync(web, "captcha_" + XunleiConstants.ClientId)
                        .ConfigureAwait(true);
                    if (string.IsNullOrEmpty(cap))
                        continue;

                    string token = ExtractCaptchaToken(cap);
                    if (string.IsNullOrEmpty(token) || token.Length < 16)
                        continue;
                    if (IsExpired(cap))
                        continue;

                    string dev = await ReadStorageAsync(web, "deviceid").ConfigureAwait(true);
                    return (token, string.IsNullOrEmpty(dev) ? null : dev.Trim());
                }

                return (null, null);
            }
            catch
            {
                return (null, null);
            }
            finally
            {
                try
                {
                    if (web != null)
                    {
                        web.Dispose();
                    }
                }
                catch { }
                try
                {
                    if (win != null)
                    {
                        win.Close();
                    }
                }
                catch { }
            }
        }

        private static async Task<string> ReadStorageAsync(WebView2 web, string key)
        {
            try
            {
                if (web == null || web.CoreWebView2 == null)
                    return null;
                string script = "(function(){try{var v=localStorage.getItem('" + key + "');"
                              + "return v===null?'':v;}catch(e){return '';}})()";
                string raw = await web.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
                return Unquote(raw);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>从 <c>captcha_&lt;cid&gt;</c> 的 JSON 里取 <c>token</c>。</summary>
        private static string ExtractCaptchaToken(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            var m = Regex.Match(json, "\"token\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>判断 <c>expires_at</c> 是否已过期（解析不出来就当没过期）。</summary>
        private static bool IsExpired(string json)
        {
            if (string.IsNullOrEmpty(json))
                return true;
            var m = Regex.Match(json, "\"expires_at\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success)
                return false;
            DateTimeOffset when;
            if (DateTimeOffset.TryParse(m.Groups[1].Value, out when))
                return when <= DateTimeOffset.UtcNow.AddSeconds(-5);
            return false;
        }

        /// <summary><c>ExecuteScriptAsync</c> 的结果是 JSON 编码过的字符串，去掉外层引号。</summary>
        private static string Unquote(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";
            string s = raw.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            {
                s = s.Substring(1, s.Length - 2);
                s = s.Replace("\\\"", "\"").Replace("\\\\", "\\")
                     .Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t")
                     .Replace("\\/", "/");
            }
            return s;
        }
    }
}

// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GeZi.Core.Api;
using GeZi.Providers;
using GeZi.Providers.Quark;
using GeZi.Providers.Uc;
using GeZi.Providers.Aliyun;
using GeZi.Providers.Xunlei;
using GeZi.Providers.Shared;
using GeZi.Providers.Qr;

using GeZi.Services;
namespace GeZi
{
    /// <summary>
    /// 迅雷账号登录窗口（手机号 + 短信验证码）。
    /// <para>
    /// <b>为什么单独做这条登录路线：</b>内嵌网页登录只能拿到 <c>pan_token</c>，
    /// 它约两小时过期且没有续期凭据 —— 用户每天都要重新登录，这正是"迅雷一直卡在
    /// pan_token 无效"的根源。账号登录能拿到 <c>refresh_token</c>，可以长期续期。
    /// </para>
    /// <para>
    /// 全程两段：先发短信拿 <c>creditkey</c>，再用验证码换 <c>sessionID</c>，
    /// 最后用 <c>sessionID</c> 换业务令牌（中间要过一次 captcha 初始化）。
    /// 这两步在 <see cref="XunleiLoginService"/> 里完成，本窗口只负责交互与错误提示。
    /// </para>
    /// </summary>
    internal sealed class XunleiLoginWindow : Window
    {
        private readonly Action<XunleiLoginResult, string> _onSuccess;
        private readonly Action<string> _log;
        private readonly string _savedDeviceId;

        private XunleiLoginService _service;

        private TextBox _mobileBox;
        private Button _sendBtn;
        private TextBox _codeBox;
        private Button _loginBtn;
        private TextBlock _status;
        private bool _busy;

        /// <param name="savedDeviceId">上次保存的设备标识（复用可避免被当成新设备触发风控）。</param>
        /// <param name="onSuccess">登录成功回调：结果 + 设备标识（调用方负责持久化）。</param>
        /// <param name="log">日志回调（可为 null）。</param>
        public XunleiLoginWindow(string savedDeviceId,
            Action<XunleiLoginResult, string> onSuccess, Action<string> log)
        {
            _savedDeviceId = savedDeviceId ?? "";
            _onSuccess = onSuccess;
            _log = log;

            Title = "迅雷云盘 账号登录";
            Width = 460;
            Height = 380;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = Brushes.White;

            _service = new XunleiLoginService(_savedDeviceId);

            BuildLayout();
            Closed += OnClosed;
        }

        /// <summary>登录成功后拿到的设备标识（调用方应持久化）。</summary>
        public string DeviceId
        {
            get { return _service == null ? _savedDeviceId : _service.DeviceId; }
        }

        private void BuildLayout()
        {
            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

            root.Children.Add(new TextBlock
            {
                Text = "用手机号接收验证码登录迅雷云盘。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x57, 0x53, 0x4E)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock
            {
                Text = "这条路线会拿到长期刷新令牌，登录一次可以用很久（网页登录约两小时就失效）。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x78, 0x71, 0x6C)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16),
            });

            // ① 手机号 + 发送
            root.Children.Add(Label("手机号"));
            var row1 = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _mobileBox = new TextBox
            {
                Height = 34,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 13,
                ToolTip = "接收验证码的手机号",
            };
            Grid.SetColumn(_mobileBox, 0);
            row1.Children.Add(_mobileBox);

            _sendBtn = new Button
            {
                Content = "发送验证码",
                Width = 110,
                Height = 34,
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            _sendBtn.Click += OnSendSms;
            Grid.SetColumn(_sendBtn, 1);
            row1.Children.Add(_sendBtn);
            root.Children.Add(row1);

            // ② 验证码 + 登录
            root.Children.Add(Label("短信验证码"));
            var row2 = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _codeBox = new TextBox
            {
                Height = 34,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 13,
                IsEnabled = false,
                ToolTip = "短信里收到的验证码",
            };
            Grid.SetColumn(_codeBox, 0);
            row2.Children.Add(_codeBox);

            _loginBtn = new Button
            {
                Content = "登录",
                Width = 110,
                Height = 34,
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            _loginBtn.Click += OnLogin;
            Grid.SetColumn(_loginBtn, 1);
            row2.Children.Add(_loginBtn);
            root.Children.Add(row2);

            // ③ 状态
            _status = new TextBlock
            {
                Text = "先填手机号，点「发送验证码」。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x78, 0x71, 0x6C)),
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 32,
            };
            root.Children.Add(_status);

            var cancel = new Button
            {
                Content = "取消",
                Width = 88,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            cancel.Click += (s, e) => Close();
            root.Children.Add(cancel);

            Content = root;
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x40, 0x3C)),
                Margin = new Thickness(0, 0, 0, 6),
            };
        }

        private void SetStatus(string text, bool error)
        {
            if (_status == null)
                return;
            _status.Text = text ?? "";
            _status.Foreground = new SolidColorBrush(error
                ? Color.FromRgb(0xB4, 0x1E, 0x1E)
                : Color.FromRgb(0x78, 0x71, 0x6C));
        }

        private async void OnSendSms(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            string mobile = (_mobileBox.Text ?? "").Trim();
            if (!XunleiLoginService.IsMobile(mobile))
            {
                SetStatus("请输入正确的手机号。", true);
                _mobileBox.Focus();
                return;
            }

            _busy = true;
            _sendBtn.IsEnabled = false;
            SetStatus("正在发送验证码…", false);
            try
            {
                bool ok = await _service.SendSmsAsync(mobile).ConfigureAwait(true);
                if (ok)
                {
                    SetStatus("验证码已发送，请查看短信并填入下面的输入框。", false);
                    _codeBox.IsEnabled = true;
                    _loginBtn.IsEnabled = true;
                    _codeBox.Focus();
                    Log("迅雷：验证码已发送到 " + MaskMobile(mobile));
                }
                else
                {
                    SetStatus(_service.LastError, true);
                    Log("迅雷：发送验证码失败 —— " + _service.LastError);
                }
            }
            catch (Exception ex)
            {
                SetStatus("发送验证码失败：" + ex.Message, true);
                Log("迅雷：发送验证码异常 —— " + ex.Message);
            }
            finally
            {
                _busy = false;
                _sendBtn.IsEnabled = true;
            }
        }

        private async void OnLogin(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            string code = (_codeBox.Text ?? "").Trim();
            if (code.Length < 4)
            {
                SetStatus("请输入短信里的验证码。", true);
                _codeBox.Focus();
                return;
            }

            _busy = true;
            _loginBtn.IsEnabled = false;
            _sendBtn.IsEnabled = false;
            SetStatus("正在登录…", false);
            try
            {
                var result = await _service.VerifySmsAsync(code).ConfigureAwait(true);
                if (result == null || !result.Ok)
                {
                    SetStatus(_service.LastError, true);
                    Log("迅雷：登录失败 —— " + _service.LastError);
                    _loginBtn.IsEnabled = true;
                    _sendBtn.IsEnabled = true;
                    return;
                }

                Log("迅雷：账号登录成功（昵称=" + (result.NickName ?? "") + "）");
                if (_onSuccess != null)
                    _onSuccess(result, _service.DeviceId);
                Close();
            }
            catch (Exception ex)
            {
                SetStatus("登录失败：" + ex.Message, true);
                Log("迅雷：登录异常 —— " + ex.Message);
                _loginBtn.IsEnabled = true;
                _sendBtn.IsEnabled = true;
            }
            finally
            {
                _busy = false;
            }
        }

        private static string MaskMobile(string mobile)
        {
            if (string.IsNullOrEmpty(mobile) || mobile.Length < 7)
                return mobile ?? "";
            return mobile.Substring(0, 3) + "****" + mobile.Substring(mobile.Length - 4);
        }

        private void Log(string message)
        {
            if (_log != null)
                _log(message);
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_service != null)
            {
                try { _service.Dispose(); } catch { }
                _service = null;
            }
        }
    }
}

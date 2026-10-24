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
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GeZi.SelfTest
{
    /// <summary>
    /// 极简 HTTP/1.1 服务器，用于**契约测试**：验证网盘客户端发出的
    /// 请求路径、请求头、请求体是否符合协议，以及能否正确解析响应。
    /// <para>
    /// <b>为什么不用 HttpListener：</b>它需要 URL ACL（非管理员通常注册不了）。
    /// 这里直接开 <see cref="TcpListener"/> 手写响应，普通用户权限即可运行，
    /// 在 CI 里也稳定。
    /// </para>
    /// <para>
    /// 每个请求都会被记录下来（<see cref="Requests"/>），断言就靠它。
    /// </para>
    /// </summary>
    internal sealed class MockHttpServer : IDisposable
    {
        internal sealed class RecordedRequest
        {
            public string Method;
            public string Path;                 // 含 query
            public Dictionary<string, string> Headers =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Body;

            public string Header(string name)
            {
                string v;
                return Headers.TryGetValue(name, out v) ? v : null;
            }
        }

        /// <summary>mock 的响应：体 + 可选的额外响应头（例如 Set-Cookie）。</summary>
        internal sealed class MockResponse
        {
            public string Body = "";
            public List<KeyValuePair<string, string>> Headers =
                new List<KeyValuePair<string, string>>();

            public MockResponse() { }

            public MockResponse(string body)
            {
                Body = body ?? "";
            }

            public MockResponse WithHeader(string name, string value)
            {
                Headers.Add(new KeyValuePair<string, string>(name, value));
                return this;
            }
        }

        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private readonly Func<RecordedRequest, MockResponse> _responder;
        private readonly List<RecordedRequest> _requests = new List<RecordedRequest>();
        private readonly object _lock = new object();
        private volatile bool _running = true;

        /// <summary>监听端口（构造后立即可用）。</summary>
        public int Port { get; private set; }

        /// <summary>访问它作为 base URL。</summary>
        public string BaseUrl
        {
            get { return "http://127.0.0.1:" + Port; }
        }

        /// <summary>已收到的请求（按到达顺序）。</summary>
        public List<RecordedRequest> Requests
        {
            get { lock (_lock) { return new List<RecordedRequest>(_requests); } }
        }

        /// <param name="responder">根据请求返回 mock 响应（HTTP 200）。</param>
        public MockHttpServer(Func<RecordedRequest, MockResponse> responder)
        {
            _responder = responder;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _thread = new Thread(Loop) { IsBackground = true, Name = "mock-http" };
            _thread.Start();
        }

        private void Loop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    return;     // listener 已关闭
                }

                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var req = ReadRequest(stream);
                        if (req == null)
                            continue;

                        lock (_lock) { _requests.Add(req); }

                        MockResponse resp;
                        try { resp = _responder(req) ?? new MockResponse(); }
                        catch (Exception ex) { resp = new MockResponse("{\"error\":\"" + ex.GetType().Name + "\"}"); }

                        WriteResponse(stream, resp);
                    }
                }
                catch
                {
                    // 单个连接出错不影响后续请求
                }
            }
        }

        private static RecordedRequest ReadRequest(NetworkStream stream)
        {
            // 读到 \r\n\r\n 为止（请求头结束）
            var head = new MemoryStream();
            int match = 0;
            while (match < 4)
            {
                int b = stream.ReadByte();
                if (b < 0)
                    return null;
                head.WriteByte((byte)b);
                if ((b == '\r' && (match == 0 || match == 2))
                    || (b == '\n' && (match == 1 || match == 3)))
                    match++;
                else
                    match = 0;
            }

            string headText = Encoding.UTF8.GetString(head.ToArray());
            string[] lines = headText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
                return null;

            string[] first = lines[0].Split(' ');
            if (first.Length < 2)
                return null;

            var req = new RecordedRequest { Method = first[0], Path = first[1] };

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line))
                    continue;
                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                req.Headers[name] = value;
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, out contentLength);
            }

            if (contentLength > 0)
            {
                var buf = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int n = stream.Read(buf, read, contentLength - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
                req.Body = Encoding.UTF8.GetString(buf, 0, read);
            }
            else
            {
                req.Body = "";
            }

            return req;
        }

        private static void WriteResponse(NetworkStream stream, MockResponse resp)
        {
            byte[] payload = Encoding.UTF8.GetBytes(resp.Body ?? "");
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 200 OK\r\n");
            sb.Append("Content-Type: application/json; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            foreach (var h in resp.Headers)
                sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            if (payload.Length > 0)
                stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _thread.Join(1000); } catch { }
        }
    }
}
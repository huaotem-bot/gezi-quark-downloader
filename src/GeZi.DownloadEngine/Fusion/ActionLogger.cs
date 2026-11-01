// GeZi - 鸽子下载器
// Copyright (C) 2026 Yi Yuan
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
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Threading.Tasks;
using GeZi.DownloadEngine.Framework.Extensions.Logging;

namespace GeZi.DownloadEngine;

/// <summary>
/// 把 bezzad 的 <see cref="ILogger"/> 桥接到一个 <c>Action&lt;string&gt;</c>。
///
/// 融合引擎对外只暴露一个「日志回调」比暴露 <see cref="ILogger"/> 干净：
/// 调用方（MainWindow / 探针）不需要认识 bezzad 的类型，而框架内部仍能拿到分级日志。
/// 这也让续传、校验这些「静默 return」的分支有了可诊断的出口。
/// </summary>
public sealed class ActionLogger : ILogger
{
    private readonly Action<string> _sink;

    public ActionLogger(Action<string> sink)
    {
        _sink = sink;
    }

    private void Write(string level, string message)
    {
        Action<string> sink = _sink;
        if (sink == null)
        {
            return;
        }

        try
        {
            sink("[" + level + "] " + message);
        }
        catch
        {
            // 日志回调本身出错绝不能影响下载
        }
    }

    public void LogDebug(string message) => Write("debug", message);

    public void LogInfo(string message) => Write("info", message);

    public void LogWarning(string message) => Write("warn", message);

    public void LogError(string message) => Write("error", message);

    public void LogError(Exception exception, string message) =>
        Write("error", message + (exception == null ? "" : " :: " + exception.Message));

    public void LogCritical(string message) => Write("critical", message);

    public void LogCritical(Exception exception, string message) =>
        Write("critical", message + (exception == null ? "" : " :: " + exception.Message));

    public string Formatter(string logType, string message, Exception exception)
    {
        return "[" + logType + "] " + message + (exception == null ? "" : " :: " + exception.Message);
    }

    public Task FlushAsync()
    {
        return Task.FromResult(0);
    }
}

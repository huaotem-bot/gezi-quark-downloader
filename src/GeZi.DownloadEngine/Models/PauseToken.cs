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

using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.DownloadEngine
{
    /// <summary>暂停令牌：替代 Python 里轮询 pause() 回调的方式。</summary>
    public sealed class PauseToken
    {
        private readonly ManualResetEventSlim _resumed = new ManualResetEventSlim(true);

        public bool IsPaused => !_resumed.IsSet;

        public void Pause() => _resumed.Reset();

        public void Resume() => _resumed.Set();

        /// <summary>暂停期间阻塞；取消时抛 OperationCanceledException。</summary>
        public async Task WaitIfPausedAsync(CancellationToken ct)
        {
            while (!_resumed.IsSet)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }
}

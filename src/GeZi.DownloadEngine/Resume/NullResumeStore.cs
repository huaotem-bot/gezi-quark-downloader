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

using System.Threading;
using System.Threading.Tasks;

namespace GeZi.DownloadEngine.Resume;

/// <summary>
/// 不存续传的空实现。作为 <c>DownloadConfiguration.ResumeStore</c> 的默认值，
/// 保证「不显式开启就没有任何磁盘副作用」。
/// </summary>
public sealed class NullResumeStore : IResumeStore
{
    public static readonly NullResumeStore Instance = new NullResumeStore();

    public string Name => "null";

    public Task<ResumeState> LoadAsync(string key, CancellationToken cancel)
    {
        return Task.FromResult<ResumeState>(null);
    }

    public Task SaveAsync(string key, ResumeState state, CancellationToken cancel)
    {
        return Task.FromResult(0);
    }

    public Task ClearAsync(string key, CancellationToken cancel)
    {
        return Task.FromResult(0);
    }
}

// GeZi 鸽子下载器 — 多网盘下载器
// Copyright (C) 2026  GeZi contributors
//
// 本程序是自由软件：你可以再分发和/或修改它，依据 GNU Affero 通用公共许可证
// 第 3 版（或更新版本）的条款，由自由软件基金会发布。
// 本程序基于「希望它有用」的目的分发，但不提供任何担保。
// 详见 <https://www.gnu.org/licenses/>。

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GeZi.DownloadEngine.Storage;

/// <summary>
/// 顺序落盘 —— 乱序到达的数据先进内存重排窗口，再严格按偏移递增刷盘。
///
/// 目标文件从偏移 0 起<b>连续增长</b>，因此：
/// <list type="bullet">
/// <item>全程顺序 I/O：机械硬盘不需要寻道（这是它相对共写的决定性优势）</item>
/// <item>任意时刻已落盘部分都是有效前缀 → <b>可以边下边看</b></item>
/// <item>不需要额外的分片文件，磁盘占用 1×（共写也是 1×，但共写有空洞）</item>
/// <item>I/O 量 1×（对比「独立分片 + 合并」是 2×：写分片一次、读回再一次）</item>
/// </list>
///
/// 代价是内存窗口：默认 <see cref="DefaultWindowBytes"/>（32 MB）。窗口满时写入方阻塞（背压），
/// 这会自动把过快的连接压下来，而不是让内存无限增长。
///
/// 实现要点（踩过的坑都写在注释里）：
/// <list type="number">
/// <item>调用方的缓冲会被复用，<b>必须立即复制</b>。</item>
/// <item>只有起点精确等于 <see cref="_flushed"/> 的数据块才能刷盘；前面有空洞就等。
///       这是「连续增长」语义的必然代价，不能为了跑快而跳洞填零（那会产出损坏文件）。</item>
/// <item>重复覆盖同一区域时，已刷过的部分直接丢弃，窗口内的以后到者为准。</item>
/// </list>
/// </summary>
public sealed class SequentialFileStorage : IChunkStorage
{
    /// <summary>默认重排窗口大小：32 MB。</summary>
    public const long DefaultWindowBytes = 32L * 1024 * 1024;

    private readonly object _gate = new object();
    private readonly SortedDictionary<long, byte[]> _pending = new SortedDictionary<long, byte[]>();
    private readonly FileStream _file;
    private readonly string _path;
    private readonly long _maxWindowBytes;

    private long _flushed;
    private long _pendingBytes;
    private bool _disposed;
    private Exception _fault;

    /// <summary>创建顺序落盘。
    /// <paramref name="filename"/> 为空时退化为内存流（仅测试用）。
    /// <paramref name="preserveFromOffset"/> 仅在续传时使用：指定「已可信的连续前缀」长度
    /// （第一个未完成分片的起点）；传 -1 表示退回用文件长度，只适用于「整段重下」的场景。</summary>
    public SequentialFileStorage(string filename, long totalBytes, long maxWindowBytes = 0,
        bool preserveExisting = false, long preserveFromOffset = -1)
    {
        _path = filename;
        _maxWindowBytes = maxWindowBytes > 0 ? maxWindowBytes : DefaultWindowBytes;

        if (string.IsNullOrWhiteSpace(filename))
        {
            _file = null;
            return;
        }

        // 顺序写：大缓冲 + SequentialScan 提示系统做写合并、关掉随机访问假设。
        // 不预分配 SetLength —— 目标是「连续增长」，预分配反而会在文件尾留下零填充的假长度。
        if (preserveExisting)
        {
            // ⚠️ 续传路径专用。用 Create 会清空目标文件，而续传的分片表是接着旧偏移写的，
            // 清空后写出来就是「前半段正确、后半段错位」的坏文件。
            //
            // 但「已连续刷盘的前缀」不能直接取文件长度：续传表可能要求把某些**已完成区域重下**
            // （ApplyResume 会按 64 KB 安全余量回退），文件里那段是旧的、可能残缺。
            // 若把文件长度当成已刷盘，WriteAsync 会因「position + length <= _flushed」把这些重下
            // 的数据整段丢弃，worker 却照样把 Position 推到片尾 —— 结果是「报下载完成、文件没变长」。
            // 所以调用方要用 preserveFromOffset 传入「第一个未完成片的起点」。
            // 🚨 FileShare.Delete 不能省：项目红线「删不掉 = 句柄没带 FileShare.Delete」。
            // 老引擎 8 处句柄都带了，融合引擎这两处原来只给 Read ⇒
            // 下载中途点「取消」时 DeleteFile 必然失败（文件被自己占着），
            // 只能退化成延迟删除，残留到重启。
            _file = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                1024 * 1024, FileOptions.SequentialScan);
            long existing = _file.Length;
            if (totalBytes > 0 && existing > totalBytes)
            {
                // 异常残留（比源文件还长）：截断到源大小，否则尾部脏数据会被当成有效内容。
                _file.SetLength(totalBytes);
                existing = totalBytes;
            }

            long flushed = existing;
            if (preserveFromOffset >= 0 && preserveFromOffset < flushed)
            {
                // 信任续传表：它前面的区域要么已完成，要么会被重下覆盖。
                flushed = preserveFromOffset;
            }

            _flushed = flushed;

            // 🚨🚨 2026-11-06：把文件长度裁到「已可信的连续前缀」。
            //
            // 老文件可能是**共写模式下预分配成满尺寸**的（用户切换过落盘方式、
            // 或续传状态来自旧版本），长度 45MB 但真实有效数据只有 0.6MB。
            // 不裁的话：
            //   ① FileInfo.Length 骗人 —— 播放器/进度判断看到 45MB 以为下完了；
            //   ② 顺序写的 _flushed 从 0.6MB 往后长，写满后文件长度还是 45MB，
            //      完成时的长度硬校验永远「相等」，拦不住假完成。
            // 裁掉的部分正是 ApplyResume 按安全余量要求**重下覆盖**的区域，
            // 与分片表语义完全一致，不会丢有效数据。
            // ⚠️ 仅在顺序写（本类）里做，共写一行不动。
            try
            {
                if (_file.Length != flushed)
                    _file.SetLength(flushed);
            }
            catch (Exception ex)
            {
                // 截断失败不能让整个续传崩掉：退回到「不裁」，靠外层长度校验兜底。
                System.Diagnostics.Debug.WriteLine(
                    "[顺序落盘] 续传截断到可信前缀失败：" + ex.Message);
            }
        }
        else
        {
            _file = new FileStream(filename, FileMode.Create, FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                1024 * 1024, FileOptions.SequentialScan);
        }
    }

    public string Name => "sequential";

    public bool CanWrite => !_disposed && _fault == null;

    /// <summary>已连续刷盘到的偏移 —— 可以直接当续传点。</summary>
    public long Length
    {
        get { lock (_gate) return _flushed; }
    }

    /// <summary>窗口内尚未刷盘的字节数（诊断用）。</summary>
    public long PendingBytes
    {
        get { lock (_gate) return _pendingBytes; }
    }

    public async Task WriteAsync(long position, byte[] bytes, int length, bool fireAndForget = true)
    {
        if (length <= 0) return;

        // 立即复制：调用方（ChunkDownloader）会复用 buffer
        var copy = new byte[length];
        Buffer.BlockCopy(bytes, 0, copy, 0, length);

        long waited = 0;
        while (true)
        {
            bool accepted = false;
            lock (_gate)
            {
                if (_disposed) return;
                if (_fault != null) throw new IOException("顺序落盘已失败：" + _fault.Message, _fault);

                // 已经完全在已刷盘区域内的数据 → 丢弃（重复覆盖）
                if (position + length <= _flushed) return;

                // 部分重叠：裁掉已刷盘的前缀
                if (position < _flushed)
                {
                    int skip = (int)(_flushed - position);
                    position += skip;
                    length -= skip;
                    var trimmed = new byte[length];
                    Buffer.BlockCopy(copy, skip, trimmed, 0, length);
                    copy = trimmed;
                    if (length <= 0) return;
                }

                // 窗口放得下才收（背压）
                if (_pendingBytes + length <= _maxWindowBytes || _pendingBytes == 0)
                {
                    byte[] existing;
                    if (_pending.TryGetValue(position, out existing))
                    {
                        _pendingBytes -= existing.Length;
                    }
                    _pending[position] = copy;
                    _pendingBytes += length;
                    accepted = true;
                }
            }

            if (accepted) break;

            // 窗口满 = 正常背压，不是错误。
            // 🚨 2026-10-24 实测教训：原来这里 60 秒后抛 IOException，
            //    但「窗口满」的真实原因往往是头部某条连接慢（分片还没到），
            //    而 bezzad 的分片超时重试最终会把它补上。抛异常等于把正常等待变成任务失败。
            //    正确做法是一直等（数据留在网络侧，由 TCP 反压），直到有空间或被取消。
            await Task.Delay(10).ConfigureAwait(false);
            waited += 10;
            if (waited > 0 && waited % 10000 == 0)
            {
                // 2026-11-05：把「最早缺口位置」也打出来 —— 这是区分
                // 「头部慢片没到（队头阻塞）」与「真死锁」的关键信息。
                // ⚠️ 只加诊断，绝不在这里抛异常或丢数据：窗口满是正常背压
                //    （见上面 2026-10-24 的教训）。头部优先机制会主动去把缺口补上。
                // 间隔从 30 秒缩到 10 秒：用户感知的「卡住」往往就几秒。
                long earliestGap = -1;
                // ⚠️ SortedDictionary 不是线程安全的，读 Keys 必须与
                //    TryFlushDrain 的增删共用 _gate，否则会抛集合被修改的异常。
                try
                {
                    lock (_gate)
                    {
                        foreach (long pos in _pending.Keys) { earliestGap = pos; break; }
                    }
                }
                catch { }
                System.Diagnostics.Debug.WriteLine(
                    "[SequentialFileStorage] 窗口已满 " + (waited / 1000) + " 秒，已刷盘 "
                    + _flushed + " 字节，待写 " + _pendingBytes + " 字节，最早待写偏移 "
                    + earliestGap + "（若为 " + _flushed + " 之外的值，即头部缺口未到）");
            }
        }

        TryFlushDrain();

        if (!fireAndForget)
            await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 把「起点正好接在已刷盘末尾」的数据块连续刷出去。
    /// 必须持锁调用（保证顺序与 _flushed 一致）。
    /// </summary>
    private void TryFlushDrain()
    {
        lock (_gate)
        {
            if (_file == null) return;

            try
            {
                while (true)
                {
                    byte[] block;
                    if (!_pending.TryGetValue(_flushed, out block)) break;

                    _pending.Remove(_flushed);
                    _pendingBytes -= block.Length;

                    // 🚨 必须显式定位到 _flushed 再写。
                    //    FileStream 的 Position 不保证等于 _flushed：续传路径用
                    //    FileMode.OpenOrCreate 打开已存在的文件，Position 初始是 0，
                    //    而 _flushed 可能是十几 MB。不定位就会把新数据写到文件开头，
                    //    表现是「报下载完成、文件长度一动不动」（旧 bug 的直接成因）。
                    //    WriteBypass 写完也会把位置复位到这里，两者约定一致。
                    _file.Seek(_flushed, SeekOrigin.Begin);
                    _file.Write(block, 0, block.Length);
                    _flushed += block.Length;
                }
            }
            catch (Exception ex)
            {
                _fault = ex;
                throw new IOException("顺序落盘写盘失败：" + ex.Message, ex);
            }
        }
    }

    /// <summary>
    /// 预分配文件长度。用于「moov 优先」：索引表在文件末尾，必须先有长度才能写到那里。
    /// <para></para>
    /// <para>
    /// ⚠️ 它<b>不改变 <see cref="Length"/></b>（那是「已连续刷盘偏移」，仍是 0），
    /// 所以续传点不会被这个假长度带偏。
    /// </para>
    /// </summary>
    public void EnsureLength(long totalBytes)
    {
        if (totalBytes <= 0) return;
        lock (_gate)
        {
            if (_disposed || _file == null) return;
            if (_file.Length >= totalBytes) return;
            try
            {
                _file.SetLength(totalBytes);
                // SetLength 不改 Position（.NET 语义），但保险起见复位到 _flushed
                _file.Seek(_flushed, SeekOrigin.Begin);
            }
            catch (Exception ex)
            {
                _fault = ex;
                throw new IOException("预分配文件长度失败：" + ex.Message, ex);
            }
        }
    }

    /// <summary>
    /// 旁路写：直接定位写入，<b>不参与连续前缀排空</b>。只用于 moov。
    /// <para></para>
    /// <para>
    /// 写入前要求文件已经有足够长度（调用方先调 <see cref="EnsureLength"/>），
    /// 否则会在文件尾留下零填充 —— 那样文件长度会跳成「看起来快下完了」，误导用户。
    /// </para>
    /// <para>
    /// 与已刷盘区域重叠时放弃本次写入：那块数据由正常顺序流程负责，可能已经是对的，
    /// 覆盖反而有风险。
    /// </para>
    /// </summary>
    public void WriteBypass(long position, byte[] bytes, int length)
    {
        if (length <= 0 || bytes == null) return;
        lock (_gate)
        {
            if (_disposed || _file == null) return;
            if (_fault != null) return;
            if (position < 0) return;

            // 已经落在已刷盘区域里 → 正常流程已经写过，别碰
            if (position + length <= _flushed) return;

            long save = _flushed;
            try
            {
                _file.Seek(position, SeekOrigin.Begin);
                _file.Write(bytes, 0, length);
                _file.Flush();
            }
            catch (Exception ex)
            {
                _fault = ex;
                throw new IOException("旁路写盘失败：" + ex.Message, ex);
            }
            finally
            {
                // 复位到连续前缀，保证后续 TryFlushDrain 的顺序追加不错位
                try { _file.Seek(save, SeekOrigin.Begin); } catch { }
            }
        }
    }

    /// <summary>
    /// 刷盘。顺序写**没有独立写线程**（worker 自己同步写），
    /// 所以暂停时 worker 一停就没有新数据进来，不需要闸门概念。
    /// <paramref name="force"/> 参数只为满足接口统一，这里忽略。
    /// </summary>
    public async Task FlushAsync(bool force = false)
    {
        // 反复排空：刷掉一块后可能又有新块接上，直到没有可刷的为止。
        // 下载完成时所有分片都已写入，这里必然能排空。
        long lastPending = -1;
        int idleRounds = 0;

        while (true)
        {
            TryFlushDrain();

            long pending;
            lock (_gate) pending = _pendingBytes;

            if (pending == 0) break;

            // 没有进展说明还有空洞未到（正常：别的分片还在下）。
            // 给几轮机会，之后不再空转 —— 调用方（下载完成路径）会再次调用。
            if (pending == lastPending)
            {
                idleRounds++;
                if (idleRounds >= 20) break;
            }
            else
            {
                idleRounds = 0;
                lastPending = pending;
            }

            await Task.Delay(5).ConfigureAwait(false);
        }

        if (_file != null)
        {
            _file.Flush(true);
        }
    }

    public Stream OpenRead()
    {
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
            return new MemoryStream(Array.Empty<byte>(), false);

        return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.SequentialScan);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try
        {
            // 尽力把能刷的刷完，但不阻塞太久
            TryFlushDrain();
        }
        catch
        {
            // Dispose 不应抛
        }

        if (_file != null)
        {
            try { _file.Flush(true); } catch { }
            try { _file.Dispose(); } catch { }
        }

        lock (_gate)
        {
            _pending.Clear();
            _pendingBytes = 0;
        }
    }
}

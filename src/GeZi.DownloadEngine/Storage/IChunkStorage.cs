// GeZi 鸽子下载器 — 多网盘下载器
// Copyright (C) 2026  GeZi contributors
//
// 本程序是自由软件：你可以再分发和/或修改它，依据 GNU Affero 通用公共许可证
// 第 3 版（或更新版本）的条款，由自由软件基金会发布。
// 本程序基于「希望它有用」的目的分发，但不提供任何担保。
// 详见 <https://www.gnu.org/licenses/>。

using System;
using System.IO;
using System.Threading.Tasks;

namespace GeZi.DownloadEngine.Storage;

/// <summary>
/// 落盘策略接口 —— 决定「下载到的字节如何变成目标文件」。
///
/// 为什么要有这一层（2026-10-24 决策）：
/// 下载器有两套落盘哲学，各有决定性优劣，必须能让用户按介质自己选：
///
/// 1. <b>共写（SharedFile）</b>：所有连接直接 <c>Seek</c> 到目标文件的随机偏移写。
///    优点：不需要合并、内存占用低、写完即完。
///    <para>
///    🚨 2026-11-03 更正：这里原来写「<b>不能边下边看</b>」，<b>那是过时的错误描述</b>。
///    实际 <see cref="SharedFileStorage"/> 已经通过两件事支持边下边看：
///    ① <see cref="EnsureLength"/> 预分配 —— 文件长度从第一秒就是全量，
///       播放器不会因为「看到一截不完整文件」而拒绝打开；
///    ② <see cref="WriteBypass"/> + moov 优先 —— 索引表单独抓下来直接写到文件末尾的正确偏移，
///       所以下到几 MB 就能播。
///    用户实测确认：现在用的就是共写，**确实能边下边看**。
///    </para>
///    <para>
///    仍然存在的缺点：机械硬盘磁头疯狂寻道 → 磁盘 100%、吞吐反向崩塌；
///    预分配 + 随机写有「长度对、内容坏」的风险（gopeed 也踩过，只能整份重下）——
///    本项目用 16 MB 写合并窗口 + 完成时内容校验来压这两个风险。
///    </para>
///
/// 2. <b>顺序（Sequential）</b>：乱序到达的数据先进内存重排窗口，再严格按偏移递增
///    顺序刷盘。目标文件从偏移 0 起<b>连续增长</b>。
///    优点：全程顺序 I/O（机械盘友好）、1× 磁盘占用、1× I/O 量、
///          任意时刻已落盘部分是有效前缀 → <b>可以边下边看</b>、续传点天然明确。
///    缺点：需要内存窗口（默认 32 MB，可配）。
///
/// 实测依据见 <c>Legacy/SegmentedDownloader.Split.cs:230-239</c>（共写被物理删除的原因）
/// 与 <c>refs/gopeed/internal/protocol/http/fetcher.go:770-776</c>（gopeed 校验失败只能 Truncate(0) 重下）。
/// </summary>
public interface IChunkStorage : IDisposable
{
    /// <summary>策略名，用于日志与自检（<c>shared</c> / <c>sequential</c>）。</summary>
    string Name { get; }

    /// <summary>是否还能写入。释放后为 false。</summary>
    bool CanWrite { get; }

    /// <summary>
    /// 当前「有效长度」。
    /// 对顺序策略是<b>已连续刷盘到的偏移</b>（不是文件总长，空洞不计入），
    /// 因此可以直接拿它当续传点用。
    /// </summary>
    long Length { get; }

    /// <summary>
    /// 写入一段数据到指定偏移。
    /// </summary>
    /// <param name="position">文件内绝对偏移（已扣除 RangeLow）。</param>
    /// <param name="bytes">数据缓冲。<b>实现必须立即复制</b>，调用方会复用这块内存。</param>
    /// <param name="length">有效字节数。</param>
    /// <param name="fireAndForget">false 表示写完要确保真正落到磁盘（用于收尾）。</param>
    Task WriteAsync(long position, byte[] bytes, int length, bool fireAndForget = true);

    /// <summary>把窗口内可写的数据全部刷盘，并 fsync。下载完成时调用。</summary>
    /// <param name="force">
    /// true = 收尾/取消场景，必须真正落盘（暂停中也要放行）；
    /// false = 周期刷盘，暂停时直接跳过。
    /// </param>
    Task FlushAsync(bool force = false);

    /// <summary>打开一个只读流用于校验/读取。</summary>
    Stream OpenRead();

    /// <summary>
    /// 把文件长度直接设为 <paramref name="totalBytes"/>（预分配）。
    /// <para></para>
    /// <para>
    /// 只用于「moov 优先」：索引表在文件末尾，必须先让文件有那个长度才能写到那里。
    /// <b>它不改变 <see cref="Length"/> 的语义</b>（顺序策略下 <see cref="Length"/> 仍是
    /// 已连续刷盘偏移，不会因为预分配就跳成总长）。
    /// </para>
    /// </summary>
    void EnsureLength(long totalBytes);

    /// <summary>
    /// 「旁路写」：把数据直接写到指定偏移，<b>不参与连续前缀的排空逻辑</b>。
    /// <para></para>
    /// <para>
    /// 只用于「moov 优先」。顺序策略下普通写入必须严格按偏移递增刷盘，
    /// 而 moov 在文件最末尾 —— 若等它轮到，那要等整段视频都下完，就失去意义了。
    /// 所以给它开一条旁路：直接定位写入，写完把文件位置复位，不影响后续顺序追加。
    /// </para>
    /// <para>
    /// 与已刷盘区域重叠时应当<b>放弃本次写入</b>（那块数据由正常流程负责，可能已经是对的）。
    /// </para>
    /// </summary>
    void WriteBypass(long position, byte[] bytes, int length);
}

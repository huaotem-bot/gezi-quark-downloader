// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Support;

namespace GeZi.Core.Download
{
    /// <summary>
    /// 多连接分片 HTTP 下载器（零外部依赖）。
    /// 每个分片写独立文件，再由专用合并线程按序 append 到目标文件并立即删除分片 ——
    /// 目标文件从偏移 0 起**连续增长**，所以可「边下边播」；磁盘峰值约 1×+单片。
    /// 续传用 sidecar 文本文件记录各分片已写字节。
    /// 另一种落盘方式「共写 SharedFile」已**物理删除**：
    /// 它是多个 worker 各自 seek 写**同一个**目标文件 = 随机 I/O，机械硬盘上磁头疯狂寻道
    /// → 磁盘占用 100%、下载速度也被拖住（用户实测反馈「2.0 后版本都会导致磁盘占用 100%」）。
    /// 而独立分片全程**顺序追加**，是唯一同时满足机械盘与固态盘的方案
    /// （夸克 CDN 上限 ~16MB/s，远低于 HDD 顺序写 ~150MB/s，顺序 I/O 下磁盘带宽从不是瓶颈）。
    /// </summary>
    public class SegmentedDownloader : IDisposable
    {
        private readonly int _threads;
        private readonly string _ua;
        private readonly string _referer;
        private readonly string _cookie;
        private readonly CookieContainer _jar;
        private readonly HttpClient _http;

        /// <summary>
        /// 分片/续传元数据的**根目录**覆盖（null = 与目标文件同级，即默认行为）。
        /// 支持它的理由（对齐 Python Flet 版的 part_dir）：把分片集中放到另一个盘
        /// （例如把小分片放 SSD、大文件放机械盘），或避免分片污染下载目录。
        /// 留空即保持原有"同级 .qparts"行为，零风险。
        /// </summary>
        private readonly string _partsRoot;

        /// <summary>
        /// 新建分片目录时是否设为「隐藏」（对齐 Python Flet 版的 hide_parts）。
        /// 静态属性，由 UI 层在加载设置后设置一次；只影响外观，不影响功能。
        /// 默认 true，与 Python 版默认一致。
        /// </summary>
        public static bool HidePartsDir { get; set; } = true;

        /// <summary>
        /// 诊断日志出口。UI 层可挂上去看"到底切了几片、用了几个连接"，
        /// 否则线程数设置是否生效对用户完全是黑箱。
        /// </summary>
        public Action<string> Log { get; set; }

        /// <summary>
        /// 诊断快照出口（可选）。每 5 秒回调一次，喂给 UI 的**结构化诊断面板**。
        /// <para>
        /// 与 <see cref="Log"/> 的分工：<see cref="Log"/> 是给人读的文本行（也写进日志框），
        /// 这里是给程序读的结构化数据 —— 面板可以直接把数值填进表格/进度条，
        /// 不必去正则解析日志字符串。**两者同源同频**，不会出现"日志说 254、面板说 200"的分歧。
        /// </para>
        /// </summary>
        public Action<DiagnosticsSnapshot> Diagnostics { get; set; }

        /// <summary>
        /// 直链刷新回调。夸克直链带时效签名，长任务下载到一半签名过期是常态；
        /// 没有这个回调时只能整个任务失败，用户得重新解析分享、从头再下。
        /// 挂上之后：遇到 401/403/412 会自动重新取一条直链，
        /// 用新链接着下 —— 已下载的部分（.qmeta 与分片）原样保留，不重下。
        /// 由 UI 层提供，因为它才知道这个文件对应的 fid / token / 分享上下文。
        /// </summary>
        public Func<CancellationToken, Task<string>> LinkRefresher { get; set; }

        /// <summary>
        /// 为 true 时，失败文案**不再承诺"可续传"**。
        /// 免转存的续传取链依赖分享的 <c>stoken</c>，而本程序**续传时不会重新换 stoken**
        /// （全代码只在解析分享那一刻取一次）→ stoken 一过期就**永远取不到新链**。
        /// 此时提示还写着"重试可续传"，用户只会反复重试、反复失败。
        /// 由 UI 层按任务的 <c>Pending.FromShare</c> 设置（FromShare == true ⟺ 免转存）。
        /// </summary>
        public bool ResumeCannotRefresh { get; set; }

        /// <summary>
        /// 单次任务内允许重新取链的次数上限。
        /// 必须有上限：若服务端一直回 403（例如文件被风控、分享已被关闭），
        /// 无上限就会变成"取链→失败→再取链"的死循环。
        /// 定 5 的依据：9GB 文件按实测 ~9MB/s 要跑 17 分钟，
        /// 若直链 TTL 是 5~10 分钟，一次任务可能碰上 2~4 次过期，3 次会偏紧；
        /// 而 5 次即使全部白跑（每次约 0.3 秒接口调用 + 一次冷启动错峰），
        /// 额外开销也在可接受范围内。真到上限了也不会丢进度，用户可手动重试续传。
        /// </summary>
        private const int MaxLinkRefreshes = 5;

        /// <summary>
        /// 重新取链次数的**硬上限**（防死循环）。真正的上限由 <see cref="ComputeMaxLinkRefreshes"/> 按文件大小算。
        /// 🚨 原来写死 5，注释自陈依据是
        /// 「9GB 文件按实测 ~9MB/s 要跑 17 分钟，直链 TTL 5~10 分钟，可能碰上 2~4 次过期」。
        /// **但那个前提是 9MB/s** —— 用户实测速度会掉到 2.5MB/s，同一个 9GB 文件要跑 **60+ 分钟**，
        /// 需要 **6~12 次**刷新 → **5 次根本不够 → 刷新用尽 → 卡在 99%**（用户报的头号问题）。
        /// ⇒ 改成按"文件大小 / 保守速度"估算，速度慢就自动多给几次机会。
        /// </summary>
        private const int MaxLinkRefreshesCap = 60;

        /// <summary>直链 TTL 的保守估计（秒）。宁可估小 —— 估小了只是多留几次机会，估大了会提前放弃。</summary>
        private const int LinkTtlSeconds = 300;

        /// <summary>
        /// 动态窃取的最小粒度（字节）。
        /// 移植自 **Gopeed 的 `stealMinChunkSize = 512 * 1024`**（其注释：`Min steal size: 512KB
        /// (avoid tiny chunks)`）与 **axel 的 `MIN_CHUNK_WORTH = 100 * 1024`**。
        /// 为什么要有下限：把片切得比这还小，新连接光是握手+慢启动就不划算 ——
        /// 窃取是为了"让空闲连接有活干"，不是把片切碎。
        /// </summary>
        private const long MinStealBytes = 512 * 1024;

        /// <summary>
        /// 按文件大小推算"这次任务最多允许换几次链"。
        /// 算法：`需要次数 = ceil(预计时长 / 直链TTL) + 1`，再夹到 `[5, 60]`。
        /// 预计时长按**最差速度 2MB/s** 估（用户实测最低 ~2.5MB/s，再留点余量）——
        /// **刻意悲观**：宁可多给几次机会（每次刷新只花 ~0.3 秒接口调用），也不要因为省事而提前放弃。
        /// </summary>
        private static int ComputeMaxLinkRefreshes(long totalBytes)
        {
            if (totalBytes <= 0) return MaxLinkRefreshes;
            const double worstSpeedBps = 2.0 * 1024 * 1024;      // 2 MB/s
            double estSeconds = totalBytes / worstSpeedBps;
            int need = (int)Math.Ceiling(estSeconds / LinkTtlSeconds) + 1;
            if (need < MaxLinkRefreshes) need = MaxLinkRefreshes;
            if (need > MaxLinkRefreshesCap) need = MaxLinkRefreshesCap;
            return need;
        }

        /// <summary>
        /// 最近一次 <see cref="ProbeAsync"/> 探到的文件总大小（0 = 未知）。
        /// 用于 <see cref="ComputeMaxLinkRefreshes"/> —— 刷新上限必须在主循环里算，
        /// 而 total 是 AttemptAsync 内部探测出来的，所以在这里带出来。
        /// </summary>
        private long _lastProbedTotal;

        /// <summary>
        /// 合并线程收到收尾信号后，还要连续多少轮读不到新数据才真的退出。
        /// 每轮约 100ms，10 轮 ≈ 1 秒宽限 —— 用来覆盖"worker 最后一段还在它自己的
        /// 文件缓冲里、尚未落到盘上"这个窗口。详见合并循环里收尾分支的注释。
        /// </summary>
        private const int MergeStallRounds = 10;

        /// <summary>单个分片的最大重试次数（首次尝试之外）。</summary>
        private const int MaxSegmentRetries = 4;

        /// <summary>
        /// 单个分片最多被"整轮重试"几次。
        /// 背景：一个分片把 <see cref="MaxSegmentRetries"/> 次尝试用完（网络抖动、
        /// 服务端掐连接、节点坏掉）之后，原来的处理是**抛弃这一片 + 干掉这个 worker**。
        /// fork 用 TCP 级故障注入实测（resetmid 场景）：5 个分片被连续 RST →
        /// 8 个 worker 全部阵亡 → 64 片里有 56 片**一次都没被尝试过** →
        /// 整包下载直接失败，报错只有一句没有上下文的 HttpRequestException。
        /// 现在改成把这片**放回队列**（换条连接、多半换到别的 CDN 节点），
        /// worker 留在池子里继续干活（并发不再递减）。
        /// 3 轮 ≈ 最多 15 次请求/片，足够跨过"某个节点持续坏一段时间"，
        /// 又不至于在一颗永久坏掉的树上吊死。
        /// </summary>
        private const int MaxPartRounds = 3;

        /// <summary>
        /// 全局"整轮重试"总预算（跨所有分片累计）。
        /// 第二道闸：几百个分片都在反复失败，说明问题不在个别节点
        /// （很可能是整条链路/账号层面出事），继续重试只是空转，
        /// 应当尽快收场并把真实原因告诉用户。
        /// </summary>
        private const int MaxTotalRequeues = 500;

        /// <summary>
        /// 「连接假死」判定：一条连接连续这么多秒**一个字节都没收到**，
        /// 就认定这条连接坏了，立即断开重连。
        /// 与"慢"（PreemptMinAgeMs / PreemptMinBps 那一套）有本质区别：
        /// 慢是服务端限速，重连也没用（所以有"整体限速"判定来避免无谓重连）；
        /// 假死是连接本身坏了（服务端接了请求却再也不发数据），重连几乎立刻就好。
        /// 两者必须分开判 —— 假死若按"慢"走，会被抢占配额、整体限速判定、
        /// 剩余量门槛层层拦截，最后被留在那里永远挂着。
        /// 取 30 秒的依据：正常分片请求首字节是秒级，30 秒没动静基本可断定对端不再发；
        /// 同时明显小于 45 秒的全局停滞看门狗 —— 能在整个任务被判停滞之前先自救。
        /// 🚨
        /// ⚠️⚠️
        ///    真实判据**不是**"这么多秒内连一个 256KB 分片块都没写出来"，而是
        ///    「这么多秒内 part.W **一个字节都没涨**」—— 因为 worker 里是
        ///    `written += n; part.W = written;`，每读到一次就累加，**不攒满一块**。
        ///    所以真实门槛 ≈ **33 B/s**（30 秒一个字节），而不是 13KB/s。
        ///    这个更正很重要：30 秒这个值**非常宽松**，只有彻底挂死的连接才会被判假死；
        ///    健康但极慢的连接（哪怕只有 1 KB/s）每个采样窗口都在涨字节，永远不会被误杀。
        ///    ⇒ 以后**不要**再以"13KB/s 太危险"为理由去调大这个值 —— 那是错误前提。
        ///      30 秒的依据只有一条：明显小于 45 秒全局停滞看门狗，能先自救。
        /// </summary>
        private const int HangSeconds = 30;

        /// <summary>
        /// 同一分片最多被判"假死"抢救多少次。
        /// 必须有上限：若对端对该范围**恒定**假死（不是偶发），无限重连就是空转。
        /// 12 次 × 30 秒 ≈ 6 分钟，够覆盖"换三四个节点还是不给数据"的最坏情况；
        /// 用完之后的兜底是全局停滞看门狗（它会 Dispose 在飞响应，保证任务一定收场）。
        /// </summary>
        private const int MaxHangRescues = 12;

        /// <summary>
        /// 每轮（5 秒）最多抢救**几条**假死连接。
        /// 🚨 这个上限是必需的，不是保险丝：第零遍原本遍历**所有** worker，
        ///    凡满足条件的一律 Dispose —— 没有任何数量约束。
        ///    一旦判据在"整体限速"等场景被误触发，就是「一轮之内把几十上百条
        ///    健康连接同时掐掉」→ 重连风暴 → 速度雪崩
        ///    （用户实测「20+ MB/s 掉到 10 MB/s、还看到反复假死」，此为候选成因）。
        /// 限 4 条/轮 = 48 条/分钟：真假死是**个别坏节点**（少数），这个速率足够在
        /// 45 秒看门狗收场之前把坏连接换干净；而即使判据被误触发，5 秒内也只误伤 4 条
        /// （相对 256 并发仅 1.6%），总速不会塌。
        /// ⚠️ 别调回 2：实测 VHANG_PCT=25 极端注入下同时有 17 条假死，2 条/轮要 45 秒才换完，
        ///    正好撞上 45 秒停滞看门狗 → 任务被中止（实测踩到）。4 条/轮能赶在看门狗收场前换完。
        /// </summary>
        private const int HangRescuePerTick = 4;

        /// <summary>
        /// 真实并发天花板。用户选的档位可以很高，但物理上"同时开着的连接数"
        /// 必须有个上限，否则会把本机网络栈和家用路由器的 NAT 表一起打爆。
        /// 为什么定在 512：
        /// - 512 路需要路由器 NAT 会话表有足够余量（企业级设备轻松，
        ///   中高端家用路由也通常能扛，但老旧的百元路由可能吃力）。
        /// - 512 路 × 256KB 读缓冲 ≈ 128MB，加上文件缓冲仍在可控范围。
        /// - 这是"还能正常工作"的经验上限；再往上（1024+）收益趋近于零，
        ///   而风险（连接超时风暴、整网卡死）急剧上升，故不再放宽。
        /// 注意：这个值不是"越大越快"。多数家宽在 64~128 路就已跑满，
        /// 继续加只是让首字节更慢、更容易触发服务端限流。
        /// 超过此值时不再真的开那么多连接，而是靠"分片多于连接数"让快的先领活
        /// （工作窃取）——并发度不降，但资源占用被钉住。
        /// UI 档位、设置夹取、线程池预热都引用这一个值，
        /// 不要再在别处写死 512，否则改一处漏一处就会出不一致的 bug。
        /// </summary>
        // 实测（2026-10-02，真实直链，4 轮）：1024 档仅比 512 快约 5%（23.20 vs 22.33 MB/s），
        // 而单连接速**减半**（44.7 → 23.2 KB/s，配额被摊薄，已近天花板），
        // 且 1024 档出现 `AggregateException` / 任务未结束的异常率更高。
        // 收益不显著、连接数翻倍（路由器 NAT 压力 + 风控风险），故**维持 512**。
        /// <summary>
        /// 是否输出**逐任务**的 5 秒诊断日志（默认 **false**）。
        /// 多任务并发时它是日志刷屏的元凶：11 个任务 × 每 5 秒一行
        /// = 每分钟 130+ 行「运行中: …/s，活跃连接 …，命中 IP …」，把"开始下载 /
        /// 下载完成 / 失败重试"这些用户真正关心的行全冲掉了。
        /// 要排查"速度上不去卡在哪"时把它设成 true 即可；
        /// 诊断面板（结构化快照）**不受这个开关影响**。
        /// </summary>
        public static bool PerTaskDiagLog = false;

        public const int MaxRealConcurrency = 512;

        // ==================== 慢连接抢占（preempt）====================
        // 移植自云析 YunX 的 Agent.md §5.3.1，详见
        // docs/并发调度-云析与N_m3u8DL对标.md。
        // 治的是什么病：网盘 CDN 是**按连接**限速的，个别连接会落在慢节点上。
        // 云析的真机日志（70.6MB 文件）：
        //   在飞=1 used=1/64 剩主池片=0 总速=3.2 KB/s 剩余=25.0KB
        //     └ m169 块大小=256.3KB 已收=231.3KB 瞬时=3.2 KB/s 已跑=42s
        // 即**最后 500KB 拖了 37 秒**，其余 61 路早已空转；多数连接 40~80KB/s，
        // 慢的只有 3~7KB/s —— 不是整站变慢，是那几条连接坏了。
        // 这与「片数 ≫ 并发」是**互补**的两种手段，别互相替代：
        //   片数 ≫ 并发 → 让快连接一直有活干（缓解尾部塌缩）
        //   慢连接抢占 → 让慢连接不把任务拖到死（根治收尾长尾）
        // 核心保证（照搬云析，改动时必须守住）：
        //   抢占**只掐当前这一片的连接**，已写字节全部保留在 part.W，
        //   分片重新入队，下一轮从断点续传 —— **不退避、不计失败**。
        //   因此抢占永不丢数据、不产生空洞。

        /// <summary>绝对下限：瞬时速度低于此值才算"慢"（正常连接 40~80 KB/s）。</summary>
        private const long PreemptMinBps = 12 * 1024;

        /// <summary>看门狗采样周期（秒）。</summary>
        private const double PreemptTickSec = 5.0;

        /// <summary>至少跑这么久才判定，避开建连与 TCP 慢启动阶段。</summary>
        private const long PreemptMinAgeMs = 15_000;

        /// <summary>同一分片两次抢占之间的冷却，防重连风暴。</summary>
        private const long PreemptCooldownMs = 10_000;

        /// <summary>剩余太少就不折腾了。</summary>
        private const long PreemptMinRemain = 128 * 1024;

        /// <summary>单个分片最多被抢 3 次（全站都慢时防止无限重连）。</summary>
        private const int PreemptMax = 3;
        /// <summary>
        /// 收尾阶段（在飞 ≤ <see cref="PreemptEndgameInflight"/>）的单分片抢占上限。
        /// 用户实测日志 ——
        ///   「慢连接抢占: 本轮掐掉 1 路（阈值 12.0 KB/s，在飞 2 路，收尾阶段）」
        ///   隔 10 秒又一条，然后**再也没有了** —— 每片 3 次机会在 30 秒内就烧完，
        ///   剩下的两片只能干等 → 速度掉到 0 → 45 秒后被看门狗判"网络阻断"中止。
        /// 🚨 原来的 30 次 / 3 秒太激进：
        ///   30 次 × 3 秒 = **90 秒内可以掐同一片 30 次** —— 那就是**重连风暴**，
        ///   每个连接刚握手就被掐，服务端看到的全是异常连接，反而更容易触发反制。
        ///   现在 10 次 / 8 秒 ≈ **80 秒窗口**，配合上面新增的"有进展就重置计数"，
        ///   既能救活落在坏节点上的片，又不会打成风暴。
        /// </summary>
        private const int PreemptMaxEndgame = 10;
        /// <summary>
        /// 收尾阶段的抢占冷却（比常规的 10 秒短一点，但不再激进到 3 秒）。
        /// 只剩几路时干等的代价大于重连，所以适度缩短；但重连本身要付 TLS 握手的代价，
        /// 3 秒连握手都不够 —— 8 秒是"给足重连时间"和"别干等太久"之间的折中。
        /// </summary>
        private const long PreemptEndgameCooldownMs = 8_000;

        /// <summary>每轮最多抢 2 路，避免同时掐掉一大片连接。</summary>
        private const int PreemptPerTick = 2;

        /// <summary>
        /// 在飞路数 ≤ 此值视为"收尾阶段"，放宽年龄与剩余门槛。
        /// 这是云析设计的精髓：只剩几路在磨时，那几路的速度就是用户看到的
        /// 总速度，此时重连握手（~0.5s）远比继续等便宜。
        /// </summary>
        private const int PreemptEndgameInflight = 3;

        /// <summary>收尾阶段的最低年龄门槛。</summary>
        private const long PreemptEndgameMinAgeMs = 3_000;

        /// <summary>
        /// 判定"整体限速"所需的最少有效样本数。
        /// 启动初期只有零星几个连接跑够了年龄（其余还在建连/错峰延迟中），
        /// 此时 1/1 路慢就会被判成"整体慢"——样本太少，结论不可靠。
        /// 少于这个数就只做抢占、不做整体限速判定。
        /// </summary>
        private const int PreemptOverallSlowMinSamples = 4;

        /// <summary>
        /// 判定"整体限速"后，暂停"慢连接抢占"的**粘滞时长**（毫秒）。
        /// 原来每轮（5 秒）独立判定：这一轮超半数慢就"暂停抢占"、
        /// 下一轮只要慢的连接掉到半数以下就立刻恢复抢占。而 CDN 在重压下的表现恰恰是
        /// **抖动**的 —— 用户实测日志里同一分钟内「整体限速中」与「慢连接抢占: 本轮掐掉
        /// 2 路」交替刷屏，等于一边判"掐了没用"、一边照掐不误，把连接当消耗品换，
        /// 速度曲线被锯成 5.7 ↔ 23.7 MB/s 的锯齿。
        /// 一旦判定为系统性限速，就按住这么久不再抢占（期间每轮照常复核，仍然系统性
        /// 就顺延窗口）。取 30 秒 = 6 个采样轮，与"限流平息回升"的静默期一致。
        /// ⚠️ 收尾阶段（在飞 ≤ 3）**不受此限** —— 那几路的速度就是总速度，该抢还得抢。
        /// </summary>
        private const long OverallSlowHoldMs = 30_000;

        /// <summary>
        /// 单调毫秒时钟。
        /// 不用 Environment.TickCount：它是 int，49.7 天回绕，
        /// 本项目是长时间运行的下载器，不做无谓的回绕处理。
        /// </summary>
        private static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        // ==================== 对端 IP 诊断 ====================
        // 移植自 Python Flet 版 FastDownloader 的 _peer_ip()：它把每条连接的对端 IP
        // 记下来，汇总成「命中 IP N 个: 1.2.3.4 x40, 5.6.7.8 x20」。
        // 为什么这个指标重要：CDN 靠 DNS 把同一域名解析到**多个边缘节点 IP**，
        // 而限速策略很可能绑定在节点上。
        //   - 64 条连接全撞同一个 IP → 被同一节点统一压制
        //   - 分散到多个 IP → 才有机会各吃各的配额
        // ⚠️ 这也解释了「慢连接抢占」为什么有时无效：若所有连接都撞同一 IP，
        //    抢占后重连还是同一个节点。两者是**协同**关系 ——
        //    `DnsRefreshTimeout = 0` 让重连有机会换 IP，抢占才有意义。
        // net48 的 HttpClient 不暴露 socket，但 ServicePoint.BindIPEndPointDelegate
        // 的 remoteEndPoint 参数就是「本次连接的目标地址」。**已实测**（tmp-ip/）：
        //   ① 回调确实被调用（并发 16 个请求 → 32 次回调）
        //   ② 返回 null **不影响**连接建立（A/B 对照：设与不设，成功率完全一致）
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>>
            _peerIpByHost = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>(
                StringComparer.OrdinalIgnoreCase);

        /// <summary>清掉该 host 的历史统计，避免跨任务累积。</summary>
        private static void ResetPeerIpTracking(string url)
        {
            try
            {
                ConcurrentDictionary<string, int> _;
                _peerIpByHost.TryRemove(new Uri(url).Host, out _);
            }
            catch { }
        }

        /// <summary>为指定 URL 的 ServicePoint 挂上对端 IP 统计（幂等）。</summary>
        private static void EnsurePeerIpTracking(string url)
        {
            try
            {
                var uri = new Uri(url);
                var sp = ServicePointManager.FindServicePoint(uri);
                if (sp.BindIPEndPointDelegate != null)
                    return;   // 该 ServicePoint 已经挂过了

                string host = uri.Host;
                sp.BindIPEndPointDelegate = (s, remote, retry) =>
                {
                    try
                    {
                        if (remote != null)
                        {
                            var bag = _peerIpByHost.GetOrAdd(host,
                                _ => new ConcurrentDictionary<string, int>());
                            bag.AddOrUpdate(remote.Address.ToString(), 1, (k, v) => v + 1);
                        }
                    }
                    catch { }
                    return null;   // 用系统默认绑定（返回 null 安全，已实测）
                };
            }
            catch { }
        }

        /// <summary>
        /// 对端 IP 命中分布（按连接数降序，最多列 4 个）。返回空串表示还没有样本。
        /// </summary>
        private static string DescribePeerIps(string url)
        {
            try
            {
                ConcurrentDictionary<string, int> bag;
                if (!_peerIpByHost.TryGetValue(new Uri(url).Host, out bag) || bag.Count == 0)
                    return "";

                var top = bag.OrderByDescending(kv => kv.Value).Take(4)
                             .Select(kv => kv.Key + " x" + kv.Value);
                return string.Format("命中 IP {0} 个: {1}", bag.Count, string.Join(", ", top));
            }
            catch { return ""; }
        }

        /// <summary>
        /// 重试抖动用的随机源。
        /// 原注释写"每个下载器实例独占一份即可"，
        /// 这是**错的**：一个实例里会同时跑最多 512 个 worker，而 System.Random
        /// 不是线程安全的。并发调用 NextDouble() 会互相踩内部状态，
        /// 导致"抖动"退化成同步退避（多个 worker 算出相近的延迟），
        /// 在限流场景下恰好会促成重试风暴 —— 正是抖动要避免的事。
        /// net48 没有 Random.Shared，所以用 ThreadStatic：每个线程一份，
        /// 无锁、无竞争。种子用线程 id + 时间混合，保证各线程不同。
        /// </summary>
        [ThreadStatic]
        private static Random _tlsRng;

        /// <summary>取当前线程的随机源（首次访问时惰性创建）。</summary>
        private static Random Rng
        {
            get
            {
                var r = _tlsRng;
                if (r == null)
                {
                    unchecked
                    {
                        int seed = Environment.TickCount * 31
                                 + Thread.CurrentThread.ManagedThreadId * 17
                                 + Guid.NewGuid().GetHashCode();
                        r = new Random(seed);
                    }
                    _tlsRng = r;
                }
                return r;
            }
        }

        /// <summary>是否属于"重试还有意义"的瞬时故障状态码。</summary>
        private static bool IsTransientStatus(int code)
        {
            // 408 超时、429 限流、5xx 服务端故障 —— 都值得退避后重试。
            return code == 408 || code == 429 || (code >= 500 && code <= 599);
        }

        /// <summary>
        /// 人类可读的字节数（仅用于诊断日志）。
        /// 否则在土耳其/德语等区域设置下，
        /// 小数点会被渲染成逗号（"1,50 MB"），日志解析脚本按 '.' 切分会直接挂掉。
        /// 日志格式属于对外契约的一部分，不能随用户区域设置漂移。
        /// </summary>
        private static string Fmt(long bytes)
        {
            var ci = CultureInfo.InvariantCulture;
            // 🚨 原来除以 1024³ 却标成 "GB" —— 那是 **GiB**，
            // 于是同一个文件「夸克分享页写 9.54 GB、本程序写 8.89 GB」，
            // 用户以为少下了（实测反馈：「明明9gb只下了8.99gb」）。
            // 改成 **1000 进制**：单位名副其实，且与夸克分享页/资源管理器之外的多数场景一致。
            // ⚠️ Windows 资源管理器用的是 1024 进制却同样标 GB，所以两者仍会有差异 ——
            //    但那属于 Windows 自己的历史包袱，我们不做错误标注。
            if (bytes < 1000) return bytes.ToString(ci) + " B";
            if (bytes < 1000L * 1000) return (bytes / 1000.0).ToString("F1", ci) + " KB";
            if (bytes < 1000L * 1000 * 1000) return (bytes / 1000.0 / 1000.0).ToString("F2", ci) + " MB";
            return (bytes / 1000.0 / 1000.0 / 1000.0).ToString("F2", ci) + " GB";
        }

        /// <summary>
        /// 速度平滑的时间常数（秒）。**想调速度显示的"稳/灵"就改这一个值。**
        /// 调大 → 读数更稳、但更迟钝；调小 → 更灵敏、但更抖。
        /// </summary>
        private const double SpeedTauSec = 1.5;

        /// <summary>
        /// 进度上报给 UI 的最小间隔（毫秒）。
        /// 采样循环本身仍是 200ms —— 停滞看门狗需要这个精度来及时发现"完全没数据"。
        /// 但**上报**没必要那么密：每次 Report 都会往 UI 线程投递一次回调，
        /// 而 WPF 的 UI 是单线程，512 并发下这些投递会持续挤占它。
        /// 参考 Python Flet 版：它是 **1 秒**上报一次。取 500ms 是折中 ——
        /// 进度条仍足够跟手，投递量降到 1/2.5。
        /// </summary>
        private const int ProgressReportMs = 500;

        /// <summary>
        /// 下载速度估计器：指数加权移动平均（EWMA）。
        /// 为什么换掉原来「最近 2 秒共传了多少字节」的滑动窗口：
        ///  - 窗口法对窗内样本等权，而窗外的样本会**整块**掉出去 ——
        ///    一个慢片段滑出窗口的瞬间读数会突然跳一下，看着像网络在抖，其实是算法在抖。
        ///  - 窗口长度还和采样周期硬绑定，改采样频率就等于改了平滑程度。
        ///  - EWMA 只要 O(1) 内存、不需要队列；而且"越近的样本权重越大"是连续表达的，
        ///    没有窗口边缘那种台阶。
        /// 平滑强度用**时间常数** τ 表达，而不是直接写死 α：
        ///     α = 1 - exp(-Δt / τ)
        /// 这样采样周期从 200ms 改成 100ms 时平滑程度不变。
        /// （若直接写死 α，采样一快就等于把平滑削弱了一半，属于隐蔽的行为漂移。）
        /// τ 的物理含义：速度发生阶跃后约 τ 秒追上 63%，约 3τ 秒追上 95%。
        /// </summary>
        /// <summary>
        /// 分批开连接（移植 **Gopeed 的 `slowStartController`** 思路）。
        /// 时间错峰只保证各 worker 的**发起**时间不同，
        /// 不保证前一批**已经建好连接**。若前一批握手慢（TLS 实测可达 1~4 秒），
        /// 后一批会在它们还没就绪时就涌上来 —— 结果仍是"一大群连接同时处于慢启动、
        /// 互相争抢带宽"，每个的拥塞窗口都涨不起来（实测「512 比 256 慢」就是这个）。
        /// 把 worker 分成若干批；第 k 批在开跑前先等**前一批有一半连接就绪**
        /// （或等满超时 —— 避免前一批全军覆没时后面永远不动）。
        /// "就绪"的定义是**首次成功拿到 HTTP 响应**，那意味着 TCP+TLS 握手已完成。
        /// </summary>
        private sealed class SlowStartGate
        {
            private readonly int _batchSize;
            private int _ready;                    // 已就绪连接数（首次响应成功）

            public SlowStartGate(int total)
            {
                // 批大小 = 总数的 1/8，夹在 [4, 64]：太小放行太慢，太大等于没分批。
                int b = total / 8;
                if (b < 4) b = 4;
                if (b > 64) b = 64;
                _batchSize = b;
            }

            /// <summary>第 index 个 worker 在开跑前等自己的批次放行。</summary>
            public async Task WaitTurnAsync(int index, CancellationToken ct)
            {
                int batch = index / _batchSize;
                if (batch == 0) return;            // 第一批直接放行

                // 等前一批的一半就绪；最多 3 秒（前一批全挂时也要放行，否则任务卡死）
                int need = Math.Max(1, batch * _batchSize / 2);
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (Volatile.Read(ref _ready) < need && DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }

            /// <summary>某个连接首次拿到响应时调用（每个 worker 只调一次）。</summary>
            public void MarkReady() { Interlocked.Increment(ref _ready); }
        }

        private sealed class SpeedMeter
        {
            private readonly double _tau;
            private double _ewma;
            private long _lastBytes;
            private DateTime _lastT;

            /// <summary>
            /// 建基线：速度是"增量 / 时间"，没有起点就算不出来，
            /// 所以必须在开始下载的那一刻把起始字节数与时间点记下来。
            /// </summary>
            public SpeedMeter(double tauSec, long startBytes, DateTime startTime)
            {
                _tau = tauSec > 0 ? tauSec : 1.0;
                _lastBytes = startBytes;
                _lastT = startTime;
            }

            /// <summary>喂一个「累计已下载字节数」采样点，返回平滑后的速度（字节/秒）。</summary>
            public double Sample(long totalBytes, DateTime now)
            {
                double dt = (now - _lastT).TotalSeconds;
                if (dt <= 0)
                    return _ewma;

                long delta = totalBytes - _lastBytes;
                if (delta < 0) delta = 0;      // 计数器只增不减，这里纯属防御
                double instant = delta / dt;

                double alpha = 1.0 - Math.Exp(-dt / _tau);
                if (alpha > 1.0) alpha = 1.0;

                // 还没有任何有效读数时直接落位，而不是从 0 慢慢爬 ——
                // 否则起播头几秒读数一直是零，看着像卡住了。
                // 只在"当前读数为 0"时走这条，所以下载中途的短暂停不会触发突跳。
                if (_ewma <= 0)
                    _ewma = instant;
                else
                    _ewma = alpha * instant + (1.0 - alpha) * _ewma;

                _lastBytes = totalBytes;
                _lastT = now;
                return _ewma;
            }
        }

        public SegmentedDownloader(int threads = 0, string ua = null, string referer = null, string cookie = "",
            string partsRoot = null)
        {
            _threads = Math.Max(1, threads > 0 ? threads : QuarkConstants.DownloadThreads);
            _ua = string.IsNullOrEmpty(ua) ? QuarkConstants.DlUa : ua;
            _referer = string.IsNullOrEmpty(referer) ? QuarkConstants.Referer : referer;
            _cookie = cookie ?? "";
            _partsRoot = string.IsNullOrWhiteSpace(partsRoot) ? null : partsRoot.Trim();
            _jar = new CookieContainer { Capacity = 500, PerDomainCapacity = 200 };
            SeedCookies(_jar, _cookie);
            var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = _jar,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.None,
                // 关键：.NET Framework 下这里不显式设置，会退回 ServicePoint 的
                // DefaultConnectionLimit（受"全局连接预算"约束）。线程数调高后
                // 连接池必须同步放开，否则线程再高也只能干等空位。
                MaxConnectionsPerServer = Math.Max(1024, _threads * 2),
            };
            // 代理策略统一交给 NetworkOptions（默认**直连**）。默认 HttpClientHandler 会读取
            // Windows 系统代理(WinINET)，而直链下载是到夸克 CDN 的大流量长连接：一旦被本机
            // 代理(如某些 VPN/抓包/加速器)接管，轻则限速重则连接被劫持到 127.0.0.1 失败。
            // Python Flet 版用 urllib 不读系统代理，这里保持一致行为。
            // ⚠️ 与 API 层（QuarkSession）用同一个开关：公司网络下要能整体走代理，
            //    否则会出现"能解析分享、但直链一条都下不动"的半通状态。
            NetworkOptions.ApplyTo(handler);
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        /// <summary>
        /// 下载到 dest。返回 (成功?, 说明)。progress 由 IProgress 自动切回 UI 线程；
        /// cancel 取消、pause 暂停。
        /// 直链签名过期（401/403/412）会走一条自愈路径：调用 <see cref="LinkRefresher"/>
        /// 重新取一条直链，用新链接着下。**已下载的部分原样保留**，不重下。
        /// </summary>
        public async Task<DownloadResult> DownloadAsync(string url, string dest,
            IProgress<DownloadProgress> progress = null, CancellationToken cancel = default, PauseToken pause = null)
        {
            // 兜底：确保目标目录存在（调用方创建失败时这里再建一次）
            try
            {
                string dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }
            catch { }

            var headers = BuildHeaders();

            // 对端 IP 诊断：每次任务开始时重置该 host 的统计，并确保采样已挂上。
            // 必须在**第一次请求之前**挂，否则首个连接不会被计入。
            ResetPeerIpTracking(url);
            EnsurePeerIpTracking(url);

            string msg;
            int refreshed = 0;
            var taskStart = DateTime.UtcNow;

            // ---- 主循环：失败后若是"签名过期"就重新取链，再用新链续传 ----
            // 为什么放在这一层而不是 worker 里：一次失效通常是全局的（整条直链过期），
            // 让所有 worker 各自去刷新只会打出一堆重复的取链接口调用；
            // 在这一层做，一次失败只换一次链，然后整个任务用新链重跑，
            // 分片规划与已写字节都从 .qmeta 里读回来，等于无损续传。
            while (true)
            {
                var (ok, m) = await AttemptAsync(url, dest, headers, progress, cancel, pause, taskStart).ConfigureAwait(false);
                if (ok)
                    return new DownloadResult { Ok = true };
                msg = m;

                if (LinkRefresher == null || !IsSignatureExpired(msg))
                    break;

                // 刷新上限**按文件大小动态算**，不再写死 5。
                // 原来写死 5 的依据是"9MB/s 跑 17 分钟"；但速度掉到 2.5MB/s 时同一文件要 60+ 分钟，
                // 需要 6~12 次刷新 → 5 次不够 → 刷新用尽 → **卡在 99%**。
                // ⚠️ 必须在第一次 AttemptAsync **之后**算 —— 那时 _lastProbedTotal 才探到值。
                int maxRefreshes = ComputeMaxLinkRefreshes(_lastProbedTotal);
                if (refreshed >= maxRefreshes)
                {
                    Log?.Invoke(string.Format(
                        "直链已连续失效 {0} 次（本文件按大小允许 {1} 次），本次任务中止，断点与已下载部分已保留。",
                        refreshed, maxRefreshes));
                    break;
                }

                refreshed++;
                cancel.ThrowIfCancellationRequested();
                Log?.Invoke(string.Format(
                    "直链已失效（{0}），正在重新获取第 {1}/{2} 次…已下载的部分会保留，换新链后自动续传。",
                    Trim(msg, 60), refreshed, maxRefreshes));

                string fresh = null;
                try
                {
                    fresh = await LinkRefresher(cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log?.Invoke("重新获取直链失败: " + ex.Message);
                }

                if (string.IsNullOrEmpty(fresh) || string.Equals(fresh, url, StringComparison.Ordinal))
                {
                    Log?.Invoke("未能取到新的直链（接口未返回或与旧链相同），本次任务中止，断点已保留。");
                    break;
                }

                url = fresh;
                Log?.Invoke("已换用新直链，继续下载。");
            }

            // 4xx（尤其 412）通常是防盗链/签名校验失败：换更干净的请求头，单线程全量 GET 兜底重试。
            // ⚠️ 但**只有磁盘上确实一点进度都没有**时才能这么做：
            // ClearPartial 会把目标文件、.qmeta 和分片目录一并删掉。
            // 若已经下了几个小时再走这条路，用户就从零重来 —— 宁可失败并保留断点，
            // 也不要静默毁掉进度。这条以前是无条件执行的，属于真实缺陷。
            if (!IsRetryable4xx(msg))
                return new DownloadResult { Ok = false, Message = msg };

            if (HasProgress(dest))
            {
                return new DownloadResult
                {
                    Ok = false,
                    // 免转存任务依赖分享 stoken；v2.3.1 起引擎在 stoken 过期时会
                    // 自动重新解析分享（commit 0aa2f53）并续传，所以"看似失败"
                    // 也能续上。仍保留 ResumeCannotRefresh 用于内部决策，但向用户
                    // 说明"会自动重新解析并续传"，比让用户不知所措强。
                    Message = msg + (ResumeCannotRefresh
                        ? "（已保留断点与已下载部分。注意：这是「免转存下载」，"
                          + "分享凭证过期时会自动重新解析分享并续传。）"
                        : "（已保留断点与已下载部分，重新获取直链后可续传）"),
                };
            }

            int i = 0;
            foreach (var fb in FallbackHeaders())
            {
                i++;
                cancel.ThrowIfCancellationRequested();
                ClearPartial(dest);
                var (ok2, msg2) = await DownloadSingleAsync(url, dest, fb, null, progress, cancel, pause).ConfigureAwait(false);
                if (ok2)
                    return new DownloadResult { Ok = true };
                msg = msg + "; 兜底" + i + "(" + msg2 + ")";
            }
            return new DownloadResult { Ok = false, Message = msg };
        }

        /// <summary>是否是"直链签名/授权已过期"这类错误（重新取链有意义）。</summary>
        private static bool IsSignatureExpired(string err)
        {
            var m = Regex.Match(err ?? "", @"HTTP (\d+)");
            if (!m.Success)
                return false;
            int code;
            if (!int.TryParse(m.Groups[1].Value, out code))
                return false;
            // 401 未授权、403 签名过期/防盗链、412 前置条件失败（夸克常用它表示签名校验不过）
            return code == 401 || code == 403 || code == 412;
        }

        /// <summary>
        /// 磁盘上是否已有"实际下载进度"。判据是 .qmeta 里有没有分片记过已写字节，
        /// **不能看目标文件大小** —— 共写模式会预分配成满尺寸，那个长度不代表下了多少。
        /// </summary>
        private bool HasProgress(string dest)
        {
            try
            {
                string pd, mp;
                ResolvePaths(dest, out pd, out mp);
                var plan = LoadPlan(mp);
                if (plan?.Parts != null)
                {
                    foreach (var p in plan.Parts)
                    {
                        if (p.W > 0)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private async Task<(bool ok, string msg)> AttemptAsync(string url, string dest,
            Dictionary<string, string> headers, IProgress<DownloadProgress> progress,
            CancellationToken cancel, PauseToken pause, DateTime taskStart)
        {
            var (total, supports) = await ProbeAsync(url, headers, cancel).ConfigureAwait(false);
            // 带出来给 ComputeMaxLinkRefreshes 用（刷新上限要按文件大小算，见那里）
            _lastProbedTotal = total;
            if (supports && total > 0)
            {
                // 落盘方式唯一：独立分片 + 边合并，
                // 在 DownloadSegmentedAsync 内部统一分派，这里不再分支。
                return await DownloadSegmentedAsync(url, dest, headers, total, progress, cancel, pause, taskStart).ConfigureAwait(false);
            }
            return await DownloadSingleAsync(url, dest, headers, total, progress, cancel, pause).ConfigureAwait(false);
        }

        private Dictionary<string, string> BuildHeaders()
        {
            var h = new Dictionary<string, string>
            {
                { "User-Agent", _ua },
                { "Referer", _referer },
            };
            // 显式下发完整 Cookie，与 Python 版 _headers() 行为一致。
            // 只靠 CookieContainer 隐式附加并不可靠：它按域/路径过滤，
            // 而直链域名（如 dl-pc-zb.drive.quark.cn）与种入时使用的 .quark.cn
            // 未必总能匹配上。实测 Python 版不带 Cookie 会被防盗链直接拒（412）。
            if (!string.IsNullOrEmpty(_cookie))
                h["Cookie"] = _cookie;

            // 客户端伪装补齐：取链时用的是"夸克 PC 客户端"身份（ApiUa），
            // 下载时若只带 UA 不带 Origin/Sec-Fetch，头集合就不自洽 ——
            // 真实客户端发起请求时这组头是齐的，缺项本身即异常特征。
            // 参考 QuarkDownloader 在 sendFilesToMotrixRpc 中同样补了 Referer，
            // 这里把 Origin 与 Sec-Fetch-* 一并补齐，形成完整闭环。
            h["Origin"] = QuarkConstants.Origin;
            h["Accept"] = "*/*";
            h["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.8";
            h["Sec-Fetch-Dest"] = "empty";
            h["Sec-Fetch-Mode"] = "cors";
            h["Sec-Fetch-Site"] = "cross-site";
            return h;
        }

        private static void SeedCookies(CookieContainer jar, string cookie)
        {
            if (string.IsNullOrEmpty(cookie))
                return;
            foreach (var part in cookie.Split(';'))
            {
                var p = part.Trim();
                if (p.Length == 0)
                    continue;
                int eq = p.IndexOf('=');
                if (eq <= 0)
                    continue;
                try
                {
                    jar.Add(new Cookie(p.Substring(0, eq).Trim(), p.Substring(eq + 1).Trim(), "/", ".quark.cn"));
                }
                catch
                {
                    // 单个 Cookie 异常不影响整体
                }
            }
        }

        private static IEnumerable<Dictionary<string, string>> FallbackHeaders()
        {
            yield return new Dictionary<string, string>
            {
                { "User-Agent", QuarkConstants.DlUa },
                { "Referer", QuarkConstants.Referer },
                { "Origin", QuarkConstants.Origin },
            };
            yield return new Dictionary<string, string>
            {
                { "User-Agent", QuarkConstants.DlUa },
            };
        }

        private static bool IsRetryable4xx(string err)
        {
            var m = Regex.Match(err ?? "", @"HTTP Error (\d+)");
            if (!m.Success)
                m = Regex.Match(err ?? "", @"HTTP (\d+)");
            if (!m.Success)
                return false;
            int code;
            return int.TryParse(m.Groups[1].Value, out code) && code >= 400 && code < 500;
        }

        private void ClearPartial(string dest)
        {
            string pd, mp;
            ResolvePaths(dest, out pd, out mp);
            try { File.Delete(mp); } catch { }
            // 独立分片模式会留下一个分片目录。不清理的话，用户取消一次就在
            // 下载目录里永久留一堆孤儿文件 —— 尤其是大文件，分片加起来可能好几 GB。
            try
            {
                if (Directory.Exists(pd))
                    Directory.Delete(pd, true);
            }
            catch { }
            // 指定了 partsRoot 时，分片目录的**父目录**（hash 目录）也是我们建的，
            // 一并清掉，否则会在根目录下堆积一堆空壳子目录。
            if (!string.IsNullOrEmpty(_partsRoot))
            {
                try
                {
                    string baseDir = Path.GetDirectoryName(pd);
                    if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir)
                        && Directory.GetFileSystemEntries(baseDir).Length == 0)
                        Directory.Delete(baseDir, false);
                }
                catch { }
            }
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
        }

        // ---------------- 探测 ----------------

        private async Task<(long total, bool supports)> ProbeAsync(string url, Dictionary<string, string> headers,
            CancellationToken cancel)
        {
            try
            {
                var h = new Dictionary<string, string>(headers) { ["Range"] = "bytes=0-0" };
                using (var req = BuildRequest(HttpMethod.Get, url, h))
                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
                {
                    if ((int)resp.StatusCode == 206)
                    {
                        var cr = resp.Content.Headers.ContentRange;
                        if (cr != null && cr.Length.HasValue)
                            return (cr.Length.Value, true);
                    }
                    long cl = resp.Content.Headers.ContentLength ?? -1;
                    if (cl > 0)
                        return (cl, false);
                }
            }
            catch { }
            return (-1, false);
        }

        private HttpRequestMessage BuildRequest(HttpMethod method, string url, Dictionary<string, string> headers)
        {
            var req = new HttpRequestMessage(method, url);
            foreach (var kv in headers)
            {
                if (kv.Key.Equals("Range", StringComparison.OrdinalIgnoreCase))
                {
                    // Range 必须走类型化属性：字符串方式在 .NET Framework 上可能不生效
                    req.Headers.Range = System.Net.Http.Headers.RangeHeaderValue.Parse(kv.Value);
                }
                else
                {
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }
            return req;
        }

        // ---------------- 单线程下载 ----------------

        private async Task<(bool ok, string msg)> DownloadSingleAsync(string url, string dest,
            Dictionary<string, string> headers, long? total, IProgress<DownloadProgress> progress,
            CancellationToken cancel, PauseToken pause)
        {
            long existing = 0;
            try
            {
                if (File.Exists(dest))
                    existing = new FileInfo(dest).Length;
            }
            catch { }

            // 不要相信"文件长度 == 已下载进度"。
            // 共写模式会把目标文件预分配成满尺寸（SetLength(total)），该模式已删除；
            // 此时文件长度 == total，但里面可能一个字节都没下。
            // 若此时走单线程兜底（分片阶段失败后），原代码会用 FileMode.Append
            // 从这个"满尺寸空洞文件"的末尾继续写，结果是最终大小变成 2×total，
            // 文件彻底损坏，而用户看到的提示只是"兜底重试"。
            // 判据：长度已经 >= 总大小 → 说明这是预分配的空洞文件（或已下完），
            // 单线程从 0 重下，用 Create 覆盖，绝不 Append。
            bool preallocated = total.HasValue && total.Value > 0 && existing >= total.Value;
            if (preallocated)
                existing = 0;

            try
            {
                var h = new Dictionary<string, string>(headers);
                if (existing > 0)
                    h["Range"] = "bytes=" + existing + "-";

                using (var req = BuildRequest(HttpMethod.Get, url, h))
                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
                {
                    bool resume = existing > 0 && (int)resp.StatusCode == 206;
                    if (existing > 0 && (int)resp.StatusCode != 206)
                    {
                        // 服务器忽略 Range → 重发不带 Range，从头重下
                        resp.Dispose();
                        req.Dispose();
                        return await ReadSingleBodyAsync(url, headers, dest, 0, total, progress, cancel, pause).ConfigureAwait(false);
                    }
                    return await ReadSingleBodyFromResponseAsync(resp, dest, resume ? existing : 0, total, progress, cancel, pause).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return (false, "已取消");
            }
            catch (Exception ex)
            {
                return (false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private async Task<(bool ok, string msg)> ReadSingleBodyAsync(string url, Dictionary<string, string> headers,
            string dest, long initialDone, long? total, IProgress<DownloadProgress> progress,
            CancellationToken cancel, PauseToken pause)
        {
            using (var req = BuildRequest(HttpMethod.Get, url, headers))
            using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
            {
                return await ReadSingleBodyFromResponseAsync(resp, dest, initialDone, total, progress, cancel, pause).ConfigureAwait(false);
            }
        }

        private async Task<(bool ok, string msg)> ReadSingleBodyFromResponseAsync(HttpResponseMessage resp, string dest,
            long initialDone, long? total, IProgress<DownloadProgress> progress, CancellationToken cancel, PauseToken pause)
        {
            long done = initialDone;
            // 速度显示统一走 EWMA，与分片路径用同一套算法 ——
            // 否则单线程兜底路径和分片路径的读数风格会不一致。
            var meter = new SpeedMeter(SpeedTauSec, done, DateTime.UtcNow);
            int code = (int)resp.StatusCode;
            if (code != 200 && code != 206)
                return (false, "HTTP " + code + " " + resp.ReasonPhrase);
            try
            {
                using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                // 用显式 Seek 而不是 FileMode.Append：
                // Append 的语义是"永远追加到当前末尾"，一旦 initialDone 与实际文件长度
                // 不一致（预分配、上次缓冲未落盘等），就会把数据写到错误位置且无法察觉。
                // 改为 OpenOrCreate + Seek(initialDone) 后，写位置由我们显式决定，
                // 与进度记账严格一致。
                // ⚠️ FileShare 必须带 **Delete**：用户点「删除本地文件」/「取消」时，
                //    只要还有一个句柄没声明 FILE_SHARE_DELETE，Windows 就会拒绝删除
                //    （报"文件正被另一进程使用"）—— 那正是"分片/半成品删不干净"的根因。
                //    带上 Delete 后，文件可以随时被删/改名，我们这边的写入照旧（写到已删除的文件上）。
                using (var fs = new FileStream(dest, FileMode.OpenOrCreate,
                           FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, QuarkConstants.DownloadChunk))
                {
                    if (initialDone > 0)
                    {
                        if (fs.Length > initialDone)
                            fs.SetLength(initialDone);     // 截掉预分配/残留的尾部
                        fs.Seek(initialDone, SeekOrigin.Begin);
                    }
                    else
                    {
                        fs.SetLength(0);                   // 从头下：清空任何残留
                        fs.Seek(0, SeekOrigin.Begin);
                    }

                    var buf = new byte[QuarkConstants.DownloadChunk];
                    var lastReport = DateTime.UtcNow;
                    while (true)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (pause != null)
                            await pause.WaitIfPausedAsync(cancel).ConfigureAwait(false);
                        int n = await stream.ReadAsync(buf, 0, buf.Length, cancel).ConfigureAwait(false);
                        if (n <= 0)
                            break;
                        // 同分片路径：暂停要在写盘前再拦一次，
                        // 否则在途的那次读会把数据写下去（单线程兜底路径同样中招）。
                        if (pause != null)
                            await pause.WaitIfPausedAsync(cancel).ConfigureAwait(false);
                        await fs.WriteAsync(buf, 0, n, cancel).ConfigureAwait(false);
                        done += n;
                        // 节流：单线程路径按 200ms 报一次进度，避免高速下载时 UI 被刷爆
                        var now = DateTime.UtcNow;
                        if ((now - lastReport).TotalMilliseconds >= 200)
                        {
                            Report(progress, done, total, meter.Sample(done, now));
                            lastReport = now;
                        }
                    }
                }
                Report(progress, done, total, 0);
                return (true, "");
            }
            catch (OperationCanceledException)
            {
                return (false, "已取消");
            }
            catch (Exception ex)
            {
                return (false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------- 分片下载（直写，无合并） ----------------

        /// <summary>
        /// 把文件切成若干区间，供多 worker 以"工作窃取"方式领取。
        /// 设计目标：**让不同大小的文件都能跑出它带宽上限的速度**，
        /// 而且读数要稳、不要忽高忽低。
        /// 历史教训（两次，方向相反）：
        ///  1. 早先单分片下限硬编码 4MB，30MB 的文件只能切 7 片，
        ///     用户把线程数调到 512 也只用上 7 个连接 —— 设置形同虚设。
        ///  2. 后来改成"片大小 = 总大小 / 线程数"，用户填 512 就切 512 片。
        ///     结果虽然快，但**速度剧烈波动**：分片太碎，每个连接要换好几次片，
        ///     而每换一次都要重新建连（TCP+TLS 握手期间那条槽位零吞吐），
        ///     于是速度在"满速"和"换片空窗"之间来回锯齿。
        /// **上面那套"片数尽量少"的思路是错的。**
        /// 教训来自 Python 版对比实测：同样的网络、账号、文件，
        /// Python 版能跑 20~100+ MB/s，C# 版只有 3~5 MB/s。
        /// 根因就是分片策略——Python 版「片数 = 线程 × 8，上限 4096」，
        /// C# 版却是「片数 = 并发 × 1.5」，片大而少。
        /// 为什么"片大而少"会致命：
        ///   夸克 CDN 会按连接限速，总有几条连接明显比别人慢。
        ///   片少时，每条连接领到 1~2 片就没了，一条慢连接攥着一大块
        ///   数据慢慢啃，其它连接早早空闲却无片可抢 —— 这就是「尾部塌缩」，
        ///   整个任务的完成时间被最慢的那条连接拖死，总速度也就塌到几条
        ///   慢连接之和。
        /// 片多时（每线程 8 片），快的连接干完立刻抢下一片，慢的只拖自己，
        /// 负载自动均衡，总速度 ≈ 所有连接的带宽之和。
        /// 现在的算法（对齐 Python 版）：
        ///   片数 = max(按大小算的基础值, 有效并发 × 8)，夹取到 [1, 4096]
        /// 下限 256KB 保持不变（低于此建连成本占比过高）。
        /// 上限从 256MB 大幅收到 8MB：超过 8MB 的片就开始有"慢连接拖长尾"
        /// 的风险，而 8MB 在百兆链路上也只跑 0.6 秒，不会碎到反复重连。
        /// 基础值按文件大小分档（借 Python 版 _plan_chunks 的分档思路）：
        /// 小文件不该被开一堆连接去抢几片小数——那样每条连接跑不到一片，
        /// 光握手就把时间耗完了。分档让"片数"随文件大小自然增长。
        /// </summary>
        private static List<(long Start, long End)> Split(long total, int threads)
        {
            if (total <= 0)
                return new List<(long, long)>();

            // 🚨🚨 封顶必须 ≥ `有效并发 × 8`，**不能照抄云析的 512**。
            // 云析的 `min(…, 512)` 是**和它"并发上限 32"配套**的：
            //   云析：并发 32 × 8 = 256 片，被 512 封顶**碰不到** → 实际 16 片/连接，有窃取余量 ✓
            //   我们：并发 512 × 8 = 4096 → **被 512 压回 512** → **1 片/连接，零窃取余量** ✗
            // 实测踩到（2026-10-11）：照抄 512 后分片变成"512 片 × 18.64MB"，
            // 等于每个连接领一整片、干完就闲，慢连接没有任何片可以补 —— 速度反而崩。
            // `片数 = 并发 × 8`（工作窃取深度 8），封顶只是**防爆护栏**，
            // 必须留够余量：`封顶 = max(4096, 并发 × 8)`。
            // 9.5GB + 512 并发 → 4096 片 × 2.3MB（= 2026-10-02 实测与 Python 持平 1.01x 的那套配置）
            // 9.5GB + 256 并发 → 2048 片 × 4.65MB
            // 单分片下限 512KB（原 256KB）：256KB 的小片只在"文件大到必须
            // 用 4096 片"时才需要；对中小文件，256KB 会把片切得过碎
            // （100MB → 400 片），每条连接反复建连，得不偿失。
            const long MinSegBytes = 512L * 1024;

            // 文件越大，片可以越大——因为大文件总时长长，单片大一点
            // 也不会显著拖长尾；而小文件必须用小片，否则片数不够分。
            // 8MB 对 3GB 文件是合适的粒度。
            long maxSegBytes;
            if (total < 32L * 1024 * 1024)        maxSegBytes = 1L * 1024 * 1024;   // <32MB → 1MB
            else if (total < 256L * 1024 * 1024)  maxSegBytes = 2L * 1024 * 1024;   // <256MB → 2MB
            else if (total < 2L * 1024 * 1024 * 1024) maxSegBytes = 8L * 1024 * 1024; // <2GB → 8MB
            else                                   maxSegBytes = 16L * 1024 * 1024;  // ≥2GB → 16MB

            // 有效并发：与 SegmentedDownloader.MaxRealConcurrency 保持一致。
            // 用户填得再大，实际也开不出更多连接，切片时就不该按更大的数去切。
            int eff = threads > 0 ? Math.Min(threads, MaxRealConcurrency) : 1;
            if (eff < 1) eff = 1;

            // 片数封顶 = max(4096, 并发 × 8) —— 保证"每连接至少 8 片"的窃取深度。
            // 详见上方长注释：**不能照抄云析的 512**（那是和它"并发上限 32"配套的）。
            int maxSegments = Math.Max(4096, eff * 8);

            // 片数 = min( max(按大小算的基础值, 有效并发 × 8),  total / 256KB,  512 )
            //        ↑ 基础盈余            ↑ 保留云析的 ×8    ↑ 夹取一         ↑ 夹取二
            // 🚨 我们当初抄云析时只抄了 `×8`，
            // 漏了后两层夹取；后来又自作主张把 `×8` 改成 `×32` —— 双重放大，
            // 结果 9.5GB 文件切出 16384 片 × 0.56MB（比所有参考实现激进 32 倍）。
            // 现在完整对齐云析：`minOf(maxOf(bySize, threads*8), total/256KB, 512)`。
            // 实测（9.5GB + 256 线程）：`wantParts = max(64, 2048) = 2048`
            //   → `min(2048, 37290, 512) = 512` 片 → 每片约 18.6MB（再被 maxSegBytes 夹到 16MB → 597 片）
            //   —— 正好回到 2026-10-02 那个"与 Python 持平（1.01x）"的配置。
            // ⚠️ 尾部塌缩不再靠"全局切小"来治 —— 那会连带牺牲吞吐（我们踩过）。
            //    正解是**静态大片 + 尾部动态窃取**（axel/Gopeed 范式，见 MEMORY.md 总表 G 项）。
            long bySize;
            if (total < 5L * 1024 * 1024)             bySize = 1;
            else if (total < 32L * 1024 * 1024)       bySize = 4;
            else if (total < 256L * 1024 * 1024)      bySize = 16;
            else if (total < 2L * 1024 * 1024 * 1024) bySize = 32;
            else                                       bySize = 64;
            long wantParts = Math.Max(bySize, (long)eff * 8);
            if (wantParts < 1) wantParts = 1;
            // 夹取一：单片不小于 256KB（云析的 minChunkBytes）—— 小文件不该被切太碎
            long capByMinChunk = total / (256L * 1024);
            if (capByMinChunk < 1) capByMinChunk = 1;
            if (wantParts > capByMinChunk) wantParts = capByMinChunk;
            // 夹取二：硬封顶（云析的 512）
            if (wantParts > maxSegments) wantParts = maxSegments;

            long seg = (total + wantParts - 1) / wantParts;
            if (seg < MinSegBytes) seg = MinSegBytes;
            if (seg > maxSegBytes) seg = maxSegBytes;

            // 由上一步的片大小算出片数
            long n = (total + seg - 1) / seg;
            if (n < 1) n = 1;
            if (n > maxSegments) n = maxSegments;
            if (n > int.MaxValue) n = int.MaxValue;

            // 重算一次对齐后的片大小（避免因夹取导致最后一片畸形）
            long finalSeg = (total + n - 1) / n;
            if (finalSeg < 1) finalSeg = 1;

            // 收尾保护：若因 maxSegments 夹取导致单片超过该档上限，
            // 说明文件远超预期（如 100GB+），此时宁可让片大一点，
            // 也不能突破 4096 片的元数据上限。
            var parts = new List<(long Start, long End)>((int)Math.Min(n, int.MaxValue));
            for (long i = 0; i < n; i++)
            {
                long s = i * finalSeg;
                if (s >= total) break;
                long e = Math.Min(s + finalSeg - 1, total - 1);
                parts.Add((s, e));
            }

            // ==================== 头部变长分片（2026-10-13）====================
            // docs/独立分片边合并 §5：真实 CDN 单连接只有 ~40KB/s，而合并
            // 必须严格按片序 —— 于是目标文件的增长速率被「头部那一片的单连接速率」卡住。
            // 30GB 按 16MB/片时，头 16MB 要约 **6.7 分钟**才凑齐，起播体验极差。
            // 把文件**头部**单独切成 1MB 的小片，让多条连接并行下头部；
            // 中后部维持原有的「静态大片 + 尾部动态窃取」（那是实测过的吞吐最优解，
            // 全局切小会崩吞吐，踩过）。
            // ⚠️ **必须是纯函数**：RunSegmentedAsync 每次都会重算 parts 并与 .qmeta 比对
            //    （`PartsEqual`），一旦不一致就重建 plan、W 全部归零 → **进度全丢**。
            //    所以这里只允许用 total/threads 推导常量，绝不能用随机数或时钟。
            // ⚠️ **不能突破 maxSegments**：那是"元数据上限"护栏（防 100GB+ 失控）。
            //    做法是中后部的片数按 `maxSegments - headParts` 夹取，头部另算。
            // ⚠️ 头部小片不会被"动态窃取"误伤：MinStealBytes=512KB，1MB 片的
            //    remainHere 最多 1MB，只在刚开始那一次满足 `>= 2*512KB`，
            //    即便被切半也只是片更小、起播更快，无害。
            const long HeadZoneMinTotal = 1L * 1024 * 1024 * 1024;  // 只在 ≥1GB 的文件启用
            const long HeadZoneBytes = 64L * 1024 * 1024;           // 头部区域最大 64MB
            const long HeadSegBytes = 1L * 1024 * 1024;             // 头部分片 1MB
            const int HeadMaxParts = 64;                            // 头部最多 64 片

            if (total >= HeadZoneMinTotal)
            {
                long headZone = Math.Min(HeadZoneBytes, total / 16);
                long headParts = headZone / HeadSegBytes;
                if (headParts > HeadMaxParts) headParts = HeadMaxParts;

                // 至少 2 片才有并行意义；剩余部分还要够切（否则不如不分）
                long headEnd = headParts * HeadSegBytes - 1;
                long remainTotal = total - (headEnd + 1);
                if (headParts >= 2 && remainTotal >= MinSegBytes)
                {
                    // 中后部**必须沿用上面已经验证过能覆盖到 total 的 `finalSeg`**，
                    // 绝不能按 `nMid` 重新算 `midSeg = remainTotal / nMid` ——
                    // 🚨 2026-10-14 实测踩坑（用户 4.97GB 文件，并发 512 档）：
                    // `n = 4096`、`finalSeg = 1301609`（能完整覆盖 total）。
                    // 变长分支里 `headParts(64) + nMid(4096) = 4160 > maxSegments(4096)`
                    // → `nMid` 被夹成 4032 → 若按 4032 重算 `midSeg` 会更大、被夹回 finalSeg，
                    // 但 `4032 × 1301609 + 67108864 = 5315594752` **够不到 total**，
                    // 而循环靠 `if (ms >= total) break` 提前退出 → **尾部 15.4MB 被静默丢掉**。
                    // 症状：所有分片 100% 下完、meta 记录完整、但目标文件就是差一截，
                    // 且 `.qparts` 已被清空 → 无法续传补下，只能整文件重下。
                    // ✅ 正解：片大小直接用 finalSeg（它对整个 total 是精确的），
                    //    片数按"实际能切出多少片"自然得出，末尾余量并入最后一片。
                    long midSeg = finalSeg;

                    var v = new List<(long Start, long End)>((int)(headParts + n));
                    long s = 0;
                    for (long i = 0; i < headParts; i++)
                    {
                        v.Add((s, s + HeadSegBytes - 1));
                        s += HeadSegBytes;
                    }
                    // 中后部：从 headEnd+1 起按 midSeg 连续切，最后一片吃到 total-1。
                    // 用 while 而不是 for(nMid) —— 片数由覆盖关系自然决定，
                    // 不会再出现"算好的片数切不完整个区间"的缺口。
                    while (s < total)
                    {
                        long e = Math.Min(s + midSeg - 1, total - 1);
                        v.Add((s, e));
                        s = e + 1;
                    }
                    return v;
                }
            }

            return parts;
        }

        // ---------------- 分片下载 ----------------
        // 另一种落盘方式「共写 SharedFile」已**物理删除**：
        // 它是多个 worker 各自 seek 写**同一个**目标文件 = 随机 I/O，机械硬盘上磁头疯狂寻道
        // → 磁盘占用 100%、下载速度也被拖住（用户实测「2.0 后版本都会导致磁盘占用 100%」）。
        // 且文件中间存在空洞，未下完时不是有效媒体文件，不能边下边播。两头不占。
        // 唯一保留：每个分片写独立文件，由专用合并线程按序 append 到目标文件
        // 并立即删除该分片。目标文件从偏移 0 起**连续增长**，因此可以边下边播；
        // 峰值占用约 1×（目标文件 + 未合并分片），而不是 2×
        // ——「全部落盘最后再合并」那种做法才是 2×。
        // 这也是唯一**同时满足机械盘与固态盘**的方案：夸克 CDN 上限 ~16MB/s，
        // 远低于机械盘顺序写 ~150MB/s，只要全程顺序 I/O，磁盘带宽从不是瓶颈。

        /// <summary>独立分片模式下分片文件的存放目录（与目标文件同级，便于整体清理）。</summary>
        private static string DefaultPartsDir(string dest) => dest + ".qparts";

        /// <summary>
        /// 解析某个目标文件对应的「分片目录」与「续传元数据」路径。
        /// **公开静态**：下载器与界面层（如"删除本地文件"清理分片）必须共用这一份推导，
        /// 否则两处各写一遍，改了 partsRoot 就会漂移 —— 那正是"分片删不掉"的成因。
        /// 默认（无 partsRoot）：`{dest}.qparts` / `{dest}.qmeta` —— 与目标同级。
        /// 指定 partsRoot：`{root}\{slug}_{fnv8}` 下统一放 `parts/` 与 `meta.json`，
        ///   避免下载目录被一堆 `.qparts` 污染，也支持分片跨盘存放。
        /// hash 用目标**绝对路径**算（FNV-1a），保证同一文件落到同一目录，
        /// 从而续传能认出旧分片；不同目录下的同名文件也不会互相串。
        /// </summary>
        /// <param name="dest">目标文件完整路径。</param>
        /// <param name="partsRoot">分片根目录；null/空白表示与目标同级。**必须与下载时传入的值一致**。</param>
        public static void ResolvePaths(string dest, string partsRoot, out string partsDir, out string metaPath)
        {
            if (string.IsNullOrWhiteSpace(partsRoot))
            {
                partsDir = DefaultPartsDir(dest);
                metaPath = dest + ".qmeta";
                return;
            }

            string full;
            try { full = Path.GetFullPath(dest); }
            catch { full = dest; }

            string slug = SafeName(Path.GetFileName(dest));
            string key = slug + "_" + Fnv1a(full).ToString("x8");
            string baseDir = Path.Combine(partsRoot.Trim(), key);
            partsDir = Path.Combine(baseDir, "parts");
            metaPath = Path.Combine(baseDir, "meta.json");
        }

        /// <summary>实例内的路径推导：直接复用公开静态实现，保证两边永不漂移。</summary>
        private void ResolvePaths(string dest, out string partsDir, out string metaPath)
            => ResolvePaths(dest, _partsRoot, out partsDir, out metaPath);

        /// <summary>
        /// 计算"删除本地文件"时需要一并清理的分片目录与续传元数据路径。
        /// 这是给界面层用的<b>只读推导</b>：除了当前 <paramref name="partsRoot"/> 对应的位置，
        /// 还会带上历史遗留的默认位置（`{dest}.qparts` / `{dest}.qmeta`）。
        /// 原因：用户可能**中途改过**分片根目录 —— 之前下载留下的分片在旧位置，
        /// 只按当前位置删就会漏掉，表现为"分片根本没删掉"。
        /// 返回的集合已按路径去重，可直接逐个尝试删除（不存在则跳过）。
        /// </summary>
        public static IEnumerable<string> EnumeratePartialPaths(string dest, string partsRoot)
            => EnumeratePartialPaths(dest, new[] { partsRoot });

        /// <summary>
        /// 同上，但支持传入**多个**分片根目录（当前生效 + 历史用过的）。
        /// 用户多次改过设置时，残留分片可能散落在多个旧根目录下，必须逐个覆盖。
        /// </summary>
        public static IEnumerable<string> EnumeratePartialPaths(string dest, IEnumerable<string> partsRoots)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) 每个（当前 + 历史的）根目录对应的位置
            var roots = new List<string>();
            if (partsRoots != null)
                foreach (var r in partsRoots)
                    if (!string.IsNullOrWhiteSpace(r)) roots.Add(r.Trim());
            if (roots.Count == 0) roots.Add(null);   // 无自定义根 → 只有默认位置

            foreach (var root in roots)
            {
                ResolvePaths(dest, root, out string partsDir, out string metaPath);
                if (seen.Add(partsDir)) yield return partsDir;
                if (seen.Add(metaPath)) yield return metaPath;

                // 指定了 partsRoot 时，hash 目录（分片目录的父目录）也是下载器建的，
                // 删完 parts/meta.json 后它就成了空壳，一并回收，避免堆积。
                if (!string.IsNullOrWhiteSpace(root))
                {
                    string baseDir = Path.GetDirectoryName(partsDir);
                    if (!string.IsNullOrEmpty(baseDir) && seen.Add(baseDir))
                        yield return baseDir;
                }
            }

            // 2) 历史默认位置：即便现在改用了 partsRoot，旧分片也可能留在这里
            string legacyParts = DefaultPartsDir(dest);
            string legacyMeta = dest + ".qmeta";
            if (seen.Add(legacyParts)) yield return legacyParts;
            if (seen.Add(legacyMeta)) yield return legacyMeta;
        }

        /// <summary>把文件名里对路径非法的字符替换掉（分片根目录下要建同名子目录）。</summary>
        private static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "file";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            string s = sb.ToString();
            // 目录名过长会占满 MAX_PATH 预算，截断到 40 字符足够区分。
            return s.Length > 40 ? s.Substring(0, 40) : s;
        }

        /// <summary>FNV-1a 32 位哈希。用于把绝对路径压成一个稳定的短 key（非加密用途）。</summary>
        private static uint Fnv1a(string s)
        {
            uint h = 2166136261;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619;
            }
            return h;
        }

        /// <summary>第 idx 片的分片文件路径。定长编号保证按文件名排序即等于按片序。</summary>
        private static string PartPath(string dir, int idx)
            => Path.Combine(dir, "p" + idx.ToString("D6", CultureInfo.InvariantCulture));

        /// <summary>
        /// 把目录设为隐藏（或取消隐藏）。失败静默 —— 隐藏只是外观，
        /// 权限受限导致设不上时不应影响下载本身。
        /// </summary>
        private static void ApplyHidden(string dir)
        {
            try
            {
                var di = new DirectoryInfo(dir);
                if (!di.Exists)
                    return;
                if (HidePartsDir)
                    di.Attributes |= FileAttributes.Hidden;
                else
                    di.Attributes &= ~FileAttributes.Hidden;
            }
            catch { }
        }

        private async Task<(bool ok, string msg)> DownloadSegmentedAsync(string url, string dest,
            Dictionary<string, string> headers, long total, IProgress<DownloadProgress> progress,
            CancellationToken cancel, PauseToken pause, DateTime taskStart)
        {
            // 共写模式（SharedFile）已物理删除，落盘方式唯一：独立分片 + 边合并。
            return await RunSegmentedAsync(url, dest, headers, total, progress, cancel, pause,
                taskStart: taskStart).ConfigureAwait(false);
        }

        /// <summary>
        /// 分片下载核心：每个分片写独立文件，由专用合并线程按序 append 到目标文件并立即删除分片。
        /// </summary>
        private async Task<(bool ok, string msg)> RunSegmentedAsync(string url, string dest,
            Dictionary<string, string> headers, long total, IProgress<DownloadProgress> progress,
            CancellationToken cancel, PauseToken pause, DateTime taskStart)
        {
            var parts = Split(total, _threads);
            // 🚨 分片规划**必须完整覆盖 [0, total)**。
            // 2026-10-14 出过一次：变长分片的 nMid 被 maxSegments 夹小后按它重算片大小，
            // 导致尾部 15.4MB 根本没被切出来 → 分片全部下完、meta 完整、目标文件却差一截，
            // 而且 .qparts 已清空、无法续传，只能整文件重下（用户实测 4.97GB 文件踩中）。
            // 这里做一次覆盖性断言：一旦不闭合就打日志，绝不再静默。
            if (parts.Count > 0)
            {
                long covered = 0;
                bool contiguous = true;
                long expect = 0;
                foreach (var p in parts)
                {
                    if (p.Start != expect) { contiguous = false; break; }
                    covered = p.End + 1;
                    expect = p.End + 1;
                }
                if (!contiguous || covered != total)
                {
                    Log?.Invoke(string.Format(
                        "⚠️ 分片规划异常：{0} 片、覆盖到 {1}，而文件应为 {2}（缺口 {3} 字节）。"
                        + "这会导致目标文件缺失尾部数据且无法续传 —— 请把此行日志反馈给开发者。",
                        parts.Count, covered, total, total - covered));
                }
            }
            string metaPath, partsDirBase;
            ResolvePaths(dest, out partsDirBase, out metaPath);
            string partsDir = partsDirBase;

            // 共写模式（SharedFile）已物理删除：本类只支持「独立分片 + 边合并」。
            // 原来由构造参数 `writeMode` 决定的分支已全部解包，此常量仅为兼容
            // DiagnosticsSnapshot.MergeMode 字段而保留（现在恒为 true）。

            // 把"实际切了几片"明明白白打出来。
            // 用户调大线程数却只跑出 7 个连接时，这一行能让原因立刻可见。
            if (parts.Count > 0)
            {
                long segBytes = parts[0].End - parts[0].Start + 1;
                Log?.Invoke(string.Format(
                    "分片规划: 文件 {0}，并发连接上限 {1}，切为 {2} 片，每片约 {3}",
                    Fmt(total), _threads, parts.Count, Fmt(segBytes)));
                if (parts.Count < _threads)
                {
                    // 片数少于线程数，说明文件还不够大，撑不起这么多连接。
                    // 这不是 bug：此时每条连接都能吃满，速度已经到顶。
                    Log?.Invoke(string.Format(
                        "说明: 该文件只切出 {0} 片（少于上限 {1}），因为文件偏小；此时每条连接都能吃满，速度已接近该文件的上限。",
                        parts.Count, _threads));
                }
            }

            var plan = LoadPlan(metaPath);
            // ⚠️ 这里加了一条保护：**已有进度的老任务绝不重建 plan**。
            // 原逻辑只要 `!PartsEqual(plan.Parts, parts)` 就重建 —— 那意味着"分片算法一变，
            // 老任务已下载的进度全部作废（W 归零）"。2026-10-13 引入「头部变长分片」后，
            // 同一文件重算出来的 parts 与旧 .qmeta 必然不同，若不保护，
            // **每个进行中的老任务升级后都会从头再下一遍** —— 对 30GB 文件是灾难。
            // 判据：Total 一致 + Parts 非空 + **确实有进度**（任一片 W>0 或已合并过字节）
            // ⇒ 沿用 .qmeta 里的旧规划，让这个任务按老分片安安静静下完。
            // 只在「全新任务 / Total 变了 / 旧规划是空的」时才写入新规划。
            bool hasOldProgress = plan != null && plan.Total == total
                                 && plan.Parts != null && plan.Parts.Count > 0
                                 && plan.Parts.Any(p => p.W > 0);
            if (hasOldProgress)
            {
                Log?.Invoke(string.Format(
                    "续传: 沿用 .qmeta 中已有的分片规划（{0} 片）——本次不重算，已下载进度不被清零。",
                    plan.Parts.Count));
            }
            else if (plan == null || plan.Total != total || plan.Parts.Count != parts.Count
                               || !PartsEqual(plan.Parts, parts))
            {
                plan = new DownloadPlan { Total = total, Parts = new List<PartPlan>() };
                foreach (var p in parts)
                    plan.Parts.Add(new PartPlan { S = p.Start, E = p.End, W = 0 });
                SavePlan(metaPath, plan);
            }

            // ---- 落盘准备：两种方式在这里分道扬镳 ----

            // 已合并进目标文件的分片数 / 字节数
            int mergedCount = 0;
            long mergedBytes = 0;
            // 第 mergedCount 片之内、已经合并进目标文件的字节数。
            // 有了它才能"边落盘边合并"：合并进度不必停在分片边界上。
            long mergedInHead = 0;

            // 分片"已完成"标记：合并线程据此判断能不能安全删掉
            // 某个分片文件 —— 必须等 worker 关闭文件流之后才能删，否则 worker 后续的
            // 写入会落进一个已删除的文件，数据静默丢失。
            // 动态窃取会往 plan.Parts 里**追加**新片（见 SplitLongestPartIfNeeded），
            // 所以这些按索引访问的数组必须**预留容量** —— 只按当前片数分配的话，
            // 新片一被访问就 IndexOutOfRange。
            int partCapacity = plan.Parts.Count * 4;
            int[] partDone = new int[partCapacity];

            // 绝不预分配目标文件。
            // 直写模式会 SetLength(total) 把空间一次占掉，但在这里那等于自毁：
            // 文件一开就是满尺寸、中间全是零，播放器读到的是一片空洞，
            // "边下边播"这个唯一的卖点当场作废。所以目标文件必须从 0 开始长。
            try
            {
                Directory.CreateDirectory(partsDir);
                ApplyHidden(partsDir);
            }
            catch (Exception ex)
            {
                return (false, "无法创建分片目录 " + partsDir + ": " + ex.Message);
            }

            // 目标文件当前长度 = 已合并进它的字节数。
            // merge 模式只 append、从不回写，所以文件长度天然就是"连续前缀"的长度，
            // 不需要（也不能）按分片边界对齐 —— 现在支持"边落盘边合并"，
            // 合并进度本来就停在某一片的中间。
            long have = 0;
            try { if (File.Exists(dest)) have = new FileInfo(dest).Length; } catch { }
            if (have > total) have = total;

            // 由已合并字节数反推：进度停在第几片、片内偏移多少
            long acc = 0;
            for (int i = 0; i < plan.Parts.Count; i++)
            {
                long sz = plan.Parts[i].E - plan.Parts[i].S + 1;
                if (acc + sz > have) break;
                acc += sz;
                mergedCount = i + 1;
            }
            mergedInHead = have - acc;
            mergedBytes = have;

            // 对账：目标文件尾部那一段必须是"从第 mergedCount 片的分片文件里读来的"。
            // 若超出分片的可信已写前缀，说明目标文件里混进了来路不明的字节，
            // 只能把这一段截掉重来 —— 宁可少一点，也不能产出内容错误的文件。
            if (mergedInHead > 0)
            {
                long headLen = 0;
                try
                {
                    var hfi = new FileInfo(PartPath(partsDir, mergedCount));
                    if (hfi.Exists) headLen = hfi.Length;
                }
                catch { }
                long trustedHead = Math.Min(headLen, plan.Parts[mergedCount].W);
                if (trustedHead < mergedInHead)
                {
                    Log?.Invoke(string.Format(
                        "边合并: 目标文件尾部 {0} 字节超出分片 {1} 的可信进度，需要截除重下（内容正确优先于省流量）。",
                        Fmt(mergedInHead - trustedHead), mergedCount));
                    try
                    {
                        using (var fs = new FileStream(dest, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                            fs.SetLength(acc + trustedHead);
                    }
                    catch (Exception ex)
                    {
                        return (false, "无法截除目标文件的未提交尾部: " + ex.Message);
                    }
                    mergedInHead = trustedHead;
                    mergedBytes = acc + trustedHead;
                }
            }

            // 与磁盘对账：进度只认 .qmeta 记账，盘长只用来截断。
            // 强杀/崩溃瞬间，最后一个在飞的 64KB 写会"撕裂"：
            // 分片文件长度涨上去了，但尾部内容是垃圾（verify1017 实测两次：
            // 第一次全零、第二次随机垃圾，且源文件里根本不存在这些字节）。
            // 旧代码把 FileInfo.Length 当可信进度填进 W → 合并线程把垃圾拼进
            // 目标文件 → 长度对、内容坏；而完成校验只比长度，完全看不出来，
            // 直接报"下载成功"（用户反馈「下载完文件全是损坏」）。
            // 铁证：两次运行损坏区都恰好是残留分片文件的最后一个 64KB。
            // 所以：W 只认记账（有 .qmeta 时），盘长超出记账的部分一律截掉
            // （撕裂尾不返回、记账不可能包含它）；没有 .qmeta 时记账为 0，
            // 盘长全不可信 → 整片重下。宁可少一点，也不能产出内容错误的文件。
            for (int i = mergedCount; i < plan.Parts.Count; i++)
            {
                long sz = plan.Parts[i].E - plan.Parts[i].S + 1;
                long len = 0;
                try
                {
                    var fi = new FileInfo(PartPath(partsDir, i));
                    if (fi.Exists) len = fi.Length;
                }
                catch { }
                // 只信任记账值：min(记账, 盘长)。
                // 记账 > 盘长（缓冲未落盘进程被杀）→ 取盘长，续写位置仍正确；
                // 盘长 > 记账 → 超出的部分是记账之后新写的，其中含撕裂尾，一律截掉。
                long trusted = Math.Min(sz, Math.Min(plan.Parts[i].W, len));
                if (len > trusted)
                {
                    Log?.Invoke(string.Format(
                        "边合并: 分片 {0} 盘长 {1} 超出可信进度 {2}，需要截掉 {3} 字节（重下以保证内容正确）。",
                        i, Fmt(len), Fmt(trusted), Fmt(len - trusted)));
                    try
                    {
                        using (var fs = new FileStream(PartPath(partsDir, i), FileMode.Open,
                                   FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                            fs.SetLength(trusted);
                    }
                    catch (Exception ex)
                    {
                        return (false, "无法截除分片 " + i + " 的未提交尾部: " + ex.Message);
                    }
                }
                plan.Parts[i].W = trusted;
                // 完整分片直接标记完成。
                // 这一步不能省：worker 见到"本片已完整"会直接跳过、不置标记，
                // 而合并线程要等标记才敢删文件 —— 两边互相干等，实测会死锁
                // （续传时目标文件卡在某个分片边界上再也不动）。
                if (trusted >= sz)
                    partDone[i] = 1;
            }
            // 已合并的分片：分片文件应已删除，残留则清掉，并补齐记账
            for (int i = 0; i < mergedCount; i++)
            {
                plan.Parts[i].W = plan.Parts[i].E - plan.Parts[i].S + 1;
                try { File.Delete(PartPath(partsDir, i)); } catch { }
            }
            SavePlan(metaPath, plan);

            Log?.Invoke(string.Format(
                "写入方式: 独立分片 + 边合并（可边下边播）。分片落盘于 {0}，合并后按序追加到目标文件并立即删除分片，峰值占用约 1×。",
                partsDir));

            // 空间预检：merge 模式不预分配，所以"磁盘满"不会在建文件时暴露，
            // 而是下载到一半才炸，还会留下分片目录垃圾。提前算一次，明确拒绝。
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(dest));
                if (!string.IsNullOrEmpty(root))
                {
                    var di = new DriveInfo(root);
                    if (di.IsReady)
                    {
                        long need = total - mergedBytes;
                        if (di.AvailableFreeSpace < need)
                        {
                            return (false, string.Format(
                                "磁盘空间不足：本次还需 {0}，而 {1} 仅剩 {2}。请清理空间后重试（已合并的部分会保留，可续传）。",
                                Fmt(need), root, Fmt(di.AvailableFreeSpace)));
                        }
                        Log?.Invoke(string.Format("空间预检: 本次还需 {0}，{1} 剩余 {2}，充足。",
                            Fmt(need), root, Fmt(di.AvailableFreeSpace)));
                    }
                }
            }
            catch { }

            var queue = new ConcurrentQueue<int>();
            for (int i = mergedCount; i < plan.Parts.Count; i++)
                queue.Enqueue(i);

            long done = 0;
            foreach (var p in plan.Parts)
                done += p.W;

            var errors = new List<string>();
            var errLock = new object();
            int cancelled = 0;

            // 区分"中断"到底是谁导致的。
            // cancelled 这个标志被四种情形共用：① 用户主动取消（外部 cancel 令牌）
            // ② 反压重试延迟被取消 ③ 服务器不支持 Range ④ 看门狗判定停滞（这个已有 stalledFlag）。
            // 只有 ① 才应该清掉半成品；②③ 是环境问题，清掉等于毁掉用户几小时的进度。
            // 判据用 cancel.IsCancellationRequested 最可靠：只有 UI 真点了取消才会置位，
            // watchdog 取消和 range-not-supported 都不会碰外部令牌。
            int userCancelled = 0;

            // 用**整个任务**的起始时刻，而不是本次尝试的起始时刻：
            // 直链失效会触发重新取链并重跑一次，若按"本次尝试"计时，
            // 收尾报告会把总耗时说成只有最后一次尝试的几秒，速度虚高好几倍，
            // 用户拿这个数去调并发就会被带偏。
            var t0 = taskStart;

            // 合并线程已合并的字节数，供 5 秒诊断日志跨线程读取
            long[] mergedBox = new long[] { mergedBytes };

            // 「所有 worker 都已结束，只剩合并线程在收尾」的标志（跨线程用 Volatile 读写）。
            // 收尾阶段 done 不再增长（数据早就下完了），但磁盘 I/O 还在跑。
            // 若进度上报线程不知道这件事，会：① 把"无数据"误判成网络停滞而主动中止；
            // ② 界面冻结在「100% + 速度 0」，用户以为卡死（实测反馈「就显示 0 kb 不动」）。
            // 有了这个标志，reporter 就能在收尾期改为上报「正在合并分片」并跳过停滞判定。
            bool[] workersDoneBox = new bool[1];

            // ==================== 慢连接抢占：采样状态 ====================
            // 按 worker 编号（不是分片编号）索引：看门狗要问的是"第 i 条连接现在跑多快"，
            // 而一条连接同一时刻只处理一片。
            // 尺寸用 MaxRealConcurrency 而不是 workerCount —— 因为 workerCount 要到
            // 更后面才算得出来，而采样要挂在先启动的进度循环上。workerNo 恒 < workerCount
            // ≤ MaxRealConcurrency，故这个尺寸永远够用。
            var wCurPart = new int[MaxRealConcurrency];      // 当前片索引，-1 = 空闲
            var wStart = new long[MaxRealConcurrency];       // 本片开始时刻
            var wLastTick = new long[MaxRealConcurrency];    // 上次采样时刻
            var wLastW = new long[MaxRealConcurrency];       // 上次采样时的 part.W
            var wLastIdx = new int[MaxRealConcurrency];      // 上次采样时的片索引（换片则重置基线）
            var wCts = new CancellationTokenSource[MaxRealConcurrency];   // 片级取消源
            // 本 worker 当前在飞的那个 HTTP 响应对象。
            // 为什么必须留着它：.NET Framework 下 HttpClient **一旦收到响应头**，
            // 之后对内容流的读取就不再受 CancellationToken 约束 —— 连接"被接了但
            // 一个字节都不发"时，取消令牌完全无效，worker 永远卡在 ReadAsync 上
            // → Task.WhenAll 永不返回 → 界面停在「99%、速度 0」再也回不来。
            // 唯一能把它救出来的办法是 Dispose 这个响应（会断掉底层 socket）。
            var wResp = new HttpResponseMessage[MaxRealConcurrency];
            // 本 worker 上一次"确实收到过数据"的时刻：只在 part.W 真增长时刷新，
            // 因此"长时间不刷新"精确等价于"这条连接假死了"。
            var wLastGainMs = new long[MaxRealConcurrency];
            // 本 worker 正被"假死抢救"置位：catch 据此把这次异常按"被抢占"处理
            // （分片回队、不计失败、不烧重试预算），而不是当成一次失败尝试。
            var wHangRescued = new int[MaxRealConcurrency];
            for (int i = 0; i < MaxRealConcurrency; i++)
            {
                wCurPart[i] = -1;
                wLastIdx[i] = -1;
                wLastGainMs[i] = NowMs();
            }
            // 按分片索引：该片被抢过几次、上次何时被抢
            var partPreempt = new int[partCapacity];
            var partLastPreempt = new long[partCapacity];
            // 按分片索引：该片已经"整轮重试"过几次（见 MaxPartRounds）。
            var partRounds = new int[partCapacity];
            // 按分片索引：该片被判"连接假死"抢救过几次。
            // 与 partPreempt 分开计数 —— 假死必须**不受抢占配额限制**（见 HangSeconds 注释）。
            var partHangRescue = new int[partCapacity];
            // 上次抢占时该片的已写字节数。
            // 用来判断"这次重连有没有真的拿到数据"—— 有进展就重置 partPreempt，
            // 让它继续享有抢占机会（详见抢占循环里的说明）。
            var partPreemptAtW = new long[partCapacity];
            // 动态窃取时改 plan.Parts / 入队要串行化（多个 worker 可能同时切）
            var splitLock = new object();
            int preemptedCount = 0;   // 累计抢占次数（收尾诊断用）
            // 分片回队自愈：全局整轮重试计数（跨分片累计，见 MaxTotalRequeues）
            int totalRequeues = 0;
            int splitCount = 0;       // 累计"动态窃取"切分次数（诊断用）
            // 各路瞬时速度（两遍扫描用：第一遍采样、第二遍按结果抢占）
            var wInstBps = new long[MaxRealConcurrency];
            bool overallSlowLogged = false;   // "整体限速"提示只打一次，避免刷屏
            // "整体限速"抑制窗口的截止时刻（与 nowMs 同基准）。
            long overallSlowUntilMs = 0;

            /// <summary>
            /// 慢连接抢占：采样各路瞬时速度，掐掉明显落后于众人的那几路。
            /// 移植自云析 YunX Agent.md §5.3.1。
            /// 判定阈值 = max(12KB/s, 本任务平均单连接速度 / 2)。
            /// 用"平均值的一半"而不是绝对值，是为了自适应：整站都快时门槛自动抬高，
            /// 不会因为绝对下限太低而误伤；整站都慢时由 12KB/s 兜底，不至于全都判慢。
            /// </summary>
            void PreemptSlowChunks(double speedBps)
            {
                // 用户暂停时各路本来就没有数据，
                // 若照常采样，必然得出"所有连接都慢"的结论 —— 实测已见到
                // 「整体限速中：16/16 路都低于阈值 12.3 KB/s（本轮实测平均单连接 0 B/s）」，
                // 轻则日志被刷屏，重则触发一轮无谓的掐连接重连，恢复时还会
                // 一次性撞上服务端并发风控。暂停是用户意愿，不是故障：
                // 这里直接不采样、不抢占。
                if (pause != null && pause.IsPaused)
                {
                    // 同时必须滚动刷新"最后有数据"时刻：暂停时连接静默是预期行为，
                    // 若把这段静默算进假死判定，恢复后会有一大批**健康**连接被
                    // 集体误判"假死"并掐掉重连（fork 实测：暂停 20 秒恢复后
                    // 40+ 条健康连接在 0.85 秒内被集体掐掉）。
                    long pauseNow = NowMs();
                    for (int i = 0; i < wLastGainMs.Length; i++)
                        wLastGainMs[i] = pauseNow;
                    return;
                }

                // ================= 第零遍：假死连接抢救（最高优先级）=================
                // 这一遍刻意放在"相对速度"抢占之前，且不看任何速度阈值、不看整体限速、
                // 不看剩余量、不占用抢占配额。理由：这里的判据是**一个字节都没来过**，
                // 它和"这条连接比别的慢"是两码事 ——
                //   慢   = 服务端限速（重连无用，整体限速判定就是为此存在）
                //   假死 = 连接坏了（重连几乎必好，越快断越省时间）
                // 把两者混在"按速度抢占"一条路径里，假死连接会被抢占配额
                // （PreemptMax / PreemptMaxEndgame）和"整体限速"判定拦下来 ——
                // 表现就是「卡 99%、速度 0、再也回不来」。
                // .NET Framework 下 HttpClient 收到响应头之后，内容流读取**不响应取消令牌**：
                // Cancel 了 worker 也照样卡在 ReadAsync 上。Dispose 响应会真正断掉
                // 底层 socket，那次读才会立刻以异常返回。
                {
                    // 🚨🚨
                    // 曾经加过 `if (lastOverallSlow) return;`（理由听起来很对：整体限速下重连无用），
                    // 用 VHANG_PCT=25 实测**直接踩死**，两个原因都是致命的：
                    //   ① **自我锁定**：整体限速分支本身就是 `return`，一旦置 true，
                    //      后面每一轮都在第零遍就返回，永远走不到"重置为 false"那行 → 永久停摆。
                    //   ② **因果倒置**：假死连接的瞬时速度本来就是 0，它们会把"平均单连接速度"
                    //      一起拉到 0 → 于是**假死被误判成"整体限速"** → 越该抢救的时候越不抢救。
                    //      实测日志原话：「整体限速中：10/10 路都低于阈值 12.3 KB/s（平均 0 B/s）」
                    //      —— 那 10 路**全是假死连接**，却被当成了"服务端配额低"。
                    // 结论：假死与"慢"必须彻底分开判（这正是 fork 作者的原意）。
                    // 防误杀的职责交给「30 秒阈值 + 每轮条数上限」这一对，不引入整体限速闸门。

                    long nowMsHang = NowMs();
                    // 每轮抢救条数上限（见 HangRescuePerTick）：防"一轮掐掉几十条"的重连风暴。
                    int hangTaken = 0;
                    for (int i = 0; i < wCurPart.Length && hangTaken < HangRescuePerTick; i++)
                    {
                        int hidx = Volatile.Read(ref wCurPart[i]);
                        if (hidx < 0) continue;
                        if (nowMsHang - wLastGainMs[i] < HangSeconds * 1000L) continue;

                        // 还没进入读取阶段（正在建连/写盘）的交给常规逻辑，别误杀：
                        // 建连在慢网络下可能几十秒，误杀会引发重连风暴。
                        var hresp = Volatile.Read(ref wResp[i]);
                        if (hresp == null) continue;

                        // 该片已抢救够多次 → 判定"对端对该范围恒定假死"，
                        // 不再重连，交给 45 秒全局停滞看门狗收场。
                        if (partHangRescue[hidx] >= MaxHangRescues) continue;
                        partHangRescue[hidx]++;

                        var hpart = plan.Parts[hidx];
                        Log?.Invoke(string.Format(
                            "连接假死: 第 {0} 片（{1}/{2} 字节）已 {3} 秒没有收到任何数据，" +
                            "判定该连接已坏，主动断开重连（第 {4}/{5} 次）。",
                            hidx, Fmt(hpart.W), Fmt(hpart.E - hpart.S + 1),
                            (nowMsHang - wLastGainMs[i]) / 1000,
                            partHangRescue[hidx], MaxHangRescues));

                        wLastGainMs[i] = nowMsHang;                       // 重新计时，避免同一轮反复抢救
                        Volatile.Write(ref wHangRescued[i], 1);           // 让该 worker 的 catch 走"视为抢占"分支
                        Interlocked.Increment(ref preemptedCount);
                        try { hresp.Dispose(); } catch { }                // ← 真正断 socket 的那一下
                        var hcts = Volatile.Read(ref wCts[i]);
                        if (hcts != null) { try { hcts.Cancel(); } catch { } }
                        hangTaken++;                                      // 计入本轮配额（见 HangRescuePerTick）
                    }
                }

                int inflight = 0;
                for (int i = 0; i < wCurPart.Length; i++)
                    if (Volatile.Read(ref wCurPart[i]) >= 0) inflight++;
                if (inflight == 0) return;

                // ⚠️ 注意：这里**不能**用 speedBps / inflight 当作"平均单连接速"。
                // speedBps 是全局 EWMA 速度，带滞后；而 inflight 是**当前**在飞数。
                // 两者不同步会算出荒谬的阈值。实测踩到（2026-10-02）：
                //   场景"先快后慢"（前 14 片在本地飞快完成 → EWMA 冲到 357KB/s，
                //   随后只剩 6 片慢片在磨）→ 阈值被抬到 357/6/2 = 29.8 KB/s，
                //   而 6 路实际只有 10 KB/s → 全部判"慢" → 误判成"整体限速" → 抢占完全不生效。
                //   日志自相矛盾：「6/6 路都低于阈值 29.8 KB/s（平均单连接仅 59.5 KB/s）」
                // 正确做法：用**本轮实际采样的各路瞬时速度**求平均（下面第一遍算完后才知道）。

                // 收尾阶段：在飞很少时，那几路的速度就是用户看到的总速度，
                // 此时重连握手（~0.5s）远比继续干等便宜 —— 放宽年龄与剩余门槛。
                bool endgame = inflight <= PreemptEndgameInflight;
                long minAge = endgame ? PreemptEndgameMinAgeMs : PreemptMinAgeMs;
                long nowMs = NowMs();

                // ---- 第一遍：采样各路瞬时速度，并统计"低于阈值"的条数 ----
                // 阈值 = max(12KB/s, 平均单连接速/2)，其中 12KB/s
                // 是在"正常单连接 40~80KB/s"的前提下定的。但网盘 CDN 的低配额时段，
                // 单连接可能只有 ~10KB/s —— 此时**所有**连接都低于 12KB/s，
                // 会被全体误判为"慢"，于是每轮掐 2 路、重连后还是慢、下轮再掐……
                // 实测症状（2026-10-02 用户日志）：
                //   运行中: 2.53 MB/s，活跃连接 254/256 → 单连接仅 10 KB/s
                //   慢连接抢占: 本轮掐掉 2 路（阈值 12.0 KB/s，在飞 256 路）  ← 每 5 秒一次，不停
                // 结果是**持续重建连接**：白白增加 TLS 握手与网络栈压力（用户感觉"卡"），
                // 而总速一点没涨。
                // 所以：若"慢连接"占了多数，说明这是**整体限速**而非个别坏节点，本轮不抢。
                // 第一遍：采样各路瞬时速度，并累计求和（用于算真实的平均单连接速）
                long sumBps = 0;
                int sampled = 0;
                for (int i = 0; i < wCurPart.Length; i++)
                {
                    int idx = Volatile.Read(ref wCurPart[i]);
                    if (idx < 0) { wInstBps[i] = -1; continue; }

                    long w = plan.Parts[idx].W;

                    // 换片了 → 重置采样基线，本轮不计入统计（避免拿上一片的字节数算速度）
                    if (wLastIdx[i] != idx)
                    {
                        wLastIdx[i] = idx;
                        wLastTick[i] = nowMs;
                        wLastW[i] = w;
                        wLastGainMs[i] = nowMs;   // 新一次尝试 → 假死计时重新开始
                        wInstBps[i] = -1;      // -1 = 本轮无有效样本
                        continue;
                    }

                    long dtMs = nowMs - wLastTick[i];
                    if (dtMs <= 0) { wInstBps[i] = -1; continue; }

                    long wPrev = wLastW[i];
                    long instBps = (long)((w - wPrev) * 1000.0 / dtMs);
                    wLastTick[i] = nowMs;      // 采样窗口照常推进（下一轮仍是 5 秒窗口）
                    wLastW[i] = w;
                    // 确实收到过数据 → 刷新"最后有数据"时刻。
                    // 这是假死判定的**唯一**依据：绝不能改成"采样窗口推移"就刷新，
                    // 否则 CDN 低配额下的健康慢片（40KB/s）会被误判假死、反复重连。
                    if (w > wPrev)
                        wLastGainMs[i] = nowMs;

                    // 年龄不够的**既不计入统计、也不参与抢占**：
                    // 刚建立的连接还没跑起来，瞬时速度天然接近 0，
                    // 会把"启动爬坡期"整体误判成"整体限速"。
                    // 实测（用户 2026-10-02 日志，512 档）：
                    //   11:33:50 开始 → 11:33:56（仅 6 秒）就报
                    //   「整体限速中：335/339 路都低于阈值 12.0 KB/s（平均单连接 840 B/s）」
                    // ——那不是真实速度，是连接还没开始传数据。
                    // 抢占那边本来就要求 age >= minAge，这里用**同一门槛**，语义自洽。
                    // ⚠️ 必须同时置 -1：否则第二遍统计仍会把它们算进 slowCount，
                    //    出现「8/2 路都低于阈值」这种自相矛盾的日志（实测踩到）。
                    if (nowMs - wStart[i] < minAge)
                    {
                        wInstBps[i] = -1;
                        continue;
                    }

                    wInstBps[i] = instBps;
                    sumBps += instBps;
                    sampled++;
                }

                // 阈值基于**本轮实际采样**的平均单连接速（无 EWMA 滞后）
                long avgPerConn = sampled > 0 ? sumBps / sampled : 0;
                long threshold = Math.Max(PreemptMinBps, avgPerConn / 2);

                // 第二遍：统计低于阈值的路数
                int slowCount = 0;
                for (int i = 0; i < wCurPart.Length; i++)
                    if (wInstBps[i] >= 0 && wInstBps[i] < threshold) slowCount++;

                // ⚠️ 收尾阶段（在飞 ≤ 3）**不做**整体限速判定：
                //   ① 样本太少（1~3 路），统计上不可靠 —— 1/1 路慢就会被判成"整体慢"；
                //   ② 收尾恰恰是**最需要抢占**的时候：只剩几路在磨，那几路的速度
                //      就是用户看到的总速度，重连换节点远比继续干等便宜。
                // 这两个机制曾互相打架（实测：进度 19/20、在飞 1 路时被误判"整体限速"
                // 而放弃抢占，最后一片只能干等）。
                // 非收尾阶段 + 样本不足（多在启动爬坡期）：**不动作**。
                // 此时数据不可靠 —— 既不该下"整体限速"的结论，也不该抢占
                // （否则又会回到"每轮空掐几路"的老问题）。
                // 收尾阶段（在飞 ≤3）不受此限：那几路的速度就是用户看到的总速度，该抢就抢。
                if (!endgame && sampled < PreemptOverallSlowMinSamples)
                    return;

                // "整体限速"抑制窗口（粘滞，见 OverallSlowHoldMs）。
                bool systemic = !endgame && nowMs < overallSlowUntilMs;

                if (!endgame && slowCount * 2 >= sampled)
                {
                    // 整体限速：抢占无意义（掐掉重连还是慢），而且有害（持续重建连接）。
                    // ⚠️ 这个结论**只用于"慢连接抢占"**，绝不能外泄给假死抢救 ——
                    //    理由见第零遍那段「实测否决」注释（会造成自我锁定 + 因果倒置）。
                    systemic = true;
                    overallSlowUntilMs = nowMs + OverallSlowHoldMs;
                    if (!overallSlowLogged)
                    {
                        overallSlowLogged = true;
                        Log?.Invoke(string.Format(
                            "整体限速中：{0}/{1} 路都低于阈值 {2}/s（本轮实测平均单连接 {3}/s）。" +
                            "这属于服务端整体配额低、而非个别慢节点，已暂停抢占以免无谓重连。",
                            slowCount, sampled, Fmt(threshold), Fmt(avgPerConn)));
                    }
                }

                if (systemic)
                    return;      // 抑制窗口内不抢占（粘滞，不再每 5 秒反复横跳）

                if (overallSlowLogged)
                {
                    overallSlowLogged = false;
                    Log?.Invoke("整体限速已缓解，恢复慢连接抢占。");
                }

                // ---- 第二遍：对确认偏慢的连接执行抢占 ----
                int taken = 0;
                for (int i = 0; i < wCurPart.Length && taken < PreemptPerTick; i++)
                {
                    int idx = Volatile.Read(ref wCurPart[i]);
                    if (idx < 0) continue;

                    long instBps = wInstBps[i];
                    if (instBps < 0 || instBps >= threshold) continue;   // 本轮无样本 / 不慢

                    long w = plan.Parts[idx].W;
                    if (nowMs - wStart[i] < minAge) continue;          // 还在建连/爬坡
                    if (!endgame)                                       // 收尾阶段不要求剩余量
                    {
                        var pp = plan.Parts[idx];
                        long remain = (pp.E - pp.S + 1) - w;
                        if (remain < PreemptMinRemain) continue;
                    }
                    // 先看"上次抢占之后有没有进展"——**有进展就重置计数**。
                    // 原来 partPreempt 只增不减：抢满 3 次后**永久放弃**那一片，只能干等。
                    // 尾部几片落到坏节点时，就会一路磨到被看门狗中止 —— 用户实测的「卡在 99%」。
                    // 判据：该片自上次被抢后，已写字节增长 ≥ **一片的 1/4**（至少 64KB）
                    // ⇒ 说明重连换到了好节点、确实在下载 → 重置，让它继续享有抢占机会。
                    // ⇒ 若毫无进展（重连还是坏节点）→ **不重置** → 仍会在抢满后放弃，
                    //    不会退化成"无限重连风暴"。
                    if (partPreempt[idx] > 0)
                    {
                        long grew = plan.Parts[idx].W - partPreemptAtW[idx];
                        long quarter = (plan.Parts[idx].E - plan.Parts[idx].S + 1) / 4;
                        if (quarter < 64 * 1024) quarter = 64 * 1024;
                        if (grew >= quarter) partPreempt[idx] = 0;
                    }

                    // 收尾期放宽上限与冷却 —— 只剩几路时多抢几次成本极低，
                    // 而"抢满就永久放弃"会让尾部那几片干等到被看门狗中止（用户实测）。
                    int maxPreempt = endgame ? PreemptMaxEndgame : PreemptMax;
                    long cooldownMs = endgame ? PreemptEndgameCooldownMs : PreemptCooldownMs;
                    if (partPreempt[idx] >= maxPreempt) continue;
                    if (partLastPreempt[idx] != 0
                        && nowMs - partLastPreempt[idx] < cooldownMs) continue;

                    // 命中：只掐这一片的连接。已写字节保留在 part.W，
                    // 重新入队由 worker 的 catch 负责 —— 这里不做任何破坏性动作。
                    partPreempt[idx]++;
                    partPreemptAtW[idx] = plan.Parts[idx].W;   // 记下被抢时的进度，供下轮判断"有无进展"
                    partLastPreempt[idx] = nowMs;
                    var cts = Volatile.Read(ref wCts[i]);
                    if (cts != null)
                    {
                        try { cts.Cancel(); } catch { }
                    }
                    taken++;
                }

                if (taken > 0)
                {
                    Log?.Invoke(string.Format(
                        "慢连接抢占: 本轮掐掉 {0} 路（阈值 {1}/s，在飞 {2} 路{3}），" +
                        "被抢分片保留已写字节并重新排队。",
                        taken, Fmt(threshold), inflight, endgame ? "，收尾阶段" : ""));
                }
            }

            // 停滞看门狗需要一个能真正掐断读循环的令牌。
            // 只设 cancelled 标志是不够的——worker 正卡在 ReadAsync 上，
            // 不主动取消就只能力等 TCP 超时（可能几分钟），界面一直假死。
            // 这里做链接令牌：外部取消 + 看门狗取消，任一触发都会真的断掉。
            var watchdog = new CancellationTokenSource();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, watchdog.Token))
            {
                var cancelToken = linked.Token;

                void AddError(string e)
                {
                    lock (errLock)
                        errors.Add(e);
                }

                // 进度/落盘监视任务（按 200ms 报进度、约 1s 落一次 sidecar 用于断点续传）
                var stop = new CancellationTokenSource();

                // ---- 停滞看门狗 ----
                // 并发过高时最糟的结局不是报错，而是"看起来在跑、其实一条数据都不动"：
                // 所有连接都在等超时，界面显示速度 0，用户完全不知道该怎么办。
                // 这里用"已下载字节数连续 StalledSeconds 秒无增长"作为判据主动收尾，
                // 给出明确原因和下一步建议，而不是无限期地挂着。
                const int StalledSeconds = 45;
                long lastProgressBytes = 0;
                int stalledFlag = 0;

                // 诊断用：当前有多少条连接正真在传数据。
                // 速度上不去时，问题无非两种：
                //   a) 连接数够但每条都很慢  → 服务端/CDN 限速，客户端无解
                //   b) 连接数根本不够        → 客户端侧有问题，能优化
                // 没有这个数字就只能猜，所以必须能观测。
                int activeConns = 0;

                // workerCount 要等分片规划完成后才知道，但诊断日志在 reporter 里就要用，
                // 所以用一个可写盒把它桥接过去。
                int workerCountBox = 0;

                var reporter = Task.Run(async () =>
                {
                    var lastSave = DateTime.UtcNow;
                    var lastAdvance = DateTime.UtcNow;
                    var lastDiag = DateTime.UtcNow;
                    var lastPreempt = DateTime.UtcNow;
                    var lastReport = DateTime.UtcNow;

                    // ---- 速度估计：EWMA ----
                    // 200ms 的瞬时速度会剧烈抖动，但原因多半不是网速真变了，而是采样噪声：
                    // 分片切得越细，同一时刻"刚好在换片、正在建连还没出数据"的连接就越多，
                    // 这一拍自然掉得低。用指数加权移动平均把读数平滑掉，
                    // 为什么不用滑动窗口、τ 怎么取值，见 SpeedMeter 的注释。
                    var meter = new SpeedMeter(SpeedTauSec, Interlocked.Read(ref done), DateTime.UtcNow);

                    // 收尾（合并）阶段的独立速度计 —— 量的是"合并吞吐"而不是网速。
                    // 懒创建：只有真的进入收尾才需要它。
                    SpeedMeter mergeMeter = null;

                    while (!stop.IsCancellationRequested)
                    {
                        await Task.Delay(200, stop.Token).ConfigureAwait(false);
                        var now = DateTime.UtcNow;

                        // ---- 收尾阶段判定：worker 全结束，只剩合并线程在把分片 append 进目标文件 ----
                        // 这期间 done 恒等于 total（不再增长），但磁盘 I/O 还在跑。
                        // 必须区别对待：① 不能判网络停滞（否则把正常收尾当故障中止）；
                        // ② 要上报阶段，否则界面冻结在「100% + 速度 0」，用户以为卡死。
                        // ⚠️ 只跳过"抢占/停滞"这两类**网络侧**逻辑；
                        //    诊断快照与每秒 SavePlan（崩溃安全）照常执行。
                        // ⚠️ 必须看 workersDoneBox 标志位而不是只看 worker 数：
                        //    否则收尾那一瞬会错误地显示「正在合并分片」。
                        bool draining = Volatile.Read(ref workersDoneBox[0]);
                        long d;
                        double speed;
                        if (draining)
                        {
                            long mb = Interlocked.Read(ref mergedBox[0]);
                            if (mergeMeter == null)
                                mergeMeter = new SpeedMeter(SpeedTauSec, mb, now);
                            speed = mergeMeter.Sample(mb, now);   // 收尾期展示"合并吞吐"
                            d = Interlocked.Read(ref done);
                            lastAdvance = now;                    // 收尾不算停滞
                        }
                        else
                        {
                            d = Interlocked.Read(ref done);
                            speed = meter.Sample(d, now);
                        }

                        // 进度上报降频（循环仍 200ms，见 ProgressReportMs 注释）：
                        // 每次 Report 都会往 UI 线程投递回调，WPF 单线程下 512 并发时
                        // 这些投递会持续挤占 UI。降到 500ms 后进度条依然跟手。
                        if ((now - lastReport).TotalMilliseconds >= ProgressReportMs)
                        {
                            lastReport = now;
                            try { Report(progress, d, total, speed, draining ? "正在合并分片" : null); } catch { }
                        }

                        // ---- 慢连接抢占：每 5 秒采样一次 ----
                        // 与诊断日志同频但**独立计时**：抢占是功能性逻辑，
                        // 不该因为诊断日志将来被调整/注释掉就跟着一起停。
                        if (!draining && (now - lastPreempt).TotalSeconds >= PreemptTickSec)
                        {
                            lastPreempt = now;
                            try { PreemptSlowChunks(speed); }
                            catch { /* 抢占失败绝不能影响下载本身 */ }
                        }

                        // 每 5 秒采一次样（结构化快照给诊断面板用）。
                        // 关键是看 活跃连接数：
                        //  - 活跃数接近并发上限而速度仍低 → 服务端限速，客户端无解
                        //  - 活跃数明显偏低            → 客户端侧没把连接发出去，能优化
                        if ((now - lastDiag).TotalSeconds >= 5.0)
                        {
                            lastDiag = now;
                            string mergeNote = string.Format("，已合并 {0}",
                                Fmt(Interlocked.Read(ref mergedBox[0])));
                            int pc = Volatile.Read(ref preemptedCount);
                            // 对端 IP 分布：直接回答"是不是所有连接都挤在同一个 CDN 节点上"。
                            // 若只有 1 个 IP，说明 DNS 没分散，慢连接抢占也难奏效（重连仍是同节点）。
                            string ipNote = DescribePeerIps(url);

                            // ⚠️ 这条**逐任务**诊断日志默认关掉了。
                            //    实测：11 个任务同时下时，每 5 秒就往日志里灌 11 行
                            //    「运行中: 0 B/s，活跃连接 49/49，分片进度 0/49，命中 IP …」，
                            //    一分钟 130+ 行，把真正有用的行（开始/完成/失败/重试）全冲掉了。
                            //    普通用户根本不看这些；要排查时打开这个开关即可（诊断面板
                            //    不受影响 —— 它走下面的结构化快照，与日志无关）。
                            if (PerTaskDiagLog)
                            {
                                Log?.Invoke(string.Format(
                                    "运行中: {0}/s，活跃连接 {1}/{2}，分片进度 {3}/{4}{5}{6}{7}",
                                    Fmt((long)speed), Volatile.Read(ref activeConns), Volatile.Read(ref workerCountBox),
                                    plan.Parts.Count(p => p.W >= p.E - p.S + 1), plan.Parts.Count, mergeNote,
                                    pc > 0 ? string.Format("，已抢占 {0} 次", pc) : "",
                                    string.IsNullOrEmpty(ipNote) ? "" : "，" + ipNote));
                            }

                            // 结构化快照：给 UI 的诊断面板用（与上面那行日志同源同频）。
                            try
                            {
                                Diagnostics?.Invoke(new DiagnosticsSnapshot
                                {
                                    Name = Path.GetFileName(dest),
                                    Dest = dest,
                                    Speed = speed,
                                    ActiveConnections = Volatile.Read(ref activeConns),
                                    WorkerCount = Volatile.Read(ref workerCountBox),
                                    PartsDone = plan.Parts.Count(p => p.W >= p.E - p.S + 1),
                                    PartsTotal = plan.Parts.Count,
                                    DoneBytes = d,
                                    TotalBytes = total,
                                    PreemptedCount = pc,
                                    MergeMode = true,
                                    MergedBytes = Interlocked.Read(ref mergedBox[0]),
                                    PeerIps = ipNote ?? "",
                                    Stalled = Volatile.Read(ref stalledFlag) != 0,
                                    Timestamp = DateTime.Now,
                                });
                            }
                            catch { /* 诊断上报失败绝不能影响下载 */ }
                        }

                        // 有推进就刷新计时器；没有推进且超时则触发停滞收尾
                        if (d > lastProgressBytes)
                        {
                            lastProgressBytes = d;
                            lastAdvance = now;
                        }
                        // 用户暂停期间不计入停滞判定。
                        // 暂停时 done 必然不增长，若不排除，用户暂停超过 45 秒就会
                        // 被判为"网络停滞"并主动中止 —— 这是错的：暂停是用户的意愿，
                        // 不是故障。恢复后把计时器重置，避免把暂停时长算进停滞窗口。
                        else if (pause != null && pause.IsPaused)
                        {
                            lastAdvance = now;
                        }
                        else if ((now - lastAdvance).TotalSeconds >= StalledSeconds
                                 && Interlocked.CompareExchange(ref stalledFlag, 1, 0) == 0)
                        {
                            Log?.Invoke(string.Format(
                                "下载停滞: 已 {0} 秒没有任何数据写入，主动中止本任务。" +
                                "若是【收尾阶段】（剩余分片很少，日志里有「收尾阶段」字样）出现：" +
                                "多半是这几个分片被夸克 CDN 调度到了慢节点，" +
                                "【与你的网络无关】—— 直接重试即可，重试会重新分配节点。" +
                                "若是【下载中途】出现：才可能是并发连接数超过了路由器/运营商的会话上限，" +
                                "可把线程数降到 64~128 再试。" +
                                "已下载的部分已写进 .qmeta，重试会自动续传。",
                                (int)(now - lastAdvance).TotalSeconds));
                            AddError("stalled: 网络层疑似阻断，长时间无数据");
                            Interlocked.Exchange(ref cancelled, 1);
                            lastAdvance = now;   // 防止重复触发
                            // 必须真的掐断令牌：否则卡在 ReadAsync 上的 worker
                            // 会一直等到 TCP 超时，界面继续假死。
                            try { watchdog.Cancel(); } catch { }
                            // 光 Cancel 令牌**不足以**唤醒卡在读里的 worker：
                            // net48 下内容流读取不响应取消令牌，Task.WhenAll 会一直等它，
                            // 于是"中止任务"本身也收不了场（界面永远停在 99%）。
                            // 唯一有效的是 Dispose 在飞响应（真正断掉底层 socket）。
                            for (int i = 0; i < wResp.Length; i++)
                            {
                                var r = Volatile.Read(ref wResp[i]);
                                if (r != null) { try { r.Dispose(); } catch { } }
                            }
                        }

                        if ((now - lastSave).TotalSeconds >= 1.0)
                        {
                            SavePlan(metaPath, plan);
                            lastSave = now;
                        }
                    }
                });

                // ---- 合并线程 ----
                // 目标文件必须"从 0 起连续增长"，播放器才可能边读边播，
                // 所以这里严格按片序合并：第 next 片没合并完，绝不先碰第 next+1 片
                // （否则目标文件会出现空洞，边下边播立刻失效）。
                // 不等整片下完才合并，而是"分片文件落盘多少就合并多少"。
                // 依据来自真实 CDN 实测：8.89GB 的文件分片是 16MB，而单连接实测只有
                // 几十 KB/s —— 一整片要下好几分钟。若等整片完成才合并，
                // 目标文件会长时间停在 0 字节，"边下边播"形同虚设。
                // 改成按"分片文件在盘上的实际长度"增量追加后，
                // 播放器几秒内就能拿到第一批数据。
                var mergeStop = new CancellationTokenSource();
                long mergedTotal = mergedBytes;
                Task mergeTask = null;

                mergeTask = Task.Run(async () =>
                {
                    // 文件流必须保证释放。
                    // 原代码在 catch 里直接 return，而 finally 里仍无条件 mfs.Dispose()——
                    // 当构造函数抛异常时 mfs 未赋值，那条路径的 Dispose 是空的（甚至编译告警）。
                    // 这里显式声明并置 null，catch 里清空，finally 里判空后释放，
                    // 语义明确且不会漏。
                    FileStream mfs = null;
                    try
                    {
                        // Append 语义在这里是对的：合并线程是"只追加、从不回写"，
                        // 而且目标文件的长度天然就是"已合并的连续前缀"长度
                        // （见 RunSegmentedAsync 里的对账注释）。
                        // FileShare.ReadWrite | FileShare.Delete 让播放器/其它进程能同时打开读取 ——
                        // 这正是"边下边播"得以成立的前提。
                        mfs = new FileStream(dest, FileMode.Append, FileAccess.Write,
                                  FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
                    }
                    catch (Exception ex)
                    {
                        AddError("无法打开目标文件用于合并: " + ex.Message);
                        return;
                    }
                    var mbuf = new byte[1024 * 1024];
                    int next = mergedCount;      // 仅作日志/初值；真正的定位在循环头按 S 做
                    long inPart = mergedInHead;
                    // 收到收尾信号后还要连续多少轮"读不到新数据"才真的退出。
                    // 见下面收尾分支的注释 —— 这个宽限期是必须的。
                    int stallRounds = 0;
                    try
                    {
                        while (true)
                        {
                            // 🚨🚨 每轮按"目标文件当前长度落在哪个片区间里"**重新定位**下一片。
                            // 动态窃取（见 SplitLongestPartIfNeeded）会往
                            // `plan.Parts` **追加**新片 —— 新片的 idx 排在最后，但它的区间落在
                            // 被切那片的后半段。若仍按 idx 递增合并，就会把"后半段"拼到"前半段"之前，
                            // **目标文件直接损坏**（而且是静默损坏，比报错更糟）。
                            // 目标文件永远是"从 0 起的连续前缀"
                            // （合并线程只追加、从不回写）—— 所以"下一个该合并的片"就是
                            // **区间覆盖 target** 的那一片。它天然容忍追加，也天然兼容断点续传。
                            // ⛔⛔ 这里原来写的是 `S == target`（严格等号），
                            // **那是错的**：边下边合时 target 经常落在某一片的**中间** ——
                            // 第一片只落盘 80854 字节就被合并进来，target=80854，而没有任何一片的
                            // S 等于 80854（S 只会是 0、15MB、30MB…）→ `pick < 0` → 合并线程
                            // **静默退出**，之后走到 2837 行收尾校验才报「合并后目标文件大小不符」。
                            // 实录症状：进度 96~98% 但目标文件不足一片、`errors.Count==0`、无任何日志、
                            // 两次失败实际值不同、重试无效（续传后 target 仍不匹配，立刻再 break）。
                            // 小文件/快网络不复现（第一片瞬间下完，target 总落在分片边界上）。
                            // ✅ 区间覆盖同时满足两头：容忍追加片（追加片区间不重叠）+
                            //    允许合并进度停在分片中间（文档 §3.3 实测过该状态可正常续传）。
                            long target = mfs.Length;
                            int pick = -1;
                            for (int i = 0; i < plan.Parts.Count; i++)
                            {
                                if (plan.Parts[i].S <= target && target <= plan.Parts[i].E)
                                { pick = i; break; }
                            }
                            if (pick < 0)
                            {
                                // target 落在所有片区之外。正常只有一种情况：已全部合并完
                                // （target == total）。若不是，说明续传后目标文件残留了一段
                                // 与当前分片规划对不上的数据 —— 必须留痕，否则下次还是全靠倒推。
                                // ⚠️ 不能在这里清进度/截断文件：那会毁掉用户已经下了几个小时的成果。
                                if (target != total)
                                    Log?.Invoke(string.Format(
                                        "合并提前退出：目标文件已有 {0} 字节，但没有任何分片覆盖该偏移（期望 {1}）。"
                                        + "未合并的分片已保留，重试会自动续传。",
                                        target, total));
                                break;                   // 找不到覆盖 target 的片 → 全合并完毕（或还没轮到）
                            }
                            next = pick;
                            inPart = target - plan.Parts[next].S;

                            long sz = plan.Parts[next].E - plan.Parts[next].S + 1;
                            string pp = PartPath(partsDir, next);

                            // net48 异步 FileStream 先扩长，再完成 WriteFile。
                            // 文件长度可能包含在飞/失败写留下的零尾部，不能作为提交点。
                            // W 只在无托管写缓冲的 WriteAsync 成功后发布。
                            long onDisk = 0;
                            try
                            {
                                var pfi = new FileInfo(pp);
                                if (pfi.Exists) onDisk = pfi.Length;
                            }
                            catch { }
                            onDisk = Math.Min(sz, Math.Min(onDisk, plan.Parts[next].W));

                            // 把"新落盘、还没合并"的那一段追加到目标文件
                            if (onDisk > inPart)
                            {
                                long copied = 0;
                                long pending = onDisk - inPart;
                                try
                                {
                                    using (var pfs = new FileStream(pp, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
                                    {
                                        pfs.Seek(inPart, SeekOrigin.Begin);
                                        while (copied < pending)
                                        {
                                            int want = (int)Math.Min(mbuf.Length, pending - copied);
                                            int n = await pfs.ReadAsync(mbuf, 0, want).ConfigureAwait(false);
                                            if (n <= 0) break;
                                            await mfs.WriteAsync(mbuf, 0, n).ConfigureAwait(false);
                                            copied += n;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AddError("合并分片 " + next + " 失败: " + ex.Message);
                                    return;
                                }

                                if (copied > 0)
                                {
                                    inPart += copied;
                                    mergedTotal += copied;
                                    stallRounds = 0;          // 有进展，宽限期重新计时
                                    Interlocked.Exchange(ref mergedBox[0], mergedTotal);
                                    // 立刻 flush：不 flush 的话数据会滞留在 1MB 缓冲里，
                                    // 播放器迟迟读不到这一段，边下边播会明显滞后。
                                    await mfs.FlushAsync().ConfigureAwait(false);
                                }
                                else if (pending > 0)
                                {
                                    // 打算读 pending 字节却一个都没读到：说明分片文件
                                    // 比 onDisk 报出来的还短。这属于异常状态，必须留痕，
                                    // 否则只会表现为"目标文件莫名短了一截"。
                                    Log?.Invoke(string.Format(
                                        "合并线程: 分片 {0} 读出 0 字节（想读 {1}，盘上 {2}，已合并 {3}）。",
                                        next, pending, onDisk, inPart));
                                }
                            }

                            // 整片合并完毕 → 删掉分片文件，把峰值占用压回 1×。
                            // 若留到最后统一删，峰值就会退化成 2×，白白多占一倍空间。
                            if (inPart >= sz)
                            {
                                // 必须等 worker 关闭文件流（partDone 置位）再删：
                                // 提前删会让 worker 后续的写入落进一个已删除的文件，数据静默丢失。
                                if (Volatile.Read(ref partDone[next]) == 0)
                                {
                                    if (mergeStop.IsCancellationRequested
                                        && ++stallRounds >= MergeStallRounds)
                                    {
                                        Log?.Invoke(string.Format(
                                            "合并线程: 分片 {0} 数据已齐（{1} 字节）但 worker 未置完成标记，收尾退出。",
                                            next, inPart));
                                        break;
                                    }
                                    await Task.Delay(100).ConfigureAwait(false);
                                    continue;
                                }
                                stallRounds = 0;
                                try { File.Delete(pp); } catch { }
                                // 不再 `next++` / `inPart = 0` ——
                                // 循环头会按"区间覆盖目标文件当前长度"重新定位下一片。
                                // （动态窃取会追加新片，idx 顺序 ≠ 区间顺序，不能靠递增。）
                                continue;                     // 立刻看下一片，可能已经就绪
                            }

                            if (mergeStop.IsCancellationRequested)
                            {
                                // 收到收尾信号**不能立刻退出**。
                                // 时机是这样的：worker 写完最后一段后先 Dispose 文件流
                                // （把缓冲里那几十 KB 真正落到盘上），才置 partDone 并结束。
                                // 而主线程是在 Task.WhenAll 返回后才取消 mergeStop 的 ——
                                // 看上去"worker 都结束了"，但合并线程可能正好在
                                // worker 落盘之前读到过旧的、偏小的文件长度，
                                // 此时若直接退出，这一片剩下的数据就永远留在分片文件里，
                                // 目标文件短一截，任务却报"大小不符"，用户一脸茫然。
                                // 所以这里要求**连续若干轮都读不到新数据**才认定确实没得合并了。
                                // 正常情况下这个宽限期根本不会用到（分片都已就绪，直接排空）。
                                if (++stallRounds >= MergeStallRounds)
                                {
                                    Log?.Invoke(string.Format(
                                        "合并线程: 收尾退出，停在分片 {0}/{1}（该片已合并 {2}/{3} 字节，盘上 {4}）。剩余分片留给续传。",
                                        next, plan.Parts.Count, inPart, sz, onDisk));
                                    break;
                                }
                            }
                            else
                            {
                                stallRounds = 0;      // 还没到收尾阶段，只是暂时没数据，不算停滞
                            }

                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        AddError("合并线程异常: " + ex.GetType().Name + ": " + ex.Message);
                    }
                    finally
                    {
                        try { if (mfs != null) mfs.Dispose(); } catch { }
                    }
                });

                // 真实并发 = min(用户设的线程数, 分片数, 物理天花板)。
                // 用户设的档位是"意图"，实际能开多少受物理约束。
                int requested = Math.Min(_threads, plan.Parts.Count);
                int workerCount = Math.Min(requested, MaxRealConcurrency);
                Volatile.Write(ref workerCountBox, workerCount);

                if (_threads > MaxRealConcurrency)
                {
                    Log?.Invoke(string.Format(
                        "并发已限幅: 你选择 {0} 路，实际并发 {1} 路（安全上限）。" +
                        "超过此值会打爆本机网络栈与路由器会话表，反而导致整体卡死，故不再往上开。",
                        _threads, workerCount));
                }

                // ---- 限流制动 ----
                // 连接开得多有利于跑满带宽，但也更容易把服务端惹毛（429/503）。
                // 这里做一个"自动缩缸"：一旦观察到限流类错误，就把并发闸门收窄，
                // 避免重试风暴把配额彻底耗光。缩缸只缩不放，保证任务能跑完。
                var gate = new SemaphoreSlim(workerCount);
                int throttleHits = 0;
                object gateLock = new object();
                // 软并发目标（取代原来的 gateLimit + "偷信号量票"）：
                // 详见 OnThrottled 的注释。由 worker 在"领片前"自查执行，不碰信号量。
                int concurrencyTarget = workerCount;
                // 正在持有分片的 worker 数（软闸门据此判断"在飞是否已达目标"）。
                int activeWorkers = 0;
                // 收窄下限：最多收窄到这里（避免把并发压到个位数反而更慢）。
                int throttleFloor = Math.Min(32, workerCount);
                // 最近一次被限流的时刻（TickCount）。用于"限流平息后把并发放回去"。
                long lastThrottleMs = Environment.TickCount;
                // 连续多久没再被限流就往上加 1 路。取得比较长（30 秒 ≈ 6 个采样轮）
                // 是有意的：放回去太急会立刻又被限流，来回抖比一直低并发更糟。
                const int GateRecoverQuietMs = 30000;

                // 一波风控会同时打回很多个 429/503（几百路并发下
                // 尤其明显）。若每个 429 都收窄一次，闸门会在几百毫秒内从满档坍塌到下限
                // ——fork 用故障注入实测：64 路在 0.35 秒内 64→44→30→…→4，之后整段下载
                // 只有 4 路（同一个文件 2.8 秒变 10.3 秒）。
                // 这里把"收窄动作"限速为最多每 15 秒一次；**收窄力度仍是 -30%**，
                // 刻意不学它的小步单次 —— 256 路下收得太小会在真风控时保持高位空转吃 429。
                // （"最近限流时刻"照常每次刷新，所以回升逻辑不会被去抖影响。）
                const int ThrottleNarrowDebounceMs = 15000;
                long lastNarrowMs = 0;

                // ---- 限流平息 → 把并发放回去 ----
                // 「为什么文件下到最后越来越慢」。
                // 原来 OnThrottled() 是**只缩不放**的：每次被 429/503 砍掉 30%，
                // 最低砍到 4 路，然后**整个任务余下时间就一直是 4 路** ——
                // 于是"前段飞快、后段龟速"，看起来就是越下越慢且再也快不起来。
                // 缩缸本身是对的（避免重试风暴），但缺了对称的那一半，这里补上。
                // （必须单独起任务：PreemptSlowChunks 定义在 gate 声明之前，作用域拿不到。）
                var gateRecoverTask = Task.Run(async () =>
                {
                    while (!cancelToken.IsCancellationRequested)
                    {
                        try { await Task.Delay(GateRecoverQuietMs, cancelToken).ConfigureAwait(false); }
                        catch { return; }

                        lock (gateLock)
                        {
                            int cur = Volatile.Read(ref concurrencyTarget);
                            if (cur < workerCount
                                && Environment.TickCount - lastThrottleMs >= GateRecoverQuietMs)
                            {
                                // 恢复步长与收窄对称：收窄一步 -30%，
                                // 恢复用 +workerCount/10（至少 +1）。旧实现是 +1 路 / 30 秒 ——
                                // 一旦被收窄到 4 路，爬回 512 要 2.1 小时，收放严重不对称。
                                int step = Math.Max(1, workerCount / 10);
                                int next = Math.Min(workerCount, cur + step);
                                Volatile.Write(ref concurrencyTarget, next);
                                lastThrottleMs = Environment.TickCount;   // 重新计时
                                Log?.Invoke("限流已平息，下载并发回升到 " + next
                                    + " 路（上限 " + workerCount + " 路）。");
                            }
                        }
                    }
                }, cancelToken);

                void OnThrottled()
                {
                    lock (gateLock)
                    {
                        throttleHits++;
                        long nowThrottleMs = Environment.TickCount;
                        lastThrottleMs = nowThrottleMs;
                        // 15 秒窗口内只收窄一次：一波风控只降一档，
                        // 而不是被几百个 429 各降一档、直接砸到下限。
                        if (lastNarrowMs != 0 && nowThrottleMs - lastNarrowMs < ThrottleNarrowDebounceMs)
                            return;
                        lastNarrowMs = nowThrottleMs;
                        // 旧实现有两个硬伤：
                        //   ① 稳态下 workerCount 张票**全在 worker 手里**，gate.Wait(0)
                        //      一张也偷不到 ⇒ "收窄"是假的、日志在骗人
                        //      （实测：日志说"收窄到 179 路"，实际并发一点没变）；
                        //   ② 偶尔偷到的票**不还**，而恢复只有 +1 路/30 秒
                        //      ⇒ 票泄漏不可逆，长期反而把并发越压越低。
                        // 现在只改一个软目标，由 worker 在**领片前**自查执行
                        // （见 worker 循环里的"软并发闸门"）：不碰信号量、不持锁阻塞、
                        // 收放完全对称；而且只影响"新开连接"，**不掐断正在跑的连接**。
                        int cur = Volatile.Read(ref concurrencyTarget);
                        int next = Math.Max(throttleFloor, (int)(cur * 0.7));   // 每次砍 30%
                        if (next >= cur)
                            return;
                        Volatile.Write(ref concurrencyTarget, next);
                        Log?.Invoke(string.Format(
                            "检测到服务端限流（第 {0} 次），下载并发自动收窄到 {1} 路以避免触发更严格的风控。",
                            throttleHits, next));
                    }
                }

                var workers = new Task[workerCount];
                // 分批开连接的闸门（Gopeed slowStartController 思路）。
                // 见 SlowStartGate 的注释：光靠时间错峰不保证前一批已建好连接。
                var slowStartGate = new SlowStartGate(workerCount);
                for (int w = 0; w < workerCount; w++)
                {
                    int workerNo = w;
                    workers[w] = Task.Run(async () =>
                    {
                        // 读缓冲按实际并发反推，把总内存钉在一个可接受的范围（约 64MB）。
                        // 并发越高，单条连接分到的缓冲越小——因为此时吞吐的瓶颈在
                        // 网络和磁盘的并发调度，而不是单连接的窗口大小。
                        int bufSize = Math.Max(64 * 1024,
                            Math.Min(QuarkConstants.DownloadChunk, 64 * 1024 * 1024 / Math.Max(1, workerCount)));
                        var buf = new byte[bufSize];

                        // 本 worker 是否已经拿到过首个响应。
                        // 拿到就意味着 TCP+TLS 握手完成 —— 那一刻通知分批闸门放行下一批。
                        bool firstResponseSeen = false;

                        // 冷启动错峰：多个 worker 同时发起首请求会挤在一起做 TLS 握手，
                        // 不仅拖慢首个字节时间，还更容易触发服务端限流。
                        // 这里按"占 workerCount 的比例"把一个固定总时长摊开，
                        // 而不是固定每路递增多少毫秒：后者在几百路时会让后半数 worker
                        // 全被夹到上限，反而聚成一堆同时爆发，等于没错峰。
                        // 总时长随并发量级走：512 路若仍压进 3 秒，等于每秒发起 170 次
                        // 握手，仍然偏猛；延长到 6 秒让连接建立更平滑。
                        if (workerNo > 0)
                        {
                            double rampMs = workerCount > 256 ? 6000.0 : 3000.0;
                            double staggerMs = rampMs * workerNo / Math.Max(1, workerCount);
                            try
                            {
                                await Task.Delay(TimeSpan.FromMilliseconds(staggerMs), cancelToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // 取消必须按"已取消"记账再退出，否则异常穿透
                                // Task.WhenAll 直达调用方，「已取消」上报与残留清理全部走不到。
                                Interlocked.Exchange(ref cancelled, 1);
                                if (cancel.IsCancellationRequested)
                                    Interlocked.Exchange(ref userCancelled, 1);
                                return;
                            }
                        }

                        // 分批闸门：等前一批有连接**真正就绪**（首次响应成功）再开跑。
                        // 与上面的时间错峰互补 —— 错峰管"发起时间不同"，闸门管"前一批已建好连接"。
                        try
                        {
                            await slowStartGate.WaitTurnAsync(workerNo, cancelToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // 同上：取消按"已取消"记账再退出（挂死场景下 worker 会卡在这道
                            // 分批闸门上等兜底，此时取消必须走记账路径而不是异常穿透）。
                            Interlocked.Exchange(ref cancelled, 1);
                            if (cancel.IsCancellationRequested)
                                Interlocked.Exchange(ref userCancelled, 1);
                            return;
                        }

                        while (true)
                        {
                            // 放在**每一次领片之前**（不是循环之外）：
                            // 否则暂停那一刻正处于"两片之间"的 worker 还会再领一片、再建一条
                            // 连接，然后才在读闸门处卡住 —— 白占连接（诊断面板「活跃连接」
                            // 暂停期不降就是这个），恢复时还会一次性撞服务端并发风控。
                            if (pause != null)
                            {
                                try
                                {
                                    await pause.WaitIfPausedAsync(cancelToken).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    // 暂停期间用户点了取消：必须按"已取消"记账再退出，
                                    // 否则调度器会把没下完的任务当「成功」上报。
                                    Interlocked.Exchange(ref cancelled, 1);
                                    if (cancel.IsCancellationRequested)
                                        Interlocked.Exchange(ref userCancelled, 1);
                                    return;
                                }
                            }
                            // 软并发闸门：只在"被限流收窄过"（目标 < 上限）时
                            // 才可能生效 —— 未收窄时目标 == 上限，条件恒为 false，零行为变化。
                            // 不抢信号量票、不持锁：在飞已达目标就等 100ms 再看，
                            // 等有 worker 干完自然腾位。收窄 = "少开新连接"，**不是掐老连接**，
                            // 所以正在下的数据一点不浪费，速度曲线也不会出现锯齿。
                            int softTarget = Volatile.Read(ref concurrencyTarget);
                            if (softTarget < workerCount
                                && Volatile.Read(ref activeWorkers) >= softTarget)
                            {
                                if (queue.Count == 0)
                                    break;      // 没活干了，正常退出（与 TryDequeue 失败同义）
                                try
                                {
                                    await Task.Delay(100, cancelToken).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    // 与上面的暂停闸门逐字一致：取消期间必须按"已取消"记账再退出，
                                    // 否则调度器会把没下完的任务当"成功"上报。
                                    Interlocked.Exchange(ref cancelled, 1);
                                    if (cancel.IsCancellationRequested)
                                        Interlocked.Exchange(ref userCancelled, 1);
                                    return;
                                }
                                continue;       // 回循环头，重新查暂停与目标
                            }
                            if (!queue.TryDequeue(out int idx))
                                break;
                            try
                            {
                                await gate.WaitAsync(cancelToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // 与 2827/2802 逐字同款：取消必须按"已取消"记账
                                // 再退出，否则 TaskCanceledException 穿透 Task.WhenAll（3191）
                                // 直达调用方 ——「已取消」上报与 ClearPartial 残留清理全部走不到
                                // （D1b 端到端实测复现：T+30 假死抢救回队过闸时取消已置）。
                                // 注：WaitAsync 抛出即未取到票，无需 Release。
                                Interlocked.Exchange(ref cancelled, 1);
                                if (cancel.IsCancellationRequested)
                                    Interlocked.Exchange(ref userCancelled, 1);
                                return;
                            }
                            bool gateTaken = true;
                            Interlocked.Increment(ref activeWorkers);
                            // 片级取消源：抢占只掐这一片，不牵连全局取消语义。
                            CancellationTokenSource pcts = null;
                            try
                            {
                                var part = plan.Parts[idx];

                                // 动态窃取（移植 axel 的 `reactivate_connection` /
                                // Gopeed 的 steal 思路）。
                                // 尾部塌缩 —— 最后几片落到慢节点时，只有几条连接在磨，
                                // 总速度必然崩。做法是把大片**切一半放回队列**，让更多连接参与。
                                // 此刻 worker **刚领到片、还没开始下** ——
                                // 天然无竞态（下面算 segSize 用的就是切完的新边界）。
                                // ⚠️ 刻意**不掐断正在下载的连接**：那种做法要处理
                                //    「改边界 vs worker 正读 part.E」的竞态，还要防止被掐的 worker
                                //    把已切过的片重复入队 —— 复杂度高得多，收益却一样。
                                // "待领的片"少于并发数的一半 —— 说明队列快见底、
                                // 再不动手就要进入"只有几条连接在磨"的尾部了。
                                if (queue.Count < workerCount / 2 && plan.Parts.Count < partCapacity)
                                {
                                    long remainHere = (part.E - part.S + 1) - part.W;
                                    if (remainHere >= 2 * MinStealBytes)
                                    {
                                        long mid = part.S + part.W + remainHere / 2;
                                        long oldEnd = part.E;
                                        part.E = mid - 1;          // 自己留前半
                                        lock (splitLock)
                                        {
                                            plan.Parts.Add(new PartPlan { S = mid, E = oldEnd, W = 0 });
                                            queue.Enqueue(plan.Parts.Count - 1);
                                        }
                                        Interlocked.Increment(ref splitCount);
                                    }
                                }

                                long segSize = part.E - part.S + 1;
                                if (part.W >= segSize)
                                {
                                    // 该分片在磁盘上已经完整（续传时很常见）。
                                    // 这里必须补一次完成标记：合并线程要等到它才敢删分片文件，
                                    // 漏掉就是双方永久互相等待（实测到的死锁）。
                                    Volatile.Write(ref partDone[idx], 1);
                                    continue;
                                }

                                // ---- 登记本片，供看门狗采样瞬时速度 ----
                                pcts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
                                Volatile.Write(ref wCts[workerNo], pcts);
                                Volatile.Write(ref wCurPart[workerNo], idx);
                                Volatile.Write(ref wStart[workerNo], NowMs());
                                Volatile.Write(ref wLastTick[workerNo], NowMs());
                                Volatile.Write(ref wLastW[workerNo], part.W);
                                Volatile.Write(ref wLastIdx[workerNo], idx);

                                // 单个分片独立重试：网络抖动不应该让整个任务失败。
                                // 指数退避 + 抖动，最多 MaxSegmentRetries 次。
                                string lastErr = null;
                                // 该错误是否可恢复（决定这一片能否回队重试）。
                                // 必须在**每一个** lastErr 赋值点同步刷新，否则会拿上一次
                                // 尝试的性质去判断这一次（例如上次超时、这次 404 → 误回队）。
                                bool lastErrTransient = false;
                                // 片内所有 I/O 走 partToken：看门狗抢占时只掐这一片，
                                // 不影响其他 worker，也不污染全局取消语义。
                                var partToken = pcts.Token;
                                for (int attempt = 0; attempt <= MaxSegmentRetries; attempt++)
                                {
                                    if (attempt > 0)
                                    {
                                        double delayMs = 500.0 * Math.Pow(2.0, attempt - 1);
                                        delayMs *= 0.6 + Rng.NextDouble() * 0.8;   // 抖动
                                        try
                                        {
                                            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), partToken).ConfigureAwait(false);
                                        }
                                        catch (OperationCanceledException)
                                            when (partToken.IsCancellationRequested && !cancelToken.IsCancellationRequested)
                                        {
                                            // 退避等待期间被「慢连接抢占」——
                                            // 必须与下面 I/O 路径那个 catch 同样处理：这一片回队列、
                                            // 由别的 worker 接着下，**不计失败、不退避**（照云析的设计保证）。
                                            // 🚨 原来这里，抢占会被当成"任务中断"（cancelled=1）
                                            //    → **整个任务被判失败**。而收尾阶段恰恰是最容易撞上的：
                                            //      ① endgame 时抢占"放宽年龄与剩余门槛"，触发更频繁；
                                            //      ② 尾部在飞的就是反复重试的慢片；
                                            //      ③ 重试越多 → 越可能正处于退避等待中。
                                            //    三者叠加 → 用户反馈的「下载到最后就停」（停在 99%）。
                                            queue.Enqueue(idx);
                                            Interlocked.Increment(ref preemptedCount);
                                            // 这片是"被抢占"不是"失败"：lastErr 是整片循环共享的局部变量，
                                            // 不清掉的话循环外那句 `if (lastErr != null) AddError(...)`
                                            // 会把已经过期的错误记成终态。
                                            lastErr = null;
                                            break;   // 跳出 attempt 循环，回去领下一片
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            Interlocked.Exchange(ref cancelled, 1);
                                            // 只有外部令牌被取消才算"用户取消"；
                                            // 看门狗取消不该清掉半成品（它另有 stalledFlag 分支）
                                            if (cancel.IsCancellationRequested)
                                                Interlocked.Exchange(ref userCancelled, 1);
                                            return;
                                        }
                                    }

                                    long written = part.W;
                                    if (written >= segSize)
                                        break;
                                    long reqStart = part.S + written;

                                    var h = new Dictionary<string, string>(headers)
                                    {
                                        ["Range"] = "bytes=" + reqStart + "-" + part.E,
                                    };

                                    bool fatal = false;   // true 表示重试无意义（取消/服务器不支持 Range）
                                    try
                                    {
                                        using (var req = BuildRequest(HttpMethod.Get, url, h))
                                        using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, partToken).ConfigureAwait(false))
                                        {
                                            // 登记在飞响应：抢救线程需要
                                            // Dispose 它才能真正断掉 socket（net48 下 Cancel 令牌
                                            // 唤不醒卡在内容流读取里的 worker）。
                                            Volatile.Write(ref wResp[workerNo], resp);
                                            // 首个响应到达 = TCP+TLS 握手已完成
                                            // → 通知分批闸门，放行下一批 worker。
                                            if (!firstResponseSeen)
                                            {
                                                firstResponseSeen = true;
                                                slowStartGate.MarkReady();
                                            }
                                            int code = (int)resp.StatusCode;
                                            if (code == 200 && !(reqStart == 0 && part.E >= total - 1))
                                            {
                                                // 服务器忽略 Range（且不是整文件单段）→ 交给单线程处理
                                                Interlocked.Exchange(ref cancelled, 1);
                                                AddError("range-not-supported");
                                                return;                                            }
                                            if (code != 200 && code != 206)
                                            {
                                                // 5xx / 429 值得重试，其余 4xx 重试无意义
                                                lastErr = "HTTP " + code + " " + resp.ReasonPhrase;
                                                lastErrTransient = IsTransientStatus(code);   // 429/503/5xx 可回队重试
                                                if (code == 429 || code == 503)
                                                    OnThrottled();   // 服务端明确限流：收窄并发闸门
                                                fatal = !IsTransientStatus(code);
                                                if (fatal) break;
                                                continue;
                                            }

                                            using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                            {
                                                // 每次尝试独立开流，禁用托管写缓冲（bufferSize=1）：
                                                // WriteAsync 成功才可推进 W，不能把仅进入缓冲的字节发布给合并线程。
                                                FileStream wfs;
                                                // 独立分片：写到自己的分片文件，从当前已写长度续写。
                                                // 注意 reqStart 是"在整个文件里的偏移"，
                                                // 而分片文件里只需从尾部追加 —— 两者语义不同，别混。
                                                // FileOptions.Asynchronous：**必须加**。
                                                // 不加的话，.NET Framework 的 FileStream.WriteAsync
                                                // 会退化成"在线程池上同步写" —— 每个 worker 写盘时
                                                // 真的占住一个线程池线程，512 并发就要 512 个线程，
                                                // 于是 NetworkConfig 里不得不 SetMinThreads(512)，
                                                // 上下文切换 + GC 暂停被放大 → CPU 飙高。
                                                // 加了之后走 overlapped IO，写盘不再占线程。
                                                wfs = new FileStream(PartPath(partsDir, idx), FileMode.OpenOrCreate,
                                                          FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1,
                                                          FileOptions.SequentialScan | FileOptions.Asynchronous);

                                                using (wfs)
                                                {
                                                    // 每次重试也要对账：失败/取消的异步写可能已经扩长文件，
                                                    // 但没有推进 W。只能从已提交偏移覆盖，不能追加到物理尾部。
                                                    wfs.SetLength(written);
                                                    wfs.Seek(written, SeekOrigin.Begin);
                                                    Interlocked.Increment(ref activeConns);
                                                    try
                                                    {
                                                        while (true)
                                                        {
                                                            cancelToken.ThrowIfCancellationRequested();
                                                            if (pause != null)
                                                                await pause.WaitIfPausedAsync(cancelToken).ConfigureAwait(false);
                                            int n = await stream.ReadAsync(buf, 0, buf.Length, partToken).ConfigureAwait(false);
                                            if (n <= 0)
                                                break;
                                            // 暂停必须在**写盘前**再查一次：
                                            // net48 下内容流读取不响应取消令牌，暂停那一刻在途的读
                                            // 会带着数据正常返回 —— 若这里不拦，就照样写盘、done 照样涨
                                            // （用户看到的"点了暂停、进度条还在涨"就是这么来的）。
                                            // 已读到的数据留在 buf 里，恢复后原样写入：不丢、不重复。
                                            if (pause != null)
                                                await pause.WaitIfPausedAsync(cancelToken).ConfigureAwait(false);
                                            await wfs.WriteAsync(buf, 0, n, partToken).ConfigureAwait(false);
                                                            written += n;
                                                            part.W = written;
                                                            Interlocked.Add(ref done, n);
                                                        }
                                                    }
                                                    finally
                                                    {
                                                        Interlocked.Decrement(ref activeConns);
                                                    }
                                                }

                                                if (written < segSize)
                                                {
                                                    // 连接被中途掐断：已写入的部分保留（part.W 已更新），
                                                    // 下一轮重试会从断点继续，不会重复下载。
                                                    lastErr = "分片不完整 " + written + "/" + segSize;
                                                    lastErrTransient = true;   // 连接被掐断：可回队
                                                    continue;
                                                }
                                            }

                                            // 本分片完整落盘、且文件流已关闭 → 通知合并线程可以取走了。
                                            // 必须在 using 之外置位：提前置位会让合并线程
                                            // 在文件还没 flush/关闭时就去读，拿到残缺数据。
                                            Volatile.Write(ref partDone[idx], 1);

                                            lastErr = null;
                                            break;   // 本分片完成
                                        }
                                    }
                                    // 先判抢占，再判真取消。
                                    // 抢占用的是 partToken（片级），全局 cancelToken 并未取消；
                                    // 若顺序颠倒或只用一个 catch，抢占就会被误当成用户取消 ——
                                    // 那会把整个任务的进度清掉（ClearPartial），是灾难性的。
                                    // 抢占的处理照云析：已写字节全部保留在 part.W，
                                    // 分片重新入队，**不计失败、不退避**，下一轮从断点续。
                                    catch (OperationCanceledException)
                                        when (partToken.IsCancellationRequested && !cancelToken.IsCancellationRequested)
                                    {
                                        queue.Enqueue(idx);
                                        Interlocked.Increment(ref preemptedCount);
                                        // ⚠️ 必须把 lastErr 清掉再跳出。
                                        // 这片被抢占后会**重新入队、由别的 worker 接着下**，
                                        // 根本不是"这片失败"。但 lastErr 是**整片循环共享**的局部变量：
                                        // 若前面某次尝试刚记过一次网络错误、紧接着这次被抢占，
                                        // 循环外那句 `if (lastErr != null) AddError(...); return;`
                                        // 就会把**已经过期的错误**记成终态 → 整个任务被判失败
                                        // （实测：用户那个 987MB 的 mp4 数据其实全下全了，
                                        //  一个空洞都没有，却因为这种残留错误报"下载失败"）。
                                        lastErr = null;
                                        break;   // 跳出 attempt 循环，回去领下一片
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        Interlocked.Exchange(ref cancelled, 1);
                                        AddError("已取消");
                                        // 同前：仅外部令牌取消才算用户取消
                                        if (cancel.IsCancellationRequested)
                                            Interlocked.Exchange(ref userCancelled, 1);
                                        return;
                                    }
                                    catch (Exception ex)
                                    {
                                        // 这次异常是抢救线程 Dispose 响应造成的：
                                        // 按"被抢占"处理 —— 已写字节保留、分片回队换连接重下，
                                        // 不计失败、不烧重试预算（否则抢救几次就把 5 次预算耗尽，
                                        // 又回到"worker 阵亡"的老路上）。
                                        // 用标记判定而不是异常类型：Dispose 抛出的可能是
                                        // ObjectDisposedException，也可能是 IOException，不确定。
                                        if (Volatile.Read(ref wHangRescued[workerNo]) != 0)
                                        {
                                            Volatile.Write(ref wHangRescued[workerNo], 0);
                                            queue.Enqueue(idx);
                                            lastErr = null;
                                            break;   // 跳出 attempt 循环，回去领下一片
                                        }
                                        lastErr = ex.GetType().Name + ": " + ex.Message;
                                        lastErrTransient = true;   // 网络层异常：可回队
                                    }
                                }

                                if (lastErr != null)
                                {
                                    // 可恢复错误不判死：把这片放回队列
                                    // （换条连接、多半换到别的 CDN 节点），worker 不退出、并发不递减。
                                    // 原行为是 AddError + return —— 一片取不到就干掉一个 worker，
                                    // 几片同时坏掉时整个任务失败（fork 故障注入实测：64 片里
                                    // 56 片连一次都没被试过，报错只有一句 HttpRequestException）。
                                    if (lastErrTransient
                                        && partRounds[idx] < MaxPartRounds
                                        && totalRequeues < MaxTotalRequeues)
                                    {
                                        partRounds[idx]++;
                                        totalRequeues++;
                                        Log?.Invoke(string.Format(
                                            "分片 {0} 整轮重试 {1}/{2}：{3}；已写 {4} 字节保留，放回队列换连接重下。",
                                            idx, partRounds[idx], MaxPartRounds, lastErr, part.W));
                                        queue.Enqueue(idx);
                                        lastErr = null;
                                        // 注意：这里**不能** break —— break 会跳出 worker 的领片循环、
                                        // 让 worker 退出（那正是原来"并发递减"的病根）。自然落到 finally
                                        // 做完片级清理后，回去领下一片即可。
                                    }
                                    else
                                    {
                                        if (totalRequeues >= MaxTotalRequeues)
                                            AddError("整轮重试预算用尽（" + MaxTotalRequeues + " 次）：" + lastErr);
                                        else
                                            AddError(lastErr);
                                        return;
                                    }
                                }
                            }
                            finally
                            {
                                // 本片结束（无论完成/失败/被抢占），都要把采样状态清掉，
                                // 否则看门狗会拿一个已失效的 worker 编号去判定。
                                Volatile.Write(ref wCts[workerNo], null);
                                Volatile.Write(ref wCurPart[workerNo], -1);
                                // 抢救线程不再持有本 worker 的响应；
                                // 同时清掉抢救标记，避免它"跨片"影响下一次真正的失败判定。
                                Volatile.Write(ref wResp[workerNo], null);
                                Volatile.Write(ref wHangRescued[workerNo], 0);
                                if (pcts != null)
                                {
                                    try { pcts.Dispose(); } catch { }
                                }
                                Interlocked.Decrement(ref activeWorkers);
                                if (gateTaken)
                                    gate.Release();
                            }
                        }
                    }, cancelToken);
                }

                try
                {
                    await Task.WhenAll(workers).ConfigureAwait(false);
                }
                finally
                {
                    // 先把"worker 已全部结束"告诉 reporter，
                    // 再排空合并线程 —— 这期间 reporter 仍在跑，UI 能看到「正在合并分片」。
                    // 原来这里是"先 stop.Cancel() 停掉 reporter，再 await mergeTask"，
                    // 于是整个收尾期（100GB 文件可能好几分钟）界面**完全冻结**在
                    // 「进度 100% + 速度 0 + 状态下载中」→ 用户判定为卡死（实测反馈）。
                    // ⚠️ 必须是**真正的收尾标志**而不是恒 true：恒 true 会让每一次
                    //    worker 收尾都被当成合并阶段，reporter 误报「正在合并分片」。
                    Volatile.Write(ref workersDoneBox[0], true);

                    // 合并线程收尾：先让它把"已经就绪"的分片排空，再退出。
                    // 直接取消而不排空，会留下一批已经下好、却白占空间的分片文件。
                    mergeStop.Cancel();
                    if (mergeTask != null)
                    {
                        try { await mergeTask.ConfigureAwait(false); } catch { }
                    }

                    // 合并排空完了，才停进度上报线程
                    stop.Cancel();
                    try { await reporter.ConfigureAwait(false); } catch { }

                    SavePlan(metaPath, plan);
                }

                if (cancelled != 0 || errors.Count > 0)
                {
                    if (cancelled != 0)
                    {
                        // 区分三种"中断"，处理方式完全不同：
                        // - 用户主动取消（userCancelled=1）：按预期清掉半成品，不留垃圾。
                        // - 看门狗判定停滞（stalledFlag=1）：环境问题，不是用户意愿。
                        //   必须保留已下载部分和 .qmeta，让用户换低线程数后能续传。
                        // - 其它（反压取消、range-not-supported 等）：环境/协议问题，
                        //   同样必须保留进度 —— 这部分以前会误删，属真实缺陷。
                        // 🚨 判断顺序：**userCancelled 必须先于 stalledFlag**。
                        // 原来的顺序是 stalledFlag 在前，导致：下载中网络卡过 45 秒
                        // （stalledFlag 一旦置位永不重置）→ 之后用户点「取消」也被
                        // 当成"停滞中止"处理 —— 残留不清、任务记失败（自带续传入局），
                        // 重开程序显示"可续传"，文案还是错的"网络停滞已中止"。
                        // 用户明确说"不要了"时，之前是否停滞过与本次取消无关，一律清干净。
                        if (userCancelled != 0)
                        {
                            ClearPartial(dest);
                            return (false, "已取消");
                        }
                        if (stalledFlag != 0)
                        {
                            return (false, "网络停滞已中止（已保留断点，降低线程数后重试可续传）");
                        }
                        // 到这里：cancelled != 0、非用户取消、非停滞 ——
                        // 其它中断（反压取消等，环境/协议问题）：保留断点，不能删。
                        {
                            // （range-not-supported 会走下面的分支单独处理，不到这里。）
                            // 和下面 errors 分支一样，**报失败前先验一次数据**：
                            // 数据其实已经全下全了、却报"失败"，用户会以为文件坏了不敢用。
                            // （10-04 那次只补了 errors 分支，漏了 cancelled 这条。）
                            bool cancelledButComplete;
                            // 目标文件是按序追加的，长度到位 ⇒ 每片都已合并进去。
                            // 走到这里合并线程已经被排空（上面的 finally），所以这个判据有效。
                            // 注意：变量名不能叫 finalLen —— 收尾校验处已有一个同名局部变量。
                            long finalLenA = -1;
                            try { finalLenA = new FileInfo(dest).Length; } catch { }
                            cancelledButComplete = finalLenA == total;
                            if (cancelledButComplete)
                            {
                                Log?.Invoke("注意: 下载中途被判中断，但数据已全部到位（"
                                    + Fmt(total) + "），按成功处理。");
                                return (true, "");
                            }
                            return (false, "下载中断（已保留断点与已下载部分，重试可续传）");
                        }
                    }
                    string msg = string.Join("; ", errors.Take(3));
                    if (msg.Contains("range-not-supported"))
                    {
                        ClearPartial(dest);
                        return await DownloadSingleAsync(url, dest, headers, total, progress, cancel, pause).ConfigureAwait(false);
                    }

                    // 「下载报失败，但文件其实是好的」——
                    // 用户那个 987.72MB 的 mp4：全盘扫描**一个空洞都没有**、分片目录已空
                    // （说明全部合并完成），可任务却被标成「失败」，于是用户以为文件坏了不敢用。
                    // 根因是"错误计数"被当成了终态判据：某个 worker 在传输中断（连接被服务端掐断）
                    // 后退避重试 4 次仍失败 → AddError → 而那片数据**后来由别的 worker
                    // 或者该片被重新入队后补齐了**，errors 里却已经留下记录。
                    // → 报失败之前先验一次**数据本身**：到位了就算成功。
                    //   完整性是硬道理，错误计数只是过程指标。
                    //   注意：走到这里说明 cancelled == 0（用户取消/停滞都在上面 return 了），
                    //   所以不存在"用户明确不要了却报成功"的问题。
                    bool dataComplete;
                    // 目标文件是**按序追加**出来的（不预分配），长度到位 ⇒ 每一片都已合并进去。
                    // 注意：变量名不能叫 finalLen —— 上面 cancelled 分支 + 收尾校验处都有同名局部变量。
                    long finalLenB = -1;
                    try { finalLenB = new FileInfo(dest).Length; } catch { }
                    dataComplete = finalLenB == total;

                    if (!dataComplete)
                        return (false, msg);

                    Log?.Invoke("注意: 下载期间出现过传输错误（" + msg
                        + "），但数据已全部到位（" + Fmt(total) + "），按成功处理。");
                }

                // ---- 收尾校验 ----
                // 必须确认目标文件真的长到了 total。
                // 差一个字节都不能算成功 —— 否则用户会拿到一个损坏的文件，
                // 界面上却显示"已完成"，比直接报错更糟。
                long finalLen = -1;
                try { finalLen = new FileInfo(dest).Length; } catch { }
                if (finalLen != total)
                {
                    return (false, string.Format(
                        "合并后目标文件大小不符：期望 {0}，实际 {1}。分片与进度已保留，可重试续传。",
                        total, finalLen));
                }

                try { File.Delete(metaPath); } catch { }
                // 分片文件在每片合并后即已删除，这里兜底清掉目录本身
                try
                {
                    if (Directory.Exists(partsDir))
                        Directory.Delete(partsDir, true);
                }
                catch (Exception ex)
                {
                    // 失败必须留痕：静默 catch 会让"残留分片"无从发现，
                    // 而残留分片 + .qmeta 已删正是"重下被污染"的温床
                    // （worker 会把新数据追加到残留分片后面，见 PartPath 续写处的截断保护）。
                    Log?.Invoke(string.Format(
                        "⚠️ 下载完成，但分片目录清理失败（{0}）：{1}。"
                        + "请手动删除该目录；残留分片不会影响本次成品，但会影响将来同目录重下。",
                        partsDir, ex.Message));
                }
                // 指定了 partsRoot 时，hash 目录是我们建的，空了也一并收掉（避免堆积空壳）
                if (!string.IsNullOrEmpty(_partsRoot))
                {
                    try
                    {
                        string baseDir = Path.GetDirectoryName(partsDirBase);
                        if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir)
                            && Directory.GetFileSystemEntries(baseDir).Length == 0)
                            Directory.Delete(baseDir, false);
                    }
                    catch { }
                }

                // ---- 本次任务的实测报告 ----
                // 光让用户调数字是没用的：他没依据知道自己网络环境的甜点区在哪。
                // 这里把本次实测到的关键指标摊出来，让"该调多少"变得有据可依。
                try
                {
                    double secs = (DateTime.UtcNow - t0).TotalSeconds;
                    double avgSpeed = secs > 0 ? total / secs : 0;
                    long segBytes = plan.Parts.Count > 0 ? plan.Parts[0].E - plan.Parts[0].S + 1 : 0;

                    Log?.Invoke(string.Format(
                        "本次实测: {0}，用时 {1:F1} 秒，平均 {2}/s；并发 {3} 路，分片 {4} 片（每片约 {5}）。",
                        Fmt(total), secs, Fmt((long)avgSpeed), workerCount, plan.Parts.Count, Fmt(segBytes)));

                    // 把"每条连接实际跑多快"算出来。这是判断瓶颈的关键：
                    // 若单连接速度正常而总速度低 → 并发没铺满；
                    // 若单连接就慢 → 是服务端限速，加并发也救不回来。
                    if (workerCount > 0)
                    {
                        double perConn = avgSpeed / workerCount;
                        Log?.Invoke(string.Format(
                            "分连接: 平均每条 {0}/s。若这个值明显偏低（如低于 200 KB/s），" +
                            "说明是夸克 CDN 在限单连接速度，此时加大并发收益有限；" +
                            "若它很健康而总速度不理想，才说明还有并发空间。",
                            Fmt((long)perConn)));
                    }

                    if (throttleHits > 0)
                    {
                        Log?.Invoke(string.Format(
                            "建议: 本次触发了 {0} 次服务端限流，并发被压到 {1} 路。" +
                            "说明 {2} 路超出了你当前网络/账号的承受范围，下次直接选更低的档位会更稳。",
                            throttleHits, Volatile.Read(ref concurrencyTarget), _threads));
                    }
                    else if (avgSpeed > 0)
                    {
                        // 单位换算成 Mbps，便于和宽带办的速度对比
                        double mbps = avgSpeed * 8 / 1000.0 / 1000.0;
                        Log?.Invoke(string.Format(
                            "建议: 本次未触发限流，平均约 {0:F0} Mbps。若这已接近你的宽带带宽，" +
                            "说明已跑满、无需再调高；若明显偏低且是外网盘，可尝试再升一档。", mbps));
                    }

                    Log?.Invoke(string.Format(
                        "边合并完成: 目标文件已按序拼装完毕（{0}），分片目录已清理，全程峰值占用约 1×。",
                        Fmt(total)));
                }
                catch { }

                Report(progress, total, total, 0);
                return (true, "");
            }   // end: using(linked)
        }

        private static bool PartsEqual(List<PartPlan> a, List<(long Start, long End)> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].S != b[i].Start || a[i].E != b[i].End)
                    return false;
            }
            return true;
        }

        private static void Report(IProgress<DownloadProgress> progress, long done, long? total, double speed,
            string phase = null)
        {
            if (progress == null)
                return;
            progress.Report(new DownloadProgress
            {
                Done = done,
                Total = total ?? 0,
                Speed = speed,
                Phase = phase,
            });
        }

        // ---------------- sidecar 续传元数据（简单文本格式，无 JSON 依赖） ----------------

        private static DownloadPlan LoadPlan(string metaPath)
        {
            try
            {
                var lines = File.ReadAllLines(metaPath);
                if (lines.Length < 2)
                    return null;
                long total = long.Parse(lines[0].Trim(), CultureInfo.InvariantCulture);
                int n = int.Parse(lines[1].Trim(), CultureInfo.InvariantCulture);
                if (lines.Length < 2 + n)
                    return null;
                var plan = new DownloadPlan { Total = total, Parts = new List<PartPlan>() };
                for (int i = 0; i < n; i++)
                {
                    var cols = lines[2 + i].Split(',');
                    if (cols.Length != 3)
                        return null;
                    plan.Parts.Add(new PartPlan
                    {
                        S = long.Parse(cols[0].Trim(), CultureInfo.InvariantCulture),
                        E = long.Parse(cols[1].Trim(), CultureInfo.InvariantCulture),
                        W = long.Parse(cols[2].Trim(), CultureInfo.InvariantCulture),
                    });
                }
                return plan;
            }
            catch
            {
                return null;
            }
        }

        private static void SavePlan(string metaPath, DownloadPlan plan)
        {
            // 原子写：先落 .tmp，再整体替换。
            // 这个文件每秒重写一次、且在下载中被多线程读取判断续传，
            // 直接 File.WriteAllText 有概率被读到"写了一半"的残缺内容，
            // 一旦解析失败就会丢掉已下载进度、从零开始。
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(plan.Total.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append(plan.Parts.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
                foreach (var p in plan.Parts)
                {
                    sb.Append(p.S.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(p.E.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(p.W.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
                string tmp = metaPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                // File.Move 的目标已存在时会抛异常，所以原先是"先 Delete 再 Move"——
                // 但这两步之间有窗口：若在 Delete 之后、Move 之前断电/进程被杀，
                // 就变成既没有 .qmeta 也没有可用的 .tmp，LoadPlan 返回 null，
                // 下载器会把整个任务当成全新的、从零开始 —— 正是本方法要防的事。
                // 正确做法是一步替换。.NET Framework 的 File.Move 不支持 overwrite 重载，
                // 但 File.Replace 语义是"用 tmp 替换 metaPath"，且是原子的，
                // 还顺带处理目标不存在的情况（不存在时抛错，故先判断）。
                if (File.Exists(metaPath))
                {
                    // File.Replace 要求三者同卷；tmp 与 metaPath 同目录，必然满足。
                    // 备份文件参数传 null，不额外产生垃圾。
                    File.Replace(tmp, metaPath, null);
                }
                else
                {
                    File.Move(tmp, metaPath);
                }
            }
            catch
            {
                // 落盘失败不影响下载本身
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    internal class PartPlan
    {
        private long _written;
        public long S { get; set; }   // 起始（含）
        public long E { get; set; }   // 结束（含）
        // 跨 worker、合并线程和元数据快照发布的已提交前缀。
        public long W
        {
            get => Interlocked.Read(ref _written);
            set => Interlocked.Exchange(ref _written, value);
        }
    }

    internal class DownloadPlan
    {
        public long Total { get; set; }
        public List<PartPlan> Parts { get; set; }
    }

    /// <summary>
    /// 一次下载的**诊断快照** —— 供 UI 的诊断面板消费（结构化数据，不需要解析日志文本）。
    /// <para>这些指标合起来回答"速度上不去到底卡在哪"：</para>
    /// <list type="bullet">
    /// <item>活跃连接数接近上限但速度低 → 服务端限速（客户端无解）；</item>
    /// <item>活跃连接数明显偏低 → 客户端侧没把连接发出去（可优化）；</item>
    /// <item>对端 IP 只有 1 个 → DNS 没分散，慢连接抢占重连也还是同节点；</item>
    /// <item>已抢占次数偏高 → 存在长尾慢连接，抢占机制正在起作用。</item>
    /// </list>
    /// </summary>
    public class DiagnosticsSnapshot
    {
        /// <summary>任务名（文件名）。</summary>
        public string Name { get; set; }
        /// <summary>
        /// 目标文件**完整路径**。
        /// <para>为什么必须单独带一个：UI 侧要拿它把快照**精确**对到某个任务行上。
        /// 用 <see cref="Name"/> 对是错的 —— 那个名字经过 <c>PathUtil.SanitizeFileName</c>
        /// 与 <c>PathUtil.UniquePath</c> 加工，和网盘上的原始文件名（= <c>TaskItem.Name</c>）
        /// 经常不相等：同名文件已存在时会加 <c> (1)</c> 后缀（实测：用户 32 条历史里有 2 条如此），
        /// 含 <c>\ / : * ? " &lt; &gt; |</c> 的会被替换成 <c>_</c>。对不上 ⇒ 车道永远不亮。</para>
        /// <para>完整路径在"同时下载的任务"之间天然唯一（<c>DownloadScheduler</c> 的同路径互斥
        /// 也是拿它当 key），所以做匹配键最合适。</para>
        /// </summary>
        public string Dest { get; set; }
        /// <summary>即时速度（字节/秒，EWMA 平滑值）。</summary>
        public double Speed { get; set; }
        /// <summary>当前活跃连接数。</summary>
        public int ActiveConnections { get; set; }
        /// <summary>本次任务实际启用的并发上限。</summary>
        public int WorkerCount { get; set; }
        /// <summary>已完成分片数。</summary>
        public int PartsDone { get; set; }
        /// <summary>分片总数。</summary>
        public int PartsTotal { get; set; }
        /// <summary>累计已写的字节数。</summary>
        public long DoneBytes { get; set; }
        /// <summary>文件总大小（0 = 未知）。</summary>
        public long TotalBytes { get; set; }
        /// <summary>累计抢占次数（慢连接被掐掉的次数）。</summary>
        public int PreemptedCount { get; set; }
        /// <summary>是否处于「边下边播」的合并模式。</summary>
        public bool MergeMode { get; set; }
        /// <summary>合并模式下已按序合并的字节数。</summary>
        public long MergedBytes { get; set; }
        /// <summary>对端 IP 分布描述（"1.2.3.4×120, 5.6.7.8×3"，空串 = 无样本）。</summary>
        public string PeerIps { get; set; }
        /// <summary>是否已判定为停滞。</summary>
        public bool Stalled { get; set; }
        /// <summary>快照时间。</summary>
        public DateTime Timestamp { get; set; }
    }
}

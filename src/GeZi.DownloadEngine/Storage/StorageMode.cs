// GeZi 鸽子下载器 — 多网盘下载器
// Copyright (C) 2026  GeZi contributors
//
// 本程序是自由软件：你可以再分发和/或修改它，依据 GNU Affero 通用公共许可证
// 第 3 版（或更新版本）的条款，由自由软件基金会发布。
// 本程序基于「希望它有用」的目的分发，但不提供任何担保。
// 详见 <https://www.gnu.org/licenses/>。

namespace GeZi.DownloadEngine.Storage;

/// <summary>落盘策略选择。</summary>
public enum StorageMode
{
    /// <summary>
    /// 顺序落盘（<see cref="SequentialFileStorage"/>）—— <b>默认</b>。
    /// 乱序数据先重排再严格顺序写，目标文件从 0 连续增长。
    /// 机械盘与固态盘都友好，可边下边看，磁盘占用 1×、I/O 量 1×。
    /// 代价是 32 MB 内存窗口（可配）。
    /// </summary>
    Sequential = 0,

    /// <summary>
    /// 共写落盘（<see cref="SharedFileStorage"/>，bezzad 原生行为）。
    /// 多连接直接随机写目标文件。内存占用最低、实现最简单，
    /// 但机械盘会寻道风暴、不能边下边看、预分配+随机写有「长度对内容坏」风险。
    /// 仅建议在固态盘且不在意边下边看时选用。
    /// </summary>
    Shared = 1,
}

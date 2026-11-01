using GeZi.DownloadEngine.Planning;

namespace GeZi.DownloadEngine.Framework;

/// <summary>
/// 分片规划的执行者：把「怎么切」交给 <see cref="IChunkPlanner"/>，
/// 自己只负责把结果塞进 <see cref="DownloadPackage.Chunks"/>。
///
/// 改造前这里是写死的静态等分（片数 = ChunkCount，余数塞最后一片）。
/// 现在默认仍是 <see cref="StaticChunkPlanner"/>（向后兼容），
/// 网盘下载通过 <c>DownloadConfiguration.Planner</c> 换成
/// <see cref="AdaptiveChunkPlanner"/> 以获得 8 片/连接的窃取深度。
/// </summary>
public class ChunkHub
{
    private readonly DownloadConfiguration _config;

    public ChunkHub(DownloadConfiguration config)
    {
        _config = config;
    }

    /// <summary>本次实际使用的规划器（便于日志与诊断）。</summary>
    public IChunkPlanner Planner => _config?.Planner ?? StaticChunkPlanner.Instance;

    public void SetFileChunks(DownloadPackage package)
    {
        if (package.Chunks is null)
        {
            package.Chunks = Planner.Plan(package.TotalFileSize, _config);
        }
        else
        {
            package.Validate();
        }
    }
}

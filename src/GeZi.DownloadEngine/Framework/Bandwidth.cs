using System;
using System.Threading;

namespace GeZi.DownloadEngine.Framework;

public class Bandwidth
{
    private const double OneSecond = 1000; // millisecond
    private long _count;
    private int _lastSecondCheckpoint;
    private long _lastTransferredBytesCount;
    private int _speedRetrieveTime;
    public double Speed { get; private set; }
    public double AverageSpeed { get; private set; }
    public long BandwidthLimit { get; set; }

    /// <summary>
    /// 🚨 2026-11-07：界面用的**平滑速度**（最近 5 秒滑动窗口平均）。
    /// 原 <see cref="Speed"/> 是单秒瞬时值 —— CDN 抖动时会在 0 B/s 与 10 MB/s 之间跳，
    /// 用户感知「一会儿快一会儿慢」。参照 refs/gopeed model.go 的 calcSpeed（5 秒窗口）。
    /// ⚠️ 原 Speed / AverageSpeed 语义一字不改（限速判断与内部逻辑仍用原值），
    ///    只是新增一个供界面展示的平滑值，默认等价、零行为风险。
    /// </summary>
    private readonly double[] _speedWindow = new double[5];
    private int _speedWindowCount;
    private int _speedWindowIndex;
    public double SmoothedSpeed { get; private set; }

    public Bandwidth()
    {
        BandwidthLimit = long.MaxValue;
        Reset();
    }

    /// <summary>把一个 1 秒采样喂进滑动窗口，重算平滑值。</summary>
    private void PushSpeedSample(double sample)
    {
        _speedWindow[_speedWindowIndex] = sample;
        _speedWindowIndex = (_speedWindowIndex + 1) % _speedWindow.Length;
        if (_speedWindowCount < _speedWindow.Length) _speedWindowCount++;
        double sum = 0;
        for (int i = 0; i < _speedWindowCount; i++) sum += _speedWindow[i];
        SmoothedSpeed = _speedWindowCount > 0 ? sum / _speedWindowCount : 0;
    }

    public void CalculateSpeed(long receivedBytesCount)
    {
        // 无符号差值：Environment.TickCount 是 int，约 24.9 天回绕成负数。
        // 直接相减在跨越回绕点时会得到负的 elapsedTime → 速度算成负数或天文数字，
        // 限速判断和界面速度会同时错乱（跑一周以上的长时间下载必踩）。
        int elapsedTime = unchecked((int)((uint)Environment.TickCount - (uint)_lastSecondCheckpoint)) + 1;
        receivedBytesCount = Interlocked.Add(ref _lastTransferredBytesCount, receivedBytesCount);
        double momentSpeed = receivedBytesCount * OneSecond / elapsedTime; // B/s

        if (OneSecond < elapsedTime)
        {
            Speed = momentSpeed;
            AverageSpeed = ((AverageSpeed * _count) + Speed) / (_count + 1);
            _count++;
            // 🚨 2026-11-07：每个 1 秒检查点喂一次滑动窗口（5 秒平均），
            //    界面改读 SmoothedSpeed，避免瞬时值剧烈跳动。
            PushSpeedSample(momentSpeed);
            SecondCheckpoint();
        }

        if (momentSpeed >= BandwidthLimit)
        {
            var expectedTime = receivedBytesCount * OneSecond / BandwidthLimit;
            Interlocked.Add(ref _speedRetrieveTime, (int)expectedTime - elapsedTime);
        }
    }

    public int PopSpeedRetrieveTime()
    {
        return Interlocked.Exchange(ref _speedRetrieveTime, 0);
    }

    public void Reset()
    {
        SecondCheckpoint();
        _count = 0;
        Speed = 0;
        AverageSpeed = 0;
        for (int i = 0; i < _speedWindow.Length; i++) _speedWindow[i] = 0;
        _speedWindowCount = 0;
        _speedWindowIndex = 0;
        SmoothedSpeed = 0;
    }

    private void SecondCheckpoint()
    {
        Interlocked.Exchange(ref _lastSecondCheckpoint, Environment.TickCount);
        Interlocked.Exchange(ref _lastTransferredBytesCount, 0);
    }
}

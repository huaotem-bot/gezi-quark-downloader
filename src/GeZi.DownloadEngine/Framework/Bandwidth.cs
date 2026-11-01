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

    public Bandwidth()
    {
        BandwidthLimit = long.MaxValue;
        Reset();
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
    }

    private void SecondCheckpoint()
    {
        Interlocked.Exchange(ref _lastSecondCheckpoint, Environment.TickCount);
        Interlocked.Exchange(ref _lastTransferredBytesCount, 0);
    }
}

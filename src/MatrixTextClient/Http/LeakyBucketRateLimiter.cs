using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MatrixTextClient.Http;
/// <summary>
/// Since matrix usually has a rate limit of 600 requests per hour, we need to implement a rate limiter to prevent
/// the client from making too many requests.
/// </summary>
/// <remarks>
/// This rate limiter is based on the leaky bucket metaphor. It allows for a burst of requests up to a certain
/// capacity, and then limits the rate of requests to a certain number per hour. Imagine a leaky bucket with requests
/// leaking out at a constant rate. When it is empty, no more requests can be made until it is refilled.
/// The implementation is pessimistic, meaning that it will allow less leaks than the desired maximum, to make sure
/// we never exceed the maximum rate.
/// </remarks>
public class LeakyBucketRateLimiter
{
    public int Capacity { get; }
    public int MaxLeaksPerHour { get; }
    public int WaterLevel { get; private set; }

    private readonly long _ticksPerRestore; // ticks to restore one leak

    private object _lock = new object();
    private long _lastRestoreTicks;

    public LeakyBucketRateLimiter(int capacity = 10, int maxLeaksPerHour = 600)
    {
        if(capacity > (maxLeaksPerHour / 2))
            throw new ArgumentException("capacity must be less than half of maxLeaksPerHour to make this reliable.");
        if(capacity < 0 || maxLeaksPerHour <= 5)
            throw new ArgumentException("capacity must be greater than 0 and maxLeaksPerHour must be greater than 5.");

        Capacity = capacity;
        MaxLeaksPerHour = maxLeaksPerHour;
        WaterLevel = capacity;

        var ticksPerSecond = Stopwatch.Frequency; // Use stopwatch, it's more accurate than DateTime
        var ticksPerHour = ticksPerSecond * 60 * 60; // Avoid using TimeSpan, its tick frequency may be different
        var currentTicks = Stopwatch.GetTimestamp();
        _lastRestoreTicks = currentTicks;
        _ticksPerRestore = ticksPerHour / (maxLeaksPerHour - capacity); // subtract capacity to make sure we never exceed maxRequestsPerHour even if the bucket is full
    }

    public bool Leak()
    {
        lock (_lock) // using lock is not the fastest, but at the rate we send requests, it does not matter
        {
            var currentTicks = Stopwatch.GetTimestamp();
            var elapsedTicks = currentTicks - _lastRestoreTicks;
            var restored = (int)(elapsedTicks / _ticksPerRestore);
            if(restored > 0)
            {
                _lastRestoreTicks = currentTicks; // We may lose some ticks here, but it's less of a problem than _lastRestoreTicks never catching up
                WaterLevel = Math.Min(WaterLevel + restored, Capacity);
            }

            if(WaterLevel > 0)
            {
                WaterLevel--;
                return true;
            }

            return false;
        }
    }
}
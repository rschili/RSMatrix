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
    public int WaterLevel => _waterLevel;
    private int _waterLevel;

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
        _waterLevel = capacity;

        var ticksPerSecond = Stopwatch.Frequency; // Use stopwatch, it's more accurate than DateTime
        var ticksPerHour = ticksPerSecond * 60 * 60; // Avoid using TimeSpan, its tick frequency may be different
        var currentTicks = Stopwatch.GetTimestamp();
        _lastRestoreTicks = currentTicks;
        _ticksPerRestore = ticksPerHour / (maxLeaksPerHour - capacity); // subtract capacity to make sure we never exceed maxRequestsPerHour even if the bucket is full
    }

    /// <summary>
    /// Attempts to leak from the bucket.
    /// </summary>
    /// <returns>True if successful, false if the bucket is empty</returns>
    public bool Leak()
    {
        if(_waterLevel <= 0)
        {
            var restoredWaterlevel = TryFillBucket();
            if(restoredWaterlevel <= 0)
                return false;
        }

        // in some race conditions _waterLevel may go negative, we chose to ignore that as the restoration rate will even this out
        Interlocked.Decrement(ref _waterLevel);
        return true;
    }

    /// <summary>
    /// Attempts to refill some of the bucket, returns the waterlevel after filling
    /// </summary>
    /// <returns>The waterlevel after the operation</returns>
    private int TryFillBucket()
    {
        lock(_lock)
        {
            var waterlevel = _waterLevel;
            if(waterlevel >= Capacity)
                return waterlevel;

            var currentTicks = Stopwatch.GetTimestamp();
            var elapsedTicks = currentTicks - Interlocked.Read(ref _lastRestoreTicks);
            var restored = (int)(elapsedTicks / _ticksPerRestore);
            if(restored == 0)
                return waterlevel;

            var newWaterLevel = Math.Min(waterlevel + restored, Capacity);
            _lastRestoreTicks = currentTicks;
            _waterLevel = newWaterLevel;
            return newWaterLevel;
        }
    }
}
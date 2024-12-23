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
/// quota, and then limits the rate of requests to a certain number per hour. Imagine a leaky bucket with requests
/// leaking out at a constant rate. When it is empty, no more requests can be made until it is refilled.
/// </remarks>
public class LeakyBucketRateLimiter
{
    private readonly int _capacity;
    private readonly long _ticksPerHour;
    private readonly long _ticksPerRestore; // ticks to restore one leak

    private readonly int _maxLeaksPerHour;

    private object _lock = new object();
    private long _lastRestoreTicks;

    private int _waterlevel;


    public LeakyBucketRateLimiter(int capacity, int maxLeaksPerHour)
    {
        if(capacity > maxLeaksPerHour)
            throw new ArgumentException("Capacity cannot be greater than maxLeaksPerHour.");
        if(capacity < 0 || maxLeaksPerHour <= 5)
            throw new ArgumentException("capacity must be greater than 0 and maxLeaksPerHour must be greater than 5.");

        _capacity = capacity;
        var ticksPerSecond = Stopwatch.Frequency; // Use stopwatch, it's more accurate than DateTime
        _ticksPerHour = ticksPerSecond * 60 * 60; // Avoid using TimeSpan, its tick frequency may be different
        var currentTicks = Stopwatch.GetTimestamp();
        _lastRestoreTicks = currentTicks;
        _ticksPerRestore = _ticksPerHour / (maxLeaksPerHour - capacity); // subtract capacity to make sure we never exceed maxRequestsPerHour even if the bucket is full
        _waterlevel = capacity;
        _maxLeaksPerHour = maxLeaksPerHour;
    }

    public bool Leak()
    {
        lock (_lock)
        {
            var currentTicks = Stopwatch.GetTimestamp();
            var elapsedTicks = currentTicks - _lastRestoreTicks;
            var restored = (int)(elapsedTicks / _ticksPerRestore);
            if(restored > 0)
            {
                _lastRestoreTicks += restored * _ticksPerRestore;
                _waterlevel = Math.Min(_waterlevel + restored, _capacity);
            }

            if(_waterlevel > 0)
            {
                _waterlevel--;
                return true;
            }

            return false;
        }
    }
}
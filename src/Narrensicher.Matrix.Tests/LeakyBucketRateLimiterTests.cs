using Narrensicher.Matrix.Http;


namespace Narrensicher.Matrix.Tests
{
    public class LeakyBucketRateLimiterTests
    {
        [Test]
        public async Task Constructor_ValidParameters_ShouldInitializeCorrectly()
        {
            // Arrange
            int capacity = 10;
            int maxLeaksPerHour = 600;

            // Act
            var rateLimiter = new LeakyBucketRateLimiter(capacity, maxLeaksPerHour);

            // Assert
            await Assert.That(capacity).IsEqualTo(rateLimiter.Capacity);
            await Assert.That(maxLeaksPerHour).IsEqualTo(rateLimiter.MaxLeaksPerHour);
            await Assert.That(capacity).IsEqualTo(rateLimiter.WaterLevel);
        }

        [Test]
        public async Task Constructor_InvalidParameters_ShouldThrowArgumentException()
        {
            // Arrange
            int invalidCapacity = 400;
            int invalidMaxLeaksPerHour = 5;

            // Act & Assert
            await Assert.That(() => new LeakyBucketRateLimiter(invalidCapacity, 600)).Throws<ArgumentException>();
            await Assert.That(() => new LeakyBucketRateLimiter(10, invalidMaxLeaksPerHour)).Throws<ArgumentException>();
        }

        [Test]
        public async Task Leak_WhenQuotaAvailable_ShouldReturnTrue()
        {
            // Arrange
            var rateLimiter = new LeakyBucketRateLimiter(10, 600);

            // Act
            bool result = rateLimiter.Leak();

            // Assert
            await Assert.That(result).IsTrue();
            await Assert.That(rateLimiter.WaterLevel).IsEqualTo(9);
        }

        [Test]
        public async Task Leak_WhenQuotaNotAvailable_ShouldReturnFalse()
        {
            // Arrange
            var rateLimiter = new LeakyBucketRateLimiter(1, 600);
            rateLimiter.Leak(); // Deplete the quota

            // Act
            bool result = rateLimiter.Leak();

            // Assert
            await Assert.That(result).IsFalse();
            await Assert.That(rateLimiter.WaterLevel).IsZero();
        }

        [Test]
        public async Task Leak_WhenQuotaRestored_ShouldReturnTrue()
        {
            // Arrange
            var rateLimiter = new LeakyBucketRateLimiter(1, 6000);
            rateLimiter.Leak(); // Deplete the quota

            // Simulate time passing to restore quota
            await Task.Delay(1000); // Sleep for 1 second to allow quota restoration

            // Act
            bool result = rateLimiter.Leak();

            // Assert
            await Assert.That(result).IsTrue();
        }

        [Test]
        public async Task Leak_MultipleThreads_ShouldBeThreadSafe()
        {
            // Arrange
            var rateLimiter = new LeakyBucketRateLimiter(10, 600);
            int successCount = 0;
            int threadCount = 20;

            // Act
            Parallel.For(0, threadCount, _ =>
            {
                if (rateLimiter.Leak())
                {
                    Interlocked.Increment(ref successCount);
                }
            });

            // Assert
            await Assert.That(successCount).IsEqualTo(10); // Only 10 requests should succeed
        }
    }
}
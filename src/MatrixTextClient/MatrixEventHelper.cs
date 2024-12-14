namespace MatrixTextClient
{
    internal static class MatrixEventHelper
    {

        public static DateTimeOffset ConvertMillisecondsToDateTimeOffset(long milliseconds)
        {
            var dateTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return dateTimeOffset;
        }
    }
}
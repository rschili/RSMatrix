using System;
using System.Net;
using System.Text.Json.Serialization;

namespace MatrixTextClient
{
    /// <summary>
    /// Represents an error response from the Matrix Server
    /// </summary>
    public class MatrixResponseException : Exception
    {
        public string ErrorCode { get; }
        public string ErrorMessage { get; }

        public MatrixResponseException(string errorCode, string errorMessage)
            : base(errorMessage)
        {
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        public override string ToString()
        {
            return $"Error Code: {ErrorCode}, Error Message: {ErrorMessage}";
        }
    }

    /// <summary>
    /// Error response from the Matrix Server which is sent as json
    /// </summary>
    public class MatrixError
    {
        [JsonPropertyName("errcode")]
        public required string ErrorCode { get; set; }

        [JsonPropertyName("error")]
        public required string ErrorMessage { get; set; }
    }
}

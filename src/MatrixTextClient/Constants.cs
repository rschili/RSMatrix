namespace MatrixTextClient
{

    public static class CommonErrorCodes
    {
        public const string M_FORBIDDEN = "M_FORBIDDEN"; // Forbidden access, e.g. joining a room without permission, failed login.
        public const string M_UNKNOWN_TOKEN = "M_UNKNOWN_TOKEN"; // The access or refresh token specified was not recognised.
        public const string M_MISSING_TOKEN = "M_MISSING_TOKEN"; // No access token was specified for the request.
        public const string M_USER_LOCKED = "M_USER_LOCKED"; // The account has been locked and cannot be used at this time.
        public const string M_BAD_JSON = "M_BAD_JSON"; // Request contained valid JSON, but it was malformed in some way, e.g. missing required keys, invalid values for keys.
        public const string M_NOT_JSON = "M_NOT_JSON"; // Request did not contain valid JSON.
        public const string M_NOT_FOUND = "M_NOT_FOUND"; // No resource was found for this request.
        public const string M_LIMIT_EXCEEDED = "M_LIMIT_EXCEEDED"; // Too many requests have been sent in a short period of time. Wait a while then try again. See Rate limiting.
        public const string M_UNRECOGNIZED = "M_UNRECOGNIZED"; // The server did not understand the request. This is expected to be returned with a 404 HTTP status code if the endpoint is not implemented or a 405 HTTP status code if the endpoint is implemented, but the incorrect HTTP method is used.
        public const string M_UNKNOWN = "M_UNKNOWN"; // An unknown error has occurred.
    }

    public static class Constants
    {
        public const string HTTP_CLIENT_NAME = "MatrixService"; // the name used to obtain clients from the factory
    }
}

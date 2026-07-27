namespace UACloudAction.Services
{
    /// <summary>
    /// Well-known rate limiter policy names used across the application.
    /// </summary>
    public static class RateLimiterPolicies
    {
        /// <summary>
        /// Per-client fixed-window rate limit applied to the OPC UA Web API endpoints.
        /// </summary>
        public const string OpcUaWebApi = "OpcUaWebApi";
    }
}

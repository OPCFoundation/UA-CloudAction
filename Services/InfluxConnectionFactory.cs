namespace UACloudAction.Services
{
    using global::InfluxDB.Client;

    /// <summary>
    /// Creates InfluxDB clients from the shared environment-variable configuration used across the
    /// application (the trigger loop and the OPC UA Web API data source).
    /// </summary>
    public static class InfluxConnectionFactory
    {
        /// <summary>
        /// Builds an InfluxDB client, or returns <c>null</c> (and logs) when INFLUX_TOKEN is missing.
        /// </summary>
        public static InfluxDBClient? Create()
        {
            string url = Environment.GetEnvironmentVariable("INFLUX_URL") ?? "http://influxdb.default.svc.cluster.local:8086";
            string? token = Environment.GetEnvironmentVariable("INFLUX_TOKEN");

            if (string.IsNullOrEmpty(token))
            {
                Console.WriteLine("InfluxDB connection not configured (INFLUX_TOKEN missing).");
                return null;
            }

            // The default HTTP timeout is only 10 seconds, which browse-style queries that scan
            // many days of data regularly exceed. Exceeding it aborts the socket read and surfaces
            // as a TaskCanceledException, so use a longer, configurable timeout instead.
            int timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("INFLUX_TIMEOUT_SECONDS"), out int parsed) && parsed > 0
                ? parsed
                : 120;

            InfluxDBClientOptions options = new InfluxDBClientOptions.Builder()
                .Url(url)
                .AuthenticateToken(token)
                .TimeOut(TimeSpan.FromSeconds(timeoutSeconds))
                .Build();

            return new InfluxDBClient(options);
        }
    }
}

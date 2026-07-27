namespace UACloudAction.Services
{
    using global::InfluxDB.Client;
    using global::InfluxDB.Client.Core.Flux.Domain;
    using System.Globalization;
    using UACloudAction.Models;

    /// <summary>
    /// InfluxDB (Flux) backed <see cref="IOpcUaDataSource"/>. Each tag maps to an Influx field
    /// within the configured measurement/bucket. The client is created lazily and reused.
    /// </summary>
    public sealed class InfluxDataSource : IOpcUaDataSource, IDisposable
    {
        private readonly object _lock = new();
        private InfluxDBClient? _influxClient;

        public OpcUaDataSourceType SourceType => OpcUaDataSourceType.InfluxDB;

        public DataValue ReadLatest(string nodeId)
        {
            // Scan all history (start at the Unix epoch) so the latest value is returned regardless of
            // how long ago it was ingested; INFLUX_RANGE only applies to windowed history reads.
            List<DataValue> values = Query(nodeId, "|> last()", rangeStart: "0", rangeStop: "now()");
            return values.Count > 0
                ? values[0]
                : new DataValue { StatusCode = OpcUaStatusCodes.BadNoData };
        }

        public List<DataValue> ReadHistory(string nodeId, DateTime startTime, DateTime endTime, uint maxValues)
        {
            // Mirror the ADX path: use the request's absolute times when provided; when a bound is
            // not given, scan all history (start at the Unix epoch) / up to now, rather than applying
            // a default window, so both data sources behave identically.
            string rangeStart = startTime > DateTime.MinValue
                ? startTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                : "0";
            string rangeStop = endTime < DateTime.MaxValue
                ? endTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                : "now()";

            string limit = maxValues > 0 ? $"|> limit(n: {maxValues})" : string.Empty;

            return Query(nodeId, limit, rangeStart, rangeStop);
        }

        public List<OpcUaBrowseTag> BrowseTags()
        {
            List<OpcUaBrowseTag> tags = new();

            InfluxDBClient? client = GetClient();
            if (client == null)
            {
                return tags;
            }

            string org = Environment.GetEnvironmentVariable("INFLUX_ORG") ?? "iot";
            string bucket = Environment.GetEnvironmentVariable("INFLUX_BUCKET") ?? "mqtt";
            string measurement = Environment.GetEnvironmentVariable("INFLUX_MEASUREMENT") ?? "opcua_pubsub";

            // The OPC UA metadata (namespace URI plus additional metadata) is recorded once as the
            // "DataSetName" tag on the measurement's points; the namespace URI is derived from it,
            // mirroring the ADX path (opcua_metadata_lkv.DataSetName).
            string? dataSetName = GetDataSetName();
            string? namespaceUri = OpcUaNodeId.NamespaceUriFromDataSetName(dataSetName);

            // The field keys of the measurement are the queryable tags.
            string flux = "import \"influxdata/influxdb/schema\"\n"
                + $"schema.measurementFieldKeys(bucket: \"{bucket}\", measurement: \"{measurement}\")";

            List<FluxTable> tables = client.GetQueryApi().QueryAsync(flux, org).GetAwaiter().GetResult();
            foreach (FluxTable table in tables)
            {
                foreach (FluxRecord record in table.Records)
                {
                    string? value = record.GetValue()?.ToString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        tags.Add(new OpcUaBrowseTag(value, namespaceUri, dataSetName));
                    }
                }
            }

            return tags;
        }

        private string? GetDataSetName()
        {
            InfluxDBClient? client = GetClient();
            if (client == null)
            {
                return null;
            }

            string org = Environment.GetEnvironmentVariable("INFLUX_ORG") ?? "iot";
            string bucket = Environment.GetEnvironmentVariable("INFLUX_BUCKET") ?? "mqtt";
            string measurement = Environment.GetEnvironmentVariable("INFLUX_MEASUREMENT") ?? "opcua_pubsub";

            // The OPC UA DataSetName (namespace URI plus additional metadata) is recorded as the
            // "DataSetName" tag on the measurement's points.
            string flux = "import \"influxdata/influxdb/schema\"\n"
                + $"schema.tagValues(bucket: \"{bucket}\", tag: \"DataSetName\", "
                + $"predicate: (r) => r._measurement == \"{measurement}\")";

            List<FluxTable> tables = client.GetQueryApi().QueryAsync(flux, org).GetAwaiter().GetResult();
            foreach (FluxTable table in tables)
            {
                foreach (FluxRecord record in table.Records)
                {
                    string? value = record.GetValue()?.ToString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        private List<DataValue> Query(string nodeId, string tail, string? rangeStart = null, string? rangeStop = null)
        {
            List<DataValue> results = new();

            InfluxDBClient? client = GetClient();
            if (client == null)
            {
                return results;
            }

            string org = Environment.GetEnvironmentVariable("INFLUX_ORG") ?? "iot";
            string bucket = Environment.GetEnvironmentVariable("INFLUX_BUCKET") ?? "mqtt";
            string measurement = Environment.GetEnvironmentVariable("INFLUX_MEASUREMENT") ?? "opcua_pubsub";

            // Resolve the NodeId to its InfluxDB series. String identifiers (the _field) are not unique
            // across DataSets, and each point carries both a "DataSetName" and a "Subject" tag (the
            // Subject is the unique series key). So we key off the DataSetName (which pins the Subject)
            // exactly when the NodeId is a full node id, mirroring the ADX (Subject, Name) resolution;
            // for the constructed "nsu=<ns>;s=<tag>" form we fall back to the _field plus a namespace
            // match on the DataSetName.
            string field = OpcUaNodeId.ParseStringIdentifier(nodeId);
            string? parsedNs = OpcUaNodeId.NamespaceFromNodeId(nodeId);
            bool syntheticNodeId = nodeId.StartsWith("nsu=", StringComparison.OrdinalIgnoreCase);

            string imports = string.Empty;
            string keyFilter;
            if (syntheticNodeId)
            {
                // Constructed nsu=<ns>;s=<tag> form: match the field and (when present) the namespace
                // contained in the DataSetName / Subject.
                if (!string.IsNullOrEmpty(parsedNs))
                {
                    imports = "import \"strings\"\n";
                    keyFilter = $" |> filter(fn: (r) => r._field == \"{EscapeFlux(field)}\""
                        + $" and exists r.DataSetName and strings.containsStr(v: r.DataSetName, substr: \"{EscapeFlux(parsedNs)}\"))";
                }
                else
                {
                    keyFilter = $" |> filter(fn: (r) => r._field == \"{EscapeFlux(field)}\")";
                }
            }
            else
            {
                // Full node id: key off the exact DataSetName (which pins the Subject). Numeric/guid
                // identifiers have no usable field, so the DataSetName match alone selects the series.
                keyFilter = $" |> filter(fn: (r) => exists r.DataSetName and r.DataSetName == \"{EscapeFlux(nodeId)}\")";
            }

            rangeStart ??= "0";
            string range = rangeStop != null
                ? $"|> range(start: {rangeStart}, stop: {rangeStop})"
                : $"|> range(start: {rangeStart})";

            string flux = imports
                + $"from(bucket: \"{bucket}\")"
                + $" {range}"
                + $" |> filter(fn: (r) => r._measurement == \"{measurement}\")"
                + keyFilter
                + $" {tail}";

            List<FluxTable> tables = client.GetQueryApi().QueryAsync(flux, org).GetAwaiter().GetResult();
            foreach (FluxTable table in tables)
            {
                foreach (FluxRecord record in table.Records)
                {
                    results.Add(new DataValue
                    {
                        Value = OpcUaValue.ConvertScalar(record.GetValue()),
                        StatusCode = OpcUaStatusCodes.Good,
                        SourceTimestamp = record.GetTime()?.ToDateTimeUtc()
                    });
                }
            }

            return results;
        }

        private static string EscapeFlux(string value)
        {
            // Escape for a double-quoted Flux string literal.
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private InfluxDBClient? GetClient()
        {
            lock (_lock)
            {
                _influxClient ??= InfluxConnectionFactory.Create();
                return _influxClient;
            }
        }

        public void Dispose()
        {
            _influxClient?.Dispose();
        }
    }
}

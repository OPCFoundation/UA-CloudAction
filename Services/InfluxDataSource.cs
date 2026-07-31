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
            string metadataMeasurement = Environment.GetEnvironmentVariable("INFLUX_METADATA_MEASUREMENT") ?? "opcua_metadata";

            // Namespace URI per dataset writer. This mirrors the ADX path, which joins
            // opcua_telemetry to opcua_metadata_lkv on Subject: here the telemetry
            // (measurement) and the metadata (metadataMeasurement) share the
            // "datasetWriterId" tag, and the metadata carries the OPC UA DataSetName in
            // its "metaName" tag.
            Dictionary<string, string> namespaceByWriter = GetNamespaceByWriter(client, org, bucket, metadataMeasurement);

            // Distinct (datasetWriterId, _field) pairs. Grouping by both columns keeps
            // datasetWriterId in the group key so it survives into each record; a bare
            // group()/distinct() would drop it and lose the writer association.
            string flux = $"from(bucket: \"{EscapeFlux(bucket)}\")"
                + " |> range(start: -30d)"
                + $" |> filter(fn: (r) => r._measurement == \"{EscapeFlux(measurement)}\")"
                + " |> keep(columns: [\"datasetWriterId\", \"_field\"])"
                + " |> group(columns: [\"datasetWriterId\", \"_field\"])"
                + " |> distinct(column: \"_field\")";

            List<FluxTable> tables = client.GetQueryApi().QueryAsync(flux, org).GetAwaiter().GetResult();
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (FluxTable table in tables)
            {
                foreach (FluxRecord record in table.Records)
                {
                    string? value = record.GetValue()?.ToString();
                    if (string.IsNullOrEmpty(value))
                    {
                        continue;
                    }

                    string? writer = record.GetValueByKey("datasetWriterId")?.ToString();
                    string? dataSetName = writer != null && namespaceByWriter.TryGetValue(writer, out string? dsn)
                        ? dsn
                        : null;

                    // Stations publish under a shared namespace URI and are distinguished
                    // only by the ApplicationUri, so de-duplicate on (application, field).
                    // De-duplicating on the field alone would collapse all four stations
                    // into a single entry.
                    string? namespaceUri = OpcUaNodeId.NamespaceUriFromDataSetName(dataSetName);
                    string? applicationUri = OpcUaNodeId.ApplicationUriFromDataSetName(dataSetName);
                    if (!seen.Add($"{applicationUri}|{namespaceUri}|{value}"))
                    {
                        continue;
                    }

                    tags.Add(new OpcUaBrowseTag(value, namespaceUri, dataSetName));
                }
            }

            return tags;
        }

        /// <summary>
        /// Maps each datasetWriterId to the DataSetName recorded for it in the metadata
        /// measurement. The OPC UA namespace URI is embedded in that value.
        /// </summary>
        private Dictionary<string, string> GetNamespaceByWriter(InfluxDBClient client, string org, string bucket, string metadataMeasurement)
        {
            Dictionary<string, string> result = new(StringComparer.Ordinal);

            // metaName holds "<ApplicationUri>;<NodeId>" (the DataSetName). Filtering to a
            // single field (cfgMajor) and taking last() per writer yields exactly one current
            // row per writer, which is the Flux equivalent of the ADX opcua_metadata_lkv
            // ("last known value") materialized view. This matches the metadata join used by
            // the InfluxDB tutorial and the Grafana dashboards.
            string flux = $"from(bucket: \"{EscapeFlux(bucket)}\")"
                + " |> range(start: -30d)"
                + $" |> filter(fn: (r) => r._measurement == \"{EscapeFlux(metadataMeasurement)}\" and r._field == \"cfgMajor\")"
                + " |> group(columns: [\"datasetWriterId\"])"
                + " |> last()"
                + " |> keep(columns: [\"datasetWriterId\", \"metaName\"])";

            try
            {
                List<FluxTable> tables = client.GetQueryApi().QueryAsync(flux, org).GetAwaiter().GetResult();
                foreach (FluxTable table in tables)
                {
                    foreach (FluxRecord record in table.Records)
                    {
                        string? writer = record.GetValueByKey("datasetWriterId")?.ToString();
                        string? metaName = record.GetValueByKey("metaName")?.ToString();
                        if (!string.IsNullOrEmpty(writer) && !string.IsNullOrEmpty(metaName))
                        {
                            result[writer] = metaName;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Metadata is optional: without it the browse result simply carries no
                // namespace URI, which is preferable to failing the whole request.
            }

            return result;
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
            string metadataMeasurement = Environment.GetEnvironmentVariable("INFLUX_METADATA_MEASUREMENT") ?? "opcua_metadata";

            // Resolve the NodeId to its InfluxDB series. The telemetry measurement is keyed by
            // (datasetWriterId, _field); the DataSetName that identifies a node lives in the
            // metadata measurement's "metaName" tag, linked by datasetWriterId. This mirrors the
            // ADX (Subject, Name) resolution, where Subject == datasetWriterId and the metadata
            // lookup supplies the DataSetName.
            string field = OpcUaNodeId.ParseStringIdentifier(nodeId);
            string? parsedNs = OpcUaNodeId.NamespaceFromNodeId(nodeId);
            bool syntheticNodeId = nodeId.StartsWith("nsu=", StringComparison.OrdinalIgnoreCase);

            string keyFilter;
            if (syntheticNodeId)
            {
                // Constructed nsu=<ns>;s=<tag> form: match the field, and narrow to the writers
                // whose metadata carries that namespace when one was supplied.
                keyFilter = $" |> filter(fn: (r) => r._field == \"{EscapeFlux(field)}\")";

                if (!string.IsNullOrEmpty(parsedNs))
                {
                    List<string> writers = GetWritersForNamespace(client, org, bucket, metadataMeasurement, parsedNs!);
                    if (writers.Count > 0)
                    {
                        string set = string.Join(", ", writers.Select(w => $"\"{EscapeFlux(w)}\""));
                        keyFilter += $" |> filter(fn: (r) => contains(value: r.datasetWriterId, set: [{set}]))";
                    }
                }
            }
            else
            {
                // Full node id: find the writers whose DataSetName matches it exactly.
                List<string> writers = GetWritersForDataSetName(client, org, bucket, metadataMeasurement, nodeId);
                if (writers.Count == 0)
                {
                    return results;
                }

                string set = string.Join(", ", writers.Select(w => $"\"{EscapeFlux(w)}\""));
                keyFilter = $" |> filter(fn: (r) => contains(value: r.datasetWriterId, set: [{set}]))";
            }

            rangeStart ??= "0";
            string range = rangeStop != null
                ? $"|> range(start: {rangeStart}, stop: {rangeStop})"
                : $"|> range(start: {rangeStart})";

            string flux = $"from(bucket: \"{EscapeFlux(bucket)}\")"
                + $" {range}"
                + $" |> filter(fn: (r) => r._measurement == \"{EscapeFlux(measurement)}\")"
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

        /// <summary>
        /// Returns the datasetWriterIds whose metadata DataSetName contains the given namespace URI.
        /// </summary>
        private List<string> GetWritersForNamespace(InfluxDBClient client, string org, string bucket, string metadataMeasurement, string namespaceUri)
        {
            return GetWriters(client, org, bucket, metadataMeasurement,
                $" |> filter(fn: (r) => strings.containsStr(v: r.metaName, substr: \"{EscapeFlux(namespaceUri)}\"))",
                needsStrings: true);
        }

        /// <summary>
        /// Returns the datasetWriterIds whose metadata DataSetName equals the given node id exactly.
        /// </summary>
        private List<string> GetWritersForDataSetName(InfluxDBClient client, string org, string bucket, string metadataMeasurement, string dataSetName)
        {
            return GetWriters(client, org, bucket, metadataMeasurement,
                $" |> filter(fn: (r) => r.metaName == \"{EscapeFlux(dataSetName)}\")",
                needsStrings: false);
        }

        private List<string> GetWriters(InfluxDBClient client, string org, string bucket, string metadataMeasurement, string metaFilter, bool needsStrings)
        {
            List<string> writers = new();

            string flux = (needsStrings ? "import \"strings\"\n" : string.Empty)
                + $"from(bucket: \"{EscapeFlux(bucket)}\")"
                + " |> range(start: -30d)"
                + $" |> filter(fn: (r) => r._measurement == \"{EscapeFlux(metadataMeasurement)}\")"
                + metaFilter
                + " |> keep(columns: [\"datasetWriterId\"])"
                + " |> group()"
                + " |> distinct(column: \"datasetWriterId\")";

            try
            {
                List<FluxTable> tables = client.GetQueryApi().QueryAsync(flux, org).GetAwaiter().GetResult();
                foreach (FluxTable table in tables)
                {
                    foreach (FluxRecord record in table.Records)
                    {
                        string? value = record.GetValue()?.ToString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            writers.Add(value);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Treated as "no match": the caller falls back to a field-only filter.
            }

            return writers;
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

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
        /// <summary>
        /// Lookback windows probed by <see cref="ReadLatest"/>, narrowest first. The final window
        /// still bounds the scan so a query can never degrade into a full-bucket read.
        /// Every miss costs a full HTTP round trip, so the ladder is deliberately kept to two rungs:
        /// the narrow one answers the common "currently reporting tag" case from a single shard, and
        /// the wide one catches everything else. Additional intermediate rungs measurably slowed reads
        /// for tags that report rarely without improving the hit rate.
        /// </summary>
        private static readonly string[] LatestLookbackWindows = { "-1h", "-30d" };

        /// <summary>
        /// Lookback window used to discover the available series during browse. Shorter windows are
        /// significantly faster; tags that have not reported within the window are not listed.
        /// Configurable via INFLUX_BROWSE_RANGE (defaults to "-24h").
        /// </summary>
        private static string BrowseRange =>
            Environment.GetEnvironmentVariable("INFLUX_BROWSE_RANGE") ?? "-24h";

        /// <summary>
        /// Lower bound applied by <see cref="ReadHistory"/> when the request carries no start time.
        /// Configurable via INFLUX_HISTORY_FLOOR (defaults to "-30d"); set it to "0" to restore the
        /// previous scan-all-history behaviour at the cost of a full-bucket read.
        /// </summary>
        private static string HistoryFloor =>
            Environment.GetEnvironmentVariable("INFLUX_HISTORY_FLOOR") ?? "-30d";

        private readonly object _lock = new();
        private InfluxDBClient? _influxClient;

        public OpcUaDataSourceType SourceType => OpcUaDataSourceType.InfluxDB;

        public DataValue ReadLatest(string nodeId)
        {
            // Probe progressively wider lookback windows instead of scanning from the Unix epoch.
            // An unbounded range forces InfluxDB to read every shard in the bucket before last()
            // can pick a point, which regularly exceeds the HTTP client timeout and surfaces as a
            // TaskCanceledException. A bounded range lets last() be answered from a few shards.
            foreach (string start in LatestLookbackWindows)
            {
                List<DataValue> values = Query(nodeId, "|> last()", rangeStart: start, rangeStop: "now()");
                if (values.Count > 0)
                {
                    return values[0];
                }
            }

            return new DataValue { StatusCode = OpcUaStatusCodes.BadNoData };
        }

        public List<DataValue> ReadHistory(string nodeId, DateTime startTime, DateTime endTime, uint maxValues)
        {
            // Mirror the ADX path: use the request's absolute times when provided; when a bound is
            // not given, fall back to the configured history floor / up to now.
            // An open-ended start ("0", the Unix epoch) makes InfluxDB read every shard in the bucket
            // before it can return anything, which is the slowest possible query and can outlive the
            // HTTP timeout. When the caller does not supply a lower bound, fall back to a bounded
            // (configurable) floor instead so the scan stays proportional to the retained data.
            string rangeStart = startTime > DateTime.MinValue
                ? startTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                : HistoryFloor;
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

            // Discovering the available (datasetWriterId, _field) pairs by scanning telemetry
            // ("range + filter + last()" over the whole browse window) forces the storage engine to
            // open one series per pair. On a high-cardinality bucket that regularly outlives the HTTP
            // client timeout and surfaces as a TaskCanceledException. The schema package answers the
            // same question from the index/metadata only, so enumerate the writers and then the field
            // keys per writer instead: no point data is read at all.
            List<string> writers = QueryTagValues(client, org, bucket, measurement, "datasetWriterId", writerFilter: null);
            if (writers.Count == 0)
            {
                // No writer tag indexed in this window: still list the fields so the browse result is
                // not empty; the namespace / DataSetName is then simply unknown.
                writers.Add(string.Empty);
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (string writer in writers)
            {
                string? dataSetName = namespaceByWriter.TryGetValue(writer, out string? dsn) ? dsn : null;
                string? namespaceUri = OpcUaNodeId.NamespaceUriFromDataSetName(dataSetName);
                string? applicationUri = OpcUaNodeId.ApplicationUriFromDataSetName(dataSetName);

                string? writerFilter = writer.Length > 0 ? writer : null;
                foreach (string field in QueryTagValues(client, org, bucket, measurement, "_field", writerFilter))
                {
                    // Stations publish under a shared namespace URI and are distinguished
                    // only by the ApplicationUri, so de-duplicate on (application, field).
                    // De-duplicating on the field alone would collapse all four stations
                    // into a single entry.
                    if (!seen.Add($"{applicationUri}|{namespaceUri}|{field}"))
                    {
                        continue;
                    }

                    tags.Add(new OpcUaBrowseTag(field, namespaceUri, dataSetName));
                }
            }

            return tags;
        }

        /// <summary>
        /// Returns the distinct values of <paramref name="tag"/> recorded for the measurement within
        /// <see cref="BrowseRange"/>, optionally restricted to a single datasetWriterId. This uses the
        /// schema package, which is answered from metadata rather than by scanning series data, and is
        /// therefore orders of magnitude cheaper than a "filter + last()" browse query.
        /// </summary>
        private List<string> QueryTagValues(InfluxDBClient client, string org, string bucket, string measurement, string tag, string? writerFilter)
        {
            string predicate = string.IsNullOrEmpty(writerFilter)
                ? $"(r) => r._measurement == \"{EscapeFlux(measurement)}\""
                : $"(r) => r._measurement == \"{EscapeFlux(measurement)}\" and r.datasetWriterId == \"{EscapeFlux(writerFilter)}\"";

            string flux = "import \"influxdata/influxdb/schema\"\n"
                + $"schema.tagValues(bucket: \"{EscapeFlux(bucket)}\", tag: \"{EscapeFlux(tag)}\", predicate: {predicate}, start: {BrowseRange})";

            List<string> values = new();

            try
            {
                List<FluxTable> tables = client.GetQueryApi().QueryAsync(flux, org).GetAwaiter().GetResult();
                foreach (FluxTable table in tables)
                {
                    foreach (FluxRecord record in table.Records)
                    {
                        string? value = record.GetValueByKey("_value")?.ToString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            values.Add(value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // A failing or timing-out query must not take down the whole browse request:
                // report no values so the caller can surface BadNoData instead.
                Console.WriteLine($"InfluxDB browse query for tag '{tag}' failed: {ex.Message}");
            }

            return values;
        }

        /// <summary>
        /// Maps each datasetWriterId to the DataSetName recorded for it in the metadata
        /// measurement. The OPC UA namespace URI is embedded in that value.
        /// </summary>
        private Dictionary<string, string> GetNamespaceByWriter(InfluxDBClient client, string org, string bucket, string metadataMeasurement)
        {
            Dictionary<string, string> result = new(StringComparer.Ordinal);

            // metaName holds "<ApplicationUri>;<NodeId>" (the DataSetName). Filtering to a
            // single field (cfgMajor) and taking last() yields the current row per series, which is
            // the Flux equivalent of the ADX opcua_metadata_lkv ("last known value") materialized
            // view. As above, last() is applied directly after range+filter so the query is pushed
            // down to the storage engine; the newest row per writer is then picked client-side (the
            // result set is tiny - one row per series).
            string flux = $"from(bucket: \"{EscapeFlux(bucket)}\")"
                + $" |> range(start: {BrowseRange})"
                + $" |> filter(fn: (r) => r._measurement == \"{EscapeFlux(metadataMeasurement)}\" and r._field == \"cfgMajor\")"
                + " |> last()"
                + " |> keep(columns: [\"datasetWriterId\", \"metaName\", \"_time\"])";

            Dictionary<string, DateTime> newestByWriter = new(StringComparer.Ordinal);

            try
            {
                List<FluxTable> tables = client.GetQueryApi().QueryAsync(flux, org).GetAwaiter().GetResult();
                foreach (FluxTable table in tables)
                {
                    foreach (FluxRecord record in table.Records)
                    {
                        string? writer = record.GetValueByKey("datasetWriterId")?.ToString();
                        string? metaName = record.GetValueByKey("metaName")?.ToString();
                        if (string.IsNullOrEmpty(writer) || string.IsNullOrEmpty(metaName))
                        {
                            continue;
                        }

                        // Keep the most recent metaName when a writer has several (it can change
                        // over time); without a group() the query returns one row per series.
                        DateTime time = record.GetTime()?.ToDateTimeUtc() ?? DateTime.MinValue;
                        if (!newestByWriter.TryGetValue(writer, out DateTime existing) || time >= existing)
                        {
                            newestByWriter[writer] = time;
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

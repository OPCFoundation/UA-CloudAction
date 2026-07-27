namespace UACloudAction.Services
{
    using Kusto.Data.Common;
    using System.Data;
    using System.Globalization;
    using UACloudAction.Models;

    /// <summary>
    /// Azure Data Explorer (Kusto) backed <see cref="IOpcUaDataSource"/>. Queries the
    /// <c>opcua_telemetry</c> table where <c>Name</c> is the tag, <c>Value</c> the reading and
    /// <c>Timestamp</c> the source time. The connection is created lazily and reused.
    /// </summary>
    public sealed class AdxDataSource : IOpcUaDataSource, IDisposable
    {
        private readonly object _lock = new();
        private ICslQueryProvider? _queryProvider;

        public OpcUaDataSourceType SourceType => OpcUaDataSourceType.ADX;

        public DataValue ReadLatest(string nodeId)
        {
            List<DataValue> values = ReadSeries(nodeId, DateTime.MinValue, DateTime.MaxValue, 1, descending: true);
            return values.Count > 0
                ? values[0]
                : new DataValue { StatusCode = OpcUaStatusCodes.BadNoData };
        }

        public List<DataValue> ReadHistory(string nodeId, DateTime startTime, DateTime endTime, uint maxValues)
        {
            return ReadSeries(nodeId, startTime, endTime, maxValues, descending: false);
        }

        public List<OpcUaBrowseTag> BrowseTags()
        {
            List<OpcUaBrowseTag> tags = new();

            ICslQueryProvider? provider = GetQueryProvider();
            if (provider == null)
            {
                return tags;
            }

            // The tags live in the telemetry table (Name), and each telemetry row's Subject links to
            // the OPC UA PubSub metadata. opcua_metadata_lkv.DataSetName carries the namespace URI
            // plus additional metadata (UA Cloud Publisher). Producers that omit it (e.g. Azure IoT
            // Operations) still carry the server namespace table in the raw DataSetMetaData
            // "Namespaces" array (OPC UA PubSub, Part 14); the first non-base entry (index > 0) is the
            // application's own namespace. We look both up per Subject so browse works for all
            // producers.
            const string query =
                "let RawNamespaceBySubject = opcua_metadata_raw"
                + "| extend Subject = tostring(split(tostring(payload[\"DataSetWriterId\"]), \"/\")[0])"
                + "| extend nss = todynamic(payload[\"MetaData\"][\"Namespaces\"])"
                + "| mv-expand with_itemindex=idx RawNamespaceUri = nss to typeof(string)"
                + "| where idx > 0 and isnotempty(RawNamespaceUri)"
                + "| summarize RawNamespaceUri = take_any(RawNamespaceUri) by Subject;"
                + "opcua_telemetry | distinct Name, Subject"
                + "| join kind=leftouter (opcua_metadata_lkv | distinct Subject, DataSetName) on Subject"
                + "| lookup kind=leftouter (RawNamespaceBySubject) on Subject"
                + "| project Tag = Name, DataSetName, RawNamespaceUri"
                + "| distinct Tag, DataSetName, RawNamespaceUri"
                + "| order by Tag asc";

            ClientRequestProperties clientRequestProperties = new()
            {
                ClientRequestId = Guid.NewGuid().ToString()
            };

            using IDataReader? reader = provider.ExecuteQuery(query, clientRequestProperties);
            while ((reader != null) && reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                string? name = reader.GetValue(0)?.ToString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string? dataSetName = reader.IsDBNull(1) ? null : reader.GetValue(1)?.ToString();
                string? rawNamespaceUri = reader.IsDBNull(2) ? null : reader.GetValue(2)?.ToString();

                // Prefer the namespace embedded in the DataSetName; fall back to the raw metadata
                // Namespaces array when the DataSetName carries no namespace URI.
                string? namespaceUri = OpcUaNodeId.NamespaceUriFromDataSetName(dataSetName)
                    ?? OpcUaNodeId.FirstNamespaceUri(rawNamespaceUri);

                tags.Add(new OpcUaBrowseTag(name, namespaceUri, dataSetName));
            }

            return tags;
        }

        private List<DataValue> ReadSeries(string nodeId, DateTime startTime, DateTime endTime, uint maxValues, bool descending)
        {
            List<DataValue> results = new();

            ICslQueryProvider? provider = GetQueryProvider();
            if (provider == null)
            {
                return results;
            }

            // Resolve the NodeId to the underlying telemetry series (unique Subject + Name) via
            // metadata - string identifiers are not unique across DataSets, and numeric/guid
            // identifiers do not map to the telemetry Name at all - then read the values for the
            // resolved series. Telemetry is keyed by (Subject, Name), so we constrain on both.
            string query = BuildResolutionPrelude(nodeId)
                + "opcua_telemetry"
                + "| where Subject in (ResolvedSeries | distinct Subject)"
                + "| join kind=inner (ResolvedSeries) on Subject, Name";

            if (startTime > DateTime.MinValue)
            {
                query += $"| where Timestamp >= datetime({startTime.ToUniversalTime():o})";
            }

            if (endTime < DateTime.MaxValue)
            {
                query += $"| where Timestamp <= datetime({endTime.ToUniversalTime():o})";
            }

            query += "| project Timestamp, Value = tostring(Value)"
                + (descending ? "| order by Timestamp desc" : "| order by Timestamp asc");

            if (maxValues > 0)
            {
                query += $"| take {maxValues}";
            }

            ClientRequestProperties clientRequestProperties = new()
            {
                ClientRequestId = Guid.NewGuid().ToString()
            };

            using IDataReader? reader = provider.ExecuteQuery(query, clientRequestProperties);
            while ((reader != null) && reader.Read())
            {
                DateTime? timestamp = null;
                if (!reader.IsDBNull(0))
                {
                    timestamp = Convert.ToDateTime(reader.GetValue(0), CultureInfo.InvariantCulture).ToUniversalTime();
                }

                // The telemetry Value is a dynamic column; it is projected as a string server-side
                // (tostring) to avoid empty-array serialization, then converted back to a typed scalar.
                object? value = reader.IsDBNull(1) ? null : OpcUaValue.ConvertScalar(reader.GetValue(1)?.ToString());

                results.Add(new DataValue
                {
                    Value = value,
                    StatusCode = OpcUaStatusCodes.Good,
                    SourceTimestamp = timestamp
                });
            }

            return results;
        }

        /// <summary>
        /// Builds the KQL prelude that defines <c>ResolvedSeries</c>: the telemetry (Subject, Name)
        /// pair(s) that the given OPC UA NodeId maps to. A NodeId matches when the metadata
        /// DataSetName equals it exactly (covers full node ids, incl. numeric/guid identifiers), or -
        /// for the constructed <c>nsu=&lt;ns&gt;;s=&lt;tag&gt;</c> form - when the telemetry Name
        /// equals the string identifier and the effective namespace (from DataSetName or the raw
        /// Namespaces array) matches. A bare tag falls back to a Name match. Telemetry is keyed by
        /// (Subject, Name), so the resolved series carries both columns.
        /// </summary>
        private static string BuildResolutionPrelude(string nodeId)
        {
            string parsed = OpcUaNodeId.ParseStringIdentifier(nodeId);
            bool hasStringId = nodeId.StartsWith("s=", StringComparison.OrdinalIgnoreCase)
                || nodeId.IndexOf(";s=", StringComparison.OrdinalIgnoreCase) >= 0;
            bool bareTag = !nodeId.Contains(';') && !StartsWithScheme(nodeId);
            string tagName = hasStringId ? parsed : (bareTag ? nodeId : string.Empty);
            string parsedNs = OpcUaNodeId.NamespaceFromNodeId(nodeId) ?? string.Empty;

            return
                $"let Node = '{EscapeKql(nodeId)}';"
                + $"let TagName = '{EscapeKql(tagName)}';"
                + $"let ParsedNs = '{EscapeKql(parsedNs)}';"
                + "let RawNamespaceBySubject = opcua_metadata_raw"
                + "| extend Subject = tostring(split(tostring(payload[\"DataSetWriterId\"]), \"/\")[0])"
                + "| extend nss = todynamic(payload[\"MetaData\"][\"Namespaces\"])"
                + "| mv-expand with_itemindex=idx RawNamespaceUri = nss to typeof(string)"
                + "| where idx > 0 and isnotempty(RawNamespaceUri)"
                + "| summarize RawNamespaceUri = take_any(RawNamespaceUri) by Subject;"
                + "let ResolvedSeries = opcua_telemetry"
                + "| distinct Name, Subject"
                + "| join kind=leftouter (opcua_metadata_lkv | distinct Subject, DataSetName) on Subject"
                + "| lookup kind=leftouter (RawNamespaceBySubject) on Subject"
                + "| extend DsnNs = extract(@'(https?://[^;]+|urn:[^;]+)', 1, DataSetName)"
                + "| extend EffNs = case(isnotempty(DsnNs), DsnNs, isnotempty(RawNamespaceUri), RawNamespaceUri, '')"
                + "| where DataSetName == Node or (TagName != '' and Name == TagName and (ParsedNs == '' or EffNs == ParsedNs or DataSetName has ParsedNs))"
                + "| distinct Subject, Name;";
        }

        private static bool StartsWithScheme(string nodeId) =>
            nodeId.StartsWith("nsu=", StringComparison.OrdinalIgnoreCase)
            || nodeId.StartsWith("ns=", StringComparison.OrdinalIgnoreCase)
            || nodeId.StartsWith("svr=", StringComparison.OrdinalIgnoreCase)
            || nodeId.StartsWith("urn:", StringComparison.OrdinalIgnoreCase);

        private ICslQueryProvider? GetQueryProvider()
        {
            lock (_lock)
            {
                _queryProvider ??= AdxConnectionFactory.Create();
                return _queryProvider;
            }
        }

        private static string EscapeKql(string value)
        {
            // Prevent breaking out of the single-quoted KQL string literal (escape backslash first).
            return value.Replace("\\", "\\\\").Replace("'", "\\'");
        }

        public void Dispose()
        {
            _queryProvider?.Dispose();
        }
    }
}

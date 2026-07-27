namespace UACloudAction.Services
{
    using System.Globalization;
    using System.Text.Json;
    using UACloudAction.Models;

    /// <summary>
    /// Selects which time-series store backs an OPC UA Web API request.
    /// </summary>
    public enum OpcUaDataSourceType
    {
        ADX,

        InfluxDB
    }

    /// <summary>
    /// OPC UA StatusCodes (OPC 10000-4 / 10000-8) used by the Web API responses.
    /// </summary>
    public static class OpcUaStatusCodes
    {
        public const uint Good = 0x00000000;

        public const uint BadNoData = 0x809B0000;

        public const uint BadUnexpectedError = 0x80010000;
    }

    /// <summary>
    /// Helpers for normalizing telemetry values returned by the data sources.
    /// </summary>
    public static class OpcUaValue
    {
        /// <summary>
        /// Converts a stringified telemetry value back to a typed scalar (bool / long / double) when
        /// possible, so the JSON response carries the natural type; otherwise returns the string.
        /// Non-string values are returned unchanged.
        /// </summary>
        public static object? ConvertScalar(object? value)
        {
            if (value is not string text)
            {
                return value;
            }

            if (bool.TryParse(text, out bool b))
            {
                return b;
            }

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
            {
                return l;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                return d;
            }

            return text;
        }
    }

    /// <summary>
    /// Abstraction over a single time-series data source (ADX or InfluxDB) that backs the OPC UA
    /// Web API Read, HistoryRead and Browse operations. Implementations query their store using a
    /// bare tag name (the ADX telemetry Name / InfluxDB field), created lazily and reused.
    /// </summary>
    public interface IOpcUaDataSource
    {
        /// <summary>
        /// The data source this implementation serves.
        /// </summary>
        OpcUaDataSourceType SourceType { get; }

        /// <summary>
        /// Returns the latest value for the given OPC UA NodeId, or a bad-status value when
        /// unavailable. The NodeId is resolved to the underlying series via the store's metadata
        /// (string identifiers are not unique, so metadata disambiguation is required).
        /// </summary>
        DataValue ReadLatest(string nodeId);

        /// <summary>
        /// Returns the raw historical values for the given OPC UA NodeId within the time range. The
        /// NodeId is resolved to the underlying series via the store's metadata.
        /// </summary>
        List<DataValue> ReadHistory(string nodeId, DateTime startTime, DateTime endTime, uint maxValues);

        /// <summary>
        /// Returns the flat list of queryable tags in this data source, each paired with the OPC UA
        /// namespace URI recorded for it in the store's metadata (or <c>null</c> when unknown).
        /// </summary>
        List<OpcUaBrowseTag> BrowseTags();
    }

    /// <summary>
    /// A queryable tag discovered by Browse: the tag name (ADX telemetry Name / InfluxDB field), the
    /// OPC UA namespace URI it belongs to (from the store's metadata, or <c>null</c> when unknown),
    /// and the raw DataSetName metadata it was derived from (when available).
    /// </summary>
    public sealed record OpcUaBrowseTag(string Tag, string? NamespaceUri, string? DataSetName = null);

    /// <summary>
    /// Helpers for parsing and building OPC UA NodeIds used by the Web API.
    /// </summary>
    public static class OpcUaNodeId
    {
        /// <summary>
        /// Fallback namespace URI used only when the OPC UA metadata does not record one.
        /// </summary>
        public const string DefaultNamespaceUri = "http://opcfoundation.org/UA/";

        /// <summary>
        /// Builds an expanded string NodeId (<c>nsu=&lt;namespaceUri&gt;;s=&lt;tag&gt;</c>) for a tag.
        /// </summary>
        public static string BuildExpandedNodeId(string tag, string namespaceUri) => $"nsu={namespaceUri};s={tag}";

        /// <summary>
        /// Normalizes a namespace value read from the OPC UA metadata. The metadata may store a
        /// single namespace URI, or a JSON array of namespace URIs (e.g.
        /// <c>["http://.../a/","http://.../b/"]</c>); in the latter case the first entry is used.
        /// Returns <c>null</c> when no namespace can be determined.
        /// </summary>
        public static string? FirstNamespaceUri(string? namespaceValue)
        {
            if (string.IsNullOrWhiteSpace(namespaceValue))
            {
                return null;
            }

            string trimmed = namespaceValue.Trim();
            if (trimmed.StartsWith('['))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(trimmed);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement element in document.RootElement.EnumerateArray())
                        {
                            string? uri = element.GetString();
                            if (!string.IsNullOrWhiteSpace(uri))
                            {
                                return uri;
                            }
                        }
                    }

                    return null;
                }
                catch (JsonException)
                {
                    // Not valid JSON; fall through and treat it as a plain string.
                }
            }

            return trimmed;
        }

        /// <summary>
        /// Derives the OPC UA namespace URI from a metadata DataSetName value. The DataSetName
        /// carries the namespace URI plus additional metadata; it may be a JSON array, or a
        /// delimited string (e.g. <c>&lt;namespaceUri&gt;;&lt;app&gt;;&lt;location&gt;</c>). The first
        /// URI-looking segment (http/https/urn) is returned, or <c>null</c> when the value carries no
        /// namespace URI (so a fallback source, e.g. the raw metadata table, can be used instead).
        /// </summary>
        public static string? NamespaceUriFromDataSetName(string? dataSetName)
        {
            string? value = FirstNamespaceUri(dataSetName);
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            foreach (string segment in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (LooksLikeUri(segment))
                {
                    return segment;
                }
            }

            return null;
        }

        private static bool LooksLikeUri(string value) =>
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("urn:", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Extracts the string identifier from an OPC UA (Expanded)NodeId. It locates the
        /// <c>s=&lt;value&gt;</c> component wherever it appears - after any combination of server
        /// (<c>svr=</c> or a server <c>urn:</c>), namespace (<c>nsu=</c>/<c>ns=</c>) prefixes - and
        /// returns everything after <c>s=</c> (the string identifier may itself contain ';'). A
        /// namespace-less <c>s=&lt;value&gt;</c> or a bare tag (no identifier component) is returned
        /// as-is. The <c>s=</c> identifier maps to the ADX telemetry Name / InfluxDB field.
        /// </summary>
        public static string ParseStringIdentifier(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return nodeId;
            }

            // Find the string identifier component: either the whole value starts with "s=", or an
            // ";s=" component appears after the server/namespace prefixes. Everything after that "s="
            // is the identifier value (which may itself contain ';').
            if (nodeId.StartsWith("s=", StringComparison.OrdinalIgnoreCase))
            {
                return nodeId[2..];
            }

            int marker = nodeId.IndexOf(";s=", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                return nodeId[(marker + 3)..];
            }

            // No string identifier component (e.g. a bare tag, or a numeric/guid identifier that does
            // not map to a tag). Strip any leading server/namespace prefixes and return the remainder.
            string remainder = nodeId;
            while (remainder.StartsWith("svr=", StringComparison.OrdinalIgnoreCase)
                || remainder.StartsWith("nsu=", StringComparison.OrdinalIgnoreCase)
                || remainder.StartsWith("ns=", StringComparison.OrdinalIgnoreCase))
            {
                int semicolon = remainder.IndexOf(';');
                if (semicolon < 0)
                {
                    break;
                }

                remainder = remainder[(semicolon + 1)..];
            }

            return remainder;
        }

        /// <summary>
        /// Extracts the <c>nsu=&lt;namespaceUri&gt;</c> namespace component from an OPC UA
        /// (Expanded)NodeId, or <c>null</c> when the NodeId does not carry one.
        /// </summary>
        public static string? NamespaceFromNodeId(string? nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return null;
            }

            foreach (string component in nodeId.Split(';'))
            {
                if (component.StartsWith("nsu=", StringComparison.OrdinalIgnoreCase))
                {
                    string uri = component["nsu=".Length..];
                    return string.IsNullOrEmpty(uri) ? null : uri;
                }
            }

            return null;
        }
    }
}

namespace UACloudAction.Models
{
    using System.Text.Json.Serialization;

    // Data Transfer Objects modeling the subset of the OPC UA Web API JSON schema
    // (https://webapi.opcfoundation.org/data/opc.ua.openapi.allservices.json) needed to
    // expose the Read and HistoryRead services over the ADX and InfluxDB data sources.
    // Only the historian-relevant operations are implemented; the shapes follow the
    // official OpenAPI definitions so standard OPC UA Web API clients can interoperate.

    /// <summary>
    /// Common request header (OPC 10000-4, 7.33). Only the fields relevant to a
    /// stateless HTTPS historian are modeled; the rest are accepted and ignored.
    /// </summary>
    public sealed class RequestHeader
    {
        public string? AuthenticationToken { get; set; }

        public DateTime? Timestamp { get; set; }

        public uint RequestHandle { get; set; }

        public uint ReturnDiagnostics { get; set; }

        public string? AuditEntryId { get; set; }

        public uint TimeoutHint { get; set; }
    }

    /// <summary>
    /// Common response header (OPC 10000-4, 7.34).
    /// </summary>
    public sealed class ResponseHeader
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public uint RequestHandle { get; set; }

        public uint ServiceResult { get; set; }

        public string? StringTable { get; set; }
    }

    /// <summary>
    /// A value read from the historian (OPC 10000-4, 7.11). A null <see cref="Value"/>
    /// combined with a non-zero <see cref="StatusCode"/> represents a bad/uncertain read.
    /// </summary>
    public sealed class DataValue
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Value { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint StatusCode { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? SourceTimestamp { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? ServerTimestamp { get; set; }
    }

    /// <summary>
    /// Identifies a node/attribute to read (OPC 10000-4, 7.24). For this historian the
    /// <see cref="NodeId"/> selects the tag (the ADX telemetry Name or the Influx field).
    /// </summary>
    public sealed class ReadValueId
    {
        public string? NodeId { get; set; }

        public uint AttributeId { get; set; } = 13; // Value

        public string? IndexRange { get; set; }
    }

    /// <summary>
    /// Read request (OPC 10000-4, 5.11.2).
    /// </summary>
    public sealed class ReadRequest
    {
        public RequestHeader? RequestHeader { get; set; }

        public double MaxAge { get; set; }

        public int TimestampsToReturn { get; set; }

        public List<ReadValueId>? NodesToRead { get; set; }
    }

    /// <summary>
    /// Read response (OPC 10000-4, 5.11.2).
    /// </summary>
    public sealed class ReadResponse
    {
        public ResponseHeader? ResponseHeader { get; set; }

        public List<DataValue>? Results { get; set; }
    }

    /// <summary>
    /// Raw/modified history read details (OPC 10000-11, 6.5.3). Selects the time range
    /// and maximum number of values per node returned by <c>HistoryRead</c>.
    /// </summary>
    public sealed class ReadRawModifiedDetails
    {
        public bool IsReadModified { get; set; }

        public DateTime StartTime { get; set; }

        public DateTime EndTime { get; set; }

        public uint NumValuesPerNode { get; set; }

        public bool ReturnBounds { get; set; }
    }

    /// <summary>
    /// Identifies a node whose history is requested (OPC 10000-11, 6.3).
    /// </summary>
    public sealed class HistoryReadValueId
    {
        public string? NodeId { get; set; }

        public string? IndexRange { get; set; }

        public string? ContinuationPoint { get; set; }
    }

    /// <summary>
    /// History read request (OPC 10000-4, 5.11.3). <see cref="HistoryReadDetails"/> carries
    /// a <see cref="ReadRawModifiedDetails"/> instance for the raw historian query.
    /// </summary>
    public sealed class HistoryReadRequest
    {
        public RequestHeader? RequestHeader { get; set; }

        public ReadRawModifiedDetails? HistoryReadDetails { get; set; }

        public int TimestampsToReturn { get; set; }

        public bool ReleaseContinuationPoints { get; set; }

        public List<HistoryReadValueId>? NodesToRead { get; set; }
    }

    /// <summary>
    /// The historical values for a single node (OPC 10000-11, 6.6.2).
    /// </summary>
    public sealed class HistoryData
    {
        public List<DataValue> DataValues { get; set; } = new();
    }

    /// <summary>
    /// The per-node result of a history read (OPC 10000-4, 7.20).
    /// </summary>
    public sealed class HistoryReadResult
    {
        public uint StatusCode { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ContinuationPoint { get; set; }

        public HistoryData? HistoryData { get; set; }
    }

    /// <summary>
    /// History read response (OPC 10000-4, 5.11.3).
    /// </summary>
    public sealed class HistoryReadResponse
    {
        public ResponseHeader? ResponseHeader { get; set; }

        public List<HistoryReadResult>? Results { get; set; }
    }

    /// <summary>
    /// Browse request (OPC 10000-4, 5.9.2). For this historian browse simply enumerates the
    /// available tags, so the request body is optional and its fields are accepted but not used.
    /// </summary>
    public sealed class BrowseRequest
    {
        public RequestHeader? RequestHeader { get; set; }
    }

    /// <summary>
    /// A single browse reference (OPC 10000-4, 7.29): a queryable tag exposed as a node.
    /// </summary>
    public sealed class ReferenceDescription
    {
        public string? NodeId { get; set; }

        public string? BrowseName { get; set; }

        public string? DisplayName { get; set; }
    }

    /// <summary>
    /// The result of a browse operation (OPC 10000-4, 7.4): a flat list of queryable tags.
    /// </summary>
    public sealed class BrowseResult
    {
        public uint StatusCode { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ContinuationPoint { get; set; }

        public List<ReferenceDescription> References { get; set; } = new();
    }

    /// <summary>
    /// Browse response (OPC 10000-4, 5.9.2).
    /// </summary>
    public sealed class BrowseResponse
    {
        public ResponseHeader? ResponseHeader { get; set; }

        public List<BrowseResult>? Results { get; set; }
    }
}

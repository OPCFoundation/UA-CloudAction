namespace UACloudAction.Services
{
    using UACloudAction.Models;

    /// <summary>
    /// Resolves the appropriate <see cref="IOpcUaDataSource"/> (ADX or InfluxDB) for a request and
    /// executes the OPC UA Web API Read, HistoryRead and Browse operations against it, handling
    /// NodeId parsing and error mapping to OPC UA StatusCodes.
    /// </summary>
    public sealed class OpcUaDataSourceResolver
    {
        private readonly AdxDataSource _adx;
        private readonly InfluxDataSource _influx;

        public OpcUaDataSourceResolver(AdxDataSource adx, InfluxDataSource influx)
        {
            _adx = adx;
            _influx = influx;
        }

        /// <summary>
        /// Resolves the data source from the DATA_SOURCE environment variable, defaulting to ADX.
        /// </summary>
        public IOpcUaDataSource Resolve()
        {
            string value = Environment.GetEnvironmentVariable("DATA_SOURCE") ?? "ADX";

            if (value.Equals("InfluxDB", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Influx", StringComparison.OrdinalIgnoreCase))
            {
                return _influx;
            }

            return _adx;
        }

        /// <summary>
        /// Returns the latest value for the given node, or a bad-status value on error.
        /// </summary>
        public DataValue ReadLatest(IOpcUaDataSource dataSource, string nodeId)
        {
            try
            {
                return dataSource.ReadLatest(nodeId);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return new DataValue { StatusCode = OpcUaStatusCodes.BadUnexpectedError };
            }
        }

        /// <summary>
        /// Returns the raw historical values for the given node within the time range.
        /// </summary>
        public HistoryReadResult ReadHistory(IOpcUaDataSource dataSource, string nodeId, DateTime startTime, DateTime endTime, uint maxValues)
        {
            try
            {
                List<DataValue> values = dataSource.ReadHistory(nodeId, startTime, endTime, maxValues);

                return new HistoryReadResult
                {
                    StatusCode = values.Count > 0 ? OpcUaStatusCodes.Good : OpcUaStatusCodes.BadNoData,
                    HistoryData = new HistoryData { DataValues = values }
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return new HistoryReadResult
                {
                    StatusCode = OpcUaStatusCodes.BadUnexpectedError,
                    HistoryData = new HistoryData()
                };
            }
        }

        /// <summary>
        /// Returns the flat list of queryable tags as OPC UA references (expanded NodeId +
        /// display name), so callers can discover what Read and HistoryRead can query.
        /// </summary>
        public BrowseResult Browse(IOpcUaDataSource dataSource)
        {
            try
            {
                List<OpcUaBrowseTag> tags = dataSource.BrowseTags();

                BrowseResult result = new()
                {
                    StatusCode = tags.Count > 0 ? OpcUaStatusCodes.Good : OpcUaStatusCodes.BadNoData
                };

                foreach (OpcUaBrowseTag tag in tags)
                {
                    // Use the tag's own namespace from metadata; fall back to the base OPC UA
                    // namespace only when the metadata does not record one for it.
                    string namespaceUri = tag.NamespaceUri ?? OpcUaNodeId.DefaultNamespaceUri;

                    // The DataSetName is preferred as the NodeId when it is itself a well-formed OPC UA
                    // (Expanded)NodeId (starts with "nsu=" or "urn:"); otherwise it is just descriptive
                    // metadata, so the constructed expanded NodeId is used as the NodeId and the
                    // DataSetName becomes the DisplayName.
                    string dataSetName = string.IsNullOrEmpty(tag.DataSetName) ? tag.Tag : tag.DataSetName;
                    string expandedNodeId = OpcUaNodeId.BuildExpandedNodeId(tag.Tag, namespaceUri);

                    string nodeId = dataSetName;
                    string displayName = expandedNodeId;
                    if (!nodeId.StartsWith("nsu=", StringComparison.OrdinalIgnoreCase)
                        && !nodeId.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
                    {
                        (nodeId, displayName) = (displayName, nodeId);
                    }

                    result.References.Add(new ReferenceDescription
                    {
                        NodeId = nodeId,
                        BrowseName = tag.Tag,
                        DisplayName = displayName
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return new BrowseResult { StatusCode = OpcUaStatusCodes.BadUnexpectedError };
            }
        }
    }
}

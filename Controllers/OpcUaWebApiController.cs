namespace UACloudAction.Controllers
{
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.RateLimiting;
    using UACloudAction.Models;
    using UACloudAction.Services;
    /// <summary>
    /// Implements the OPC UA Web API (https://webapi.opcfoundation.org/data/opc.ua.openapi.allservices.json)
    /// operations that map to a historian, backed by the ADX and InfluxDB data sources:
    /// <list type="bullet">
    ///   <item><description>POST /opcua/browse - flat list of queryable tags (what Read/HistoryRead can query).</description></item>
    ///   <item><description>POST /opcua/read - latest value(s) of the requested node(s).</description></item>
    ///   <item><description>POST /opcua/historyread - raw historical values over a time range.</description></item>
    /// </list>
    /// The data source (ADX or InfluxDB) is chosen from the <c>DATA_SOURCE</c> environment variable.
    /// </summary>
    [ApiController]
    [Route("opcua")]
    [Authorize(AuthenticationSchemes = BasicAuthenticationHandler.SchemeName)]
    [EnableRateLimiting(RateLimiterPolicies.OpcUaWebApi)]
    [Produces("application/json")]
    public sealed class OpcUaWebApiController : ControllerBase
    {
        private readonly OpcUaDataSourceResolver _resolver;

        public OpcUaWebApiController(OpcUaDataSourceResolver resolver)
        {
            _resolver = resolver;
        }

        /// <summary>
        /// OPC UA Browse service (OPC 10000-4, 5.9.2): returns the flat list of queryable tags
        /// (as expanded NodeIds) that Read and HistoryRead can be called with.
        /// </summary>
        [HttpPost("browse")]
        public ActionResult<BrowseResponse> Browse([FromBody] BrowseRequest? request)
        {
            IOpcUaDataSource source = _resolver.Resolve();

            BrowseResponse response = new()
            {
                ResponseHeader = new ResponseHeader
                {
                    RequestHandle = request?.RequestHeader?.RequestHandle ?? 0
                },
                Results = new List<BrowseResult> { _resolver.Browse(source) }
            };

            return Ok(response);
        }

        /// <summary>
        /// OPC UA Read service (OPC 10000-4, 5.11.2): returns the latest value for each node.
        /// </summary>
        [HttpPost("read")]
        public ActionResult<ReadResponse> Read([FromBody] ReadRequest request)
        {
            IOpcUaDataSource source = _resolver.Resolve();

            ReadResponse response = new()
            {
                ResponseHeader = new ResponseHeader
                {
                    RequestHandle = request?.RequestHeader?.RequestHandle ?? 0
                },
                Results = new List<DataValue>()
            };

            if (request?.NodesToRead != null)
            {
                foreach (ReadValueId nodeToRead in request.NodesToRead)
                {
                    if (string.IsNullOrEmpty(nodeToRead.NodeId))
                    {
                        response.Results.Add(new DataValue { StatusCode = OpcUaStatusCodes.BadNoData });
                        continue;
                    }

                    response.Results.Add(_resolver.ReadLatest(source, nodeToRead.NodeId));
                }
            }

            return Ok(response);
        }

        /// <summary>
        /// OPC UA HistoryRead service (OPC 10000-4, 5.11.3) with ReadRawModifiedDetails: returns
        /// the raw historical values for each node over the requested time range.
        /// </summary>
        [HttpPost("historyread")]
        public ActionResult<HistoryReadResponse> HistoryRead([FromBody] HistoryReadRequest request)
        {
            IOpcUaDataSource source = _resolver.Resolve();

            ReadRawModifiedDetails details = request?.HistoryReadDetails ?? new ReadRawModifiedDetails();
            DateTime startTime = details.StartTime == default ? DateTime.MinValue : details.StartTime;
            DateTime endTime = details.EndTime == default ? DateTime.MaxValue : details.EndTime;

            HistoryReadResponse response = new()
            {
                ResponseHeader = new ResponseHeader
                {
                    RequestHandle = request?.RequestHeader?.RequestHandle ?? 0
                },
                Results = new List<HistoryReadResult>()
            };

            if (request?.NodesToRead != null)
            {
                foreach (HistoryReadValueId nodeToRead in request.NodesToRead)
                {
                    if (string.IsNullOrEmpty(nodeToRead.NodeId))
                    {
                        response.Results.Add(new HistoryReadResult
                        {
                            StatusCode = OpcUaStatusCodes.BadNoData,
                            HistoryData = new HistoryData()
                        });

                        continue;
                    }

                    response.Results.Add(_resolver.ReadHistory(source, nodeToRead.NodeId, startTime, endTime, details.NumValuesPerNode));
                }
            }

            return Ok(response);
        }
    }
}

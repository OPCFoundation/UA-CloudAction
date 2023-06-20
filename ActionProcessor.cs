
namespace UACloudAction
{
    using Confluent.Kafka;
    using Kusto.Data;
    using Kusto.Data.Common;
    using Kusto.Data.Net.Client;
    using Newtonsoft.Json;
    using System.Data;
    using System.Text;

    public class ActionProcessor
    {
        public bool Running { get; set; } = false;

        public bool ConnectionToADX { get; set; } = false;

        public bool ConnectionToBroker { get; set; } = false;

        public bool ConnectionToUACloudCommander { get; set; } = false;

        private ICslQueryProvider? _queryProvider = null;
        private IProducer<Null, string>? _producer = null;
        private IConsumer<Ignore, byte[]>? _consumer = null;

        private void RunADXQuery(string query, Dictionary<string, object> values, bool allowMultiRow = false)
        {
            ClientRequestProperties clientRequestProperties = new ClientRequestProperties()
            {
                ClientRequestId = Guid.NewGuid().ToString()
            };

            try
            {
                using (IDataReader? reader = _queryProvider?.ExecuteQuery(query, clientRequestProperties))
                {
                    while ((reader != null) && reader.Read())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            try
                            {
                                if (reader.GetValue(i) != null)
                                {
                                    if (!allowMultiRow)
                                    {
                                        if (values.ContainsKey(reader.GetName(i)))
                                        {
                                            values[reader.GetName(i)] = reader.GetValue(i);
                                        }
                                        else
                                        {
                                            values.TryAdd(reader.GetName(i), reader.GetValue(i));
                                        }
                                    }
                                    else
                                    {
                                        string? value = reader.GetValue(i).ToString();
                                        if (value != null)
                                        {
                                            if (values.ContainsKey(value))
                                            {
                                                values[value] = reader.GetValue(i);
                                            }
                                            else
                                            {
                                                values.TryAdd(value, reader.GetValue(i));
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.Message);

                                // ignore this field and move on
                            }
                        }

                        if (!allowMultiRow && (values.Count > 0))
                        {
                            // we got a value
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        public void Run()
        {
            Running = true;

            while (true)
            {
                // we run every 15 seconds
                Thread.Sleep(15000);

                try
                {
                    string? applicationClientId = Environment.GetEnvironmentVariable("APPLICATION_ID");
                    string? applicationKey = Environment.GetEnvironmentVariable("APPLICATION_KEY");
                    string? adxInstanceURL = Environment.GetEnvironmentVariable("ADX_INSTANCE_URL");
                    string? adxDatabaseName = Environment.GetEnvironmentVariable("ADX_DB_NAME");
                    string? adxTableName = Environment.GetEnvironmentVariable("ADX_TABLE_NAME");
                    string? tenantId = Environment.GetEnvironmentVariable("AAD_TENANT_ID");
                    string? uaServerApplicationName = Environment.GetEnvironmentVariable("UA_SERVER_APPLICATION_NAME");
                    string? uaServerLocationName = Environment.GetEnvironmentVariable("UA_SERVER_LOCATION_NAME");

                    // acquire access to ADX token Kusto SDK
                    if (!string.IsNullOrEmpty(adxInstanceURL) && !string.IsNullOrEmpty(adxDatabaseName) && !string.IsNullOrEmpty(applicationClientId))
                    {
                        KustoConnectionStringBuilder connectionString;
                        if (!string.IsNullOrEmpty(applicationKey) && !string.IsNullOrEmpty(tenantId))
                        {
                            connectionString = new KustoConnectionStringBuilder(adxInstanceURL.Replace("https://", string.Empty), adxDatabaseName).WithAadApplicationKeyAuthentication(applicationClientId, applicationKey, tenantId);
                        }
                        else
                        {
                            connectionString = new KustoConnectionStringBuilder(adxInstanceURL, adxDatabaseName).WithAadUserManagedIdentity(applicationClientId);
                        }

                        _queryProvider = KustoClientFactory.CreateCslQueryProvider(connectionString);
                        ConnectionToADX = (_queryProvider != null);
                    }
                    else
                    {
                        Console.WriteLine("Environment variables not set!");
                    }

                    // call ADX REST endpoint with query
                    string query = "opcua_metadata_lkv"
                                 + "| where Name contains '" + uaServerApplicationName + "'"
                                 + "| where Name contains '" + uaServerLocationName + "'"
                                 + "| join kind = inner(opcua_telemetry"
                                 + "    | where Name == 'Pressure'"
                                 + "    | where Timestamp > now() - 10m" // TimeStamp is when the data was generated in the UA server, so we take cloud ingestion time into account!"
                                 + ") on DataSetWriterID"
                                 + "| extend NodeValue = toint(Value)"
                                 + "| project Timestamp, NodeValue"
                                 + "| order by Timestamp desc"
                                 + "| where NodeValue > 4000";

                    Dictionary<string, object> _values = new Dictionary<string, object>();
                    RunADXQuery(query, _values);

                    if ((_values.Count > 1) && _values.ContainsKey("NodeValue"))
                    {
                        Console.WriteLine("High pressure detected: " + _values["NodeValue"].ToString());

                        // call OPC UA method on UA Server via UACommander via Event Hubs
                        RequestModel request = new()
                        {
                            Command = "methodcall",
                            TimeStamp = DateTime.UtcNow,
                            CorrelationId = Guid.NewGuid(),
                            Endpoint = Environment.GetEnvironmentVariable("UA_SERVER_ENDPOINT"),
                            MethodNodeId = Environment.GetEnvironmentVariable("UA_SERVER_METHOD_ID"),
                            ParentNodeId = Environment.GetEnvironmentVariable("UA_SERVER_OBJECT_ID")
                        };

                        // create Kafka client
                        var config = new ProducerConfig
                        {
                            BootstrapServers = Environment.GetEnvironmentVariable("BROKER_NAME") + ":9093",
                            MessageTimeoutMs = 10000,
                            SecurityProtocol = SecurityProtocol.SaslSsl,
                            SaslMechanism = SaslMechanism.Plain,
                            SaslUsername = Environment.GetEnvironmentVariable("BROKER_USERNAME"),
                            SaslPassword = Environment.GetEnvironmentVariable("BROKER_PASSWORD"),
                        };

                        _producer = new ProducerBuilder<Null, string>(config).Build();

                        var conf = new ConsumerConfig
                        {
                            GroupId = Guid.NewGuid().ToString(),
                            BootstrapServers = Environment.GetEnvironmentVariable("BROKER_NAME") + ":9093",
                            AutoOffsetReset = AutoOffsetReset.Earliest,
                            SecurityProtocol = SecurityProtocol.SaslSsl,
                            SaslMechanism = SaslMechanism.Plain,
                            SaslUsername = Environment.GetEnvironmentVariable("BROKER_USERNAME"),
                            SaslPassword = Environment.GetEnvironmentVariable("BROKER_PASSWORD")
                        };

                        _consumer = new ConsumerBuilder<Ignore, byte[]>(conf).Build();

                        _consumer.Subscribe(Environment.GetEnvironmentVariable("RESPONSE_TOPIC"));

                        Message<Null, string> message = new()
                        {
                            Headers = new Headers() { { "Content-Type", Encoding.UTF8.GetBytes("application/json") } },
                            Value = JsonConvert.SerializeObject(request)
                        };
                        _producer.ProduceAsync(Environment.GetEnvironmentVariable("TOPIC"), message).GetAwaiter().GetResult();

                        ConnectionToBroker = true;

                        Console.WriteLine($"Sent command {JsonConvert.SerializeObject(request)} to UA Cloud Commander.");

                        // wait for up to 15 seconds for the response
                        while (true)
                        {
                            ConsumeResult<Ignore, byte[]> result = _consumer.Consume(15 * 1000);
                            if (result != null)
                            {
                                ResponseModel? response;
                                try
                                {
                                    response = JsonConvert.DeserializeObject<ResponseModel>(Encoding.UTF8.GetString(result.Message.Value));
                                }
                                catch (Exception)
                                {
                                    // ignore message
                                    continue;
                                }

                                ConnectionToUACloudCommander = true;

                                if (response?.CorrelationId == request.CorrelationId)
                                {
                                    if (response.Success)
                                    {
                                        Console.WriteLine("Command successfully sent!");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"Response received but result is failure: {response.Status}.");
                                    }

                                    break;
                                }
                            }
                            else
                            {
                                Console.WriteLine("Timeout waiting for response from UA Cloud Commander");
                                break;
                            }
                        }

                        _consumer.Unsubscribe();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);

                    ConnectionToADX = false;
                    ConnectionToBroker = false;
                    ConnectionToUACloudCommander = false;
                }
                finally
                {
                    if (_producer != null)
                    {
                        _producer.Dispose();
                    }

                    if (_consumer != null)
                    {
                        _consumer.Dispose();
                    }

                    if (_queryProvider != null)
                    {
                        _queryProvider.Dispose();
                    }
                }
            }
        }
    }
}

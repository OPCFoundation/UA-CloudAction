
namespace UACloudAction
{
    using Confluent.Kafka;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System.Text;

    public class ActionProcessor
    {
        HttpClient webClient = new HttpClient();
        IProducer<Null, string>? producer = null;
        IConsumer<Ignore, byte[]>? consumer = null;

        public void Run()
        {

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

                    // acquire OAuth2 token via AAD REST endpoint
                    webClient = new HttpClient();
                    webClient.DefaultRequestHeaders.Add("Accept", "application/json");

                    string content = $"grant_type=client_credentials&resource={adxInstanceURL}&client_id={applicationClientId}&client_secret={applicationKey}";
                    HttpResponseMessage responseMessage = webClient.Send(new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/" + tenantId + "/oauth2/token")
                    {
                        Content = new StringContent(content, Encoding.UTF8, "application/x-www-form-urlencoded")
                    });

                    string restResponse = responseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult();

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

                    webClient.DefaultRequestHeaders.Remove("Accept");
                    webClient.DefaultRequestHeaders.Add("Authorization", "bearer " + JObject.Parse(restResponse)["access_token"]?.ToString());
                    responseMessage = webClient.Send(new HttpRequestMessage(HttpMethod.Post, adxInstanceURL + "/v2/rest/query")
                    {
                        Content = new StringContent("{ \"db\":\"" + adxDatabaseName + "\", \"csl\":\"" + query + "\" }", Encoding.UTF8, "application/json")
                    });

                    restResponse = responseMessage.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (restResponse.Contains("dtmi:digitaltwins:opcua:node:double"))
                    {
                        Console.WriteLine("High pressure detected!");

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

                        producer = new ProducerBuilder<Null, string>(config).Build();

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

                        consumer = new ConsumerBuilder<Ignore, byte[]>(conf).Build();

                        consumer.Subscribe(Environment.GetEnvironmentVariable("RESPONSE_TOPIC"));

                        Message<Null, string> message = new()
                        {
                            Headers = new Headers() { { "Content-Type", Encoding.UTF8.GetBytes("application/json") } },
                            Value = JsonConvert.SerializeObject(request)
                        };
                        producer.ProduceAsync(Environment.GetEnvironmentVariable("TOPIC"), message).GetAwaiter().GetResult();

                        Console.WriteLine($"Sent command {JsonConvert.SerializeObject(request)} to UA Cloud Commander.");

                        // wait for up to 15 seconds for the response
                        while (true)
                        {
                            ConsumeResult<Ignore, byte[]> result = consumer.Consume(15 * 1000);
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

                        consumer.Unsubscribe();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
                finally
                {
                    if (producer != null)
                    {
                        producer.Dispose();
                    }

                    if (consumer != null)
                    {
                        consumer.Dispose();
                    }

                    if (webClient != null)
                    {
                        webClient.Dispose();
                    }
                }
            }
        }
    }
}

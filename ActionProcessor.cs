
namespace UACloudAction
{
    using Confluent.Kafka;
    using Kusto.Data;
    using Kusto.Data.Common;
    using Kusto.Data.Net.Client;
    using MQTTnet;
    using MQTTnet.Formatter;
    using MQTTnet.Protocol;
    using System.Buffers;
    using System.Data;
    using System.Net.Security;
    using System.Security.Authentication;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class ActionProcessor
    {
        public bool Running { get; set; } = false;

        public bool ConnectionToADX { get; set; } = false;

        public bool ConnectionToBroker { get; set; } = false;

        public bool ConnectionToUACloudCommander { get; set; } = false;

        private ICslQueryProvider? _queryProvider = null;
        private IProducer<Null, string>? _producer = null;
        private IConsumer<Ignore, byte[]>? _consumer = null;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

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
                                 + "| where DataSetName contains '" + uaServerApplicationName + "'"
                                 + "| where DataSetName contains '" + uaServerLocationName + "'"
                                 + "| join kind = inner(opcua_telemetry"
                                 + "    | where Name == 'Pressure'"
                                 + "    | where Timestamp > now() - 1m" // TimeStamp is when the data was generated in the UA server, so we take cloud ingestion time into account!"
                                 + ") on Subject"
                                 + "| extend NodeValue = toint(Value)"
                                 + "| project Timestamp, NodeValue"
                                 + "| order by Timestamp desc"
                                 + "| where NodeValue > 4000";

                    Dictionary<string, object> _values = new Dictionary<string, object>();
                    RunADXQuery(query, _values);

                    if ((_values.Count > 1) && _values.ContainsKey("NodeValue"))
                    {
                        Console.WriteLine("High pressure detected: " + _values["NodeValue"].ToString());

                        ActionNetworkMessage request = BuildActionRequest(out byte[] correlationData);
                        string requestJson = JsonSerializer.Serialize(request, _jsonOptions);

                        // select the transport used to reach UA Cloud Commander: "MQTT" (or "AIO") targets a
                        // Commander running in Azure IoT Operations, "Kafka" (default) uses Azure Event Hubs
                        string platform = Environment.GetEnvironmentVariable("MESSAGING_PLATFORM") ?? "Kafka";
                        if (platform.Equals("MQTT", StringComparison.OrdinalIgnoreCase)
                         || platform.Equals("AIO", StringComparison.OrdinalIgnoreCase))
                        {
                            SendRequestViaMqtt(requestJson, correlationData);
                        }
                        else
                        {
                            SendRequestViaKafka(requestJson, correlationData);
                        }
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

        private void SendRequestViaKafka(string requestJson, byte[] correlationData)
        {
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
                Value = requestJson
            };
            _producer.ProduceAsync(Environment.GetEnvironmentVariable("TOPIC"), message).GetAwaiter().GetResult();

            ConnectionToBroker = true;

            Console.WriteLine($"Sent ActionRequest {requestJson} to UA Cloud Commander.");

            // wait for up to 15 seconds for the response
            while (true)
            {
                ConsumeResult<Ignore, byte[]> result = _consumer.Consume(15 * 1000);
                if (result != null)
                {
                    if (HandleResponse(Encoding.UTF8.GetString(result.Message.Value), correlationData))
                    {
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

        private void SendRequestViaMqtt(string requestJson, byte[] correlationData)
        {
            // Azure IoT Operations (AIO) uses an MQTT broker as its messaging backbone. When UA Cloud Commander
            // runs in AIO it subscribes to TOPIC and publishes the correlated ActionResponse to RESPONSE_TOPIC.
            string brokerName = Environment.GetEnvironmentVariable("BROKER_NAME") ?? "aio-broker";
            int port = int.TryParse(Environment.GetEnvironmentVariable("MQTT_PORT"), out int parsedPort) ? parsedPort : 8883;
            string? topic = Environment.GetEnvironmentVariable("TOPIC");
            string? responseTopic = Environment.GetEnvironmentVariable("RESPONSE_TOPIC");
            string clientId = Environment.GetEnvironmentVariable("REQUESTOR_ID") ?? "UACloudAction";

            MqttClientFactory factory = new();
            using IMqttClient client = factory.CreateMqttClient();

            MqttClientOptionsBuilder optionsBuilder = new MqttClientOptionsBuilder()
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithClientId(clientId)
                .WithTcpServer(brokerName, port);

            // Authentication: prefer the AIO in-cluster Kubernetes Service Account Token (K8S-SAT) when the
            // projected token is mounted, otherwise fall back to username/password credentials.
            string satTokenFile = Environment.GetEnvironmentVariable("MQTT_SAT_TOKEN_FILE") ?? "/var/run/secrets/tokens/broker-sat";
            string? username = Environment.GetEnvironmentVariable("BROKER_USERNAME");
            string? password = Environment.GetEnvironmentVariable("BROKER_PASSWORD");
            if (File.Exists(satTokenFile))
            {
                optionsBuilder = optionsBuilder.WithEnhancedAuthentication("K8S-SAT", File.ReadAllBytes(satTokenFile));
            }
            else if (!string.IsNullOrEmpty(username))
            {
                optionsBuilder = optionsBuilder.WithCredentials(username, password);
            }

            // TLS: the AIO MQTT broker requires TLS. Trust the AIO CA when it is available on disk.
            bool useTls = !string.Equals(Environment.GetEnvironmentVariable("MQTT_USE_TLS"), "false", StringComparison.OrdinalIgnoreCase);
            if (useTls)
            {
                bool insecure = string.Equals(Environment.GetEnvironmentVariable("MQTT_TLS_INSECURE"), "true", StringComparison.OrdinalIgnoreCase);
                string caFile = Environment.GetEnvironmentVariable("MQTT_CA_FILE") ?? "/var/run/certs/ca.crt";

                X509Certificate2Collection trustedCaCertificates = new();
                if (File.Exists(caFile))
                {
                    trustedCaCertificates.ImportFromPemFile(caFile);
                }

                optionsBuilder = optionsBuilder.WithTlsOptions(tls =>
                {
                    tls.UseTls(true);
                    tls.WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);
                    tls.WithCertificateValidationHandler(context =>
                    {
                        if (insecure)
                        {
                            return true;
                        }

                        if (context.SslPolicyErrors == SslPolicyErrors.None)
                        {
                            return true;
                        }

                        // validate the broker certificate against the supplied AIO CA
                        if ((trustedCaCertificates.Count == 0) || (context.Certificate is not X509Certificate2 brokerCertificate))
                        {
                            return false;
                        }

                        using X509Chain validationChain = new();
                        validationChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        validationChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        validationChain.ChainPolicy.CustomTrustStore.AddRange(trustedCaCertificates);
                        if (context.Chain != null)
                        {
                            foreach (X509ChainElement element in context.Chain.ChainElements)
                            {
                                validationChain.ChainPolicy.ExtraStore.Add(element.Certificate);
                            }
                        }

                        return validationChain.Build(brokerCertificate);
                    });
                });
            }

            using ManualResetEventSlim responseReceived = new(false);

            client.ApplicationMessageReceivedAsync += args =>
            {
                try
                {
                    string responseJson = Encoding.UTF8.GetString(args.ApplicationMessage.Payload.ToArray());
                    if (HandleResponse(responseJson, correlationData))
                    {
                        responseReceived.Set();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }

                return Task.CompletedTask;
            };

            try
            {
                client.ConnectAsync(optionsBuilder.Build()).GetAwaiter().GetResult();

                if (!string.IsNullOrEmpty(responseTopic))
                {
                    client.SubscribeAsync(responseTopic, MqttQualityOfServiceLevel.AtLeastOnce).GetAwaiter().GetResult();
                }

                MqttApplicationMessage message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(Encoding.UTF8.GetBytes(requestJson))
                    .WithContentType("application/json")
                    .WithResponseTopic(responseTopic)
                    .WithCorrelationData(correlationData)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                client.PublishAsync(message).GetAwaiter().GetResult();

                ConnectionToBroker = true;

                Console.WriteLine($"Sent ActionRequest {requestJson} to UA Cloud Commander running in Azure IoT Operations.");

                // wait for up to 15 seconds for the correlated response
                if (!responseReceived.Wait(TimeSpan.FromSeconds(15)))
                {
                    Console.WriteLine("Timeout waiting for response from UA Cloud Commander");
                }
            }
            finally
            {
                if (client.IsConnected)
                {
                    if (!string.IsNullOrEmpty(responseTopic))
                    {
                        client.UnsubscribeAsync(responseTopic).GetAwaiter().GetResult();
                    }

                    client.DisconnectAsync().GetAwaiter().GetResult();
                }
            }
        }

        private ActionNetworkMessage BuildActionRequest(out byte[] correlationData)
        {
            // build a spec-compliant OPC UA PubSub MethodCall ActionRequest (OPC 10000-14, 7.2.5.6)
            correlationData = Guid.NewGuid().ToByteArray();

            return new ActionNetworkMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                MessageType = ActionMessageTypes.Request,
                PublisherId = Environment.GetEnvironmentVariable("UA_CLOUD_COMMANDER_ID") ?? "UACloudCommander", // the Responder
                Timestamp = DateTime.UtcNow,
                ResponseAddress = Environment.GetEnvironmentVariable("RESPONSE_TOPIC"), // where the Responder sends the ActionResponse
                CorrelationData = correlationData,
                RequestorId = Environment.GetEnvironmentVariable("REQUESTOR_ID") ?? "UACloudAction",
                TimeoutHint = 15000,
                Messages = new List<ActionDataSetMessage>
                {
                    new()
                    {
                        DataSetWriterId = 1,
                        ActionTargetId = (ushort)CommanderActionTarget.MethodCall,
                        RequestId = 1,
                        ActionState = ActionState.Executing,
                        Payload = JsonSerializer.SerializeToElement(new
                        {
                            Endpoint = Environment.GetEnvironmentVariable("UA_SERVER_ENDPOINT"),
                            MethodNodeId = Environment.GetEnvironmentVariable("UA_SERVER_METHOD_ID"),
                            ParentNodeId = Environment.GetEnvironmentVariable("UA_SERVER_OBJECT_ID")
                        }, _jsonOptions)
                    }
                }
            };
        }

        private bool HandleResponse(string responseJson, byte[] correlationData)
        {
            ActionNetworkMessage? response;
            try
            {
                response = JsonSerializer.Deserialize<ActionNetworkMessage>(responseJson, _jsonOptions);
            }
            catch (Exception)
            {
                // ignore message
                return false;
            }

            // only process ua-action-response NetworkMessages that correlate to our request
            if ((response == null)
             || (response.MessageType != ActionMessageTypes.Response)
             || (response.CorrelationData == null)
             || !response.CorrelationData.SequenceEqual(correlationData))
            {
                return false;
            }

            ConnectionToUACloudCommander = true;

            ActionDataSetMessage? responseMessage = response.Messages?.FirstOrDefault();
            if ((responseMessage != null) && (responseMessage.Status == 0)) // 0 = StatusCode Good
            {
                string? resultValue = GetPayloadString(responseMessage.Payload, "Result");
                Console.WriteLine($"Command successfully executed! Result: {resultValue}");
            }
            else
            {
                string? error = (responseMessage != null) ? GetPayloadString(responseMessage.Payload, "Error") : null;
                string statusText = "0x" + (responseMessage?.Status ?? 0).ToString("X8");
                Console.WriteLine($"Response received but result is failure: {error ?? statusText}.");
            }

            return true;
        }

        private static string? GetPayloadString(JsonElement payload, string name)
        {
            if ((payload.ValueKind == JsonValueKind.Object) && payload.TryGetProperty(name, out JsonElement value))
            {
                return value.ToString();
            }

            return null;
        }
    }
}

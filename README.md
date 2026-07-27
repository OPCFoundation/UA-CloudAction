# UA-CloudAction

An OPC UA Cloud- or Edge-based Action implementation which queries data from Azure Data Explorer and calls UA Cloud Commander on the Edge - either via Kafka (Azure Event Hubs) or via the Azure IoT Operations (AIO) MQTT broker - using spec-compliant OPC UA PubSub Actions (OPC 10000-14).

The scenario implemented is closely tied to the OPC UA PubSub telemetry stored in Azure Data Explorer, as provided in the [Manufacturing Ontologies](https://github.com/digitaltwinconsortium/ManufacturingOntologies) reference implementation. There, the "digital feedback loop" is implemented by triggering a command on one of the OPC UA servers in the factory simulation from the cloud, based on a OPC UA time-series of a machine reaching a certain threshold (the simulated pressure).

Note: Docker containers are automatically built and available on GitHub under "Packages".

Note: When running on Azure, you need to register UA-CloudAction as an app with Active Directory, see [here](https://learn.microsoft.com/en-us/azure/active-directory/develop/quickstart-register-app).

## Environment Variables that Must be Defined

* ADMIN_USERNAME - the username to use to login to the service
* ADMIN_PASSWORD - the password for the username
* ADX_INSTANCE_URL - the endpoint of your ADX cluster, e.g. https://ontologies.eastus2.kusto.windows.net/
* ADX_DB_NAME - the name of your ADX database
* ADX_TABLE_NAME - the name of your ADX table
* AAD_TENANT_ID - the GUID of your AAD tenant of your Azure subscription
* APPLICATION_KEY - the secret you created during UA-Cloud Action app registration
* APPLICATION_ID - the GUID assigned to the UA-Cloud Action during app registration
* BROKER_NAME - the name of your event hubs namespace, e.g. ontologies-eventhubs.servicebus.windows.net
* BROKER_USERNAME - set to "$ConnectionString"
* BROKER_PASSWORD - the primary key connection string of your event hubs namespace
* TOPIC - set to "commander.command"
* RESPONSE_TOPIC - set to "commander.response"
* UA_SERVER_ENDPOINT - set to "opc.tcp://assembly.seattle/" to open the pressure relief valve of the Seattle assembly machine
* UA_SERVER_METHOD_ID - set to "ns=2;i=435"
* UA_SERVER_OBJECT_ID - set to "ns=2;i=424"
* UA_SERVER_APPLICATION_NAME - set to "assembly"
* UA_SERVER_LOCATION_NAME - set to "seattle"

## Optional Environment Variables

* UA_CLOUD_COMMANDER_ID - the PublisherId of the UA Cloud Commander instance acting as the Action Responder. Defaults to "UACloudCommander". Written to the PublisherId field of the outgoing `ua-action-request` NetworkMessage.
* REQUESTOR_ID - the PublisherId identifying this application as the Action Requestor. Defaults to "UACloudAction".
* MESSAGING_PLATFORM - the transport used to reach UA Cloud Commander. Set to "Kafka" (default) to use Azure Event Hubs, or "MQTT" (alias "AIO") to use the Azure IoT Operations MQTT broker.
* DATA_SOURCE - selects the telemetry data source used to detect the high-pressure trigger. Set to "ADX" (default) to query Azure Data Explorer, or "InfluxDB" (alias "Influx") to query the in-cluster InfluxDB time-series store (see below).
* AZURE_FEDERATED_TOKEN_FILE - when running on the edge (Arc-enabled Kubernetes) with Microsoft Entra Workload Identity, this file is projected by the mutating webhook. When set, ADX is accessed with a token credential (`DefaultAzureCredential`) instead of an application key, so `APPLICATION_KEY`/`AAD_TENANT_ID` are not required. Normally set automatically by the platform.
* KAFKA_TARGET - the intended recipient of the Kafka (Event Hubs) message. Defaults to "UACloudCommander" (sends the full OPC UA PubSub ActionRequest envelope). Set to "AIOCommander" to target an Azure IoT Operations OPC UA connector commander via the AIO Kafka<->MQTTv5 header mapping.
* RESPONSE_MQTT_TOPIC - only used when `KAFKA_TARGET=AIOCommander`. The MQTT v5 Response Topic the AIO commander replies on (the in-cluster topic forwarded by the reverse data flow to the `RESPONSE_TOPIC` Event Hub). Falls back to `RESPONSE_TOPIC` when not set.
* MQTT_TARGET - only used when `MESSAGING_PLATFORM=MQTT`/`AIO`. Defaults to "AIOCommander" (sends the method-arguments object to an AIO OPC UA connector commander RPC endpoint). Set to "UACloudCommander" to instead send the full ActionRequest envelope to a real UA Cloud Commander.
* MQTT_RPC_EXPIRY_SECONDS - only used when `MESSAGING_PLATFORM=MQTT`/`AIO`. The MQTT v5 Message Expiry Interval (in seconds) applied to the RPC request message. Defaults to 30.

## Calling UA Cloud Commander in Azure IoT Operations (AIO)

Set `MESSAGING_PLATFORM` to `MQTT` (or `AIO`) to publish the OPC UA PubSub Action request to a UA Cloud Commander instance running in [Azure IoT Operations](https://learn.microsoft.com/azure/iot-operations/) over its MQTT broker instead of Kafka. The `TOPIC`, `RESPONSE_TOPIC`, and all `UA_SERVER_*` variables are used exactly as they are with Kafka.

When UA-CloudAction runs as a pod inside the same Kubernetes cluster as Azure IoT Operations, the defaults connect to the built-in broker's default listener and authenticate with the projected Kubernetes Service Account Token (SAT). The following variables control the MQTT/AIO connection:

* BROKER_NAME - the MQTT broker hostname. Defaults to "aio-broker" (the AIO in-cluster default listener).
* MQTT_PORT - the MQTT broker port. Defaults to 8883. Use 18883 for the AIO in-cluster default listener.
* BROKER_USERNAME / BROKER_PASSWORD - MQTT username/password credentials. Only used when no SAT token file is present (see MQTT_SAT_TOKEN_FILE).
* MQTT_SAT_TOKEN_FILE - path to the mounted Kubernetes Service Account Token used for the AIO "K8S-SAT" enhanced authentication. Defaults to "/var/run/secrets/tokens/broker-sat". When this file exists it takes precedence over BROKER_USERNAME/BROKER_PASSWORD.
* MQTT_USE_TLS - set to "false" to disable TLS. Defaults to "true" (the AIO MQTT broker requires TLS).
* MQTT_CA_FILE - path to the PEM CA certificate used to validate the broker's TLS certificate. Defaults to "/var/run/certs/ca.crt" (the AIO cluster CA).
* MQTT_TLS_INSECURE - set to "true" to skip broker certificate validation (for testing only). Defaults to "false".

To satisfy the in-cluster defaults, mount a projected service account token (audience `aio-internal`) at `MQTT_SAT_TOKEN_FILE` and the `azure-iot-operations-aio-ca-trust-bundle` config map at `MQTT_CA_FILE` in the UA-CloudAction pod spec.

## Using InfluxDB as the Telemetry Data Source

Set `DATA_SOURCE` to `InfluxDB` (or `Influx`) to query an InfluxDB (Flux) time-series store for the latest value of the configured field over the configured range instead of querying Azure Data Explorer. The value is compared against a threshold in code and, when exceeded, triggers the OPC UA PubSub Action exactly as the ADX path does. The following variables control the InfluxDB connection and query (both `INFLUX_TOKEN` and `INFLUX_FIELD` must be set for the query to run):

* INFLUX_URL - the InfluxDB endpoint. Defaults to "http://influxdb.default.svc.cluster.local:8086".
* INFLUX_TOKEN - the InfluxDB API token. Required (no default); the query is skipped when not set.
* INFLUX_ORG - the InfluxDB organization. Defaults to "iot".
* INFLUX_BUCKET - the InfluxDB bucket to query. Defaults to "mqtt".
* INFLUX_MEASUREMENT - the measurement name to filter on. Defaults to "opcua_pubsub".
* INFLUX_FIELD - the field name to read the latest value from. Required (no default); the query is skipped when not set.
* INFLUX_RANGE - the Flux range start used for the query (e.g. "-1m"). Defaults to "-1m".
* INFLUX_THRESHOLD - the numeric threshold above which the high-value (high-pressure) trigger fires. Defaults to 4000.0.

## Message Format

UA-CloudAction communicates with UA Cloud Commander using the spec-compliant OPC UA PubSub **Actions** request/response pattern defined in [OPC 10000-14](https://reference.opcfoundation.org/Core/Part14/) (PubSub). When a high-pressure condition is detected, it publishes a `ua-action-request` NetworkMessage containing a `MethodCall` Action (`ActionTargetId` 4) to the `TOPIC` and waits for the correlated `ua-action-response` NetworkMessage on the `RESPONSE_TOPIC`. Requests and responses are correlated via the `CorrelationData` field.

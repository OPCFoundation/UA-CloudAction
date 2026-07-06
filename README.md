# UA-CloudAction

An OPC UA Cloud- or Edge-based Action implementation which queries data from Azure Data Explorer and calls UA Cloud Commander on the Edge via Kafka using spec-compliant OPC UA PubSub Actions (OPC 10000-14).

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

## Message Format

UA-CloudAction communicates with UA Cloud Commander using the spec-compliant OPC UA PubSub **Actions** request/response pattern defined in [OPC 10000-14](https://reference.opcfoundation.org/Core/Part14/) (PubSub). When a high-pressure condition is detected, it publishes a `ua-action-request` NetworkMessage containing a `MethodCall` Action (`ActionTargetId` 4) to the `TOPIC` and waits for the correlated `ua-action-response` NetworkMessage on the `RESPONSE_TOPIC`. Requests and responses are correlated via the `CorrelationData` field.

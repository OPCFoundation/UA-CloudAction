# UA-CloudAction

An OPC UA Cloud-based Action reference implementation which calls UA Cloud Commander on the Edge via Kafka.

The scenario implemented is closely tied to the OPC UA PubSub telemetry stored in Azure Data Explorer, as provided in the [Manufacturing Ontologies](https://github.com/digitaltwinconsortium/ManufacturingOntologies) reference implementation. There, the "digital feedback loop" is implemented by triggering a command on one of the OPC UA servers in the factory simulation from the cloud, based on a OPC UA time-series of a machine reaching a certain threshold (the simulated pressure).

Note: Docker containers are automatically built and available under "packages".

Note: When running on Azure, you need to register UA-CloudAction as an app with Active Directory, see [here](https://learn.microsoft.com/en-us/azure/active-directory/develop/quickstart-register-app).

## Environment Variables that Must be Defined

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

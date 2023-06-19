# UA-CloudAction

An OPC UA Cloud-based Action reference implementation which calls UA Cloud Commander on the Edge via Kafka.

The scenario implemented is closely tied to the OPC UA telemetry stored in the Azure Data Explorer, as provided in the [Manufacturing Ontologies](https://github.com/digitaltwinconsortium/ManufacturingOntologies) reference implementation. There, the "digital feedback loop" is implemented by triggering a command on one of the OPC UA servers in the factory simulation from the cloud, based on a OPC UA time-series reaching a certain threshold (the simulated pressure).



namespace UACloudAction.Services
{
    using Azure.Identity;
    using Kusto.Data;
    using Kusto.Data.Net.Client;
    using Kusto.Data.Common;

    /// <summary>
    /// Creates ADX (Kusto) query providers from the shared environment-variable configuration used
    /// across the application (the trigger loop and the OPC UA Web API data source), so the
    /// connection/authentication logic lives in one place.
    /// </summary>
    public static class AdxConnectionFactory
    {
        /// <summary>
        /// Builds an ADX query provider, or returns <c>null</c> (and logs) when ADX is not configured.
        /// Authentication order: application key (client id + secret + tenant), user-assigned managed
        /// identity (client id), then <see cref="DefaultAzureCredential"/> (Workload/Managed Identity
        /// in Azure; Visual Studio / Azure CLI / Azure PowerShell locally). Set AAD_TENANT_ID when the
        /// cluster is in a non-home tenant.
        /// </summary>
        public static ICslQueryProvider? Create()
        {
            string? adxInstanceURL = Environment.GetEnvironmentVariable("ADX_INSTANCE_URL");
            string? adxDatabaseName = Environment.GetEnvironmentVariable("ADX_DB_NAME");
            string? applicationClientId = Environment.GetEnvironmentVariable("APPLICATION_ID");
            string? applicationKey = Environment.GetEnvironmentVariable("APPLICATION_KEY");
            string? tenantId = Environment.GetEnvironmentVariable("AAD_TENANT_ID");
            bool useWorkloadIdentity = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE"));

            if (string.IsNullOrEmpty(adxInstanceURL) || string.IsNullOrEmpty(adxDatabaseName))
            {
                Console.WriteLine("ADX connection not configured. "
                    + $"ADX_INSTANCE_URL={(string.IsNullOrEmpty(adxInstanceURL) ? "<missing>" : adxInstanceURL)}, "
                    + $"ADX_DB_NAME={adxDatabaseName}.");
                return null;
            }

            KustoConnectionStringBuilder connectionString;
            if (!string.IsNullOrEmpty(applicationClientId) && !string.IsNullOrEmpty(applicationKey) && !string.IsNullOrEmpty(tenantId))
            {
                // App registration: client id + secret + tenant.
                connectionString = new KustoConnectionStringBuilder(adxInstanceURL.Replace("https://", string.Empty), adxDatabaseName)
                    .WithAadApplicationKeyAuthentication(applicationClientId, applicationKey, tenantId);
            }
            else if (!useWorkloadIdentity && !string.IsNullOrEmpty(applicationClientId))
            {
                // User-assigned managed identity (its client id).
                connectionString = new KustoConnectionStringBuilder(adxInstanceURL, adxDatabaseName)
                    .WithAadUserManagedIdentity(applicationClientId);
            }
            else
            {
                // DefaultAzureCredential resolves Workload Identity / Managed Identity in Azure, and
                // locally your Visual Studio sign-in, Azure CLI, Azure PowerShell, etc.
                DefaultAzureCredentialOptions credentialOptions = new();
                if (!string.IsNullOrEmpty(tenantId))
                {
                    credentialOptions.TenantId = tenantId;
                    credentialOptions.AdditionallyAllowedTenants.Add(tenantId);
                }

                connectionString = new KustoConnectionStringBuilder(adxInstanceURL, adxDatabaseName)
                    .WithAadAzureTokenCredentialsAuthentication(new DefaultAzureCredential(credentialOptions));
            }

            return KustoClientFactory.CreateCslQueryProvider(connectionString);
        }
    }
}

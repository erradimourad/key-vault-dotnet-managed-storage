﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.Azure.Management.KeyVault;
using Microsoft.Azure.Management.KeyVault.Models;
using Microsoft.Rest;

namespace AzureKeyVaultManagedStorageSamples
{
    /// <summary>
    /// Base class for KeyVault recovery samples.
    /// </summary>
    public class KeyVaultSampleBase
    {
        /// <summary>
        /// Represents the client context - Azure tenant, subscription, identity etc.
        /// </summary>
        protected ClientContext context;

        /// <summary>
        /// KeyVault management (Control Plane) client instance.
        /// </summary>
        public KeyVaultManagementClient ManagementClient { get; private set; }

        /// <summary>
        /// KeyVault data (Data Plane) client instance.
        /// </summary>
        public KeyVaultClient DataClient { get; private set; }

        /// <summary>
        /// Builds a sample object from the specified parameters.
        /// </summary>
        /// <param name="tenantId">Tenant id.</param>
        /// <param name="appId">AAD application id.</param>
        /// <param name="appSecret">AAD application secret.</param>
        /// <param name="subscriptionId">Subscription id.</param>
        /// <param name="resourceGroupName">Resource group name.</param>
        /// <param name="vaultLocation">Vault location.</param>
        /// <param name="vaultName">Vault name.</param>
        /// <param name="storageAccountName">Storage account name</param>
        /// <param name="storageAccountResourceId">Storage account resource id.</param>
        public KeyVaultSampleBase(string tenantId, string appId, string appSecret, string subscriptionId, string resourceGroupName, string vaultLocation, string vaultName, string storageAccountName, string storageAccountResourceId)
        {
            InstantiateSample(tenantId, appId, appSecret, subscriptionId, resourceGroupName, vaultLocation, vaultName, storageAccountName, storageAccountResourceId);
        }

        /// <summary>
        /// Builds a sample object from configuration.
        /// </summary>
        public KeyVaultSampleBase()
        {
            // retrieve parameters from configuration
            var tenantId = ConfigurationManager.AppSettings[SampleConstants.ConfigKeys.TenantId];
            var appSecret = ConfigurationManager.AppSettings[SampleConstants.ConfigKeys.VaultMgmtAppSecret];
            var appId = ConfigurationManager.AppSettings[SampleConstants.ConfigKeys.VaultMgmtAppId];
            var subscriptionId = ConfigurationManager.AppSettings[SampleConstants.ConfigKeys.SubscriptionId];
            var resourceGroupName = ConfigurationManager.AppSettings[SampleConstants.ConfigKeys.ResourceGroupName];
            var vaultLocation = ConfigurationManager.AppSettings[SampleConstants.ConfigKeys.VaultLocation];
            var vaultName = ConfigurationManager.AppSettings[SampleConstants.ConfigKeys.VaultName];
            var storageAccountName = ConfigurationManager.AppSettings[SampleConstants.ConfigKeys.StorageAccountName];
            var storageAccountResourceId = ConfigurationManager.AppSettings[SampleConstants.ConfigKeys.StorageAccountResourceId];

            InstantiateSample(tenantId, appId, appSecret, subscriptionId, resourceGroupName, vaultLocation, vaultName, storageAccountName, storageAccountResourceId);
        }

        private void InstantiateSample(string tenantId, string appId, string appSecret, string subscriptionId, string resourceGroupName, string vaultLocation, string vaultName, string storageAccountName, string storageAccountResourceId)
        {
            context = ClientContext.Build(tenantId, appId, appSecret, subscriptionId, resourceGroupName, vaultLocation, vaultName, storageAccountName, storageAccountResourceId);

            // log in with as the specified service principal for vault management operations
            var serviceCredentials = Task.Run(() => ClientContext.GetServiceCredentialsAsync(tenantId, appId, appSecret)).ConfigureAwait(false).GetAwaiter().GetResult();

            // instantiate the management client
            ManagementClient = new KeyVaultManagementClient(serviceCredentials);
            ManagementClient.SubscriptionId = subscriptionId;

            // instantiate the data client, specifying the user-based access token retrieval callback
            DataClient = new KeyVaultClient(ClientContext.AcquireUserAccessTokenAsync);
        }

        #region utilities
        /// <summary>
        /// Creates a vault with the specified parameters and coordinates.
        /// </summary>
        /// <param name="resourceGroupName"></param>
        /// <param name="vaultName"></param>
        /// <param name="vaultLocation"></param>
        /// <param name="enableSoftDelete"></param>
        /// <param name="enablePurgeProtection"></param>
        /// <returns></returns>
        protected VaultCreateOrUpdateParameters CreateVaultParameters(string resourceGroupName, string vaultName, string vaultLocation, bool enableSoftDelete, bool enablePurgeProtection)
        {
            var properties = new VaultProperties
            {
                TenantId = Guid.Parse(context.TenantId),
                Sku = new Sku(),
                AccessPolicies = new List<AccessPolicyEntry>(),
                EnabledForDeployment = false,
                EnabledForDiskEncryption = false,
                EnabledForTemplateDeployment = false,
                EnableSoftDelete = enableSoftDelete ? (bool?)enableSoftDelete : null,
                CreateMode = CreateMode.Default
            };

            // accessing managed storage account functionality requires a user identity
            // since the login would have to be interactive, it is acceptable to expect that
            // the user has been granted the required roles and permissions in preamble.
            return new VaultCreateOrUpdateParameters(vaultLocation, properties);
        }

        protected async Task<Vault> CreateOrRetrieveVaultAsync(string resourceGroupName, string vaultName, bool enableSoftDelete, bool enablePurgeProtection)
        {
            Vault vault = null;

            try
            {
                // check whether the vault exists
                Console.Write("Checking the existence of the vault...");
                vault = await ManagementClient.Vaults.GetAsync(resourceGroupName, vaultName).ConfigureAwait(false);
                Console.WriteLine("done.");
            }
            catch (Exception e)
            {
                VerifyExpectedARMException(e, HttpStatusCode.NotFound);
            }

            if (vault == null)
            {
                // create a new vault
                var vaultParameters = CreateVaultParameters(resourceGroupName, vaultName, context.PreferredLocation, enableSoftDelete, enablePurgeProtection);

                try
                {
                    // create new soft-delete-enabled vault
                    Console.Write("Vault does not exist; creating...");
                    vault = await ManagementClient.Vaults.CreateOrUpdateAsync(resourceGroupName, vaultName, vaultParameters).ConfigureAwait(false);
                    Console.WriteLine("done.");

                    // wait for the DNS record to propagate; verify properties
                    Console.Write("Waiting for DNS propagation..");
                    Thread.Sleep(10 * 1000);
                    Console.WriteLine("done.");

                    Console.Write("Retrieving newly created vault...");
                    vault = await ManagementClient.Vaults.GetAsync(resourceGroupName, vaultName).ConfigureAwait(false);
                    Console.WriteLine("done.");
                }
                catch (Exception e)
                {
                    Console.WriteLine("Unexpected exception encountered updating or retrieving the vault: {0}", e.Message);
                    throw;
                }
            }

            return vault;
        }

        /// <summary>
        /// Verifies the specified exception is a CloudException, and its status code matches the expected value.
        /// </summary>
        /// <param name="e"></param>
        /// <param name="expectedStatusCode"></param>
        protected static void VerifyExpectedARMException(Exception e, HttpStatusCode expectedStatusCode)
        {
            // verify that the exception is a CloudError one
            var armException = e as Microsoft.Rest.Azure.CloudException;
            if (armException == null)
            {
                Console.WriteLine("Unexpected exception encountered running sample: {0}", e.Message);
                throw e;
            }

            // verify that the exception has the expected status code
            if (armException.Response.StatusCode != expectedStatusCode)
            {
                Console.WriteLine("Encountered unexpected ARM exception; expected status code: {0}, actual: {1}", armException.Response.StatusCode, expectedStatusCode);
                throw e;
            }
        }

        /// <summary>
        /// Retries the specified function, representing an http request, according to the specified policy.
        /// </summary>
        /// <param name="function"></param>
        /// <param name="functionName"></param>
        /// <param name="initialBackoff"></param>
        /// <param name="numAttempts"></param>
        /// <param name="continueOn"></param>
        /// <param name="retryOn"></param>
        /// <param name="abortOn"></param>
        /// <returns></returns>
        public async static Task<HttpOperationResponse> RetryHttpRequestAsync(
            Func<Task<HttpOperationResponse>> function,
            string functionName,
            int initialBackoff,
            int numAttempts,
            HashSet<HttpStatusCode> continueOn,
            HashSet<HttpStatusCode> retryOn,
            HashSet<HttpStatusCode> abortOn = null)
        {
            HttpOperationResponse response = null;

            for (int idx = 0, backoff = initialBackoff; idx < numAttempts; idx++, backoff <<= 1)
            {
                try
                {
                    response = await function().ConfigureAwait(false);

                    break;
                }
                catch (KeyVaultErrorException kvee)
                {
                    var statusCode = kvee.Response.StatusCode;

                    Console.Write("attempt #{0} to {1} returned: {2};", idx, functionName, statusCode);
                    if (continueOn.Contains(statusCode))
                    {
                        Console.WriteLine("{0} is expected, continuing..", statusCode);
                        break;
                    }
                    else if (retryOn.Contains(statusCode))
                    {
                        Console.WriteLine("{0} is retriable, retrying after {1}s..", statusCode, backoff);
                        Thread.Sleep(TimeSpan.FromSeconds(backoff));

                        continue;
                    }
                    else if (abortOn != null && abortOn.Contains(statusCode))
                    {
                        Console.WriteLine("{0} is designated 'abort', terminating..", statusCode);

                        string message = String.Format("status code {0} is designated as 'abort'; terminating request", statusCode);
                        throw new InvalidOperationException(message);
                    }
                    else
                    {
                        Console.WriteLine("handling of {0} is unspecified; retrying after {1}s..", statusCode, backoff);
                        Thread.Sleep(TimeSpan.FromSeconds(backoff));
                    }
                }
            }

            return response;
        }

        /// <summary>
        /// Retries the specified function according to the specified retry policy.
        /// </summary>
        /// <param name="function"></param>
        /// <param name="functionName"></param>
        /// <param name="policy"></param>
        /// <returns></returns>
        public static Task<HttpOperationResponse> RetryHttpRequestAsync(
            Func<Task<HttpOperationResponse>> function,
            string functionName,
            RetryPolicy policy)
        {
            if (policy != null)
                return RetryHttpRequestAsync(function, functionName, policy.InitialBackoff, policy.MaxAttempts, policy.ContinueOn, policy.RetryOn, policy.AbortOn);
            else
                return function();
        }
        #endregion
    }
}

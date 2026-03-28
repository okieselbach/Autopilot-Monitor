using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Centralized Blob Storage service supporting both Managed Identity and connection string authentication.
    /// When AzureStorageAccountName is set, uses DefaultAzureCredential (Managed Identity).
    /// Falls back to AzureBlobStorageConnectionString for local dev or legacy deployments.
    /// </summary>
    public class BlobStorageService
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger<BlobStorageService> _logger;
        private readonly bool _usesManagedIdentity;

        public BlobStorageService(IConfiguration configuration, ILogger<BlobStorageService> logger)
        {
            _logger = logger;

            var storageAccountName = configuration["AzureStorageAccountName"];
            var connectionString = configuration["AzureBlobStorageConnectionString"];

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                var blobUri = new Uri($"https://{storageAccountName}.blob.core.windows.net");
                _blobServiceClient = new BlobServiceClient(blobUri, new DefaultAzureCredential());
                _usesManagedIdentity = true;
                _logger.LogInformation("Blob Storage initialized with Managed Identity (account: {Account})", storageAccountName);
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                _blobServiceClient = new BlobServiceClient(connectionString);
                _usesManagedIdentity = false;
                _logger.LogInformation("Blob Storage initialized with connection string");
            }
            else
            {
                throw new InvalidOperationException(
                    "Blob Storage not configured. Set either 'AzureStorageAccountName' (for Managed Identity) or 'AzureBlobStorageConnectionString'.");
            }
        }

        /// <summary>
        /// Gets a BlobContainerClient for the specified container.
        /// </summary>
        public BlobContainerClient GetContainerClient(string containerName)
        {
            return _blobServiceClient.GetBlobContainerClient(containerName);
        }

        /// <summary>
        /// Generates a time-limited download URL for a blob.
        /// Uses User Delegation SAS for Managed Identity, or the connection string SAS for legacy.
        /// </summary>
        public async Task<string> GetDownloadUrlAsync(string containerName, string blobName, TimeSpan? validity = null)
        {
            var expiresOn = DateTimeOffset.UtcNow.Add(validity ?? TimeSpan.FromMinutes(15));
            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(blobName);

            if (_usesManagedIdentity)
            {
                // Generate User Delegation SAS (no account key needed)
                var delegationKey = await _blobServiceClient.GetUserDelegationKeyAsync(
                    DateTimeOffset.UtcNow.AddMinutes(-5), expiresOn);

                var sasBuilder = new BlobSasBuilder
                {
                    BlobContainerName = containerName,
                    BlobName = blobName,
                    Resource = "b",
                    StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
                    ExpiresOn = expiresOn,
                };
                sasBuilder.SetPermissions(BlobSasPermissions.Read);

                var sasUri = new BlobUriBuilder(blobClient.Uri)
                {
                    Sas = sasBuilder.ToSasQueryParameters(delegationKey, _blobServiceClient.AccountName)
                };

                return sasUri.ToUri().ToString();
            }
            else
            {
                // Connection string with SAS token — URI already contains access token
                return blobClient.Uri.ToString();
            }
        }
    }
}

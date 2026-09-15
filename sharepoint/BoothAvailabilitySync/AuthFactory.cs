using Azure.Core;
using Azure.Identity;
using System.Security.Cryptography.X509Certificates;

namespace BoothAvailabilitySync;

public static class AuthFactory
{
    public static TokenCredential Create(AuthSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.TenantId))
            throw new InvalidOperationException("Auth.TenantId is not configured in appsettings.json.");

        if (string.IsNullOrWhiteSpace(settings.ClientId) ||
            settings.ClientId.Contains("PASTE-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Auth.ClientId is not configured. Create the Entra app described in AUTH-SETUP.md, then paste its Application (client) ID into appsettings.json.");
        }

        if (settings.Mode.Equals("InteractiveBrowser", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
            {
                TenantId = settings.TenantId,
                ClientId = settings.ClientId,
                RedirectUri = new Uri(settings.RedirectUri),
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                {
                    Name = "KWI-BoothAvailabilitySync"
                }
            });
        }

        if (settings.Mode.Equals("Certificate", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
                throw new InvalidOperationException("Certificate authentication is selected but CertificateThumbprint is blank.");

            var storeLocation = settings.CertificateStoreLocation.Equals("LocalMachine", StringComparison.OrdinalIgnoreCase)
                ? StoreLocation.LocalMachine
                : StoreLocation.CurrentUser;

            using var store = new X509Store(StoreName.My, storeLocation);
            store.Open(OpenFlags.ReadOnly);
            var matches = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                settings.CertificateThumbprint,
                validOnly: false);

            if (matches.Count == 0)
                throw new InvalidOperationException(
                    $"Certificate '{settings.CertificateThumbprint}' was not found in {storeLocation}\\My.");

            return new ClientCertificateCredential(
                settings.TenantId,
                settings.ClientId,
                matches[0]);
        }

        throw new InvalidOperationException(
            $"Unsupported Auth.Mode '{settings.Mode}'. Use InteractiveBrowser or Certificate.");
    }
}

using System;

namespace RssApp.Config
{
    /// <summary>
    /// Blob-storage durability (BLOB-DURABILITY-SPEC.md): the writer lease and the
    /// Litestream child that replicates the live database to blob. Bound from
    /// <c>RssAppConfig:Durability</c>. Everything is off until
    /// <see cref="BlobServiceUri"/> or <see cref="ConnectionString"/> is set, which is
    /// how local development and the e2e suite run without a storage account.
    ///
    /// Rollout state (spec section 8): steps 1 and 2. Replication runs beside the file
    /// backup and the lease is taken beside the lock file; the share stays authoritative
    /// and nothing restores from blob yet.
    /// </summary>
    public class DurabilityConfig
    {
        /// <summary>
        /// Blob service endpoint of the storage account, e.g. <c>https://acct.blob.core.windows.net</c>.
        /// Reached with the app's managed identity (DefaultAzureCredential). Empty disables the lease.
        /// </summary>
        public string BlobServiceUri { get; set; } = string.Empty;

        /// <summary>
        /// Storage connection string; used instead of <see cref="BlobServiceUri"/> when set.
        /// For Azurite in tests and the Docker durability test, never for production.
        /// </summary>
        public string ConnectionString { get; set; } = string.Empty;

        /// <summary>Blob container holding the writer lease blob.</summary>
        public string LeaseContainer { get; set; } = "litestream";

        /// <summary>Name of the blob whose lease is the writer lock (spec 3.4).</summary>
        public string LeaseBlob { get; set; } = "writer-lease";

        /// <summary>Lease duration; the service allows 15 to 60 seconds. Expires server-side without a release.</summary>
        public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>How often the held lease is renewed; keep at a third of <see cref="LeaseDuration"/>.</summary>
        public TimeSpan LeaseRenewInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long the acquire keeps retrying when the blob service answers with an error
        /// other than "lease already present" before the writer carries on without a lease
        /// (logged as a warning). Contention never times out.
        /// </summary>
        public TimeSpan LeaseGiveUpAfter { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Run <c>litestream replicate</c> as a child of the app once the live database is
        /// in place, replicating it to the blob replica in <see cref="LitestreamConfigPath"/>.
        /// </summary>
        public bool LitestreamEnabled { get; set; }

        /// <summary>Litestream executable; on PATH in the image.</summary>
        public string LitestreamBinary { get; set; } = "litestream";

        /// <summary>Litestream configuration file (replica, sync interval, retention).</summary>
        public string LitestreamConfigPath { get; set; } = "/etc/litestream.yml";

        /// <summary>
        /// Upper bound for the final replica sync on shutdown (spec 3.3 step 4). Litestream's
        /// own shutdown sync retries for up to its <c>shutdown-sync-timeout</c> (30 s default)
        /// after the child is told to stop; this bounds how long the host waits for it.
        /// </summary>
        public TimeSpan FinalSyncTimeout { get; set; } = TimeSpan.FromSeconds(20);

        public bool LeaseEnabled => !string.IsNullOrWhiteSpace(BlobServiceUri) || !string.IsNullOrWhiteSpace(ConnectionString);
    }
}

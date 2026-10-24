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
    /// Rollout state (spec section 8): steps 1 and 2 are on; step 3 is built and behind
    /// <see cref="RestoreFromBlob"/>. Replication runs beside the file backup and the
    /// lease is taken beside the lock file. With <see cref="RestoreFromBlob"/> on, startup
    /// restores the live database from the replica instead of copying the share; the file
    /// backup keeps running either way, so the share stays current for a rollback.
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

        /// <summary>
        /// Rollout step 3 (spec 3.2 steps 3 to 5): once the blob lease is held, restore the
        /// live database from the Litestream replica (<c>litestream restore</c>, then
        /// <c>PRAGMA quick_check</c>) instead of copying the share. Needs
        /// <see cref="LitestreamEnabled"/> and the lease; a failed restore falls back to the
        /// share copy with an error logged, so the flag is safe to flip and to flip back.
        /// </summary>
        public bool RestoreFromBlob { get; set; }

        /// <summary>
        /// Upper bound for <c>litestream restore</c> at startup. The target is single-digit
        /// seconds in region at 320 MB; past this the restore is abandoned and the share
        /// copy is used instead.
        /// </summary>
        public TimeSpan RestoreTimeout { get; set; } = TimeSpan.FromSeconds(120);

        /// <summary>True when startup should try the replica first (spec 3.2 step 4).</summary>
        public bool RestoreEnabled => RestoreFromBlob && LitestreamEnabled && LeaseEnabled;

        // Cooperative handover (spec 3.2 "v2" plus a handoff marker). All under RestoreFromBlob.

        /// <summary>Blob in <see cref="LeaseContainer"/> carrying the handoff request and its acknowledgement.</summary>
        public string HandoffBlob { get; set; } = "writer-handoff";

        /// <summary>How often the running writer looks for a handoff request.</summary>
        public TimeSpan HandoffPollInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>How often the new writer polls the lease while a handover is pending.</summary>
        public TimeSpan HandoffLeasePollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>Bound on stopping the feed scheduler and worker when a handoff is requested.</summary>
        public TimeSpan HandoffDrainTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>Polling interval of <c>litestream restore -f</c> on the new writer.</summary>
        public TimeSpan FollowInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// After the lease is held, how long the new writer waits for the follower to reach the
        /// transaction id the old writer recorded (or, with no acknowledgement, to go quiet)
        /// before giving up on the followed copy and taking a fresh full restore.
        /// </summary>
        public TimeSpan CatchUpTimeout { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>With no acknowledgement to compare against, the follower counts as caught up once its txid has not moved for this long.</summary>
        public TimeSpan CatchUpQuietPeriod { get; set; } = TimeSpan.FromSeconds(2);

        public bool LeaseEnabled => !string.IsNullOrWhiteSpace(BlobServiceUri) || !string.IsNullOrWhiteSpace(ConnectionString);
    }
}

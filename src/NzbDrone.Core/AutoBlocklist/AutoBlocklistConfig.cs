using System.Collections.Generic;

namespace NzbDrone.Core.AutoBlocklist
{
    // Static defaults. Promote to IConfigService entries once the UI is wired.
    public static class AutoBlocklistConfig
    {
        public static bool Enabled => true;

        // Number of hours a download that HAS made some progress may sit idle
        // before it's considered stalled and auto-blocklisted. Distinct from
        // Sonarr's "remove stalled" — this one ALSO blocklists the release.
        public static int StallThresholdHours => 6;

        // A download that has made ZERO progress since it first appeared is
        // almost certainly never going to complete on a debrid backend:
        // the torrent either caches near-instantly (progress within minutes) or
        // — uncached with no seeders — never moves at all. Fail those fast
        // instead of holding a queue slot for StallThresholdHours. Applies only
        // while the download has never progressed; the moment it shows ANY
        // progress it earns the full StallThresholdHours window.
        public static int EarlyStallThresholdMinutes => 20;

        // Number of import failures (same DownloadId) before the release
        // is auto-blocklisted.
        public static int MaxImportRetries => 3;

        // Distinctive substrings that mark a download client error message as
        // permanent. These are specific enough that a plain (case-insensitive)
        // substring match won't false-positive on normal status text.
        public static IReadOnlyList<string> PermanentErrorMarkers { get; } = new[]
        {
            // RD's machine-readable codes
            "infringing_file",
            "unknown_resource",
            "permission_denied",

            // Human-readable variants surfaced by rdt-client to Sonarr's qBit shim.
            // NOTE: do NOT add "could not add to provider" — that's the generic
            // wrapper rdt-client prefixes to BOTH permanent and transient failures
            // (e.g. "Could not add to provider: A task was canceled."), so matching
            // it would blocklist recoverable errors. Match the specific reason.
            "infringing",
            "copyright",
        };

        // HTTP status codes that mark a permanent failure. Matched only when
        // they appear in an error/status context (e.g. "HTTP 404", "error 451",
        // "status code: 403") — never as a bare 3-digit substring, which would
        // otherwise false-positive on byte counts, ports, IDs, etc. in the
        // free-text message of unrelated, healthy downloads.
        public static string PermanentErrorCodePattern { get; } =
            @"(?i)\b(?:https?(?:/\d(?:\.\d)?)?|status(?:\s*code)?|error(?:\s*code)?|code|response)\b[\s:=#-]*\b(?:403|404|451)\b";
    }
}

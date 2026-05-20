using System.Collections.Generic;

namespace NzbDrone.Core.AutoBlocklist
{
    // Static defaults. Promote to IConfigService entries once the UI is wired.
    public static class AutoBlocklistConfig
    {
        public static bool Enabled => true;

        // Number of hours a download must show zero progress before it is
        // considered stalled and auto-blocklisted. Distinct from Sonarr's
        // "remove stalled" — this one ALSO blocklists the release.
        public static int StallThresholdHours => 6;

        // Number of import failures (same DownloadId) before the release
        // is auto-blocklisted.
        public static int MaxImportRetries => 3;

        // Substrings that mark a download client error message as permanent.
        // Match Real-Debrid's terminal codes plus a generic catch-all.
        public static IReadOnlyList<string> PermanentErrorMarkers { get; } = new[]
        {
            // RD's machine-readable codes
            "infringing_file",
            "unknown_resource",
            "permission_denied",
            "451",
            "403",
            "404",
            // Human-readable variants surfaced by rdt-client to Sonarr's qBit shim
            "infringing",
            "could not add to provider",
            "copyright",
        };
    }
}

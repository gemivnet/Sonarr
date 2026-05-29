namespace NzbDrone.Core.SeasonSplit
{
    // Fork toggle for the "grab multi-season packs as a single download" behaviour.
    // Mainline rejects multi-season releases at both the grab spec and the import
    // spec; this fork accepts them and lets Sonarr's per-file import map each file
    // to the right episode across all the pack's seasons (one download per pack,
    // no per-season fan-out). Kept as a static default so the core edits that read
    // it stay one-liners and are trivially upstreamable to an IConfigService
    // setting later.
    public static class SeasonSplitConfig
    {
        public static bool AllowMultiSeasonPacks => true;
    }
}

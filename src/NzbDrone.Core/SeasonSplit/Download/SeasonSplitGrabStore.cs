using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NLog;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Core.SeasonSplit.Download
{
    public interface ISeasonSplitGrabStore
    {
        void Put(SeasonSplitGrab grab);
        SeasonSplitGrab GetByGuid(string syntheticGuid);
        SeasonSplitGrab GetBySyntheticHash(string syntheticInfoHash);
        IReadOnlyList<SeasonSplitGrab> SiblingsOf(string realInfoHash);
        void Delete(string syntheticGuid);
    }

    // JSON-file-backed store. Lives under AppData/seasonsplit-grabs.json.
    // Kept deliberately small — this is fork-only state; if we want a proper
    // table later, add a migration and swap the implementation.
    public sealed class SeasonSplitGrabStore : ISeasonSplitGrabStore
    {
        private readonly string _path;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private Dictionary<string, SeasonSplitGrab> _byGuid;

        public SeasonSplitGrabStore(IAppFolderInfo appFolderInfo, Logger logger)
        {
            _path = Path.Combine(appFolderInfo.AppDataFolder, "seasonsplit-grabs.json");
            _logger = logger;
            _byGuid = Load();
            _logger.Info("[SeasonSplit] Grab store ready at {0} ({1} existing grabs loaded)", _path, _byGuid.Count);
        }

        public void Put(SeasonSplitGrab grab)
        {
            lock (_lock)
            {
                var existed = _byGuid.ContainsKey(grab.SyntheticGuid);
                _byGuid[grab.SyntheticGuid] = grab;
                Persist();
                _logger.Debug("[SeasonSplit] Store {0} grab guid={1} season=S{2:D2} real={3}", existed ? "updated" : "added", grab.SyntheticGuid, grab.Season, grab.RealInfoHash);
            }
        }

        public SeasonSplitGrab GetByGuid(string syntheticGuid)
        {
            lock (_lock)
            {
                return _byGuid.TryGetValue(syntheticGuid, out var g) ? g : null;
            }
        }

        public SeasonSplitGrab GetBySyntheticHash(string syntheticInfoHash)
        {
            lock (_lock)
            {
                return _byGuid.Values.FirstOrDefault(g =>
                    string.Equals(g.SyntheticInfoHash, syntheticInfoHash, System.StringComparison.OrdinalIgnoreCase));
            }
        }

        public IReadOnlyList<SeasonSplitGrab> SiblingsOf(string realInfoHash)
        {
            lock (_lock)
            {
                return _byGuid.Values
                    .Where(g => string.Equals(g.RealInfoHash, realInfoHash, System.StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        public void Delete(string syntheticGuid)
        {
            lock (_lock)
            {
                if (_byGuid.Remove(syntheticGuid))
                {
                    Persist();
                }
            }
        }

        private Dictionary<string, SeasonSplitGrab> Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new Dictionary<string, SeasonSplitGrab>();
                }

                var json = File.ReadAllText(_path);
                var list = JsonSerializer.Deserialize<List<SeasonSplitGrab>>(json) ?? new List<SeasonSplitGrab>();

                // Tolerate a corrupt/hand-edited file: skip entries with no
                // guid and keep the last entry on duplicate guids, rather than
                // letting ToDictionary throw and wipe the entire store (which
                // would drop every in-flight season mapping).
                return list
                    .Where(g => g != null && !string.IsNullOrEmpty(g.SyntheticGuid))
                    .GroupBy(g => g.SyntheticGuid)
                    .ToDictionary(grp => grp.Key, grp => grp.Last());
            }
            catch (System.Exception ex)
            {
                _logger.Warn(ex, "Failed to load season-split grab store; starting empty");
                return new Dictionary<string, SeasonSplitGrab>();
            }
        }

        private void Persist()
        {
            try
            {
                var json = JsonSerializer.Serialize(_byGuid.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
            catch (System.Exception ex)
            {
                _logger.Warn(ex, "Failed to persist season-split grab store");
            }
        }
    }
}

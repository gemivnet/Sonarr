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
        private readonly object _lock = new();
        private Dictionary<string, SeasonSplitGrab> _byGuid;

        public SeasonSplitGrabStore(IAppFolderInfo appFolderInfo, Logger logger)
        {
            _path = Path.Combine(appFolderInfo.AppDataFolder, "seasonsplit-grabs.json");
            _logger = logger;
            _byGuid = Load();
        }

        public void Put(SeasonSplitGrab grab)
        {
            lock (_lock)
            {
                _byGuid[grab.SyntheticGuid] = grab;
                Persist();
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
                return list.ToDictionary(g => g.SyntheticGuid);
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

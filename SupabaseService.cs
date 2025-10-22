using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Supabase;

namespace ViewTracker
{
    public class SupabaseService
    {
        private Client _supabase;
        private string _url;
        private string _key;
        private bool _initialized = false;

        public SupabaseService()
        {
            // Credentials will be loaded from config file on first InitializeAsync() call
        }

        public async Task InitializeAsync()
        {
            // Only load config if not already initialized
            if (!_initialized)
            {
                try
                {
                    // Load credentials from config file stored in Supabase Storage
                    var config = await ConfigService.GetConfigAsync();
                    _url = config.SupabaseUrl;
                    _key = config.SupabaseKey;
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to load Supabase configuration: {ex.Message}\n\n" +
                        "Please ensure:\n" +
                        "1. The config file exists in Supabase Storage at 'config/supabase-config.json'\n" +
                        "2. You have internet connectivity\n" +
                        "3. The config file is valid JSON with 'supabaseUrl' and 'supabaseKey' properties", ex);
                }
            }

            // Initialize Supabase client
            var options = new SupabaseOptions { AutoConnectRealtime = false };
            _supabase = new Client(_url, _key, options);
            await _supabase.InitializeAsync();
            _initialized = true;
        }

        /// <summary>
        /// Forces a refresh of the configuration and reinitializes the client
        /// </summary>
        public async Task RefreshConfigAndReinitializeAsync()
        {
            _initialized = false;
            await ConfigService.RefreshConfigAsync();
            await InitializeAsync();
        }

        // Fetch activation count for one view
        public async Task<int> GetActivationCountAsync(string viewUniqueId)
        {
            var records = (await _supabase
                .From<ViewActivationRecord>()
                .Where(x => x.ViewUniqueId == viewUniqueId)
                .Get()).Models;
            var record = records.FirstOrDefault();
            return record?.ActivationCount ?? 0;
        }

        // Upsert one record (for event-driven activation tracking)
        public async Task UpsertViewActivationAsync(ViewActivationRecord record)
        {
            try
            {
                await _supabase.From<ViewActivationRecord>().Upsert(record);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error upserting to Supabase: {ex.Message}");
            }
        }

        // Batch upsert, preserving user fields
        public async Task BulkUpsertInitViewsPreserveAsync(List<ViewActivationRecord> newRecords, string fileName)
        {
            if (newRecords == null || !newRecords.Any())
                return;

            try
            {
                var existingRecords = (await _supabase
                        .From<ViewActivationRecord>()
                        .Where(x => x.FileName == fileName)
                        .Get()).Models;

                var existingDict = existingRecords.ToDictionary(x => x.ViewUniqueId, x => x);

                var finalRecords = newRecords
                    .Select(r =>
                    {
                        if (existingDict.TryGetValue(r.ViewUniqueId, out var found))
                        {
                            r.LastViewer = found.LastViewer;
                            r.LastActivationDate = found.LastActivationDate;
                            r.ActivationCount = found.ActivationCount;
                        }
                        // If new, leave defaults
                        return r;
                    })
                    .ToList();

                const int batchSize = 300;
                for (int i = 0; i < finalRecords.Count; i += batchSize)
                {
                    var batch = finalRecords.Skip(i).Take(batchSize).ToList();
                    await _supabase.From<ViewActivationRecord>().Upsert(batch);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error bulk upserting with preservation: {ex.Message}");
            }
        }

        // Get all uniqueIds for a given file
        public async Task<HashSet<string>> GetExistingViewUniqueIdsAsync(string fileName)
        {
            try
            {
                var results = await _supabase
                    .From<ViewActivationRecord>()
                    .Where(x => x.FileName == fileName)
                    .Get();

                return new HashSet<string>(results.Models.Select(r => r.ViewUniqueId));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching existing unique IDs: {ex.Message}");
                return new HashSet<string>();
            }
        }

        // Delete orphans from DB


public async Task<List<ViewActivationRecord>> GetViewActivationsByProjectAsync(Guid projectId)
{
    var allRows = new List<ViewActivationRecord>();
    const int batchSize = 1000;
    int offset = 0;

    while (true)
    {
        var resp = await _supabase
            .From<ViewActivationRecord>()
            .Where(x => x.ProjectId == projectId)
            // Add any secondary ordering for deterministic pagination
            .Order(x => x.FileName, Supabase.Postgrest.Constants.Ordering.Ascending)
            .Order(x => x.ViewUniqueId, Supabase.Postgrest.Constants.Ordering.Ascending)
            .Range(offset, offset + batchSize - 1)
            .Get();

        var batch = resp.Models.ToList();
        allRows.AddRange(batch);

        if (batch.Count < batchSize) break;
        offset += batchSize;
    }

    return allRows;
}

public async Task BulkDeleteOrphanedRecordsAsync(List<string> orphanUniqueIds, string fileName)
{
    if (orphanUniqueIds == null || !orphanUniqueIds.Any()) return;

    try
    {
        foreach (var orphanId in orphanUniqueIds)
        {
            await _supabase.From<ViewActivationRecord>().Delete(new ViewActivationRecord
            {
                ViewUniqueId = orphanId,
                FileName = fileName
            });
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"Error bulk deleting orphaned records: {ex.Message}");
    }
}

        // Optional: Check projectId existence in Projects table
        public async Task<bool> ProjectIdExistsAsync(Guid projectId)
        {
            try
            {
                var projects = await _supabase
                    .From<ProjectRecord>()
                    .Where(x => x.Uuid == projectId)
                    .Get();
                return projects.Models.Any();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking project id: {ex.Message}");
                return false;
            }
        }

        // Room Snapshot Methods

        // Check if trackIDs already exist in the project (for validation before snapshot)
        public async Task<List<string>> GetExistingTrackIdsInProjectAsync(List<string> trackIds, Guid projectId)
        {
            try
            {
                var existingSnapshots = await _supabase
                    .From<RoomSnapshot>()
                    .Where(x => x.ProjectId == projectId)
                    .Get();

                var existingTrackIds = existingSnapshots.Models
                    .Select(s => s.TrackId)
                    .Distinct()
                    .Where(trackId => trackIds.Contains(trackId))
                    .ToList();

                return existingTrackIds;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking existing trackIDs: {ex.Message}");
                return new List<string>();
            }
        }

        // Check if version name already exists
        public async Task<bool> VersionExistsAsync(string versionName, string fileName)
        {
            try
            {
                var results = await _supabase
                    .From<RoomSnapshot>()
                    .Where(x => x.VersionName == versionName && x.FileName == fileName)
                    .Limit(1)
                    .Get();
                return results.Models.Any();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking version: {ex.Message}");
                return false;
            }
        }

        // Get version info (for showing who created it)
        public async Task<RoomSnapshot?> GetVersionInfoAsync(string versionName, string fileName)
        {
            try
            {
                var results = await _supabase
                    .From<RoomSnapshot>()
                    .Where(x => x.VersionName == versionName && x.FileName == fileName)
                    .Limit(1)
                    .Get();
                return results.Models.FirstOrDefault();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting version info: {ex.Message}");
                return null;
            }
        }

        // Batch upsert room snapshots
        public async Task BulkUpsertRoomSnapshotsAsync(List<RoomSnapshot> snapshots)
        {
            if (snapshots == null || !snapshots.Any())
                return;

            try
            {
                const int batchSize = 300;
                for (int i = 0; i < snapshots.Count; i += batchSize)
                {
                    var batch = snapshots.Skip(i).Take(batchSize).ToList();
                    await _supabase.From<RoomSnapshot>().Upsert(batch);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error bulk upserting room snapshots: {ex.Message}");
                throw;
            }
        }

        // Get all unique version names for a project (for dropdown selection)
        public async Task<List<string>> GetAllVersionNamesAsync(Guid projectId)
        {
            try
            {
                var results = await _supabase
                    .From<RoomSnapshot>()
                    .Where(x => x.ProjectId == projectId)
                    .Get();

                return results.Models
                    .Select(r => r.VersionName)
                    .Distinct()
                    .OrderByDescending(v => v)
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting versions: {ex.Message}");
                return new List<string>();
            }
        }

        // Get all versions with detailed info (for version selection UI)
        public async Task<List<RoomSnapshot>> GetAllVersionsWithInfoAsync(Guid projectId)
        {
            try
            {
                var allSnapshots = new List<RoomSnapshot>();
                const int batchSize = 1000;
                int offset = 0;

                // Load all snapshots with pagination
                while (true)
                {
                    var resp = await _supabase
                        .From<RoomSnapshot>()
                        .Where(x => x.ProjectId == projectId)
                        // Add ordering for deterministic pagination
                        .Order(x => x.VersionName, Supabase.Postgrest.Constants.Ordering.Ascending)
                        .Order(x => x.TrackId, Supabase.Postgrest.Constants.Ordering.Ascending)
                        .Range(offset, offset + batchSize - 1)
                        .Get();

                    var batch = resp.Models.ToList();
                    allSnapshots.AddRange(batch);

                    if (batch.Count < batchSize) break;
                    offset += batchSize;
                }

                // Return one snapshot per version (with metadata)
                return allSnapshots
                    .GroupBy(r => r.VersionName)
                    .Select(g => g.First())
                    .OrderByDescending(r => r.SnapshotDate)
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting versions with info: {ex.Message}");
                return new List<RoomSnapshot>();
            }
        }

        // Get all rooms for a specific version and project (with pagination for large datasets)
        public async Task<List<RoomSnapshot>> GetRoomsByVersionAsync(string versionName, Guid projectId)
        {
            try
            {
                var allRooms = new List<RoomSnapshot>();
                const int batchSize = 1000;
                int offset = 0;

                while (true)
                {
                    var resp = await _supabase
                        .From<RoomSnapshot>()
                        .Where(x => x.VersionName == versionName && x.ProjectId == projectId)
                        // Add ordering for deterministic pagination
                        .Order(x => x.TrackId, Supabase.Postgrest.Constants.Ordering.Ascending)
                        .Range(offset, offset + batchSize - 1)
                        .Get();

                    var batch = resp.Models.ToList();
                    allRooms.AddRange(batch);

                    // If we got fewer results than batch size, we've reached the end
                    if (batch.Count < batchSize) break;
                    offset += batchSize;
                }

                return allRooms;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting rooms for version: {ex.Message}");
                return new List<RoomSnapshot>();
            }
        }

        // Get all snapshots for a specific room (by trackID) across all versions
        public async Task<List<RoomSnapshot>> GetRoomHistoryAsync(string trackId, Guid projectId)
        {
            try
            {
                var results = await _supabase
                    .From<RoomSnapshot>()
                    .Where(x => x.TrackId == trackId && x.ProjectId == projectId)
                    .Order(x => x.SnapshotDate, Supabase.Postgrest.Constants.Ordering.Ascending)
                    .Get();

                return results.Models.ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting room history: {ex.Message}");
                return new List<RoomSnapshot>();
            }
        }

        // Door Snapshot Methods

        // Check if door trackIDs already exist in the project (for validation before snapshot)
        public async Task<List<string>> GetExistingDoorTrackIdsInProjectAsync(List<string> trackIds, Guid projectId)
        {
            try
            {
                var existingSnapshots = await _supabase
                    .From<DoorSnapshot>()
                    .Where(x => x.ProjectId == projectId)
                    .Get();

                var existingTrackIds = existingSnapshots.Models
                    .Select(s => s.TrackId)
                    .Distinct()
                    .Where(trackId => trackIds.Contains(trackId))
                    .ToList();

                return existingTrackIds;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking existing door trackIDs: {ex.Message}");
                return new List<string>();
            }
        }

        // Check if door version name already exists
        public async Task<bool> DoorVersionExistsAsync(string versionName, string fileName)
        {
            try
            {
                var results = await _supabase
                    .From<DoorSnapshot>()
                    .Where(x => x.VersionName == versionName && x.FileName == fileName)
                    .Limit(1)
                    .Get();
                return results.Models.Any();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking door version: {ex.Message}");
                return false;
            }
        }

        // Get door version info (for showing who created it)
        public async Task<DoorSnapshot?> GetDoorVersionInfoAsync(string versionName, string fileName)
        {
            try
            {
                var results = await _supabase
                    .From<DoorSnapshot>()
                    .Where(x => x.VersionName == versionName && x.FileName == fileName)
                    .Limit(1)
                    .Get();
                return results.Models.FirstOrDefault();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting door version info: {ex.Message}");
                return null;
            }
        }

        // Batch upsert door snapshots
        public async Task BulkUpsertDoorSnapshotsAsync(List<DoorSnapshot> snapshots)
        {
            if (snapshots == null || !snapshots.Any())
                return;

            try
            {
                const int batchSize = 300;
                for (int i = 0; i < snapshots.Count; i += batchSize)
                {
                    var batch = snapshots.Skip(i).Take(batchSize).ToList();
                    await _supabase.From<DoorSnapshot>().Upsert(batch);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error bulk upserting door snapshots: {ex.Message}");
                throw;
            }
        }

        // Get all unique door version names for a project (for dropdown selection)
        public async Task<List<string>> GetAllDoorVersionNamesAsync(Guid projectId)
        {
            try
            {
                var results = await _supabase
                    .From<DoorSnapshot>()
                    .Where(x => x.ProjectId == projectId)
                    .Get();

                return results.Models
                    .Select(r => r.VersionName)
                    .Distinct()
                    .OrderByDescending(v => v)
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting door versions: {ex.Message}");
                return new List<string>();
            }
        }

        // Get all door versions with detailed info (for version selection UI)
        public async Task<List<DoorSnapshot>> GetAllDoorVersionsWithInfoAsync(Guid projectId)
        {
            try
            {
                var allSnapshots = new List<DoorSnapshot>();
                const int batchSize = 1000;
                int offset = 0;

                // Load all snapshots with pagination
                while (true)
                {
                    var resp = await _supabase
                        .From<DoorSnapshot>()
                        .Where(x => x.ProjectId == projectId)
                        // Add ordering for deterministic pagination
                        .Order(x => x.VersionName, Supabase.Postgrest.Constants.Ordering.Ascending)
                        .Order(x => x.TrackId, Supabase.Postgrest.Constants.Ordering.Ascending)
                        .Range(offset, offset + batchSize - 1)
                        .Get();

                    var batch = resp.Models.ToList();
                    allSnapshots.AddRange(batch);

                    if (batch.Count < batchSize) break;
                    offset += batchSize;
                }

                // Return one snapshot per version (with metadata)
                return allSnapshots
                    .GroupBy(r => r.VersionName)
                    .Select(g => g.First())
                    .OrderByDescending(r => r.SnapshotDate)
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting door versions with info: {ex.Message}");
                return new List<DoorSnapshot>();
            }
        }

        // Get all doors for a specific version and project
        public async Task<List<DoorSnapshot>> GetDoorsByVersionAsync(string versionName, Guid projectId)
        {
            try
            {
                var results = await _supabase
                    .From<DoorSnapshot>()
                    .Where(x => x.VersionName == versionName && x.ProjectId == projectId)
                    .Get();

                return results.Models.ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting doors for version: {ex.Message}");
                return new List<DoorSnapshot>();
            }
        }

        // Get all snapshots for a specific door (by trackID) across all versions
        public async Task<List<DoorSnapshot>> GetDoorHistoryAsync(string trackId, Guid projectId)
        {
            try
            {
                var results = await _supabase
                    .From<DoorSnapshot>()
                    .Where(x => x.TrackId == trackId && x.ProjectId == projectId)
                    .Order(x => x.SnapshotDate, Supabase.Postgrest.Constants.Ordering.Ascending)
                    .Get();

                return results.Models.ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting door history: {ex.Message}");
                return new List<DoorSnapshot>();
            }
        }

        // Element Snapshot Methods

        // Check if element trackIDs already exist in the project (for validation before snapshot)
        public async Task<List<string>> GetExistingElementTrackIdsInProjectAsync(List<string> trackIds, Guid projectId)
        {
            try
            {
                var existingSnapshots = await _supabase
                    .From<ElementSnapshot>()
                    .Where(x => x.ProjectId == projectId)
                    .Get();

                var existingTrackIds = existingSnapshots.Models
                    .Select(s => s.TrackId)
                    .Distinct()
                    .Where(trackId => trackIds.Contains(trackId))
                    .ToList();

                return existingTrackIds;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking existing element trackIDs: {ex.Message}");
                return new List<string>();
            }
        }

        // Check if element version name already exists
        public async Task<bool> ElementVersionExistsAsync(string versionName, string fileName)
        {
            try
            {
                var results = await _supabase
                    .From<ElementSnapshot>()
                    .Where(x => x.VersionName == versionName && x.FileName == fileName)
                    .Limit(1)
                    .Get();
                return results.Models.Any();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking element version: {ex.Message}");
                return false;
            }
        }

        // Get element version info (for showing who created it)
        public async Task<ElementSnapshot?> GetElementVersionInfoAsync(string versionName, string fileName)
        {
            try
            {
                var results = await _supabase
                    .From<ElementSnapshot>()
                    .Where(x => x.VersionName == versionName && x.FileName == fileName)
                    .Limit(1)
                    .Get();
                return results.Models.FirstOrDefault();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting element version info: {ex.Message}");
                return null;
            }
        }

        // Batch upsert element snapshots
        public async Task BulkUpsertElementSnapshotsAsync(List<ElementSnapshot> snapshots)
        {
            if (snapshots == null || !snapshots.Any())
                return;

            try
            {
                const int batchSize = 300;
                for (int i = 0; i < snapshots.Count; i += batchSize)
                {
                    var batch = snapshots.Skip(i).Take(batchSize).ToList();
                    await _supabase.From<ElementSnapshot>().Upsert(batch);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error bulk upserting element snapshots: {ex.Message}");
                throw;
            }
        }

        // Get all unique element version names for a project (for dropdown selection)
        public async Task<List<string>> GetAllElementVersionNamesAsync(Guid projectId)
        {
            try
            {
                var results = await _supabase
                    .From<ElementSnapshot>()
                    .Where(x => x.ProjectId == projectId)
                    .Get();

                return results.Models
                    .Select(r => r.VersionName)
                    .Distinct()
                    .OrderByDescending(v => v)
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting element versions: {ex.Message}");
                return new List<string>();
            }
        }

        // Get all element versions with detailed info (for version selection UI)
        public async Task<List<ElementSnapshot>> GetAllElementVersionsWithInfoAsync(Guid projectId)
        {
            try
            {
                var allSnapshots = new List<ElementSnapshot>();
                const int batchSize = 1000;
                int offset = 0;

                // Load all snapshots with pagination
                while (true)
                {
                    var resp = await _supabase
                        .From<ElementSnapshot>()
                        .Where(x => x.ProjectId == projectId)
                        // Add ordering for deterministic pagination
                        .Order(x => x.VersionName, Supabase.Postgrest.Constants.Ordering.Ascending)
                        .Order(x => x.TrackId, Supabase.Postgrest.Constants.Ordering.Ascending)
                        .Range(offset, offset + batchSize - 1)
                        .Get();

                    var batch = resp.Models.ToList();
                    allSnapshots.AddRange(batch);

                    if (batch.Count < batchSize) break;
                    offset += batchSize;
                }

                // Return one snapshot per version (with metadata)
                return allSnapshots
                    .GroupBy(r => r.VersionName)
                    .Select(g => g.First())
                    .OrderByDescending(r => r.SnapshotDate)
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting element versions with info: {ex.Message}");
                return new List<ElementSnapshot>();
            }
        }

        // Get all elements for a specific version and project
        public async Task<List<ElementSnapshot>> GetElementsByVersionAsync(string versionName, Guid projectId)
        {
            try
            {
                var results = await _supabase
                    .From<ElementSnapshot>()
                    .Where(x => x.VersionName == versionName && x.ProjectId == projectId)
                    .Get();

                return results.Models.ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting elements for version: {ex.Message}");
                return new List<ElementSnapshot>();
            }
        }

        // Get all snapshots for a specific element (by trackID) across all versions
        public async Task<List<ElementSnapshot>> GetElementHistoryAsync(string trackId, Guid projectId)
        {
            try
            {
                var results = await _supabase
                    .From<ElementSnapshot>()
                    .Where(x => x.TrackId == trackId && x.ProjectId == projectId)
                    .Order(x => x.SnapshotDate, Supabase.Postgrest.Constants.Ordering.Ascending)
                    .Get();

                return results.Models.ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting element history: {ex.Message}");
                return new List<ElementSnapshot>();
            }
        }
    }
}

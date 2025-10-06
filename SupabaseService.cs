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
        private readonly string _url;
        private readonly string _key;

        public SupabaseService()
        {
            _url = "https://agexakhxckfvkwnflwxp.supabase.co";
            _key = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImFnZXhha2h4Y2tmdmt3bmZsd3hwIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTg4NDM2MjMsImV4cCI6MjA3NDQxOTYyM30.0LO5K2jehWHgm-Bj6tvIt0Qt8SwmHv39EKa9GBhyHEE";
        }

        public async Task InitializeAsync()
        {
            var options = new SupabaseOptions { AutoConnectRealtime = false };
            _supabase = new Client(_url, _key, options);
            await _supabase.InitializeAsync();
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

        public async Task BulkDeleteOrphanedRecordsAsync(List<string> orphanUniqueIds)
        {
            if (orphanUniqueIds == null || !orphanUniqueIds.Any()) return;

            try
            {
                const int batchSize = 200;
                for (int i = 0; i < orphanUniqueIds.Count; i += batchSize)
                {
                    var batch = orphanUniqueIds.Skip(i).Take(batchSize).ToList();

                    var recordsToDelete = (await _supabase
                        .From<ViewActivationRecord>()
                        .Where(x => batch.Contains(x.ViewUniqueId))
                        .Get()).Models.ToList();

                    foreach (var record in recordsToDelete)
                        await _supabase.From<ViewActivationRecord>().Delete(record);
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
    }
}

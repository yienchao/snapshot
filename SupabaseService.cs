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

        public async Task UpsertViewActivationAsync(
            string fileName,
            string viewUniqueId,
            string viewElementId,
            string viewName,
            string viewType,
            string lastViewer,
            string creatorName,
            string lastChangedBy,
            string sheetNumber,
            string viewNumber)
        {
            try
            {
                var currentDateTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var existingRecord = (await _supabase
                    .From<ViewActivationRecord>()
                    .Where(x => x.ViewUniqueId == viewUniqueId)
                    .Get()).Models.FirstOrDefault();

                var record = new ViewActivationRecord
                {
                    ViewUniqueId = viewUniqueId,
                    FileName = fileName,
                    ViewId = viewElementId,
                    ViewName = viewName,
                    ViewType = viewType,
                    LastViewer = lastViewer,
                    LastActivationDate = currentDateTime,
                    LastInitialization = existingRecord?.LastInitialization ?? currentDateTime,
                    ActivationCount = (existingRecord?.ActivationCount ?? 0) + 1,
                    CreatorName = creatorName,
                    LastChangedBy = lastChangedBy,
                    SheetNumber = sheetNumber,
                    ViewNumber = viewNumber
                };

                await _supabase.From<ViewActivationRecord>().Upsert(record);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error upserting to Supabase: {ex.Message}");
            }
        }

        // ----------- NEW: Batch Upsert that preserves last_viewer/count/activation_date -----------

        public async Task BulkUpsertInitViewsPreserveAsync(List<ViewActivationRecord> newRecords, string fileName)
        {
            if (newRecords == null || !newRecords.Any())
                return;

            try
            {
                // Get all existing records for this file name in one call
                var existingRecords = (await _supabase
                        .From<ViewActivationRecord>()
                        .Where(x => x.FileName == fileName)
                        .Get()).Models;

                var existingDict = existingRecords.ToDictionary(x => x.ViewUniqueId, x => x);

                // Project: keep new info for each, fill in user fields from existing
                var finalRecords = newRecords
                    .Select(r =>
                    {
                        if (existingDict.TryGetValue(r.ViewUniqueId, out var found))
                        {
                            r.LastViewer = found.LastViewer;
                            r.LastActivationDate = found.LastActivationDate;
                            r.ActivationCount = found.ActivationCount;
                        }
                        // else leave at default/null/zero (new view)
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
    }
}

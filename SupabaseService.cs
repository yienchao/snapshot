using System;
using System.Collections.Generic;
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

        public async Task UpsertViewActivationAsync(string fileName, string viewUniqueId, int viewElementId, string viewName, string viewType, string lastViewer)
        {
            try
            {
                var currentDateTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var existingRecord = await _supabase
                    .From<ViewActivationRecord>()
                    .Where(x => x.ViewUniqueId == viewUniqueId)
                    .Single();

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
                    ActivationCount = (existingRecord?.ActivationCount ?? 0) + 1
                };

                await _supabase.From<ViewActivationRecord>().Upsert(record);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error upserting to Supabase: {ex.Message}");
            }
        }

        public async Task InitializeOrUpdateViewAsync(string fileName, string viewUniqueId, int viewElementId, string viewName, string viewType, string lastViewer)
        {
            try
            {
                var currentDateTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var existingRecord = await _supabase
                    .From<ViewActivationRecord>()
                    .Where(x => x.ViewUniqueId == viewUniqueId)
                    .Single();

                var activationCount = existingRecord?.ActivationCount ?? 0;
                var lastInitialization = existingRecord?.LastInitialization ?? currentDateTime;

                var record = new ViewActivationRecord
                {
                    ViewUniqueId = viewUniqueId,
                    FileName = fileName,
                    ViewId = viewElementId,
                    ViewName = viewName,
                    ViewType = viewType,
                    LastViewer = lastViewer,
                    LastActivationDate = existingRecord?.LastActivationDate ?? currentDateTime,
                    LastInitialization = lastInitialization,
                    ActivationCount = activationCount
                };

                await _supabase.From<ViewActivationRecord>().Upsert(record);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error upserting/initializing view in Supabase: {ex.Message}");
            }
        }

        public async Task<List<ViewActivationRecord>> GetViewActivationsByFileNameAsync(string fileName)
        {
            try
            {
                var results = await _supabase
                    .From<ViewActivationRecord>()
                    .Where(x => x.FileName == fileName)
                    .Get();

                return results.Models;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching view activations: {ex.Message}");
                return new List<ViewActivationRecord>();
            }
        }

        public async Task DeleteViewActivationByUniqueIdAsync(string viewUniqueId)
        {
            try
            {
                await _supabase
                    .From<ViewActivationRecord>()
                    .Where(x => x.ViewUniqueId == viewUniqueId)
                    .Delete();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error deleting view activation: {ex.Message}");
            }
        }
    }
}

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ViewTracker
{
    /// <summary>
    /// Configuration model for Supabase credentials
    /// </summary>
    public class SupabaseConfig
    {
        public string SupabaseUrl { get; set; }
        public string SupabaseKey { get; set; }
        public string ConfigVersion { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Service to download and cache Supabase configuration from cloud storage
    /// </summary>
    public class ConfigService
    {
        // Bootstrap credentials - ONLY used to download config file from Storage
        // These have minimal permissions (read-only access to public config bucket)
        private const string BOOTSTRAP_URL = "https://agexakhxckfvkwnflwxp.supabase.co";
        private const string BOOTSTRAP_KEY = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImFnZXhha2h4Y2tmdmt3bmZsd3hwIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTg4NDM2MjMsImV4cCI6MjA3NDQxOTYyM30.0LO5K2jehWHgm-Bj6tvIt0Qt8SwmHv39EKa9GBhyHEE";

        // Config file location in Supabase Storage
        private const string CONFIG_BUCKET = "config";
        private const string CONFIG_FILE_PATH = "supabase-config.json";

        // Local cache settings
        private static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snapshot"
        );
        private static readonly string CachedConfigPath = Path.Combine(CacheDirectory, "supabase-config.json");
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(24);

        private static SupabaseConfig _cachedConfig;
        private static DateTime _cacheTimestamp;
        private static readonly object _lock = new object();

        // Reusable HttpClient - creating new instances causes socket exhaustion
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        /// <summary>
        /// Gets Supabase configuration, using cache if available and valid
        /// </summary>
        public static async Task<SupabaseConfig> GetConfigAsync()
        {
            lock (_lock)
            {
                // Return memory cache if valid
                if (_cachedConfig != null && DateTime.UtcNow - _cacheTimestamp < CacheExpiration)
                {
                    return _cachedConfig;
                }
            }

            // Try to load from disk cache
            var diskConfig = TryLoadFromDiskCache();
            if (diskConfig != null)
            {
                lock (_lock)
                {
                    _cachedConfig = diskConfig;
                    _cacheTimestamp = DateTime.UtcNow;
                }
                return diskConfig;
            }

            // Download fresh config from Supabase Storage
            var freshConfig = await DownloadConfigAsync();

            // Cache to memory and disk
            lock (_lock)
            {
                _cachedConfig = freshConfig;
                _cacheTimestamp = DateTime.UtcNow;
            }
            SaveToDiskCache(freshConfig);

            return freshConfig;
        }

        /// <summary>
        /// Forces a fresh download of config, bypassing cache
        /// </summary>
        public static async Task<SupabaseConfig> RefreshConfigAsync()
        {
            var freshConfig = await DownloadConfigAsync();

            lock (_lock)
            {
                _cachedConfig = freshConfig;
                _cacheTimestamp = DateTime.UtcNow;
            }
            SaveToDiskCache(freshConfig);

            return freshConfig;
        }

        /// <summary>
        /// Downloads config file from Supabase Storage with retry logic
        /// </summary>
        private static async Task<SupabaseConfig> DownloadConfigAsync()
        {
            const int maxRetries = 3;
            const int delayMs = 1000; // 1 second base delay

            Exception lastException = null;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // Construct URL to public config file in Supabase Storage
                    // Format: https://{project}.supabase.co/storage/v1/object/public/{bucket}/{path}
                    var configUrl = $"{BOOTSTRAP_URL}/storage/v1/object/public/{CONFIG_BUCKET}/{CONFIG_FILE_PATH}";

                    // Public buckets don't need authorization headers
                    var response = await _httpClient.GetAsync(configUrl);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception($"Failed to download config: {response.StatusCode} - {response.ReasonPhrase}");
                    }

                    var jsonContent = await response.Content.ReadAsStringAsync();
                    var config = JsonConvert.DeserializeObject<SupabaseConfig>(jsonContent);

                    if (config == null || string.IsNullOrWhiteSpace(config.SupabaseUrl) || string.IsNullOrWhiteSpace(config.SupabaseKey))
                    {
                        throw new Exception("Downloaded config is invalid or incomplete");
                    }

                    // Success!
                    if (attempt > 1)
                    {
                        System.Diagnostics.Debug.WriteLine($"Config download succeeded on attempt {attempt}");
                    }
                    return config;
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    if (attempt < maxRetries)
                    {
                        var delay = delayMs * attempt; // Exponential backoff: 1s, 2s, 3s
                        System.Diagnostics.Debug.WriteLine($"Config download attempt {attempt} failed: {ex.Message}. Retrying in {delay}ms...");
                        await Task.Delay(delay);
                    }
                }
                catch (TaskCanceledException ex)
                {
                    lastException = ex;
                    if (attempt < maxRetries)
                    {
                        var delay = delayMs * attempt;
                        System.Diagnostics.Debug.WriteLine($"Config download attempt {attempt} timed out. Retrying in {delay}ms...");
                        await Task.Delay(delay);
                    }
                }
                catch (Exception ex)
                {
                    // Non-retryable exceptions (invalid JSON, etc.) - fail immediately
                    throw new Exception($"Failed to download Supabase configuration: {ex.Message}", ex);
                }
            }

            // All retries exhausted
            throw new Exception($"Failed to download Supabase configuration after {maxRetries} attempts: {lastException?.Message}", lastException);
        }

        /// <summary>
        /// Tries to load config from local disk cache
        /// </summary>
        private static SupabaseConfig TryLoadFromDiskCache()
        {
            try
            {
                if (!File.Exists(CachedConfigPath))
                    return null;

                var fileInfo = new FileInfo(CachedConfigPath);

                // Check if cache is expired
                if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > CacheExpiration)
                    return null;

                var jsonContent = File.ReadAllText(CachedConfigPath);
                var config = JsonConvert.DeserializeObject<SupabaseConfig>(jsonContent);

                return config;
            }
            catch
            {
                // If cache is corrupted, return null to trigger fresh download
                return null;
            }
        }

        /// <summary>
        /// Saves config to local disk cache
        /// </summary>
        private static void SaveToDiskCache(SupabaseConfig config)
        {
            try
            {
                // Ensure directory exists
                Directory.CreateDirectory(CacheDirectory);

                var jsonContent = JsonConvert.SerializeObject(config, Formatting.Indented);

                File.WriteAllText(CachedConfigPath, jsonContent);
            }
            catch
            {
                // Silently fail - caching is not critical
            }
        }

        /// <summary>
        /// Clears all caches (for troubleshooting)
        /// </summary>
        public static void ClearCache()
        {
            lock (_lock)
            {
                _cachedConfig = null;
                _cacheTimestamp = DateTime.MinValue;
            }

            try
            {
                if (File.Exists(CachedConfigPath))
                {
                    File.Delete(CachedConfigPath);
                }
            }
            catch
            {
                // Silently fail
            }
        }
    }
}

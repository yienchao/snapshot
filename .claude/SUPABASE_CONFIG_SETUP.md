# Supabase Configuration Setup Guide

This guide explains how to set up the external configuration file for Supabase credentials.

## Why Use External Config?

Instead of hardcoding Supabase credentials in the source code, we store them in a config file on Supabase Storage. This provides:

1. **Security**: Credentials are not committed to source control
2. **Flexibility**: Update credentials without recompiling code
3. **Distribution**: All users automatically get updated credentials (cached for 24 hours)
4. **Simplicity**: One central place to manage credentials

## Architecture

```
┌─────────────────┐
│  Revit Add-in   │
└────────┬────────┘
         │
         │ 1. Uses bootstrap credentials (minimal permissions)
         ▼
┌─────────────────────────┐
│ Supabase Storage Bucket │
│   "config" (public)     │
└────────┬────────────────┘
         │
         │ 2. Downloads supabase-config.json
         ▼
┌─────────────────┐
│  Local Cache    │
│  (24h expiry)   │
└────────┬────────┘
         │
         │ 3. Uses actual credentials for DB operations
         ▼
┌─────────────────┐
│ Supabase Tables │
│ (rooms, doors,  │
│  elements, etc) │
└─────────────────┘
```

## Step 1: Create Supabase Storage Bucket

1. Go to your Supabase Dashboard: https://app.supabase.com
2. Select your project: `agexakhxckfvkwnflwxp`
3. Navigate to **Storage** in the left sidebar
4. Click **New bucket**
5. Configure the bucket:
   - **Name**: `config`
   - **Public bucket**: ✅ **CHECKED** (important!)
   - **File size limit**: 1 MB (default is fine)
   - **Allowed MIME types**: `application/json`
6. Click **Create bucket**

## Step 2: Create Config File

1. Create a file named `supabase-config.json` with this content:

```json
{
  "supabaseUrl": "https://agexakhxckfvkwnflwxp.supabase.co",
  "supabaseKey": "YOUR_ACTUAL_ANON_KEY_HERE",
  "configVersion": "1.0",
  "lastUpdated": "2025-01-17T10:00:00Z"
}
```

2. Replace `YOUR_ACTUAL_ANON_KEY_HERE` with your actual Supabase anon key:
   - Go to **Project Settings** → **API**
   - Copy the **anon public** key
   - Paste it in the config file

## Step 3: Upload Config File

1. In Supabase Dashboard, go to **Storage** → **config** bucket
2. Click **Upload file**
3. Upload your `supabase-config.json` file
4. Verify the file path is: `config/supabase-config.json`

## Step 4: Verify Public Access

1. In the `config` bucket, click on `supabase-config.json`
2. Click **Get URL**
3. The URL should look like:
   ```
   https://agexakhxckfvkwnflwxp.supabase.co/storage/v1/object/public/config/supabase-config.json
   ```
4. Open this URL in your browser - you should see the JSON content

## Step 5: Test the Add-in

1. Build and run the Revit add-in
2. The first time it runs, it will:
   - Download the config file using bootstrap credentials
   - Cache it locally for 24 hours
   - Use the downloaded credentials for all database operations

## Updating Credentials

To update credentials without recompiling:

1. Edit `supabase-config.json` in Supabase Storage
2. Update the `lastUpdated` timestamp
3. Wait up to 24 hours for caches to expire, OR
4. Manually clear cache by deleting:
   ```
   %APPDATA%\ViewTracker\supabase-config.json
   ```

## Troubleshooting

### Error: "Failed to download config: 404"
- Verify the bucket is named exactly `config`
- Verify the file is named exactly `supabase-config.json`
- Verify the bucket is set to **Public**

### Error: "Downloaded config is invalid or incomplete"
- Check the JSON syntax is valid
- Ensure `supabaseUrl` and `supabaseKey` fields exist
- Ensure values are not empty

### Error: "Failed to download Supabase configuration"
- Check internet connectivity
- Verify bootstrap credentials in `SupabaseConfig.cs` are correct
- Check Supabase project is not paused

## Security Notes

### Bootstrap Credentials
The bootstrap credentials (hardcoded in `SupabaseConfig.cs`) should have **minimal permissions**:
- **Only** read access to the `config` bucket in Storage
- No access to tables, functions, or other storage buckets

To set this up:
1. Go to **Authentication** → **Policies**
2. For the `config` bucket, create a policy allowing public read-only access
3. Ensure the anon key used for bootstrap only has these permissions

### Actual Credentials
The credentials in `supabase-config.json` should have:
- Full access to your tables (view_activations, room_snapshots, etc.)
- RLS (Row Level Security) policies as needed for your security model

## Config File Reference

```json
{
  "supabaseUrl": "https://YOUR_PROJECT.supabase.co",
  "supabaseKey": "YOUR_ANON_KEY",
  "configVersion": "1.0",
  "lastUpdated": "2025-01-17T10:00:00Z"
}
```

### Fields

- **supabaseUrl** (required): Your Supabase project URL
- **supabaseKey** (required): Your Supabase anon/public key
- **configVersion** (optional): Version tracking for config format changes
- **lastUpdated** (optional): ISO 8601 timestamp of last update

## Cache Behavior

- **Memory cache**: Valid for duration of Revit session
- **Disk cache**: Valid for 24 hours
- **Location**: `%APPDATA%\ViewTracker\supabase-config.json`
- **Auto-refresh**: Automatically downloads fresh config when cache expires

## Advanced: Multiple Environments

To support dev/staging/prod:

1. Create multiple config files:
   - `supabase-config.json` (production)
   - `supabase-config-dev.json` (development)
   - `supabase-config-staging.json` (staging)

2. Modify `SupabaseConfig.cs` to use environment variable:
   ```csharp
   private static readonly string CONFIG_FILE_PATH =
       Environment.GetEnvironmentVariable("SUPABASE_CONFIG")
       ?? "supabase-config.json";
   ```

3. Set environment variable before running Revit:
   ```cmd
   set SUPABASE_CONFIG=supabase-config-dev.json
   ```

-- ============================================================================
-- Database Performance Optimization - Index Creation
-- ============================================================================
-- These indexes dramatically improve query performance for snapshot operations
-- Run these commands in the Supabase SQL Editor
-- ============================================================================

-- ROOM_SNAPSHOTS TABLE INDEXES
-- ----------------------------------------------------------------------------

-- Index for fetching all snapshots for a specific version
-- Used by: GetRoomsByVersionAsync(), RoomCompareCommand
CREATE INDEX IF NOT EXISTS idx_room_snapshots_project_version
ON room_snapshots(project_id, version_name);

-- Index for fetching history for a specific room across versions
-- Used by: GetRoomHistoryAsync(), RoomHistoryCommand
CREATE INDEX IF NOT EXISTS idx_room_snapshots_project_track
ON room_snapshots(project_id, track_id);

-- Index for checking if version exists
-- Used by: VersionExistsAsync(), RoomSnapshotCommand
CREATE INDEX IF NOT EXISTS idx_room_snapshots_version_filename
ON room_snapshots(version_name, file_name);

-- Composite index for optimized version metadata queries
-- Used by: GetAllVersionsWithInfoAsync() - new optimized query
CREATE INDEX IF NOT EXISTS idx_room_snapshots_project_version_date
ON room_snapshots(project_id, version_name, snapshot_date DESC);


-- DOOR_SNAPSHOTS TABLE INDEXES
-- ----------------------------------------------------------------------------

-- Index for fetching all snapshots for a specific version
-- Used by: GetDoorsByVersionAsync(), DoorCompareCommand
CREATE INDEX IF NOT EXISTS idx_door_snapshots_project_version
ON door_snapshots(project_id, version_name);

-- Index for fetching history for a specific door across versions
-- Used by: GetDoorHistoryAsync(), DoorHistoryCommand
CREATE INDEX IF NOT EXISTS idx_door_snapshots_project_track
ON door_snapshots(project_id, track_id);

-- Index for checking if version exists
-- Used by: DoorVersionExistsAsync(), DoorSnapshotCommand
CREATE INDEX IF NOT EXISTS idx_door_snapshots_version_filename
ON door_snapshots(version_name, file_name);

-- Composite index for optimized version metadata queries
-- Used by: GetAllDoorVersionsWithInfoAsync() - new optimized query
CREATE INDEX IF NOT EXISTS idx_door_snapshots_project_version_date
ON door_snapshots(project_id, version_name, snapshot_date DESC);


-- ELEMENT_SNAPSHOTS TABLE INDEXES
-- ----------------------------------------------------------------------------

-- Index for fetching all snapshots for a specific version
-- Used by: GetElementsByVersionAsync(), ElementCompareCommand
CREATE INDEX IF NOT EXISTS idx_element_snapshots_project_version
ON element_snapshots(project_id, version_name);

-- Index for fetching history for a specific element across versions
-- Used by: GetElementHistoryAsync(), ElementHistoryCommand
CREATE INDEX IF NOT EXISTS idx_element_snapshots_project_track
ON element_snapshots(project_id, track_id);

-- Index for checking if version exists
-- Used by: ElementVersionExistsAsync(), ElementSnapshotCommand
CREATE INDEX IF NOT EXISTS idx_element_snapshots_version_filename
ON element_snapshots(version_name, file_name);

-- Composite index for optimized version metadata queries
-- Used by: GetAllElementVersionsWithInfoAsync() - new optimized query
CREATE INDEX IF NOT EXISTS idx_element_snapshots_project_version_date
ON element_snapshots(project_id, version_name, snapshot_date DESC);


-- ============================================================================
-- VERIFICATION QUERIES
-- ============================================================================
-- Run these to verify indexes were created successfully:

-- Check room_snapshots indexes
SELECT indexname, indexdef
FROM pg_indexes
WHERE tablename = 'room_snapshots'
ORDER BY indexname;

-- Check door_snapshots indexes
SELECT indexname, indexdef
FROM pg_indexes
WHERE tablename = 'door_snapshots'
ORDER BY indexname;

-- Check element_snapshots indexes
SELECT indexname, indexdef
FROM pg_indexes
WHERE tablename = 'element_snapshots'
ORDER BY indexname;


-- ============================================================================
-- PERFORMANCE IMPACT ESTIMATES
-- ============================================================================
-- Before indexes:
--   - Version list query: Scans ALL snapshots (5000+ rows for 10 versions × 500 rooms)
--   - Compare query: Full table scan filtered by project_id + version_name
--   - History query: Full table scan filtered by project_id + track_id
--
-- After indexes:
--   - Version list query: Index-only scan on metadata columns (10-100x faster)
--   - Compare query: Index seek (10-100x faster depending on data size)
--   - History query: Index seek (10-100x faster)
--
-- Expected user-facing improvements:
--   - Compare dropdown opens instantly instead of 1-5 second delay
--   - Version selection responds immediately
--   - History window loads faster
-- ============================================================================


-- ============================================================================
-- OPTIONAL: ANALYZE TABLES AFTER INDEX CREATION
-- ============================================================================
-- Run these to update query planner statistics after creating indexes:

ANALYZE room_snapshots;
ANALYZE door_snapshots;
ANALYZE element_snapshots;

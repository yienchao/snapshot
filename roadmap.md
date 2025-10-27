# ViewTracker Roadmap

## Implemented Features

### Core Functionality
-  **Snapshot System**
  - Room parameters (instance + dedicated columns)
  - Door parameters (instance + type + orientation)
  - Element/Loadable family parameters (instance + type + orientation)
  - Version management (official/draft versions)
  - Track ID generation and validation
  - Project-based organization

-  **Comparison**
  - Current model vs snapshot (rooms, doors, elements)
  - Shows instance parameter changes
  - Shows type parameter changes (warning only)
  - Shows orientation changes (facing/hand)
  - Shows family/type swaps

-  **Restore**
  - Instance parameter restoration (rooms, doors, elements)
  - Orientation restoration (door/element facing and hand flips)
  - Deleted room recreation (with placement options)
  - Unplaced room placement restoration
  - Backup snapshot creation before restore
  - Empty value restoration (can clear parameters)

### Performance & Quality
-  **Performance Optimizations**
  - Database query optimization (pagination, column selection)
  - Database indexing (version queries, history queries)
  - UI virtualization (ListBox with VirtualizingStackPanel)
  - Eliminated redundant element queries
  - Optimized checkbox interactions (preview updates only)

-  **Bug Fixes & Improvements**
  - TrackID comparison (case-insensitive, whitespace trimming)
  - JObject deserialization handling
  - ElementId normalization (-1 vs empty string)
  - Orientation formatting consistency (F2 precision)
  - Read-only parameter exclusions (Area, Perimeter, Volume, Variantes)

---

## Planned Features

### Medium Priority

#### Delete Draft Snapshots
- **Goal:** Allow users to clean up work-in-progress/test snapshots
- **Scope:**
  - Only draft snapshots can be deleted (not official snapshots)
  - Official snapshots are locked/protected (permanent historical record)
  - Delete button/icon appears only for draft snapshots in version dropdown
  - Confirmation dialog before deletion
  - Shows what will be deleted (count of room/door/element snapshots)
- **Design Philosophy:**
  - Official snapshots = audit trail (must be permanent)
  - Draft snapshots = WIP/tests (users can clean up their own)
  - Prevents accidental deletion of important milestones
- **Permissions:**
  - Users can delete their own draft snapshots
  - Cannot delete other users' snapshots (optional: admin override)

#### Family Type Restoration (Opt-in)
- **Status:** Type tracking and comparison already implemented
- **Missing:** Restore capability
- **Scope:**
  - Show family/type differences in restore preview
  - Opt-in checkbox for type restoration (separate from instance parameters)
  - Use `element.ChangeTypeId()` to swap types by matching TypeName
  - Handle missing types gracefully (warning: "Type 'XXX' not found")
- **Design Philosophy:**
  - BIM team manages family library and types
  - Users restore instance parameters
  - Type restore is opt-in only (intentional design changes should not be auto-reverted)
- **Risks:**
  - Type might not exist in current document
  - Type swap might break constraints
  - Different types might have incompatible instance parameters

### Low Priority / Future Ideas

#### System Family Tracking
- Walls, Floors, Ceilings, Roofs, Stairs, Railings
- More complex than loadable families
- Need to evaluate use cases

#### Sheet & Revision Management
- **Status:** Needs further analysis
- **Challenges:**
  - Sheets/revisions are project-wide, not instance-based
  - Many-to-many relationships (sheets � revisions � clouds)
  - Doesn't fit snapshot/compare/restore paradigm well
- **Alternative Approach:**
  - Might be better as separate reporting/validation tool
  - Focus on QA/QC rules rather than restore
  - Example: "Check for revision mismatches between clouds and sheets"

#### Nice-to-Have Features
- Export comparison results to Excel (partially done)
- Scheduled snapshots (automatic daily/weekly)
- Snapshot annotations/notes

---

## Technical Notes

### Architecture Decisions
- **Type Parameters:** Shown in comparison but NOT auto-restored
  - Rationale: BIM Manager controls type library, not end users
  - Type changes often intentional (design evolution)

- **TrackID Strategy:**
  - Persistent unique identifier per element instance
  - Survives copy/paste, mirror, array
  - Essential for matching elements across snapshots

### Database Schema
- Supabase/PostgreSQL backend
- Composite primary keys: (track_id, version_name)
- JSONB columns for flexible parameter storage (AllParameters, TypeParameters)
- Dedicated columns for frequently queried fields (room_number, level, etc.)
- Comprehensive indexing for performance

### Performance Considerations
- Pagination for large datasets (1000 rows per batch)
- UI virtualization for large lists (500+ items)
- Database indexes on query patterns
- Minimize redundant element collection queries

---

## Development Workflow

### BIM Team Responsibilities
- Manage family library (create, modify, delete types)
- Control type parameters (widths, heights, materials)
- Maintain consistent type naming conventions

### End User Workflow
1. Create snapshot (captures current state)
2. Work in model (modify parameters, move elements)
3. Compare current vs snapshot (see what changed)
4. Restore selected parameters (revert to snapshot state)

### Quality Assurance
- Backup snapshots created before restore
- Transaction rollback on errors
- Read-only parameter validation
- Missing element warnings (deleted elements)

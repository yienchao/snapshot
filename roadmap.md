# ViewTracker Roadmap

## ✅ Completed - Core System (Production Ready)

### Snapshot, Compare, Restore System
- **Rooms**: Full snapshot/compare/restore with JSON parameter storage
- **Doors**: Full snapshot/compare/restore with JSON parameter storage + flip detection
- **Elements**: Full snapshot/compare/restore with JSON parameter storage + orientation tracking
- **Version Management**: Official/draft versions with project-based organization
- **TrackID System**: Persistent unique identifiers for element tracking
- **Deleted Room Recreation**: Restore deleted rooms with placement options

### Refactoring Achievements (October 2025)
- ✅ **JSON Parameter Storage**: Migrated from dedicated columns to AllParameters/TypeParameters JSON
- ✅ **Type-Safe Comparisons**: ParameterValue class with robust IsEqualTo() logic
- ✅ **Language-Independent**: TypeId comparison using ElementId.Value (long), not localized names
- ✅ **Zero False Positives**: Eliminated 770+ false door comparisons
- ✅ **Conditional Formatting**: Bold red only when values actually differ
- ✅ **Consistent UX**: All restore windows show "CurrentValue → NewValue" format
- ✅ **Performance**: Batched checkbox updates, select all/none optimizations

### Database Architecture
- **Supabase/PostgreSQL** backend
- **Composite Primary Keys**: (track_id, version_name)
- **JSONB Columns**: AllParameters, TypeParameters for flexible storage
- **Dedicated Columns**: mark, level, type_id for fast queries/indexing
- **RLS Policies**: Row-level security configured

### Comparison Logic (Bulletproof)
- ✅ **Internal Value Comparison**: Uses RawValue (not DisplayValue) for accuracy
- ✅ **ElementId Handling**: Compares long values with fallback to DisplayValue
- ✅ **Type Comparison**: Compares type_id (long) instead of localized type names
- ✅ **Location/Rotation Excluded**: Prevents false positives from element movement
- ✅ **Flip Detection**: Tracks facing/hand orientation for doors and elements
- ✅ **Custom Parameters**: Handles all parameter types (String, Integer, Double, ElementId)

---

## 🎯 High Priority - Next Features

### 1. Level Comparison Enhancement
**Current State**: Comparing level NAME (string) - works but sensitive to renames

**Goal**: Store and compare `level_id` (long) like we do for `type_id`

**Benefits**:
- Rename-proof (level can be renamed without triggering changes)
- Consistent with type_id approach
- Language-independent

**Scope**:
- Add `level_id` column to room_snapshots, door_snapshots, element_snapshots
- Store `element.LevelId.Value` during snapshot creation
- Compare level_id (long) instead of level name (string)
- Fallback to level name display for user-friendly messages

**Effort**: Low (same pattern as type_id)

---

### 2. Excel Import for Bulk Parameter Updates
**Status**: Export to Excel ✅ DONE | Import from Excel ❌ TODO

**Goal**: Re-import edited Excel file to bulk-update Revit parameters

**Scope**:
- Match elements by TrackID column
- Validate parameter values (type checking, read-only detection)
- Preview changes before applying (show current vs new)
- Transaction-based (all or nothing)
- Create backup snapshot before import
- Handle errors gracefully (missing elements, invalid values)

**Use Cases**:
- Bulk parameter updates (e.g., update 100 rooms' finish parameters)
- Data cleanup (standardize naming, fix typos)
- Cross-project parameter copying

**Design Philosophy**:
- Excel as powerful bulk editing interface
- Validation before committing changes
- Non-destructive (backup snapshot created)
- Read-only parameters ignored (safe)

**Effort**: Medium

---

### 3. Snapshot Management

#### 3a. Rename Draft Snapshots
**Goal**: Allow users to rename draft snapshots for better organization

**Scope**:
- Only draft snapshots can be renamed (official = immutable)
- Validation: unique name, not empty
- Updates all related tables (rooms, doors, elements)
- Confirmation dialog showing old → new name

**Use Cases**:
- Fix typos in snapshot names
- Rename "WIP" to descriptive names before marking official
- Standardize naming conventions

**Effort**: Low

#### 3b. Delete Draft Snapshots
**Goal**: Clean up work-in-progress/test snapshots

**Scope**:
- Only draft snapshots can be deleted (official = protected)
- Confirmation dialog before deletion
- Shows what will be deleted (count of room/door/element snapshots)
- Cascade delete from all related tables

**Use Cases**:
- Remove test snapshots
- Clean up failed/partial snapshots
- Manage disk space

**Permissions**:
- Users can delete their own draft snapshots
- Cannot delete other users' snapshots (optional: admin override)

**Effort**: Low

---

## 🔮 Medium Priority - Future Enhancements

### 4. Family Type Restoration (Opt-in)
**Status**: Type tracking and comparison ✅ DONE | Restore capability ❌ TODO

**Goal**: Allow restoring element family types (not just instance parameters)

**Scope**:
- Show family/type differences in restore preview
- Opt-in checkbox for type restoration (separate from instance parameters)
- Use `element.ChangeTypeId()` to swap types
- Match by type_id (long) stored in snapshot
- Handle missing types gracefully (warning: "Type 'XXX' not found in project")

**Design Philosophy**:
- BIM team manages family library and types
- Type restore is opt-in only (intentional design changes should not be auto-reverted)

**Risks**:
- Type might not exist in current document
- Type swap might break constraints
- Different types might have incompatible instance parameters

**Effort**: Medium

---

### 5. Non-Restorable Parameter UI Indicators
**Goal**: Clearly mark parameters that are included for comparison but not restorable

**Current State**: Mark, Level, Variantes, From Room, To Room are included in snapshots but shouldn't be restorable

**Scope**:
- Grey out non-restorable parameters in restore window
- Add tooltip explaining why (e.g., "Read-only", "Controlled by placement", "System parameter")
- Disable checkboxes for these parameters
- Keep them visible for comparison purposes

**Use Cases**:
- User sees Mark changed but can't restore it (expected - Mark is managed manually)
- User sees Level changed and can restore it (or not - TBD based on user needs)

**Effort**: Low

---

### 6. Comparison Filters & Search
**Goal**: Better navigation in large comparison results

**Scope**:
- Filter by change type (Modified, New, Deleted)
- Filter by parameter name (show only elements where "Finish" changed)
- Search by Track ID or element number
- Export filtered results to Excel/CSV

**Use Cases**:
- "Show only doors where finish changed"
- "Find all elements with 'PCQ_' parameter changes"
- Large projects with 1000+ elements

**Effort**: Low-Medium

---

## 💭 Nice-to-Have / Long-Term Ideas

### 7. Scheduled Automatic Snapshots
- Daily/weekly snapshots at specified time
- Configurable snapshot retention (keep last N snapshots)
- Background task (no UI interruption)

**Effort**: Medium

---

### 8. Snapshot Annotations & Notes
- Add notes/comments to snapshots
- Tag snapshots (e.g., "Before coordination", "Client review")
- Snapshot description field

**Effort**: Low

---

### 9. Comparison Diff View
- Side-by-side comparison view (snapshot vs current)
- Visual diff highlighting (like Git diff)
- Parameter-by-parameter comparison

**Effort**: Medium

---

### 10. System Family Tracking
**Status**: Needs evaluation

**Scope**: Walls, Floors, Ceilings, Roofs, Stairs, Railings

**Challenges**:
- More complex than loadable families
- Different parameter structures
- Sketch-based elements (different restore logic)

**Effort**: High (needs research)

---

## ❌ Out of Scope

### Sheet & Revision Management
**Rationale**:
- Sheets/revisions are project-wide, not instance-based
- Many-to-many relationships (sheets ↔ revisions ↔ clouds)
- Doesn't fit snapshot/compare/restore paradigm well

**Alternative Approach**:
- Better as separate QA/QC validation tool
- Focus on reporting/rules rather than restore
- Example: "Check for revision mismatches between clouds and sheets"

---

## 🏗️ Technical Debt & Improvements

### Performance Optimizations
- ✅ Database query optimization (pagination, column selection)
- ✅ UI virtualization (ListBox with VirtualizingStackPanel)
- ✅ Parameter caching (_instanceParamCache, _typeParamCache)
- 🔄 Consider lazy loading for large snapshots (load parameters on-demand)

### Code Quality
- ✅ Consistent ParameterValue usage across all commands
- ✅ Robust error handling with try-catch and null checks
- 🔄 Unit tests for ParameterValue.IsEqualTo() logic
- 🔄 Integration tests for snapshot/compare/restore workflows

### Documentation
- 🔄 User guide (how to use snapshot/compare/restore)
- 🔄 Developer guide (how to add new entity types)
- 🔄 API documentation (Supabase schema, RLS policies)

---

## 🎓 Lessons Learned

### What Worked Well
1. **JSON Storage**: Flexible, scalable, handles custom parameters automatically
2. **ParameterValue Class**: Type-safe, consistent comparison logic
3. **TypeId Comparison**: Language-independent, bulletproof
4. **Dedicated Columns**: Fast queries while maintaining flexibility
5. **Incremental Refactoring**: Rooms first, then doors, then elements

### What to Avoid
1. **String-based comparisons**: Fragile, language-dependent
2. **Manual parameter handling**: Error-prone, doesn't scale
3. **Dedicated columns for everything**: Inflexible, hard to maintain
4. **Comparing display values**: Formatting differences cause false positives

### Best Practices
1. **Always compare internal values (RawValue)**, not display strings
2. **Use ElementId.Value (long)** for element references, not names
3. **Exclude location/rotation from comparison** to avoid movement false positives
4. **Include orientation (facing/hand) in comparison** to detect flips
5. **Batch UI updates** for performance (Select All/None)
6. **Take fresh snapshots** after code changes that affect storage format

---

## 📊 Success Metrics

### Before Refactoring
- ❌ 770 false positives on door comparison
- ❌ Type warnings showing incorrect values
- ❌ Bold red on all parameters (even unchanged)
- ❌ Inconsistent UX between rooms/doors/elements

### After Refactoring (October 2025)
- ✅ **0 false positives** on all entity types
- ✅ **Accurate type detection** (compares TypeId, not names)
- ✅ **Conditional formatting** (bold red only when different)
- ✅ **Consistent UX** (all restore windows identical)
- ✅ **Production-ready** (bulletproof, language-independent)

---

*Last Updated: October 29, 2025*
*Status: Core system complete and production-ready. Ready for feature expansion.*

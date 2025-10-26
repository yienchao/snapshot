## Implemented Features
- ✅ Room parameter tracking and comparison
- ✅ Door parameter tracking and comparison  
- ✅ Element (loadable family) parameter tracking
- ✅ Restore functionality for instance parameters
- ✅ Type parameter warnings (not restorable)
- ✅ Track ID generation and validation
- ✅ Comparison: current vs snapshot
- ✅ Comparison: snapshot vs snapshot (rooms only)

## Planned Features

### High Priority
- [ ✅] Fix restore issues (empty strings, locale-dependent parsing)

### Medium Priority
- [ ] Sheet & Revision tracking
  - Track which revisions appear on which sheets
  - Track individual revision clouds and their assigned revisions
  - Detect errors when users assign wrong revision to clouds
  - Compare sheet revisions between snapshots
  - Separate data model from parameter tracking

### Low Priority / Future Ideas
- [ ] System family tracking (Stairs, Railings, Walls, Floors, etc.)
- [ ✅] Export comparison results to Excel
- [ ] Scheduled snapshots (automatic daily/weekly)
- [ ] Snapshot annotations/notes

## Notes
- Type parameters shown in comparison but NOT restored (BIM Manager fixes manually via Revit)
- Snapshot-vs-snapshot comparison only for Rooms (too complicated for Doors/Elements)
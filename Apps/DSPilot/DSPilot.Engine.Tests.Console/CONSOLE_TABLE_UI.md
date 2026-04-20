# Console Table UI - Real-time PLC Monitoring

## Overview

The console table UI provides real-time visualization of PLC tag changes and Call state transitions.

## Features

### Real-time Status Table

```
╔════════════════════════════════════════════════════════════════════════════════════════════════════════════════╗
║  Flow Name        Call Name         State      Last Start        Last Finish       Duration(ms)   Cycle Count   ║
╠════════════════════════════════════════════════════════════════════════════════════════════════════════════════╣
║  MainFlow         Work1             Going      14:32:15          -                 -              0             ║
║  MainFlow         Work2             Ready      14:32:10          14:32:11          1050.23        3             ║
║  SubFlow1         Work3             Done       14:32:16          14:32:17          1123.45        2             ║
║  SubFlow2         Work4             Ready      -                 -                 -              0             ║
╚════════════════════════════════════════════════════════════════════════════════════════════════════════════════╝

Press 'Q' to quit...
```

### Color-Coded States

- **Ready** (Gray): Call is waiting for OutTag signal
- **Going** (Yellow): Call is in progress (OutTag received, waiting for InTag)
- **Done** (Green): Call completed (InTag received, auto-transitions to Ready)

### Columns

| Column | Description | Example |
|--------|-------------|---------|
| **Flow Name** | Flow that contains this Call | MainFlow |
| **Call Name** | Name of the Call | Work1 |
| **State** | Current state (Ready/Going/Done) | Going |
| **Last Start** | Time when last cycle started (OutTag rising) | 14:32:15 |
| **Last Finish** | Time when last cycle finished (InTag rising) | 14:32:16 |
| **Duration(ms)** | Last cycle duration in milliseconds | 1123.45 |
| **Cycle Count** | Number of completed cycles | 5 |

## Usage

### Run the Test

```bash
cd DSPilot.Engine.Tests.Console
dotnet run realplc
```

### Interactive Controls

- **Q** key: Quit monitoring and exit
- The table updates automatically whenever a state change occurs

### Workflow

1. **Initialization Phase** (1-7 steps shown)
   - Loading AASX
   - Initializing database
   - Building tag mappings
   - Connecting to PLC

2. **Monitoring Phase** (Real-time table)
   - Screen clears
   - Table appears showing all Calls
   - Table updates on every state change
   - Press 'Q' to stop

## State Transitions in Table

### Example Sequence

**Initial State:**
```
║  MainFlow    Work1    Ready    -          -          -         0  ║
```

**OutTag Rising Edge (PLC sends output signal):**
```
║  MainFlow    Work1    Going    14:32:15   -          -         0  ║
```

**InTag Rising Edge (Sensor confirms completion):**
```
║  MainFlow    Work1    Done     14:32:15   14:32:16   1050.23   1  ║
```

**Auto-transition to Ready:**
```
║  MainFlow    Work1    Ready    14:32:15   14:32:16   1050.23   1  ║
```

**Next Cycle:**
```
║  MainFlow    Work1    Going    14:33:20   14:32:16   1050.23   1  ║
                                  ↑ Updated
```

**After 2nd Cycle:**
```
║  MainFlow    Work1    Ready    14:33:20   14:33:21   1075.50   2  ║
                                            ↑ Updated   ↑ Updated  ↑ Incremented
```

## Multiple Flows Running Concurrently

```
╔════════════════════════════════════════════════════════════════════════════════════════════════════════════════╗
║  Flow Name        Call Name         State      Last Start        Last Finish       Duration(ms)   Cycle Count   ║
╠════════════════════════════════════════════════════════════════════════════════════════════════════════════════╣
║  MainFlow         Work1             Going      14:32:15          14:32:16          1050.23        3             ║
║  MainFlow         Work2             Ready      14:32:17          14:32:18          1023.45        3             ║
║  SubFlow1         Work3             Going      14:32:16          14:32:17          1123.45        2             ║
║  SubFlow1         Work4             Ready      14:32:19          14:32:20          998.67         2             ║
║  SubFlow2         Work5             Done       14:32:18          14:32:19          1087.34        1             ║
╚════════════════════════════════════════════════════════════════════════════════════════════════════════════════╝
```

## Technical Details

### Update Frequency

- **PLC Polling**: Every 100ms
- **Table Refresh**: Only when state changes occur (efficient rendering)
- **Keyboard Check**: Every 100ms for 'Q' key

### Display Format

- **Times**: HH:mm:ss format (24-hour)
- **Duration**: Milliseconds with 2 decimal places
- **Cycle Count**: Integer counter
- **State Colors**: ANSI console colors (Gray/Yellow/Green)

### Table Rendering

The table uses a smart rendering algorithm:

1. **Initial Render**: Shows all Calls with initial state (Ready)
2. **Update on Change**: Only re-renders when a rising edge is detected
3. **Clear Previous**: Uses console cursor positioning to overwrite previous table
4. **Sorted Output**: Rows sorted by FlowName, then CallName

## Integration with DSPilot.TestConsole

When DSPilot.TestConsole test 4 is running on another PC, you'll see:

1. **Parallel Flow Execution**: Multiple flows showing different states simultaneously
2. **Synchronized Cycles**: All Calls in a flow progressing together
3. **Real-time Statistics**: Cycle counts and durations updating live

## Example Session

```bash
$ dotnet run realplc

========================================
DSPilot.Engine Step-by-Step Test Console
========================================

AASX path (default: C:\ds\ds2\Apps\DSPilot\DsCSV_0318_C.aasx): [Enter]
DB path (default: C:\Users\...\DSPilot\real_plc_connection.db): [Enter]

╔════════════════════════════════════════════╗
║     Real PLC Connection Test               ║
╚════════════════════════════════════════════╝

1️⃣  Loading AASX: C:\ds\ds2\Apps\DSPilot\DsCSV_0318_C.aasx
   ✅ AASX loaded successfully

2️⃣  Initializing database: C:\Users\...\real_plc_connection.db
   Initialized 5 flows, 23 calls
   ✅ Database initialized

3️⃣  Building tag mappings from AASX...
   ✅ 46 tag mappings created

4️⃣  Creating TagSpecs...
   ✅ 46 unique tag specs created

5️⃣  Configuring Mitsubishi PLC connection...
   ✅ PLC config ready

6️⃣  Starting PLC service...
   ✅ Connected: MitsubishiPLC

7️⃣  Monitoring PLC tags...
   Real-time status table will appear below

[Screen clears after 1 second]

╔════════════════════════════════════════════════════════════════════════════════════════════════════════════════╗
║  Flow Name        Call Name         State      Last Start        Last Finish       Duration(ms)   Cycle Count   ║
╠════════════════════════════════════════════════════════════════════════════════════════════════════════════════╣
║  MainFlow         Work1             Going      14:32:15          14:32:14          1050.23        5             ║
║  MainFlow         Work2             Ready      14:32:16          14:32:17          1023.45        5             ║
║  SubFlow1         Work3             Ready      14:32:18          14:32:19          1123.45        3             ║
╚════════════════════════════════════════════════════════════════════════════════════════════════════════════════╝

Press 'Q' to quit...

[Press Q]

✅ Monitoring stopped
```

## Troubleshooting

### Table Flickers

**Cause**: Console refresh rate too high
**Solution**: Already optimized - table only updates on state changes

### Missing Rows

**Cause**: Call has no ApiCall with InTag/OutTag
**Solution**: Check AASX project - all Calls need valid ApiCalls

### State Not Updating

**Cause**: PLC tags not changing or tag addresses mismatch
**Solution**:
1. Verify DSPilot.TestConsole test 4 is running
2. Check tag addresses in AASX match actual PLC tags

### 'Q' Key Not Working

**Cause**: Console input buffer full
**Solution**: Restart the test - keyboard listener runs in background thread

## Performance

- **Memory Usage**: ~50MB (includes F# runtime and DLL references)
- **CPU Usage**: <5% during monitoring (polling + rendering)
- **Network Traffic**: Minimal (100ms poll interval with small tag list)

## Comparison with DSPilot.TestConsole

| Feature | DSPilot.TestConsole Test 4 | DSPilot.Engine.Tests.Console |
|---------|---------------------------|------------------------------|
| **Purpose** | Send signals TO PLC | Receive signals FROM PLC |
| **Direction** | Client → PLC | PLC → Client |
| **Mode** | Simulation (infinite loop) | Real-time monitoring |
| **Output** | Console log (linear) | Real-time table (structured) |
| **Control** | Ctrl+C to stop | 'Q' key to quit |
| **Database** | No database updates | Persists all state changes |

## Future Enhancements

- [ ] Export to CSV on quit
- [ ] Statistics summary (avg/min/max duration)
- [ ] Flow-level aggregation view
- [ ] Filtering by Flow name
- [ ] Custom refresh rate configuration

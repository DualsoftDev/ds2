# Real PLC Connection Test

## Overview

`RealPlcTest.cs` connects to a real Mitsubishi PLC at `192.168.9.120:4444` (TCP) and monitors tag changes to process Call state transitions in real-time.

## Architecture

```
┌─────────────────────┐
│   AASX Project      │
│   (DsCSV_0318_C)    │
│  - Flows            │
│  - Calls            │
│  - ApiCalls (IOTags)│
└──────────┬──────────┘
           │ Load
           ↓
┌─────────────────────┐
│   RealPlcTest       │
│  - Build tag maps   │
│  - Initialize DB    │
└──────────┬──────────┘
           │
           ↓
┌─────────────────────────────────────────┐
│   Mitsubishi PLC (192.168.9.120:4444)  │
│   Protocol: QnA 3E Binary / TCP         │
│   Tags: X/Y addressing (e.g. X10A0)     │
└──────────┬──────────────────────────────┘
           │ Poll every 100ms
           │ Detect rising edges
           ↓
┌─────────────────────┐
│  State Transitions  │
│                     │
│  OutTag rising:     │
│    Ready → Going    │
│                     │
│  InTag rising:      │
│    Going → Done     │
│    Done → Ready     │
└──────────┬──────────┘
           │
           ↓
┌─────────────────────┐
│  SQLite Database    │
│  - dspFlow table    │
│  - dspCall table    │
│  - State tracking   │
│  - Duration stats   │
└─────────────────────┘
```

## How It Works

### 1. AASX Loading

The test loads an AASX project file (default: `C:\ds\ds2\Apps\DSPilot\DsCSV_0318_C.aasx`) and extracts:

- **Flows**: Top-level process definitions
- **Calls**: Individual operations within flows
- **ApiCalls**: Each Call has an ApiCall with:
  - **OutTag**: Output signal (e.g., `Y1190`) - triggers action start
  - **InTag**: Input signal/sensor (e.g., `X10A0`) - confirms completion

### 2. Tag Mapping

For each Call, the test creates two tag mappings:

```csharp
// OutTag mapping (triggers Ready → Going)
{
    CallId: <guid>,
    FlowName: "MainFlow",
    CallName: "Work1",
    TagAddress: "Y1190",
    IsInTag: false
}

// InTag mapping (triggers Going → Done)
{
    CallId: <guid>,
    FlowName: "MainFlow",
    CallName: "Work1",
    TagAddress: "X10A0",
    IsInTag: true
}
```

### 3. PLC Connection

Uses Ev2.Backend.PLC with Mitsubishi MX protocol:

```csharp
var connectionConfig = new MxConnectionConfig
{
    IpAddress = "192.168.9.120",
    Port = 4444,
    Name = "MitsubishiPLC",
    EnableScan = true,
    ScanInterval = TimeSpan.FromMilliseconds(500),
    FrameType = FrameType.QnA_3E_Binary,
    Protocol = TransportProtocol.TCP,
    AccessRoute = new AccessRoute(0, 255, 1023, 0),
    MonitoringTimer = 16
};
```

### 4. State Transition Logic

The test polls PLC tags every 100ms and detects rising edges:

**OutTag Rising Edge (PLC sends output signal)**
```
Current State: Ready
Event: OutTag (Y1190) changes from false → true
Action:
  - Set state to "Going"
  - Record LastStartAt timestamp
  - Update database
```

**InTag Rising Edge (Sensor confirms completion)**
```
Current State: Going
Event: InTag (X10A0) changes from false → true
Action:
  - Set state to "Done"
  - Record LastFinishAt timestamp
  - Calculate duration (FinishAt - StartAt)
  - Update database with duration
  - Auto-transition to "Ready"
```

### 5. Database Schema

**dspFlow Table**
```sql
CREATE TABLE dspFlow (
    Id TEXT PRIMARY KEY,
    FlowName TEXT NOT NULL,
    State TEXT NOT NULL DEFAULT 'Ready',
    ActiveCallCount INTEGER NOT NULL DEFAULT 0
);
```

**dspCall Table**
```sql
CREATE TABLE dspCall (
    Id TEXT PRIMARY KEY,
    CallName TEXT NOT NULL,
    FlowId TEXT NOT NULL,
    FlowName TEXT NOT NULL,
    State TEXT NOT NULL DEFAULT 'Ready',
    LastStartAt TEXT,        -- ISO 8601 timestamp
    LastFinishAt TEXT,       -- ISO 8601 timestamp
    LastDurationMs REAL,     -- Duration in milliseconds
    FOREIGN KEY(FlowId) REFERENCES dspFlow(Id)
);
```

## Usage

### Run the Test

```bash
cd DSPilot.Engine.Tests.Console
dotnet run realplc
```

### Interactive Prompts

```
AASX path (default: C:\ds\ds2\Apps\DSPilot\DsCSV_0318_C.aasx): [Enter]
DB path (default: C:\Users\...\AppData\Roaming\DSPilot\real_plc_connection.db): [Enter]
```

### Expected Output

```
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
   Press Ctrl+C to stop

  [14:32:15.123] 🔼 MainFlow/Work1: Ready → Going
  [14:32:16.234] 🔽 MainFlow/Work1: Going → Done (1111.00ms)
  [14:32:18.456] 🔼 SubFlow1/Work3: Ready → Going
  [14:32:19.567] 🔽 SubFlow1/Work3: Going → Done (1111.00ms)
```

## Verification

Check the database contents:

```bash
dotnet run verify
```

Expected output:

```
========================================
Database Verification
========================================

[Calls]
  - Work1 (MainFlow):
      State: Ready
      LastStartAt: 2025-03-22T14:32:15.123
      LastFinishAt: 2025-03-22T14:32:16.234
      LastDurationMs: 1111.00
  - Work3 (SubFlow1):
      State: Ready
      LastStartAt: 2025-03-22T14:32:18.456
      LastFinishAt: 2025-03-22T14:32:19.567
      LastDurationMs: 1111.00
```

## Integration with DSPilot.TestConsole

The real PLC is currently running test 4 on another PC:

```bash
cd DSPilot.TestConsole
dotnet run
# Select option: 4. AASX Flow Simulation
```

This test simulates all Flows in the AASX by sending signals to the PLC:

1. **OUT signal** → PLC receives output command (triggers OutTag rising edge)
2. **Wait 1s** → Simulated action delay
3. **SENSOR ON** → PLC sends sensor confirmation (triggers InTag rising edge)
4. **OUT OFF** → Action complete
5. **Wait 0.5s** → Sensor delay
6. **SENSOR OFF** → Ready for next cycle

## Key Files

- `RealPlcTest.cs` - Main test implementation
- `Program.cs` - Entry point with "realplc" command
- `DbVerifier.cs` - Database verification utility
- `PlcDbInspector.cs` - PLC database inspection tool

## Troubleshooting

### PLC Not Reachable

```
❌ Error: Connection timeout
```

**Solution**: Ensure PLC is powered on and network connectivity:
```bash
ping 192.168.9.120
```

### No Tag Changes Detected

**Possible causes**:
1. DSPilot.TestConsole not running test 4
2. AASX tag addresses don't match actual PLC tags
3. PLC tags are not changing values

**Solution**: Check tag addresses match between AASX and PLC configuration.

### Database Locked

```
❌ Error: database is locked
```

**Solution**: Ensure DSPilot application is not running simultaneously.

## Next Steps

This test validates that:

✅ AASX projects can be loaded and tag mappings extracted
✅ Real-time PLC connection works with Mitsubishi protocol
✅ Rising edge detection triggers correct state transitions
✅ Database updates persist Call states and durations

Future enhancements:

- [ ] Flow-level state management (ActiveCallCount)
- [ ] Multiple PLC support
- [ ] Historical statistics aggregation
- [ ] Real-time dashboard visualization

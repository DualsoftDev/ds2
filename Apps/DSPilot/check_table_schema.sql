-- Check plcTagLog table schema
PRAGMA table_info(plcTagLog);

-- Sample data
SELECT * FROM plcTagLog LIMIT 5;

-- Check plcTag table
PRAGMA table_info(plcTag);
SELECT * FROM plcTag LIMIT 5;

-- Check plc table
PRAGMA table_info(plc);
SELECT * FROM plc LIMIT 5;

using System;
using System.Collections.Generic;
using System.Linq;

namespace DSPilot.Engine.Tests.Console;

/// <summary>
/// Console table renderer for real-time status display
/// </summary>
public class ConsoleTable
{
    public class Row
    {
        public required string FlowName { get; init; }
        public required string CallName { get; init; }
        public required string State { get; init; }
        public string? LastStartAt { get; init; }
        public string? LastFinishAt { get; init; }
        public double? LastDurationMs { get; init; }
        public int CycleCount { get; init; }
    }

    private readonly List<Row> _rows = new();
    private readonly Dictionary<string, string> _previousStates = new();
    private readonly HashSet<string> _changedRows = new();
    private bool _isFirstRender = true;

    public void UpdateRow(Row row)
    {
        var key = $"{row.FlowName}:{row.CallName}";
        var existingIndex = _rows.FindIndex(r =>
            r.FlowName == row.FlowName && r.CallName == row.CallName);

        // Check if state changed
        if (_previousStates.TryGetValue(key, out var previousState))
        {
            if (previousState != row.State)
            {
                _changedRows.Add(key);
                _previousStates[key] = row.State;
            }
        }
        else
        {
            _previousStates[key] = row.State;
        }

        if (existingIndex >= 0)
        {
            _rows[existingIndex] = row;
        }
        else
        {
            _rows.Add(row);
        }
    }

    public void Render()
    {
        try
        {
            // Clear console and redraw entire table
            if (!_isFirstRender)
            {
                System.Console.Clear();
            }
            _isFirstRender = false;

            // Header
            System.Console.WriteLine("====================================================================================================================");
            System.Console.WriteLine("  Flow Name        Call Name         State      Last Start        Last Finish       Duration(ms)   Cycle Count   ");
            System.Console.WriteLine("====================================================================================================================");

            // Rows
            foreach (var row in _rows.OrderBy(r => r.FlowName).ThenBy(r => r.CallName))
            {
                var key = $"{row.FlowName}:{row.CallName}";
                var isChanged = _changedRows.Contains(key);

                var stateColor = row.State switch
                {
                    "Ready" => ConsoleColor.Gray,
                    "Going" => ConsoleColor.Yellow,
                    "Done" => ConsoleColor.Green,
                    _ => ConsoleColor.White
                };

                var flowName = row.FlowName.Length > 16 ? row.FlowName.Substring(0, 16) : row.FlowName;
                var callName = row.CallName.Length > 16 ? row.CallName.Substring(0, 16) : row.CallName;
                var startTime = row.LastStartAt != null ? DateTime.Parse(row.LastStartAt).ToString("HH:mm:ss") : "-";
                var finishTime = row.LastFinishAt != null ? DateTime.Parse(row.LastFinishAt).ToString("HH:mm:ss") : "-";
                var duration = row.LastDurationMs.HasValue ? $"{row.LastDurationMs.Value:F2}" : "-";

                // Highlight changed rows with background color
                if (isChanged)
                {
                    System.Console.BackgroundColor = ConsoleColor.DarkBlue;
                }

                // Row data
                System.Console.Write(isChanged ? "> " : "  ");
                System.Console.Write($"{flowName,-16}  ");
                System.Console.Write($"{callName,-16}  ");

                // State with color
                System.Console.ForegroundColor = stateColor;
                System.Console.Write($"{row.State,-9}");
                System.Console.ResetColor();
                if (isChanged) System.Console.BackgroundColor = ConsoleColor.DarkBlue;

                System.Console.Write("  ");
                System.Console.Write($"{startTime,-16}  ");
                System.Console.Write($"{finishTime,-16}  ");
                System.Console.Write($"{duration,-13}  ");
                System.Console.Write($"{row.CycleCount,-12}");

                // Reset colors
                System.Console.ResetColor();
                System.Console.WriteLine();
            }

            // Clear changed rows after rendering (show highlight for one cycle)
            _changedRows.Clear();

            System.Console.WriteLine("====================================================================================================================");
            System.Console.WriteLine();
            System.Console.ForegroundColor = ConsoleColor.Cyan;
            System.Console.WriteLine("Press 'Q' to quit...");
            System.Console.ResetColor();
        }
        catch
        {
            // If console operations fail, fall back to normal output
        }
    }

    public void Clear()
    {
        _rows.Clear();
        _isFirstRender = true;
    }
}

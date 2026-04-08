using System;
using System.Collections.Generic;
using System.Text;

namespace CostSim.Presentation;

public sealed class OutputPanelBuffer
{
    private readonly List<string> _activityLines = [];

    public string StatusMessage { get; private set; } = "Ready.";

    public void SetStatus(string message)
    {
        StatusMessage = string.IsNullOrWhiteSpace(message) ? "Ready." : message.Trim();
    }

    public void AppendActivity(string message, int maxLineCount = 200)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        _activityLines.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message.Trim()}");
        if (_activityLines.Count > maxLineCount)
            _activityLines.RemoveAt(_activityLines.Count - 1);
    }

    public string BuildOutputText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Status]");
        builder.AppendLine(StatusMessage);
        builder.AppendLine();
        builder.AppendLine("[Output]");

        if (_activityLines.Count == 0)
            builder.AppendLine("(no output)");
        else
            builder.Append(string.Join(Environment.NewLine, _activityLines));

        return builder.ToString().TrimEnd();
    }
}

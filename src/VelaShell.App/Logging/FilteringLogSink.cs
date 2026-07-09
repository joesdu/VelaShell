using System;
using Avalonia.Logging;

namespace VelaShell.App.Logging;

/// <summary>
/// Wraps another <see cref="ILogSink"/> and drops Dock.Avalonia's benign "DockCapability" binding
/// warnings. Dock's default control templates bind to capability attached-properties
/// (<c>DockCapabilityOverrides.*</c> / <c>Owner.DockCapabilityPolicy.*</c>) that are null unless a
/// capability policy is configured, producing "Value is null" binding diagnostics that are noise
/// only. Every other log message — including our own binding errors — is forwarded unchanged.
/// </summary>
public sealed class FilteringLogSink : ILogSink
{
    private const string NoiseMarker = "DockCapability";

    private readonly ILogSink _inner;

    public FilteringLogSink(ILogSink inner) => _inner = inner;

    public bool IsEnabled(LogEventLevel level, string area) => _inner.IsEnabled(level, area);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (ShouldDrop(area, messageTemplate, null))
            return;
        _inner.Log(level, area, source, messageTemplate);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (ShouldDrop(area, messageTemplate, propertyValues))
            return;
        _inner.Log(level, area, source, messageTemplate, propertyValues);
    }

    private static bool ShouldDrop(string area, string messageTemplate, object?[]? propertyValues)
    {
        if (!string.Equals(area, LogArea.Binding, StringComparison.Ordinal))
            return false;

        if (Mentions(messageTemplate))
            return true;

        if (propertyValues is not null)
        {
            foreach (var value in propertyValues)
            {
                if (value is not null && Mentions(value.ToString()))
                    return true;
            }
        }

        return false;
    }

    private static bool Mentions(string? text) =>
        text is not null && text.Contains(NoiseMarker, StringComparison.Ordinal);
}

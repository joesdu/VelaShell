namespace VelaShell.Presentation.Services;

public sealed record ConnectionTestResult(bool Success, string? ErrorMessage = null);

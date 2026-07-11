namespace RPGModder.Core.Models;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record OperationDiagnostic(
    string Code,
    string Message,
    DiagnosticSeverity Severity,
    string? Subject = null);

public sealed class OperationResult
{
    private readonly List<OperationDiagnostic> _diagnostics = new();

    public bool Success => _diagnostics.All(item => item.Severity != DiagnosticSeverity.Error);
    public IReadOnlyList<OperationDiagnostic> Diagnostics => _diagnostics;

    public void Add(OperationDiagnostic diagnostic)
    {
        _diagnostics.Add(diagnostic);
    }

    public void AddError(string code, string message, string? subject = null)
    {
        Add(new OperationDiagnostic(code, message, DiagnosticSeverity.Error, subject));
    }

    public void AddWarning(string code, string message, string? subject = null)
    {
        Add(new OperationDiagnostic(code, message, DiagnosticSeverity.Warning, subject));
    }
}


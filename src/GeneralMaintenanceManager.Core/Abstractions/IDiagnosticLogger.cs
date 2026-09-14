namespace GeneralMaintenanceManager.Core.Abstractions;

public interface IDiagnosticLogger
{
    void Info(string eventName, string message);
    void LogError(string eventName, Exception exception, string message);
}

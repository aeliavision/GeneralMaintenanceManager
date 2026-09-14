using CommunityToolkit.Mvvm.ComponentModel;

namespace GeneralMaintenanceManager.App.ViewModels;

public sealed class TextPromptDialogViewModel(string title, string label, string initialValue = "") : ObservableObject
{
    private string _value = initialValue ?? string.Empty;
    private string _validationMessage = string.Empty;

    public string Title { get; } = title ?? string.Empty;
    public string Label { get; } = label ?? string.Empty;
    public string Value { get => _value; set => SetProperty(ref _value, value ?? string.Empty); }
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }

    public bool ValidateRequired(string message)
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            ValidationMessage = message;
            return false;
        }
        ValidationMessage = string.Empty;
        return true;
    }
}

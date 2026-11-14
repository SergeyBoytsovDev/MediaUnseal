using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MediaUnseal.Services;
using MediaUnseal.ViewModels;

namespace MediaUnseal;

/// <summary>
/// Shows the existing fallback for view models without a registered view template.
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;
        
        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        return new TextBlock { Text = LocalizationManager.Active.Format("ViewNotFound", name) };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}

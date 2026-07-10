using ReactiveUI;
using VelaShell.Core.Models;

namespace VelaShell.ViewModels;

public class QuickCommandViewModel(QuickCommand model) : ReactiveObject
{
    private readonly QuickCommand _model = model ?? throw new ArgumentNullException(nameof(model));

    public Guid Id => _model.Id;

    public bool IsBuiltIn => _model.IsBuiltIn;

    public string Name
    {
        get;
        set
        {
            if (IsBuiltIn)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            _model.Name = value;
        }
    } = model.Name;

    public string Category
    {
        get;
        set
        {
            if (IsBuiltIn)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            _model.Category = value;
        }
    } = model.Category;

    public string CommandText
    {
        get;
        set
        {
            if (IsBuiltIn)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            _model.CommandText = value;
        }
    } = model.CommandText;

    public string Description
    {
        get;
        set
        {
            if (IsBuiltIn)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            _model.Description = value;
        }
    } = model.Description;

    public QuickCommand ToModel() => _model;
}

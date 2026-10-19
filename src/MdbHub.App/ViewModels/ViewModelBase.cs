using MdbHub.App.Infrastructure;

namespace MdbHub.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    private bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    protected bool NotBusy => !IsBusy;

    /// <summary>Runs <paramref name="work"/> with the busy overlay on and shows any error in a dialog.</summary>
    protected async Task RunAsync(Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

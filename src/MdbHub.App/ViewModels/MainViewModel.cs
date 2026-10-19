using MdbHub.App.Infrastructure;

namespace MdbHub.App.ViewModels;

public sealed class MainViewModel
{
    public MainViewModel()
    {
        var config = new ConfigService(ConfigService.DefaultPath);
        Browse = new BrowseViewModel();
        Extract = new ExtractViewModel(config);
        Apply = new ApplyViewModel(config);
        Compare = new CompareViewModel(config);
        Settings = new SettingsViewModel(config);

        // A broken config file is shown once up front; the app keeps running on defaults and never overwrites the file by itself.
        if (config.LoadError is not null) Dialogs.Error(config.LoadError + "\n\n기본 설정으로 실행합니다. 설정 탭에서 고친 뒤 저장하거나 파일을 직접 수정하세요.");
    }

    public BrowseViewModel Browse { get; }
    public ExtractViewModel Extract { get; }
    public ApplyViewModel Apply { get; }
    public CompareViewModel Compare { get; }
    public SettingsViewModel Settings { get; }
}

namespace AasxEditor.Components.Pages;

public partial class Home
{
    private const string LoggingSubmodelIdShort = "SequenceLogging";
    private const string ModelSubmodelIdShort = "SequenceModel";

    private bool _createLoggingBannerDismissed;

    private bool HasSequenceLoggingSubmodel
        => _currentEnv?.Submodels?.Any(sm => sm.IdShort == LoggingSubmodelIdShort) == true;

    private bool HasSequenceModelSubmodel
        => _currentEnv?.Submodels?.Any(sm => sm.IdShort == ModelSubmodelIdShort) == true;

    private bool ShouldShowCreateLoggingBanner
        => _contentLoaded
           && _currentEnv is not null
           && HasSequenceModelSubmodel
           && !HasSequenceLoggingSubmodel
           && !_createLoggingBannerDismissed;

    private void OnDismissCreateLoggingBanner() => _createLoggingBannerDismissed = true;
}

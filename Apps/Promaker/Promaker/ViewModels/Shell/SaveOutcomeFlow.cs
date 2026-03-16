using System;
using Microsoft.FSharp.Core;

namespace Promaker.ViewModels;

internal static class SaveOutcomeFlow
{
    public static bool TryCompleteMermaidSave(
        FSharpResult<Unit, string> result,
        Action<string> warn,
        Action onSuccess)
    {
        if (result.IsError)
        {
            warn(result.ErrorValue);
            return false;
        }

        onSuccess();
        return true;
    }

    public static bool TryCompleteAasxSave(
        bool exported,
        Action<string> warn,
        string failureMessage,
        Action onSuccess)
    {
        if (!exported)
        {
            warn(failureMessage);
            return false;
        }

        onSuccess();
        return true;
    }
}

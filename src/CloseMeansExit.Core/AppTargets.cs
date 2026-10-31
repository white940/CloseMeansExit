namespace CloseMeansExit.Core;

/// <summary>
/// Recognizes a known application's helper layout without granting termination
/// authority. The native layer must still prove the actual ancestor identity.
/// </summary>
public static class AppTargets
{
    private const string SteamHelperSuffix = @"\bin\cef\cef.win64\steamwebhelper.exe";

    public static string? ExpectedMainExecutable(string windowExecutablePath)
    {
        try
        {
            string helper = RulePaths.Normalize(windowExecutablePath);
            if (!helper.EndsWith(SteamHelperSuffix, StringComparison.OrdinalIgnoreCase)) return null;
            string installation = helper[..^SteamHelperSuffix.Length];
            return RulePaths.Normalize(installation + @"\Steam.exe");
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

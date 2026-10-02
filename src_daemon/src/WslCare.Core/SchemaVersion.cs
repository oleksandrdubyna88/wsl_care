namespace WslCare.Core;

/// <summary>
/// The version every JSON answer and every stored record carries (plan §6). The extension refuses a
/// major it does not know and says so, rather than reading fields that may have moved.
/// </summary>
public static class SchemaVersion
{
    public const int Current = 1;
}

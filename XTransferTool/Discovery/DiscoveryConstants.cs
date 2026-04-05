namespace XTransferTool.Discovery;

public static class DiscoveryConstants
{
    public const string ServiceType = "_xtransfer._tcp";
    public const string Domain = "local.";

    // TXT size varies by implementation; we keep conservative budget.
    public const int TxtBudgetBytes = 800;

    public static readonly string[] EssentialTxtKeys =
    [
        "id", "nickname", "tags", "os", "app", "ver"
    ];
}


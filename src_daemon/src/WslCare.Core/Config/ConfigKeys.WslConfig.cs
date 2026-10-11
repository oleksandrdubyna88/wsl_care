namespace WslCare.Core.Config;

/// <summary>The <c>wslConfig.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static class WslConfig
    {
        /// <summary>The .wslconfig memory the audit recommends (shown, never applied). Default 36.</summary>
        public static readonly ConfigKey.IntKey RecommendedMemoryGb = new("wslConfig.recommendedMemoryGb", 1, 1024) { Trust = KeyTrust.Display };

        /// <summary>E14 S5: the <c>.wslconfig</c> swap the advice recommends (shown, never written); 0 advises no swap line. Default
        /// 16 (the 2026-10-07 evening used 9.9 of 12 GB).</summary>
        public static readonly ConfigKey.IntKey RecommendedSwapGb = new("wslConfig.recommendedSwapGb", 0, 1024) { Trust = KeyTrust.Display };

        /// <summary>E14 S7a: above this many GiB held by <c>vmmemWSL</c>, the Windows binary's <c>status</c> shows the reclaim advice
        /// (text, never acted on). Default 24.</summary>
        public static readonly ConfigKey.IntKey VmmemAdviceGb = new("wslConfig.vmmemAdviceGb", 1, 1024) { Trust = KeyTrust.Display };
    }
}

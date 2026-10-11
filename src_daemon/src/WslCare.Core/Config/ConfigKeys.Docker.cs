namespace WslCare.Core.Config;

/// <summary>The <c>docker.*</c> number keys (E7.S2c), in their own file: one file per key group, split from
/// <c>ConfigKeys.Numbers.cs</c> (the doctrine caps a file at 800 lines). They join <c>NumberKeys()</c> like every other
/// number key.</summary>
public static partial class ConfigKeys
{
    public static partial class Docker
    {
        /// <summary>docker version / network inspect / help. Default 10.</summary>
        public static readonly ConfigKey.IntKey ProbeTimeoutSeconds = new("docker.probeTimeoutSeconds", 1, 10) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>docker volume ls / ps / stats / inspect. Default 30.</summary>
        public static readonly ConfigKey.IntKey ListTimeoutSeconds = new("docker.listTimeoutSeconds", 5, 30) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>docker system df. Default 120.</summary>
        public static readonly ConfigKey.IntKey DiskUsageTimeoutSeconds = new("docker.diskUsageTimeoutSeconds", 10, 120) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>docker volume rm / rm -v (one batch). Default 300.</summary>
        public static readonly ConfigKey.IntKey RemoveTimeoutSeconds = new("docker.removeTimeoutSeconds", 30, 300) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>docker image / builder prune. Default 900.</summary>
        public static readonly ConfigKey.IntKey PruneTimeoutSeconds = new("docker.pruneTimeoutSeconds", 60, 900) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Ids in one inspect or rm command. Default 100.</summary>
        public static readonly ConfigKey.IntKey BatchSize = new("docker.batchSize", 1, 100) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of docker version, system df, network inspect, help. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey SmallOutputCapBytes = new("docker.smallOutputCapBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of docker system df -v, volume ls, ps -a, stats. Default 67108864.</summary>
        public static readonly ConfigKey.IntKey LargeOutputCapBytes = new("docker.largeOutputCapBytes", 1048576, 67108864) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Output kept of a Docker cleanup command. Default 4194304.</summary>
        public static readonly ConfigKey.IntKey ActionOutputCapBytes = new("docker.actionOutputCapBytes", 65536, 4194304) { Trust = new(SafeDirection.Lower, MachineOnly: true) };

        /// <summary>Bytes of Docker Desktop's daemon.json read. Default 1048576.</summary>
        public static readonly ConfigKey.IntKey MaxDaemonJsonBytes = new("docker.maxDaemonJsonBytes", 65536, 1048576) { Trust = new(SafeDirection.Lower, MachineOnly: true) };
    }
}

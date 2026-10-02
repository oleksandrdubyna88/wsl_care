# Read-only: kernel pool usage by tag (SystemPoolTagInformation) and NP pool charged per process.
$src = @"
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public static class Pool {
  [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
  public struct Tag { public string Name; public long NpBytes; public long NpAllocs; public long NpFrees; public long PBytes; }
  public static List<Tag> Read(out int status) {
    int len = 1 << 20; IntPtr buf = IntPtr.Zero; int ret; status = 0;
    for (int i = 0; i < 8; i++) {
      buf = Marshal.AllocHGlobal(len);
      status = NtQuerySystemInformation(22, buf, len, out ret);
      if (status == unchecked((int)0xC0000004)) { Marshal.FreeHGlobal(buf); len = Math.Max(ret, len * 2); continue; }
      break;
    }
    var list = new List<Tag>();
    if (status != 0) { Marshal.FreeHGlobal(buf); return list; }
    int count = Marshal.ReadInt32(buf);
    // SYSTEM_POOLTAG on x64: Tag[4], PagedAllocs u32, PagedFrees u32, PagedUsed SIZE_T, NonPagedAllocs u32, NonPagedFrees u32, NonPagedUsed SIZE_T = 40 bytes
    int off = 8;
    for (int i = 0; i < count; i++) {
      IntPtr p = buf + off + i * 40;
      byte[] t = new byte[4]; Marshal.Copy(p, t, 0, 4);
      var tag = new Tag();
      tag.Name = System.Text.Encoding.ASCII.GetString(t);
      tag.PBytes = Marshal.ReadInt64(p + 16);
      tag.NpAllocs = (uint)Marshal.ReadInt32(p + 24);
      tag.NpFrees = (uint)Marshal.ReadInt32(p + 28);
      tag.NpBytes = Marshal.ReadInt64(p + 32);
      list.Add(tag);
    }
    Marshal.FreeHGlobal(buf);
    return list;
  }
}
"@
Add-Type -TypeDefinition $src -Language CSharp
$st = 0; $tags = [Pool]::Read([ref]$st)
"NtQuerySystemInformation status: 0x{0:X8}; tags: {1}" -f $st, $tags.Count
"NP total by tags: {0:N2} GB; paged total: {1:N2} GB" -f (($tags | Measure-Object NpBytes -Sum).Sum/1GB), (($tags | Measure-Object PBytes -Sum).Sum/1GB)
"`n=== Top 20 NON-PAGED tags ==="
$tags | Sort-Object NpBytes -Descending | Select-Object -First 20 @{n='Tag';e={$_.Name}}, @{n='NP_MB';e={[math]::Round($_.NpBytes/1MB,1)}}, @{n='LiveAllocs';e={$_.NpAllocs - $_.NpFrees}}, NpAllocs | Format-Table -AutoSize
"`n=== Top 10 PAGED tags ==="
$tags | Sort-Object PBytes -Descending | Select-Object -First 10 @{n='Tag';e={$_.Name}}, @{n='P_MB';e={[math]::Round($_.PBytes/1MB,1)}} | Format-Table -AutoSize
"`n=== NP pool charged per process (top 15) ==="
Get-Process | Sort-Object NonpagedSystemMemorySize64 -Descending | Select-Object -First 15 Name, Id, @{n='NP_KB';e={[math]::Round($_.NonpagedSystemMemorySize64/1KB)}}, @{n='Paged_MB';e={[math]::Round($_.PagedSystemMemorySize64/1MB,1)}}, Handles | Format-Table -AutoSize
"Sum NP charged to processes: {0:N1} MB" -f ((Get-Process | Measure-Object NonpagedSystemMemorySize64 -Sum).Sum/1MB)
"Counter now: NP {0:N2} GB" -f ((Get-Counter '\Memory\Pool Nonpaged Bytes').CounterSamples[0].CookedValue/1GB)

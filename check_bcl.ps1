# BCL member-reference audit: scans the compiled plugin for memberrefs into
# mscorlib/System/System.Core and flags anything that is not a .NET 2.0-era
# API (the main game runs Unity 5.6 old Mono / .NET 3.5-profile BCL).
# Known-dangerous: op_Equality/op_Inequality on reflection types and Type
# (added in .NET 4.0). System.String's operators are ancient and fine.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
Add-Type -Path (Join-Path $root "BepInEx\core\Mono.Cecil.dll")

$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $here "COM3D2.SceneUndo.dll"))
$refs = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($t in $asm.MainModule.Types) {
  foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    foreach ($i in $m.Body.Instructions) {
      if ($null -eq $i.Operand) { continue }
      $mr = $i.Operand -as [Mono.Cecil.MemberReference]
      if ($null -eq $mr) { continue }
      $dt = $mr.DeclaringType
      if ($null -eq $dt) { continue }
      if ($dt.Scope -is [Mono.Cecil.AssemblyNameReference] -and
          $dt.Scope.Name -in @("mscorlib", "System", "System.Core")) {
        [void]$refs.Add($dt.FullName + "::" + $mr.Name)
      }
    }
  }
}
Write-Output "=== BCL member references ==="
$refs | Sort-Object | ForEach-Object { Write-Output ("  " + $_) }
Write-Output ("TOTAL: " + $refs.Count)
$bad = $refs | Where-Object { ($_ -match "op_(In)?Equality") -and ($_ -notmatch "System.String::") }
if ($bad) { Write-Output "DANGER: .NET4-only operator refs:"; $bad | ForEach-Object { Write-Output ("  " + $_) }; exit 1 }
Write-Output "OK: no .NET4-only operator memberrefs"

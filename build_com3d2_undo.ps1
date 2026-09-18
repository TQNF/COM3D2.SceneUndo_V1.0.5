$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
Set-Location $root

# compile target = OLDEST supported game assembly (OH 2.0-era) so all hard
# memberrefs also bind on 3.38; UnityEngine = 3.38 monolithic (Unity 5.6)
$ohManaged = Join-Path $root "COM3D2OHx64_Data\Managed"
$mainManaged = Join-Path $root "COM3D2x64_Data\Managed"

$src = [System.IO.File]::ReadAllText((Join-Path $here "COM3D2.SceneUndo.cs"))
$refs = @(
  (Join-Path $root "BepInEx\core\BepInEx.dll"),
  (Join-Path $root "BepInEx\core\0Harmony.dll"),
  (Join-Path $ohManaged "Assembly-CSharp.dll"),
  (Join-Path $mainManaged "UnityEngine.dll"),
  "System.dll",
  "System.Core.dll"
)
$outDll = Join-Path $here "COM3D2.SceneUndo.dll"
if (Test-Path $outDll) { Remove-Item $outDll -Force }
Add-Type -TypeDefinition $src -ReferencedAssemblies $refs -OutputAssembly $outDll -OutputType Library
Write-Output ("COMPILED: " + $outDll + " (" + (Get-Item $outDll).Length + " bytes)")

# ---- HARD GATE: BCL member-reference audit (same logic as check_bcl.ps1) ----
# The game runs Unity 5.6 old Mono / CLR 2.0 (.NET 3.5-profile BCL). Any
# memberref into mscorlib/System/System.Core that only exists on .NET 4.0+
# kills the method at JIT time IN GAME (the driver tests on .NET 4.x cannot
# catch this). Known trap: == / != on Type/MethodInfo/FieldInfo etc.
# compiles to op_Equality/op_Inequality memberrefs (.NET 4.0-only; String's
# are .NET 1.0 and safe). v1.0.4 shipped exactly this bug - never again:
# on failure the DLL is DELETED so a bad build cannot be deployed.
Add-Type -Path (Join-Path $root "BepInEx\core\Mono.Cecil.dll")
# Cecil has NO ReadAssembly(byte[]) overload (string/Stream only) - read the
# bytes ourselves and hand it a MemoryStream (also guarantees no file lock
# when the gate below deletes the DLL on audit failure)
$dllBytes = [byte[]][System.IO.File]::ReadAllBytes($outDll)
$dllStream = New-Object System.IO.MemoryStream(,$dllBytes)
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dllStream)
$brefs = New-Object 'System.Collections.Generic.HashSet[string]'
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
        [void]$brefs.Add($dt.FullName + "::" + $mr.Name)
      }
    }
  }
}
$bad = @($brefs | Where-Object { ($_ -match "op_(In)?Equality") -and ($_ -notmatch "System.String::") })
if ($bad.Count -gt 0) {
  Write-Output "BCL AUDIT FAILED - .NET4-only operator memberrefs found:"
  $bad | ForEach-Object { Write-Output ("  " + $_) }
  Remove-Item $outDll -Force
  throw "BUILD REJECTED: fix the op_Equality/op_Inequality comparisons (use type-NAME string compare or object.ReferenceEquals)"
}
Write-Output ("BCL AUDIT PASSED: " + $brefs.Count + " BCL memberrefs, only System.String operators.")

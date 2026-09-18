$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
Set-Location $root
$ohManaged = Join-Path $root "COM3D2OHx64_Data\Managed"
$mainManaged = Join-Path $root "COM3D2x64_Data\Managed"
$bepCore = Join-Path $root "BepInEx\core"

# resolve game assemblies for the driver's compile-time references
$script:dirs = @($ohManaged, $mainManaged, $bepCore)
$script:busy = New-Object 'System.Collections.Generic.HashSet[string]'
$handler = [ResolveEventHandler]{
  param($s, $e)
  $n = (New-Object System.Reflection.AssemblyName($e.Name)).Name
  if (-not $script:busy.Add($n)) { return $null }
  try {
    foreach ($d in $script:dirs) {
      $f = Join-Path $d ($n + ".dll")
      if (Test-Path -LiteralPath $f) { return [System.Reflection.Assembly]::LoadFrom($f) }
    }
    return $null
  } finally { [void]$script:busy.Remove($n) }
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($handler)
try {
  Add-Type -TypeDefinition ([System.IO.File]::ReadAllText((Join-Path $here "Driver.cs"))) -ReferencedAssemblies @("mscorlib.dll", "System.dll", "System.Core.dll", (Join-Path $ohManaged "Assembly-CSharp.dll"), (Join-Path $mainManaged "UnityEngine.dll"))
} finally {
  [System.AppDomain]::CurrentDomain.remove_AssemblyResolve($handler)
}
$results = [Driver]::Run((Join-Path $here "COM3D2.SceneUndo.dll"), $ohManaged, $mainManaged, $bepCore)
foreach ($r in $results) { Write-Output ("  " + $r) }
$fails = @($results | Where-Object { $_ -like "FAIL*" })
Write-Output ("TOTAL: " + $results.Count + "  FAILED: " + $fails.Count)
if ($fails.Count -gt 0) { exit 1 }

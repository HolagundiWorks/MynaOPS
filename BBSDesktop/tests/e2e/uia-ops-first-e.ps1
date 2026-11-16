param(
  [Parameter(Mandatory = $true)][string]$OpsExe,     # O-CoreOp.exe (the MynaOps build under test)
  [Parameter(Mandatory = $true)][string]$CadExe,     # ODraft.WinUi.exe (the real MynaCAD build under test, aad_host.dll beside it)
  [Parameter(Mandatory = $true)][string]$Sandbox,    # an empty scratch folder: local state, projects, logs
  [string]$Shots = ""                                # folder for screenshots (optional)
)
# Ops-first package E, real MynaOps and real MynaCAD (docs/ops-first-workflow.md 16): a project made by MynaCAD alone is opened the way a double-click
# opens it (O-CoreOp.exe <osp>), adopted (with MynaCAD reading the drawing), opened in MynaCAD, a second start is forwarded, MynaCAD started with a
# DXF or an .osp hands over to MynaOps, close both. PASS/FAIL lines; nothing outside $Sandbox is touched (OSTUDIO_LOCAL_DIR moves the local state there).
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$PC = [System.Windows.Automation.PropertyCondition]
$root = $AE::RootElement
$script:fails = 0
function Check($name, $ok, $detail = "") {
  if (-not $ok) { $script:fails++ }
  "{0,-5} {1} {2}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $name, $detail
}
function Wait-Until([scriptblock]$cond, [int]$ms = 15000) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $ms) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 200 }
  return $false
}
function Find-Id($parent, [string]$id, [int]$ms = 8000) {
  $cond = New-Object $PC($AE::AutomationIdProperty, $id)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  do { $e = $parent.FindFirst($TS::Descendants, $cond); if ($e) { return $e }; Start-Sleep -Milliseconds 150 } while ($sw.ElapsedMilliseconds -lt $ms)
  return $null
}
function Find-Name($parent, [string]$name, [int]$ms = 8000) {
  $cond = New-Object $PC($AE::NameProperty, $name)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  do { $e = $parent.FindFirst($TS::Descendants, $cond); if ($e) { return $e }; Start-Sleep -Milliseconds 150 } while ($sw.ElapsedMilliseconds -lt $ms)
  return $null
}
function Get-Win([int]$procId, [string]$like, [int]$ms = 20000) {
  $cond = New-Object $PC($AE::ProcessIdProperty, $procId)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  do {
    foreach ($w in $root.FindAll($TS::Children, $cond)) {
      if ($w.Current.Name -like $like) { return $w }
      # an owned (modal) window is listed under its owner, not at the top
      $wc = New-Object $PC($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
      foreach ($o in $w.FindAll($TS::Descendants, $wc)) { if ($o.Current.Name -like $like) { return $o } }
    }
    Start-Sleep -Milliseconds 250
  } while ($sw.ElapsedMilliseconds -lt $ms)
  return $null
}
function Invoke-El($e) {
  if (-not $e) { return $false }
  $p = $null
  if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return $true }
  if ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); return $true }
  if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return $true }
  return $false
}
function Set-Text($e, [string]$v) {
  if (-not $e) { return $false }
  $p = $null
  if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { $p.SetValue($v); return $true }
  return $false
}
function Text-Of($e) { if (-not $e) { return $null }; return $e.Current.Name }
function Shot($win, [string]$name) {
  if (-not $Shots) { return }
  New-Item -ItemType Directory -Force $Shots | Out-Null
  try {
    $r = $win.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Shots "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
  } catch { "(screenshot $name failed: $($_.Exception.Message))" }
}
function Sql([string]$db, [string]$q) { python -I -c "import sqlite3,sys; c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro', uri=True); print(c.execute(sys.argv[2]).fetchone()[0])" $db $q }


$Sandbox = (New-Item -ItemType Directory -Force $Sandbox).FullName
$local = Join-Path $Sandbox "local"; $projects = Join-Path $Sandbox "projects"; $src = Join-Path $Sandbox "source"
New-Item -ItemType Directory -Force $local, $projects, $src | Out-Null
$env:OSTUDIO_LOCAL_DIR = $local
$env:OSTUDIO_CAD_EXE = $CadExe
$env:AAD_RECOVERY_DIR = Join-Path $Sandbox "recovery"
$env:AAD_CONTROL_PORT = "47955"
$env:OSTUDIO_AI_ROLE = ""
$leases = Join-Path $local "leases"
$Under = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OpsExe)))  # only processes started from the build folder next to the MynaOps build are touched
function Ctl([int]$port, [string]$method, $params) {
  $c = New-Object System.Net.Sockets.TcpClient("127.0.0.1", $port)
  $s = $c.GetStream(); $w = New-Object System.IO.StreamWriter($s); $r = New-Object System.IO.StreamReader($s)
  $w.AutoFlush = $true
  $w.WriteLine((@{ id = 1; method = $method; params = $params } | ConvertTo-Json -Compress -Depth 8))
  $line = $r.ReadLine(); $c.Close(); return $line
}
function Mine([string]$name) { @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Under, [StringComparison]::OrdinalIgnoreCase) }) }
function Count-Proc([string]$name) { (Mine $name).Count }

"== E1. a project made by MynaCAD alone (no configuration), and a stale pointer file"
$legacy = Join-Path $projects "Legacy.osp"
$cad0 = Start-Process -FilePath $CadExe -ArgumentList @("--standalone", "--control", "47954") -PassThru
$env:AAD_CONTROL_PORT = "47955"
Wait-Until { try { (Ctl 47954 "ping" @{}) -ne $null } catch { $false } } 30000 | Out-Null
Ctl 47954 "engine.execute" @{ command = "level.new"; params = @{ name = "Ground"; elevation = 0; height = 3000 } } | Out-Null
Ctl 47954 "engine.execute" @{ command = "level.new"; params = @{ name = "First"; elevation = 3000; height = 3000 } } | Out-Null
Ctl 47954 "engine.execute" @{ command = "line"; params = @{ points = @(@(0,0), @(5000,0)) } } | Out-Null
Ctl 47954 "app.save" @{ path = $legacy } | Out-Null
$lv = Ctl 47954 "engine.execute" @{ command = "level.list"; params = @{} }
Check "standalone MynaCAD saved the legacy project" (Test-Path $legacy)
Ctl 47954 "app.quit" @{} | Out-Null
Wait-Until { $cad0.HasExited } 20000 | Out-Null
if (-not $cad0.HasExited) { $cad0.Kill() }
Check "the legacy file has no configuration" ((Sql $legacy "select count(*) from meta where key='config.schema'") -eq "0")
$hash0 = Sql $legacy "select sha256 from drawing"; $bytes0 = Sql $legacy "select hex(bytes) from drawing"
New-Item -ItemType Directory -Force $local | Out-Null
Set-Content (Join-Path $local "active-project.json") '{"format":"studio-active-project","version":1,"project":"C:\\x\\old.osp"}'

"== E2. double-click path: O-CoreOp.exe <project.osp> opens MynaOps with the Adopt window"
$app = Start-Process -FilePath $OpsExe -ArgumentList @("`"$legacy`"") -PassThru
$ad = Get-Win $app.Id "Adopt project settings"
Check "Adopt window appears for the file given on the command line" ($null -ne $ad)
Check "nothing is written before Adopt" ((Sql $legacy "select count(*) from meta where key='config.schema'") -eq "0")
Check "the stale active-project.json was removed at start" (Wait-Until { -not (Test-Path (Join-Path $local "active-project.json")) } 10000)
Shot $ad "e2-adopt-window"

"== E3. a second start forwards to the running MynaOps (no second window)"
$before = Count-Proc "O-CoreOp"
$second = Start-Process -FilePath $OpsExe -ArgumentList @("`"$legacy`"") -PassThru -Wait
Check "the second start exited with code 0" ($second.ExitCode -eq 0) ("exit " + $second.ExitCode)
Check "no second MynaOps process remains" ((Count-Proc "O-CoreOp") -eq $before) ("before $before, now " + (Count-Proc "O-CoreOp"))

"== E4. read the drawing in MynaCAD, then Adopt"
$read = Find-Id $ad "Adopt.ReadCad" 3000
if ($read) {
  Invoke-El $read | Out-Null
  Check "MynaCAD wrote its proposal (config.proposal)" (Wait-Until { (Sql $legacy "select count(*) from meta where key='config.proposal'") -eq "1" } 90000)
  # the window is made again with the proposal: wait for the one that lists the levels
  Wait-Until { $script:ad2 = Get-Win $app.Id "Adopt project settings" 3000; $script:ad2 -and (Find-Name $script:ad2 "Levels (3)" 1000) } 60000 | Out-Null
  if ($script:ad2) { $ad = $script:ad2 }
}
# the legacy drawing has its implicit "Level 1" (the line) and the two levels made with level.new
Check "the Adopt window lists the three levels MynaCAD wrote" ($null -ne (Find-Name $ad "Levels (3)" 20000))
Shot $ad "e4-adopt-with-proposal"
Invoke-El (Find-Id $ad "Adopt.Adopt") | Out-Null
Check "config written by Adopt" (Wait-Until { (Sql $legacy "select count(*) from meta where key='config.schema'") -eq "1" })
Check "drawing row hash unchanged" ((Sql $legacy "select sha256 from drawing") -eq $hash0)
Check "drawing bytes unchanged" ((Sql $legacy "select hex(bytes) from drawing") -eq $bytes0)
$bk = @(Get-ChildItem (Join-Path $projects "Legacy.backups") -Filter "* manual.osp" -ErrorAction SilentlyContinue)
Check "a manual backup was made first" ($bk.Count -eq 1)

"== E5. Open MynaCAD: managed run, levels bound, take-off bridge"
$win = Get-Win $app.Id "*Legacy*"
Check "project is open on Home" ($null -ne $win -and $null -ne (Find-Id $win "Home.Path"))
Invoke-El (Find-Id $win "Home.OpenCad") | Out-Null
Check "MynaCAD holds the project (.cad.lock)" (Wait-Until { @(Get-ChildItem $leases -Filter "*.cad.lock" -ErrorAction SilentlyContinue).Count -eq 1 } 60000)
$ok = Wait-Until { try { (Ctl 47955 "ping" @{}) -ne $null } catch { $false } } 60000
$lv = Ctl 47955 "engine.execute" @{ command = "level.list"; params = @{} }
"level.list: $lv"
Check "MynaCAD shows the drawing's levels" ($lv -match "Ground" -and $lv -match "First" -and $lv -match "Level 1")
Check "configuration applied with no orphans" ((Sql $legacy "select json_extract(value,'$.result') from meta where key='config.applied.cad'") -eq "ok")
Ctl 47955 "engine.execute" @{ command = "takeoff.record"; params = @{ levels = "all" } } | Out-Null
Ctl 47955 "app.save" @{} | Out-Null
Check "take-off header written" (Wait-Until { [int](Sql $legacy "select count(*) from takeoff_header") -ge 1 } 20000)

"== E6. MynaCAD started with a DXF and with an .osp (launch screen hand-over)"
$dxf = Join-Path $src "plan.dxf"
"0`nSECTION`n2`nENTITIES`n0`nLINE`n8`n0`n10`n0.0`n20`n0.0`n11`n10.0`n21`n0.0`n0`nENDSEC`n0`nEOF" | Set-Content $dxf
$beforeFiles = @(Get-ChildItem $src).Count
$env:AAD_CONTROL_PORT = ""
$h = Start-Process -FilePath $CadExe -ArgumentList @("`"$dxf`"") -PassThru
Wait-Until { $h.HasExited } 20000 | Out-Null
Check "MynaCAD with a DXF handed over and exited (no project opened)" ($h.HasExited)
$pick = Get-Win $app.Id "Import plan.dxf" 20000
Check "MynaOps asks where the drawing goes" ($null -ne $pick)
Shot $pick "e6-import-pick"
$names = if ($pick) { ($pick.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join " | " } else { "" }
Check "the dialog offers import into the project, new project, cancel" ($names -match "Import into project" -and $names -match "New project" -and $names -match "Cancel") $names
# New project from this file: the wizard opens named after the drawing (checked, then cancelled: the full wizard run is in uia-ops-first.ps1)
Invoke-El (Find-Name $pick "New project" 3000) | Out-Null
$leave = Find-Name $win "Leave it open" 8000
Check "leaving the project that MynaCAD has open is asked first" ($null -ne $leave)
Invoke-El $leave | Out-Null
$wiz = Get-Win $app.Id "New project" 15000
$VP = [System.Windows.Automation.ValuePattern]::ValueProperty
$nm = if ($wiz) { [string](Find-Id $wiz "NewProject.Name").GetCurrentPropertyValue($VP) } else { "(no window)" }
$fl = if ($wiz) { [string](Find-Id $wiz "NewProject.File").GetCurrentPropertyValue($VP) } else { "(no window)" }
Check "New project from this file opens the wizard named after the drawing" ($null -ne $wiz -and $nm -eq "plan" -and $fl -eq "plan") "name=[$nm] file=[$fl]"
Shot $wiz "e6-new-from-file-wizard"
Invoke-El (Find-Id $wiz "NewProject.Cancel") | Out-Null
Check "cancelling the wizard closes it and creates nothing" ((Wait-Until { $null -eq (Get-Win $app.Id "New project" 1000) } 10000) -and @(Get-ChildItem $projects | Where-Object { $_.Extension -eq ".osp" }).Count -eq 1) (($projects | Get-ChildItem | ForEach-Object Name) -join ",")
Check "no file appeared beside the DXF" (@(Get-ChildItem $src).Count -eq $beforeFiles)
$h2 = Start-Process -FilePath $CadExe -ArgumentList @("`"$legacy`"") -PassThru
Wait-Until { $h2.HasExited } 20000 | Out-Null
Check "MynaCAD with an .osp handed over and exited" ($h2.HasExited)
Check "MynaCAD did not open the project itself (one MynaCAD, the managed one)" ((Count-Proc "ODraft.WinUi") -eq 1)

"== E7. close both"
$wp = $null; $win.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$wp) | Out-Null; $wp.Close()
$q = Get-Win $app.Id "*MynaCAD has this project open*" 8000
if ($q) { Invoke-El (Find-Name $q "Close both" 3000) | Out-Null } else { $dlg = $win.FindFirst($TS::Descendants, (New-Object $PC($AE::NameProperty, "Close both"))); Invoke-El $dlg | Out-Null }
Wait-Until { $app.HasExited } 130000 | Out-Null
Check "both ended" ($app.HasExited -and (Count-Proc "ODraft.WinUi") -eq 0)
function Lease-Held($f) { try { $fs = [IO.File]::Open($f, "Open", "ReadWrite", "ReadWrite, Delete"); $fs.Dispose(); $false } catch [IO.IOException] { $true } }
Check "no lease held (a lock file left behind is not a lease)" (@(Get-ChildItem $leases -Filter *.lock -ErrorAction SilentlyContinue | Where-Object { Lease-Held $_.FullName }).Count -eq 0)
(Mine "O-CoreOp") + (Mine "ODraft.WinUi") | Stop-Process -Force -ErrorAction SilentlyContinue
"`nDONE, failures: $script:fails"
exit $script:fails

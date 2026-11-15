param(
  [Parameter(Mandatory = $true)][string]$Exe,        # O-CoreOp.exe (the MynaOps build under test)
  [Parameter(Mandatory = $true)][string]$CadStub,    # ODraft.WinUi.exe of tests\e2e\CadStub (stands in for MynaCAD managed mode)
  [Parameter(Mandatory = $true)][string]$Sandbox,    # an empty scratch folder: local state, projects, logs
  [string]$Shots = ""                                # folder for screenshots (optional)
)
# Ops-first package A, driven through UI Automation (docs/ops-first-workflow.md section 7a): create a project in the wizard, see the project home, Open MynaCAD
# (the stub records its arguments), press it again (activation), AI Stop/Start, Database offline/online, adopt a legacy project, close both.
# Everything the script saw is printed as PASS/FAIL lines; nothing outside $Sandbox is touched (OSTUDIO_LOCAL_DIR moves the app's local state there).
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
$local = Join-Path $Sandbox "local"; $projects = Join-Path $Sandbox "projects"; $log = Join-Path $Sandbox "cadstub.log"
New-Item -ItemType Directory -Force $local, $projects | Out-Null
$env:OSTUDIO_LOCAL_DIR = $local
$env:OSTUDIO_CAD_EXE = $CadStub
$env:CADSTUB_LOG = $log
$env:OSTUDIO_AI_ROLE = ""                      # the app hosts the AI agent itself when the port is free
$leases = Join-Path $local "leases"

"== 1. start MynaOps with no project"
$app = Start-Process -FilePath $Exe -PassThru
$win = Get-Win $app.Id "MynaOps*"
Check "main window appears" ($null -ne $win)
$welcome = Find-Id $win "Welcome.New"
Check "welcome card offers New project and Open project" ($null -ne $welcome -and $null -ne (Find-Id $win "Welcome.Open" 2000))
Shot $win "01-welcome"

"== 2. New project wizard"
Invoke-El $welcome | Out-Null
$wiz = Get-Win $app.Id "New project"
Check "wizard opens in a window of its own" ($null -ne $wiz)
Shot $wiz "02-wizard-project"
Set-Text (Find-Id $wiz "NewProject.Name") "UIA House" | Out-Null
Set-Text (Find-Id $wiz "NewProject.Folder") $projects | Out-Null
Set-Text (Find-Id $wiz "NewProject.Client") "R. Rao" | Out-Null
Set-Text (Find-Id $wiz "NewProject.Location") "Hospet" | Out-Null
Check "file name follows the project name" ((Find-Id $wiz "NewProject.File").GetCurrentPropertyValue([System.Windows.Automation.ValuePattern]::ValueProperty) -eq "UIA House")
Invoke-El (Find-Id $wiz "NewProject.Next") | Out-Null   # Team
Set-Text (Find-Id $wiz "NewProject.Architect") "A. Kulkarni" | Out-Null
Invoke-El (Find-Id $wiz "NewProject.Next") | Out-Null   # Building
Check "building page lists generated levels" ($null -ne (Find-Id $wiz "NewProject.Level2.Name" 3000))
Shot $wiz "03-wizard-building"
Invoke-El (Find-Id $wiz "NewProject.Next") | Out-Null   # Standards
Invoke-El (Find-Id $wiz "NewProject.Next") | Out-Null   # Review
$review = Text-Of (Find-Id $wiz "NewProject.Review")
Check "review page names the project and its levels" ($review -like "*UIA House*" -and $review -like "*Ground Floor*") $review
Shot $wiz "04-wizard-review"
$osp = Join-Path $projects "UIA House.osp"
Invoke-El (Find-Id $wiz "NewProject.Create") | Out-Null
Check "project file created" (Wait-Until { Test-Path -LiteralPath $osp })
Check "wizard window closed" (Wait-Until { $null -eq (Get-Win $app.Id "New project" 500) } 5000)
Check "file has config.schema and config.rev 1" ((Sql $osp "select count(*) from meta where key='config.schema'") -eq "1" -and (Sql $osp "select json_extract(value,'$.rev') from meta where key='config.rev'") -eq "1")
Check "meta.revision is 3 or less (config in one revision, then the project document)" ([int](Sql $osp "select value from meta where key='revision'") -le 3) (Sql $osp "select value from meta where key='revision'")
Check "levels written (Ground Floor first)" ((Sql $osp "select json_extract(value,'$.items[0].name') from meta where key='config.levels'") -eq "Ground Floor")
Check "ops lease file exists" (@(Get-ChildItem $leases -Filter *.ops.lock -ErrorAction SilentlyContinue).Count -eq 1)

"== 3. project home"
$win = Get-Win $app.Id "*UIA House*"
Check "window titled with the project" ($null -ne $win)
Check "Home shows the project path" ((Text-Of (Find-Id $win "Home.Path")) -eq $osp) (Text-Of (Find-Id $win "Home.Path"))
Check "Home lists the levels" ((Text-Of (Find-Id $win "Home.Levels")) -like "*Ground Floor*First Floor*") (Text-Of (Find-Id $win "Home.Levels"))
Check "Home says MynaCAD is not running" ((Text-Of (Find-Id $win "Home.CadState")) -like "*not running*")
Check "Database online" ((Text-Of (Find-Id $win "Home.DbState")) -like "Database: online*")
Shot $win "05-home"

"== 4. Open MynaCAD"
Invoke-El (Find-Id $win "Home.OpenCad") | Out-Null
Check "stub started and recorded its arguments" (Wait-Until { (Test-Path $log) -and ((Get-Content $log -Raw) -match "start args") })
$args1 = (Get-Content $log | Where-Object { $_ -match "start args" } | Select-Object -First 1)
"      $args1"
Check "arguments are --managed --project <osp> --ops-pid <MynaOps pid>" ($args1 -like "*start args: --managed --project `"$osp`" --ops-pid $($app.Id)")
Check "MynaCAD lease held" (Wait-Until { @(Get-ChildItem $leases -Filter *.cad.lock -ErrorAction SilentlyContinue).Count -eq 1 })
Check "Home shows MynaCAD open" (Wait-Until { (Text-Of (Find-Id $win "Home.CadState" 500)) -like "*open for this project*" } 8000) (Text-Of (Find-Id $win "Home.CadState"))
Shot $win "06-home-cad-open"

"== 5. second press activates"
Invoke-El (Find-Id $win "Home.OpenCad") | Out-Null
Check "activate arrived on the pipe" (Wait-Until { (Get-Content $log -Raw) -match 'pipe: \{"cmd":"activate"\}' })
Check "no second start" (@(Get-Content $log | Where-Object { $_ -match "start args" }).Count -eq 1)

"== 6. AI Stop and Start"
$health = { param($port) try { $tok = (Get-Content (Join-Path $local "ai\auth.token") -Raw).Trim(); (Invoke-WebRequest -UseBasicParsing -Headers @{Authorization = "Bearer $tok" } -Uri "http://127.0.0.1:$port/v1/health" -TimeoutSec 3).StatusCode } catch { 0 } }
Check "agent answers /v1/health before Stop" ((& $health 8742) -eq 200)
Check "Home says the agent runs" (Wait-Until { (Text-Of (Find-Id $win "Home.AiToggle" 500)) -eq "Stop the AI agent" } 8000) (Text-Of (Find-Id $win "Home.AiState"))
Invoke-El (Find-Id $win "Home.AiToggle") | Out-Null
Check "after Stop nothing answers" (Wait-Until { (& $health 8742) -eq 0 } 8000)
Check "Home says AI: off and offers Start" (Wait-Until { (Text-Of (Find-Id $win "Home.AiState" 500)) -like "*off*" -and (Text-Of (Find-Id $win "Home.AiToggle" 500)) -eq "Start the AI agent" } 8000) (Text-Of (Find-Id $win "Home.AiState"))
Check "status strip says AI: off" (Wait-Until { $null -ne (Find-Name $win "AI: off" 500) } 12000)
Shot $win "07-home-ai-stopped"
Invoke-El (Find-Id $win "Home.AiToggle") | Out-Null
Check "after Start /v1/health answers again" (Wait-Until { (& $health 8742) -eq 200 } 8000)
Check "Home offers Stop again" (Wait-Until { (Text-Of (Find-Id $win "Home.AiToggle" 500)) -eq "Stop the AI agent" } 8000)

"== 7. Database offline and online"
Invoke-El (Find-Id $win "Home.DbToggle") | Out-Null     # MynaCAD (stub) is open: it is asked to close
Check "stub received close" (Wait-Until { (Get-Content $log -Raw) -match 'pipe: \{"cmd":"close"' } 10000)
Check "database offline" (Wait-Until { (Text-Of (Find-Id $win "Home.DbState" 500)) -like "Database: offline*" } 20000) (Text-Of (Find-Id $win "Home.DbState"))
Check "MynaCAD lease gone and MynaOps lease released" (@(Get-ChildItem $leases -Filter *.lock -ErrorAction SilentlyContinue).Count -eq 0)
Check "write-ahead log folded in" (-not (Test-Path "$osp-wal") -or (Get-Item "$osp-wal").Length -eq 0)
Check "Open MynaCAD disabled while offline" (-not (Find-Id $win "Home.OpenCad" 1000).Current.IsEnabled)
Shot $win "08-home-offline"
Invoke-El (Find-Id $win "Home.DbToggle") | Out-Null
Check "database online again with the lease back" ((Wait-Until { (Text-Of (Find-Id $win "Home.DbState" 500)) -like "Database: online*" } 10000) -and @(Get-ChildItem $leases -Filter *.ops.lock).Count -eq 1)
Invoke-El (Find-Id $win "Home.OpenCad") | Out-Null     # start MynaCAD again for the close question
Check "stub started a second time" (Wait-Until { @(Get-Content $log | Where-Object { $_ -match "start args" }).Count -eq 2 })
Check "MynaCAD lease held again" (Wait-Until { @(Get-ChildItem $leases -Filter *.cad.lock -ErrorAction SilentlyContinue).Count -eq 1 })

"== 8. close MynaOps while MynaCAD is open"
$wp = $null
$win.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$wp) | Out-Null
$wp.Close()
$q = Find-Name $win "MynaCAD has this project open" 8000
Check "the close question appears" ($null -ne $q)
Shot $win "09-close-question"
Invoke-El (Find-Name $win "Close both" 3000) | Out-Null
Check "Close both: MynaCAD closed (lease gone)" (Wait-Until { @(Get-ChildItem $leases -Filter *.cad.lock -ErrorAction SilentlyContinue).Count -eq 0 } 20000)
Check "Close both: MynaOps exited" (Wait-Until { $app.HasExited } 20000)
Check "Close both: holder cleared and lease released" (@(Get-ChildItem $leases -Filter *.lock -ErrorAction SilentlyContinue).Count -eq 0 -and (Sql $osp "select count(*) from meta where key='session.holder'") -eq "0")

"== 9. adopt a legacy project"
$legacy = Join-Path $projects "Legacy.osp"
python -I (Join-Path $PSScriptRoot "make-legacy-osp.py") $legacy
$before = Sql $legacy "select sha256 from drawing"
$bytesBefore = Sql $legacy "select hex(bytes) from drawing"
$app2 = Start-Process -FilePath $Exe -ArgumentList @("`"$legacy`"") -PassThru
$ad = Get-Win $app2.Id "Adopt project settings"
Check "Adopt window appears for a file with no configuration" ($null -ne $ad)
Check "nothing is written before Adopt" ((Sql $legacy "select count(*) from meta where key='config.schema'") -eq "0")
$levels = Find-Name $ad "Levels (2)"
Shot $ad "10-adopt"
Check "the proposal lists the levels MynaCAD wrote" ($null -ne $levels)
Invoke-El (Find-Id $ad "Adopt.Adopt") | Out-Null
Check "config written" (Wait-Until { (Sql $legacy "select count(*) from meta where key='config.schema'") -eq "1" })
Check "drawing row hash unchanged" ((Sql $legacy "select sha256 from drawing") -eq $before)
Check "drawing bytes unchanged" ((Sql $legacy "select hex(bytes) from drawing") -eq $bytesBefore)
$backups = @(Get-ChildItem (Join-Path $projects "Legacy.backups") -Filter "* manual.osp" -ErrorAction SilentlyContinue)
Check "a manual backup was made first" ($backups.Count -eq 1) ($backups | ForEach-Object Name)
Check "level ids of the drawing are kept (cid 3 is Ground)" ((Sql $legacy "select json_extract(value,'$.items[0].cid') from meta where key='config.levels'") -eq "3")
$win2 = Get-Win $app2.Id "*Legacy*"
Check "project opens on Home after adopting" ($null -ne $win2 -and $null -ne (Find-Id $win2 "Home.Path"))
Shot $win2 "11-adopted-home"
$wp = $null; $win2.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$wp) | Out-Null; $wp.Close()
Wait-Until { $app2.HasExited } 15000 | Out-Null
if (-not $app2.HasExited) { $app2.Kill() }
"`nDONE, failures: $script:fails"
exit $script:fails



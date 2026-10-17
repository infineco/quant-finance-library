<#
.SYNOPSIS
    Loads the packed InFine.QuantFinance add-in into a real 64-bit Excel through COM and smoke-tests every QF.* function.

.DESCRIPTION
    Starts a private, hidden Excel instance (it never attaches to an Excel the user has open), registers the .xll with
    Application.RegisterXLL, writes formulas with Range.Formula2 (dynamic arrays) and reads the results back from
    Range.Value2 / Range.Text. Every check is compared with an independent value (closed form, PowerShell computation)
    or with a property the function must satisfy (round trip, monotonicity, ES >= VaR...). Results are printed as a
    table and written to a JSON file.

    Covered behaviours: the 25 QF.* functions, omitted optional arguments, #N/A-then-result of the asynchronous QF.MCVAR
    and its cancellation when the cell is changed or cleared, handle versioning and #REF! on a stale handle, reopening a
    saved workbook then a full recalculation (Ctrl+Alt+F9 = Application.CalculateFull), multi-threaded recalculation and
    QF.LASTERROR. The Insert Function dialog cannot be driven through COM; the registered category and descriptions
    are checked separately (see docs/verification-windows-gpu.md).

    With -SampleWorkbooks (or -SamplesOnly) the script also opens every example workbook of samples\excel (see
    docs/excel.md, "Sample workbooks") read-only in the same Excel, forces a full recalculation, waits until no cell
    shows #N/A any more (QF.MCVAR is asynchronous), and fails a workbook if any cell holds an Excel error or if the
    "Add-in status" cell of its About sheet does not say the add-in is loaded.

    Requires Windows, Excel 64-bit and the .NET 8 Desktop Runtime x64. Build the add-in first:
        dotnet build InFine.QuantFinance.Excel/InFine.QuantFinance.Excel.csproj -c Release

.PARAMETER XllPath
    The add-in to load. Defaults to the packed Release build.

.PARAMETER OutputJson
    Where to write the results. Defaults to excel-smoke-results.json in the temp directory.

.PARAMETER Visible
    Show the Excel window while the test runs.

.PARAMETER SampleWorkbooks
    After the function checks, open and check every .xlsx of the samples directory (see the description).

.PARAMETER SamplesOnly
    Check only the sample workbooks (implies -SampleWorkbooks); skips the function checks, which take several minutes.

.PARAMETER SamplesDirectory
    Folder of the sample workbooks. Defaults to samples\excel at the root of the repository.

.PARAMETER SampleTimeoutSeconds
    How long to wait, per workbook, for the asynchronous cells to finish. Defaults to 180.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File InFine.QuantFinance.Excel/smoke/Invoke-ExcelSmokeTest.ps1
#>
[CmdletBinding()]
param(
    [string]$XllPath,
    [string]$OutputJson = (Join-Path ([System.IO.Path]::GetTempPath()) 'excel-smoke-results.json'),
    [switch]$Visible,
    [switch]$SampleWorkbooks,
    [switch]$SamplesOnly,
    [string]$SamplesDirectory,
    [int]$SampleTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot is not set yet in parameter defaults under Windows PowerShell 5.1.
if (-not $XllPath) {
    $XllPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\bin\Release\net8.0-windows\publish\InFine.QuantFinance.Excel-AddIn64-packed.xll'
}
$XllPath = (Resolve-Path $XllPath).Path
if (-not $SamplesDirectory) {
    $SamplesDirectory = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..\samples\excel'
}
$results = New-Object System.Collections.Generic.List[object]
$culture = [System.Globalization.CultureInfo]::InvariantCulture

function Add-Result([string]$Area, [string]$Check, [bool]$Ok, [string]$Detail) {
    $results.Add([pscustomobject]@{ Area = $Area; Check = $Check; Status = $(if ($Ok) { 'PASS' } else { 'FAIL' }); Detail = $Detail })
    $color = if ($Ok) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1} | {2} | {3}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Area, $Check, $Detail) -ForegroundColor $color
}

function Format-Num($x) {
    if ($x -is [double]) { return $x.ToString('R', $culture) }
    return [string]$x
}

# Absolute check when |expected| < 1, relative otherwise.
function Test-Close([string]$Area, [string]$Check, $Actual, [double]$Expected, [double]$Tolerance) {
    $ok = $false
    if ($Actual -is [double]) {
        $scale = [Math]::Max(1.0, [Math]::Abs($Expected))
        $ok = [Math]::Abs($Actual - $Expected) -le $Tolerance * $scale
    }
    Add-Result $Area $Check $ok ("got {0}, expected {1} (tol {2})" -f (Format-Num $Actual), (Format-Num $Expected), $Tolerance)
}

function Start-Excel {
    $before = @(Get-Process EXCEL -ErrorAction SilentlyContinue | ForEach-Object Id)
    $app = New-Object -ComObject Excel.Application
    $app.DisplayAlerts = $false
    $app.Visible = [bool]$Visible
    $after = @(Get-Process EXCEL -ErrorAction SilentlyContinue | ForEach-Object Id)
    $newPid = @($after | Where-Object { $before -notcontains $_ })
    if ($newPid.Count -ne 1) { throw "Could not identify the Excel process started by this script." }
    $script:excelPid = $newPid[0]
    $app.RTD.ThrottleInterval = 0
    return $app
}

function Stop-Excel($app) {
    if ($null -eq $app) { return }
    try { foreach ($wb in @($app.Workbooks)) { $wb.Close($false) } } catch { }
    try { $app.Quit() } catch { }
    [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($app)
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    $p = Get-Process -Id $script:excelPid -ErrorAction SilentlyContinue
    if ($p) { if (-not $p.WaitForExit(15000)) { $p.Kill() } }
}

# Excel refuses COM calls (RPC_E_CALL_REJECTED, 0x80010001) while it is busy, for instance computing an asynchronous
# QF function; the call is retried until the timeout instead of failing the run.
function Invoke-ComRetry([scriptblock]$Call, [int]$TimeoutSeconds = 60) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try { return & $Call }
        catch [System.Runtime.InteropServices.COMException] {
            if ($_.Exception.HResult -ne -2147418111 -or $sw.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw }
            Start-Sleep -Milliseconds 50
        }
    }
}

function Set-F($ws, [string]$Address, [string]$Formula) { Invoke-ComRetry { $ws.Range($Address).Formula2 = $Formula } }
# Scalars are written through IDispatch directly: Windows PowerShell 5.1's COM binder sometimes caches a string
# conversion for Range.Value2 at a call site and then fails on a double ("cannot cast System.Double to System.String").
# 2-D arrays go through the binder, which marshals them as a SAFEARRAY (InvokeMember does not: 0x800A03EC).
function Set-V($sheet, [string]$Address, $Value) {
    Invoke-ComRetry {
        $range = $sheet.Range($Address)
        if ($Value -is [Array]) { $range.Value2 = $Value; return }
        [void]$range.GetType().InvokeMember('Value2', [System.Reflection.BindingFlags]::SetProperty, $null, $range, @(, $Value))
    }
}
function Get-V($ws, [string]$Address) { return (Invoke-ComRetry { $ws.Range($Address).Value2 }) }
function Get-T($ws, [string]$Address) { return [string](Invoke-ComRetry { $ws.Range($Address).Text }) }
# Excel error values as Value2 returns them (Int32 CVErr codes); Range.Text is localised (#VALEUR! in French).
$XlErr = @{ 'NULL' = -2146826288; 'DIV0' = -2146826281; 'VALUE' = -2146826273; 'REF' = -2146826265; 'NAME' = -2146826259; 'NUM' = -2146826252; 'NA' = -2146826246 }
function Test-XlError($ws, [string]$Address, [string]$Name) {
    $v = Invoke-ComRetry { $ws.Range($Address).Value2 }
    return ($v -is [int]) -and ($v -eq $XlErr[$Name])
}

# Waits until the cell holds a number (asynchronous functions show #N/A first). Returns the elapsed seconds or -1.
function Wait-Numeric($app, $ws, [string]$Address, [double]$TimeoutSeconds) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ((Get-V $ws $Address) -is [double]) { return $sw.Elapsed.TotalSeconds }
        Start-Sleep -Milliseconds 100
    }
    return -1
}

function Get-ExcelCpuSeconds { return (Get-Process -Id $script:excelPid).TotalProcessorTime.TotalSeconds }

# Deterministic T x N returns (LCG + Box-Muller), correlated by a fixed lower-triangular factor; written to the sheet.
function New-Returns([int]$DateCount, [double[][]]$Factor, [double[]]$Sigma) {
    $n = $Sigma.Length
    $rng = New-Object System.Random 20251012
    $rows = New-Object 'double[,]' $DateCount, $n
    for ($t = 0; $t -lt $DateCount; $t++) {
        $z = New-Object double[] $n
        for ($j = 0; $j -lt $n; $j++) {
            $u1 = 1.0 - $rng.NextDouble(); $u2 = $rng.NextDouble()
            $z[$j] = [Math]::Sqrt(-2.0 * [Math]::Log($u1)) * [Math]::Cos(2.0 * [Math]::PI * $u2)
        }
        for ($i = 0; $i -lt $n; $i++) {
            $y = 0.0
            for ($j = 0; $j -le $i; $j++) { $y += $Factor[$i][$j] * $z[$j] }
            $rows[$t, $i] = $Sigma[$i] * $y
        }
    }
    return , $rows
}

# Scans every sheet of a workbook for error values. Excel hands errors back through Value2 as Int32 CVErr codes.
# #N/A is reported separately: it is what an asynchronous QF function shows while it computes.
function Get-WorkbookErrors($book) {
    $names = @{ -2146826288 = '#NULL!'; -2146826281 = '#DIV/0!'; -2146826273 = '#VALUE!'; -2146826265 = '#REF!'; -2146826259 = '#NAME?'; -2146826252 = '#NUM!'; -2146826246 = '#N/A' }
    $errors = New-Object System.Collections.Generic.List[string]
    $pending = New-Object System.Collections.Generic.List[string]
    $status = ''
    foreach ($ws in $book.Worksheets) {
        $used = $ws.UsedRange
        $values = $used.Value2
        $row0 = $used.Row; $col0 = $used.Column
        if ($values -isnot [System.Array]) { $values = @(, $values); $single = $true } else { $single = $false }
        $rows = if ($single) { 1 } else { $values.GetLength(0) }
        $cols = if ($single) { 1 } else { $values.GetLength(1) }
        for ($i = 1; $i -le $rows; $i++) {
            for ($j = 1; $j -le $cols; $j++) {
                $v = if ($single) { $values[0] } else { $values[$i, $j] }
                if ($v -is [int]) {
                    $where = '{0}!{1}' -f $ws.Name, $ws.Cells.Item($row0 + $i - 1, $col0 + $j - 1).Address($false, $false)
                    if ($v -eq -2146826246) { $pending.Add($where) }
                    else { $errors.Add(('{0}={1}' -f $where, $(if ($names.ContainsKey($v)) { $names[$v] } else { $v }))) }
                }
                elseif ($v -is [string] -and $v -like '*Add-in status:*') { $status = $v.Trim() }
            }
        }
    }
    return [pscustomobject]@{ Errors = $errors; Pending = $pending; Status = $status }
}

# Opens each sample workbook read-only, recalculates everything, waits for the asynchronous cells, checks for errors.
function Test-SampleWorkbooks($app, [string]$Directory, [double]$TimeoutSeconds) {
    $files = @(Get-ChildItem -Path $Directory -Filter '*.xlsx' -ErrorAction SilentlyContinue | Sort-Object Name)
    Add-Result 'Samples' 'sample workbooks found' ($files.Count -gt 0) ("{0} in {1}" -f $files.Count, $Directory)
    foreach ($file in $files) {
        $book = $null
        try {
            $book = $app.Workbooks.Open($file.FullName, 0, $true)   # UpdateLinks 0, ReadOnly
            $app.Calculation = -4105                                 # xlCalculationAutomatic
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $app.CalculateFull()                                     # Ctrl+Alt+F9: objects are rebuilt before their users
            do {
                Start-Sleep -Milliseconds 500
                $scan = Get-WorkbookErrors $book
            } while (($scan.Pending.Count -gt 0 -or $app.CalculationState -ne 0) -and $sw.Elapsed.TotalSeconds -lt $TimeoutSeconds)
            $charts = 0
            foreach ($ws in $book.Worksheets) { $charts += $ws.ChartObjects().Count }
            $loaded = $scan.Status -like '*loaded, version*'
            $ok = ($scan.Errors.Count -eq 0) -and ($scan.Pending.Count -eq 0) -and $loaded
            $detail = "{0:N1} s, {1} chart(s), {2}" -f $sw.Elapsed.TotalSeconds, $charts, $scan.Status
            if ($scan.Errors.Count -gt 0) { $detail += '; errors: ' + (($scan.Errors | Select-Object -First 10) -join ', ') }
            if ($scan.Pending.Count -gt 0) { $detail += '; still #N/A after the timeout: ' + (($scan.Pending | Select-Object -First 10) -join ', ') }
            Add-Result 'Samples' ("{0}: recalculates without error cells" -f $file.Name) $ok $detail
        }
        catch {
            Add-Result 'Samples' ("{0}: recalculates without error cells" -f $file.Name) $false $_.Exception.Message
        }
        finally {
            if ($book) { try { $book.Close($false) } catch { } }
        }
    }
}

$excelPid = 0
$xl = $null
$workbookPath = Join-Path ([System.IO.Path]::GetTempPath()) ("qf-smoke-{0}.xlsx" -f [guid]::NewGuid().ToString('N'))
try {
    $xl = Start-Excel
    Add-Result 'Environment' 'Excel version' $true ("Excel {0} build {1}, {2}, pid {3}" -f $xl.Version, $xl.Build, $xl.OperatingSystem, $excelPid)

    $registered = $xl.RegisterXLL($XllPath)
    Add-Result 'Load' 'RegisterXLL(packed .xll)' ([bool]$registered) $XllPath
    if (-not $registered) { throw "RegisterXLL returned false: the add-in did not load." }

    if (-not $SamplesOnly) {
    $wb = $xl.Workbooks.Add()
    $xl.Calculation = -4105   # xlCalculationAutomatic (needs an open workbook)
    $ws = $wb.Worksheets.Item(1)
    $ws.Name = 'Smoke'

    # ---------------------------------------------------------------- vanillas
    Set-F $ws 'A1' '=QF.BS.PRICE("CALL",100,100,1,0.05,0,0.2)'
    Set-F $ws 'A2' '=QF.BS.PRICE("PUT",100,100,1,0.05,0,0.2)'
    Test-Close 'Vanillas' 'QF.BS.PRICE call (Hull 10.4506)' (Get-V $ws 'A1') 10.450583572185565 1e-12
    Test-Close 'Vanillas' 'QF.BS.PRICE put (Hull 5.5735)' (Get-V $ws 'A2') 5.573526022256971 1e-12
    Add-Result 'Vanillas' 'call displayed to 4 dp' ([Math]::Round((Get-V $ws 'A1'), 4) -eq 10.4506 -and [Math]::Round((Get-V $ws 'A2'), 4) -eq 5.5735) ("{0} / {1}" -f (Get-V $ws 'A1'), (Get-V $ws 'A2'))
    $parity = (Get-V $ws 'A1') - (Get-V $ws 'A2') - (100 - 100 * [Math]::Exp(-0.05))
    Add-Result 'Vanillas' 'put-call parity' ([Math]::Abs($parity) -lt 1e-12) ("C - P - (S - K e^-rT) = {0}" -f $parity)

    Set-F $ws 'A3' '=QF.IMPLIEDVOL(A1,"CALL",100,100,1,0.05,0)'
    Test-Close 'Vanillas' 'QF.IMPLIEDVOL round trip' (Get-V $ws 'A3') 0.2 1e-12
    Set-F $ws 'A4' '=QF.BLACK76.PRICE("C",100*EXP(0.05),100,1,EXP(-0.05),0.2)'
    Test-Close 'Vanillas' 'QF.BLACK76.PRICE on F = S e^rT equals BS' (Get-V $ws 'A4') 10.450583572185565 1e-12
    Set-F $ws 'A5' '=QF.IMPLIEDVOL.BLACK76(A4,"C",100*EXP(0.05),100,1,EXP(-0.05))'
    Test-Close 'Vanillas' 'QF.IMPLIEDVOL.BLACK76 round trip' (Get-V $ws 'A5') 0.2 1e-12

    # QF.BS.GREEKS: default ALL spills 1x10; one greek; HEADER spills 2x10.
    Set-F $ws 'C1' '=QF.BS.GREEKS("C",100,100,1,0.05,0,0.2)'
    $spill = $ws.Range('C1').SpillingToRange
    Add-Result 'Vanillas' 'QF.BS.GREEKS default (ALL) spills 1x10' ($null -ne $spill -and $spill.Rows.Count -eq 1 -and $spill.Columns.Count -eq 10) ("spill {0}" -f $(if ($spill) { $spill.Address($false, $false) } else { 'none' }))
    Test-Close 'Vanillas' 'QF.BS.GREEKS price column' (Get-V $ws 'C1') 10.450583572185565 1e-12
    $d1 = (0.05 + 0.02) / 0.2
    Set-F $ws 'C3' '=QF.BS.GREEKS("C",100,100,1,0.05,0,0.2,"Delta")'
    Set-F $ws 'C4' ('=NORM.S.DIST({0},TRUE)' -f $d1.ToString('R', $culture))
    Test-Close 'Vanillas' 'QF.BS.GREEKS Delta = N(d1)' (Get-V $ws 'C3') (Get-V $ws 'C4') 1e-14
    Set-F $ws 'C6' '=QF.BS.GREEKS("C",100,100,1,0.05,0,0.2,"HEADER")'
    $spill = $ws.Range('C6').SpillingToRange
    Add-Result 'Vanillas' 'QF.BS.GREEKS HEADER spills 2x10 with names' ($null -ne $spill -and $spill.Rows.Count -eq 2 -and (Get-T $ws 'C6') -eq 'Price' -and (Get-T $ws 'L6') -eq 'DualDelta') ("C6={0}, L6={1}" -f (Get-T $ws 'C6'), (Get-T $ws 'L6'))

    # ---------------------------------------------------------------- dates (optional arguments omitted)
    Set-F $ws 'A10' '=QF.DATE.ADJUST(DATE(2025,12,25),"TARGET")'
    Set-F $ws 'A11' '=QF.DATE.ADVANCE(DATE(2025,12,24),"1B","TARGET")'
    Set-F $ws 'A12' '=QF.DATE.ADVANCE(DATE(2025,1,31),"1M","TARGET",,TRUE)'
    Set-F $ws 'A13' '=QF.YEARFRAC(DATE(2025,1,1),DATE(2026,1,1),"ACT/360")'
    Set-F $ws 'B10' '=DATE(2025,12,29)'
    Set-F $ws 'B12' '=DATE(2025,2,28)'
    Add-Result 'Dates' 'QF.DATE.ADJUST, convention omitted (Following over Christmas)' ((Get-V $ws 'A10') -eq (Get-V $ws 'B10')) ("{0} vs {1}" -f (Get-V $ws 'A10'), (Get-V $ws 'B10'))
    Add-Result 'Dates' 'QF.DATE.ADVANCE 1B from 24-Dec-2025 = 29-Dec-2025' ((Get-V $ws 'A11') -eq (Get-V $ws 'B10')) ("{0}" -f (Get-V $ws 'A11'))
    Add-Result 'Dates' 'QF.DATE.ADVANCE 1M, convention omitted, endOfMonth TRUE' ((Get-V $ws 'A12') -eq (Get-V $ws 'B12')) ("{0} vs {1}" -f (Get-V $ws 'A12'), (Get-V $ws 'B12'))
    Test-Close 'Dates' 'QF.YEARFRAC ACT/360 = 365/360' (Get-V $ws 'A13') (365.0 / 360.0) 1e-15

    # ---------------------------------------------------------------- curves
    Set-F $ws 'E1' '=QF.CURVE.FLAT("USD",DATE(2025,1,15),0.04,"ACT/365F")'
    Add-Result 'Curves' 'QF.CURVE.FLAT handle' ((Get-T $ws 'E1') -eq 'QF.Curve:USD#1') (Get-T $ws 'E1')
    Set-F $ws 'E2' '=QF.CURVE.DF(E1,DATE(2026,1,15))'
    Test-Close 'Curves' 'QF.CURVE.DF flat = exp(-0.04)' (Get-V $ws 'E2') ([Math]::Exp(-0.04)) 1e-14
    Set-F $ws 'E3' '=QF.CURVE.ZERO(E1,DATE(2026,1,15))'
    Test-Close 'Curves' 'QF.CURVE.ZERO, compounding omitted (continuous)' (Get-V $ws 'E3') 0.04 1e-14

    $quotes = @(@('Deposit', '3M', 0.0215), @('Deposit', '6M', 0.0225), @('FRA', '6x12', 0.0240), @('Swap', '2Y', 0.0230),
        @('Swap', '3Y', 0.0228), @('Swap', '5Y', 0.0233), @('Swap', '7Y', 0.0240), @('Swap', '10Y', 0.0250))
    for ($i = 0; $i -lt $quotes.Count; $i++) {
        $r = 20 + $i
        Set-V $ws "G$r" ($quotes[$i][0]); Set-V $ws "H$r" ($quotes[$i][1]); Set-V $ws "I$r" ([double]$quotes[$i][2])
    }
    $last = 19 + $quotes.Count
    Set-F $ws 'E5' ("=QF.CURVE.BOOTSTRAP(""EUR"",DATE(2025,1,15),G20:G$last,H20:H$last,I20:I$last)")
    Add-Result 'Curves' 'QF.CURVE.BOOTSTRAP, interpolation and calendar omitted' ((Get-T $ws 'E5') -eq 'QF.Curve:EUR#1') (Get-T $ws 'E5')
    Set-F $ws 'E6' '=QF.CURVE.DF(E5,DATE(2026,1,15))'
    Set-F $ws 'E7' '=QF.CURVE.DF(E5,DATE(2035,1,15))'
    $df1 = Get-V $ws 'E6'; $df10 = Get-V $ws 'E7'
    Add-Result 'Curves' 'QF.CURVE.DF on the bootstrapped curve: 1 > DF(1Y) > DF(10Y) > 0' ($df1 -is [double] -and $df10 -is [double] -and 1 -gt $df1 -and $df1 -gt $df10 -and $df10 -gt 0) ("DF(1Y)={0}, DF(10Y)={1}" -f $df1, $df10)
    # A 1Y deposit-like rate implied by the curve must sit between the 6M deposit and the 2Y swap quotes.
    $z1 = -[Math]::Log($df1)
    Add-Result 'Curves' 'implied 1Y zero rate within the quoted range' ($z1 -gt 0.019 -and $z1 -lt 0.026) ("{0}" -f $z1)
    Set-F $ws 'E8' '=QF.OBJ.INFO(E5)'
    $info = $ws.Range('E8').SpillingToRange
    $infoText = ''
    if ($info) { $v = $info.Value2; for ($i = 1; $i -le $v.GetLength(0); $i++) { $infoText += ("{0}={1}; " -f $v[$i, 1], $v[$i, 2]) } }
    Add-Result 'Objects' 'QF.OBJ.INFO spills a property table' ($null -ne $info -and $info.Columns.Count -eq 2) $infoText

    # ---------------------------------------------------------------- SVI
    $strikes = @(70, 80, 90, 100, 110, 120, 130)
    # Raw SVI a, b, rho, m, sigma at T = 1, forward 100; vols = sqrt(w(k)/T).
    $a = 0.02; $b = 0.1; $rho = -0.4; $m = 0.05; $sig = 0.2
    $sviData = New-Object 'object[,]' $strikes.Count, 2
    for ($i = 0; $i -lt $strikes.Count; $i++) {
        $k = [Math]::Log($strikes[$i] / 100.0)
        $w = $a + $b * ($rho * ($k - $m) + [Math]::Sqrt(($k - $m) * ($k - $m) + $sig * $sig))
        $sviData[$i, 0] = [double]$strikes[$i]
        $sviData[$i, 1] = [Math]::Sqrt($w)
    }
    Set-V $ws 'G40:H46' ($sviData)
    Set-F $ws 'E10' '=QF.SVI.FIT("SPX",1,100,G40:G46,H40:H46)'
    Add-Result 'Volatility' 'QF.SVI.FIT handle' ((Get-T $ws 'E10') -eq 'QF.Surface:SPX#1') (Get-T $ws 'E10')
    $kq = [Math]::Log(95.0 / 100.0)
    $wq = $a + $b * ($rho * ($kq - $m) + [Math]::Sqrt(($kq - $m) * ($kq - $m) + $sig * $sig))
    Set-F $ws 'E11' '=QF.SURFACE.VOL(E10,1,95)'
    Test-Close 'Volatility' 'QF.SURFACE.VOL at K=95 reproduces the generating SVI' (Get-V $ws 'E11') ([Math]::Sqrt($wq)) 1e-6
    Set-F $ws 'E12' '=QF.SURFACE.VOL(E10,2,95)'
    Test-Close 'Volatility' 'QF.SURFACE.VOL at T=2 (total variance linear in T)' (Get-V $ws 'E12') ([Math]::Sqrt($wq)) 1e-6

    # ---------------------------------------------------------------- objects, version, errors
    Set-F $ws 'K1' '=QF.VERSION()'
    Add-Result 'Objects' 'QF.VERSION' ((Get-T $ws 'K1') -match '\d+\.\d+\.\d+') (Get-T $ws 'K1')
    Set-F $ws 'K3' '=QF.OBJ.LIST()'
    $list = $ws.Range('K3').SpillingToRange
    Add-Result 'Objects' 'QF.OBJ.LIST lists the live objects (header + 3)' ($null -ne $list -and $list.Rows.Count -eq 4) ("rows {0}" -f $(if ($list) { $list.Rows.Count } else { 0 }))

    Set-F $ws 'K10' '=QF.BS.PRICE("CALL","abc",100,1,0.05,0,0.2)'
    Set-F $ws 'K11' '=QF.LASTERROR()'
    $xl.Calculate()
    Add-Result 'Errors' 'bad argument gives #VALUE!' (Test-XlError $ws 'K10' 'VALUE') (Get-T $ws 'K10')
    Add-Result 'Errors' 'QF.LASTERROR names the function and the argument' ((Get-T $ws 'K11') -like 'QF.BS.PRICE*abc*') (Get-T $ws 'K11')
    Set-F $ws 'K12' '=QF.BS.PRICE("CALL",100,100,1,0.05,0,-0.2)'
    Add-Result 'Errors' 'negative volatility gives #NUM!' (Test-XlError $ws 'K12' 'NUM') (Get-T $ws 'K12')
    Set-F $ws 'K13' '=QF.CURVE.DF(E10,DATE(2026,1,15))'
    Add-Result 'Errors' 'handle of the wrong kind gives #VALUE!' (Test-XlError $ws 'K13' 'VALUE') (Get-T $ws 'K13')
    Set-F $ws 'K14' '=QF.BS.PRICE(1/0,100,100,1,0.05,0,0.2)'
    Add-Result 'Errors' 'an error argument propagates (#DIV/0!)' (Test-XlError $ws 'K14' 'DIV0') (Get-T $ws 'K14')

    # ---------------------------------------------------------------- handle versioning
    $ws2 = $wb.Worksheets.Add()
    $ws2.Name = 'Handles'
    Set-V $ws2 'B1' (0.04)
    Set-F $ws2 'A1' '=QF.CURVE.FLAT("V",DATE(2025,1,15),B1,"ACT/365F")'
    Set-F $ws2 'A2' '=QF.CURVE.DF(A1,DATE(2026,1,15))'
    $h1 = Get-T $ws2 'A1'
    Set-V $ws2 'B1' (0.05)
    $h2 = Get-T $ws2 'A1'
    Add-Result 'Handles' 'replacing an object bumps the version' ($h1 -eq 'QF.Curve:V#1' -and $h2 -eq 'QF.Curve:V#2') ("{0} -> {1}" -f $h1, $h2)
    Test-Close 'Handles' 'dependent cell follows the new object' (Get-V $ws2 'A2') ([Math]::Exp(-0.05)) 1e-14
    Set-F $ws2 'A3' '=QF.CURVE.DF("QF.Curve:V#1",DATE(2026,1,15))'
    Add-Result 'Handles' 'stale handle gives #REF!' (Test-XlError $ws2 'A3' 'REF') (Get-T $ws2 'A3')
    $xl.CalculateFull()
    Add-Result 'Handles' 'full recalculation with identical inputs keeps the handle' ((Get-T $ws2 'A1') -eq 'QF.Curve:V#2') (Get-T $ws2 'A1')
    Set-F $ws2 'D1' '=QF.CURVE.FLAT("DEL",DATE(2025,1,15),0.03,"ACT/365F")'
    $hDel = Get-T $ws2 'D1'
    Set-V $ws2 'D2' ($hDel)
    Set-F $ws2 'D3' '=QF.OBJ.DELETE(D2)'
    Set-F $ws2 'D4' '=QF.CURVE.DF(D2,DATE(2026,1,15))'
    $xl.Calculate()
    Add-Result 'Handles' 'QF.OBJ.DELETE then use gives #REF!' ((Get-V $ws2 'D3') -eq $true -and (Test-XlError $ws2 'D4' 'REF')) ("delete={0}, use={1}" -f (Get-T $ws2 'D3'), (Get-T $ws2 'D4'))
    $ws2.Range('D3:D4').ClearContents()

    # ---------------------------------------------------------------- risk
    $wsR = $wb.Worksheets.Add()
    $wsR.Name = 'Risk'
    $dates = 500
    $chol = @(@(1.0, 0.0, 0.0), @(0.3, [Math]::Sqrt(1 - 0.09), 0.0), @(0.1, 0.2, [Math]::Sqrt(1 - 0.01 - 0.04)))
    $sigma = @(0.012, 0.006, 0.009)
    $ret = New-Returns $dates $chol $sigma
    Set-V $wsR 'A1' ('Equity'); Set-V $wsR 'B1' ('Bond'); Set-V $wsR 'C1' ('Gold')
    Set-V $wsR "A2:C$($dates + 1)" ($ret)
    # Sample covariance computed here, independently of the library.
    $mean = @(0.0, 0.0, 0.0)
    for ($t = 0; $t -lt $dates; $t++) { for ($j = 0; $j -lt 3; $j++) { $mean[$j] += $ret[$t, $j] / $dates } }
    $cov01 = 0.0; $var0 = 0.0
    for ($t = 0; $t -lt $dates; $t++) { $cov01 += ($ret[$t, 0] - $mean[0]) * ($ret[$t, 1] - $mean[1]); $var0 += ($ret[$t, 0] - $mean[0]) * ($ret[$t, 0] - $mean[0]) }
    $cov01 /= ($dates - 1); $var0 /= ($dates - 1)

    Set-F $wsR 'F1' ("=QF.COV(A2:C$($dates + 1))")
    $covRange = $wsR.Range('F1').SpillingToRange
    Add-Result 'Risk' 'QF.COV spills 3x3, method omitted (SAMPLE)' ($null -ne $covRange -and $covRange.Rows.Count -eq 3 -and $covRange.Columns.Count -eq 3) ''
    Test-Close 'Risk' 'QF.COV(0,0) = sample variance' (Get-V $wsR 'F1') $var0 1e-12
    Test-Close 'Risk' 'QF.COV(0,1) = sample covariance' (Get-V $wsR 'G1') $cov01 1e-12

    Set-F $wsR 'F5' ("=QF.COV.OBJ(""PF"",A1:C$($dates + 1),""EWMA"",0.97,,TRUE)")
    Add-Result 'Risk' 'QF.COV.OBJ with a middle argument omitted' ((Get-T $wsR 'F5') -eq 'QF.Cov:PF#1') (Get-T $wsR 'F5')

    # P&L of a 1000/-300 position on Equity/Bond, scaled by spot 100.
    Set-F $wsR 'F7' ("=QF.VAR(A2:B$($dates + 1),0.99,""PARAMETRIC"",,{100000,-30000})")
    $pnl = New-Object double[] $dates
    for ($t = 0; $t -lt $dates; $t++) { $pnl[$t] = 100000.0 * $ret[$t, 0] - 30000.0 * $ret[$t, 1] }
    $pm = ($pnl | Measure-Object -Average).Average
    $sd = [Math]::Sqrt((($pnl | ForEach-Object { ($_ - $pm) * ($_ - $pm) }) | Measure-Object -Sum).Sum / ($dates - 1))
    Test-Close 'Risk' 'QF.VAR PARAMETRIC = z(0.99) x sd(P&L)' (Get-V $wsR 'F7') (2.3263478740408408 * $sd) 1e-9
    Set-F $wsR 'F8' ("=QF.VAR(A2:B$($dates + 1),0.99,,,{100000,-30000})")
    Set-F $wsR 'F9' ("=QF.ES(A2:B$($dates + 1),0.99,,,{100000,-30000})")
    Add-Result 'Risk' 'QF.VAR / QF.ES HISTORICAL (method omitted): 0 < VaR <= ES' ((Get-V $wsR 'F8') -gt 0 -and (Get-V $wsR 'F9') -ge (Get-V $wsR 'F8')) ("VaR={0}, ES={1}" -f (Get-V $wsR 'F8'), (Get-V $wsR 'F9'))
    Set-F $wsR 'F10' ("=QF.VAR(A2:B$($dates + 1),0.99,""PARAMETRIC"",10,{100000,-30000})")
    Test-Close 'Risk' 'QF.VAR horizon 10 = sqrt(10) x 1-period' (Get-V $wsR 'F10') ([Math]::Sqrt(10) * (Get-V $wsR 'F7')) 1e-12
    Set-F $wsR 'F12' ("=QF.VAR.DECOMP(A2:C$($dates + 1),{100000;-30000;20000},0.99,""PARAMETRIC"")")
    $dec = $wsR.Range('F12').SpillingToRange
    $okDec = $false; $decText = ''
    if ($dec) {
        $v = $dec.Value2; $rows = $v.GetLength(0)
        $sum = 0.0; for ($i = 2; $i -lt $rows; $i++) { $sum += [double]$v[$i, 3] }   # row 1 is the header, the last row the total
        $total = [double]$v[$rows, 3]
        $okDec = [Math]::Abs($sum - $total) -le 1e-9 * [Math]::Abs($total)
        $decText = ("rows {0}, sum of component VaR {1}, total {2}" -f $rows, $sum, $total)
    }
    Add-Result 'Risk' 'QF.VAR.DECOMP components add up to the total' $okDec $decText

    # QF.MCVAR: asynchronous; #N/A first, then the result; linear book close to delta-normal.
    Set-V $wsR 'J1' (100.0); Set-V $wsR 'J2' (100.0); Set-V $wsR 'J3' (50.0)
    Set-V $wsR 'K1' (0.2); Set-V $wsR 'K2' (0.25); Set-V $wsR 'K3' (0.3)
    Set-V $wsR 'M1' ('Equity'); Set-V $wsR 'N1' ('SPOT'); Set-V $wsR 'O1' (1000.0)
    Set-V $wsR 'M2' ('Bond'); Set-V $wsR 'N2' ('SPOT'); Set-V $wsR 'O2' (-300.0)
    Set-F $wsR 'F20' ("=QF.COV.OBJ(""MC"",A1:C$($dates + 1),,,,TRUE)")
    Set-F $wsR 'F22' '=QF.MCVAR(F20,J1:J3,K1:K3,M1:O2,0.99,400000,1)'
    # Sample the cell every 10 ms until the result lands: at least one sample must be #N/A.
    $sawNa = $false; $samples = 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 120 -and -not ((Get-V $wsR 'G23') -is [double])) {
        if (Test-XlError $wsR 'F22' 'NA') { $sawNa = $true }
        $samples++; Start-Sleep -Milliseconds 10
    }
    $elapsed = $(if ((Get-V $wsR 'G23') -is [double]) { $sw.Elapsed.TotalSeconds } else { -1 })
    Add-Result 'Async' 'QF.MCVAR shows #N/A while computing' $sawNa ("{0} samples before the result" -f $samples)
    Add-Result 'Async' 'QF.MCVAR result arrives' ($elapsed -ge 0) ("after {0:N2} s" -f $elapsed)
    if ($elapsed -ge 0) {
        $mcVar = Get-V $wsR 'G23'; $mcEs = Get-V $wsR 'G24'
        # Delta-normal VaR of exposures (100000, -30000) with the sample covariance of the first two columns.
        $var1 = 0.0; $cov11 = 0.0
        for ($t = 0; $t -lt $dates; $t++) { $var1 += ($ret[$t, 1] - $mean[1]) * ($ret[$t, 1] - $mean[1]) }
        $var1 /= ($dates - 1)
        $sdP = [Math]::Sqrt(100000.0 * 100000.0 * $var0 + 30000.0 * 30000.0 * $var1 - 2 * 100000.0 * 30000.0 * $cov01)
        $dnVar = 2.3263478740408408 * $sdP
        $dnEs = $sdP * [Math]::Exp(-0.5 * 2.3263478740408408 * 2.3263478740408408) / [Math]::Sqrt(2 * [Math]::PI) / 0.01
        Add-Result 'Async' 'QF.MCVAR linear book within 2% of delta-normal VaR' ([Math]::Abs($mcVar - $dnVar) -le 0.02 * $dnVar) ("MC {0}, delta-normal {1}" -f $mcVar, $dnVar)
        Add-Result 'Async' 'QF.MCVAR linear book within 2% of delta-normal ES' ([Math]::Abs($mcEs - $dnEs) -le 0.02 * $dnEs) ("MC {0}, delta-normal {1}" -f $mcEs, $dnEs)
        Add-Result 'Async' 'QF.MCVAR 3x3 block with header' ((Get-T $wsR 'F23') -eq 'VaR' -and (Get-T $wsR 'F24') -eq 'ES') ("{0} / {1}" -f (Get-T $wsR 'F23'), (Get-T $wsR 'F24'))
        # Recalculating a finished call restarts it (Excel-DNA ends the RTD topic once the result is delivered):
        # #N/A again, then the same digits.
        $wsR.Range('F22').Dirty(); $wsR.Range('F22').Calculate()
        $naAgain = Test-XlError $wsR 'F22' 'NA'
        $tAgain = Wait-Numeric $xl $wsR 'G23' 120
        Add-Result 'Async' 'recalculating a finished call recomputes it (#N/A, then the same digits)' ($naAgain -and $tAgain -ge 0 -and (Get-V $wsR 'G23') -eq $mcVar) ("#N/A shown: {0}, result after {1:N2} s" -f $naAgain, $tAgain)
        # The same arguments in another cell: same digits (deterministic); report whether it was served at once.
        Set-F $wsR 'F30' '=QF.MCVAR(F20,J1:J3,K1:K3,M1:O2,0.99,400000,1)'
        $firstOther = Get-T $wsR 'F30'
        $tOther = Wait-Numeric $xl $wsR 'G31' 60
        Add-Result 'Async' 'identical call in another cell gives the same digits' ($tOther -ge 0 -and (Get-V $wsR 'G31') -eq $mcVar) ("first read {0}, result after {1:N2} s" -f $firstOther, $tOther)
        $wsR.Range('F30').ClearContents()
    }

    # Cancellation. The book has 40 option positions so that the simulation phase of 20M scenarios lasts well beyond
    # the observation window (the final sort of the P&L is not cancellable and must not be what is measured).
    $book = New-Object 'object[,]' 40, 5
    $assets = @('Equity', 'Bond', 'Gold'); $spotsOf = @(100.0, 100.0, 50.0)
    for ($i = 0; $i -lt 40; $i++) {
        $book[$i, 0] = $assets[$i % 3]; $book[$i, 1] = $(if ($i % 2 -eq 0) { 'CALL' } else { 'PUT' })
        $book[$i, 2] = 1.0 + $i; $book[$i, 3] = $spotsOf[$i % 3] * (0.8 + 0.01 * $i); $book[$i, 4] = 0.25 + 0.05 * $i
    }
    Set-V $wsR 'M101:Q140' $book
    $heavy = '=QF.MCVAR(F20,J1:J3,K1:K3,M101:Q140,0.99,20000000,{0})'

    # CPU of the Excel process per one-second window.
    function Get-CpuProfile([int]$Seconds) {
        $samples = @(); $prev = Get-ExcelCpuSeconds
        for ($sec = 0; $sec -lt $Seconds; $sec++) { Start-Sleep -Seconds 1; $now = Get-ExcelCpuSeconds; $samples += [Math]::Round($now - $prev, 2); $prev = $now }
        return , $samples
    }
    Set-F $wsR 'F40' ($heavy -f 1)
    $running = Get-CpuProfile 3
    $stillNa = Test-XlError $wsR 'F40' 'NA'
    $wsR.Range('F40').ClearContents()
    $afterClear = Get-CpuProfile 6
    $ok = $stillNa -and ($running[2] -gt 2) -and ($afterClear[1] -lt 0.3) -and ($afterClear[5] -lt 0.3)
    Add-Result 'Async' 'clearing the cell cancels the computation' $ok ("Excel CPU s per s: running {0} (still #N/A: {1}); after clear {2}" -f ($running -join ' '), $stillNa, ($afterClear -join ' '))

    Set-F $wsR 'F40' ($heavy -f 1)
    $running = Get-CpuProfile 3
    Set-F $wsR 'F40' '=QF.MCVAR(F20,J1:J3,K1:K3,M1:O2,0.99,20000,3)'
    $t2 = Wait-Numeric $xl $wsR 'G41' 60
    $afterChange = Get-CpuProfile 6
    $ok = ($t2 -ge 0) -and ($running[2] -gt 2) -and ($afterChange[1] -lt 0.3) -and ($afterChange[5] -lt 0.3)
    Add-Result 'Async' 'changing the arguments cancels the old computation and runs the new one' $ok ("new result after {0:N2} s; Excel CPU s per s: before {1}; after {2}" -f $t2, ($running -join ' '), ($afterChange -join ' '))

    # Recalculating a call still in flight joins it instead of starting again: time a fresh run, then recalculate a
    # second fresh run 2 s after its start and check that its result does not arrive 2 s later than a fresh run.
    $medium = '=QF.MCVAR(F20,J1:J3,K1:K3,M101:Q140,0.99,4000000,{0})'
    Set-F $wsR 'F40' ($medium -f 11)
    $tFresh = Wait-Numeric $xl $wsR 'G41' 300
    Set-F $wsR 'F40' ($medium -f 12)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 2
    $wsR.Range('F40').Dirty(); $wsR.Range('F40').Calculate()
    $tJoined = $sw.Elapsed.TotalSeconds + (Wait-Numeric $xl $wsR 'G41' 300)
    Add-Result 'Async' 'recalculating a call in flight joins it' ($tFresh -gt 2.5 -and $tJoined -lt $tFresh + 1.0) ("fresh run {0:N2} s; recalculated at +2 s, result at {1:N2} s (a restart would give about {2:N2} s)" -f $tFresh, $tJoined, ($tFresh + 2))
    $wsR.Range('F40').ClearContents()

    # Control: the same heavy computation left alone keeps the cores busy (so the drop above is the cancellation).
    Set-F $wsR 'F40' ($heavy -f 2)
    $control = Get-CpuProfile 6
    $wsR.Range('F40').ClearContents()
    Add-Result 'Async' 'control: an uncancelled heavy computation keeps running' ($control[5] -gt 2) ("Excel CPU s per s: {0}" -f ($control -join ' '))
    [void](Get-CpuProfile 3)

    # ---------------------------------------------------------------- multi-threaded recalculation
    $wsM = $wb.Worksheets.Add()
    $wsM.Name = 'MT'
    $n = 4000
    $k = New-Object 'object[,]' $n, 1
    for ($i = 0; $i -lt $n; $i++) { $k[$i, 0] = 50.0 + 100.0 * $i / $n }
    Set-V $wsM "A1:A$n" ($k)
    $wsM.Range("B1:B$n").Formula = '=QF.BS.PRICE("C",100,A1,1,0.05,0.01,0.25)'
    $wsM.Range("C1:C$n").Formula = '=QF.IMPLIEDVOL(B1,"C",100,A1,1,0.05,0.01)'
    $wsM.Range("D1:D$n").Formula = '=QF.CURVE.DF(Smoke!$E$5,DATE(2025,1,15)+A1*30)'
    $xl.MultiThreadedCalculation.Enabled = $false
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); $xl.CalculateFull(); $single = $sw.Elapsed.TotalMilliseconds
    $ref = $wsM.Range("B1:D$n").Value2
    $xl.MultiThreadedCalculation.Enabled = $true
    $xl.MultiThreadedCalculation.ThreadMode = 0   # xlThreadModeAutomatic
    $threads = $xl.MultiThreadedCalculation.ThreadCount
    $sw.Restart(); $xl.CalculateFull(); $multi = $sw.Elapsed.TotalMilliseconds
    $mt = $wsM.Range("B1:D$n").Value2
    $same = $true; $maxIv = 0.0
    for ($i = 1; $i -le $n; $i++) {
        for ($j = 1; $j -le 3; $j++) { if ($ref[$i, $j] -ne $mt[$i, $j]) { $same = $false } }
        $iv = $mt[$i, 2]
        if ($iv -is [double]) { $maxIv = [Math]::Max($maxIv, [Math]::Abs($iv - 0.25)) } else { $same = $false }
    }
    Add-Result 'Multithread' ("{0} thread-safe cells: identical results single vs {1} threads" -f (3 * $n), $threads) $same ("single {0:N0} ms, multi {1:N0} ms" -f $single, $multi)
    Add-Result 'Multithread' 'implied vol round trip on every row' ($maxIv -lt 1e-9) ("max |iv - 0.25| = {0}" -f $maxIv)

    # ---------------------------------------------------------------- save, reopen, Ctrl+Alt+F9
    $wsR.Range('F40').ClearContents()
    $wb.SaveAs($workbookPath, 51)   # xlOpenXMLWorkbook
    $wb.Close($false)
    Stop-Excel $xl
    $xl = Start-Excel
    [void]$xl.RegisterXLL($XllPath)
    $wb = $xl.Workbooks.Open($workbookPath)
    $xl.Calculation = -4105
    $wsH = $wb.Worksheets.Item('Handles')
    $openedHandle = Get-T $wsH 'A1'
    $openedDf = Get-T $wsH 'A2'
    $liveObjects = 0
    # A new formula is the only dirty cell, so writing it recalculates nothing else.
    Set-F $wsH 'H1' '=ROWS(QF.OBJ.LIST())-1'
    $liveObjects = Get-V $wsH 'H1'
    Add-Result 'Reopen' 'state right after opening (informational)' $true ("A1={0}, A2={1}, live objects={2}, calc state={3}" -f $openedHandle, $openedDf, $liveObjects, $xl.CalculationState)
    $wsH.Range('A2').Formula2 = $wsH.Range('A2').Formula2   # F2 + Enter on the dependent cell
    $afterOpen = Get-V $wsH 'A2'
    $objectsGone = ($liveObjects -eq 0)
    $expectRef = if ($objectsGone) { Test-XlError $wsH 'A2' 'REF' } else { $afterOpen -is [double] }
    Add-Result 'Reopen' 'a dependent re-entered after reopening: #REF! while the objects are gone' $expectRef ("objects gone={0}, A2 after re-entry: {1}" -f $objectsGone, (Get-T $wsH 'A2'))
    $xl.CalculateFull()
    Test-Close 'Reopen' 'Ctrl+Alt+F9 (CalculateFull) rebuilds objects before their users' (Get-V $wsH 'A2') ([Math]::Exp(-0.05)) 1e-14
    $wsRisk = $wb.Worksheets.Item('Risk')
    $t3 = Wait-Numeric $xl $wsRisk 'G23' 120
    Add-Result 'Reopen' 'QF.MCVAR recomputed after Ctrl+Alt+F9' ($t3 -ge 0) ("after {0:N2} s" -f $t3)
    }   # -not $SamplesOnly

    if ($SampleWorkbooks -or $SamplesOnly) { Test-SampleWorkbooks $xl $SamplesDirectory $SampleTimeoutSeconds }
}
catch {
    Add-Result 'Script' 'unexpected error' $false ($_.Exception.Message + ' @ ' + $_.InvocationInfo.PositionMessage)
}
finally {
    Stop-Excel $xl
    if (Test-Path $workbookPath) { Remove-Item $workbookPath -Force }
}

$results | ConvertTo-Json -Depth 3 | Set-Content -Path $OutputJson -Encoding UTF8
$failed = @($results | Where-Object Status -eq 'FAIL').Count
Write-Host ("{0} checks, {1} failed. Results: {2}" -f $results.Count, $failed, $OutputJson)
exit $(if ($failed -eq 0) { 0 } else { 1 })

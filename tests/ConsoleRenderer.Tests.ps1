# Hardware-free resize regression checks: pwsh -NoProfile -File tests/ConsoleRenderer.Tests.ps1
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPositionalParameters', '')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '')]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestHelpers.ps1')

$script:moveCursor = ${function:Set-ConsoleCursorPosition}
function Get-ConsoleSize { return $script:size }
function Set-ConsoleCursorPosition([int]$Row) {
    if ($Row -eq $script:failRow) {
        if ($script:failure -eq 'range') {
            # Exercise the real .NET exception wrapped by PowerShell, as on resize.
            & $script:moveCursor -Row -1
        }
        else { throw [IO.IOException]::new('test console failure') }
    }
    $script:cursorRows.Add($Row)
}
function Write-ConsoleLine($Text, $Color, $Width, $BackgroundColor, $Segments) {
    $script:lines.Add([pscustomobject]@{ Text = $Text; Width = $Width; BackgroundColor = $BackgroundColor })
    if ($script:onWrite) { & $script:onWrite }
}

function Reset-TestRenderer {
    $script:size = [pscustomobject]@{ Width = 10; Height = 4 }
    $script:cursorRows = [Collections.Generic.List[int]]::new()
    $script:lines = [Collections.Generic.List[object]]::new()
    $script:failRow = -1
    $script:failure = 'range'
    $script:onWrite = $null
}
$script:frame = [pscustomobject]@{
    Body   = @((New-FrameLine 'first'), (New-FrameLine 'second'))
    Footer = New-FrameLine 'footer'
}

Reset-TestRenderer
Assert-True (Show-Frame $script:frame 10 4) 'A stable frame was not completed.'
Assert-Equal @(0, 1, 2, 3) $script:cursorRows.ToArray() 'The body or footer was positioned incorrectly.'
Assert-Equal @('first', 'second', '', 'footer') @($script:lines.Text) 'The unused body row was not cleared.'
Assert-Equal 'DarkCyan' $script:lines[3].BackgroundColor 'The footer lost its background.'
Write-Output 'PASS: stable frame clears unused rows and draws the footer'

foreach ($dimension in 'Width', 'Height') {
    Reset-TestRenderer
    $script:onWrite = { $script:size.$dimension = 2 }
    Assert-True (-not (Show-Frame $script:frame 10 4)) "A $dimension resize did not interrupt drawing."
    Assert-Equal @(0) $script:cursorRows.ToArray() "Drawing continued at stale $dimension coordinates."
    $script:onWrite = $null
    Assert-True (Show-Frame $script:frame $script:size.Width $script:size.Height) "Drawing did not recover after a $dimension resize."
    Assert-Equal 'footer' $script:lines[-1].Text 'The recovered frame lost its footer.'
}
Write-Output 'PASS: width and height changes interrupt drawing and recover at the new size'

foreach ($row in 1, 3) {
    Reset-TestRenderer
    $script:failRow = $row
    Assert-True (-not (Show-Frame $script:frame 10 4)) 'A cursor resize exception escaped.'
    Assert-Equal $row $script:lines.Count 'Drawing continued after a failed cursor move.'
    $script:failRow = -1
    Assert-True (Show-Frame $script:frame 10 4) 'Drawing did not recover after a cursor resize exception.'
}
Write-Output 'PASS: resize races at body and footer cursor moves are handled'

foreach ($dimensions in @(@(0, 4), @(-1, 4), @(10, 0), @(10, -1))) {
    Reset-TestRenderer
    Assert-True (-not (Show-Frame $script:frame $dimensions[0] $dimensions[1])) 'An unusable frame size was accepted.'
    Assert-Equal 0 $script:cursorRows.Count 'An unusable frame moved the cursor.'
}
Write-Output 'PASS: unusable dimensions skip drawing'

Reset-TestRenderer
$script:failRow = 1
$script:failure = 'io'
$reported = $false
try { $null = Show-Frame $script:frame 10 4 }
catch [IO.IOException] { $reported = $_.Exception.Message -eq 'test console failure' }
Assert-True $reported 'An unrelated console error was swallowed.'
Write-Output 'PASS: unrelated console errors still propagate'

# Keep LastWidth/Height equal to avoid touching the real console's Clear method.
function Get-MonitorFrame($Session, $View, $Width) { return $script:frame }
Reset-TestRenderer
$view = @{ LastWidth = 10; LastHeight = 4; Dirty = $false }
$script:failRow = 1
Show-TuiScreen $null $view
Assert-True $view.Dirty 'An interrupted frame was not marked for retry.'
$script:failRow = -1
Show-TuiScreen $null $view
Assert-True (-not $view.Dirty) 'A successful redraw kept requesting a retry.'
$script:size.Width = 0
Show-TuiScreen $null $view
Assert-True $view.Dirty 'An unusable screen size was not marked for retry.'
Assert-True (Test-WindowResized $view) 'Resize detection ignored the current console dimensions.'
Write-Output 'PASS: the TUI requests another frame after resize and skips unusable sizes'

# An interrupted frame must also be retried while paused and with unchanged size.
function Read-TuiInput {
    $script:ticks++
    Assert-True ($script:ticks -le 2) 'The monitor loop lost the redraw request from an interrupted frame.'
}
function Test-WindowResized { return $false }
function Show-TuiScreen($Session, $View) {
    $script:frames++
    if ($script:frames -eq 1) { $View.Dirty = $true }
    else { $View.Quit = $true }
}
$script:frames = 0
$script:ticks = 0
$view = @{ Paused = $true; Fetching = $false; Done = $false; Quit = $false; Dirty = $true }
Invoke-TuiLoop $null $view $null
Assert-Equal 2 $script:frames 'The monitor loop lost the redraw request from an interrupted frame.'
Write-Output 'PASS: interrupted frames are retried even while paused'

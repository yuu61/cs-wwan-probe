# Presentation: writes frames to the console.

function Write-ConsoleLine {
    # TUI needs direct cursor-addressed console writes; Write-Output cannot do this.
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '')]
    param([string]$Text, [string]$Color, [int]$Width, [string]$BackgroundColor = '', [object[]]$Segments = @())

    [Console]::ForegroundColor = $Color
    if ($BackgroundColor) { [Console]::BackgroundColor = $BackgroundColor }
    if ($Segments.Count -gt 0) {
        $remaining = $Width
        foreach ($segment in $Segments) {
            if ($remaining -le 0) { break }
            $length = [math]::Min($segment.Text.Length, $remaining)
            [Console]::ForegroundColor = $segment.Color
            if ($null -ne $segment.GrayLevel) {
                $level = $segment.GrayLevel
                [Console]::Write("`e[38;2;$level;$level;${level}m")
            }
            [Console]::Write($segment.Text.Substring(0, $length))
            # End the RGB override before spaces, the NR suffix or the next line.
            if ($null -ne $segment.GrayLevel) { [Console]::Write("`e[39m") }
            $remaining -= $length
        }
        [Console]::ForegroundColor = $Color
        [Console]::Write(' ' * $remaining)
    }
    else {
        if ($Text.Length -gt $Width) { $Text = $Text.Substring(0, $Width) }
        [Console]::Write($Text.PadRight($Width))
    }
    [Console]::ResetColor()
}

# Switches to/from the alternate screen buffer (VT). Leaving it restores the
# screen as it was before entering, so no TUI frame remains after exit.
function Set-AlternateScreen {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '')]
    param([bool]$Enabled)

    [Console]::Write($(if ($Enabled) { "`e[?1049h" } else { "`e[?1049l" }))
}

# Keep drawing within both the visible window and the current buffer, leaving the
# rightmost column unused so writing the footer cannot scroll the screen.
function Get-ConsoleSize {
    return [pscustomobject]@{
        Width  = [math]::Max(0, [math]::Min([Console]::WindowWidth, [Console]::BufferWidth) - 1)
        Height = [math]::Max(0, [math]::Min([Console]::WindowHeight, [Console]::BufferHeight))
    }
}

function Set-ConsoleCursorPosition {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '')]
    param([int]$Row)

    [Console]::SetCursorPosition(0, $Row)
}

# Redraws the whole screen in place (no Clear-Host per frame to avoid flicker).
# Returns false if a resize interrupts this frame; the UI retries on its next tick.
function Show-Frame($Frame, [int]$Width, [int]$Height) {
    if ($Width -lt 1 -or $Height -lt 1) { return $false }
    for ($row = 0; $row -lt $Height; $row++) {
        $size = Get-ConsoleSize
        if ($size.Width -ne $Width -or $size.Height -ne $Height) { return $false }
        # The buffer can shrink between the size check and this cursor move.
        try { Set-ConsoleCursorPosition -Row $row }
        catch [ArgumentOutOfRangeException] { return $false }

        if ($row -eq $Height - 1) {
            Write-ConsoleLine -Text $Frame.Footer.Text -Color $Frame.Footer.Color -Width $Width -BackgroundColor 'DarkCyan'
        }
        elseif ($row -lt $Frame.Body.Count) {
            $line = $Frame.Body[$row]
            Write-ConsoleLine -Text $line.Text -Color $line.Color -Width $Width -Segments $line.Segments
        }
        else {
            Write-ConsoleLine -Text '' -Color 'Gray' -Width $Width
        }
    }
    return $true
}

# Plain sequential output for redirected consoles (pipes, logs, CI).
function Show-PlainFrame($Frame) {
    foreach ($line in $Frame.Body) { [Console]::Out.WriteLine($line.Text) }
    [Console]::Out.WriteLine('')
}

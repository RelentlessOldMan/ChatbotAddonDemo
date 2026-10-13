<#
.SYNOPSIS
  Rebuilds the apps and regenerates every image in docs/images by driving the real UI.

.DESCRIPTION
  Runs Demo.Desktop in --demo mode (seeded data, scripted assistant, throwaway profile),
  clicks through the walkthrough with UI Automation (FlaUI), connects a real MCP client to
  both apps, and renders real console output. No API key or network access is needed.

  The desktop app's window is captured from the screen, so don't use the mouse/keyboard
  while it runs (about 30 seconds).
#>
param(
    [string]$Configuration = "Debug"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

dotnet build "$root\ChatbotAddonDemo.sln" -c $Configuration -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$desktop = "$root\src\Demo.Desktop\bin\$Configuration\net10.0-windows\Demo.Desktop.exe"
$cli = "$root\src\Demo.Cli\bin\$Configuration\net10.0\demo-cli.exe"
$tool = "$root\tools\Demo.Screenshots\bin\$Configuration\net10.0-windows\Demo.Screenshots.exe"

& $tool --desktop $desktop --cli $cli --out "$root\docs\images"
if ($LASTEXITCODE -ne 0) { throw "Screenshot capture failed" }

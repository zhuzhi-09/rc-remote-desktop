#requires -Version 5.1
<#
.SYNOPSIS
    Build every release artifact for the Rc remote-control system.

.DESCRIPTION
    - reset : delete bin/obj/publish
    - build : Debug build of the whole solution, then run the end-to-end test
    - dist  : publish self-contained single-file artifacts into publish/
    - e2e   : run the end-to-end test only (plain ws + TLS wss)
    With no arguments this behaves like -Task dist.

.EXAMPLE
    .\build.ps1 -Task dist
    .\build.ps1 -Task e2e

.NOTES
    This file is intentionally ASCII-only: Windows PowerShell 5.1 decodes UTF-8 files without a
    BOM using the ANSI code page, which corrupts non-ASCII text and can break parsing.
#>
param(
    [ValidateSet('reset', 'build', 'dist', 'e2e')]
    [string]$Task = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Step failed: $Name (exit $LASTEXITCODE)"
    }
}

switch ($Task) {
    'reset' {
        Invoke-Step 'Clean bin/obj/publish' {
            Get-ChildItem -Path $root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -in @('bin', 'obj', 'publish') } |
                Sort-Object { $_.FullName.Length } -Descending |
                Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    'build' {
        Invoke-Step 'Debug build' { dotnet build "$root\RemoteControl.slnx" -v m }
        Invoke-Step 'End-to-end test' { dotnet run --project "$root\tools\Rc.E2E\Rc.E2E.csproj" -- --port 18111 }
    }

    'e2e' {
        Invoke-Step 'End-to-end test (plain ws)' { dotnet run --project "$root\tools\Rc.E2E\Rc.E2E.csproj" -- --port 18112 }
        Invoke-Step 'End-to-end test (wss + cert pinning)' { dotnet run --project "$root\tools\Rc.E2E\Rc.E2E.csproj" -- --tls --port 18113 }
    }

    'dist' {
        Invoke-Step 'Release build' { dotnet build "$root\RemoteControl.slnx" -c Release -v m }

        Invoke-Step 'Publish agent rcagent.exe (win-x64 self-contained single file)' {
            # Compression matters: this binary may be downloaded over a constrained link, so we trade a
            # slightly slower first launch for a much smaller download (~110 MB -> ~45 MB).
            dotnet publish "$root\src\Rc.Agent\Rc.Agent.csproj" -c Release -r win-x64 --self-contained true `
                -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
                -p:EnableCompressionInSingleFile=true -o "$root\publish\agent"
        }

        Invoke-Step 'Publish controller rccontrol.exe (win-x64 self-contained single file)' {
            dotnet publish "$root\src\Rc.Controller\Rc.Controller.csproj" -c Release -r win-x64 --self-contained true `
                -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$root\publish\controller"
        }

        Invoke-Step 'Publish relay rcrelay (linux-x64 self-contained single file)' {
            dotnet publish "$root\src\Rc.Relay\Rc.Relay.csproj" -c Release -r linux-x64 --self-contained true `
                -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$root\publish\relay-linux"
        }

        Write-Host ""
        Write-Host "Done. Artifacts:" -ForegroundColor Green
        Get-ChildItem -Path "$root\publish" -Recurse -File |
            Where-Object { $_.Extension -eq '.exe' -or $_.Name -eq 'appsettings.json' } |
            Select-Object @{ n = 'Artifact'; e = { $_.FullName.Replace("$root\", '') } },
                          @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } |
            Format-Table -AutoSize
    }
}

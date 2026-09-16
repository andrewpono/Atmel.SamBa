# Generic Release Packer
# Automatically discovers all projects in /src and packages all target frameworks
#
# To enable script, run command:
# Set-ExecutionPolicy -Scope CurrentUser remotesigned
#
# Run script:
# .\pack-release-generic.ps1 -ReleaseVersion "1.0.0"

param(
    [Parameter(Mandatory=$true)]
    [string]$ReleaseVersion,  # The release tag (e.g., "1.0.1" or "1.0.0-beta.1")
    
    [Parameter(Mandatory=$false)]
    [string]$SolutionName = "",  # Auto-detect if not provided
    
    [Parameter(Mandatory=$false)]
    [string[]]$ExcludeProjects = @()  # Projects to skip (e.g., test projects)
)

$ErrorActionPreference = "Stop"

# Helper function to read version from .csproj (evaluating MSBuild properties)
function Get-ProjectVersion {
    param(
        [string]$ProjectFile
    )
    
    try {
        # Parse XML to get properties
        [xml]$projXml = Get-Content $ProjectFile
        
        # Collect all properties from all PropertyGroups
        $props = @{}
        foreach ($propGroup in $projXml.Project.PropertyGroup) {
            foreach ($prop in $propGroup.ChildNodes) {
                if ($prop.Name) {
                    # Store the text content, even if it's empty string
                    $textContent = $prop.'#text'
                    if ($textContent -eq $null) {
                        $textContent = ""
                    }
                    $props[$prop.Name] = $textContent
                }
            }
        }
        
        # Start with Version property
        if ($props.ContainsKey('Version')) {
            $version = $props['Version']
            
            # Resolve MSBuild variables iteratively (handle nested variables)
            $maxIterations = 10
            $iteration = 0
            
            while ($version -match '\$\(([^)]+)\)' -and $iteration -lt $maxIterations) {
                $iteration++
                $resolved = $false
                
                # Find all variables in the string
                $matches = [regex]::Matches($version, '\$\(([^)]+)\)')
                
                foreach ($match in $matches) {
                    $varName = $match.Groups[1].Value
                    
                    if ($props.ContainsKey($varName)) {
                        # Replace the variable with its value (even if empty)
                        $varValue = $props[$varName]
                        $version = $version -replace [regex]::Escape($match.Value), $varValue
                        $resolved = $true
                    } else {
                        # Variable not found in properties - treat as empty
                        Write-Host "    Variable `$$($varName)` not found, treating as empty" -ForegroundColor DarkGray
                        $version = $version -replace [regex]::Escape($match.Value), ""
                        $resolved = $true
                    }
                }
                
                # If nothing was resolved in this iteration, break to avoid infinite loop
                if (-not $resolved) {
                    break
                }
            }
            
            # If still contains unresolved variables, warn and try fallback
            if ($version -match '\$\(') {
                Write-Warning "Could not fully resolve version for $ProjectFile`: $version"
                Write-Warning "  Attempting to read from built assembly..."
                
                # Try to get version from built assembly
                $assemblyVersion = Get-AssemblyVersion -ProjectFile $ProjectFile
                if ($assemblyVersion) {
                    return $assemblyVersion
                }
                
                return "1.0.0"
            }
            
            # Clean up the version string (remove Git metadata if present)
            $version = ($version -split '\+')[0]  # Remove +commit-hash
            
            return $version
        }
        
        # Try VersionPrefix + VersionSuffix
        if ($props.ContainsKey('VersionPrefix')) {
            $version = $props['VersionPrefix']
            if ($props.ContainsKey('VersionSuffix') -and ![string]::IsNullOrEmpty($props['VersionSuffix'])) {
                $version = "$version-$($props['VersionSuffix'])"
            }
            return $version
        }
        
        # Try to read from built assembly
        $assemblyVersion = Get-AssemblyVersion -ProjectFile $ProjectFile
        if ($assemblyVersion) {
            return $assemblyVersion
        }
        
        # Try AssemblyVersion as last resort
        if ($props.ContainsKey('AssemblyVersion')) {
            $version = $props['AssemblyVersion']
            # Resolve variables
            foreach ($key in $props.Keys) {
                if (![string]::IsNullOrEmpty($props[$key])) {
                    $version = $version -replace "\`$\($key\)", $props[$key]
                }
            }
            return $version
        }
        
        # Default
        Write-Warning "Could not determine version for $ProjectFile, using default"
        return "1.0.0"
    }
    catch {
        Write-Warning "Error reading version from $ProjectFile`: $_"
        return "1.0.0"
    }
}

# Helper function to read AssemblyName from .csproj (falls back to project file name)
function Get-AssemblyName {
    param(
        [string]$ProjectFile
    )

    try {
        [xml]$projXml = Get-Content $ProjectFile
        foreach ($propGroup in $projXml.Project.PropertyGroup) {
            if ($propGroup.AssemblyName) {
                return $propGroup.AssemblyName
            }
        }
    }
    catch { }

    # Fallback: use project file name without extension
    return [System.IO.Path]::GetFileNameWithoutExtension($ProjectFile)
}

# Helper function to get version from built assembly
function Get-AssemblyVersion {
    param(
        [string]$ProjectFile
    )
    
    try {
        $assemblyName = Get-AssemblyName -ProjectFile $ProjectFile
        $projectDir = [System.IO.Path]::GetDirectoryName($ProjectFile)

        # Look for built assembly in bin/Release folders
        $binPath = Join-Path $projectDir "bin\Release"
        if (Test-Path $binPath) {
            # Find first target framework folder
            $targetDirs = Get-ChildItem -Path $binPath -Directory | Select-Object -First 1
            if ($targetDirs) {
                $dllPath = Join-Path $targetDirs.FullName "$assemblyName.dll"
                if (Test-Path $dllPath) {
                    # Get file version info (includes InformationalVersion which has SemVer)
                    $fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath)
                    
                    # Try ProductVersion first (this is InformationalVersion, supports SemVer)
                    if (![string]::IsNullOrEmpty($fileInfo.ProductVersion)) {
                        # Remove Git metadata (+commit-hash)
                        $version = ($fileInfo.ProductVersion -split '\+')[0]
                        return $version
                    }
                    
                    # Fallback to FileVersion
                    if (![string]::IsNullOrEmpty($fileInfo.FileVersion)) {
                        return $fileInfo.FileVersion
                    }
                    
                    # Last resort: AssemblyVersion
                    $assembly = [System.Reflection.Assembly]::LoadFile($dllPath)
                    $version = $assembly.GetName().Version
                    return "$($version.Major).$($version.Minor).$($version.Build)"
                }
            }
        }
        
        return $null
    }
    catch {
        return $null
    }
}

# Navigate to solution root
$solutionRoot = Split-Path $PSScriptRoot -Parent
Push-Location $solutionRoot

try {
    # Auto-detect solution name if not provided
    if ([string]::IsNullOrEmpty($SolutionName)) {
        $slnFiles = Get-ChildItem -Path "." -Filter "*.sln" -File
        if ($slnFiles.Count -eq 0) {
            throw "No solution file found in root directory"
        }
        if ($slnFiles.Count -gt 1) {
            throw "Multiple solution files found. Please specify -SolutionName parameter"
        }
        $SolutionName = [System.IO.Path]::GetFileNameWithoutExtension($slnFiles[0].Name)
    }

    $solutionFile = "$SolutionName.sln"
    if (-not (Test-Path $solutionFile)) {
        throw "Solution file not found: $solutionFile"
    }

    Write-Host "Building $SolutionName - Release v$ReleaseVersion" -ForegroundColor Cyan

    # Clean artifacts
    Write-Host "`nCleaning previous builds..." -ForegroundColor Yellow
    $artifactsPath = ".\artifacts"
    Remove-Item -Path $artifactsPath -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -Path $artifactsPath -ItemType Directory | Out-Null

    # Discover all projects in /src
    Write-Host "`nDiscovering projects..." -ForegroundColor Yellow
    $srcPath = ".\src"
    if (-not (Test-Path $srcPath)) {
        throw "Source directory not found: $srcPath"
    }

    $projectDirs = Get-ChildItem -Path $srcPath -Directory | Where-Object {
        $projectName = $_.Name
        $projectFile = Join-Path $_.FullName "$projectName.csproj"
        $shouldInclude = (Test-Path $projectFile) -and ($ExcludeProjects -notcontains $projectName)
        $shouldInclude
    }

    if ($projectDirs.Count -eq 0) {
        throw "No valid projects found in $srcPath"
    }

    Write-Host "Found $($projectDirs.Count) project(s):" -ForegroundColor Green
    foreach ($dir in $projectDirs) {
        Write-Host "  - $($dir.Name)" -ForegroundColor White
    }

    # Categorize projects by output type
    $libraries = @()
    $applications = @()

    foreach ($dir in $projectDirs) {
        $projectName = $dir.Name
        $projectFile = Join-Path $dir.FullName "$projectName.csproj"
        
        # Read project file to determine output type
        [xml]$projXml = Get-Content $projectFile
        $outputType = $projXml.Project.PropertyGroup.OutputType | Select-Object -First 1
        
        if ($outputType -eq "WinExe" -or $outputType -eq "Exe") {
            $applications += $projectName
        } else {
            $libraries += $projectName
        }
    }

    Write-Host "`nLibraries: $($libraries.Count)" -ForegroundColor Cyan
    foreach ($lib in $libraries) {
        Write-Host "  - $lib" -ForegroundColor White
    }
    
    Write-Host "`nApplications: $($applications.Count)" -ForegroundColor Cyan
    foreach ($app in $applications) {
        Write-Host "  - $app" -ForegroundColor White
    }

    # Restore - often breaks costura.fodi
    #Write-Host "`nRestoring packages..." -ForegroundColor Yellow
    #dotnet restore $solutionFile

    # Build entire solution at once (fixes Fody and dependency issues)
    Write-Host "`nBuilding solution..." -ForegroundColor Yellow
    dotnet build $solutionFile -c Release
    
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }

    # Publish projects that have publish profiles
    Write-Host "`nDiscovering publish profiles..." -ForegroundColor Yellow
    $publishedProjects = @{}  # ProjectName -> array of PSCustomObjects { ProfileName, RID, PublishDir, ProjectFile }

    foreach ($dir in $projectDirs) {
        $projectName = $dir.Name
        $profilesPath = Join-Path $dir.FullName "Properties\PublishProfiles"

        if (-not (Test-Path $profilesPath)) {
            continue
        }

        $profiles = Get-ChildItem -Path $profilesPath -Filter "*.pubxml" -File
        if ($profiles.Count -eq 0) {
            continue
        }

        $projectFile = Join-Path $dir.FullName "$projectName.csproj"
        $assemblyName = Get-AssemblyName -ProjectFile $projectFile
        $entries = @()

        Write-Host "  $projectName [$assemblyName]: $($profiles.Count) profile(s)" -ForegroundColor Gray

        foreach ($profile in $profiles) {
            $profileName = [System.IO.Path]::GetFileNameWithoutExtension($profile.Name)

            # Read profile to extract TFM, RID and publish dir
            [xml]$profileXml = Get-Content $profile.FullName
            $tfm = $profileXml.Project.PropertyGroup.TargetFramework
            $rid = $profileXml.Project.PropertyGroup.RuntimeIdentifier
            $publishDir = $profileXml.Project.PropertyGroup.PublishDir

            Write-Host "    Publishing profile '$profileName' ($tfm / $rid)..." -ForegroundColor DarkGray
            dotnet publish $projectFile -c Release -f $tfm -p:PublishProfile="$($profile.FullName)"

            if ($LASTEXITCODE -ne 0) {
                throw "Publish failed for $projectName profile '$profileName' with exit code $LASTEXITCODE"
            }

            # Resolve publish output path (relative to project dir)
            $resolvedPublishDir = Join-Path $dir.FullName $publishDir

            $entries += [PSCustomObject]@{
                ProfileName = $profileName
                RID         = $rid
                PublishDir  = $resolvedPublishDir
                ProjectFile = $projectFile
            }
        }

        $publishedProjects[$projectName] = $entries
    }

    if ($publishedProjects.Count -eq 0) {
        Write-Host "  No publish profiles found" -ForegroundColor DarkGray
    }

    # Read versions AFTER build (so we can inspect assemblies if needed)
    Write-Host "`nReading project versions..." -ForegroundColor Yellow
    $projectVersions = @{}
    
    foreach ($dir in $projectDirs) {
        $projectName = $dir.Name
        $projectFile = Join-Path $dir.FullName "$projectName.csproj"
        
        # Read version from project file
        $version = Get-ProjectVersion -ProjectFile $projectFile
        $projectVersions[$projectName] = $version
        
        Write-Host "  $projectName -> v$version" -ForegroundColor Gray
    }

    # Pack libraries (NuGet)
    if ($libraries.Count -gt 0) {
        Write-Host "`nPacking NuGet packages..." -ForegroundColor Yellow
        foreach ($lib in $libraries) {
            $projectFile = ".\src\$lib\$lib.csproj"
            $version = $projectVersions[$lib]
            Write-Host "  Packing $lib (v$version)..." -ForegroundColor Gray
            # Version comes from .csproj evaluation, build artifacts already exist
            dotnet pack $projectFile -c Release -o .\artifacts\nuget --no-build

            if ($LASTEXITCODE -ne 0) {
                throw "Pack failed for $lib with exit code $LASTEXITCODE"
            }
        }
    }

    # Discover all target frameworks across all projects
    Write-Host "`nDiscovering target frameworks..." -ForegroundColor Yellow
    $allTargets = @{}  # ProjectName -> array of PSCustomObjects

    foreach ($dir in $projectDirs) {
        $projectName = $dir.Name
        $binPath = Join-Path $dir.FullName "bin\Release"
        
        if (Test-Path $binPath) {
            $targetDirs = Get-ChildItem -Path $binPath -Directory
            $targets = @()
            
            foreach ($targetDir in $targetDirs) {
                # Normalize target framework name (remove platform suffixes for grouping)
                $targetName = $targetDir.Name
                $normalizedTarget = $targetName -replace '-windows.*$', ''
                
                $targetObj = [PSCustomObject]@{
                    Original = $targetName
                    Normalized = $normalizedTarget
                    Path = $targetDir.FullName
                }
                
                $targets += $targetObj
            }
            
            $allTargets[$projectName] = $targets
            
            Write-Host "  $projectName targets:" -ForegroundColor Gray
            foreach ($target in $targets) {
                Write-Host "    - $($target.Original) -> $($target.Normalized)" -ForegroundColor DarkGray
            }
        }
    }

    # Get unique normalized target frameworks
    $uniqueTargets = @()
    foreach ($projectTargets in $allTargets.Values) {
        foreach ($target in $projectTargets) {
            if ($uniqueTargets -notcontains $target.Normalized) {
                $uniqueTargets += $target.Normalized
            }
        }
    }
    $uniqueTargets = $uniqueTargets | Sort-Object

    Write-Host "`nUnique target frameworks: $($uniqueTargets.Count)" -ForegroundColor Green
    foreach ($target in $uniqueTargets) {
        Write-Host "  - $target" -ForegroundColor White
    }

    # Create binaries package (libraries only)
    if ($libraries.Count -gt 0) {
        Write-Host "`nCreating binaries package..." -ForegroundColor Yellow
        $binariesPath = ".\artifacts\binaries\$SolutionName-v$ReleaseVersion-Binaries"
        
        # Create directory structure for each target framework
        foreach ($target in $uniqueTargets) {
            New-Item -Path "$binariesPath\$target" -ItemType Directory -Force | Out-Null
        }
        
        # Copy library binaries for each target
        foreach ($lib in $libraries) {
            $projectFile = ".\src\$lib\$lib.csproj"
            $assemblyName = Get-AssemblyName -ProjectFile $projectFile
            $targets = $allTargets[$lib]

            foreach ($targetInfo in $targets) {
                $sourcePattern = Join-Path $targetInfo.Path "$assemblyName.*"
                $destPath = "$binariesPath\$($targetInfo.Normalized)"

                Write-Host "  Copying $lib [$assemblyName] ($($targetInfo.Original)) -> $($targetInfo.Normalized)" -ForegroundColor Gray
                Copy-Item $sourcePattern $destPath -ErrorAction SilentlyContinue
            }
        }
        
        # Create README
        $targetsList = ($uniqueTargets | ForEach-Object { "- $_" }) -join "`n"
        $librariesList = ($libraries | ForEach-Object { 
            $version = $projectVersions[$_]
            "- $_ (v$version)"
        }) -join "`n"
        
        @"
$SolutionName v$ReleaseVersion - Binary Distribution

Release Version: $ReleaseVersion
Release Date: $(Get-Date -Format 'yyyy-MM-dd')

This package contains compiled libraries for target frameworks.

Target Frameworks:
$targetsList

Libraries:
$librariesList

Contents:
- .dll files: Compiled assemblies
- .xml files: XML documentation for IntelliSense
"@ | Out-File "$binariesPath\README.txt" -Encoding UTF8

        Compress-Archive -Path "$binariesPath\*" -DestinationPath ".\artifacts\$SolutionName-v$ReleaseVersion-Binaries.zip" -Force
    }

    # Create application packages (one per application)
    if ($applications.Count -gt 0) {
        Write-Host "`nCreating application packages..." -ForegroundColor Yellow
        
        foreach ($app in $applications) {
            $appVersion = $projectVersions[$app]
            $targets = $allTargets[$app]
            
            # For each target framework of the application
            foreach ($targetInfo in $targets) {
                $appPath = ".\artifacts\application\$app-$($targetInfo.Normalized)"
                New-Item -Path $appPath -ItemType Directory -Force | Out-Null
                
                Write-Host "  Packaging $app v$appVersion ($($targetInfo.Original))..." -ForegroundColor Gray
                Copy-Item "$($targetInfo.Path)\*" $appPath -Recurse -Exclude @("*.pdb", "*.xml", "*.config")
                
                # Create README
                @"
$app v$appVersion

Release: $SolutionName v$ReleaseVersion
Target Framework: $($targetInfo.Normalized)
"@ | Out-File "$appPath\README.txt" -Encoding UTF8
                
                $zipName = "$app-v$ReleaseVersion-$($targetInfo.Normalized).zip"
                Compress-Archive -Path "$appPath\*" -DestinationPath ".\artifacts\$zipName" -Force
            }
        }
    }

    # Create published application packages (platform-specific single-file etc.)
    if ($publishedProjects.Count -gt 0) {
        Write-Host "`nCreating published application packages..." -ForegroundColor Yellow

        foreach ($entry in $publishedProjects.GetEnumerator()) {
            $projectName = $entry.Key
            $appVersion = $projectVersions[$projectName]
            $assemblyName = Get-AssemblyName -ProjectFile $entry.Value[0].ProjectFile

            foreach ($pub in $entry.Value) {
                $rid = $pub.RID
                $publishDir = $pub.PublishDir

                if (-not (Test-Path $publishDir)) {
                    Write-Warning "  Publish output not found: $publishDir"
                    continue
                }

                $pubPath = ".\artifacts\publish\$assemblyName-$rid"
                New-Item -Path $pubPath -ItemType Directory -Force | Out-Null

                Write-Host "  Packaging $projectName [$assemblyName] v$appVersion ($rid)..." -ForegroundColor Gray
                Copy-Item "$publishDir\*" $pubPath -Recurse -Exclude @("*.pdb", "*.xml", "*.config")

                $zipName = "$assemblyName-v$ReleaseVersion-$rid.zip"
                Compress-Archive -Path "$pubPath\*" -DestinationPath ".\artifacts\$zipName" -Force
            }
        }
    }

    # Generate version manifest
    Write-Host "`nGenerating version manifest..." -ForegroundColor Yellow
    $manifest = [ordered]@{
        ReleaseVersion = $ReleaseVersion
        ReleaseDate = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
        SolutionName = $SolutionName
        Components = [ordered]@{}
    }

    foreach ($proj in ($projectVersions.GetEnumerator() | Sort-Object Name)) {
        $type = if ($libraries -contains $proj.Name) { "Library" } else { "Application" }
        $manifest.Components[$proj.Name] = [ordered]@{
            Version = $proj.Value
            Type = $type
        }
    }

    # Save manifest as JSON
    $manifest | ConvertTo-Json | Out-File ".\artifacts\VERSION-MANIFEST.json" -Encoding UTF8
    
    Write-Host "`nVersion manifest saved to artifacts\VERSION-MANIFEST.json" -ForegroundColor Green

    # Summary
    Write-Host "`n--- Build complete! Artifacts in .\artifacts\ ---" -ForegroundColor Green
    Write-Host "`n=================================================" -ForegroundColor Cyan
    Write-Host "Release: $SolutionName v$ReleaseVersion" -ForegroundColor Cyan
    Write-Host "=================================================" -ForegroundColor Cyan
    
    Write-Host "`nComponent Versions:" -ForegroundColor Yellow
    foreach ($proj in ($projectVersions.GetEnumerator() | Sort-Object Name)) {
        $type = if ($libraries -contains $proj.Name) { "[LIB]" } else { "[APP]" }
        Write-Host "  $type $($proj.Name): v$($proj.Value)" -ForegroundColor White
    }
    
    if ($libraries.Count -gt 0) {
        Write-Host "`nBinaries Package:" -ForegroundColor Yellow
        Write-Host "  - $SolutionName-v$ReleaseVersion-Binaries.zip" -ForegroundColor White
        
        Write-Host "`nNuGet Packages:" -ForegroundColor Yellow
        foreach ($lib in $libraries) {
            $version = $projectVersions[$lib]
            Write-Host "  - $lib.$version.nupkg" -ForegroundColor White
        }
    }
    
    if ($applications.Count -gt 0) {
        Write-Host "`nApplication Packages:" -ForegroundColor Yellow
        Get-ChildItem ".\artifacts\*.zip" -Filter "*$SolutionName*" | Where-Object {
            $_.Name -notlike "*Binaries.zip"
        } | ForEach-Object {
            Write-Host "  - $($_.Name)" -ForegroundColor White
        }
    }
    
    Write-Host "`n=================================================" -ForegroundColor Cyan
	
	
	
	# Create release-ready folder
	Write-Host "`nPreparing GitHub release assets..." -ForegroundColor Yellow
	$releasePath = ".\artifacts\release"
	New-Item -Path $releasePath -ItemType Directory -Force | Out-Null

	# Copy main packages
	Copy-Item ".\artifacts\*-Binaries.zip" $releasePath -ErrorAction SilentlyContinue
	Copy-Item ".\artifacts\*-v$ReleaseVersion-*.zip" $releasePath -ErrorAction SilentlyContinue
	Copy-Item ".\artifacts\VERSION-MANIFEST.json" $releasePath -ErrorAction SilentlyContinue

	# Copy NuGet packages
	if (Test-Path ".\artifacts\nuget") {
		Copy-Item ".\artifacts\nuget\*.nupkg" $releasePath -ErrorAction SilentlyContinue
		Copy-Item ".\artifacts\nuget\*.snupkg" $releasePath -ErrorAction SilentlyContinue
	}

	# List release assets
	Write-Host "`nGitHub Release Assets (artifacts\release\):" -ForegroundColor Cyan
	Get-ChildItem $releasePath | ForEach-Object {
		$size = "{0:N2} MB" -f ($_.Length / 1MB)
		Write-Host "  - $($_.Name) ($size)" -ForegroundColor White
	}

Write-Host "`nUpload these files to GitHub Release v$ReleaseVersion" -ForegroundColor Green
}
catch {
    Write-Host "`n--- Build failed: $_ ---" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}